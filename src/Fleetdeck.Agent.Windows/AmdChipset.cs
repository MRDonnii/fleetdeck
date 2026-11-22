using System.Text.RegularExpressions;
using Fleetdeck.Shared;
using Microsoft.Win32;

namespace Fleetdeck.Agent.Windows;

/// <summary>
/// AMD Chipset Software (Ryzen-chipset-driverne) som en linje i den dybe opdateringsscanning: id <c>chipset:amd</c>.
///
/// Nyeste version: AMD har ingen JSON-liste for chipset-pakken. AMD's driversider for chipsættene linker direkte til
/// <c>https://drivers.amd.com/drivers/amd_chipset_software_&lt;version&gt;.exe</c> og til udgivelsesnoterne
/// <c>RN-RYZEN-CHIPSET-&lt;version&gt;.html</c>. Pakken er fælles for alle Ryzen-sokler, så agenten læser et par af siderne,
/// tager den højeste version, den finder, og spørger AMD's server (HEAD med henviser www.amd.com), om filen findes. Uden
/// henviser sender AMD en omdirigering til en fejlside – den afvises, fordi slutadressen så ikke er drivers.amd.com.
///
/// Installation: pakken er en NSIS-pakke. Køres den med /S som SYSTEM, fejler den indre MSI med 1316, ruller tilbage
/// (kode 2), og den gamle programpost forsvinder. Agenten kører den derfor med /S i den indloggede brugers session med
/// brugerens fulde administratortoken (samme som "Kør som administrator"), og kun hvis den bruger er administrator.
/// Er ingen logget ind, eller er brugeren ikke administrator, stopper den med en forklaring – den falder aldrig tilbage til SYSTEM.
/// </summary>
public static class AmdChipset
{
    public const string Id = "chipset:amd";
    public const string Title = "AMD Chipset Software (chipset-driver)";
    public const string Unknown = "ukendt";
    public const string ProgramName = "AMD Chipset Software";

    /// <summary>AMD-sider, der i dag linker direkte til den nyeste chipset-pakke (fælles for alle Ryzen-sokler).</summary>
    public static readonly string[] Pages =
    [
        "https://www.amd.com/en/support/downloads/drivers.html/chipsets/am5/x870e.html",
        "https://www.amd.com/en/support/downloads/drivers.html/chipsets/am5/x670e.html",
        "https://www.amd.com/en/support/downloads/drivers.html/chipsets/am4/b550.html",
    ];

    private static readonly Regex ExeLink = new(@"amd_chipset_software_(?<v>\d{1,3}(?:\.\d{1,5}){1,4})\.exe", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex NotesLink = new(@"RN-RYZEN-CHIPSET-(?<v>\d{1,3}(?:-\d{1,5}){1,4})\.html", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex VersionShape = new(@"^\d{1,3}(?:\.\d{1,5}){1,4}$", RegexOptions.CultureInvariant);

    public sealed record ChipsetBuild(string Version, string Url, long Bytes);

    // ---------------------------------------------------------------- ren logik (testes)

    /// <summary>Alle versioner, siderne nævner (download-links og udgivelsesnoter), nyeste først og uden dubletter.</summary>
    public static List<string> Candidates(IEnumerable<string> pages)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var html in pages)
        {
            foreach (Match m in ExeLink.Matches(html)) found.Add(m.Groups["v"].Value);
            foreach (Match m in NotesLink.Matches(html)) found.Add(m.Groups["v"].Value.Replace('-', '.'));
        }
        var list = found.Where(ValidVersion).ToList();
        list.Sort((a, b) => DeepUpdates.Newer(a, b) ? -1 : DeepUpdates.Newer(b, a) ? 1 : string.CompareOrdinal(a, b));
        return list;
    }

    public static bool ValidVersion(string v) => VersionShape.IsMatch(v);

    /// <summary>AMD's faste adresse for en version. Kun versioner med den rigtige form – ellers kunne en side styre adressen.</summary>
    public static string? PackageUrl(string version) =>
        ValidVersion(version) ? $"https://drivers.amd.com/drivers/amd_chipset_software_{version}.exe" : null;

