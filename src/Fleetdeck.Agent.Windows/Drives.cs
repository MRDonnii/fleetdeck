using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using Fleetdeck.Shared;

namespace Fleetdeck.Agent.Windows;

/// <summary>
/// Netværksdrev for den indloggede bruger: forbinder \\Host\Share som drevbogstav i brugerens session (ikke SYSTEM's),
/// gemmer Unraid-login i brugerens legitimationsoplysninger (cmdkey) og fjerner kun drev, Fleetdeck selv har lavet.
/// En vagthund tjekker drevene hvert minut og kobler dem på igen, hvis de er faldet af. Drev, brugeren selv har koblet på,
/// meldes til serveren (så de kan holdes forbundet). Kører i sin egen løkke, så check-in aldrig venter på det.
/// </summary>
public sealed class DriveMapper(Runner runner, AgentOptions opt, ILogger<DriveMapper> log)
{
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(10);          // fuld opdatering (cmdkey, bogstaver, oprydning)
    private static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(60);     // vagthund
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan NetSettle = TimeSpan.FromSeconds(10);      // vent lidt efter netværksskift/dvale

    private sealed record Input(List<DriveSpec> Want, ShareInfo? Share, AgentConfig Cfg,
        Func<CancellationToken, Task> SendHealth, Func<FoundDrivesRequest, CancellationToken, Task> SendFound);

    private readonly SemaphoreSlim _kick = new(0, 1);
    private int _started;
    private volatile Input? _in;
    private volatile string? _hash;
    private string? _user, _foundHash;
    private DateTime _at = DateTime.MinValue, _checkAt = DateTime.MinValue, _loopAt = DateTime.MinValue, _foundRetry = DateTime.MinValue;
    private long _netAt;                       // ticks for seneste netværksskift (0 = intet)
    private bool _needFull;
    private readonly Dictionary<string, string> _drop = [];   // nøgle → klokkeslæt, drevet faldt af (kun vagthundens tråd)
    private volatile List<DriveState> _states = [];

    /// <summary>Seneste status for de ønskede drev (til helbredet).</summary>
    public List<DriveState> States => [.. _states];

    /// <summary>
    /// Kaldes ved hvert check-in med den ønskede liste. Starter vagthunden første gang; ellers vækkes den kun, når listen er ændret.
    /// </summary>
    public void Tick(List<DriveSpec>? want, ShareInfo? share, AgentConfig cfg, Func<CancellationToken, Task> sendHealth,
        Func<FoundDrivesRequest, CancellationToken, Task> sendFound, CancellationToken stop)
    {
        var i = new Input(want ?? [], share, cfg, sendHealth, sendFound);
        _in = i;
        if (Interlocked.Exchange(ref _started, 1) == 0)
        {
            try { NetworkChange.NetworkAddressChanged += (_, _) => Volatile.Write(ref _netAt, DateTime.UtcNow.Ticks); }
            catch (Exception e) { log.LogDebug("Netværksskift kan ikke følges: {Msg}", e.Message); }
            using (ExecutionContext.SuppressFlow())   // aldrig en del af et kørende job (fremdrift/annullering)
                _ = Task.Run(() => Watch(stop), CancellationToken.None);
        }
        else if (Hash(i.Want, i.Share) != _hash) Wake();
    }

    private void Wake() { try { _kick.Release(); } catch (SemaphoreFullException) { } }

