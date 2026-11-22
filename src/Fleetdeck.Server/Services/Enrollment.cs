using System.Text.RegularExpressions;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Hubs;
using Fleetdeck.Shared;
using Microsoft.EntityFrameworkCore;

namespace Fleetdeck.Server.Services;

/// <summary>
/// Tilmelding af agenter. Den globale nøgle (eller en engangskode) giver adgang til at tilmelde en NY maskine –
/// aldrig til at overtage en eksisterende maskines identitet (token, jobs, gendannelsessæt, backup).
/// En eksisterende maskine overtages kun, når tilmeldingen beviser, at det er den samme maskine (dens gamle token),
/// når en administrator har sagt god for det på forhånd (engangskode til netop den maskine eller en aktiv geninstallation fra netværket),
/// eller når maskinen aldrig har haft et token. Ellers bliver det en ny, ventende maskine, som en administrator
/// godkender som erstatning ("Godkend som erstatning for X") eller afviser.
/// </summary>
public sealed partial class EnrollmentService(AppDb db, SettingsService settings, AuditService audit)
{
    [GeneratedRegex("^[A-Z0-9][A-Z0-9_-]{0,30}$")] private static partial Regex ValidName();

    public const string CodePrefix = "fd-";
    public static class Methods { public const string Key = "key", Code = "code", Token = "token", Deploy = "deploy", New = "new"; }

    public enum KeyKind { Invalid, Global, Code }
    public sealed record KeyCheck(KeyKind Kind, EnrollCode? Code);

    /// <summary>
    /// Er nøglen den globale nøgle (hvis den er slået til) eller en gyldig, ubrugt engangskode? Bruger ikke koden.
    /// En kode udstedt til en "Anmod om adgang" er kun gyldig sammen med anmodningens hemmelighed (<paramref name="accessSecret"/>).
    /// </summary>
    public async Task<KeyCheck> CheckKey(string? key, DateTime? now = null, string? accessSecret = null)
    {
        key = (key ?? "").Trim();
        if (key.Length == 0) return new(KeyKind.Invalid, null);
        if (key.StartsWith(CodePrefix, StringComparison.Ordinal))
        {
            var hash = Crypto.Sha256(key);
            var t = now ?? DateTime.UtcNow;
            var c = await db.EnrollCodes.AsNoTracking().FirstOrDefaultAsync(x => x.CodeHash == hash);
            if (c is null || c.UsedAt is not null || c.Expires <= t) return new(KeyKind.Invalid, null);
            if (c.AccessRequestId is int arId)
            {
                var stored = await db.AccessRequests.AsNoTracking().Where(a => a.Id == arId).Select(a => a.SecretHash).FirstOrDefaultAsync();
                if (stored is null || !AccessRequestService.SecretMatches(accessSecret, stored)) return new(KeyKind.Invalid, null);
            }
            return new(KeyKind.Code, c);
        }
        if (await settings.Get("enroll_key_enabled") == "0") return new(KeyKind.Invalid, null);
        return Crypto.FixedEquals(key, await settings.Get("enroll_key")) ? new(KeyKind.Global, null) : new(KeyKind.Invalid, null);
    }

    public sealed record Created(string Code, EnrollCode Row);

    /// <summary>Ny engangskode (vises kun én gang).</summary>
    public async Task<Created> CreateCode(string label, TimeSpan valid, int? replaceMachineId, int? profileId, string by, int? accessRequestId = null, string? name = null, bool log = true)
    {
        var code = CodePrefix + Crypto.Token(15);
        var row = new EnrollCode
        {
            CodeHash = Crypto.Sha256(code), Hint = code[..7], Label = label.Trim() is { Length: > 100 } l ? l[..100] : label.Trim(),
            ReplaceMachineId = replaceMachineId, ProfileId = profileId, CreatedBy = by, AccessRequestId = accessRequestId, Name = name,
            Expires = DateTime.UtcNow + (valid < TimeSpan.FromMinutes(5) ? TimeSpan.FromMinutes(5) : valid > TimeSpan.FromDays(30) ? TimeSpan.FromDays(30) : valid),
        };
        db.EnrollCodes.Add(row);
        await db.SaveChangesAsync();
        if (log) await audit.Log(by, "enroll.code_create", $"{row.Hint}… {row.Label}{(replaceMachineId is int r ? $" (geninstallation af maskine {r})" : "")}, udløber {row.Expires:yyyy-MM-dd HH:mm} UTC");
        return new(code, row);
    }

