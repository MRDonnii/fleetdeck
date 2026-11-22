using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Fleetdeck.Server.Data;
using Fleetdeck.Shared;
using Microsoft.EntityFrameworkCore;

namespace Fleetdeck.Server.Services;

/// <summary>
/// "Anmod om adgang": en maskine, der har hentet agenten og fundet serveren, men ingen nøgle har, beder administratoren om lov.
/// Kun SHA-256 af hemmeligheden gemmes. Før godkendelsen ser klienten intet (ingen profiler, maskiner eller nøgler); bagefter får den
/// en engangskode, der kun virker sammen med hemmeligheden, kun én gang og aldrig overtager en eksisterende maskine.
/// </summary>
public sealed partial class AccessRequestService(AppDb db, SettingsService settings, AuditService audit, EnrollmentService enroll, AccessRequestMemory memory)
{
    [GeneratedRegex("^[A-Z0-9][A-Z0-9_-]{0,30}$")] private static partial Regex ValidName();
    [GeneratedRegex("[^A-Z0-9_-]")] private static partial Regex Invalid();

    public const string ModeSetting = "access_requests", MaxSetting = "access_requests_max", HoursSetting = "access_requests_hours";
    public const string RateLimitPolicy = "access-request", PollRateLimitPolicy = "access-poll";
    /// <summary>Højst så mange åbne anmodninger pr. IP-adresse.</summary>
    public const int PerIp = 2;
    /// <summary>Hemmeligheden skal afsløres så hurtigt efter forpligtelsen (ellers må anmodningen startes forfra).</summary>
    public static readonly TimeSpan RevealWithin = TimeSpan.FromSeconds(60);
    /// <summary>Højst så mange notifikationer om nye anmodninger i timen; derefter én samlet besked.</summary>
    public const int NotifyPerHour = 10;
    /// <summary>En tilmelding, hvis svar gik tabt, kan hentes igen så længe (kun hvis maskinen aldrig har tjekket ind).</summary>
    public static readonly TimeSpan ReissueWithin = TimeSpan.FromDays(7);
    /// <summary>Afsluttede anmodninger gemmes så længe (vises i 7 dage).</summary>
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(30);
    private const int MaxFactsJson = 16_000;

