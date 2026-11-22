using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Fleetdeck.Shared;

/// <summary>Hvad et usb_ipad-job (iPad i kabel på en pc med udvidelsen "iPad via USB") kan gøre.</summary>
public static class UsbIpadActions
{
    public const string Info = "info", InstallProfile = "install_profile", Backup = "backup", Restore = "restore",
        Supervise = "supervise", Erase = "erase",
        /// <summary>Fjern den låste profil (og den gamle DNS-profil) – "Løsn styring".</summary>
        RemoveProfile = "remove_profile",
        /// <summary>"Frigiv helt" (afprøves): fjern profilen → krypteret backup (også til Fleetdeck) → slet → gendan backuppen uden overvågning.</summary>
        Release = "release",
        /// <summary>"Overvåg uden datatab" (<see cref="NoWipeFlow"/>): slet → overvåg → gendan uden systemfiler (eller overvåget uden gendannelse).</summary>
        SuperviseNoWipe = "supervise_nowipe";
    public static readonly string[] All = [Info, InstallProfile, Backup, Restore, Supervise, Erase, RemoveProfile, Release, SuperviseNoWipe];
    /// <summary>Kun via "Overvåg uden datatab" (Opsætning).</summary>
    public static readonly string[] NoWipeOnly = [SuperviseNoWipe];
    /// <summary>Kun via "Frigiv iPad" (Opsætning), ikke som almindelige USB-handlinger.</summary>
    public static readonly string[] ReleaseOnly = [RemoveProfile, Release];
    /// <summary>Afprøves: kræver, at navnet på iPad'en skrives som bekræftelse.</summary>
    public static readonly string[] Experimental = [Supervise, Erase];
}

/// <summary>Hemmeligheder, agenten kan hente én gang til et kørende job (med jobbets engangsbillet). Står aldrig i jobbets parametre eller output.</summary>
public static class UsbIpadSecretKinds
{
    public const string BackupPassword = "backup_password", Keybag = "keybag";
}

/// <summary>Hvad agenten melder hvert ~minut (og straks ved ændringer), når udvidelsen "iPad via USB" er installeret.</summary>
public sealed class UsbIpadReport
{
    /// <summary>pymobiledevice3 er installeret og kan køres.</summary>
    public bool Ready { get; set; }
    /// <summary>Hvorfor iPads ikke kan ses (fx Apples USB-tjeneste kører ikke). null = alt er i orden.</summary>
    public string? Problem { get; set; }
    public List<IosDeviceInfo> Devices { get; set; } = [];
}

public sealed class UsbIpadSecretRequest
{
    public int JobId { get; set; }
    public string Ticket { get; set; } = "";
    public string Kind { get; set; } = "";
}

public sealed class UsbIpadSecretResponse
{
    public string Value { get; set; } = "";
}

/// <summary>Hvor udvidelsen lægger pymobiledevice3 (en privat uv-installation med egen Python) på hver platform.</summary>
public static class UsbIpadPaths
{
    /// <summary>Den afprøvede version. Ændres kun sammen med en test på rigtig hardware.</summary>
    public const string Pmd3Version = "11.58.0";

    public static string Root => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Fleetdeck", "ipad-usb")
        : OperatingSystem.IsMacOS() ? "/usr/local/fleetdeck/ipad-usb" : "/opt/fleetdeck/ipad-usb";

    /// <summary>Python i pymobiledevice3's eget miljø (uv tool install).</summary>
    public static string Python(string root, bool windows) => windows
        ? root + @"\tools\pymobiledevice3\Scripts\python.exe"
        : root + "/tools/pymobiledevice3/bin/python";

    public static string CurrentPython => Python(Root, OperatingSystem.IsWindows());
    public static string Work => Path.Combine(Root, "work");
}

/// <summary>
/// Kommandoerne til pymobiledevice3. De køres altid med Python fra udvidelsens eget miljø gennem en lille indpakning,
/// der læser hemmeligheder (backup-kode) fra stdin og sætter dem ind i stedet for pladsholderne – så de aldrig står på
/// kommandolinjen (ps), i jobbets parametre eller i outputtet. iPad'en vælges med miljøvariablen PYMOBILEDEVICE3_UDID.
/// </summary>
public static class Pmd3
{
    public const string Secret1 = "@FD_SECRET1@", Secret2 = "@FD_SECRET2@";
    public const string UdidEnv = "PYMOBILEDEVICE3_UDID";

