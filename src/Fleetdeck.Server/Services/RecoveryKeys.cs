using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Fleetdeck.Server.Data;
using Fleetdeck.Shared;

namespace Fleetdeck.Server.Services;

/// <summary>Det, brugerfladen må se om en gemt nøgle – aldrig selve nøglen.</summary>
public sealed record RecoveryKeyView(int Id, string Kind, string Volume, string ProtectorId, DateTime Created, DateTime LastSeen, DateTime? Superseded, string Source);

/// <summary>
/// Gendannelsesnøgler til diskkryptering: gemt krypteret med data protection, med historik (gamle nøgler bevares som udskiftet).
/// Nøglen kommer kun ud igen via <see cref="Reveal"/>: administrator, nylig bekræftelse, audit og besked.
/// </summary>
public sealed class RecoveryKeyVault(AppDb db, IDataProtectionProvider dp, TimeProvider clock)
{
    public const string Purpose = "Fleetdeck.RecoveryKey";
    public const string AuditEscrow = "recovery_key.escrow", AuditReveal = "recovery_key.reveal", AuditDenied = "recovery_key.denied",
        AuditRotate = "recovery_key.rotate";
    private readonly IDataProtector protector = dp.CreateProtector(Purpose);
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public sealed record StoreResult(int Added, int Confirmed, int Superseded, int Rejected);

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gem agentens nøgler. Samme nøgle igen = kun "set". Ny nøgle på samme beskytter = den gamle udskiftes.
    /// Full: nøgler af samme slags, som maskinen ikke længere har, markeres udskiftet (de slettes aldrig).
    /// </summary>
    public async Task<StoreResult> Store(int machineId, RecoveryKeyPost post)
    {
        var now = Now;
        var items = post.Keys.Take(64).ToList();
        var valid = items.Where(RecoveryKeys.Valid).ToList();
        var current = await db.RecoveryKeys.Where(k => k.MachineId == machineId && k.Superseded == null).ToListAsync();
        var keep = new HashSet<RecoveryKey>();
        int added = 0, confirmed = 0, superseded = 0;
        foreach (var it in valid)
        {
            var vol = it.Volume.Trim();
            var same = current.FirstOrDefault(c => c.Superseded is null && c.Kind == it.Kind && Same(c.Volume, vol) && Same(c.ProtectorId, it.ProtectorId));
            if (same is not null && Unprotect(same) == it.Key) { same.LastSeen = now; keep.Add(same); confirmed++; continue; }
            if (same is not null) { same.Superseded = now; superseded++; }
            var row = new RecoveryKey
            {
                MachineId = machineId, Kind = it.Kind, Volume = vol, ProtectorId = it.ProtectorId.Trim(), Protected = protector.Protect(it.Key),
                Created = now, LastSeen = now, Source = post.JobId is null ? "agent" : "rotate", JobId = post.JobId,
            };
            db.RecoveryKeys.Add(row);
            keep.Add(row);
            added++;
        }
        var kinds = valid.Select(v => v.Kind).ToHashSet();
        foreach (var c in current.Where(c => c.Superseded is null && !keep.Contains(c)))
        {
            // fuld melding: alt af samme slags, der ikke er med, er væk fra maskinen. Ny nøgle (FileVault): den gamle på drevet er udskiftet.
            var gone = post.Full ? kinds.Contains(c.Kind) : valid.Any(v => v.Kind == c.Kind && Same(v.Volume.Trim(), c.Volume));
            if (gone) { c.Superseded = now; superseded++; }
        }
        await db.SaveChangesAsync();
        return new(added, confirmed, superseded, items.Count - valid.Count);
    }

