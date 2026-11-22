using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Fleetdeck.Shared;

// Printere (Services/Printers på serveren): Fleetdeck lægger printere på maskinerne uden driver (IPP Everywhere) og holder dem på.
// Alt her er ren logik, som både Windows- og Unix-agenten bruger: plan, kommandoer pr. platform (med citering) og fortolkning af svar.

public static class PrinterDuplex
{
    public const string None = "none", Long = "long", Short = "short";
    public static readonly string[] All = [None, Long, Short];
}

public static class PrinterPaper
{
    public static readonly string[] All = ["A4", "A3", "A5", "Letter", "Legal"];
    /// <summary>PWG-navnet til CUPS (-o media=…).</summary>
    public static string Pwg(string paper) => paper switch
    {
        "A3" => "iso_a3_297x420mm",
        "A5" => "iso_a5_148x210mm",
        "Letter" => "na_letter_8.5x11in",
        "Legal" => "na_legal_8.5x14in",
        _ => "iso_a4_210x297mm",
    };
}

/// <summary>En printer, maskinen skal have (fra profil eller maskine).</summary>
public sealed class PrinterSpec
{
    /// <summary>Stabilt id ("p12"), så agenten kan fjerne printere, der ikke længere ønskes.</summary>
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>ipp://, ipps://, http(s)://, socket:// eller lpd://.</summary>
    public string Uri { get; set; } = "";
    public string Location { get; set; } = "";
    public string Paper { get; set; } = "A4";
    public string Duplex { get; set; } = PrinterDuplex.Long;
    public bool Color { get; set; } = true;
    /// <summary>Gør til standardprinter, hvis maskinen ikke har en (rigtig) standardprinter.</summary>
    public bool MakeDefault { get; set; }
    /// <summary>Hold på: læg printeren på igen, hvis nogen fjerner den. Fra: lægges på én gang.</summary>
    public bool Keep { get; set; } = true;
    /// <summary>Kræver driver: driverens navn (Windows) eller CUPS-model/PPD-sti. Null = uden driver (IPP Everywhere).</summary>
    public string? Driver { get; set; }
}

/// <summary>En printer, maskinen har (alle, også dem Fleetdeck ikke har lagt på).</summary>
public sealed class LocalPrinter
{
    public string Name { get; set; } = "";
    /// <summary>Adressen, hvis den kendes (CUPS: device-uri; Windows: TCP/IP- eller IPP-port).</summary>
    public string? Uri { get; set; }
    public string? Driver { get; set; }
    public bool IsDefault { get; set; }
    /// <summary>PDF, XPS, OneNote, fax … – tæller ikke som rigtig standardprinter.</summary>
    public bool Virtual { get; set; }
    /// <summary>Lagt på af Fleetdeck (sættes af agenten).</summary>
    public bool Fleetdeck { get; set; }
}

/// <summary>Status for en ønsket printer på maskinen (sendes med helbredet).</summary>
public sealed class PrinterState
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Ok { get; set; }
    public bool IsDefault { get; set; }
    /// <summary>Fandtes i forvejen med samme navn, men er ikke lagt på af Fleetdeck (røres ikke).</summary>
    public bool Foreign { get; set; }
    /// <summary>Hold på er slået fra, og printeren er fjernet på maskinen.</summary>
    public bool Dropped { get; set; }
    public string? Error { get; set; }
    public DateTime? AddedAt { get; set; }
    /// <summary>Sidst lagt på igen, fordi den var fjernet (UTC).</summary>
    public DateTime? ReaddedAt { get; set; }
}

/// <summary>Det agenten husker om en printer, den selv har lagt på. Kun de må ændres og fjernes igen.</summary>
public sealed class OwnedPrinter
{
    public string Name { get; set; } = "";
    public string Uri { get; set; } = "";
    public string? Driver { get; set; }
    public string Settings { get; set; } = "";
    public DateTime? AddedAt { get; set; }
    public DateTime? ReaddedAt { get; set; }
    /// <summary>Hold på er slået fra, og brugeren har fjernet printeren: lægges ikke på igen.</summary>
    public bool Dropped { get; set; }
}