    /// <summary>Indpakningen (python -I -c …): pladsholderne erstattes med linjer fra stdin, og pymobiledevice3's main() køres i samme proces.</summary>
    public const string Wrapper =
        "import sys;a=sys.argv[1:];k=('" + Secret1 + "','" + Secret2 + "');" +
        @"s=[sys.stdin.readline().rstrip('\r\n') for _ in k] if any(x in k for x in a) else ['',''];" +
        "sys.argv=['pymobiledevice3']+[s[k.index(x)] if x in k else x for x in a];" +
        "from pymobiledevice3.__main__ import main;sys.exit(main())";

    /// <summary>Argumenterne til Python for én pymobiledevice3-kommando.</summary>
    public static List<string> Argv(IEnumerable<string> cmd) => ["-I", "-c", Wrapper, "--no-color", .. cmd];

    /// <summary>Miljøet: ingen farver, ingen "pæne" tracebacks med lokale variabler (kunne vise en kode), UTF-8 og den valgte iPad.</summary>
    public static Dictionary<string, string> Env(string? udid)
    {
        var e = new Dictionary<string, string>
        {
            ["NO_COLOR"] = "1", ["TERM"] = "dumb", ["_TYPER_STANDARD_TRACEBACK"] = "1", ["PYTHONIOENCODING"] = "utf-8", ["PYTHONUTF8"] = "1",
        };
        if (udid is { Length: > 0 }) e[UdidEnv] = udid;
        return e;
    }

    // ------------------------------------------------------------------ kommandoer
    public static string[] UsbList() => ["usbmux", "list", "--usb"];
    public static string[] Info() => ["lockdown", "info"];
    public static string[] Battery() => ["diagnostics", "battery", "single"];
    public static string[] CloudConfiguration() => ["profile", "cloud-configuration"];
    public static string[] ProfileList() => ["profile", "list"];
    public static string[] InstallProfile(string file, string? keybag) =>
        keybag is { Length: > 0 } kb ? ["profile", "install", "--keybag", kb, file] : ["profile", "install", file];
    public static string[] RemoveProfile(string identifier) => ["profile", "remove", identifier];
    public static string[] EncryptionState() => ["backup2", "encryption"];
    /// <summary>Slår krypteret backup til med koden fra stdin (første linje).</summary>
    public static string[] EncryptionOn() => ["backup2", "encryption", "on", Secret1];
    /// <summary>Skift backup-koden fra den første til den anden linje på stdin. Med samme kode to gange: tjekker, at koden er den, iPad'en krypterer med.</summary>
    public static string[] ChangePassword() => ["backup2", "change-password", Secret1, Secret2];
    public static string[] Backup(string dir) => ["backup2", "backup", "--full", dir];
    /// <summary>Gendan fra <paramref name="dir"/>/&lt;kilde-UDID&gt; med koden fra stdin.</summary>
    public static string[] Restore(string dir, string sourceUdid) => ["backup2", "restore", "--password", Secret1, "--source", sourceUdid, dir];
    public static string[] Supervise(string organization, string keybag) => ["profile", "supervise", organization, "--keybag", keybag];
    public static string[] PairSupervised(string keybag) => ["lockdown", "pair-supervised", keybag];
    /// <summary>Almindelig parring (efter en sletning står iPad'en i opsætningen og skal parres igen, før en backup kan lægges tilbage).</summary>
    public static string[] Pair() => ["lockdown", "pair"];
    public static string[] Activate() => ["activation", "activate"];
    public static string[] Erase() => ["profile", "erase-device"];

    /// <summary>Erstat hemmeligheder med *** (til sidst, hvis pymobiledevice3 skulle skrive dem).</summary>
    public static string Mask(string text, IEnumerable<string>? secrets)
    {
        foreach (var s in secrets ?? [])
            if (s.Length >= 4) text = text.Replace(s, "***", StringComparison.Ordinal);
        return text;
    }
}

/// <summary>Læser pymobiledevice3's JSON-svar (print_json). Alt læses forsigtigt: felter kan mangle, og der kan stå logning omkring JSON'en.</summary>
public static partial class Pmd3Parse
{
    /// <summary>JSON-værdien i teksten (objekt eller liste), også med logning før/efter.</summary>
    public static JsonElement? Json(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();
        if (TryParse(t) is { } whole) return whole;
        foreach (var (open, close) in new[] { ('[', ']'), ('{', '}') })
        {
            var a = t.IndexOf(open); var b = t.LastIndexOf(close);
            if (a >= 0 && b > a && TryParse(t[a..(b + 1)]) is { } x) return x;
        }
        return null;
    }