    /// <summary>Nye anmodninger: 5 i timen pr. IP. Forespørgsler: 60 i minuttet pr. IP. IPv6 tælles pr. /64 (ellers skifter man bare adresse).</summary>
    public static void AddRateLimits(RateLimiterOptions o)
    {
        o.AddPolicy(RateLimitPolicy, ctx => RateLimitPartition.GetFixedWindowLimiter("access|" + RateKey(ctx.Connection.RemoteIpAddress),
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromHours(1) }));
        o.AddPolicy(PollRateLimitPolicy, ctx => RateLimitPartition.GetFixedWindowLimiter("access-poll|" + RateKey(ctx.Connection.RemoteIpAddress),
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1) }));
    }

    /// <summary>Nøglen, der tælles under: IPv4-adressen, IPv6 pr. /64, "socket" uden IP (socket-filen på Unraid-værten).</summary>
    public static string RateKey(IPAddress? ip)
    {
        if (ip is null) return "socket";
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily != AddressFamily.InterNetworkV6 || IPAddress.IsLoopback(ip)) return ip.ToString();
        var b = ip.GetAddressBytes();
        Array.Clear(b, 8, 8);
        return new IPAddress(b) + "/64";
    }

    /// <summary>Tager serveren imod anmodninger (og forespørgsler) herfra? Fra: aldrig. Kun hjemmenet: kun derfra.</summary>
    public async Task<bool> Accepts(bool homeNetwork) => AccessMode.Normalize(await settings.Get(ModeSetting)) switch
    {
        AccessMode.Off => false,
        AccessMode.Lan => homeNetwork,
        _ => true,
    };

    public static bool SecretMatches(string? secret, string storedHash) =>
        secret is { Length: > 0 } && storedHash.Length == 64 &&
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Crypto.Sha256(secret)), Encoding.ASCII.GetBytes(storedHash));

    /// <summary>
    /// Fingeraftrykket, serveren regner koden med. Direkte https: serverens eget certifikat (det klienten så, medmindre der er en mellemmand).
    /// Klienten brugte http: tomt. Klienten så https, men serveren fik kaldet uden (proxy foran): indstillingen tls_pin, ellers
    /// klientens påstand – så er koden ikke verificeret mod en mellemmand.
    /// </summary>
    public static (string Pin, bool Verified) ChoosePin(bool directTls, string? serverPin, string? configuredPin, string? claimedPin)
    {
        if (directTls && serverPin is { Length: > 0 }) return (serverPin, true);
        var claimed = TlsPinning.Normalize(claimedPin);
        // ingen TLS hele vejen (http): koden binder stadig agentens nøgle, men forbindelsen er ukrypteret – vises som advarsel
        if (claimed is null) return ("", false);
        if (TlsPinning.Normalize(configuredPin) is { } c) return (c, true);
        return (claimed, false);
    }

    /// <summary>Maskinnavnet som Fleetdeck bruger det (store bogstaver, andre tegn bliver til "-"). Null = ubrugeligt.</summary>
    public static string? CleanName(string? name)
    {
        var n = Invalid().Replace((name ?? "").Trim().ToUpperInvariant(), "-").TrimStart('-', '_');
        if (n.Length > 31) n = n[..31];
        return ValidName().IsMatch(n) ? n : null;
    }

    public static string NotifyText(AccessRequest a) =>
        $"🔑 Anmodning om adgang\n{a.Name} ({a.Platform}{(a.RequestedBy is { Length: > 0 } u ? $", {u}" : "")}) · kode {a.Code}. Godkend eller afvis under Maskiner.";

    /// <param name="Reason">Maskinlæsbar grund ved 403, så agenten kan forklare den: <see cref="AccessRefusal.Outside"/> ved "kun hjemmenet"
    /// og et kald udefra, <see cref="AccessRefusal.Off"/> ved slået fra – og ved en blokering, så den ligner slået fra.</param>
    public sealed record StartResult(int Status, string? Error, AccessRequestStarted? Started = null, string? Reason = null);

    /// <summary>
    /// Første skridt: forpligtelsen gemmes (kun i hukommelsen) sammen med serverens nonce. <paramref name="homeNetwork"/>: kaldet kom fra
    /// hjemmenettet/VPN (ApiKeys.FromHomeNetwork).
    /// </summary>
    public async Task<StartResult> Start(AccessRequestCreate r, string ip, bool homeNetwork, (string Pin, bool Verified) pin, DateTime? at = null)
    {
        var now = at ?? DateTime.UtcNow;
        if (!await Accepts(homeNetwork))
        {
            var mode = AccessMode.Normalize(await settings.Get(ModeSetting));
            await audit.Log(ip, "access.refused", mode == AccessMode.Off ? "anmodning om adgang er slået fra" : "anmodning udefra (kun hjemmenet og VPN)");
            return new(403, "Serveren tager ikke imod anmodninger om adgang herfra", Reason: mode == AccessMode.Lan ? AccessRefusal.Outside : AccessRefusal.Off);
        }
        if (r.Commit is not { Length: 64 } commit || !commit.All(char.IsAsciiHexDigitLower)) return new(400, "Ugyldig anmodning");
        if (CleanName(r.Name) is null) return new(400, "Ugyldigt maskinnavn");
        // blokeret af administratoren: samme svar som "slået fra", så enheden ikke kan se forskel (og ikke optager en plads)
        // (også grunden "off", så en blokeret maskine bare ser en slået-fra-server)
        if (await Blocked(ip, r.Name, AccessBlockKinds.Macs(r.Facts), now)) return new(403, "Serveren tager ikke imod anmodninger om adgang herfra", Reason: AccessRefusal.Off);
        var nonce = AccessCode.NewNonce();
        if (memory.Add(new AccessRequestMemory.Commit(r, ip, pin, nonce, now), now) is not { } handle) return new(429, "For mange anmodninger lige nu");
        return new(200, null, new AccessRequestStarted(handle, nonce, (int)RevealWithin.TotalSeconds));
    }

    /// <param name="Notice">Teksten til notifikationen (null = ingen: en erstattet anmodning eller for mange i timen).</param>
    public sealed record CreateResult(int Status, string? Error, AccessRequest? Row = null, bool Fresh = false, string? Notice = null);

    /// <summary>Andet skridt: hemmeligheden afsløres, koden regnes ud, og anmodningen oprettes hos administratoren.</summary>
    public async Task<CreateResult> Reveal(AccessReveal rv, string ip, DateTime? at = null)
    {
        var now = at ?? DateTime.UtcNow;
        if (memory.Take(rv.Handle ?? "") is not { } c) return new(404, "Anmodningen findes ikke");
        if (now - c.At > RevealWithin) return new(410, "Anmodningen tog for lang tid");
        if (c.Ip != ip) return new(404, "Anmodningen findes ikke");
        if (rv.Secret is not { Length: >= 32 and <= 128 } || !AccessSeal.ValidPublicKey(rv.PublicKey)
            || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(AccessCode.Commit(rv.Secret, rv.PublicKey)), Encoding.ASCII.GetBytes(c.Request.Commit)))
        {
            await audit.Log(ip, "access.bad_reveal", "hemmeligheden passer ikke til anmodningen");
            return new(400, "Ugyldig anmodning");
        }
        var r = c.Request;
        if (CleanName(r.Name) is not { } name) return new(400, "Ugyldigt maskinnavn");
        if (await Blocked(ip, name, AccessBlockKinds.Macs(r.Facts), now)) return new(403, "Serveren tager ikke imod anmodninger om adgang herfra");
        var hash = Crypto.Sha256(rv.Secret);
        // samme hemmelighed igen: samme anmodning
        if (await db.AccessRequests.FirstOrDefaultAsync(a => a.SecretHash == hash) is { } same)
            return AccessStates.Open(same.State) ? new(200, null, same) : new(409, "Anmodningen er afsluttet");
        await Expire(now);
        await using var tx = await db.Database.BeginTransactionAsync();
        // en ny anmodning fra samme sted og navn erstatter den gamle – og maskinens egen tidligere anmodning ("Anmod igen"), uanset navn og IP
        var older = await Open().Where(a => a.Ip == ip && a.Name == name).ToListAsync();
        if (r.Replaces is { } rep && await Open().FirstOrDefaultAsync(a => a.Id == rep.Id) is { } mine && SecretMatches(rep.Secret, mine.SecretHash)
            && older.All(o => o.Id != mine.Id))
            older.Add(mine);
        var olderIds = older.Select(o => o.Id).ToList();
        // lofterne tjekkes, før noget erstattes: en afvist anmodning må ikke koste den ventende
        var still = Open().Where(a => !olderIds.Contains(a.Id));
        if (await still.CountAsync(a => a.Ip == ip) >= PerIp) return new(429, "For mange åbne anmodninger fra denne adresse");
        if (await still.CountAsync() >= Math.Max(1, (int)await settings.GetNum(MaxSetting, 10))) return new(429, "For mange åbne anmodninger");
        foreach (var o in older) { o.State = AccessStates.Superseded; o.DecidedAt = now; }
        var hours = Math.Clamp(await settings.GetNum(HoursSetting, 24), 1, 24 * 7);
        var facts = r.Facts is null ? null : Json.Ser(r.Facts);
        var row = new AccessRequest
        {
            SecretHash = hash, Code = AccessCode.Display(rv.Secret, c.Nonce, c.Pin.Pin, rv.PublicKey), PinVerified = c.Pin.Verified,
            ObservedPin = Cut(TlsPinning.Normalize(r.ObservedPin), 64), PublicKey = rv.PublicKey,
            Name = name, Platform = r.Platform is Platforms.Linux or Platforms.Mac ? r.Platform : Platforms.Windows,
            FactsJson = facts is { Length: <= MaxFactsJson } ? facts : null,
            Os = Cut(Join(r.Facts?.Os, r.Facts?.OsVersion), 120), Model = Cut(Join(r.Facts?.Manufacturer, r.Facts?.Model), 120),
            RequestedBy = Cut(Clean(r.RequestedBy), 64), Message = Cut(Clean(r.Message), 200), Ip = Cut(ip, 45)!,
            Created = now, Expires = now.AddHours(hours), State = AccessStates.Pending,
        };
        var hourAgo = now.AddHours(-1);
        var lastHour = await db.AccessRequests.CountAsync(a => a.Created > hourAgo && a.State != AccessStates.Superseded);
        db.AccessRequests.Add(row);
        await db.SaveChangesAsync();
        if (olderIds.Count > 0) await DropCodes(olderIds);
        await tx.CommitAsync();
        await audit.Log(ip, "access.request", $"{row.Name} ({row.Platform}{(row.RequestedBy is { } u ? $", {u}" : "")}) fra {ip} · kode {row.Code}" +
            (row.PinVerified ? "" : row.ObservedPin is null ? " · ukrypteret forbindelse" : " · TLS ikke verificeret") +
            (olderIds.Count > 0 ? $" · erstatter {string.Join(", ", olderIds)}" : ""));
        // ingen notifikation, når den bare erstatter en tidligere (den er allerede meldt), og højst NotifyPerHour i timen
        var notice = olderIds.Count > 0 ? null
            : lastHour < NotifyPerHour ? NotifyText(row)
            : lastHour == NotifyPerHour ? $"🔑 Mange anmodninger om adgang den sidste time – der sendes ikke flere beskeder lige nu. Se dem under Maskiner." : null;
        return new(200, null, row, true, notice);
    }

    private IQueryable<AccessRequest> Open() =>
        db.AccessRequests.Where(a => a.State == AccessStates.Pending || a.State == AccessStates.Approved || a.State == AccessStates.Issued);

    /// <summary>Hvordan går det? Null = ukendt id eller forkert hemmelighed (samme svar, så intet kan gættes).</summary>
    public async Task<AccessPollResult?> Poll(int id, string? secret, string ip, DateTime? at = null)
    {
        var now = at ?? DateTime.UtcNow;
        var a = await db.AccessRequests.FirstOrDefaultAsync(x => x.Id == id);
        if (a is null || !SecretMatches(secret, a.SecretHash))
        {
            // højst én række i revisionsloggen pr. adresse i timen (ellers kan enhver fylde den)
            if (memory.ShouldLogBadSecret(RateKeyOf(ip), now))
                await audit.Log(ip, "access.bad_secret", $"anmodning {id}: {(a is null ? "findes ikke" : "forkert hemmelighed")} (flere fra samme adresse den næste time logges ikke)");
            return null;
        }
        a.LastPoll = now;
        if (AccessStates.Open(a.State) && a.Expires <= now)
        {
            a.State = AccessStates.Expired;
            await db.SaveChangesAsync();
            await DropCodes([a.Id]);
            return new(AccessStates.Expired);
        }
        if (a.State is AccessStates.Approved or AccessStates.Issued)
            return await Issue(a, now);
        if (a.State == AccessStates.Enrolled && await LostEnrollment(a, now) is { } machine)
        {
            // svaret på tilmeldingen nåede aldrig agenten (timeout, genstart …): en ny kode, der kun må tage netop den maskine
            a.State = AccessStates.Issued;
            if (a.Expires < now.AddHours(1)) a.Expires = now.AddHours(1);
            await audit.Log("agent", "access.reissue", $"anmodning {a.Id} · {machine.Name}: tilmeldingen nåede ikke maskinen – ny engangskode");
            return await Issue(a, now);
        }
        await db.SaveChangesAsync();
        return new(a.State, null, a.State == AccessStates.Rejected ? a.RejectReason : null);
    }

    /// <summary>
    /// Den forseglede engangskode: den samme, så længe den ikke er brugt (så en, der spørger med, ikke kan ødelægge den for maskinen),
    /// ellers en ny. Er maskinen allerede oprettet (tabt svar), må koden kun tage netop den maskine.
    /// </summary>
    private async Task<AccessPollResult> Issue(AccessRequest a, DateTime now)
    {
        var name = a.ApprovedName ?? a.Name;
        var valid = a.EnrollCodeId is int cid && a.SealedKey is { Length: > 0 }
            && await db.EnrollCodes.AnyAsync(c => c.Id == cid && c.UsedAt == null && c.Expires > now);
        if (!valid)
        {
            await DropCodes([a.Id]);
            var machine = a.MachineId is int mid ? await db.Machines.AsNoTracking().FirstOrDefaultAsync(m => m.Id == mid) : null;
            var code = await enroll.CreateCode($"Anmodning {a.Code} · {machine?.Name ?? name}", a.Expires - now, machine?.Id, a.ProfileId, a.DecidedBy ?? "admin",
                a.Id, machine?.Name ?? name, log: false);
            a.EnrollCodeId = code.Row.Id;
            a.SealedKey = AccessSeal.Seal(a.PublicKey ?? "", code.Code, AccessSeal.Context(a.Id));
        }
        a.State = AccessStates.Issued;
        await db.SaveChangesAsync();
        return new(AccessStates.Issued, a.SealedKey, null, name);
    }

    /// <summary>Maskinen, anmodningen oprettede, hvis den aldrig har tjekket ind (og det ikke er for længe siden).</summary>
    private async Task<Machine?> LostEnrollment(AccessRequest a, DateTime now) =>
        a.MachineId is int mid && a.PublicKey is { Length: > 0 }
            && await db.Machines.AsNoTracking().FirstOrDefaultAsync(m => m.Id == mid) is { } m
            && m.LastSeen == m.Enrolled && m.Enrolled > now - ReissueWithin ? m : null;

    private static string RateKeyOf(string ip) => IPAddress.TryParse(ip, out var a) ? RateKey(a) : ip;

    public sealed record Row(int Id, string Code, string Name, string Platform, string? Os, string? Model, string? RequestedBy, string? Message,
        string Ip, DateTime Created, DateTime Expires, DateTime? LastPoll, string State, int? ProfileId, string? ApprovedName, string? DecidedBy,
        DateTime? DecidedAt, string? RejectReason, int? MachineId, string? MachineName, bool PinVerified, string? Conflict, bool Unencrypted,
        List<string> Macs, string IpKey, string? IpShared);

    /// <summary>Åbne anmodninger plus dem fra de sidste 7 dage, nyeste først.</summary>
    public async Task<List<Row>> List(DateTime? at = null)
    {
        var now = at ?? DateTime.UtcNow;
        await Expire(now);
        var since = now.AddDays(-7);
        var list = await db.AccessRequests.AsNoTracking()
            .Where(a => a.State == AccessStates.Pending || a.State == AccessStates.Approved || a.State == AccessStates.Issued || a.Created > since || a.DecidedAt > since)
            .OrderByDescending(a => a.Id).Take(100).ToListAsync();
        var names = list.Select(a => a.ApprovedName ?? a.Name).Distinct().ToList();
        var taken = await db.Machines.AsNoTracking().Where(m => names.Contains(m.Name)).Select(m => m.Name).ToListAsync();
        var ids = list.Select(a => a.MachineId).OfType<int>().ToList();
        var machines = await db.Machines.AsNoTracking().Where(m => ids.Contains(m.Id)).ToDictionaryAsync(m => m.Id, m => m.DisplayName ?? m.Name);
        var ips = list.Select(a => a.Ip).Distinct().ToList();
        var machineIps = (await db.Machines.AsNoTracking().Where(m => m.Ip != null && ips.Contains(m.Ip)).Select(m => new { m.Ip, m.Name }).ToListAsync())
            .GroupBy(m => m.Ip!).ToDictionary(g => g.Key, g => g.Select(m => m.Name).Distinct().Count());
        var gateways = Gateways();
        return [.. list.Select(a => new Row(a.Id, a.Code, a.Name, a.Platform, a.Os, a.Model, a.RequestedBy, a.Message, a.Ip, a.Created, a.Expires, a.LastPoll,
            a.State, a.ProfileId, a.ApprovedName, a.DecidedBy, a.DecidedAt, a.RejectReason, a.MachineId,
            a.MachineId is int mid ? machines.GetValueOrDefault(mid) : null, a.PinVerified,
            AccessStates.Open(a.State) && taken.Contains(a.ApprovedName ?? a.Name)
                ? $"Navnet findes allerede: {a.ApprovedName ?? a.Name}. Maskinen kommer til at vente på godkendelse som erstatning eller ny " +
                  "(profilen, du vælger, gælder, hvis du beholder den som ny)." : null,
            !a.PinVerified && a.ObservedPin is null, [.. MacsOf(a).Select(AccessBlockKinds.ShowMac)], RateKeyOf(a.Ip),
            IpShared(a, list, machineIps.GetValueOrDefault(a.Ip), gateways)))];
    }

    /// <summary>
    /// Er IP'en sandsynligvis delt (proxy, router, fælles NAT/CGNAT)? Så rammer en IP-blokering alle bag den – dialogen advarer og lader IP
    /// være fravalgt. Null = intet tegn på deling.
    /// </summary>
    public static string? IpShared(AccessRequest a, IReadOnlyList<AccessRequest> recent, int machinesWithIp, IReadOnlySet<string> gateways)
    {
        if (a.Ip == "socket") return null;
        if (IPAddress.TryParse(a.Ip, out var addr))
        {
            if (addr.IsIPv4MappedToIPv6) addr = addr.MapToIPv4();
            if (TrustedProxies.IsTrusted(addr))
                return "IP'en hører til en betroet proxy (Indstillinger → Sikkerhed). Alle, der kommer gennem den, har samme IP – en blokering rammer dem alle.";
            if (gateways.Contains(addr.ToString()))
                return "IP'en er serverens router (gateway). Alle, der kommer udefra gennem den, har samme IP – en blokering rammer dem alle.";
        }
        var key = RateKeyOf(a.Ip);
        var others = recent.Where(o => o.Id != a.Id && RateKeyOf(o.Ip) == key && o.Name != a.Name).Select(o => o.Name).Distinct().Count();
        if (others > 0 || machinesWithIp > 0)
        {
            var what = new List<string>();
            if (others > 0) what.Add(others == 1 ? "1 anden anmodning" : $"{others} andre anmodninger");
            if (machinesWithIp > 0) what.Add(machinesWithIp == 1 ? "1 maskine" : $"{machinesWithIp} maskiner");
            return $"Samme IP bruges også af {string.Join(" og ", what)} – den er sandsynligvis en proxy eller en fælles router (NAT). En blokering rammer alle bag den.";
        }
        return null;
    }

    /// <summary>Serverens egne gateways (routeren, docker-broen): anmodninger derfra har routerens IP, ikke enhedens.</summary>
    private static IReadOnlySet<string> Gateways()
    {
        try
        {
            return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(n => n.GetIPProperties().GatewayAddresses).Select(g => g.Address)
                .Where(x => !x.Equals(IPAddress.Any) && !x.Equals(IPAddress.IPv6Any)).Select(x => x.ToString()).ToHashSet();
        }
        catch (System.Net.NetworkInformation.NetworkInformationException) { return new HashSet<string>(); }
    }

    public sealed record Decision(int Status, string? Error, AccessRequest? Row = null, string? Key = null);

    public async Task<Decision> Approve(int id, int? profileId, string? name, string by, DateTime? at = null)
    {
        var now = at ?? DateTime.UtcNow;
        var a = await db.AccessRequests.FirstOrDefaultAsync(x => x.Id == id);
        if (a is null) return new(404, "Anmodningen findes ikke");
        if (a.State != AccessStates.Pending) return new(400, "Anmodningen venter ikke på godkendelse");
        if (a.Expires <= now) return new(400, "Anmodningen er udløbet");
        if (profileId is not int pid) return new(400, "Vælg en profil (eller ingen profil)");
        if (pid != 0 && !await db.Profiles.AnyAsync(p => p.Id == pid)) return new(400, "Profilen findes ikke");
        string? approved = null;
        if (name is { Length: > 0 })
        {
            if (CleanName(name) is not { } n || n != name.Trim().ToUpperInvariant()) return new(400, "Ugyldigt maskinnavn (A-Z, 0-9, - og _, højst 31 tegn)");
            if (n != a.Name) approved = n;
        }
        a.State = AccessStates.Approved; a.ProfileId = pid; a.ApprovedName = approved; a.DecidedBy = by; a.DecidedAt = now;
        await db.SaveChangesAsync();
        await audit.Log(by, "access.approve", $"{a.Name}{(approved is not null ? $" som {approved}" : "")} · kode {a.Code} · profil {(pid == 0 ? "ingen" : pid)}");
        return new(200, null, a);
    }

    public async Task<Decision> Reject(int id, string? reason, string by, DateTime? at = null)
    {
        var now = at ?? DateTime.UtcNow;
        var a = await db.AccessRequests.FirstOrDefaultAsync(x => x.Id == id);
        if (a is null) return new(404, "Anmodningen findes ikke");
        if (!AccessStates.Open(a.State)) return new(400, "Anmodningen er allerede afsluttet");
        a.State = AccessStates.Rejected; a.RejectReason = Cut(Clean(reason), 200); a.DecidedBy = by; a.DecidedAt = now;
        await db.SaveChangesAsync();
        await DropCodes([a.Id]);
        await audit.Log(by, "access.reject", $"{a.Name} · kode {a.Code}{(a.RejectReason is { } r ? $": {r}" : "")}");
        return new(200, null, a);
    }

    /// <summary>"Send nøgle i stedet": en almindelig engangskode, administratoren selv giver videre. Anmodningen afsluttes.</summary>
    public async Task<Decision> SendKey(int id, int? profileId, double? hours, string by, DateTime? at = null)
    {
        var now = at ?? DateTime.UtcNow;
        var a = await db.AccessRequests.FirstOrDefaultAsync(x => x.Id == id);
        if (a is null) return new(404, "Anmodningen findes ikke");
        if (!AccessStates.Open(a.State)) return new(400, "Anmodningen er allerede afsluttet");
        if (profileId is not int pid) return new(400, "Vælg en profil (eller ingen profil)");
        if (pid != 0 && !await db.Profiles.AnyAsync(p => p.Id == pid)) return new(400, "Profilen findes ikke");
        var code = await enroll.CreateCode($"Anmodning {a.Code} · {a.Name}", TimeSpan.FromHours(hours is > 0 ? hours.Value : 24), null, pid, by);
        a.State = AccessStates.KeySent; a.ProfileId = pid; a.DecidedBy = by; a.DecidedAt = now;
        await db.SaveChangesAsync();
        await DropCodes([a.Id]);
        await audit.Log(by, "access.key_sent", $"{a.Name} · kode {a.Code} · engangskode {code.Row.Hint}…");
        return new(200, null, a, code.Code);
    }

    /// <summary>Oprydning: udløbne anmodninger, deres ubrugte koder og rækker ældre end 30 dage.</summary>
    public async Task<int> Tick(DateTime? at = null)
    {
        var now = at ?? DateTime.UtcNow;
        var n = await Expire(now);
        var old = now - KeepFor;
        await db.AccessRequests.Where(a => !(a.State == AccessStates.Pending || a.State == AccessStates.Approved || a.State == AccessStates.Issued) && a.Created < old).ExecuteDeleteAsync();
        await db.AccessBlocks.Where(b => b.Until <= now).ExecuteDeleteAsync();
        return n;
    }

    // ------------------------------------------------------------------ blokeringer

    /// <summary>Maskinens MAC-adresser fra de gemte fakta (normaliserede).</summary>
    public static List<string> MacsOf(AccessRequest a) =>
        a.FactsJson is { Length: > 0 } f ? AccessBlockKinds.Macs(Json.Des<SystemFacts>(f)) : [];

    /// <summary>Er adressen, navnet eller en af MAC-adresserne blokeret lige nu? Et ramt forsøg tælles og logges (højst én gang i timen pr. adresse).</summary>
    public async Task<bool> Blocked(string ip, string? name, IReadOnlyCollection<string> macs, DateTime now)
    {
        var keys = new List<(string Kind, string Value)> { (AccessBlockKinds.Ip, RateKeyOf(ip)) };
        if (CleanName(name) is { } n) keys.Add((AccessBlockKinds.Name, n));
        keys.AddRange(macs.Select(m => (AccessBlockKinds.Mac, m)));
        var values = keys.Select(k => k.Value).Distinct().ToList();
        var hits = (await db.AccessBlocks.Where(b => b.Until > now && values.Contains(b.Value)).ToListAsync())
            .Where(b => keys.Contains((b.Kind, b.Value))).ToList();
        if (hits.Count == 0) return false;
        foreach (var b in hits) { b.Hits++; b.LastHit = now; }
        await db.SaveChangesAsync();
        if (memory.ShouldLogBadSecret("blocked|" + RateKeyOf(ip), now))
            await audit.Log(ip, "access.blocked", $"anmodning fra {ip}{(CleanName(name) is { } nn ? $" ({nn})" : "")} afvist: blokeret " +
                string.Join(", ", hits.Select(Describe)) + " (flere fra samme adresse den næste time logges ikke)");
        return true;
    }

    public static string Describe(AccessBlock b) => b.Kind switch
    {
        AccessBlockKinds.Ip => $"IP {b.Value}",
        AccessBlockKinds.Mac => $"MAC {AccessBlockKinds.ShowMac(b.Value)}",
        _ => $"navn {b.Value}",
    };

    public sealed record BlockResult(int Status, string? Error, List<AccessBlock>? Blocks = null, int Closed = 0);

    /// <summary>
    /// "Bloker…" på en anmodning: IP-adressen (IPv6 pr. /64), og/eller MAC-adresserne og maskinnavnet, i <paramref name="hours"/> timer.
    /// Åbne anmodninger, der passer (også selve anmodningen), afvises med det samme, så de ikke optager pladserne.
    /// </summary>
    public async Task<BlockResult> Block(int requestId, bool ip, bool mac, bool name, double? hours, string? reason, string by, DateTime? at = null)
    {
        var now = at ?? DateTime.UtcNow;
        var a = await db.AccessRequests.FirstOrDefaultAsync(x => x.Id == requestId);
        if (a is null) return new(404, "Anmodningen findes ikke");
        if (!ip && !mac && !name) return new(400, "Vælg mindst én ting at blokere");
        if (hours is not double h || double.IsNaN(h) || h <= 0) return new(400, "Vælg hvor længe blokeringen skal gælde");
        var until = now.AddHours(Math.Min(h, AccessBlockKinds.MaxHours));
        var why = Cut(Clean(reason), 200);
        var wanted = new List<(string Kind, string Value)>();
        var ipKey = RateKeyOf(a.Ip);
        if (ip)
        {
            if (ipKey == "socket" || (IPAddress.TryParse(ipKey, out var addr) && IPAddress.IsLoopback(addr)))
                return new(400, "Anmodningen kom fra serverens egen vært – den adresse kan ikke blokeres");
            wanted.Add((AccessBlockKinds.Ip, ipKey));
        }
        var macs = MacsOf(a);
        if (mac)
        {
            if (macs.Count == 0) return new(400, "Anmodningen har ingen MAC-adresse");
            wanted.AddRange(macs.Select(m => (AccessBlockKinds.Mac, m)));
        }
        if (name) wanted.Add((AccessBlockKinds.Name, a.Name));
        var values = wanted.Select(w => w.Value).ToList();
        var existing = await db.AccessBlocks.Where(b => b.Until > now && values.Contains(b.Value)).ToListAsync();
        var blocks = new List<AccessBlock>();
        foreach (var (kind, value) in wanted)
        {
            if (existing.FirstOrDefault(b => b.Kind == kind && b.Value == value) is { } old)
            {
                // findes allerede: forlæng (aldrig afkort) og opdatér begrundelsen
                if (until > old.Until) old.Until = until;
                old.Reason = why ?? old.Reason; old.CreatedBy = Cut(by, 64)!;
                blocks.Add(old);
                continue;
            }
            var b = new AccessBlock { Kind = kind, Value = value, Created = now, Until = until, CreatedBy = Cut(by, 64)!, Reason = why, AccessRequestId = a.Id };
            db.AccessBlocks.Add(b);
            blocks.Add(b);
        }
        // åbne anmodninger, der passer, afvises nu (ellers blev de ved med at optage pladserne til de udløb)
        var open = await Open().ToListAsync();
        var closed = open.Where(o => o.Id == a.Id
            || (ip && RateKeyOf(o.Ip) == ipKey)
            || (name && o.Name == a.Name)
            || (mac && MacsOf(o).Intersect(macs).Any())).ToList();
        foreach (var o in closed)
        {
            o.State = AccessStates.Rejected; o.RejectReason = why ?? "Blokeret af administratoren"; o.DecidedBy = by; o.DecidedAt = now;
        }
        await db.SaveChangesAsync();
        if (closed.Count > 0) await DropCodes(closed.Select(o => o.Id));
        await audit.Log(by, "access.block", $"{string.Join(", ", blocks.Select(Describe))} til {until:yyyy-MM-dd HH:mm} UTC (fra anmodning {a.Id}, {a.Name} · kode {a.Code})" +
            (why is { } w ? $": {w}" : "") + (closed.Count > 0 ? $" · afviste {string.Join(", ", closed.Select(o => $"{o.Name} ({o.Id})"))}" : ""));
        return new(200, null, blocks, closed.Count);
    }

    public sealed record BlockRow(int Id, string Kind, string Value, string Label, DateTime Created, DateTime Until, string CreatedBy, string? Reason,
        int? AccessRequestId, string? RequestName, int Hits, DateTime? LastHit);

    /// <summary>Blokeringer, der gælder nu (de længste først).</summary>
    public async Task<List<BlockRow>> Blocks(DateTime? at = null)
    {
        var now = at ?? DateTime.UtcNow;
        var list = await db.AccessBlocks.AsNoTracking().Where(b => b.Until > now).OrderByDescending(b => b.Until).ThenByDescending(b => b.Id).Take(200).ToListAsync();
        var ids = list.Select(b => b.AccessRequestId).OfType<int>().Distinct().ToList();
        var names = await db.AccessRequests.AsNoTracking().Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Name);
        return [.. list.Select(b => new BlockRow(b.Id, b.Kind, b.Kind == AccessBlockKinds.Mac ? AccessBlockKinds.ShowMac(b.Value) : b.Value, Describe(b),
            b.Created, b.Until, b.CreatedBy, b.Reason, b.AccessRequestId, b.AccessRequestId is int i ? names.GetValueOrDefault(i) : null, b.Hits, b.LastHit))];
    }

    public async Task<bool> Unblock(int id, string by)
    {
        var b = await db.AccessBlocks.FirstOrDefaultAsync(x => x.Id == id);
        if (b is null) return false;
        db.AccessBlocks.Remove(b);
        await db.SaveChangesAsync();
        await audit.Log(by, "access.unblock", $"{Describe(b)} ophævet (afviste {b.Hits} forsøg)");
        return true;
    }

    private async Task<int> Expire(DateTime now)
    {
        var gone = await db.AccessRequests.Where(a => (a.State == AccessStates.Pending || a.State == AccessStates.Approved || a.State == AccessStates.Issued) && a.Expires <= now).ToListAsync();
        if (gone.Count == 0) return 0;
        foreach (var a in gone) a.State = AccessStates.Expired;
        await db.SaveChangesAsync();
        await DropCodes(gone.Select(a => a.Id));
        return gone.Count;
    }

    /// <summary>Ubrugte engangskoder, der er udstedt til anmodningerne, slettes (brugte bliver stående i historikken).</summary>
    private Task<int> DropCodes(IEnumerable<int> ids)
    {
        var list = ids.ToList();
        return db.EnrollCodes.Where(c => c.AccessRequestId != null && list.Contains(c.AccessRequestId.Value) && c.UsedAt == null).ExecuteDeleteAsync();
    }

    private static string? Join(string? a, string? b) => string.Join(" ", new[] { a, b }.Where(x => x is { Length: > 0 })) is { Length: > 0 } s ? s : null;
    private static string? Clean(string? s) => s is null ? null : new string([.. s.Where(c => !char.IsControl(c))]).Trim() is { Length: > 0 } t ? t : null;
    private static string? Cut(string? s, int max) => s is null ? null : s.Length > max ? s[..max] : s;
}

