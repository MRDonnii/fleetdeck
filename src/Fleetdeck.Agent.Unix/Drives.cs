using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fleetdeck.Shared;

namespace Fleetdeck.Agent.Unix;

/// <summary>
/// Netværksdrev (SMB) fra profilen og maskinen.
/// Linux: /mnt/Navn via systemd-automount (ellers en linje i /etc/fstab). Mac: monteret i brugerens session + genvej ~/Navn.
/// Kun drev, Fleetdeck selv har lavet (mærket "# fleetdeck:nøgle"), bliver ændret eller fjernet.
/// Drev, brugeren selv har koblet på (OwnLogin), meldes til serveren og holdes forbundet med deres egen opsætning
/// (fstab-linje, systemd-enhed eller nøglering) – Fleetdeck ændrer eller fjerner dem aldrig.
/// En vagt tjekker hvert minut (og straks efter dvale), at drevene er forbundet, og forbinder dem igen.
/// </summary>
public static class Drives
{
    private const string Creds = AgentConfig.Dir + "/smb-credentials";
    private const string Marker = "# fleetdeck:";
    private const string UnitDir = "/etc/systemd/system";
    private const string Fstab = "/etc/fstab";
    private const string MacState = AgentConfig.Dir + "/mac-drives.json";
    private const string SeenFile = AgentConfig.Dir + "/found-drives.json";
    private const string HostRx = @"^[A-Za-z0-9.\-]{1,253}$";
    private static readonly HashSet<string> NetTypes = ["cifs", "smb3", "smbfs", "nfs", "nfs4"];

    private sealed record Drive(DriveSpec Spec, string Dir)
    {
        public string Key => Spec.Key;
        public string Name => Spec.Name is { Length: > 0 } n ? n : Spec.Share;
        public bool Own => Spec.OwnLogin;
        /// <summary>Hvor drevet er på maskinen. Egne drev: hvor det blev fundet ("" = findes ikke længere).</summary>
        public string Where => Own ? Resolve(Spec) ?? "" : Mac.Is ? "/Volumes/" + Spec.Share : "/mnt/" + Dir;
    }

    /// <summary>Et drev fundet på maskinen (ikke lavet af Fleetdeck); huskes, så det kan findes igen, når det er faldet ud.</summary>
    private sealed record Seen(string Host, string Share, string Target, string Name, string Type);

    private static readonly object L = new();
    private static List<Drive> _want = [];
    private static string _user = "", _pass = "";
    private static string _applied = "";
    private static DateTime _lastRun = DateTime.MinValue;
    private static int _busy;
    private static Dictionary<string, string> _errors = [];
    private static readonly Dictionary<string, string> _units = [];   // nøgle -> mount-enhed (systemd)
    private static bool _cifsTried;
    private static string? _cifsError;
    private static List<Seen> _seen = [];
    private static bool _seenLoaded;
    private static HashSet<string> _current = [];   // mapper, der blev fundet ved seneste tjek
    private static readonly Dictionary<string, (DateTime Next, int Fails)> _retry = [];
    private static string? _reported;   // "nøgle:ok;…" fra den seneste status i helbredet
    private static string _foundSig = "[]";
    private static DateTime _foundRetry = DateTime.MinValue;

    private static string Q(string s) => Pkg.Q(s);

    /// <summary>Fra check-in: husk ønsket og anvend det i baggrunden, når listen/login er ændret, og ellers højst hver time
    /// (vagten tjekker hvert minut, at drevene er forbundet).</summary>
    public static void Want(CheckinResponse r, Func<Task> changed)
    {
        if (Unraid.Is) return;   // Unraid er selv serveren
        var want = Plan(r.Drives);
        var managed = want.Where(d => !d.Own).ToList();   // egne drev sættes aldrig op af Fleetdeck – kun vagten holder øje med dem
        string user = r.Share?.User ?? "", pass = r.Share?.Password ?? "";
        var sig = JsonSerializer.Serialize(new { d = managed.Select(d => new[] { d.Key, d.Spec.Host, d.Spec.Share, d.Dir }), user, pass });
        lock (L) { _want = want; _user = user; _pass = pass; }
        if (sig == _applied && DateTime.UtcNow - _lastRun < TimeSpan.FromHours(1)) return;
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;   // en kørsel (eller vagten) er i gang
        // _busy frigives først, når kørslen er helt færdig (alle kommandoer har egne tidsgrænser), så de aldrig overlapper
        var work = Task.Run(() =>
        {
            try { return Mac.Is ? ApplyMac(managed, user, pass) : ApplyLinux(managed, user, pass); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
            { Log.Warn($"Netværksdrev: {e.Message}"); return false; }
            finally { _applied = sig; _lastRun = DateTime.UtcNow; Interlocked.Exchange(ref _busy, 0); }
        });
        _ = Task.Run(async () =>
        {
            if (await Task.WhenAny(work, Task.Delay(TimeSpan.FromMinutes(15))) != work) { Log.Warn("Netværksdrev: opsætningen tager for lang tid"); return; }
            if (!await work) return;
            try { await changed(); } catch (Exception e) { Log.Warn($"Helbred efter netværksdrev: {e.Message}"); }
        });
    }

    /// <summary>Gyldige drev med et sikkert, entydigt mappenavn.</summary>
    private static List<Drive> Plan(List<DriveSpec> specs)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<Drive>();
        foreach (var d in specs)
        {
            if (!Regex.IsMatch(d.Key, "^[A-Za-z0-9_-]{1,40}$") || !Regex.IsMatch(d.Host, HostRx) || list.Any(x => x.Key == d.Key)) continue;
            if (d.OwnLogin)
            {
                // egne drev: delen kan være en undermappe/NFS-sti; bruges kun til at finde drevet og (Mac) i en mount-adresse
                if (!Regex.IsMatch(d.Share, @"^[^\x00-\x1f\\""'`$]{1,200}$")) continue;
                list.Add(new Drive(d, Safe(d.Name is { Length: > 0 } on ? on : d.Share)));
                continue;
            }
            if (!Regex.IsMatch(d.Share, @"^[^\x00-\x1f/\\""'#%$`]{1,80}$")) continue;
            var dir = Safe(d.Name is { Length: > 0 } n ? n : d.Share);
            if (dir.Length == 0) dir = Safe(d.Share) is { Length: > 0 } s ? s : d.Key;
            var name = dir;
            for (var i = 2; !used.Add(name); i++) name = $"{dir}_{i}";
            list.Add(new Drive(d, name));
        }
        return list;
    }

