using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Fleetdeck.Server.Data;
using Fleetdeck.Shared;

namespace Fleetdeck.Server.Services;

public sealed record ComplianceItem(int PackageId, string Name, string Type, string Category, string Intent, string State,
    bool? Installed, JobBrief? LastJob);
public sealed record JobBrief(int Id, string Type, string Status, DateTime Created, DateTime? Finished);
public sealed record ComplianceSummary(int Total, int Ok, int Missing, int Failed, int Pending, int Unknown, int Pct);

/// <summary>Sammenligner maskinens inventar med profilen og opretter jobs for det der mangler.</summary>
public sealed partial class ComplianceService(AppDb db, JobService jobs)
{
    [GeneratedRegex("[^a-z0-9]")] private static partial Regex NonAlnum();
    private static string Norm(string? s) => NonAlnum().Replace((s ?? "").ToLowerInvariant(), "");

    public static bool IsInstalled(Package p, IReadOnlyList<string> names, IReadOnlySet<string> wingetIds)
    {
        var id = p.Identifier.ToLowerInvariant();
        if (p.Type is PackageTypes.Winget or PackageTypes.MsStore && id.Length > 0 && wingetIds.Contains(id)) return true;
        // Linux-agenten melder installerede apt- og snap-pakker som id'er
        // (script-pakker genkendes på deres Detect-mønster mod programnavnene, se nedenfor)
        if (PackageTypes.IsLinux(p.Type) && p.Type != PackageTypes.Script) return id.Length > 0 && wingetIds.Contains(id);
        // Mac-agenten melder Homebrew-navne (uden cask:/formula:) og programnavne
        if (PackageTypes.IsMac(p.Type))
        {
            var bare = id.Contains(':') ? id[(id.IndexOf(':') + 1)..] : id;
            return bare.Length > 0 && (wingetIds.Contains(bare) || names.Any(n => Norm(n) == Norm(p.Name)));
        }
        if (p.Type == PackageTypes.Steam && id.Length > 0)
            return names.Any(n => n.EndsWith($"(Steam {id})", StringComparison.OrdinalIgnoreCase));   // agenten læser appmanifest_*.acf
        if (!string.IsNullOrWhiteSpace(p.Detect))
        {
            try
            {
                var rx = new Regex(p.Detect, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200));
                return names.Any(n => rx.IsMatch(n));
            }
            catch (ArgumentException) { }
        }
        var key = Norm(p.Name);
        if (key.Length > 0 && names.Any(n => Norm(n).Contains(key))) return true;
        // Ord-match: alle ord (min. 3 tegn) fra navnet uden parentes skal findes i programnavnet.
        // "REW (Room EQ Wizard)" ~ "REW 5.40 beta", "RealVNC Viewer" ~ "RealVNC Connect Viewer"
        var words = Words().Matches(Parens().Replace(p.Name, " ").ToLowerInvariant()).Select(m => m.Value).Where(w => w.Length >= 3).ToList();
        if (words.Count == 0) return false;
        return names.Any(n =>
        {
            var hay = Words().Matches(n.ToLowerInvariant()).Select(m => m.Value).ToHashSet();
            var flat = Norm(n);
            return words.All(w => hay.Contains(w) || (w.Length >= 5 && flat.Contains(w)));
        });
    }

    [GeneratedRegex(@"\([^)]*\)")] private static partial Regex Parens();
    [GeneratedRegex(@"[\p{L}\p{N}+]+")] private static partial Regex Words();

    public async Task<List<ComplianceItem>> Evaluate(int machineId)
    {
        var m = await db.Machines.AsNoTracking().FirstOrDefaultAsync(x => x.Id == machineId);
        if (m?.ProfileId is null) return [];
        var names = await db.Software.Where(s => s.MachineId == machineId).Select(s => s.Name).ToListAsync();
        var wids = (await db.WingetIds.Where(w => w.MachineId == machineId).Select(w => w.Value).ToListAsync())
            .Select(v => v.ToLowerInvariant()).ToHashSet();
        var since = m.Enrolled ?? DateTime.MinValue;
        var pkgs = (await db.ProfilePackages.Where(pp => pp.ProfileId == m.ProfileId).Include(pp => pp.Package)
            .OrderBy(pp => pp.Package!.Name).ToListAsync())
            .Where(pp => PackageTypes.Fits(pp.Package!.Type, m.Platform)).ToList();   // winget til Windows, apt/snap til Linux
        // printere, der kræver driver: driverpakken hører med, selv om den ikke står på profilen
        foreach (var drv in await Printers.PrinterService.DriverPackages(db, m))
            if (PackageTypes.Fits(drv.Type, m.Platform) && pkgs.All(pp => pp.PackageId != drv.Id))
                pkgs.Add(new ProfilePackage { ProfileId = m.ProfileId.Value, PackageId = drv.Id, Package = drv, Intent = Intents.Required });
        var lastJobs = (await db.Jobs.Where(j => j.MachineId == machineId && j.PackageId != null && j.Created >= since)
                .Select(j => new { j.Id, j.PackageId, j.Type, j.Status, j.Created, j.Finished }).ToListAsync())
            .GroupBy(j => j.PackageId!.Value).ToDictionary(g => g.Key, g => g.OrderByDescending(j => j.Id).First());

        var hasInv = m.LastInventory is not null;
        var result = new List<ComplianceItem>();
        foreach (var pp in pkgs)
        {
            var p = pp.Package!;
            lastJobs.TryGetValue(p.Id, out var last);
            bool? installed = hasInv ? IsInstalled(p, names, wids) : null;
            // et lykket installationsjob tæller kun, til inventaret er nyere: er programmet væk i et inventar indsamlet
            // efter jobbet blev færdigt (fx afinstalleret af brugeren), mangler det igen
            if (installed == false && last is { Type: JobTypes.Install, Status: JobStatus.Success } && !(m.LastInventory > last.Finished)) installed = true;
            if (installed == true && pp.Intent == Intents.Uninstall && last is { Type: JobTypes.Uninstall, Status: JobStatus.Success }) installed = false;
            var failed = last?.Status == JobStatus.Failed;
            string state = last is not null && JobStatus.Active.Contains(last.Status) ? "pending"
                : pp.Intent switch
                {
                    Intents.Required => installed switch { true => "ok", null => "unknown", _ => failed ? "failed" : "missing" },
                    Intents.Uninstall => installed switch { false => "ok", null => "unknown", _ => failed ? "failed" : "present" },
                    _ => installed switch { true => "installed", null => "unknown", _ => "available" },
                };
            result.Add(new ComplianceItem(p.Id, p.Name, p.Type, p.Category, pp.Intent, state, installed,
                last is null ? null : new JobBrief(last.Id, last.Type, last.Status, last.Created, last.Finished)));
        }
        return result;
    }

    public static ComplianceSummary Summarize(IEnumerable<ComplianceItem> items)
    {
        var e = items.Where(i => i.Intent is Intents.Required or Intents.Uninstall).ToList();
        int ok = e.Count(i => i.State == "ok"), missing = e.Count(i => i.State is "missing" or "present"),
            failed = e.Count(i => i.State == "failed"), pending = e.Count(i => i.State == "pending"), unknown = e.Count(i => i.State == "unknown");
        return new ComplianceSummary(e.Count, ok, missing, failed, pending, unknown, e.Count == 0 ? 100 : (int)Math.Round(100.0 * ok / e.Count));
    }

    /// <summary>Opret jobs så maskinen passer til profilen. Venter 6 timer efter en fejl før nyt forsøg.</summary>
    public async Task<int> Enforce(int machineId, string by = "overholdelse")
    {
        var m = await db.Machines.AsNoTracking().Include(x => x.Profile).FirstOrDefaultAsync(x => x.Id == machineId);
        if (m?.Profile is null || !m.Profile.Enforce || m.LastInventory is null) return 0;
        var n = 0;
        foreach (var i in await Evaluate(machineId))
        {
            if (i.LastJob is { Status: JobStatus.Failed } lj && lj.Finished > DateTime.UtcNow.AddHours(-6)) continue;
            var action = i.Intent == Intents.Required && i.State is "missing" or "failed" ? JobTypes.Install
                : i.Intent == Intents.Uninstall && i.State is "present" or "failed" ? JobTypes.Uninstall : null;
            if (action is null) continue;
            // påkrævet program væk i et nyere inventar efter en lykket installation: det vises som "mangler", men udløser ikke
            // selv en installation (ejerens regel) – før stod det som "OK", og der skete intet. Installér det i hånden.
            if (action == JobTypes.Install && i.LastJob is { Type: JobTypes.Install, Status: JobStatus.Success }) continue;
            var p = await db.Packages.FindAsync(i.PackageId);
            if (p is null || (p.Type == PackageTypes.File && string.IsNullOrEmpty(p.Identifier))) continue;
            await jobs.ForPackage(machineId, p, action, "compliance", by, save: false);
            n++;
        }
        if (n > 0) await jobs.Commit(machineId);
        return n;
    }
}