/// <summary>
/// "Anmod om adgang" i hukommelsen (singleton): forpligtelser, der venter på hemmeligheden (højst et minut), og hvornår et forkert
/// kald fra en adresse sidst blev skrevet i revisionsloggen.
/// </summary>
public sealed class AccessRequestMemory
{
    public sealed record Commit(AccessRequestCreate Request, string Ip, (string Pin, bool Verified) Pin, string Nonce, DateTime At);

    /// <summary>Højst så mange forpligtelser ad gangen (hver adresse kan højst lave 5 i timen).</summary>
    public const int MaxCommits = 200;
    private readonly ConcurrentDictionary<string, Commit> _commits = new();
    private readonly ConcurrentDictionary<string, DateTime> _badLogged = new();

    /// <summary>Gemmer forpligtelsen og returnerer håndtaget (null = for mange lige nu).</summary>
    public string? Add(Commit c, DateTime now)
    {
        foreach (var (k, v) in _commits) if (now - v.At > AccessRequestService.RevealWithin) _commits.TryRemove(k, out _);
        if (_commits.Count >= MaxCommits) return null;
        var handle = Crypto.Token(24);
        _commits[handle] = c;
        return handle;
    }

    /// <summary>Henter og fjerner (et håndtag kan kun bruges én gang).</summary>
    public Commit? Take(string handle) => _commits.TryRemove(handle, out var c) ? c : null;

    /// <summary>Første forkerte kald fra adressen i denne time?</summary>
    public bool ShouldLogBadSecret(string key, DateTime now)
    {
        if (_badLogged.Count > 10_000) foreach (var (k, v) in _badLogged) if (now - v > TimeSpan.FromHours(1)) _badLogged.TryRemove(k, out _);
        lock (_badLogged)
        {
            if (_badLogged.TryGetValue(key, out var last) && now - last <= TimeSpan.FromHours(1)) return false;
            _badLogged[key] = now;
            return true;
        }
    }
}