    /// <summary>Vagthundens løkke: vågner hvert 10. sekund, men kører kun PowerShell, når der er noget at gøre.</summary>
    private async Task Watch(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try { if (_in is { } i) await Step(i, stop); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception e) { log.LogWarning("Netværksdrev: {Msg}", e.Message); }
            try { await _kick.WaitAsync(TimeSpan.FromSeconds(10), stop); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task Step(Input i, CancellationToken stop)
    {
        var now = DateTime.UtcNow;
        if (_loopAt != DateTime.MinValue && now - _loopAt > TimeSpan.FromMinutes(2))
            Volatile.Write(ref _netAt, now.Ticks);   // vågnet fra dvale: tjek, så snart netværket har haft et øjeblik
        _loopAt = now;
        var user = Native.ConsoleUser();
        var hash = Hash(i.Want, i.Share);
        var net = Volatile.Read(ref _netAt);
        var before = Signature(_states);

        if (hash != _hash || user != _user || now - _at >= Every || (_needFull && now - _at >= TimeSpan.FromMinutes(3)))
        {
            var userChanged = user != _user;
            if (userChanged) _drop.Clear();
            _hash = hash; _user = user; _at = now; _checkAt = now; _needFull = false;
            try { await Full(i, user, userChanged, stop); }
            catch (Exception e) when (e is not OperationCanceledException || !stop.IsCancellationRequested)
            {
                log.LogWarning("Netværksdrev: {Msg}", e.Message);
                _at = now - Every + TimeSpan.FromMinutes(2);   // prøv igen om et par minutter
            }
        }
        else if (user is not null && i.Want.Count > 0 && (now - _checkAt >= CheckEvery || (net != 0 && now.Ticks - net >= NetSettle.Ticks)))
        {
            _checkAt = now;
            Volatile.Write(ref _netAt, 0);
            await Check(i, user, stop);
        }
        else return;

        if (Signature(_states) != before)
            try { await i.SendHealth(stop); }
            catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException) { log.LogDebug("Helbred efter netværksdrev: {Msg}", e.Message); }
    }

    // ---------- fuld opdatering ----------

    private async Task Full(Input i, string? user, bool userChanged, CancellationToken stop)
    {
        if (user is null)
        {
            if (userChanged && i.Want.Count > 0) log.LogDebug("Netværksdrev venter: ingen bruger er logget ind");
            _states = [.. i.Want.Select(d => State(d, d.Letter, false, "ingen bruger er logget ind"))];
            return;
        }
        await Apply(i, user, i.Cfg.MappedDrives?.GetValueOrDefault(user) ?? [], stop);
    }

    private async Task Apply(Input i, string user, Dictionary<string, string> owned, CancellationToken stop)
    {
        var want = i.Want;
        var bad = want.Where(d => !Valid(d)).ToList();
        var good = want.Except(bad).ToList();
        var r = await runner.PowerShell(Script(good, i.Share, owned), RunAs.User, Timeout, stop);
        var states = bad.Select(d => State(d, d.Letter, false, "ugyldigt drev (server, mappe eller bogstav mangler)")).ToList();
        if (r.NoUser)
        {
            _states = [.. states, .. good.Select(d => State(d, d.Letter, false, "ingen bruger er logget ind"))];
            return;
        }
        if (r.TimedOut || r.Cancelled)
        {
            log.LogWarning("Netværksdrev for {User}: tidsgrænsen på {S} sekunder blev overskredet", user, (int)Timeout.TotalSeconds);
            _states = [.. states, .. good.Select(d => State(d, d.Letter, false, "tidsgrænsen blev overskredet (svarer serveren?)"))];
            return;
        }

        var prev = Prev();
        var seen = new Dictionary<string, DriveState>();
        var own = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var created = new List<string>();
        var found = new List<FoundDrive>();
        var done = false;
        foreach (var line in r.Output.Split('\n'))
        {
            var p = line.Trim().Split('|');
            if (p[0] == "DONE") done = true;
            else if (p[0] == "OWN" && p.Length >= 3 && IsLetter(p[1])) own[p[1].ToUpperInvariant()] = p[2];
            else if (p[0] == "FOUND" && Found(p) is { } f) found.Add(f);
            else if (p[0] == "REMOVED" && p.Length >= 2) log.LogInformation("Netværksdrev {L}: fjernet (ønskes ikke længere)", p[1]);
            else if (p[0] == "REMOVEFAIL" && p.Length >= 3) log.LogWarning("Netværksdrev {L}: kunne ikke fjernes ({Why})", p[1], Reason(p[2]));
            else if (p[0] == "DRIVE" && p.Length >= 4 && good.FirstOrDefault(d => d.Key == p[1]) is { } d)
            {
                var ok = p[3] == "ok";
                seen[d.Key] = Settle(State(d, p[2], ok, ok ? null : Reason(p.Length >= 5 ? string.Join('|', p[4..]) : "")), prev, false);
                if (ok && p.Length >= 5 && p[4] == "ny") created.Add($"{p[2]}: \\\\{d.Host}\\{d.Share}");
            }
        }
        states.AddRange(good.Select(d => seen.GetValueOrDefault(d.Key) ?? State(d, d.Letter, false, r.ExitCode == 0 ? "intet svar fra scriptet" : $"scriptet fejlede: {Short(r.Output)}")));
        _states = states;
        _needFull = good.Any(d => !seen.TryGetValue(d.Key, out var s) || s.Target.Length == 0);

        foreach (var c in created) log.LogInformation("Netværksdrev forbundet for {User}: {Drive}", user, c);
        foreach (var s in states.Where(s => !s.Ok)) log.LogWarning("Netværksdrev {Name} ({Target}) for {User}: {Error}", s.Name, s.Target, user, s.Error);

        // Husk de bogstaver, Fleetdeck selv har lavet (pr. bruger): kun de må fjernes senere.
        if (done)
        {
            var same = own.Count == owned.Count && own.All(kv => owned.TryGetValue(kv.Key, out var v) && string.Equals(v, kv.Value, StringComparison.OrdinalIgnoreCase));
            if (!same) SaveOwned(i.Cfg, user, own);
            await Report(i, user, found, stop);
        }
    }

    // ---------- vagthund ----------

    /// <summary>Let tjek hvert minut: er bogstavet der, peger det rigtigt, og svarer det? Ellers kobles det på igen med det samme.</summary>
    private async Task Check(Input i, string user, CancellationToken stop)
    {
        var prev = Prev();
        var watch = new List<(DriveSpec D, string L)>();
        foreach (var d in i.Want.Where(Valid))
        {
            var last = prev.GetValueOrDefault(d.Key);
            // forkert login: prøv ikke hvert minut (kan låse kontoen) – først ved næste fulde opdatering
            if (last is { Ok: false, Error: { } e } && e.Contains("brugernavn eller adgangskode", StringComparison.Ordinal)) continue;
            var l = Letter(d.Letter);
            if (l.Length == 0 && last?.Target is { Length: > 0 } t) l = t[..1];
            if (l.Length == 0) { _needFull = true; continue; }
            watch.Add((d, l));
        }
        if (watch.Count == 0) return;

        var owned = i.Cfg.MappedDrives?.GetValueOrDefault(user) ?? [];
        var r = await runner.PowerShell(CheckScript(watch, owned), RunAs.User, CheckTimeout, stop);
        if (r.NoUser) return;   // brugeren er lige logget ud: den fulde opdatering tager sig af det
        if (r.TimedOut || r.Cancelled) { log.LogDebug("Tjek af netværksdrev for {User}: tidsgrænsen blev overskredet", user); return; }

        var next = new Dictionary<string, DriveState>();
        var added = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var found = new List<FoundDrive>();
        var done = false;
        foreach (var line in r.Output.Split('\n'))
        {
            var p = line.Trim().Split('|');
            if (p[0] == "DONE") done = true;
            else if (p[0] == "NEW" && p.Length >= 3 && IsLetter(p[1])) added[p[1].ToUpperInvariant()] = p[2];
            else if (p[0] == "FOUND" && Found(p) is { } f) found.Add(f);
            else if (p[0] == "DRIVE" && p.Length >= 4 && watch.FirstOrDefault(x => x.D.Key == p[1]) is { D: not null } w)
            {
                var ok = p[3] == "ok";
                var s = Settle(State(w.D, p[2], ok, ok ? null : Reason(p.Length >= 5 ? string.Join('|', p[4..]) : "")), prev, true);
                if (ok && p.Length >= 5 && p[4] == "igen") log.LogInformation("Netværksdrev {Name} ({Target}) for {User} var faldet af – koblet på igen", s.Name, s.Target, user);
                next[w.D.Key] = s;
            }
        }
        if (next.Count == 0 && !done) { log.LogDebug("Tjek af netværksdrev: intet svar ({Out})", Short(r.Output)); return; }
        _states = [.. i.Want.Select(d => next.GetValueOrDefault(d.Key) ?? prev.GetValueOrDefault(d.Key) ?? State(d, d.Letter, false, "ikke tjekket endnu"))];

        if (added.Count > 0)
        {
            var own = new Dictionary<string, string>(owned, StringComparer.OrdinalIgnoreCase);
            foreach (var (l, u) in added) own[l] = u;
            SaveOwned(i.Cfg, user, own);
        }
        if (done) await Report(i, user, found, stop);
    }

    /// <summary>Fejl efter at drevet virkede: "faldt af kl. HH:mm – årsag" (tiden står fast, så helbredet ikke sendes hvert minut).</summary>
    private DriveState Settle(DriveState s, Dictionary<string, DriveState> prev, bool logDrop)
    {
        if (s.Ok) { _drop.Remove(s.Key); return s; }
        if (!_drop.ContainsKey(s.Key) && prev.GetValueOrDefault(s.Key) is { Ok: true })
        {
            _drop[s.Key] = DateTime.Now.ToString("HH:mm");
            if (logDrop) log.LogWarning("Netværksdrev {Name} ({Target}) faldt af og kunne ikke kobles på igen: {Error}", s.Name, s.Target, s.Error);
        }
        if (_drop.TryGetValue(s.Key, out var at)) s.Error = $"faldt af kl. {at} – {s.Error}";
        return s;
    }

    // ---------- fundne drev ----------

    /// <summary>Send brugerens egne drev til serveren, når listen er ændret (aldrig en tom liste).</summary>
    private async Task Report(Input i, string user, List<FoundDrive> found, CancellationToken stop)
    {
        var key = user + "\n" + string.Join('\n', found.OrderBy(f => f.Letter).Select(f => $"{f.Letter}|{f.Host}|{f.Share}|{f.Name}".ToLowerInvariant()));
        if (key == _foundHash || DateTime.UtcNow < _foundRetry) return;
        if (found.Count == 0) { _foundHash = key; return; }
        try
        {
            await i.SendFound(new FoundDrivesRequest { Drives = found }, stop);
            _foundHash = key;
            log.LogInformation("Netværksdrev koblet på af {User} selv, sendt til serveren: {Drives}", user, string.Join(", ", found.Select(f => $"{f.Target} \\\\{f.Host}\\{f.Share}")));
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException)
        {
            _foundRetry = DateTime.UtcNow.AddMinutes(10);
            log.LogDebug("Fundne netværksdrev kunne ikke sendes: {Msg}", e.Message);
        }
    }

    /// <summary>FOUND|L|\\server\share|etiket → FoundDrive.</summary>
    private static FoundDrive? Found(string[] p)
    {
        if (p.Length < 3 || !IsLetter(p[1])) return null;
        var parts = p[2].Trim().TrimStart('\\').TrimEnd('\\').Split('\\', 2);
        if (parts.Length < 2 || parts[0].Length == 0 || parts[1].Length == 0) return null;
        var label = p.Length >= 4 ? string.Join('|', p[3..]).Trim() : "";
        var l = p[1].ToUpperInvariant();
        return new FoundDrive { Host = parts[0], Share = parts[1], Letter = l, Target = l + ":", Name = label.Length > 0 ? label : parts[1].Split('\\')[^1] };
    }

    private void SaveOwned(AgentConfig cfg, string user, Dictionary<string, string> own)
    {
        var all = new Dictionary<string, Dictionary<string, string>>(cfg.MappedDrives ?? [], StringComparer.OrdinalIgnoreCase);
        if (own.Count == 0) all.Remove(user); else all[user] = own;
        cfg.MappedDrives = all.Count == 0 ? null : all;   // ny ordbog: må ikke ændres, mens en anden tråd gemmer
        try { cfg.Save(opt.ConfigFile); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log.LogWarning("Kunne ikke gemme netværksdrev i konfigurationen: {Msg}", e.Message); }
    }

    // ---------- PowerShell ----------

    /// <summary>Fælles for begge scripts: hjælpefunktioner og opslag af, hvad et bogstav peger på.</summary>
    private const string Common = """
        $ErrorActionPreference = 'Continue'
        $net = New-Object -ComObject WScript.Network
        function Norm($p) { if (-not $p) { return '' }; return ([string]$p).TrimEnd('\').ToLowerInvariant() }
        function Code($err) { $e = $err.Exception; while ($e.InnerException) { $e = $e.InnerException }; return ($e.HResult -band 0xFFFF) }
        function Why($c) { $m = (New-Object ComponentModel.Win32Exception ([int]$c)).Message -replace '[|\r\n]', ' '; return "kode:$c|$m" }
        $sys = @{}; foreach ($d in [IO.DriveInfo]::GetDrives()) { if ([string]$d.DriveType -ne 'Network') { $sys[$d.Name.Substring(0,1).ToUpper()] = $true } }
        $gone = @{}
        function Cur($l) {
          if (-not $gone[$l]) {
            $d = Get-PSDrive -Name $l -PSProvider FileSystem -ErrorAction SilentlyContinue
            if ($d) { if ($d.DisplayRoot) { return [string]$d.DisplayRoot }; return '#lokal' }
          }
          $k = Get-ItemProperty -Path ('HKCU:\Network\' + $l) -ErrorAction SilentlyContinue
          if ($k -and $k.RemotePath) { return [string]$k.RemotePath }
          if ($sys.ContainsKey($l)) { return '#lokal' }
          return ''
        }

        """;

    /// <summary>Brugerens egne netværksdrev: ikke lavet af Fleetdeck ($mine) og ikke ønsket i forvejen ($want). Svar: FOUND|L|unc|etiket.</summary>
    private const string Discover = """
        $skip = @{}; foreach ($w in $want) { $skip[(Norm $w.U)] = $true }
        $map = @{}
        foreach ($k in @(Get-ChildItem -Path 'HKCU:\Network' -ErrorAction SilentlyContinue)) {
          $p = (Get-ItemProperty -Path $k.PSPath -ErrorAction SilentlyContinue).RemotePath
          if ($p) { $map[([string]$k.PSChildName).ToUpper()] = [string]$p }
        }
        foreach ($d in @(Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue)) { if ($d.DisplayRoot -like '\\*' -and -not $gone[$d.Name]) { $map[([string]$d.Name).ToUpper()] = [string]$d.DisplayRoot } }
        foreach ($l in @($map.Keys)) {
          $u = $map[$l].TrimEnd('\')
          if ($l.Length -ne 1 -or $gone[$l] -or $skip[(Norm $u)]) { continue }
          if ($mine.ContainsKey($l) -and (Norm $mine[$l]) -eq (Norm $u)) { continue }
          $p = $u.TrimStart('\').Split('\', 2)
          if ($p.Count -lt 2 -or -not $p[0] -or -not $p[1] -or $p[0] -like '*@*') { continue }   # kun SMB (ikke WebDAV)
          $lab = ''
          try { $lab = [string](Get-ItemProperty -Path ('HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\MountPoints2\' + ($u -replace '\\', '#')) -ErrorAction SilentlyContinue)._LabelFromReg } catch { }
          Write-Output "FOUND|$l|$u|$($lab -replace '[|\r\n]', ' ')"
        }

        """;

    /// <summary>$want-listen til PowerShell. L = bogstav (tomt = vælg selv), O = brugerens eget login.</summary>
    private static void AppendWant(StringBuilder sb, IEnumerable<(DriveSpec D, string L)> want)
    {
        var q = Runner.Quote;
        sb.Append("$want = @(\r\n");
        foreach (var (d, l) in want)
            sb.Append("  @{K=").Append(q(d.Key)).Append(";L=").Append(q(l))
              .Append(";U=").Append(q($"\\\\{d.Host.Trim().Trim('\\', '/')}\\{d.Share.Trim().Trim('\\', '/')}"))
              .Append(";H=").Append(q(d.Host.Trim().Trim('\\', '/'))).Append(";N=").Append(q(d.Name.Trim()))
              .Append(";O=").Append(d.OwnLogin ? "$true" : "$false").Append("}\r\n");
        sb.Append(")\r\n$owned = @{");
    }

    private static void AppendOwned(StringBuilder sb, Dictionary<string, string> owned)
    {
        var q = Runner.Quote;
        foreach (var (l, u) in owned) sb.Append(q(l)).Append('=').Append(q(u)).Append(';');
        sb.Append("}\r\n");
    }

    /// <summary>
    /// Fuld opdatering (PowerShell 5.1) i brugerens session.
    /// Svar: DRIVE|key|L|ok[|ny], DRIVE|key|L|fejl|årsag, OWN|L|unc, REMOVED|L, FOUND|L|unc|etiket.
    /// </summary>
    private static string Script(List<DriveSpec> want, ShareInfo? share, Dictionary<string, string> owned)
    {
        var q = Runner.Quote;
        var sb = new StringBuilder();
        sb.Append("$user = ").Append(q(share?.User ?? "")).Append("; $pass = ").Append(q(share?.Password ?? "")).Append("\r\n");
        AppendWant(sb, want.Select(d => (d, Letter(d.Letter))));
        AppendOwned(sb, owned);
        sb.Append(Common);
        sb.Append("""
            # login gemmes i brugerens legitimationsoplysninger, saa drevene virker efter genstart.
            # Ikke for servere, hvor brugeren selv har koblet et drev paa: der bruges det login, Windows allerede har.
            $mineHosts = @{}; foreach ($w in $want) { if ($w.O) { $mineHosts[$w.H.ToLower()] = $true } }
            if ($user) { foreach ($h in ($want | Where-Object { -not $_.O -and -not $mineHosts[$_.H.ToLower()] } | ForEach-Object { $_.H } | Sort-Object -Unique)) { & cmdkey.exe "/add:$h" "/user:$user" "/pass:$pass" | Out-Null } }
            # bogstaver: faste foerst, saa tidligere brugte, saa ledige fra Z og nedad
            $taken = @{}
            foreach ($w in $want) { if ($w.L) { $taken[$w.L] = $true } }
            foreach ($w in $want) {
              if ($w.L) { continue }
              foreach ($l in @($owned.Keys)) { if (-not $taken[$l] -and (Norm $owned[$l]) -eq (Norm $w.U)) { $w.L = $l; break } }
              if (-not $w.L) { foreach ($c in [char[]]'ZYXWVUTSRQPONMLKJIHGFED') { $l = [string]$c; if (-not $taken[$l] -and (Norm (Cur $l)) -eq (Norm $w.U)) { $w.L = $l; break } } }
              if (-not $w.L) { foreach ($c in [char[]]'ZYXWVUTSRQPONMLKJIHGFED') { $l = [string]$c; if (-not $taken[$l] -and -not $owned.ContainsKey($l) -and -not (Cur $l)) { $w.L = $l; break } } }
              if ($w.L) { $taken[$w.L] = $true }
            }
            # fjern drev, Fleetdeck har lavet, men som ikke laengere oenskes (eller skal pege et andet sted hen).
            # Brugerens egne drev ($owned har dem ikke) roeres aldrig.
            $own = @{}
            foreach ($l in @($owned.Keys)) {
              $keep = $want | Where-Object { $_.L -eq $l -and (Norm $_.U) -eq (Norm $owned[$l]) }
              if ($keep) { $own[$l] = $owned[$l]; continue }
              $cur = Cur $l
              if ((Norm $cur) -ne (Norm $owned[$l])) { continue }   # brugeren har selv aendret det: lad det vaere
              try { $net.RemoveNetworkDrive("$($l):", $true, $true); $gone[$l] = $true; Write-Output "REMOVED|$l" }
              catch {
                $c = Code $_
                if ($c -eq 2250) { Remove-Item -Path ('HKCU:\Network\' + $l) -Recurse -Force -ErrorAction SilentlyContinue; $gone[$l] = $true; Write-Output "REMOVED|$l" }
                else { $own[$l] = $owned[$l]; Write-Output "REMOVEFAIL|$l|$(Why $c)" }
              }
            }
            foreach ($w in $want) {
              $l = $w.L
              if (-not $l) { Write-Output "DRIVE|$($w.K)||fejl|ingen-ledige"; continue }
              $cur = Cur $l
              $new = $false
              if ($cur -and (Norm $cur) -ne (Norm $w.U)) { Write-Output "DRIVE|$($w.K)|$l|fejl|optaget|$cur"; continue }
              if (-not $cur) {
                try { $net.MapNetworkDrive("$($l):", $w.U, $true); $new = $true; if (-not $w.O) { $own[$l] = $w.U } }
                catch { Write-Output "DRIVE|$($w.K)|$l|fejl|$(Why (Code $_))"; continue }
              }
              if ($w.N -and -not $w.O) {
                $mp = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\MountPoints2\' + ($w.U -replace '\\', '#')
                try { if (-not (Test-Path $mp)) { New-Item -Path $mp -Force | Out-Null }; Set-ItemProperty -Path $mp -Name '_LabelFromReg' -Value $w.N } catch { }
              }
              if ($new) { Write-Output "DRIVE|$($w.K)|$l|ok|ny" } else { Write-Output "DRIVE|$($w.K)|$l|ok" }
            }
            foreach ($l in $own.Keys) { Write-Output "OWN|$l|$($own[$l])" }
            $mine = $own

            """);
        sb.Append(Discover);
        sb.Append("Write-Output 'DONE'\r\nexit 0\r\n");
        return sb.ToString();
    }

    /// <summary>
    /// Vagthundens lette tjek (PowerShell 5.1) i brugerens session. Rører kun drev, der mangler eller ikke svarer.
    /// Svar: DRIVE|key|L|ok[|igen], DRIVE|key|L|fejl|årsag, NEW|L|unc (Fleetdeck har lavet det), FOUND|L|unc|etiket.
    /// </summary>
    private static string CheckScript(List<(DriveSpec D, string L)> watch, Dictionary<string, string> owned)
    {
        var sb = new StringBuilder();
        AppendWant(sb, watch);
        AppendOwned(sb, owned);
        sb.Append(Common);
        sb.Append("""
            $reach = @{}
            # svarer serveren overhovedet (SMB)? Hurtigt, saa Test-Path aldrig haenger paa en slukket server
            function Reach($h) {
              if ($reach.ContainsKey($h)) { return $reach[$h] }
              $ok = $false
              foreach ($port in 445, 139) {
                try { $c = New-Object Net.Sockets.TcpClient; $a = $c.BeginConnect($h, $port, $null, $null); if ($a.AsyncWaitHandle.WaitOne(2000) -and $c.Connected) { $ok = $true }; $c.Close() } catch { }
                if ($ok) { break }
              }
              $reach[$h] = $ok; return $ok
            }
            function Alive($l) { return [bool](Test-Path -LiteralPath "$($l):\" -ErrorAction SilentlyContinue) }
            # genopret en husket forbindelse med det login, Windows allerede har
            function Restore($l) {
              try {
                if (-not ('Fd.Mpr' -as [type])) { Add-Type -Namespace Fd -Name Mpr -MemberDefinition '[DllImport("mpr.dll", CharSet = CharSet.Unicode)] public static extern int WNetRestoreSingleConnectionW(IntPtr hwnd, string name, bool ui);' }
                return [Fd.Mpr]::WNetRestoreSingleConnectionW([IntPtr]::Zero, "$($l):", $false)
              } catch { return -1 }
            }
            $mine = @{}; foreach ($k in $owned.Keys) { $mine[$k] = $owned[$k] }
            foreach ($w in $want) {
              $l = $w.L
              $cur = Cur $l
              if ($cur -and (Norm $cur) -ne (Norm $w.U)) { Write-Output "DRIVE|$($w.K)|$l|fejl|optaget|$cur"; continue }
              if (-not (Reach $w.H)) { Write-Output "DRIVE|$($w.K)|$l|fejl|kode:53|"; continue }
              if ($cur) {
                if (Alive $l) { Write-Output "DRIVE|$($w.K)|$l|ok"; continue }
                Restore $l | Out-Null
                if (Alive $l) { Write-Output "DRIVE|$($w.K)|$l|ok|igen"; continue }
                # husket i profilen beholdes (updateProfile = false), saa intet gaar tabt, hvis det ikke lykkes
                try { $net.RemoveNetworkDrive("$($l):", $true, $false) } catch { }
              }
              try {
                try { $net.MapNetworkDrive("$($l):", $w.U, $true) }
                catch { $c = Code $_; if ($c -eq 1202 -or $c -eq 85) { $net.MapNetworkDrive("$($l):", $w.U, $false) } else { throw } }
                if (-not $w.O -and -not $cur -and -not $mine.ContainsKey($l)) { $mine[$l] = $w.U; Write-Output "NEW|$l|$($w.U)" }
                Write-Output "DRIVE|$($w.K)|$l|ok|igen"
              }
              catch { Write-Output "DRIVE|$($w.K)|$l|fejl|$(Why (Code $_))" }
            }

            """);
        sb.Append(Discover);
        sb.Append("Write-Output 'DONE'\r\nexit 0\r\n");
        return sb.ToString();
    }

    // ---------- hjælpere ----------

    /// <summary>Scriptets fejlkode til en dansk forklaring.</summary>
    private static string Reason(string raw)
    {
        if (raw == "ingen-ledige") return "der er ingen ledige drevbogstaver";
        if (raw.StartsWith("optaget", StringComparison.Ordinal)) return raw.Split('|') is [_, var cur, ..] && cur != "#lokal" ? $"bogstavet er optaget (peger på {cur})" : "bogstavet er optaget";
        if (!raw.StartsWith("kode:", StringComparison.Ordinal)) return raw.Length > 0 ? raw : "ukendt fejl";
        var parts = raw[5..].Split('|', 2);
        int.TryParse(parts[0], out var code);
        return code switch
        {
            86 or 1326 or 1327 or 1330 or 1331 or 1909 => "forkert brugernavn eller adgangskode",
            5 or 65 => "adgang nægtet (har brugeren adgang til mappen?)",
            53 or 1203 or 1222 or 64 or 1231 or 1311 => "serveren kan ikke nås (forkert navn, slukket eller intet netværk)",
            67 or 2116 => "serveren eller mappen findes ikke",
            1219 => "der er allerede forbindelse til serveren med en anden bruger",
            85 => "bogstavet er optaget",
            1200 => "ugyldigt drevbogstav",
            _ => $"fejl {code}: {(parts.Length > 1 ? parts[1].Trim() : "")}".TrimEnd(' ', ':'),
        };
    }

    private Dictionary<string, DriveState> Prev() => _states.GroupBy(s => s.Key).ToDictionary(g => g.Key, g => g.First());

    private static bool Valid(DriveSpec d) => d.Host.Trim().Length > 0 && d.Share.Trim().Length > 0 && (d.Letter is not { Length: > 0 } l || IsLetter(l));

    private static string Letter(string? l) => (l ?? "").Trim().TrimEnd(':').ToUpperInvariant();

    private static DriveState State(DriveSpec d, string? letter, bool ok, string? error) => new()
    {
        Key = d.Key, Name = d.Name.Length > 0 ? d.Name : d.Share, Ok = ok, Error = error,
        Target = letter is { Length: > 0 } l ? l.Trim().TrimEnd(':').ToUpperInvariant() + ":" : "",
    };

    private static bool IsLetter(string l) { l = l.Trim().TrimEnd(':'); return l.Length == 1 && char.ToUpperInvariant(l[0]) is >= 'D' and <= 'Z'; }

    private static string Short(string s) { s = s.Replace('\n', ' ').Trim(); return s.Length > 200 ? s[..200] + "…" : s; }

    private static string Signature(List<DriveState> states) =>
        string.Join('\n', states.Select(s => $"{s.Key}|{s.Target}|{s.Ok}|{s.Error}|{s.Name}"));

    private static string Hash(List<DriveSpec> want, ShareInfo? share)
    {
        var text = string.Join('\n', want.Select(d => $"{d.Key}|{d.Host}|{d.Share}|{d.Letter}|{d.Name}|{d.OwnLogin}")) + $"\n{share?.User}\n{share?.Password}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}