    /// <param name="Repeat">En gentaget tilmelding af en maskine, der allerede er oprettet, men aldrig fik svaret (anmod om adgang): intet skal køres igen.</param>
    public sealed record Outcome(int Status, string? Error, Machine? Machine = null, string? Secret = null, bool Pending = false, Machine? Existing = null, string Method = "",
        bool Repeat = false)
    {
        public bool Ok => Status == 200;
    }

    /// <summary>Selve beslutningen. Kalderen står for udvidelser, udrulning, genskabelse og live-opdatering bagefter.</summary>
    public async Task<Outcome> Enroll(EnrollRequest r, DateTime? at = null)
    {
        var now = at ?? DateTime.UtcNow;
        var key = await CheckKey(r.Key, now, r.AccessSecret);
        if (key.Kind == KeyKind.Invalid) return new(403, "Forkert tilmeldingsnøgle");
        var access = key.Code?.AccessRequestId;
        // anmodning om adgang, hvis svar gik tabt: koden må kun tage netop den maskine, anmodningen oprettede
        Machine? again = null;
        if (access is not null && key.Code!.ReplaceMachineId is int rid && (again = await db.Machines.FirstOrDefaultAsync(x => x.Id == rid)) is null)
            return new(403, "Maskinen findes ikke længere");
        // en anmodning om adgang: navnet, administratoren godkendte, gælder (ikke det, agenten sender nu)
        var name = again?.Name ?? (key.Code?.Name is { Length: > 0 } approved ? approved : r.Name ?? "").Trim().ToUpperInvariant();
        if (again is null && !ValidName().IsMatch(name)) return new(400, "Ugyldigt maskinnavn");
        // engangskoden bruges med det samme (atomisk), så den ikke kan bruges to gange samtidig
        if (key.Code is { } code)
        {
            var used = await db.EnrollCodes.Where(x => x.Id == code.Id && x.UsedAt == null)
                .ExecuteUpdateAsync(x => x.SetProperty(c => c.UsedAt, now));
            if (used == 0) return new(403, "Engangskoden er allerede brugt");
        }
        var platform = r.Platform is Platforms.Linux or Platforms.Mac ? r.Platform : Platforms.Windows;
        var existing = again ?? await db.Machines.FirstOrDefaultAsync(x => x.Name == name);
        var method = key.Kind == KeyKind.Code ? Methods.Code : Methods.Key;
        Machine m;
        var pending = false;
        if (again is not null)
        {
            // samme maskine igen (har aldrig tjekket ind): kun et nyt token; venter den på godkendelse, gør den det stadig
            m = again; pending = again.ReplacesId is not null;
            if (pending) existing = await db.Machines.FirstOrDefaultAsync(x => x.Id == again.ReplacesId);
        }
        else if (existing is null)
        {
            m = new Machine { Name = name, Platform = platform };
            db.Machines.Add(m);
        }
        else if (await Proven(existing, r, key.Code, now) is { } how)
        {
            // Geninstalleret maskine, som beviseligt er den samme: behold profil og historik, nulstil inventar og afbryd gamle jobs
            m = existing; method = how;
            await db.Software.Where(x => x.MachineId == m.Id).ExecuteDeleteAsync();
            await db.WingetIds.Where(x => x.MachineId == m.Id).ExecuteDeleteAsync();
            await db.Upgrades.IgnoreQueryFilters().Where(x => x.MachineId == m.Id).ExecuteDeleteAsync();
            await db.Jobs.Where(j => j.MachineId == m.Id && JobStatus.Active.Contains(j.Status))
                .ExecuteUpdateAsync(x => x.SetProperty(j => j.Status, JobStatus.Cancelled).SetProperty(j => j.Finished, now));
        }
        else
        {
            // Navnet er taget, og intet beviser, at det er samme maskine: ny, ventende maskine. Intet fra den gamle følger med.
            m = new Machine { Name = await PendingName(name), Platform = platform, ReplacesId = existing.Id, PendingProvision = r.Provision };
            db.Machines.Add(m);
            pending = true;
        }
        m.Platform = platform;
        // Profilen, der blev valgt ved installationen (0 = bevidst ingen profil); en engangskode kan også have en profil.
        // Venter maskinen på godkendelse, gemmes valget til "Godkend som ny".
        var pid = access is not null ? key.Code!.ProfileId : r.ProfileId ?? key.Code?.ProfileId;
        if (pending) m.PendingProfileId = pid ?? m.PendingProfileId;
        else if (pid == 0) m.ProfileId = null;
        else if (pid is int p && await db.Profiles.AnyAsync(x => x.Id == p)) m.ProfileId = p;
        var secret = Crypto.Token();
        m.TokenHash = Crypto.Sha256(secret);
        m.TokenAlias = null;
        m.EnrollMethod = method;
        m.JobTrust = null; m.TrustError = null; m.TrustErrorAt = null; m.TlsPinned = false;
        m.Enrolled = now; m.LastSeen = now; m.LastInventory = null; m.InventoryRequested = true;
        if (r.Facts is not null) m.FactsJson = Json.Ser(r.Facts);
        await db.SaveChangesAsync();
        if (key.Code is { } usedCode)
            await db.EnrollCodes.Where(x => x.Id == usedCode.Id).ExecuteUpdateAsync(x => x.SetProperty(c => c.UsedByMachineId, m.Id));
        if (access is int aid)
        {
            if (await db.AccessRequests.FirstOrDefaultAsync(a => a.Id == aid) is { } req)
            {
                req.State = AccessStates.Enrolled; req.MachineId = m.Id;
                await db.SaveChangesAsync();
            }
            await audit.Log("agent", "access.enrolled", $"anmodning {aid} → {m.Name}{(again is not null ? " (igen: svaret nåede ikke maskinen)" : "")}" +
                (pending ? " (venter på godkendelse: navnet findes allerede)" : ""));
            if (again is not null) return new(200, null, m, secret, pending, existing, Methods.Code, Repeat: true);
        }
        if (pending)
            await audit.Log("agent", "agent.enroll_pending", $"{name}: navnet findes allerede – venter på godkendelse som {m.Name} ({method})");
        else
            await audit.Log("agent", "agent.enroll", $"{name}{(r.Provision ? " (nyinstallation)" : "")} · {method}{(existing is not null ? " · overtog eksisterende maskine" : "")}");
        return new(200, null, m, secret, pending, existing, method);
    }

