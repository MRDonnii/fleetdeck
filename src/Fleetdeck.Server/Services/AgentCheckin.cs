using Microsoft.EntityFrameworkCore;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Endpoints;
using Fleetdeck.Shared;

namespace Fleetdeck.Server.Services;

/// <summary>Svaret på et check-in: jobs og alt det andet agenten skal vide, signeret til agenter, der sender en nonce.</summary>
public static class AgentCheckin
{
    public static async Task<CheckinResponse> Build(AppDb db, SettingsService s, AgentBinary bin, SigningKeys fileKeys, JobSigningKey jobKey, TlsInfo tls,
        Machine m, CheckinRequest r, int tokenId, string webBase, IReadOnlyList<TerminalTicket>? terminals = null,
        IReadOnlyList<LiveLogTicket>? liveLogs = null)
    {
        // Ventende tilmelding (navnet fandtes): intet arbejde, ingen adgangsoplysninger før en administrator har godkendt den.
        // "Kræv signerede jobs": jobs leveres kun til agenter, der har pinnet nøglen (de øvrige får stadig opdateringen og opgraderer sig selv).
        var mode = SignedJobsMode.Normalize(await s.Get("signed_jobs"));
        var pendingApproval = m.ReplacesId is not null;
        var withhold = pendingApproval || (mode == SignedJobsMode.Enforce && r.Trust?.JobKeyPinned != true);
        var jobs = withhold ? [] : await db.Jobs.AsNoTracking().Where(j => j.MachineId == m.Id && (j.Status == JobStatus.Pending || j.Status == JobStatus.WaitingUser))
            .OrderBy(j => j.Id).Take(10).ToListAsync();
        var invH = await s.GetNum("inventory_hours", 6);
        var healthM = await s.GetNum("health_minutes", 30);
        // overvågningstjek, som denne maskine skal køre nu
        var nowC = DateTime.UtcNow;
        var agentChecks = (await db.Checks.AsNoTracking().Where(c => c.Enabled && c.RunOnMachineId == m.Id).ToListAsync())
            .Where(c => c.LastCheck is null || nowC - c.LastCheck.Value >= TimeSpan.FromSeconds(Math.Max(20, c.IntervalSec - 10)))
            .Select(CheckProbe.ToAgent).ToList();
        var resp = new CheckinResponse
        {
            Checks = pendingApproval ? [] : agentChecks,
            KeepRunning = !pendingApproval && m.ProfileId is int kpid ? await db.Profiles.AsNoTracking().Where(p => p.Id == kpid).Select(p => p.KeepRunning).FirstOrDefaultAsync() ?? [] : [],
            PollSeconds = (int)await s.GetNum("poll_seconds", 30),
            InventoryDue = m.InventoryRequested || m.LastInventory is null || m.LastInventory < DateTime.UtcNow.AddHours(-invH),
            HealthDue = m.LastHealth is null || m.LastHealth < DateTime.UtcNow.AddMinutes(-healthM),
            AgentVersion = bin.VersionFor(m.Platform, r.Arch),
            ServerUrl = await s.Get("server_url"),
            // til ikonet i proceslinjen
            Profile = m.ProfileId is int pid ? await db.Profiles.Where(p => p.Id == pid).Select(p => p.Name).FirstOrDefaultAsync() : null,
            Upgrades = await UpgradeLocks.OpenCount(db, m.Id),
            Alerts = await db.Alerts.CountAsync(a => a.MachineId == m.Id && a.Resolved == null && a.Acknowledged == null && a.Severity != "info"),
            Health = m.HealthState,
            WebUrl = webBase,
            Jobs = jobs.Select(j => new AgentJob { Id = j.Id, Type = j.Type, Title = j.Title, RunAs = j.RunAs, Params = Json.Des<JobParams>(j.ParamsJson) }).ToList(),
            Share = pendingApproval ? null : new ShareInfo { Path = await s.Get("share_path"), User = await s.Get("share_user"), Password = await s.Get("share_pass") },
            Drives = pendingApproval ? [] : (await Drives.For(db, m)).Select(Drives.Spec).ToList(),
            KeepAwake = pendingApproval ? null : KeepAwakeSync.For(m),
            // Beskeder: ventende beskeder til brugeren (leveres igen, til agenten har kvitteret)
            Helpdesk = pendingApproval ? [] : await HelpdeskService.PendingFor(db, s, m.Id),
            // printere: null = rør ingenting (ventende tilmelding, Unraid, iPad)
            Printers = pendingApproval ? null : await Printers.PrinterService.Specs(db, m),
            // printere: søg på netværket og meld fundene, når Fleetdeck har startet en ny søgning (ikke iPads)
            PrinterSearchAt = pendingApproval || m.Platform is not (Platforms.Windows or Platforms.Linux or Platforms.Mac) ? null
                : DateTime.TryParse(await s.Get(Printers.PrinterDiscovery.SearchAtSetting), null, System.Globalization.DateTimeStyles.RoundtripKind, out var psa) ? psa : null,
        };
        // Signeret svar til agenter, der sender en nonce (ældre agenter får præcis det samme som før)
        if (mode != SignedJobsMode.Off && r.Nonce is { Length: >= 16 and <= 128 } nonce)
        {
            var signedAt = DateTime.UtcNow;
            var file = resp.AgentVersion.Length > 0 ? bin.PathFor(m.Platform, r.Arch) : null;
            resp.Signed = jobKey.Sign(new SignedCommands
            {
                MachineId = tokenId,
                Nonce = nonce, Issued = signedAt, Expires = signedAt + CommandSigning.Lifetime,
                Jobs = resp.Jobs, AgentVersion = resp.AgentVersion, AgentSha256 = file is null ? null : fileKeys.For(file).Hash,
                // agenter med pinnet nøgle flyttes til https, når en https-adresse er sat (de andre bliver, hvor de er)
                ServerUrl = r.Trust?.JobKeyPinned == true && await s.Get("agent_https_url") is { Length: > 0 } httpsUrl ? httpsUrl : resp.ServerUrl,
                TlsPin = await AgentEndpoints.TlsPin(s, tls),
                KeepRunning = resp.KeepRunning, Drives = resp.Drives, Share = resp.Share, KeepAwake = resp.KeepAwake,
                Printers = resp.Printers,
                Terminals = terminals is { Count: > 0 } ? terminals.ToList() : null,
                LiveLogs = liveLogs is { Count: > 0 } ? liveLogs.ToList() : null,
                Helpdesk = resp.Helpdesk.Count > 0 ? resp.Helpdesk : null,
            });
        }
        return resp;
    }
}