/// <summary>Nyinstallations-sekvens: drivere → Windows Update → programmer → filer → opsætning → genstart.</summary>
public sealed class ProvisionService(AppDb db, JobService jobs)
{
    /// <summary>Sidste trin i nyinstallationen (kører som SYSTEM).</summary>
    public const string FinishScript = """
        $wl = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'
        $u = (Get-ItemProperty $wl -ErrorAction SilentlyContinue).DefaultUserName
        Set-ItemProperty $wl -Name AutoAdminLogon -Value '0'
        Remove-ItemProperty $wl -Name DefaultPassword, AutoLogonCount -ErrorAction SilentlyContinue
        'Automatisk login er slået fra.'
        $acct = if ($u) { Get-LocalUser -Name $u -ErrorAction SilentlyContinue }
        if ($acct -and -not $acct.PasswordLastSet) {
            # Kontoen er oprettet uden adgangskode: Windows beder om en ved næste login
            net accounts /maxpwage:unlimited | Out-Null
            Set-LocalUser -Name $u -PasswordNeverExpires $false
            net user "$u" /logonpasswordchg:yes | Out-Null
            "$u vælger adgangskode ved næste login."
        }
        Get-PSDrive -PSProvider FileSystem | ForEach-Object { Join-Path $_.Root 'WinSetupBoot' } | Where-Object { Test-Path $_ } |
            ForEach-Object { Remove-Item $_ -Recurse -Force -ErrorAction SilentlyContinue }
        Remove-Item "$env:ProgramData\WinSetup\Bootstrap.ps1" -Force -ErrorAction SilentlyContinue
        'Nyinstallationen er færdig.'
        """;