    private static JsonElement? TryParse(string s)
    {
        try
        {
            using var d = JsonDocument.Parse(s);
            return d.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array ? d.RootElement.Clone() : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Nye UDID'er (A12 og nyere) er "CPID-ECID": 8 hex-cifre, bindestreg, 16 hex-cifre. Så kendes ECID'en uden parring.</summary>
    public static string EcidFromUdid(string? udid) =>
        udid is { } u && NewUdid().Match(u.Trim()) is { Success: true } m ? Cfgutil.NormEcid(m.Groups[1].Value) : "";

    [GeneratedRegex("^[0-9A-Fa-f]{8}-([0-9A-Fa-f]{16})$")] private static partial Regex NewUdid();

    /// <summary>"usbmux list --usb": de tilsluttede iPads/iPhones (kun det, der kan læses uden parring).</summary>
    public static List<IosDeviceInfo> UsbList(JsonElement root)
    {
        var list = new List<IosDeviceInfo>();
        if (root.ValueKind != JsonValueKind.Array) return list;
        foreach (var d in root.EnumerateArray())
        {
            if (d.ValueKind == JsonValueKind.String && d.GetString() is { Length: > 0 } simple)
            {
                list.Add(new IosDeviceInfo { Udid = simple, Ecid = EcidFromUdid(simple) });
                continue;
            }
            if (d.ValueKind != JsonValueKind.Object) continue;
            if (Str(d, "ConnectionType") is { } ct && !ct.Equals("USB", StringComparison.OrdinalIgnoreCase)) continue;
            var udid = Str(d, "UniqueDeviceID") ?? Str(d, "Identifier");
            if (string.IsNullOrWhiteSpace(udid) || list.Any(x => x.Udid == udid)) continue;
            list.Add(new IosDeviceInfo
            {
                Udid = udid, Ecid = EcidFromUdid(udid), Name = Str(d, "DeviceName"), DeviceType = Str(d, "ProductType"),
                DeviceClass = Str(d, "DeviceClass"), Firmware = Str(d, "ProductVersion"), Build = Str(d, "BuildVersion"),
            });
        }
        return list;
    }

    /// <summary>"lockdown info": alle oplysninger (kræver, at iPad'en er parret – "Stol på").</summary>
    public static IosDeviceInfo Info(JsonElement d)
    {
        var udid = Str(d, "UniqueDeviceID");
        var ecid = d.ValueKind == JsonValueKind.Object && d.TryGetProperty("UniqueChipID", out var c) ? c.ValueKind switch
        {
            JsonValueKind.Number when c.TryGetUInt64(out var n) => "0x" + n.ToString("X", CultureInfo.InvariantCulture),
            JsonValueKind.String => Cfgutil.NormEcid(c.GetString()),
            _ => "",
        } : "";
        if (ecid.Length == 0) ecid = EcidFromUdid(udid);
        return new IosDeviceInfo
        {
            Ecid = ecid, Udid = udid, Name = Str(d, "DeviceName"), DeviceType = Str(d, "ProductType"), DeviceClass = Str(d, "DeviceClass"),
            Serial = Str(d, "SerialNumber"), Firmware = Str(d, "ProductVersion"), Build = Str(d, "BuildVersion"),
            ActivationState = Str(d, "ActivationState"), Passcode = Bool(d, "PasswordProtected"), Paired = true, BootedState = "Booted",
        };
    }

    /// <summary>"diagnostics battery single" (IORegistry) – eller lockdown-domænet com.apple.mobile.battery.</summary>
    public static (int? Percent, bool? Charging) Battery(JsonElement d)
    {
        int? pct = Int(d, "BatteryCurrentCapacity");
        if (pct is null && Int(d, "CurrentCapacity") is int cur)
        {
            var max = Int(d, "MaxCapacity");
            pct = max is > 100 ? (int)Math.Round(cur * 100.0 / max.Value) : cur;   // nogle modeller melder mAh
        }
        if (pct is int p) pct = Math.Clamp(p, 0, 100);
        return (pct, Bool(d, "BatteryIsCharging") ?? Bool(d, "IsCharging"));
    }

    /// <summary>"profile cloud-configuration": er iPad'en overvåget, og af hvem.</summary>
    public static (bool? Supervised, string? Organization) Cloud(JsonElement d) => (Bool(d, "IsSupervised"), Str(d, "OrganizationName"));

    /// <summary>"profile list": de installerede konfigurationsprofiler.</summary>
    public static List<IosProfileInfo> Profiles(JsonElement d)
    {
        var list = new List<IosProfileInfo>();
        if (d.ValueKind != JsonValueKind.Object) return list;
        if (d.TryGetProperty("ProfileMetadata", out var meta) && meta.ValueKind == JsonValueKind.Object)
            foreach (var p in meta.EnumerateObject())
                list.Add(new IosProfileInfo { Identifier = p.Name, Name = Str(p.Value, "PayloadDisplayName"), Version = Int(p.Value, "PayloadVersion") });
        if (d.TryGetProperty("OrderedIdentifiers", out var ids) && ids.ValueKind == JsonValueKind.Array)
            foreach (var id in ids.EnumerateArray())
                if (id.GetString() is { Length: > 0 } s && list.All(x => x.Identifier != s)) list.Add(new IosProfileInfo { Identifier = s });
        return list;
    }

    /// <summary>"backup2 encryption" uden argumenter skriver "on" eller "off".</summary>
    public static bool? EncryptionOn(string? output)
    {
        foreach (var line in Enumerable.Reverse((output ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
        {
            if (line.Equals("on", StringComparison.OrdinalIgnoreCase)) return true;
            if (line.Equals("off", StringComparison.OrdinalIgnoreCase)) return false;
        }
        return null;
    }

    /// <summary>Fremdrift i procent fra en linje af backup2's statuslinje ("backup:  42%|████ …").</summary>
    public static int? Percent(string line) =>
        PercentRx().Match(line) is { Success: true } m && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? (int)Math.Clamp(Math.Floor(v), 0, 100) : null;

    [GeneratedRegex(@"(\d{1,3}(?:\.\d+)?)\s?%")] private static partial Regex PercentRx();

    internal static string? Str(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.GetRawText(),
            _ => null,
        } : null;

    private static int? Int(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) ? v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt64(out var n) => (int)Math.Clamp(n, int.MinValue, int.MaxValue),
            JsonValueKind.Number => (int)Math.Round(v.GetDouble()),
            JsonValueKind.String when int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
            _ => null,
        } : null;

    private static bool? Bool(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) ? v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => v.TryGetInt32(out var n) ? n != 0 : null,
            JsonValueKind.String => v.GetString()?.Trim().ToLowerInvariant() switch { "yes" or "true" or "1" => true, "no" or "false" or "0" => false, _ => null },
            _ => null,
        } : null;
}

/// <summary>Resultatet af én pymobiledevice3-kørsel (hemmeligheder er allerede skjult).</summary>
public sealed record Pmd3Result(int Exit, string Out, string Err, bool TimedOut, bool Cancelled)
{
    public bool Ok => Exit == 0 && !TimedOut && !Cancelled;
    public JsonElement? Json => Pmd3Parse.Json(Out);
    public string Message => TimedOut ? "tiden løb ud" : Cancelled ? "annulleret fra Fleetdeck"
        : LastLines(Err) is { Length: > 0 } e ? e : LastLines(Out) is { Length: > 0 } o ? o : $"pymobiledevice3 sluttede med kode {Exit}";
    private static string LastLines(string s) => string.Join(" ", s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(l => Pmd3Parse.Percent(l) is null).TakeLast(3));
}

/// <summary>Kører pymobiledevice3 uden skal, med tidsgrænse og annullering. Hemmeligheder sendes på stdin og skjules i outputtet.</summary>
public static class Pmd3Runner
{
    /// <summary>Hvor mange jobs der bruger iPads lige nu – så holder vagten pause (to forbindelser til samme iPad giver bøvl).</summary>
    private static int _busy;
    public static bool Busy => Volatile.Read(ref _busy) > 0;
    public static IDisposable Hold() { Interlocked.Increment(ref _busy); return new Release(); }
    private sealed class Release : IDisposable
    {
        private int _done;
        public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) Interlocked.Decrement(ref _busy); }
    }

    public static bool Installed => File.Exists(UsbIpadPaths.CurrentPython);

    public static async Task<Pmd3Result> Run(string? udid, string[] cmd, TimeSpan timeout, CancellationToken ct,
        IReadOnlyList<string>? secrets = null, Action<int>? progress = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = UsbIpadPaths.CurrentPython, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Directory.Exists(UsbIpadPaths.Root) ? UsbIpadPaths.Root : Path.GetTempPath(),
        };
        foreach (var a in Pmd3.Argv(cmd)) psi.ArgumentList.Add(a);
        foreach (var (k, v) in Pmd3.Env(udid)) psi.Environment[k] = v;
        if (udid is null) psi.Environment.Remove(Pmd3.UdidEnv);
        using var p = new Process { StartInfo = psi };
        try { p.Start(); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        { return new Pmd3Result(-1, "", $"pymobiledevice3 kunne ikke startes: {e.Message}", false, false); }
        try
        {
            if (secrets is { Count: > 0 })
            {
                foreach (var s in secrets) await p.StandardInput.WriteLineAsync(s);
                await p.StandardInput.FlushAsync(CancellationToken.None);
            }
            p.StandardInput.Close();
        }
        catch (IOException) { }
        var outTask = p.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var err = new StringBuilder();
        var errTask = Task.Run(async () =>
        {
            // statuslinjen skrives med \r: del selv, så fremdriften kan følges undervejs
            var buf = new char[4096]; var line = new StringBuilder(); var last = -1;
            int n;
            while ((n = await p.StandardError.ReadAsync(buf, CancellationToken.None)) > 0)
                for (var i = 0; i < n; i++)
                {
                    var ch = buf[i];
                    if (ch is '\r' or '\n')
                    {
                        if (line.Length == 0) continue;
                        var l = line.ToString(); line.Clear();
                        if (Pmd3Parse.Percent(l) is int pct) { if (pct != last) { last = pct; progress?.Invoke(pct); } continue; }
                        lock (err) err.AppendLine(l);
                    }
                    else line.Append(ch);
                }
            if (line.Length > 0) lock (err) err.AppendLine(line.ToString());
        });
        bool done = true, cancelled = false;
        using (var limit = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            limit.CancelAfter(timeout);
            try { await p.WaitForExitAsync(limit.Token); }
            catch (OperationCanceledException) { done = false; cancelled = ct.IsCancellationRequested; }
        }
        if (!done) { try { p.Kill(true); } catch (InvalidOperationException) { } }
        p.WaitForExit(5000);
        string so = "";
        try { so = await outTask.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None); } catch (TimeoutException) { }
        try { await errTask.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None); } catch (TimeoutException) { }
        string se; lock (err) se = err.ToString();
        return new Pmd3Result(done ? p.ExitCode : -1, Pmd3.Mask(so, secrets), Pmd3.Mask(se, secrets), !done && !cancelled, cancelled);
    }

    /// <summary>Tilsluttede iPads (USB). Error = hvorfor listen ikke kunne hentes.</summary>
    public static async Task<(List<IosDeviceInfo>? Devices, string? Error)> List(CancellationToken ct)
    {
        var r = await Run(null, Pmd3.UsbList(), TimeSpan.FromSeconds(30), ct);
        if (!r.Ok) return (null, ExplainList(r.Message));
        return (r.Json is { } j ? Pmd3Parse.UsbList(j) : [], null);
    }

    /// <summary>Alle oplysninger om én iPad: lockdown info, overvågning, batteri og profiler. Fejler parringen, bruges det, listen gav.</summary>
    public static async Task<IosDeviceInfo> Details(IosDeviceInfo basic, CancellationToken ct)
    {
        var udid = basic.Udid;
        var info = await Run(udid, Pmd3.Info(), TimeSpan.FromSeconds(60), ct);
        // fejler det, er iPad'en som regel ikke parret endnu (låst, eller "Stol på" er ikke trykket)
        if (!info.Ok || info.Json is not { ValueKind: JsonValueKind.Object } ij)
            return Cfgutil.Merge(basic, new IosDeviceInfo { Ecid = basic.Ecid, Paired = false });
        var d = Cfgutil.Merge(basic, Pmd3Parse.Info(ij));
        var cloud = await Run(udid, Pmd3.CloudConfiguration(), TimeSpan.FromSeconds(30), ct);
        if (cloud.Ok && cloud.Json is { ValueKind: JsonValueKind.Object } cj) d.Supervised = Pmd3Parse.Cloud(cj).Supervised ?? false;
        var bat = await Run(udid, Pmd3.Battery(), TimeSpan.FromSeconds(30), ct);
        if (bat.Ok && bat.Json is { ValueKind: JsonValueKind.Object } bj) { var (pct, ch) = Pmd3Parse.Battery(bj); d.Battery = pct ?? d.Battery; d.Charging = ch ?? d.Charging; }
        var prof = await Run(udid, Pmd3.ProfileList(), TimeSpan.FromSeconds(30), ct);
        if (prof.Ok && prof.Json is { ValueKind: JsonValueKind.Object } pj) d.Profiles = Pmd3Parse.Profiles(pj);
        return d;
    }

    /// <summary>Forklaring, når listen ikke kan hentes (typisk: Apples USB-tjeneste/usbmuxd mangler).</summary>
    public static string ExplainList(string msg)
    {
        var m = msg.ToLowerInvariant();
        if (m.Contains("usbmux") || m.Contains("connection refused") || m.Contains("no such file") || m.Contains("27015") || m.Contains("connectionfailed"))
            return OperatingSystem.IsWindows()
                ? "Apples USB-tjeneste (Apple Mobile Device Service) kører ikke. Installér \"Apple Devices\" fra Microsoft Store eller udvidelsen igen."
                : OperatingSystem.IsMacOS() ? "macOS' usbmuxd svarer ikke – genstart Mac'en."
                : "usbmuxd kører ikke. Installér udvidelsen igen (den installerer usbmuxd), og sæt kablet i igen.";
        return msg;
    }
}