    /// <summary>Hvordan tilmeldingen beviser, at den må overtage den eksisterende maskine (null = det gør den ikke).</summary>
    private async Task<string?> Proven(Machine existing, EnrollRequest r, EnrollCode? code, DateTime now)
    {
        var tokenProof = r.PreviousToken is { Length: > 0 } prev && await AgentAuth.Resolve(db, prev) is { } same && same.Id == existing.Id;
        // "Anmod om adgang": kun maskinens eget gamle token beviser noget – aldrig en forberedt maskine eller en igangværende geninstallation
        if (code?.AccessRequestId is not null) return tokenProof ? Methods.Token : null;
        if (existing.TokenHash is null) return Methods.New;   // har aldrig haft en agent
        if (tokenProof) return Methods.Token;
        if (code?.ReplaceMachineId == existing.Id) return Methods.Code;
        // "Geninstallér fra netværket", startet af en administrator for netop denne maskine
        if (await db.Deployments.AnyAsync(d => d.MachineId == existing.Id && DeployStatus.Active.Contains(d.Status))) return Methods.Deploy;
        return null;
    }

    private async Task<string> PendingName(string name)
    {
        for (var i = 2; ; i++)
        {
            var n = $"{name}~{i}";
            if (!await db.Machines.AnyAsync(x => x.Name == n)) return n;
        }
    }