    public async Task<int> Queue(int machineId, string by)
    {
        var m = await db.Machines.Include(x => x.Profile).FirstAsync(x => x.Id == machineId);
        var p = m.Profile;
        var n = 0;
        if (m.Platform == Platforms.Mac)
        {
            // Mac: intet genskab-kit endnu (Time Machine) – opdatér programmer og macOS
            await jobs.Create(machineId, JobTypes.Upgrade, "Opdatér alle programmer", new JobParams(), RunAs.System, null, "provision", by, false);
            await jobs.Commit(machineId);
            return 1;
        }
        if (m.Platform == Platforms.Linux)
        {
            // Linux: læg genskab-kittet på (pakker, opsætning, tjenester), opdatér og genstart
            await jobs.WinSetup(machineId, "Restore", "provision", by, false);
            await jobs.Create(machineId, JobTypes.Upgrade, "Opdatér alle pakker", new JobParams(), RunAs.System, null, "provision", by, false);
            await jobs.Create(machineId, JobTypes.Reboot, "Genstart efter genskabelse",
                new JobParams { Delay = 120, OnlyIfPending = false, Message = "Maskinen er genskabt og genstarter om 2 minutter." }, RunAs.System, null, "provision", by, false);
            await jobs.Commit(machineId);
            return 3;
        }
        if (p is null || p.ProvDrivers) { await jobs.WinSetup(machineId, "Drivers", "provision", by, false); n++; }
        if (p is not null)
        {
            var pkgs = await db.ProfilePackages.Where(pp => pp.ProfileId == p.Id && pp.Intent == Intents.Required)
                .Include(pp => pp.Package).Select(pp => pp.Package!).OrderBy(x => x.Type == PackageTypes.Steam).ThenBy(x => x.Id).ToListAsync();
            foreach (var pkg in pkgs.Where(x => x.Type != PackageTypes.File || x.Identifier.Length > 0))
            {
                await jobs.ForPackage(machineId, pkg, JobTypes.Install, "provision", by, false); n++;
            }
        }
        if (p is null || p.ProvRestore) { await jobs.WinSetup(machineId, "Restore", "provision", by, false); n++; }
        if (p is null || p.ProvAppearance) { await jobs.WinSetup(machineId, "Appearance", "provision", by, false); n++; }
        if (p is null || p.ProvWindowsUpdate)
        {
            await jobs.Create(machineId, JobTypes.WindowsUpdate, "Installér Windows-opdateringer", new JobParams(), RunAs.System, null, "provision", by, false); n++;
        }
        // Afslut: automatisk login fra, og har kontoen aldrig fået en adgangskode, beder Windows om en ved næste login
        await jobs.Create(machineId, JobTypes.Script, "Afslut nyinstallation (automatisk login fra)",
            new JobParams { Body = FinishScript, TimeoutMin = 5, Name = "finish_provision" }, RunAs.System, null, "provision", by, false);
        await jobs.Create(machineId, JobTypes.Reboot, "Genstart efter nyinstallation",
            new JobParams { Delay = 120, OnlyIfPending = false, Message = "Opsætningen er færdig. Pc'en genstarter om 2 minutter." },
            RunAs.System, null, "provision", by, false);
        await jobs.Commit(machineId);
        return n + 1;
    }
}