    /// <summary>Hash af de gældende nøgler (samme beregning som agentens), så serveren kan se, om den mangler nogen.</summary>
    public async Task<string?> CurrentHash(int machineId, string kind)
    {
        var rows = await db.RecoveryKeys.AsNoTracking().Where(k => k.MachineId == machineId && k.Kind == kind && k.Superseded == null).ToListAsync();
        var items = new List<RecoveryKeyItem>();
        foreach (var r in rows)
            if (Unprotect(r) is { } key) items.Add(new RecoveryKeyItem { Kind = r.Kind, Volume = r.Volume, ProtectorId = r.ProtectorId, Key = key });
        return RecoveryKeys.Hash(items);
    }

    public async Task<List<RecoveryKeyView>> List(int machineId) =>
        (await db.RecoveryKeys.AsNoTracking().Where(k => k.MachineId == machineId).ToListAsync())
        .OrderBy(k => k.Superseded is null ? 0 : 1).ThenBy(k => k.Volume).ThenByDescending(k => k.Created)
        .Select(View).ToList();

    public static RecoveryKeyView View(RecoveryKey k) => new(k.Id, k.Kind, k.Volume, k.ProtectorId, k.Created, k.LastSeen, k.Superseded, k.Source);

    private string? Unprotect(RecoveryKey k)
    {
        try { return protector.Unprotect(k.Protected); }
        catch (CryptographicException) { return null; }
    }

    public sealed record RevealResult(int Status, string? Error, string? Key = null, RecoveryKeyView? View = null, bool NeedsReauth = false);