    private static string Safe(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s.Trim()) sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_');
        var r = sb.ToString().Trim('.', '_');
        return r.Length > 64 ? r[..64] : r;
    }

    /// <summary>Status til helbredet: er hvert drev monteret (automount udløses først)?</summary>
    public static List<DriveState> Status()
    {
        if (Unraid.Is) return [];
        List<Drive> want; Dictionary<string, string> errors;
        lock (L) { want = _want.ToList(); errors = new(_errors); }
        var list = new List<DriveState>();
        var macMounted = Mac.Is && want.Count > 0 ? MacMounted(Shell.Out("mount", 10)) : [];
        foreach (var d in want)
        {
            var t = d.Where;
            if (t.Length == 0)
            {
                list.Add(new DriveState { Key = d.Key, Name = d.Name, Target = "", Ok = false, Error = errors.GetValueOrDefault(d.Key) ?? "Drevet findes ikke længere på maskinen" });
                continue;
            }
            if (Mac.Is)
            {
                var ok = macMounted.Contains(t);
                list.Add(new DriveState { Key = d.Key, Name = d.Name, Target = t, Ok = ok, Error = ok ? null : errors.GetValueOrDefault(d.Key) ?? "Ikke forbundet" });
                continue;
            }
            var mounted = IsMounted(t);
            string? unit; lock (L) unit = d.Own ? null : _units.GetValueOrDefault(d.Key);
            if (!mounted && (unit is not null || IsAutofs(t))) { Trigger(t, 15); mounted = IsMounted(t); }
            string? err = null;
            if (!mounted) err = errors.GetValueOrDefault(d.Key) ?? (unit is not null ? JournalError(unit) : null) ?? "Ikke forbundet";
            list.Add(new DriveState { Key = d.Key, Name = d.Name, Target = t, Ok = mounted, Error = err });
        }
        lock (L) _reported = Sig(list.Select(x => (x.Key, x.Ok)));
        return list;
    }

    private static string Sig(IEnumerable<(string Key, bool Ok)> states) =>
        string.Join(';', states.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => $"{s.Key}:{(s.Ok ? 1 : 0)}"));

    // ------------------------------------------------------------------ Vagt

    /// <summary>Start vagten: hvert minut (og straks efter dvale) tjekkes drevene, og fundne drev meldes til serveren.</summary>
    public static void Start(Api api, Func<Task> changed, CancellationToken stop)
    {
        if (Unraid.Is) return;
        _ = Task.Run(() => Watch(api, changed, stop));
    }

    private static async Task Watch(Api api, Func<Task> changed, CancellationToken stop)
    {
        // korte skridt efter vægur: Task.Delay står stille under dvale, så et hul på over 2 minutter betyder, at maskinen har sovet
        var last = DateTime.UtcNow;
        var lastCheck = DateTime.MinValue;
        while (!stop.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            var resumed = now - last > TimeSpan.FromMinutes(2);
            if (resumed)
            {
                Log.Info("Netværksdrev: maskinen er vågnet – tjekker drevene");
                lock (L) _retry.Clear();
                _lastRun = DateTime.MinValue;   // fuld opsætning ved næste check-in
            }
            if ((resumed || now - lastCheck >= TimeSpan.FromSeconds(60)) && Interlocked.CompareExchange(ref _busy, 1, 0) == 0)
            {
                lastCheck = now;
                List<Seen>? found = null;
                var stateChanged = false;
                try { (found, stateChanged) = await Task.Run(Tick, stop); }
                catch (Exception e) when (e is not OperationCanceledException) { Log.Warn($"Netværksdrev-vagt: {e.Message}"); }
                finally { Interlocked.Exchange(ref _busy, 0); }
                if (found is not null) await Report(api, found, stop);
                if (stateChanged)
                    try { await changed(); } catch (Exception e) when (e is not OperationCanceledException) { Log.Warn($"Helbred efter netværksdrev: {e.Message}"); }
            }
            last = DateTime.UtcNow;
            try { await Task.Delay(TimeSpan.FromSeconds(10), stop); } catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Ét tjek: find egne drev, forbind faldne drev igen. Returnerer fundne drev og om status er ændret siden helbredet.</summary>
    private static (List<Seen>, bool) Tick()
    {
        List<Drive> want; string user, pass;
        lock (L) { want = _want.ToList(); user = _user; pass = _pass; }
        var mountOut = Mac.Is ? Shell.Out("mount", 10) : "";
        var found = Mac.Is ? FindMac(want, mountOut) : FindLinux(want);
        Remember(found);
        var states = Mac.Is ? WatchMac(want, user, pass, mountOut) : WatchLinux(want);
        var sig = Sig(states.Select(s => (s.Key, s.Value)));
        bool changed; lock (L) changed = _reported is not null && sig != _reported;
        return (found, changed);
    }

    /// <summary>Meld fundne drev til serveren, når listen er ændret.</summary>
    private static async Task Report(Api api, List<Seen> found, CancellationToken stop)
    {
        var drives = found.OrderBy(f => f.Target, StringComparer.Ordinal)
            .Select(f => new FoundDrive { Host = f.Host, Share = f.Share, Target = f.Target, Name = f.Name }).ToList();
        var sig = JsonSerializer.Serialize(drives.Select(d => new[] { d.Host, d.Share, d.Target, d.Name }));
        if (sig == _foundSig || DateTime.UtcNow < _foundRetry) return;
        try
        {
            await api.Post("/api/agent/drives/found", new FoundDrivesRequest { Drives = drives }, stop);
            _foundSig = sig;
            if (drives.Count > 0) Log.Info($"Netværksdrev fundet på maskinen: {string.Join(", ", drives.Select(d => d.Target))}");
        }
        catch (Exception e) when (!stop.IsCancellationRequested)
        {
            _foundRetry = DateTime.UtcNow + TimeSpan.FromMinutes(10);
            Log.Warn($"Fundne netværksdrev blev ikke sendt: {e.Message}");
        }
    }

    private static void Remember(List<Seen> found)
    {
        List<Seen> next;
        lock (L)
        {
            LoadSeen();
            _current = found.Select(f => f.Target).ToHashSet();
            next = found.Concat(_seen.Where(s => !_current.Contains(s.Target))).Take(50).ToList();   // nyeste først
            if (next.SequenceEqual(_seen)) return;
            _seen = next;
        }
        try
        {
            Directory.CreateDirectory(AgentConfig.Dir);
            File.WriteAllText(SeenFile, JsonSerializer.Serialize(next));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static void LoadSeen()
    {
        if (_seenLoaded) return;
        _seenLoaded = true;
        try { if (File.Exists(SeenFile)) _seen = JsonSerializer.Deserialize<List<Seen>>(File.ReadAllText(SeenFile)) ?? []; }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { }
    }

    private static string HS(string host, string share) => $"{host.Trim()}/{share.Trim().Trim('/')}".ToLowerInvariant();

    private static Seen? Find(DriveSpec s)
    {
        lock (L)
        {
            LoadSeen();
            var hs = HS(s.Host, s.Share);
            return _seen.Where(x => HS(x.Host, x.Share) == hs)
                .OrderByDescending(x => _current.Contains(x.Target))
                .ThenByDescending(x => string.Equals(Safe(x.Name), s.Name, StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();
        }
    }

    /// <summary>Hvor et eget drev er på maskinen (fundet nu eller tidligere).</summary>
    private static string? Resolve(DriveSpec s) => Find(s)?.Target;

    private static bool Due(string key) { lock (L) return !_retry.TryGetValue(key, out var r) || DateTime.UtcNow >= r.Next; }

    /// <summary>Husk et forsøg: efter fejl ventes 1, 2, 4, 8 og derefter 15 minutter. Returnerer antal fejl i træk.</summary>
    private static int Tried(string key, bool ok)
    {
        lock (L)
        {
            if (ok) { _retry.Remove(key); return 0; }
            var n = _retry.TryGetValue(key, out var r) ? r.Fails + 1 : 1;
            _retry[key] = (DateTime.UtcNow + TimeSpan.FromMinutes(Math.Min(15, 1 << Math.Min(n - 1, 4))), n);
            return n;
        }
    }

    private static void SetError(string key, string? err)
    {
        lock (L) { if (err is null) _errors.Remove(key); else _errors[key] = err; }
    }

    private static Dictionary<string, bool> WatchLinux(List<Drive> want)
    {
        var states = new Dictionary<string, bool>();
        if (want.Count == 0) return states;
        var mounts = ProcMounts();
        foreach (var d in want)
        {
            var t = d.Where;
            if (t.Length == 0) { states[d.Key] = false; continue; }
            var ok = mounts.Any(m => m.Mp == t && NetTypes.Contains(m.Type));
            if (!ok && mounts.Any(m => m.Mp == t && m.Type == "autofs")) { Trigger(t, 5); ok = IsMounted(t); }
            if (!ok && Due(d.Key))
            {
                var (tried, err) = RemountLinux(d, t);
                if (tried)
                {
                    ok = IsMounted(t);
                    if (!ok && IsAutofs(t)) { Trigger(t, 10); ok = IsMounted(t); }
                    if (ok) Log.Info($"Netværksdrev forbundet igen: {t}");
                }
                if (!ok)
                {
                    if (Tried(d.Key, false) == 1 && tried) Log.Warn($"Netværksdrev {t} kunne ikke forbindes igen: {err ?? "ikke forbundet"}");
                    if (err is not null || tried) SetError(d.Key, err ?? "Ikke forbundet");
                }
            }
            if (ok) { SetError(d.Key, null); Tried(d.Key, true); }
            states[d.Key] = ok;
        }
        return states;
    }

    /// <summary>Forbind et drev igen med dets egen opsætning. Tried=false: der er intet at genstarte.</summary>
    private static (bool Tried, string? Err) RemountLinux(Drive d, string t)
    {
        string cmd;
        if (!d.Own)
        {
            string? unit; lock (L) unit = _units.GetValueOrDefault(d.Key);
            if (unit is not null) cmd = $"systemctl reset-failed {Q(unit)} 2>/dev/null; systemctl restart {Q(AutoOf(unit))}";
            else if (!Init.IsSystemd && FstabEntries().Any(e => e.Ours && e.Mp == t)) cmd = $"mount {Q(t)}";
            else return (false, null);   // ikke sat op endnu (fejlen fra opsætningen gælder)
        }
        else
        {
            // brugerens egen opsætning: automount-/mount-enhed (også fra fstab), ellers fstab-linjen
            var unit = Init.IsSystemd ? Shell.Out($"systemd-escape -p --suffix=mount {Q(t)}", 10) : "";
            if (unit.EndsWith(".mount") && Loaded(AutoOf(unit))) cmd = $"systemctl reset-failed {Q(unit)} 2>/dev/null; systemctl restart {Q(AutoOf(unit))}";
            else if (unit.EndsWith(".mount") && Loaded(unit)) cmd = $"systemctl reset-failed {Q(unit)} 2>/dev/null; systemctl start {Q(unit)}";
            else if (FstabEntries().Any(e => !e.Ours && e.Mp == t)) cmd = $"mount {Q(t)}";
            else return (false, "Ikke forbundet (koblet på i hånden – kan ikke forbindes igen automatisk)");
        }
        var r = Shell.Run(cmd, TimeSpan.FromSeconds(60));
        return (true, r.ExitCode == 0 ? null : Translate(r.Output));
    }

    private static string AutoOf(string mountUnit) => mountUnit[..^".mount".Length] + ".automount";

    private static bool Loaded(string unit) => Shell.Out($"systemctl show -p LoadState --value {Q(unit)}", 10) == "loaded";

    /// <summary>Netværksdrev på Linux, som Fleetdeck ikke har lavet: /proc/mounts og /etc/fstab (cifs/smb3/smbfs/nfs).</summary>
    private static List<Seen> FindLinux(List<Drive> want)
    {
        var ours = want.Where(d => !d.Own).Select(d => d.Where).ToHashSet();
        if (Directory.Exists(UnitDir))
            foreach (var f in Directory.GetFiles(UnitDir, "*.*mount"))
            {
                var text = Read(f);
                if (OwnerKey(text) is not null && Regex.Match(text, @"^Where=(.+)$", RegexOptions.Multiline) is { Success: true } m) ours.Add(Norm(m.Groups[1].Value.Trim()));
            }
        var tab = FstabEntries();
        foreach (var e in tab) if (e.Ours) ours.Add(e.Mp);
        var list = new List<Seen>();
        void Add(string src, string mp, string type)
        {
            if (!NetTypes.Contains(type) || ours.Contains(mp) || list.Any(x => x.Target == mp) || Source(src, type) is not { } hs) return;
            list.Add(new Seen(hs.Host, hs.Share, mp, Path.GetFileName(mp) is { Length: > 0 } n ? n : mp, type));
        }
        foreach (var m in ProcMounts()) Add(m.Src, m.Mp, m.Type);
        foreach (var e in tab)
            if (!e.Ours && (!e.Opts.Split(',').Contains("noauto") || e.Opts.Contains("x-systemd.automount"))) Add(e.Src, e.Mp, e.Type);
        return list;
    }

    /// <summary>Server og del fra "//server/del" (SMB) eller "server:/sti" (NFS).</summary>
    private static (string Host, string Share)? Source(string src, string type)
    {
        string host, share;
        if (type.StartsWith("nfs"))
        {
            var i = src.IndexOf(":/", StringComparison.Ordinal);
            if (i <= 0) return null;
            host = src[..i]; share = src[(i + 1)..];
        }
        else
        {
            var s = src.Replace('\\', '/');
            if (!s.StartsWith("//")) return null;
            s = s[2..];
            var i = s.IndexOf('/');
            if (i <= 0) return null;
            host = s[..i]; share = s[(i + 1)..];
            if (host.LastIndexOf('@') is var at and >= 0) host = host[(at + 1)..];   // Mac: //bruger@server/del
            if (host.EndsWith("._smb._tcp.local", StringComparison.OrdinalIgnoreCase)) host = host[..^"._smb._tcp.local".Length] + ".local";
        }
        share = share.Trim('/');
        if (!Regex.IsMatch(host, HostRx) || share.Length is 0 or > 200 || Regex.IsMatch(share, @"[\x00-\x1f\\""'`$]")) return null;
        return (host, share);
    }

    private sealed record Tab(string Src, string Mp, string Type, string Opts, bool Ours);

    private static List<Tab> FstabEntries()
    {
        string[] lines;
        try { lines = File.Exists(Fstab) ? File.ReadAllLines(Fstab) : []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
        var list = new List<Tab>();
        for (var i = 0; i < lines.Length; i++)
        {
            var l = lines[i].Trim();
            if (l.Length == 0 || l.StartsWith('#')) continue;
            if (l.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries) is not { Length: > 2 } f) continue;
            list.Add(new Tab(Unesc(f[0]), Norm(Unesc(f[1])), f[2], f.Length > 3 ? f[3] : "", i > 0 && lines[i - 1].StartsWith(Marker)));
        }
        return list;
    }

    private static List<(string Src, string Mp, string Type)> ProcMounts()
    {
        try { return [.. File.ReadLines("/proc/mounts").Select(l => l.Split(' ')).Where(f => f.Length > 2).Select(f => (Unesc(f[0]), Norm(Unesc(f[1])), f[2]))]; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>Mellemrum m.m. står som \040 i /proc/mounts og fstab.</summary>
    private static string Unesc(string s) => Regex.Replace(s, @"\\([0-7]{3})", m => ((char)Convert.ToInt32(m.Groups[1].Value, 8)).ToString());

    private static string Norm(string p) => p.Length > 1 ? p.TrimEnd('/') : p;

    // ------------------------------------------------------------------ Linux

    private static bool ApplyLinux(List<Drive> want, string user, string pass)
    {
        var changed = false;
        var errors = new Dictionary<string, string>();
        var systemd = Init.IsSystemd;
        var keys = want.Select(d => d.Key).ToHashSet();

        // 1) Fjern Fleetdecks egne drev, der ikke længere ønskes (eller har skiftet mappe)
        var units = new Dictionary<string, string>();   // nøgle -> ønsket mount-enhed
        if (systemd)
            foreach (var d in want)
                if (Shell.Out($"systemd-escape -p --suffix=mount {Q(d.Where)}", 10) is { Length: > 0 } u && u.EndsWith(".mount")) units[d.Key] = u;
        var removedUnits = false;
        if (Directory.Exists(UnitDir))
            foreach (var f in Directory.GetFiles(UnitDir, "*.*mount"))
            {
                if (OwnerKey(Read(f)) is not { } k) continue;
                var name = Path.GetFileName(f);
                var stem = name.EndsWith(".automount") ? name[..^".automount".Length] : name[..^".mount".Length];
                if (systemd && units.TryGetValue(k, out var keep) && keep[..^".mount".Length] == stem) continue;
                var where = Regex.Match(Read(f), @"^Where=(.+)$", RegexOptions.Multiline).Groups[1].Value.Trim();
                Shell.Run(name.EndsWith(".automount") ? $"systemctl disable --now {Q(name)}" : $"systemctl stop {Q(name)}", TimeSpan.FromSeconds(30));
                File.Delete(f);
                RemoveDir(where);
                removedUnits = changed = true;
                Log.Info($"Netværksdrev fjernet: {name}");
            }
        if (removedUnits) Shell.Run("systemctl daemon-reload", TimeSpan.FromSeconds(60));

        var fstab = File.Exists(Fstab) ? File.ReadAllLines(Fstab).ToList() : [];
        var fstabDirty = false;
        for (var i = 0; i < fstab.Count; i++)
        {
            if (!fstab[i].StartsWith(Marker)) continue;
            var k = fstab[i][Marker.Length..].Trim();
            var entry = i + 1 < fstab.Count ? fstab[i + 1] : "";
            var mp = entry.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries) is { Length: > 1 } fe ? fe[1] : "";
            if (!systemd && keys.Contains(k) && want.First(d => d.Key == k).Where == mp) continue;
            if (mp.Length > 0) { Shell.Run($"umount {Q(mp)}", TimeSpan.FromSeconds(30)); RemoveDir(mp); }
            fstab.RemoveRange(i, i + 1 < fstab.Count ? 2 : 1); i--;
            fstabDirty = changed = true;
            Log.Info($"Netværksdrev fjernet fra fstab: {k}");
        }

        // 2) cifs-utils (mount.cifs) skal være installeret
        if (want.Count > 0 && !HasCifs())
        {
            if (!_cifsTried)
            {
                _cifsTried = true;
                Log.Info("Installerer cifs-utils til netværksdrev");
                Pkg.Refresh();
                var r = Shell.Run(Pkg.InstallCmd(Pkg.NameFor("cifs-utils")), TimeSpan.FromMinutes(10));
                _cifsError = HasCifs() ? null : $"cifs-utils kunne ikke installeres: {LastLine(r.Output)}";
            }
            if (!HasCifs())
            {
                foreach (var d in want) errors[d.Key] = _cifsError ?? "cifs-utils mangler (mount.cifs)";
                if (fstabDirty) WriteFstab(fstab);
                return SetErrors(errors) | changed;
            }
        }

        // 3) Login (fælles for alle drev): kun root kan læse filen
        var creds = (user.Length > 0 ? $"username={user}\n" : "") + (pass.Length > 0 ? $"password={pass}\n" : "");
        var credsChanged = false;
        if (want.Count > 0 && (!File.Exists(Creds) || Read(Creds) != creds))
        {
            WriteSecret(Creds, creds);
            credsChanged = changed = true;
        }
        var (uid, gid) = Owner();
        var opts = $"{(user.Length > 0 ? $"credentials={Creds}" : "guest")},uid={uid},gid={gid},iocharset=utf8,vers=3.0,file_mode=0664,dir_mode=0775,nofail,_netdev";

        // 4) Opret/ret hvert ønsket drev
        var toStart = new List<Drive>();
        var reload = false;
        foreach (var d in want)
        {
            if (systemd)
            {
                if (!units.TryGetValue(d.Key, out var unit)) { errors[d.Key] = "Kunne ikke lave systemd-enhedens navn"; continue; }
                var auto = unit[..^".mount".Length] + ".automount";
                string mountFile = Path.Combine(UnitDir, unit), autoFile = Path.Combine(UnitDir, auto);
                if ((File.Exists(mountFile) && OwnerKey(Read(mountFile)) != d.Key) || (File.Exists(autoFile) && OwnerKey(Read(autoFile)) != d.Key) || ForeignFstab(fstab, d.Where))
                { errors[d.Key] = $"{d.Where} bruges allerede af en anden opsætning på maskinen"; continue; }
                lock (L) _units[d.Key] = unit;
                string E(string s) => s.Replace("%", "%%");
                var head = $"[Unit]\nDescription=Fleetdeck-netværksdrev {d.Dir} (Fleetdeck {d.Key})\n{Marker}{d.Key}\n";
                var mountText = head + $"""
                    After=network-online.target
                    Wants=network-online.target

                    [Mount]
                    What=//{d.Spec.Host}/{E(d.Spec.Share)}
                    Where={d.Where}
                    Type=cifs
                    Options={opts}
                    TimeoutSec=30

                    """;
                var autoText = head + $"""

                    [Automount]
                    Where={d.Where}
                    TimeoutIdleSec=0

                    [Install]
                    WantedBy=multi-user.target

                    """;
                var dirty = credsChanged;
                if (Read(mountFile) != mountText) { File.WriteAllText(mountFile, mountText); dirty = true; }
                if (Read(autoFile) != autoText) { File.WriteAllText(autoFile, autoText); dirty = true; }
                if (dirty) { reload = changed = true; toStart.Add(d); }
                else if (Shell.Out($"systemctl is-active {Q(auto)}", 10) != "active" || Shell.Out($"systemctl is-enabled {Q(auto)}", 10) != "enabled") toStart.Add(d);
            }
            else
            {
                if (ForeignFstab(fstab, d.Where)) { errors[d.Key] = $"{d.Where} bruges allerede i /etc/fstab"; continue; }
                var line = $"//{d.Spec.Host}/{d.Spec.Share.Replace(" ", "\\040")} {d.Where} cifs {opts} 0 0";
                var at = fstab.IndexOf(Marker + d.Key);
                if (at >= 0 && at + 1 < fstab.Count && fstab[at + 1] == line && !credsChanged)
                {
                    if (!IsMounted(d.Where)) toStart.Add(d);
                    continue;
                }
                if (at >= 0) fstab[at + 1] = line; else fstab.AddRange([Marker + d.Key, line]);
                fstabDirty = changed = true;
                if (IsMounted(d.Where)) Shell.Run($"umount {Q(d.Where)}", TimeSpan.FromSeconds(30));
                toStart.Add(d);
            }
        }
        if (fstabDirty) WriteFstab(fstab);
        if (reload) Shell.Run("systemctl daemon-reload", TimeSpan.FromSeconds(60));

        // 5) Start/montér
        foreach (var d in toStart)
        {
            if (systemd)
            {
                var unit = units[d.Key]; var auto = unit[..^".mount".Length] + ".automount";
                if (IsMounted(d.Where)) Shell.Run($"systemctl stop {Q(unit)}", TimeSpan.FromSeconds(30));   // nye indstillinger ved næste brug
                var r = Shell.Run($"systemctl enable {Q(auto)} && systemctl restart {Q(auto)}", TimeSpan.FromSeconds(60));
                if (r.ExitCode != 0) errors[d.Key] = Translate(r.Output);
                changed = true;
            }
            else
            {
                Directory.CreateDirectory(d.Where);
                var r = Shell.Run($"mount {Q(d.Where)}", TimeSpan.FromSeconds(60));
                if (r.ExitCode != 0) errors[d.Key] = Translate(r.Output);
                changed = true;
            }
        }

        // 6) Tjek at de virker (automount udløses ved at kigge i mappen)
        foreach (var d in want)
        {
            if (errors.ContainsKey(d.Key)) continue;
            if (!IsMounted(d.Where) && systemd) Trigger(d.Where);
            if (IsMounted(d.Where)) continue;
            errors[d.Key] = (systemd && units.TryGetValue(d.Key, out var u) ? JournalError(u) : null) ?? "Ikke forbundet";
        }
        if (changed) Log.Info($"Netværksdrev opdateret: {want.Count} drev, {errors.Count} med fejl");
        return SetErrors(errors) | changed;
    }

    private static bool SetErrors(Dictionary<string, string> errors)
    {
        lock (L)
        {
            // fejl for egne drev kommer fra vagten, ikke fra opsætningen
            foreach (var d in _want.Where(d => d.Own))
                if (_errors.TryGetValue(d.Key, out var e)) errors.TryAdd(d.Key, e);
            var same = errors.Count == _errors.Count && errors.All(e => _errors.GetValueOrDefault(e.Key) == e.Value);
            _errors = errors;
            return !same;
        }
    }

    /// <summary>Nøglen i en fil, Fleetdeck har lavet ("# fleetdeck:nøgle"); null = ikke vores.</summary>
    private static string? OwnerKey(string text) =>
        Regex.Match(text, "^" + Regex.Escape(Marker) + @"([A-Za-z0-9_-]+)\s*$", RegexOptions.Multiline) is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>En linje i fstab, som ikke er vores, bruger allerede mappen.</summary>
    private static bool ForeignFstab(List<string> fstab, string where)
    {
        for (var i = 0; i < fstab.Count; i++)
        {
            var l = fstab[i].Trim();
            if (l.Length == 0 || l.StartsWith('#')) continue;
            if (i > 0 && fstab[i - 1].StartsWith(Marker)) continue;
            if (l.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries) is { Length: > 1 } f && f[1].TrimEnd('/') == where) return true;
        }
        return false;
    }

    private static void WriteFstab(List<string> lines)
    {
        var tmp = Fstab + ".fleetdeck.tmp";
        File.WriteAllText(tmp, string.Join('\n', lines) + "\n");
        File.SetUnixFileMode(tmp, (UnixFileMode)0b110_100_100);
        File.Move(tmp, Fstab, true);
        if (Init.IsSystemd) Shell.Run("systemctl daemon-reload", TimeSpan.FromSeconds(60));
    }

    private static void WriteSecret(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.Delete(tmp);
        using (var fs = new FileStream(tmp, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite }))
        using (var w = new StreamWriter(fs)) w.Write(text);
        File.Move(tmp, path, true);
    }

    /// <summary>Ejer af filerne på drevet: brugeren på skærmen; ellers den, der blev brugt sidst; ellers 1000.</summary>
    private static (int Uid, int Gid) Owner()
    {
        if (UserSession.Find() is { } s)
            return (s.Uid, int.TryParse(Shell.Out($"id -g {Q(s.User)}", 10), out var g) ? g : s.Uid);
        var prev = Directory.Exists(UnitDir) ? Directory.GetFiles(UnitDir, "*.mount").Select(Read).Where(t => OwnerKey(t) is not null) : [];
        foreach (var t in prev.Append(Read(Fstab)))
            if (Regex.Match(t, @"credentials=\S*?uid=(\d+),gid=(\d+)|guest,uid=(\d+),gid=(\d+)") is { Success: true } m)
                return (int.Parse(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[3].Value), int.Parse(m.Groups[2].Success ? m.Groups[2].Value : m.Groups[4].Value));
        return (1000, 1000);
    }

    private static bool HasCifs() =>
        File.Exists("/sbin/mount.cifs") || File.Exists("/usr/sbin/mount.cifs") || File.Exists("/usr/bin/mount.cifs") || Shell.Has("mount.cifs");

    /// <summary>Er mappen monteret som netværksdrev (læses direkte i /proc/mounts – ingen processer)?</summary>
    private static bool IsMounted(string where) => ProcMounts().Any(m => m.Mp == where && NetTypes.Contains(m.Type));

    /// <summary>Står der en automount (autofs) på mappen, som udløses ved at kigge i den?</summary>
    private static bool IsAutofs(string where) => ProcMounts().Any(m => m.Mp == where && m.Type == "autofs");

    private static void Trigger(string where, int seconds = 15) => Shell.Run($"ls {Q(where)} >/dev/null 2>&1", TimeSpan.FromSeconds(seconds));

    private static string? JournalError(string unit)
    {
        var log = Shell.Out($"journalctl -u {Q(unit)} -n 20 --no-pager -o cat 2>/dev/null", 15);
        var line = Enumerable.Reverse(log.Split('\n')).FirstOrDefault(l => l.Contains("error", StringComparison.OrdinalIgnoreCase) || l.Contains("Failed", StringComparison.Ordinal));
        return line is null ? null : Translate(log);
    }

    /// <summary>Fejl fra mount/systemctl/osascript, oversat hvor det er tydeligt.</summary>
    private static string Translate(string output)
    {
        if (Regex.IsMatch(output, @"Permission denied|error\(13\)|Authentication|-5045|-5023", RegexOptions.IgnoreCase)) return "Forkert brugernavn eller adgangskode";
        if (Regex.IsMatch(output, @"No such file or directory|error\(2\)|error\(11[23]\)|Host is down|unreachable|No route to host|resolve|-43\b|-5014", RegexOptions.IgnoreCase))
            return "Serveren eller mappen findes ikke";
        return LastLine(output) is { Length: > 0 } l ? l : "Kunne ikke forbinde";
    }

    private static string LastLine(string s)
    {
        var l = s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
        return l.Length > 200 ? l[..200] : l;
    }

    private static void RemoveDir(string dir)
    {
        if (!dir.StartsWith("/mnt/") || dir.Length <= 5) return;
        try { if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static string Read(string f) { try { return File.ReadAllText(f); } catch (Exception) { return ""; } }

    // ------------------------------------------------------------------ Mac

    private sealed record MacDrive(string Volume, string Link);

    private static Dictionary<string, MacDrive> LoadMacState()
    {
        try { if (File.Exists(MacState)) return JsonSerializer.Deserialize<Dictionary<string, MacDrive>>(File.ReadAllText(MacState)) ?? []; }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { }
        return [];
    }

    /// <summary>Monterede netværksdrev under /Volumes ud fra "mount".</summary>
    private static HashSet<string> MacMounted(string mountOut) =>
        Regex.Matches(mountOut, @"^.+? on (/Volumes/.+?) \((?:smbfs|nfs|afpfs|webdav)[,)]", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToHashSet();

    /// <summary>Netværksdrev på Mac'en, som Fleetdeck ikke har lavet (smbfs/nfs under /Volumes).</summary>
    private static List<Seen> FindMac(List<Drive> want, string mountOut)
    {
        var ours = want.Where(d => !d.Own).Select(d => d.Where).ToHashSet();
        ours.UnionWith(LoadMacState().Values.Select(v => v.Volume));
        var list = new List<Seen>();
        foreach (Match m in Regex.Matches(mountOut, @"^(.+?) on (/Volumes/.+?) \((smbfs|nfs)[,)]", RegexOptions.Multiline))
        {
            var mp = m.Groups[2].Value; var type = m.Groups[3].Value;
            if (ours.Contains(mp) || list.Any(x => x.Target == mp) || Source(Uri.UnescapeDataString(m.Groups[1].Value), type) is not { } hs) continue;
            list.Add(new Seen(hs.Host, hs.Share, mp, Path.GetFileName(mp), type));
        }
        return list;
    }

    /// <summary>Montér et drev i brugerens session. Egne drev uden login (macOS bruger nøgleringen). Returnerer fejl eller null.</summary>
    private static string? MacMount(Drive d, string user, string pass, UserSession s)
    {
        string url;
        if (d.Own)
        {
            var nfs = Find(d.Spec)?.Type.StartsWith("nfs") == true;
            url = $"{(nfs ? "nfs" : "smb")}://{d.Spec.Host}/{string.Join('/', d.Spec.Share.Split('/').Select(Uri.EscapeDataString))}";
        }
        else
        {
            var auth = user.Length > 0 ? $"{Uri.EscapeDataString(user)}:{Uri.EscapeDataString(pass)}@" : "guest:@";
            url = $"smb://{auth}{d.Spec.Host}/{Uri.EscapeDataString(d.Spec.Share)}";
        }
        var r = Shell.Run($"launchctl asuser {s.Uid} sudo -u {Q(s.User)} osascript -e {Q($"mount volume \"{url}\"")}", TimeSpan.FromSeconds(60));
        return r.ExitCode == 0 ? null : Translate(r.Output.Replace(url, "smb://…"));
    }

    private static Dictionary<string, bool> WatchMac(List<Drive> want, string user, string pass, string mountOut)
    {
        var states = new Dictionary<string, bool>();
        if (want.Count == 0) return states;
        var mounted = MacMounted(mountOut);
        UserSession? s = null;
        var looked = false;
        foreach (var d in want)
        {
            var t = d.Where;
            if (t.Length == 0) { states[d.Key] = false; continue; }
            var ok = mounted.Contains(t);
            if (!ok && Due(d.Key))
            {
                if (!looked) { s = Mac.Session(); looked = true; }
                if (s is not null && (d.Own || _applied.Length > 0))
                {
                    var err = MacMount(d, user, pass, s);
                    ok = MacMounted(Shell.Out("mount", 10)).Contains(t);
                    if (ok) Log.Info($"Netværksdrev forbundet igen: {t}");
                    else
                    {
                        if (Tried(d.Key, false) == 1) Log.Warn($"Netværksdrev {t} kunne ikke forbindes igen: {err ?? "ikke forbundet"}");
                        SetError(d.Key, err ?? "Ikke forbundet");
                    }
                }
            }
            if (ok) { SetError(d.Key, null); Tried(d.Key, true); }
            states[d.Key] = ok;
        }
        return states;
    }

    private static bool ApplyMac(List<Drive> want, string user, string pass)
    {
        var changed = false;
        var errors = new Dictionary<string, string>();
        var state = LoadMacState();
        var s = Mac.Session();
        var mounted = MacMounted(Shell.Out("mount", 10));

        // fjern drev, Fleetdeck har lavet, som ikke længere ønskes
        foreach (var (key, md) in state.ToList())
        {
            var w = want.FirstOrDefault(d => d.Key == key);
            if (w is not null && w.Where == md.Volume) continue;
            if (Shell.Out("mount").Contains($" on {md.Volume} (smbfs")) Shell.Run($"diskutil unmount {Q(md.Volume)}", TimeSpan.FromSeconds(30));
            if (md.Link.Length > 0 && new FileInfo(md.Link) is { LinkTarget: { } t } && t == md.Volume) File.Delete(md.Link);
            state.Remove(key);
            changed = true;
        }

        foreach (var d in want)
        {
            var vol = d.Where;
            if (s is null) { if (!mounted.Contains(vol)) errors[d.Key] = "Ingen er logget ind på Mac'en"; continue; }
            if (!mounted.Contains(vol))
            {
                var err = MacMount(d, user, pass, s);
                changed = true;
                if (err is not null || !MacMounted(Shell.Out("mount", 10)).Contains(vol)) { errors[d.Key] = err ?? "Ikke forbundet"; continue; }
            }
            // genvej i hjemmemappen (~/Navn), hvis navnet er ledigt
            var link = Path.Combine(s.Home, d.Dir);
            var fi = new FileInfo(link);
            if (!fi.Exists && !Directory.Exists(link) && fi.LinkTarget is null)
            {
                Shell.Run($"sudo -u {Q(s.User)} ln -s {Q(vol)} {Q(link)}", TimeSpan.FromSeconds(10));
                changed = true;
            }
            var ours = new FileInfo(link).LinkTarget == vol;
            if (!state.TryGetValue(d.Key, out var old) || old.Volume != vol || (ours && old.Link != link))
            { state[d.Key] = new MacDrive(vol, ours ? link : old?.Link ?? ""); changed = true; }
        }
        if (changed)
        {
            Directory.CreateDirectory(AgentConfig.Dir);
            File.WriteAllText(MacState, JsonSerializer.Serialize(state));
        }
        return SetErrors(errors) | changed;
    }
}