/// <summary>
/// Vagten: mens udvidelsen er installeret, kigges der efter iPads i USB hvert 10. sekund (billigt: kun usbmuxd-listen).
/// En ny iPad læses grundigt (kræver "Stol på"), og alle oplysninger læses igen hvert 5. minut. Serveren får besked med det samme ved
/// ændringer og ellers hvert minut (så den ved, at udvidelsen virker).
/// </summary>
public sealed class UsbIpadWatcher(Func<UsbIpadReport, CancellationToken, Task> send, Action<string> log)
{
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(10), Heartbeat = TimeSpan.FromSeconds(60), Refresh = TimeSpan.FromMinutes(5),
        RetryUnpaired = TimeSpan.FromSeconds(30);

    private sealed record Seen(IosDeviceInfo Device, DateTime At);

    public async Task Run(CancellationToken stop)
    {
        var cache = new Dictionary<string, Seen>();
        string? lastKey = null; var lastSent = DateTime.MinValue; bool? wasReady = null;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                if (!Pmd3Runner.Installed)
                {
                    if (wasReady == true) await send(new UsbIpadReport { Ready = false, Problem = "pymobiledevice3 er ikke installeret" }, stop);
                    wasReady = false; cache.Clear(); lastKey = null;
                    await Task.Delay(TimeSpan.FromMinutes(1), stop);
                    continue;
                }
                wasReady = true;
                if (Pmd3Runner.Busy) { await Task.Delay(Poll, stop); continue; }   // et iPad-job kører

                var report = new UsbIpadReport { Ready = true };
                var (found, err) = await Pmd3Runner.List(stop);
                if (found is null) { report.Problem = err; cache.Clear(); }
                else
                {
                    foreach (var gone in cache.Keys.Where(k => found.All(f => f.Udid != k)).ToList()) cache.Remove(gone);
                    foreach (var f in found.Where(f => f.Udid is { Length: > 0 }))
                    {
                        var udid = f.Udid!;
                        var due = !cache.TryGetValue(udid, out var c)
                                  || DateTime.UtcNow - c.At > (c.Device.Paired == false ? RetryUnpaired : Refresh);
                        if (due && !Pmd3Runner.Busy)
                        {
                            var d = await Pmd3Runner.Details(f, stop);
                            if (c is null) log($"iPad via USB: {d.Name ?? udid} er sat i ({(d.Paired == false ? "ikke parret endnu" : "parret")})");
                            cache[udid] = new Seen(d, DateTime.UtcNow);
                        }
                        report.Devices.Add(cache.TryGetValue(udid, out var s) ? Cfgutil.Merge(s.Device, f) : f);
                    }
                }
                var key = Key(report);
                if (key != lastKey || DateTime.UtcNow - lastSent > Heartbeat)
                {
                    await send(report, stop);
                    lastKey = key; lastSent = DateTime.UtcNow;
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception e) { log($"iPad via USB: {e.Message}"); lastKey = null; }
            try { await Task.Delay(Poll, stop); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Det, der er værd at melde straks (batteriet kun i 10 %-trin).</summary>
    public static string Key(UsbIpadReport r) => $"{r.Ready}|{r.Problem}|" + string.Join(";", r.Devices.OrderBy(d => d.Udid, StringComparer.Ordinal)
        .Select(d => $"{d.Udid}/{d.Ecid}/{d.Name}/{d.Paired}/{d.Supervised}/{d.Firmware}/{(d.Battery ?? -10) / 10}/{d.Profiles?.Count}"));
}