    /// <summary>
    /// Godkend en ventende maskine som erstatning for den, den har navn fra: den gamle post (profil, historik, gendannelsessæt,
    /// backup) får den nye agents token, og den ventende post slettes. Returnerer den gamle maskine.
    /// </summary>
    public async Task<(Machine? Target, string? Error)> ApproveReplacement(int pendingId, string by)
    {
        var p = await db.Machines.FirstOrDefaultAsync(x => x.Id == pendingId);
        if (p is null) return (null, "Maskinen findes ikke");
        if (p.ReplacesId is not int tid || await db.Machines.FirstOrDefaultAsync(x => x.Id == tid) is not { } t)
            return (null, "Maskinen venter ikke på godkendelse som erstatning");
        var now = DateTime.UtcNow;
        await db.Software.Where(x => x.MachineId == t.Id).ExecuteDeleteAsync();
        await db.WingetIds.Where(x => x.MachineId == t.Id).ExecuteDeleteAsync();
        await db.Upgrades.IgnoreQueryFilters().Where(x => x.MachineId == t.Id).ExecuteDeleteAsync();
        await db.Jobs.Where(j => j.MachineId == t.Id && JobStatus.Active.Contains(j.Status))
            .ExecuteUpdateAsync(x => x.SetProperty(j => j.Status, JobStatus.Cancelled).SetProperty(j => j.Finished, now));
        t.TokenHash = p.TokenHash; t.TokenAlias = p.TokenAlias ?? p.Id;
        t.Platform = p.Platform; t.FactsJson = p.FactsJson; t.HealthJson = p.HealthJson;
        t.AgentVersion = p.AgentVersion; t.Ip = p.Ip; t.Mac = p.Mac; t.User = p.User; t.LastSeen = p.LastSeen; t.OnlineSince = p.OnlineSince;
        t.JobTrust = p.JobTrust; t.TlsPinned = p.TlsPinned; t.Transport = p.Transport; t.TrustError = null; t.TrustErrorAt = null;
        t.Enrolled = now; t.EnrollMethod = "approved"; t.LastInventory = null; t.LastHealth = null; t.InventoryRequested = true;
        var provision = p.PendingProvision;
        // gendannelsesnøgler, den nye installation allerede har sendt, følger med (de slettes ellers med den ventende maskine)
        await db.RecoveryKeys.Where(k => k.MachineId == p.Id).ExecuteUpdateAsync(x => x.SetProperty(k => k.MachineId, t.Id));
        db.Machines.Remove(p);
        await db.SaveChangesAsync();
        await audit.Log(by, "machine.approve_replacement", $"{p.Name} godkendt som erstatning for {t.Label}{(provision ? " (genskabes fra gendannelsessæt)" : "")}");
        return (t, null);
    }

    /// <summary>Behold den ventende maskine som en selvstændig, ny maskine (den gamle røres ikke).</summary>
    public async Task<(Machine? Machine, string? Error)> ApproveAsNew(int pendingId, string by)
    {
        var p = await db.Machines.FirstOrDefaultAsync(x => x.Id == pendingId);
        if (p?.ReplacesId is null) return (null, "Maskinen venter ikke på godkendelse");
        p.ReplacesId = null; p.PendingProvision = false;
        // profilen fra installationen eller godkendelsen af anmodningen (0 = bevidst ingen)
        if (p.PendingProfileId is int pid && pid != 0 && await db.Profiles.AnyAsync(x => x.Id == pid)) p.ProfileId = pid;
        p.PendingProfileId = null;
        await db.SaveChangesAsync();
        await audit.Log(by, "machine.approve_new", $"{p.Name} godkendt som ny maskine{(p.ProfileId is int pp ? $" · profil {pp}" : "")}");
        return (p, null);
    }
}

/// <summary>Agentens melding om tillid ved check-in, gemt på maskinen (vises i Fleetdeck).</summary>
public static class AgentTrust
{
    public const string Pinned = "pinned", Unpinned = "unpinned", Mismatch = "mismatch";

    public static void Record(Machine m, CheckinRequest r, bool https, bool socket, string ourKeyId, DateTime now)
    {
        m.Transport = socket ? "socket" : https ? "https" : "http";
        if (r.Trust is not { } t) { m.JobTrust = null; m.TlsPinned = false; return; }   // ældre agent uden signerede jobs
        m.JobTrust = !t.JobKeyPinned ? Unpinned : t.JobKeyId is { Length: > 0 } id && id != ourKeyId ? Mismatch : Pinned;
        m.TlsPinned = t.TlsPinned;
        if (t.Error is { Length: > 0 } e) { m.TrustError = e.Length > 300 ? e[..300] : e; m.TrustErrorAt = now; }
        else m.TrustError = null;
    }
}