    /// <summary>
    /// Svarede AMD med selve pakken? Slutadressen (efter omdirigeringer) skal være https://drivers.amd.com/…exe, svaret må ikke være
    /// en HTML-side, og størrelsen skal ligne en chipset-pakke (i dag ca. 80 MB).
    /// </summary>
    public static bool PackageLooksRight(Uri? finalUri, string? mediaType, long? bytes) =>
        finalUri is { Scheme: "https" } u && u.Host.Equals("drivers.amd.com", StringComparison.OrdinalIgnoreCase)
        && u.AbsolutePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
        && !(mediaType ?? "").Contains("html", StringComparison.OrdinalIgnoreCase)
        && bytes is > 10L * 1024 * 1024 and < 1024L * 1024 * 1024;

    /// <summary>Versionen af AMD Chipset Software i programlisten, hvis den står der.</summary>
    public static string? Installed(IEnumerable<SoftwareEntry> software) =>
        software.FirstOrDefault(s => s.Name.StartsWith(ProgramName, StringComparison.OrdinalIgnoreCase) && s.Version is { Length: > 0 })?.Version;

    /// <summary>
    /// Skal linjen vises? Kun på pc'er, der har AMD's chipset-pakke: enten står den i programlisten (og der er en nyere), eller
    /// posten er forsvundet, men AMD's mappe er der stadig (det sker efter en fejlet installation som SYSTEM) – så vises
    /// den med nuværende version "ukendt", så posten kan lægges tilbage.
    /// </summary>
    public static UpgradeEntry? Offer(string? installed, bool leftoverFolder, string latest)
    {
        if (!ValidVersion(latest)) return null;
        if (installed is { Length: > 0 })
            return DeepUpdates.Newer(latest, installed) ? new UpgradeEntry { Name = Title, Id = Id, Current = installed, Available = latest } : null;
        return leftoverFolder ? new UpgradeEntry { Name = Title, Id = Id, Current = Unknown, Available = latest } : null;
    }

    /// <summary>Er hovedversionen 7 eller nyere? Fra 7.x kan man ikke gå tilbage til 6.x uden at afinstallere og slette Qt_Dependencies.</summary>
    public static bool NoWayBack(string version) =>
        int.TryParse(version.Split('.')[0], out var major) && major >= 7;

    /// <summary>Sidste linje fra PowerShell-tjekket "er jeg administrator?" – True betyder, at tokenet har administratorrettigheder.</summary>
    public static bool IsAdminAnswer(string output) =>
        output.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim().Equals("True", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Resultatet afgøres af programlisten bagefter, ikke kun af resultatkoden: NSIS-pakken kan sige 0, selv om den indre MSI
    /// rullede tilbage. Kode 2 = rullet tilbage (set ved /S som SYSTEM).
    /// </summary>
    public static (bool Ok, bool Reboot, string Text) Result(int? exitCode, bool timedOut, string? before, string? after, string target)
    {
        if (after is { Length: > 0 } && !DeepUpdates.Newer(target, after))
            return (true, true, $"AMD Chipset Software {after} er installeret. Genstart pc'en, så de nye drivere bliver brugt.");
        if (timedOut || exitCode is null) return (false, false, "AMD's installation blev ikke færdig i tide (stoppet efter 30 minutter).");
        var now = after is { Length: > 0 } ? $"Programlisten viser stadig {after}." : "AMD Chipset Software står ikke i programlisten.";
        return exitCode switch
        {
            0 => (false, false, $"AMD's installation sluttede uden fejlkode, men {target} blev ikke installeret. {now}"),
            2 => (false, false, $"AMD's installation rullede tilbage (kode 2). {now} Driverne, der allerede er installeret, bliver brugt som før."),
            _ => (false, false, $"AMD's installation fejlede (kode {exitCode}). {now}"),
        };
    }

    public const string NoUserText = "Ingen er logget ind på pc'en. AMD's chipset-pakke skal køres i en indlogget administrators session " +
        "(som SYSTEM fejler den med MSI-fejl 1316 og ruller tilbage). Prøv igen, når en administrator er logget ind.";
    public const string NotAdminText = "Den indloggede bruger er ikke administrator. AMD's chipset-pakke skal køres med administratorrettigheder " +
        "i brugerens session (som SYSTEM fejler den med MSI-fejl 1316). Log ind som administrator, og prøv igen.";

    // ---------------------------------------------------------------- Windows og netværk

    /// <summary>AMD's programmappe findes, selv om posten er væk fra programlisten (driverne og Qt_Dependencies bliver liggende).</summary>
    public static bool LeftoverFolder()
    {
        var x86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        return x86.Length > 0 && Directory.Exists(Path.Combine(x86, "AMD", "Chipset_Software"));
    }

    /// <summary>Læs versionen direkte i registreringsdatabasen (bruges før og efter installationen).</summary>
    public static string? ReadInstalled()
    {
        const string un = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        var found = new List<SoftwareEntry>();
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var hk = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var root = hk.OpenSubKey(un);
            if (root is null) continue;
            foreach (var sub in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(sub);
                if (k?.GetValue("DisplayName") is string n && n.StartsWith(ProgramName, StringComparison.OrdinalIgnoreCase))
                    found.Add(new SoftwareEntry { Name = n, Version = k.GetValue("DisplayVersion")?.ToString() });
            }
        }
        var versions = found.Where(f => f.Version is { Length: > 0 }).Select(f => f.Version!).ToList();
        return versions.Count == 0 ? null : versions.Aggregate((a, b) => DeepUpdates.Newer(b, a) ? b : a);
    }

