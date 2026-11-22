using System.Diagnostics;
using System.Text.Json;
using Fleetdeck.Shared;
using Microsoft.Win32;

namespace Fleetdeck.Agent.Windows;

/// <summary>
/// Den dybe opdateringsscanning: det, winget ikke ser. Hver kilde får sit eget præfiks i id'et, så serveren og siden kan
/// kende dem fra hinanden uden et nyt felt:
///   wu:&lt;UpdateID&gt;   Windows Update via Microsoft Update – også Office (MSI), andre Microsoft-programmer og drivere
///   office:c2r       Microsoft 365 / Office (Klik og kør), sammenlignet med Microsofts liste over den nyeste version pr. kanal
///   gpu:amd          AMD Software: Adrenalin Edition, sammenlignet med AMD's egen liste (installeres med AMD's program)
///   chipset:amd      AMD Chipset Software, nyeste version fra AMD's driversider (installeres i brugerens session, se AmdChipset)
/// Svarene huskes i 50 minutter, så inventaret ikke venter på Windows Update hver gang.
/// </summary>
public static class DeepUpdates
{
    public const string WuPrefix = "wu:", OfficeId = "office:c2r", AmdId = "gpu:amd";
    public const string AmdDownload = "https://www.amd.com/en/support/download/drivers.html";
    private const string OfficeFeed = "https://clients.config.office.net/releases/v1.0/OfficeReleases";
    private const string AmdFeed = "https://drivers.amd.com/drivers/installer/json/version.json";
    private static readonly string OfficeClient = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
        @"microsoft shared\ClickToRun\OfficeC2RClient.exe");
    internal static readonly HttpClient Http = MakeHttp();
    private static (DateTime At, List<UpgradeEntry> List)? _cache;

    private static HttpClient MakeHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Fleetdeck");
        h.DefaultRequestHeaders.Referrer = new Uri("https://www.amd.com/");   // AMD's server svarer kun med en henviser
        return h;
    }

    public static bool IsDeep(string id) => id.StartsWith(WuPrefix, StringComparison.Ordinal) || id is OfficeId or AmdId or AmdChipset.Id;

    /// <summary>Alle kilder. En kilde, der fejler, springes over (og logges) – de andre kommer stadig med.</summary>
    public static async Task<List<UpgradeEntry>> Collect(IReadOnlyList<SoftwareEntry> software, Action<string> log, CancellationToken ct, bool fresh = false)
    {
        if (!fresh && _cache is { } c && c.At > DateTime.UtcNow.AddMinutes(-50)) return c.List;
        var list = new List<UpgradeEntry>();
        try { list.AddRange(WindowsUpdate.PendingEntries()); }
        catch (Exception e) { log($"Windows Update (dyb scanning): {e.Message}"); }
        try { if (await Office(ct) is { } o) list.Add(o); }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested) { log($"Office: {e.Message}"); }
        try { if (await Amd(software, ct) is { } a) list.Add(a); }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested) { log($"AMD-driver: {e.Message}"); }
        try { if (await AmdChipset.Check(software, Http, ct) is { } chip) list.Add(chip); }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested) { log($"AMD chipset-driver: {e.Message}"); }
        _cache = (DateTime.UtcNow, list);
        return list;
    }

    /// <summary>Glem det huskede svar (efter en installation).</summary>
    public static void Forget() => _cache = null;

    // ---------------------------------------------------------------- Office (Klik og kør)

    /// <summary>Den installerede version og kanal står i registreringsdatabasen; den nyeste pr. kanal i Microsofts liste.</summary>
    public static async Task<UpgradeEntry?> Office(CancellationToken ct)
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Office\ClickToRun\Configuration");
        if (k?.GetValue("VersionToReport") is not string current || current.Length == 0) return null;
        var cdn = (k.GetValue("UpdateChannel") as string is { Length: > 0 } uc ? uc : k.GetValue("CDNBaseUrl") as string) ?? "";
        var channelGuid = cdn.TrimEnd('/').Split('/').LastOrDefault() ?? "";
        if (channelGuid.Length == 0) return null;
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(OfficeFeed, ct));
        string? latest = null;
        foreach (var ch in doc.RootElement.EnumerateArray())
        {
            if (!ch.TryGetProperty("officeVersions", out var versions)) continue;
            var match = versions.EnumerateArray().Any(v => v.TryGetProperty("cdnBaseUrl", out var u) && (u.GetString() ?? "").TrimEnd('/').EndsWith(channelGuid, StringComparison.OrdinalIgnoreCase));
            if (match && ch.TryGetProperty("latestVersion", out var lv)) { latest = lv.GetString(); break; }
        }
        if (latest is null || !Newer(latest, current)) return null;
        var name = (k.GetValue("ProductReleaseIds") as string ?? "").Contains("O365", StringComparison.OrdinalIgnoreCase) ? "Microsoft 365 Apps" : "Microsoft Office";
        return new UpgradeEntry { Name = $"{name} (Office)", Id = OfficeId, Current = current, Available = latest };
    }

    /// <summary>Bed Office' egen opdatering om at hente den nyeste version. Den kører videre i baggrunden; åbne programmer lukkes ikke.</summary>
    public static (bool Ok, string Output) UpdateOffice()
    {
        if (!File.Exists(OfficeClient)) return (false, "Office' opdateringsprogram (OfficeC2RClient.exe) findes ikke på maskinen.");
        using var p = Process.Start(new ProcessStartInfo(OfficeClient, "/update user displaylevel=false forceappshutdown=false") { UseShellExecute = false, CreateNoWindow = true });
        if (p is null) return (false, "OfficeC2RClient.exe kunne ikke startes.");
        if (!p.WaitForExit(TimeSpan.FromMinutes(5))) return (true, "Office-opdateringen er sat i gang og kører videre i baggrunden.");
        return p.ExitCode == 0
            ? (true, "Office-opdateringen er sat i gang. Den kører i baggrunden, og programmer, der er åbne, opdateres, når de lukkes.")
            : (false, $"OfficeC2RClient.exe sluttede med kode {p.ExitCode}.");
    }

    // ---------------------------------------------------------------- AMD-grafikdriver

    /// <summary>AMD Software står i programlisten med sin version (fx 26.10.1); AMD's liste siger, hvad den nyeste WHQL-version er.</summary>
    public static async Task<UpgradeEntry?> Amd(IReadOnlyList<SoftwareEntry> software, CancellationToken ct)
    {
        if (InstalledAmd(software) is not { } current) return null;
        if (await LatestAmd(ct) is not { } latest) return null;
        return Newer(latest.Version, current) ? new UpgradeEntry { Name = "AMD Software: Adrenalin Edition (grafikdriver)", Id = AmdId, Current = current, Available = latest.Version } : null;
    }

    public static string? InstalledAmd(IReadOnlyList<SoftwareEntry> software) =>
        software.FirstOrDefault(s => s.Name.StartsWith("AMD Software", StringComparison.OrdinalIgnoreCase) && s.Version is { Length: > 0 })?.Version;

    /// <summary>Den nyeste WHQL-pakke i AMD's liste: version, fuld installationsfil og størrelse (MB).</summary>
    public sealed record AmdBuild(string Version, string Url, double SizeMb);

    public static async Task<AmdBuild?> LatestAmd(CancellationToken ct)
    {
        using var index = JsonDocument.Parse(await Http.GetStringAsync(AmdFeed, ct));
        if (!index.RootElement.TryGetProperty("Consumer_Whql_Win10", out var whql) || !whql.TryGetProperty("url", out var u) || u.GetString() is not { } detailsUrl) return null;
        if (!TrustedAmdUrl(detailsUrl)) return null;
        using var details = JsonDocument.Parse(await Http.GetStringAsync(detailsUrl, ct));
        return ParseAmdDetails(details.RootElement);
    }

    public static AmdBuild? ParseAmdDetails(JsonElement root)
    {
        var first = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 ? root[0] : default;
        if (first.ValueKind != JsonValueKind.Object || !first.TryGetProperty("externalbuildversion", out var v) || v.GetString() is not { Length: > 0 } version) return null;
        var url = first.TryGetProperty("fullbuild", out var f) ? f.GetString() ?? "" : "";
        var size = first.TryGetProperty("fullbuildfilesize", out var s) && double.TryParse(s.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var mb) ? mb : 0;
        return new AmdBuild(version, url, size);
    }

    /// <summary>Kun AMD's egne servere over https – en ændret liste må ikke kunne pege på en fremmed fil.</summary>
    public static bool TrustedAmdUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps
        && u.Host.ToLowerInvariant() is "www2.ati.com" or "drivers.amd.com" or "www.amd.com";

    /// <summary>
    /// AMD's Setup.exe (AMD's vejledning "Command Line Installation"): resultatkode 0 = alt installeret, 1 = noget fejlede,
    /// 2 = noget fejlede og pc'en skal genstartes. Loggen har en ErrorCode pr. pakke; 3 = installeret, men kræver genstart.
    /// </summary>
    public static (bool Ok, bool Reboot, string Text) AmdResult(int? exitCode, string? log)
    {
        var reboot = exitCode == 2 || (log is not null && System.Text.RegularExpressions.Regex.IsMatch(log, @"ErrorCode\s*=\s*[23]\b"));
        var failed = log is null ? [] : System.Text.RegularExpressions.Regex.Matches(log, @"Package Name\s*=\s*(?<n>[^\r\n]+)[\s\S]*?ErrorCode\s*=\s*(?<c>\d+)")
            .Where(m => m.Groups["c"].Value is "1" or "2").Select(m => m.Groups["n"].Value.Trim()).Distinct().ToList();
        return exitCode switch
        {
            0 => (true, reboot, "AMD-driveren er installeret." + (reboot ? " Genstart kræves." : "")),
            null => (false, false, "AMD's installation blev ikke færdig i tide (stoppet)."),
            _ => (false, reboot, $"AMD's installation fejlede (kode {exitCode})" + (failed.Count > 0 ? ": " + string.Join(", ", failed) : "") + (reboot ? ". Genstart kræves." : ".")),
        };
    }

    /// <summary>7-Zip's kommandolinjeprogram, hvis det er installeret (bruges til at pakke AMD's selvudpakkende fil ud uden vinduer).</summary>
    public static string? SevenZip()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view).OpenSubKey(@"SOFTWARE\7-Zip");
            foreach (var name in new[] { "Path64", "Path" })
                if (k?.GetValue(name) is string p && File.Exists(Path.Combine(p, "7z.exe"))) return Path.Combine(p, "7z.exe");
        }
        foreach (var root in new[] { Environment.GetEnvironmentVariable("ProgramW6432"), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) })
            if (root is { Length: > 0 } && File.Exists(Path.Combine(root, "7-Zip", "7z.exe"))) return Path.Combine(root, "7-Zip", "7z.exe");
        return null;
    }

    internal static readonly HttpClient Big = MakeBig();
    private static HttpClient MakeBig()
    {
        var h = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Fleetdeck");
        h.DefaultRequestHeaders.Referrer = new Uri("https://www.amd.com/");
        return h;
    }

    /// <summary>
    /// Installér den nyeste AMD-grafikdriver uden vinduer: hent AMD's fulde pakke, kontrollér at den er signeret af Advanced Micro
    /// Devices, pak den ud med 7-Zip (installeres med winget, hvis den mangler) og kør Setup.exe -INSTALL -LOG. Skærmen blinker
    /// undervejs. Filerne slettes bagefter; loggen gemmes i arbejdsmappen.
    /// </summary>
    public static async Task<(bool Ok, string Output, bool Reboot)> InstallAmd(Runner runner, Winget winget, string work, IReadOnlyList<SoftwareEntry>? software, CancellationToken ct)
    {
        var lines = new List<string>();
        var build = await LatestAmd(ct);
        if (build is null || !TrustedAmdUrl(build.Url)) return (false, "AMD's liste over drivere kunne ikke læses (eller pegede ikke på AMD's egen server).", false);
        if (software is not null && InstalledAmd(software) is { } cur && !Newer(build.Version, cur)) return (true, $"AMD-driveren {cur} er allerede den nyeste.", false);
        var dir = Path.Combine(work, "amd");
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, $"adrenalin-{build.Version}.exe");
        var unpacked = Path.Combine(dir, $"adrenalin-{build.Version}");
        try
        {
            var started = DateTime.UtcNow;
            using (var resp = await Big.GetAsync(build.Url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(exe);
                await src.CopyToAsync(dst, 1 << 20, ct);
            }
            var mb = new FileInfo(exe).Length / 1048576.0;
            lines.Add($"Hentet AMD {build.Version}: {mb:0.0} MB på {(DateTime.UtcNow - started).TotalSeconds:0} s");
            if (build.SizeMb > 0 && Math.Abs(mb - build.SizeMb) > Math.Max(5, build.SizeMb * 0.01))
                return (false, Join(lines, $"Filen har en anden størrelse end AMD's liste ({build.SizeMb:0.0} MB) – stopper."), false);

            var sig = await runner.PowerShell($"$s = Get-AuthenticodeSignature -LiteralPath '{exe.Replace("'", "''")}'; \"$($s.Status)|$($s.SignerCertificate.Subject)\"", RunAs.System, TimeSpan.FromMinutes(3), ct);
            var sigText = sig.Output.Trim().Split('\n').LastOrDefault()?.Trim() ?? "";
            if (!SignedByAmd(sigText)) return (false, Join(lines, $"Signaturen er ikke AMD's ({sigText}) – stopper."), false);
            lines.Add("Signatur: gyldig, Advanced Micro Devices");

            var seven = SevenZip();
            if (seven is null)
            {
                lines.Add("7-Zip mangler – installerer den med winget");
                await winget.Run($"install --id 7zip.7zip --exact --source winget {Winget.Headless}", RunAs.System, TimeSpan.FromMinutes(15), ct);
                seven = SevenZip();
                if (seven is null) return (false, Join(lines, "7-Zip kunne ikke installeres, så AMD's pakke kan ikke pakkes ud."), false);
            }
            if (Directory.Exists(unpacked)) Directory.Delete(unpacked, true);
            var x = await runner.Run(seven, $"x -y \"-o{unpacked}\" \"{exe}\"", RunAs.System, TimeSpan.FromMinutes(20), dir, ct);
            var setup = Path.Combine(unpacked, "Setup.exe");
            if (x.ExitCode != 0 || !File.Exists(setup)) return (false, Join(lines, $"Udpakningen fejlede (7-Zip kode {x.ExitCode})."), false);
            File.Delete(exe);

            var log = Path.Combine(dir, $"amd-install-{build.Version}.log");
            lines.Add("Installerer (skærmen blinker et par gange)…");
            var r = await runner.Run(setup, $"-INSTALL -LOG \"{log}\"", RunAs.System, TimeSpan.FromMinutes(60), unpacked, ct);
            var logText = File.Exists(log) ? await File.ReadAllTextAsync(log, ct) : null;
            var (ok, reboot, text) = AmdResult(r.TimedOut ? null : r.ExitCode, logText);
            lines.Add(text);
            if (File.Exists(log)) lines.Add($"Log: {log}");
            return (ok, Join(lines), reboot);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            return (false, Join(lines, $"Fejl: {e.Message}"), false);
        }
        finally
        {
            Forget();
            try { if (File.Exists(exe)) File.Delete(exe); } catch (IOException) { }
            try { if (Directory.Exists(unpacked)) Directory.Delete(unpacked, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>"Valid|CN=Advanced Micro Devices, O=Advanced Micro Devices, …" fra Get-AuthenticodeSignature.</summary>
    public static bool SignedByAmd(string statusAndSubject)
    {
        var parts = statusAndSubject.Split('|', 2);
        return parts.Length == 2 && parts[0] == "Valid" && System.Text.RegularExpressions.Regex.IsMatch(parts[1], @"(^|,\s*)O=""?Advanced Micro Devices(,? Inc\.?)?""?(,|$)");
    }

    private static string Join(List<string> lines, string? last = null) => string.Join('\n', last is null ? lines : [.. lines, last]);

    /// <summary>Er <paramref name="a"/> nyere end <paramref name="b"/>? Tallene sammenlignes ét for ét (26.11.2 &gt; 26.10.1).</summary>
    public static bool Newer(string a, string b)
    {
        static long[] Parts(string s) => s.Split('.', '-', ' ').TakeWhile(x => x.Length > 0 && x.All(char.IsDigit)).Select(x => long.TryParse(x, out var n) ? n : 0).ToArray();
        var x = Parts(a); var y = Parts(b);
        if (x.Length == 0 || y.Length == 0) return false;
        for (var i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            long p = i < x.Length ? x[i] : 0, q = i < y.Length ? y[i] : 0;
            if (p != q) return p > q;
        }
        return false;
    }
}

/// <summary>Har agenten skiftet version siden sidste inventar? (Filen skrives, når et inventar er sendt.)</summary>
public static class InventoryVersion
{
    public static bool Changed(string file, string myVersion)
    {
        if (myVersion is not { Length: > 0 } || myVersion == "dev") return false;
        try { return !File.Exists(file) || File.ReadAllText(file).Trim() != myVersion; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}

/// <summary>
/// Valgfrie drivere fra Windows Update, der ikke bør installeres: gamle (over 3 år), ældre end den driver, der sidder i dag,
/// eller en producentdriver, der vil erstatte Microsofts egen. De markeres "Anbefales ikke" og kan skjules (IsHidden).
/// </summary>
public static class DriverAdvice
{
    public const int MaxAgeYears = 3;

    /// <summary>En installeret driver: enhedens hardware-id, udgiveren og driverens dato.</summary>
    public sealed record Installed(string HardwareId, string? Provider, DateTime? Date);

    /// <summary>Den installerede driver til samme hardware-id (WU's id kan være mere eller mindre specifikt end enhedens).</summary>
    public static Installed? Match(IReadOnlyList<Installed> installed, string? hardwareId)
    {
        if (string.IsNullOrWhiteSpace(hardwareId)) return null;
        var h = hardwareId.Trim();
        return installed.FirstOrDefault(i => i.HardwareId.Equals(h, StringComparison.OrdinalIgnoreCase))
            ?? installed.FirstOrDefault(i => i.HardwareId.StartsWith(h, StringComparison.OrdinalIgnoreCase) || h.StartsWith(i.HardwareId, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsMicrosoft(string? provider) => provider?.Trim().StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Hvorfor driveren ikke anbefales – eller null, når der ikke er noget at sige.</summary>
    public static string? For(DateTime? offered, string? offeredProvider, Installed? current, DateTime now)
    {
        var why = new List<string>();
        if (offered is { } d && d.Date < now.Date.AddYears(-MaxAgeYears)) why.Add($"Driveren er fra {d:yyyy} – over {MaxAgeYears} år gammel");
        if (offered is { } o && current?.Date is { } c && c.Date > o.Date) why.Add($"Den installerede driver er nyere ({c:yyyy-MM-dd})");
        if (current is not null && IsMicrosoft(current.Provider) && !string.IsNullOrWhiteSpace(offeredProvider) && !IsMicrosoft(offeredProvider))
            why.Add("Erstatter Windows' egen driver");
        return why.Count == 0 ? null : string.Join(". ", why) + ".";
    }
}