    /// <summary>
    /// Vis én nøgle: kun administrator, kun med en bekræftelse (adgangskode/kode) fra de sidste 5 minutter.
    /// Alle forsøg skrives i audit; et vellykket sender en besked ("Gendannelsesnøgle vist for X af Y").
    /// </summary>
    public async Task<RevealResult> Reveal(ClaimsPrincipal p, int machineId, int keyId, string? reauth, ReauthTokens tokens,
        Func<string, string, string, int?, Task> audit, Action<string> notify)
    {
        var uid = int.TryParse(p.FindFirstValue(ClaimTypes.NameIdentifier), out var i) ? i : 0;
        var u = uid > 0 ? await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == uid) : null;
        var who = u?.Username ?? p.Identity?.Name ?? "?";
        var k = await db.RecoveryKeys.Include(x => x.Machine).FirstOrDefaultAsync(x => x.Id == keyId && x.MachineId == machineId);
        var what = k is null ? $"nøgle #{keyId}" : $"{k.Machine?.Label}: {Name(k.Kind)} {k.Volume} (nøgle #{k.Id}{(k.Superseded is null ? "" : ", tidligere")})";
        async Task<RevealResult> Deny(int status, string msg, string reason, bool reauthNeeded = false)
        {
            await audit(who, AuditDenied, $"{what}: {reason}", k?.MachineId ?? machineId);
            return new(status, msg, NeedsReauth: reauthNeeded);
        }
        if (u is null || u.Role != Roles.Admin) return await Deny(403, "Kun administratorer kan se gendannelsesnøgler", $"rolle {u?.Role ?? "ukendt"}");
        if (!tokens.Valid(reauth, u)) return await Deny(428, "Bekræft med din adgangskode eller en kode fra appen først", "ingen bekræftelse inden for 5 minutter", true);
        if (k is null) return await Deny(404, "Nøglen findes ikke", "findes ikke");
        if (Unprotect(k) is not { } key) return await Deny(500, "Nøglen kan ikke dekrypteres. Er serverens nøgler (keys-mappen) gået tabt?", "kan ikke dekrypteres");
        await audit(u.Username, AuditReveal, what, k.MachineId);
        notify($"🔑 Gendannelsesnøgle vist for {k.Machine?.Label} af {u.Username}\n{Name(k.Kind)} {k.Volume}{(k.Superseded is null ? "" : " (tidligere nøgle)")}");
        return new(200, null, key, View(k));
    }

    public static string Name(string kind) => kind switch { EncryptionKinds.BitLocker => "BitLocker", EncryptionKinds.FileVault => "FileVault", EncryptionKinds.Luks => "LUKS", _ => kind };

    /// <summary>Alarmer: systemdisken er ikke krypteret på en bærbar (info), og krypteret uden nøgle i Fleetdeck (advarsel).</summary>
    public static IEnumerable<Finding> Findings(EncryptionInfo? e, IReadOnlyCollection<(string Kind, string Volume)> escrowed)
    {
        if (e is null || e.Volumes.Count == 0) yield break;
        var sys = e.Volumes.FirstOrDefault(v => v.System);
        if (e.Portable == true && sys is not null && sys.Protected != true)
        {
            var detail = e.Kind switch
            {
                EncryptionKinds.BitLocker when sys.Encrypted == true && sys.Status is "FullyEncrypted" => $"BitLocker er sat på pause på {sys.Mount}, så nøglen ligger ubeskyttet på disken.",
                EncryptionKinds.BitLocker when sys.Status is "EncryptionInProgress" => $"BitLocker er ved at kryptere {sys.Mount} ({sys.Percent:0} %).",
                EncryptionKinds.BitLocker => $"BitLocker er slået fra på {sys.Mount}. Bliver den bærbare stjålet, kan alle filer læses.",
                EncryptionKinds.FileVault => "FileVault er slået fra. Bliver Mac'en stjålet, kan alle filer læses.",
                _ => "/ ligger ikke på en krypteret disk (LUKS). Bliver den bærbare stjålet, kan alle filer læses.",
            };
            yield return new("encryption", "info", "Systemdisken er ikke krypteret", detail);
        }
        if (!EncryptionKinds.Escrowable(e.Kind)) yield break;
        var set = escrowed.Select(x => (x.Kind, x.Volume.ToUpperInvariant())).ToHashSet();
        var missing = e.Volumes.Where(v => v.Protected == true && !set.Contains((e.Kind, v.Mount.ToUpperInvariant()))).ToList();
        if (missing.Count == 0) yield break;
        yield return e.Kind == EncryptionKinds.FileVault
            ? new("escrow", "warning", "Gendannelsesnøgle mangler i Fleetdeck",
                "FileVault er slået til, men Fleetdeck har ikke gendannelsesnøglen. Brug \"Ny gendannelsesnøgle til Fleetdeck\" under Kryptering på maskinens side.")
            : new("escrow", "warning", "Gendannelsesnøgle mangler i Fleetdeck",
                $"{string.Join(", ", missing.Select(v => v.Mount))} er krypteret med BitLocker, men Fleetdeck har ikke nøglen" +
                (missing.Any(v => v.RecoveryKeys == 0) ? " – drevet har ingen numerisk gendannelsesadgangskode." : ". Agenten sender den ved næste helbredstjek."));
    }
}

/// <summary>
/// Nylig bekræftelse ("skriv din adgangskode igen"): et kortlivet bevis bundet til brugeren og dennes SecurityStamp.
/// Bruges før følsomme visninger som gendannelsesnøgler.
/// </summary>
public sealed class ReauthTokens(IDataProtectionProvider dp, TimeProvider clock)
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
    private readonly IDataProtector protector = dp.CreateProtector("Fleetdeck.Reauth");

    public (string Token, DateTime Expires) Issue(User u)
    {
        var now = clock.GetUtcNow();
        return (protector.Protect($"{u.Id}|{u.SecurityStamp}|{now.ToUnixTimeMilliseconds()}"), (now + Window).UtcDateTime);
    }

    public bool Valid(string? token, User u)
    {
        if (string.IsNullOrEmpty(token)) return false;
        try
        {
            if (protector.Unprotect(token).Split('|') is not [var id, var stamp, var ms] || !long.TryParse(ms, out var t)) return false;
            var age = clock.GetUtcNow() - DateTimeOffset.FromUnixTimeMilliseconds(t);
            return id == u.Id.ToString() && stamp == u.SecurityStamp && age <= Window && age >= TimeSpan.FromMinutes(-1);
        }
        catch (CryptographicException) { return false; }
    }
}