    /// <summary>Linjen til den dybe scanning. Uden AMD's chipset-pakke på pc'en spørges AMD slet ikke.</summary>
    public static async Task<UpgradeEntry?> Check(IReadOnlyList<SoftwareEntry> software, HttpClient http, CancellationToken ct)
    {
        var installed = Installed(software);
        var leftover = installed is null && LeftoverFolder();
        if (installed is null && !leftover) return null;
        if (await Latest(http, ct) is not { } latest) return null;
        return Offer(installed, leftover, latest.Version);
    }

    /// <summary>Den nyeste version på AMD's sider, som AMD's server også svarer med en pakke for.</summary>
    public static async Task<ChipsetBuild?> Latest(HttpClient http, CancellationToken ct)
    {
        var pages = new List<string>();
        foreach (var page in Pages)
        {
            try { pages.Add(await http.GetStringAsync(page, ct)); }
            catch (Exception e) when (e is HttpRequestException || (e is TaskCanceledException && !ct.IsCancellationRequested)) { }
            if (pages.Count >= 2) break;
        }
        foreach (var v in Candidates(pages).Take(3))
        {
            if (PackageUrl(v) is not { } url) continue;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Head, url);
                using var resp = await http.SendAsync(req, ct);
                var len = resp.Content.Headers.ContentLength;
                if (resp.IsSuccessStatusCode && PackageLooksRight(resp.RequestMessage?.RequestUri, resp.Content.Headers.ContentType?.MediaType, len))
                    return new ChipsetBuild(v, url, len ?? 0);
            }
            catch (Exception e) when (e is HttpRequestException || (e is TaskCanceledException && !ct.IsCancellationRequested)) { }
        }
        return null;
    }

    /// <summary>
    /// Installér den nyeste AMD Chipset Software: find versionen, kontrollér at den indloggede bruger er administrator, hent pakken
    /// til agentens arbejdsmappe (kun SYSTEM og administratorer kan skrive der), kontrollér signaturen (Advanced Micro Devices) og
    /// kør den med /S i brugerens session med fuldt administratortoken. Programlisten afgør, om det lykkedes.
    /// </summary>
    public static async Task<(bool Ok, string Output, bool Reboot)> Install(Runner runner, HttpClient http, HttpClient big, string work, CancellationToken ct)
    {
        var lines = new List<string>();
        string Join(string? last = null) => string.Join('\n', last is null ? lines : [.. lines, last]);
        LiveJob.Phase(0, 5, "Finder AMD's nyeste chipset-driver");
        var before = ReadInstalled();
        var build = await Latest(http, ct);
        if (build is null) return (false, "AMD's sider med chipset-drivere kunne ikke læses, eller AMD's server svarede ikke med pakken.", false);
        if (before is { } cur && !DeepUpdates.Newer(build.Version, cur)) return (true, $"AMD Chipset Software {cur} er allerede den nyeste.", false);
        lines.Add($"Nyeste fra AMD: {build.Version} (installeret nu: {before ?? "står ikke i programlisten"})");
        if (NoWayBack(build.Version) && (before is null || !NoWayBack(before)))
            lines.Add("Bemærk: fra 7.x kan man ikke gå tilbage til 6.x uden at afinstallere og slette mappen Qt_Dependencies først.");

        // Først: er der en administrator logget ind? Ellers er der ingen grund til at hente 80 MB.
        var probe = await runner.PowerShell("([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)",
            RunAs.UserAdmin, TimeSpan.FromMinutes(1), ct);
        if (probe.NoUser) return (false, Join(NoUserText), false);
        if (!IsAdminAnswer(probe.Output)) return (false, Join(NotAdminText), false);
        lines.Add("Indlogget bruger: administrator");

        var dir = Path.Combine(work, "chipset");
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, $"amd_chipset_software_{build.Version}.exe");
        try
        {
            LiveJob.Phase(5, 40, $"Henter AMD Chipset Software {build.Version}");
            var started = DateTime.UtcNow;
            using (var resp = await big.GetAsync(build.Url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                if (!PackageLooksRight(resp.RequestMessage?.RequestUri, resp.Content.Headers.ContentType?.MediaType, resp.Content.Headers.ContentLength))
                    return (false, Join("AMD's server svarede ikke med pakken (omdirigeret eller en side i stedet for filen) – stopper."), false);
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(exe);
                await src.CopyToAsync(dst, 1 << 20, ct);
            }
            var bytes = new FileInfo(exe).Length;
            lines.Add($"Hentet: {bytes / 1048576.0:0.0} MB på {(DateTime.UtcNow - started).TotalSeconds:0} s");
            if (build.Bytes > 0 && bytes != build.Bytes) return (false, Join($"Filen har en anden størrelse end AMD's server oplyste ({build.Bytes} byte) – stopper."), false);

            LiveJob.Phase(40, 45, "Kontrollerer AMD's signatur");
            var sig = await runner.PowerShell($"$s = Get-AuthenticodeSignature -LiteralPath '{exe.Replace("'", "''")}'; \"$($s.Status)|$($s.SignerCertificate.Subject)\"", RunAs.System, TimeSpan.FromMinutes(3), ct);
            var sigText = sig.Output.Trim().Split('\n').LastOrDefault()?.Trim() ?? "";
            if (!DeepUpdates.SignedByAmd(sigText)) return (false, Join($"Signaturen er ikke AMD's ({sigText}) – stopper."), false);
            lines.Add("Signatur: gyldig, Advanced Micro Devices");

            LiveJob.Phase(45, 100, "Installerer i brugerens session (skærmen kan blinke)");
            var r = await runner.Run(exe, "/S", RunAs.UserAdmin, TimeSpan.FromMinutes(30), dir, ct);
            if (r.Cancelled) return (false, Join("Annulleret fra Fleetdeck."), false);
            if (r.NoUser) return (false, Join(NoUserText), false);
            // Meldte pakken OK, men står den nye version ikke i programlisten endnu, ventes der op til to minutter på posten
            var after = ReadInstalled();
            for (var i = 0; i < 12 && r.ExitCode is 0 or 3010 && (after is null || DeepUpdates.Newer(build.Version, after)); i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
                after = ReadInstalled();
            }
            var (ok, reboot, text) = Result(r.TimedOut ? null : r.ExitCode, r.TimedOut, before, after, build.Version);
            if (r.ExitCode is int code) lines.Add($"AMD's resultatkode: {code}");
            lines.Add(text);
            return (ok, Join(), reboot);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            return (false, Join($"Fejl: {e.Message}"), false);
        }
        finally
        {
            DeepUpdates.Forget();
            try { if (File.Exists(exe)) File.Delete(exe); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}