public enum PrinterOp { Add, Readd, Replace, Configure, Remove, Forget, SetDefault }

/// <summary>Ét skridt i planen. OldName: navnet på maskinen, der skal fjernes først (omdøbt eller ny adresse).</summary>
public sealed record PrinterStep(PrinterOp Op, string Key, string LocalName, PrinterSpec? Spec, string? OldName = null);

/// <summary>Resultat af ét skridt fra scriptet ("::p nøgle ok" / "::p nøgle err besked" / "::p nøgle:d skip").
/// Skipped: standardprinter ikke sat, fordi brugeren allerede har en anden rigtig standardprinter.</summary>
public sealed record PrinterResult(bool Ok, string? Error, bool Skipped = false);

public static partial class PrinterNames
{
    [GeneratedRegex("^p[0-9]{1,9}$")] private static partial Regex KeyRx();
    [GeneratedRegex(@"PDF|XPS|OneNote|\bFax\b|Send To|Snagit|Adobe PDF", RegexOptions.IgnoreCase)] private static partial Regex VirtualName();
    [GeneratedRegex(@"^(PORTPROMPT:|nul:|SHRFAX:|FILE:|XPSPort:|Microsoft\.Office\.OneNote)", RegexOptions.IgnoreCase)] private static partial Regex VirtualPort();

    public static bool ValidKey(string? k) => k is not null && KeyRx().IsMatch(k);

    /// <summary>Virtuelle printere (PDF, XPS, OneNote, fax) – tæller ikke som rigtig standardprinter.</summary>
    public static bool IsVirtual(string name, string? port) =>
        VirtualName().IsMatch(name) || (port is { Length: > 0 } p && VirtualPort().IsMatch(p)) || (port ?? "").StartsWith("cups-pdf:", StringComparison.OrdinalIgnoreCase);

    /// <summary>Navnet på maskinen. CUPS: kun bogstaver, tal, - og _ (æøå skrives om); Windows: navnet uden \ , og styretegn.</summary>
    public static string Local(string name, bool cups)
    {
        var n = Clean(name);
        if (!cups)
        {
            n = new string(n.Select(c => c is '\\' or ',' or '!' ? '-' : c).ToArray()).Trim();
            return n.Length == 0 ? "Printer" : n.Length > 200 ? n[..200] : n;
        }
        var sb = new StringBuilder();
        foreach (var c in n)
            sb.Append(c switch
            {
                'æ' => "ae", 'Æ' => "Ae", 'ø' => "oe", 'Ø' => "Oe", 'å' => "aa", 'Å' => "Aa", 'ü' => "u", 'ö' => "o", 'ä' => "a",
                _ when char.IsAsciiLetterOrDigit(c) || c is '-' or '_' => c.ToString(),
                _ => "_",
            });
        var q = Regex.Replace(sb.ToString(), "_+", "_").Trim('_');
        return q.Length == 0 ? "Printer" : q.Length > 100 ? q[..100] : q;
    }

    /// <summary>Uden linjeskift og andre styretegn (de må aldrig nå en kommandolinje).</summary>
    public static string Clean(string? s) => new string((s ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
}

public static class PrinterUri
{
    private static readonly string[] Schemes = ["ipp", "ipps", "http", "https", "socket", "lpd"];

    /// <summary>Normaliseret adresse (skema og vært med små bogstaver, ingen / til sidst), eller null hvis den ikke kan bruges.</summary>
    public static string? Normalize(string? uri)
    {
        var s = PrinterNames.Clean(uri);
        if (s.Length is 0 or > 400 || s.Any(char.IsWhiteSpace) || s.IndexOfAny(['\'', '"', '`', '$', '\\', ';', '|', '&', '<', '>']) >= 0) return null;
        if (!System.Uri.TryCreate(s, UriKind.Absolute, out var u) || !Schemes.Contains(u.Scheme.ToLowerInvariant()) || u.Host.Length == 0) return null;
        if (u.UserInfo.Length > 0) return null;   // aldrig login i adressen
        var scheme = u.Scheme.ToLowerInvariant();
        var port = u.IsDefaultPort || u.Port < 0 || (scheme is "ipp" or "ipps" && u.Port == 631) ? "" : $":{u.Port}";
        var path = u.AbsolutePath == "/" ? "" : u.AbsolutePath.TrimEnd('/');
        var host = u.HostNameType == UriHostNameType.IPv6 ? $"[{u.IdnHost}]" : u.Host.ToLowerInvariant();
        return $"{scheme}://{host}{port}{path}{u.Query}";
    }

    public static string? Host(string? uri) =>
        System.Uri.TryCreate(uri, UriKind.Absolute, out var u) && u.Host.Length > 0 ? u.Host.ToLowerInvariant() : null;

    /// <summary>IPP-adresse (ipp, ipps eller http(s) til en IPP-sti): virker uden driver, og status kan læses.</summary>
    public static bool IsIpp(string? uri) => uri is not null && new[] { "ipp://", "ipps://", "http://", "https://" }.Any(x => uri.StartsWith(x, StringComparison.OrdinalIgnoreCase));

    /// <summary>HTTP-adressen til en IPP-forespørgsel: ipp → http og ipps → https, port 631 hvis ingen er angivet.</summary>
    public static string? ToHttp(string? uri)
    {
        if (Normalize(uri) is not { } n || !System.Uri.TryCreate(n, UriKind.Absolute, out var u)) return null;
        var scheme = u.Scheme switch { "ipp" => "http", "ipps" => "https", "http" or "https" => u.Scheme, _ => null };
        if (scheme is null) return null;
        var port = u.Port > 0 ? u.Port : 631;
        var host = u.HostNameType == UriHostNameType.IPv6 ? $"[{u.IdnHost}]" : u.Host;
        return $"{scheme}://{host}:{port}{(u.AbsolutePath.Length > 0 ? u.AbsolutePath : "/")}";
    }
}

/// <summary>Hvad agenten skal gøre: sammenlign ønsket med det, maskinen har, og det Fleetdeck selv har lagt på.</summary>
public static class PrinterPlan
{
    public static string Settings(PrinterSpec s) => $"{s.Paper}|{s.Duplex}|{(s.Color ? "color" : "mono")}";

    public static bool Valid(PrinterSpec s) => PrinterNames.ValidKey(s.Key) && PrinterNames.Clean(s.Name).Length > 0 && PrinterUri.Normalize(s.Uri) is not null;

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static bool SameUri(string a, string b) => Same(PrinterUri.Normalize(a) ?? a, PrinterUri.Normalize(b) ?? b);

    public static List<PrinterStep> Make(IReadOnlyList<PrinterSpec> want, IReadOnlyList<LocalPrinter> present, IReadOnlyDictionary<string, OwnedPrinter> owned, bool cups)
    {
        var here = present.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var steps = new List<PrinterStep>();
        var wanted = want.Where(Valid).GroupBy(s => s.Key).Select(g => g.First()).ToList();
        var localNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in wanted)
        {
            var local = PrinterNames.Local(s.Name, cups);
            if (!localNames.Add(local)) continue;                  // to printere med samme navn: kun den første
            owned.TryGetValue(s.Key, out var o);
            if (o is not null && !Same(o.Name, local))
            {
                // omdøbt: fjern den gamle (hvis den er der) og læg den nye på
                steps.Add(new(PrinterOp.Replace, s.Key, local, s, here.Contains(o.Name) ? o.Name : null));
                continue;
            }
            if (here.Contains(local))
            {
                if (o is null) continue;                            // ikke lagt på af Fleetdeck: røres aldrig
                if (!SameUri(o.Uri, s.Uri) || (o.Driver ?? "") != (s.Driver ?? "")) steps.Add(new(PrinterOp.Replace, s.Key, local, s, local));
                else if (o.Settings != Settings(s) || o.Dropped) steps.Add(new(PrinterOp.Configure, s.Key, local, s));
                continue;
            }
            if (o is null) steps.Add(new(PrinterOp.Add, s.Key, local, s));
            else if (!s.Keep) { if (!o.Dropped) steps.Add(new(PrinterOp.Forget, s.Key, local, s)); }   // fjernet af brugeren: lad den være
            else steps.Add(new(PrinterOp.Readd, s.Key, local, s));
        }
        var keys = wanted.Select(s => s.Key).ToHashSet();
        foreach (var (k, o) in owned.Where(x => !keys.Contains(x.Key)).OrderBy(x => x.Key, StringComparer.Ordinal))
            steps.Add(here.Contains(o.Name) ? new(PrinterOp.Remove, k, o.Name, null) : new(PrinterOp.Forget, k, o.Name, null));

        // standardprinter: kun hvis maskinen ikke har en rigtig (PDF/XPS tæller ikke) – og kun en printer, der er (eller bliver) lagt på
        if (!present.Any(p => p.IsDefault && !p.Virtual))
        {
            var removed = steps.Where(x => x.Op is PrinterOp.Remove).Select(x => x.LocalName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var skip = steps.Where(x => x.Op is PrinterOp.Forget).Select(x => x.Key).ToHashSet();
            var pick = wanted.FirstOrDefault(s => s.MakeDefault && !skip.Contains(s.Key) && !removed.Contains(PrinterNames.Local(s.Name, cups))
                && (steps.Any(x => x.Key == s.Key && x.Op is PrinterOp.Add or PrinterOp.Readd or PrinterOp.Replace)
                    || (here.Contains(PrinterNames.Local(s.Name, cups)) && owned.ContainsKey(s.Key))));
            if (pick is not null) steps.Add(new(PrinterOp.SetDefault, pick.Key, PrinterNames.Local(pick.Name, cups), pick));
        }
        return steps;
    }

    /// <summary>Det agenten husker bagefter: kun vellykkede skridt ændrer noget.</summary>
    public static Dictionary<string, OwnedPrinter> Commit(IReadOnlyDictionary<string, OwnedPrinter> owned, IEnumerable<PrinterStep> steps,
        IReadOnlyDictionary<string, PrinterResult> results, DateTime nowUtc)
    {
        var next = owned.ToDictionary(x => x.Key, x => x.Value);
        foreach (var st in steps)
        {
            var ok = results.TryGetValue(StepId(st), out var r) && r.Ok;
            next.TryGetValue(st.Key, out var o);
            switch (st.Op)
            {
                case PrinterOp.Add or PrinterOp.Readd or PrinterOp.Replace when ok && st.Spec is { } s:
                    next[st.Key] = new OwnedPrinter
                    {
                        Name = st.LocalName, Uri = PrinterUri.Normalize(s.Uri) ?? s.Uri, Driver = s.Driver, Settings = Settings(s),
                        AddedAt = o?.AddedAt ?? nowUtc, ReaddedAt = st.Op == PrinterOp.Readd ? nowUtc : o?.ReaddedAt,
                    };
                    break;
                case PrinterOp.Configure when ok && st.Spec is { } s && o is not null:
                    o.Settings = Settings(s); o.Dropped = false;
                    break;
                case PrinterOp.Remove when ok:
                    next.Remove(st.Key);
                    break;
                case PrinterOp.Forget:
                    if (st.Spec is null) next.Remove(st.Key); else if (o is not null) o.Dropped = true;
                    break;
            }
        }
        return next;
    }

    /// <summary>Status pr. ønsket printer efter kørslen.</summary>
    public static List<PrinterState> Report(IReadOnlyList<PrinterSpec> want, IReadOnlyList<LocalPrinter> present, IReadOnlyDictionary<string, OwnedPrinter> owned,
        IReadOnlyDictionary<string, PrinterResult> results, bool cups)
    {
        var list = new List<PrinterState>();
        foreach (var s in want.GroupBy(x => x.Key).Select(g => g.First()))
        {
            var local = PrinterNames.Local(s.Name, cups);
            var p = present.FirstOrDefault(x => Same(x.Name, local));
            owned.TryGetValue(s.Key, out var o);
            var err = results.TryGetValue(s.Key, out var r) && !r.Ok ? r.Error : null;
            var defErr = results.TryGetValue(s.Key + ":d", out var rd) && !rd.Ok ? "standardprinter: " + rd.Error : null;
            list.Add(new PrinterState
            {
                Key = s.Key, Name = local, IsDefault = p?.IsDefault == true,
                Ok = Valid(s) && p is not null && err is null,
                Foreign = p is not null && o is null,
                Dropped = o?.Dropped == true && p is null,
                Error = !Valid(s) ? "ugyldig printer (navn eller adresse mangler)" : err ?? (p is null && o?.Dropped != true ? "mangler på maskinen" : defErr),
                AddedAt = o?.AddedAt, ReaddedAt = o?.ReaddedAt,
            });
        }
        return list;
    }

    /// <summary>Id i scriptets svar: nøglen, og ":d" for standardprinter (så en fejl her ikke tæller som manglende printer).</summary>
    public static string StepId(PrinterStep s) => s.Op == PrinterOp.SetDefault ? s.Key + ":d" : s.Key;
}

/// <summary>Kommandoer pr. platform. Alt fra serveren citeres; nøgler tjekkes mod ^p[0-9]+$.</summary>
public static class PrinterCommands
{
    public const string Marker = "::p ";

    /// <summary>PowerShell-streng i enkelte anførselstegn. PowerShell regner også ‘ ’ ‚ ‛ som anførselstegn, så de fordobles også.</summary>
    public static string Ps(string? s)
    {
        var sb = new StringBuilder("'");
        foreach (var c in PrinterNames.Clean(s)) { sb.Append(c); if (c is '\'' or '‘' or '’' or '‚' or '‛') sb.Append(c); }
        return sb.Append('\'').ToString();
    }

    /// <summary>sh-streng i enkelte anførselstegn.</summary>
    public static string Sh(string? s) => "'" + PrinterNames.Clean(s).Replace("'", "'\\''") + "'";

    // ---------------- Windows ----------------

    /// <summary>Alle printere som JSON (navn, port, driver, adresse, standard).</summary>
    public const string WindowsList = """
        $ErrorActionPreference = 'SilentlyContinue'
        $def = (Get-CimInstance Win32_Printer -Filter 'Default=TRUE' | Select-Object -First 1).Name
        $ports = @{}; foreach ($p in @(Get-PrinterPort)) { $ports[$p.Name] = $p }
        $rows = @(foreach ($x in @(Get-Printer)) {
          $p = $ports[$x.PortName]; $uri = $null
          if ($p -and $p.PrinterHostAddress) { if ($p.LprQueueName) { $uri = 'lpd://' + $p.PrinterHostAddress + '/' + $p.LprQueueName } else { $uri = 'socket://' + $p.PrinterHostAddress + ':' + $p.PortNumber } }
          elseif ($x.PortName -match '^(https?|ipps?)://') { $uri = $x.PortName }
          [pscustomobject]@{ name = $x.Name; port = $x.PortName; driver = $x.DriverName; uri = $uri; isDefault = ($x.Name -eq $def) }
        })
        ConvertTo-Json -InputObject $rows -Compress
        """;

    public static string WindowsPaper(string paper) => paper is "A3" or "A5" or "Letter" or "Legal" ? paper : "A4";
    public static string WindowsDuplex(string d) => d switch { PrinterDuplex.None => "OneSided", PrinterDuplex.Short => "TwoSidedShortEdge", _ => "TwoSidedLongEdge" };

    /// <summary>Scriptet, der kører som SYSTEM: læg på, ret, fjern. Hvert skridt melder "::p nøgle ok|err …".</summary>
    public static string Windows(IEnumerable<PrinterStep> steps)
    {
        var sb = new StringBuilder("""
            $ErrorActionPreference = 'Stop'
            function R([string]$k, [bool]$ok, [string]$m) { if ($ok) { Write-Output ('::p ' + $k + ' ok') } else { Write-Output ('::p ' + $k + ' err ' + ($m -replace '\s+', ' ')) } }
            function Conf([string]$n, [string]$paper, [string]$duplex, [bool]$color) {
              try { Set-PrintConfiguration -PrinterName $n -PaperSize $paper } catch { }
              try { Set-PrintConfiguration -PrinterName $n -DuplexingMode $duplex } catch { }
              try { Set-PrintConfiguration -PrinterName $n -Color $color } catch { }
            }
            function Put([string]$n, [string]$u, [string]$hostaddr, [string]$driver, [string]$loc) {
              if ($driver) {
                if (-not (Get-PrinterDriver -Name $driver -ErrorAction SilentlyContinue)) {
                  try { Add-PrinterDriver -Name $driver } catch { throw ('driveren "' + $driver + '" mangler på maskinen') }
                }
                $port = 'IP_' + $hostaddr
                if (-not (Get-PrinterPort -Name $port -ErrorAction SilentlyContinue)) { Add-PrinterPort -Name $port -PrinterHostAddress $hostaddr }
                Add-Printer -Name $n -DriverName $driver -PortName $port -Location $loc
              } elseif ((Get-Command Add-Printer).Parameters.ContainsKey('IppURL')) {
                Add-Printer -Name $n -IppURL $u
                if ($loc) { try { Set-Printer -Name $n -Location $loc } catch { } }
              } else {
                throw 'denne Windows-version kan ikke lægge en printer på uden driver (IPP) – markér printeren "Kræver driver"'
              }
            }

            """);
        foreach (var st in steps.Where(x => PrinterNames.ValidKey(x.Key) && x.Op is not PrinterOp.Forget and not PrinterOp.SetDefault))
        {
            var k = Ps(st.Key);
            var n = Ps(st.LocalName);
            sb.Append("try {\n");
            if (st.Op is PrinterOp.Remove || (st.Op is PrinterOp.Replace && st.OldName is not null))
                sb.Append($"  Remove-Printer -Name {Ps(st.Op is PrinterOp.Remove ? st.LocalName : st.OldName)} -ErrorAction SilentlyContinue\n");
            if (st.Spec is { } s && st.Op is not PrinterOp.Remove)
            {
                if (st.Op is not PrinterOp.Configure)
                {
                    var uri = PrinterUri.Normalize(s.Uri) ?? "";
                    sb.Append($"  Put {n} {Ps(uri)} {Ps(PrinterUri.Host(uri) ?? "")} {Ps(s.Driver ?? "")} {Ps(s.Location)}\n");
                }
                sb.Append($"  Conf {n} {Ps(WindowsPaper(s.Paper))} {Ps(WindowsDuplex(s.Duplex))} {(s.Color ? "$true" : "$false")}\n");
            }
            sb.Append($"  R {k} $true ''\n}} catch {{ R {k} $false $_.Exception.Message }}\n");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Standardprinter i brugerens session (standardprinteren er pr. bruger på Windows): kun hvis brugeren ikke allerede har
    /// en rigtig standardprinter. Slår "Lad Windows styre min standardprinter" fra, ellers skifter Windows den selv igen.
    /// </summary>
    public static string WindowsDefault(PrinterStep st) => $$"""
        $ErrorActionPreference = 'Stop'
        try {
          $d = Get-CimInstance Win32_Printer -Filter 'Default=TRUE' | Select-Object -First 1
          if ($d -and $d.Name -eq {{Ps(st.LocalName)}}) { Write-Output ('::p ' + {{Ps(st.Key + ":d")}} + ' ok'); return }
          if ($d -and $d.Name -notmatch 'PDF|XPS|OneNote|Fax' -and $d.PortName -notmatch '^(PORTPROMPT:|nul:|SHRFAX:|FILE:)') { Write-Output ('::p ' + {{Ps(st.Key + ":d")}} + ' skip'); return }
          Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows NT\CurrentVersion\Windows' -Name LegacyDefaultPrinterMode -Value 1 -Type DWord
          $p = Get-CimInstance Win32_Printer | Where-Object { $_.Name -eq {{Ps(st.LocalName)}} } | Select-Object -First 1
          if (-not $p) { throw 'printeren findes ikke' }
          Invoke-CimMethod -InputObject $p -MethodName SetDefaultPrinter | Out-Null
          Write-Output ('::p ' + {{Ps(st.Key + ":d")}} + ' ok')
        } catch { Write-Output ('::p ' + {{Ps(st.Key + ":d")}} + ' err ' + ($_.Exception.Message -replace '\s+', ' ')) }
        """;

    // ---------------- CUPS (Linux og Mac) ----------------

    /// <summary>Alle køer med adresse og standard (sprog C, så svaret kan læses).</summary>
    public const string CupsList = "LC_ALL=C lpstat -v 2>/dev/null; echo '::default'; LC_ALL=C lpstat -d 2>/dev/null; true";

    public static string CupsDuplex(string d) => d switch { PrinterDuplex.None => "one-sided", PrinterDuplex.Short => "two-sided-short-edge", _ => "two-sided-long-edge" };

    public static string CupsOptions(PrinterSpec s) =>
        $"-o {Sh("media=" + PrinterPaper.Pwg(s.Paper))} -o {Sh("sides=" + CupsDuplex(s.Duplex))} -o {Sh("print-color-mode=" + (s.Color ? "color" : "monochrome"))}";

    /// <summary>Uden driver: -m everywhere. Med driver: -P /sti/fil.ppd eller -m model (fx drv:///…).</summary>
    public static string CupsModel(PrinterSpec s) =>
        s.Driver is not { Length: > 0 } d ? "-m everywhere"
        : d.StartsWith('/') && (d.EndsWith(".ppd", StringComparison.OrdinalIgnoreCase) || d.EndsWith(".ppd.gz", StringComparison.OrdinalIgnoreCase)) ? $"-P {Sh(d)}" : $"-m {Sh(d)}";

    /// <summary>Scriptet til CUPS. installCups: kommando, der lægger CUPS på, hvis lpadmin mangler (Linux), ellers null.</summary>
    public static string Cups(IEnumerable<PrinterStep> steps, string? installCups = null)
    {
        var list = steps.Where(x => PrinterNames.ValidKey(x.Key) && x.Op is not PrinterOp.Forget).ToList();
        var sb = new StringBuilder("export LC_ALL=C\n");
        sb.Append("r() { if [ \"$2\" = 0 ]; then echo \"::p $1 ok\"; else printf '::p %s err %s\\n' \"$1\" \"$(printf '%s' \"$3\" | tr '\\n' ' ')\"; fi; }\n");
        if (installCups is { Length: > 0 } && list.Any(x => x.Op is not PrinterOp.Remove))
            sb.Append($"if ! command -v lpadmin >/dev/null 2>&1; then echo 'CUPS mangler – lægger det på'; {installCups} >/dev/null 2>&1; systemctl enable --now cups >/dev/null 2>&1 || true; fi\n");
        foreach (var st in list)
        {
            var k = Sh(PrinterPlan.StepId(st));
            var q = Sh(st.LocalName);
            string cmd = st.Op switch
            {
                PrinterOp.Remove => $"lpadmin -x {q}",
                PrinterOp.SetDefault => $"lpadmin -d {q}",
                PrinterOp.Configure => $"lpadmin -p {q} {CupsOptions(st.Spec!)}",
                _ => (st.Op is PrinterOp.Replace && st.OldName is not null ? $"{{ lpadmin -x {Sh(st.OldName)} || true; }} && " : "")
                     + $"lpadmin -p {q} -E -v {Sh(PrinterUri.Normalize(st.Spec!.Uri) ?? "")} {CupsModel(st.Spec)} -D {Sh(st.Spec.Name)} -L {Sh(st.Spec.Location)} -o printer-is-shared=false {CupsOptions(st.Spec)}",
            };
            sb.Append($"out=$({cmd} 2>&1); r {k} $? \"$out\"\n");
        }
        return sb.ToString();
    }

    // ---------------- svar ----------------

    /// <summary>"::p nøgle ok" / "::p nøgle err besked" → resultat pr. skridt.</summary>
    public static Dictionary<string, PrinterResult> Results(string output)
    {
        var d = new Dictionary<string, PrinterResult>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith(Marker, StringComparison.Ordinal)) continue;
            var parts = line[Marker.Length..].Split(' ', 3);
            if (parts.Length < 2) continue;
            var key = parts[0];
            if (!PrinterNames.ValidKey(key.EndsWith(":d") ? key[..^2] : key)) continue;
            d[key] = parts[1] switch
            {
                "ok" => new(true, null),
                "skip" => new(true, null, Skipped: true),
                _ => new(false, parts.Length > 2 && parts[2].Trim().Length > 0 ? Short(parts[2].Trim()) : "ukendt fejl"),
            };
        }
        return d;
    }

    private static string Short(string s) => s.Length > 300 ? s[..300] + "…" : s;

    /// <summary>lpstat -v og lpstat -d (sprog C) → køer.</summary>
    public static List<LocalPrinter> ParseCups(string output)
    {
        var list = new List<LocalPrinter>();
        string? def = null;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("device for ", StringComparison.Ordinal))
            {
                var rest = line["device for ".Length..];
                var i = rest.IndexOf(": ", StringComparison.Ordinal);
                if (i <= 0) continue;
                var name = rest[..i].Trim();
                var uri = rest[(i + 2)..].Trim();
                list.Add(new LocalPrinter { Name = name, Uri = uri, Virtual = PrinterNames.IsVirtual(name, uri) });
            }
            else if (line.StartsWith("system default destination: ", StringComparison.Ordinal))
                def = line["system default destination: ".Length..].Trim();
        }
        foreach (var p in list) p.IsDefault = def is not null && p.Name == def;
        return list;
    }

    private sealed class WinRow
    {
        public string? Name { get; set; }
        public string? Port { get; set; }
        public string? Driver { get; set; }
        public string? Uri { get; set; }
        public bool IsDefault { get; set; }
    }

    /// <summary>JSON fra WindowsList (et objekt eller en liste) → printere.</summary>
    public static List<LocalPrinter> ParseWindows(string output)
    {
        var json = output.Trim();
        var start = json.IndexOfAny(['[', '{']);
        if (start < 0) return [];
        json = json[start..];
        var opt = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        List<WinRow> rows;
        try { rows = json.StartsWith('[') ? JsonSerializer.Deserialize<List<WinRow>>(json, opt) ?? [] : [JsonSerializer.Deserialize<WinRow>(json, opt)!]; }
        catch (JsonException) { return []; }
        return rows.Where(r => r?.Name is { Length: > 0 }).Select(r => new LocalPrinter
        {
            Name = r.Name!, Uri = PrinterUri.Normalize(r.Uri) ?? (r.Uri is { Length: > 0 } ? r.Uri : null), Driver = r.Driver, IsDefault = r.IsDefault,
            Virtual = PrinterNames.IsVirtual(r.Name!, r.Port),
        }).ToList();
    }
}
