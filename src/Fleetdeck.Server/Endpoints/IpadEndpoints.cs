using System.Security.Claims;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Services;
using Fleetdeck.Shared;
using Microsoft.EntityFrameworkCore;

namespace Fleetdeck.Server.Endpoints;

public sealed record IpadCreate(string Name, string? Kind, string? Owner, string? Model, string? DeviceType = null, string? LanIp = null);
public sealed record IpadInput(string? Name, string? Kind, string? Owner, string? Model, string? LanIp, string? Schedule, List<string>? BlockedServices, List<string>? AllowedDomains,
    List<string>? BlockedDomains, bool? FilterAdult, bool? SafeSearch, string? YtMode, string? YtWindows, int? YtDailyMin,
    List<string>? HiddenApps = null, bool? AllowAppInstall = null, bool? AllowInAppPurchases = null, bool? AllowAccountChanges = null, int? AgeRatingApps = null,
    string? DeviceType = null, string? KidsMode = null, bool? KidsLinked = null, string? KidsWindows = null, int? KidsDailyMin = null);
public sealed record MinutesInput(int Minutes);
/// <summary>Ét klik på kortet: App er "youtube" eller "kids".</summary>
public sealed record YtAppInput(string App, bool Allowed);
/// <summary>Handling på iPad'en via en Mac (cfgutil): list, install_profile, backup eller update.</summary>
/// <summary>Confirm: iPad'ens navn skrevet af brugeren (kræves før handlinger, der sletter iPad'en). BackupSet: gendan fra Fleetdecks kopi.</summary>
public sealed record MacActionInput(int MacId, string Action, string? Confirm = null, string? Order = null, string? BackupSet = null);
public sealed record SupervisionOrgInput(string Organization);
public sealed record IpadLinkInput(string? Ecid);
public sealed record FamilySettingsInput(string? AdguardUrl, string? AdguardUser, string? AdguardPass, string? DohHost, string? SchoolGroups, double? WatchHours = null);

/// <summary>iPads: internet, sengetid og YouTube via AdGuard. Forældre ser kun de iPads, de har fået adgang til.</summary>
public static partial class IpadEndpoints
{
    public static void MapIpadEndpoints(this WebApplication app)
    {
        var fam = app.MapGroup("/api").RequireAuthorization("family");
        var adm = app.MapGroup("/api").RequireAuthorization("admin");

        // ?type=ipad|iphone|appletv|android: kun den type. "types" er de typer, brugeren har enheder af (til faner og menu).
        fam.MapGet("/ipads", async (string? type, ClaimsPrincipal u, AppDb db, IpadEngine engine, SettingsService settings) =>
        {
            var sc = await FamilyScope.Of(u, db);
            if (type is { Length: > 0 } && DeviceTypes.Parse(type) is null) return Results.Problem("Ukendt enhedstype", statusCode: 400);
            var only = DeviceTypes.Parse(type);
            var seen = (await db.Ipads.AsNoTracking().OrderBy(i => i.Kind).ThenBy(i => i.Name).ToListAsync()).Where(i => sc.Sees(i.Id)).ToList();
            var types = DeviceTypes.All.Where(t => seen.Any(i => i.DeviceType == t)).ToArray();
            var all = only is null ? seen : seen.Where(i => i.DeviceType == only).ToList();
            var used = await IpadEngine.YtUsedToday(db);
            var today = await Today(db);
            var outdated = await IpadAppStore.OutdatedCounts(db);
            var now = DateTime.UtcNow;
            var hours = await IpadWatch.Hours(settings);
            return Results.Ok(new
            {
                can = sc.PermList, admin = sc.Admin, error = sc.Admin ? engine.LastError : null,
                watchHeld = sc.Admin && engine.WatchHeld,
                types,
                ipads = all.Select(i => Row(i, IpadPolicy.State(i, now, IpadEngine.Tz, used.GetValueOrDefault(i.Id)), today.GetValueOrDefault(i.Id),
                    outdated.GetValueOrDefault(i.Id), sc.Admin, hours, now)),
            });
        });

        fam.MapGet("/ipads/{id:int}", async (int id, ClaimsPrincipal u, AppDb db, FamilySettings fs, LockedProfiles lp, SettingsService settings) =>
        {
            var sc = await FamilyScope.Of(u, db);
            var i = await db.Ipads.FirstOrDefaultAsync(x => x.Id == id);
            if (i is null || !sc.Sees(id)) return Results.NotFound();
            // den låste profils version følger med ændringer, der ikke gik gennem iPad'en selv (fx en ny DNS-adresse)
            var host = await fs.DohHost();
            if (lp.Sync(i, host)) await db.SaveChangesAsync();
            var used = await IpadEngine.YtUsedToday(db);
            var st = IpadPolicy.State(i, DateTime.UtcNow, IpadEngine.Tz, used.GetValueOrDefault(i.Id));
            return Results.Ok(new
            {
                row = Row(i, st, (await Today(db)).GetValueOrDefault(i.Id), await db.IpadApps.CountAsync(x => x.IpadId == id && x.Outdated), sc.Admin, await IpadWatch.Hours(settings), DateTime.UtcNow),
                i.Schedule, i.BlockedServices, i.AllowedDomains, i.BlockedDomains, i.FilterAdult, i.SafeSearch, i.YtMode, i.YtWindows, i.YtDailyMin,
                i.KidsMode, i.KidsLinked, i.KidsWindows, i.KidsDailyMin,
                i.HiddenApps, i.AllowAppInstall, i.AllowInAppPurchases, i.AllowAccountChanges, i.AgeRatingApps,
                // enhedens tekniske oplysninger kun til administratorer (forældre ser kun det, der styrer barnet)
                locked = sc.Admin
                    ? new
                    {
                        version = i.LockedProfileVersion, applied = i.LockedProfileApplied, supervised = i.Supervised, ecid = i.Ecid, serial = i.Serial,
                        osVersion = i.OsVersion, deviceSeen = i.DeviceSeen, dns = host.Length > 0, identifier = LockedProfile.Identifier(i),
                    }
                    : new { version = 0, applied = (int?)null, supervised = false, ecid = (string?)null, serial = (string?)null,
                        osVersion = (string?)null, deviceSeen = (DateTime?)null, dns = host.Length > 0, identifier = "" },
                can = sc.PermList,
                setup = IpadSetupGuide.For(i),
            });
        });

        fam.MapGet("/ipads/services", async (FamilySettings fs, IpadEngine engine, CancellationToken ct) =>
        {
            if (await fs.Client() is not { } ag) return Results.Ok(Array.Empty<object>());
            try
            {
                var cat = await engine.Catalog(ag, ct);
                return Results.Ok((cat?.Items ?? []).Select(s => new { s.Id, s.Name, s.Group, icon = s.Icon }));
            }
            catch (Exception e) when (e is InvalidOperationException or HttpRequestException or TaskCanceledException) { return Results.Problem(e.Message, statusCode: 502); }
        });

        fam.MapGet("/ipads/{id:int}/activity", async (int id, int? days, ClaimsPrincipal u, AppDb db) =>
        {
            var sc = await FamilyScope.Of(u, db);
            if (!sc.Sees(id)) return Results.NotFound();
            var d = Math.Clamp(days ?? 7, 1, 35);
            var localToday = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, IpadEngine.Tz).Date;
            var from = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localToday.AddDays(1 - d), DateTimeKind.Unspecified), IpadEngine.Tz);
            var slots = await db.IpadSlots.AsNoTracking().Where(s => s.IpadId == id && s.Slot >= from && !s.Service.StartsWith(IpadPolicy.AppPrefix)).Select(s => new { s.Service, s.Slot }).ToListAsync();
            var perDay = slots.GroupBy(s => TimeZoneInfo.ConvertTimeFromUtc(s.Slot, IpadEngine.Tz).Date)
                .Select(g => new
                {
                    day = DateOnly.FromDateTime(g.Key),
                    total = g.Count(x => x.Service == "*") * 5,
                    services = g.Where(x => x.Service != "*").GroupBy(x => x.Service).Select(s => new { id = s.Key, minutes = s.Count() * 5 }).OrderByDescending(s => s.minutes),
                }).OrderBy(x => x.day);
            // i dag: aktive halve timer (til tidslinjen)
            var fromToday = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localToday, DateTimeKind.Unspecified), IpadEngine.Tz);
            var timeline = slots.Where(s => s.Slot >= fromToday && s.Service == "*")
                .GroupBy(s => IpadPolicy.SlotOf(TimeZoneInfo.ConvertTimeFromUtc(s.Slot, IpadEngine.Tz)) % 48).Select(g => new { slot = g.Key, minutes = g.Count() * 5 });
            return Results.Ok(new { days = perDay, timeline });
        });

        fam.MapPut("/ipads/{id:int}", async (int id, IpadInput i, ClaimsPrincipal u, AppDb db, AuditService audit, IpadEngine engine, Live live, LockedProfiles lp) =>
        {
            var sc = await FamilyScope.Of(u, db);
            var p = await db.Ipads.FindAsync(id);
            if (p is null || !sc.Sees(id)) return Results.NotFound();
            if (!sc.Can(FamilyPerms.Rules)) return Results.Problem("Du må ikke ændre reglerne", statusCode: 403);
            if (sc.Admin)
            {
                if (i.Name is { } n && n.Trim().Length > 0) p.Name = n.Trim()[..Math.Min(80, n.Trim().Length)];
                if (i.Kind is IpadKinds.Child or IpadKinds.Family) p.Kind = i.Kind;
                if (i.Owner is not null) p.Owner = i.Owner.Trim().Length == 0 ? null : i.Owner.Trim();
                if (i.Model is not null) p.Model = i.Model.Trim().Length == 0 ? null : i.Model.Trim();
                var newType = i.DeviceType is { Length: > 0 } ? DeviceTypes.Parse(i.DeviceType) : p.DeviceType;
                if (newType is null) return Results.Problem("Ukendt enhedstype", statusCode: 400);
                var newIp = i.LanIp is not null ? i.LanIp.Trim() : p.LanIp;
                if (DeviceTypes.Validate(newType, newIp) is { } err) return Results.Problem(err, statusCode: 400);
                if (await LanIpTaken(db, newIp, p.Id) is { } other) return Results.Problem($"IP-adressen bruges allerede af {other}", statusCode: 400);
                p.DeviceType = newType;
                p.LanIp = string.IsNullOrEmpty(newIp) ? null : newIp;
            }
            if (i.Schedule is { } s)
            {
                if (s.Length != IpadPolicy.Slots || s.Any(c => c is not ('0' or 's' or 'b'))) return Results.Problem("Ugyldigt skema", statusCode: 400);
                p.Schedule = s;
            }
            if (i.BlockedServices is { } bs) p.BlockedServices = bs.Where(x => x.Length is > 0 and < 60).Distinct().ToList();
            if (i.AllowedDomains is { } ad) p.AllowedDomains = IpadPolicy.Domains(ad).Distinct().Take(200).ToList();
            if (i.BlockedDomains is { } bd) p.BlockedDomains = IpadPolicy.Domains(bd).Distinct().Take(500).ToList();
            if (i.FilterAdult is bool fa) p.FilterAdult = fa;
            if (i.SafeSearch is bool ss) p.SafeSearch = ss;
            // "kids" er det gamle navn for: YouTube spærret og YouTube Kids tilladt
            if (i.YtMode == YtModes.KidsOnly) { p.YtMode = YtModes.Blocked; p.KidsMode = KidsModes.Allowed; }
            else if (YtModes.Valid(i.YtMode)) p.YtMode = i.YtMode!;
            if (KidsModes.Valid(i.KidsMode)) p.KidsMode = i.KidsMode!;
            if (i.KidsLinked is bool kl) p.KidsLinked = kl;
            if (i.KidsWindows is { } kw)
            {
                if (kw.Length == 0) p.KidsWindows = null;
                else if (kw.Length != IpadPolicy.Slots || kw.Any(c => c is not ('0' or '1'))) return Results.Problem("Ugyldige YouTube Kids-tider", statusCode: 400);
                else p.KidsWindows = kw.All(c => c == '1') ? null : kw;
            }
            if (i.KidsDailyMin is int kd) p.KidsDailyMin = Math.Clamp(kd, 0, 24 * 60);
            if (i.YtWindows is { } w)
            {
                if (w.Length == 0) p.YtWindows = null;
                else if (w.Length != IpadPolicy.Slots || w.Any(c => c is not ('0' or '1'))) return Results.Problem("Ugyldige YouTube-tider", statusCode: 400);
                else p.YtWindows = w.All(c => c == '1') ? null : w;
            }
            if (i.YtDailyMin is int ym) p.YtDailyMin = Math.Clamp(ym, 0, 24 * 60);
            // statisk tilstand: det, der ligger i den låste profil – kun administrator (fanen "Apps og begrænsninger" vises ikke for forældre; felterne ignoreres)
            if (sc.Admin)
            {
                if (i.HiddenApps is { } ha)
                {
                    if (ha.Select(x => x.Trim()).FirstOrDefault(x => x.Length > 0 && !LockedProfile.IsBundleId(x)) is { } bad)
                        return Results.Problem($"\"{bad}\" er ikke et app-id – skriv fx com.google.ios.youtube", statusCode: 400);
                    p.HiddenApps = LockedProfile.CleanApps(ha);
                }
                if (i.AllowAppInstall is bool aai) p.AllowAppInstall = aai;
                if (i.AllowInAppPurchases is bool iap) p.AllowInAppPurchases = iap;
                if (i.AllowAccountChanges is bool acc) p.AllowAccountChanges = acc;
                if (i.AgeRatingApps is int age)
                {
                    if (age != 0 && !LockedProfile.AgeRatings.ContainsKey(age)) return Results.Problem("Aldersgrænsen skal være 4, 9, 13, 16 eller 18 år", statusCode: 400);
                    p.AgeRatingApps = age == 0 ? null : age;
                }
            }
            await lp.Sync(p);   // ny version af den låste profil, hvis noget i den er ændret
            await db.SaveChangesAsync();
            await audit.Log(u.UserName(), "ipad.rules", p.Name);
            engine.Kick(); await live.Changed("ipads");
            return Results.Ok();
        });

        // Pause internettet: minutter > 0 = i så lang tid, -1 = til i morgen kl. 07, 0 = stop pausen
        fam.MapPost("/ipads/{id:int}/pause", (int id, MinutesInput m, ClaimsPrincipal u, AppDb db, AuditService audit, IpadEngine engine, Live live) =>
            Act(id, u, db, audit, engine, live, FamilyPerms.Pause, p =>
            {
                p.PauseUntil = m.Minutes switch { 0 => null, < 0 => Tomorrow(7), _ => DateTime.UtcNow.AddMinutes(Math.Min(m.Minutes, 7 * 24 * 60)) };
                return m.Minutes == 0 ? "pause slået fra" : $"pause til {p.PauseUntil?.ToLocalTime():HH:mm}";
            }));

        // Ekstra tid: løfter sengetid/skoletid i et antal minutter (0 = stop)
        fam.MapPost("/ipads/{id:int}/extra", (int id, MinutesInput m, ClaimsPrincipal u, AppDb db, AuditService audit, IpadEngine engine, Live live) =>
            Act(id, u, db, audit, engine, live, FamilyPerms.Pause, p =>
            {
                p.FreeUntil = m.Minutes <= 0 ? null : (p.FreeUntil > DateTime.UtcNow ? p.FreeUntil.Value : DateTime.UtcNow).AddMinutes(Math.Min(m.Minutes, 12 * 60));
                if (m.Minutes > 0) p.PauseUntil = null;
                return m.Minutes <= 0 ? "ekstra tid stoppet" : $"ekstra tid til {p.FreeUntil?.ToLocalTime():HH:mm}";
            }));

        // Ekstra YouTube-minutter i dag (lægges oven i dagsgrænsen)
        fam.MapPost("/ipads/{id:int}/youtube-bonus", (int id, MinutesInput m, ClaimsPrincipal u, AppDb db, AuditService audit, IpadEngine engine, Live live) =>
            Act(id, u, db, audit, engine, live, FamilyPerms.Pause, p =>
            {
                var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, IpadEngine.Tz));
                var cur = p.YtBonusDay == today ? p.YtBonusMin : 0;
                p.YtBonusDay = today; p.YtBonusMin = m.Minutes <= 0 ? 0 : Math.Min(cur + m.Minutes, 24 * 60);
                return $"YouTube-bonus i dag: {p.YtBonusMin} min.";
            }));

        // Slå YouTube eller YouTube Kids til/fra med ét klik – den anden app røres ikke
        fam.MapPost("/ipads/{id:int}/youtube-app", (int id, YtAppInput a, ClaimsPrincipal u, AppDb db, AuditService audit, IpadEngine engine, Live live) =>
            a.App is not ("youtube" or "kids") ? Task.FromResult(Results.Problem("Ukendt app", statusCode: 400)) :
            Act(id, u, db, audit, engine, live, FamilyPerms.Rules, p =>
            {
                IpadPolicy.SetApp(p, a.App == "kids" ? IpadPolicy.AppKids : IpadPolicy.AppYoutube, a.Allowed);
                return $"{(a.App == "kids" ? "YouTube Kids" : "YouTube")} er {(a.Allowed ? "tilladt" : "spærret")} på {p.Name}";
            }));

        // Engangslink til DNS-profilen (gyldigt i 24 timer) – åbnes på iPad'en eller scannes som QR-kode
        adm.MapPost("/ipads/{id:int}/profile-link", async (int id, ClaimsPrincipal u, AppDb db, SettingsService s, FamilySettings fs, HttpContext ctx) =>
        {
            var sc = await FamilyScope.Of(u, db);
            var p = await db.Ipads.FindAsync(id);
            if (p is null || !sc.Sees(id)) return Results.NotFound();
            if (!sc.Can(FamilyPerms.Rules)) return Results.Problem("Du må ikke installere profiler", statusCode: 403);
            if (!DeviceTypes.IsIos(p.DeviceType)) return Results.Problem("DNS-profilen via QR-kode findes kun til iPad og iPhone – denne enhed styres via sin faste IP-adresse", statusCode: 400);
            if ((await fs.DohHost()).Length == 0) return Results.Problem("Den offentlige DNS-adresse er ikke sat op endnu (Indstillinger → iPads)", statusCode: 400);
            p.ProfileToken = Crypto.Token(18); p.ProfileTokenExpires = DateTime.UtcNow.AddHours(24);
            await db.SaveChangesAsync();
            var baseUrl = (await s.Get("server_url")).TrimEnd('/');
            if (baseUrl.Length == 0) baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
            // adressen sendes også base64-kodet: en mellemliggende proxy (fx Kontrolrum) omskriver ellers Fleetdecks egen adresse til en relativ sti,
            // og så mangler IP'en i QR-koden på iPad'en
            static string B64(string x) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(x));
            return Results.Ok(new { url = $"{baseUrl}/ipad-profil/{p.ProfileToken}", shortUrl = $"{baseUrl}/p/{p.ProfileToken}",
                urlB64 = B64($"{baseUrl}/ipad-profil/{p.ProfileToken}"), shortUrlB64 = B64($"{baseUrl}/p/{p.ProfileToken}"), expires = p.ProfileTokenExpires });
        });

        // kort adresse til at skrive i Safari på iPad'en
        app.MapGet("/p/{token}", (string token) => token.Length < 16 ? Results.NotFound() : Results.Redirect($"/ipad-profil/{token}")).AllowAnonymous();

        app.MapGet("/ipad-profil/{token}", async (string token, AppDb db, FamilySettings fs, AuditService audit) =>
        {
            if (token.Length < 16) return Results.NotFound();
            var p = await db.Ipads.FirstOrDefaultAsync(x => x.ProfileToken == token);
            if (p is null || p.ProfileTokenExpires < DateTime.UtcNow) return Results.Text("Linket er udløbet – lav et nyt i Fleetdeck.", "text/plain; charset=utf-8", statusCode: 410);
            var host = await fs.DohHost();
            await audit.Log("iPad", "ipad.profile", p.Name);
            var bytes = System.Text.Encoding.UTF8.GetBytes(IpadProfile.Build(p, host));
            return Results.File(bytes, "application/x-apple-aspen-config", $"Fleetdeck-{p.Id}.mobileconfig");
        }).AllowAnonymous();

        adm.MapPost("/ipads", async (IpadCreate c, AppDb db, AuditService audit, ClaimsPrincipal u, IpadEngine engine, Live live, LockedProfiles lp) =>
        {
            var name = (c.Name ?? "").Trim();
            var type = c.DeviceType is { Length: > 0 } ? DeviceTypes.Parse(c.DeviceType) : DeviceTypes.Ipad;
            if (type is null) return Results.Problem("Ukendt enhedstype", statusCode: 400);
            if (name.Length == 0) return Results.Problem("Giv enheden et navn", statusCode: 400);
            var lanIp = c.LanIp?.Trim() is { Length: > 0 } li ? li : null;
            if (DeviceTypes.Validate(type, lanIp) is { } err) return Results.Problem(err, statusCode: 400);
            if (await LanIpTaken(db, lanIp, 0) is { } other) return Results.Problem($"IP-adressen bruges allerede af {other}", statusCode: 400);
            var kind = c.Kind is IpadKinds.Family ? IpadKinds.Family : IpadKinds.Child;
            var p = new Ipad
            {
                Name = name[..Math.Min(80, name.Length)], Kind = kind, Owner = c.Owner?.Trim() is { Length: > 0 } o ? o : null, Model = c.Model?.Trim() is { Length: > 0 } mo ? mo : null,
                ClientId = NewClientId(), DeviceType = type, LanIp = lanIp,
                Schedule = kind == IpadKinds.Child ? IpadPolicy.DefaultBedtime() : new string('0', IpadPolicy.Slots),
                YtMode = kind == IpadKinds.Child ? YtModes.Moderate : YtModes.Open,
                FilterAdult = kind == IpadKinds.Child, SafeSearch = kind == IpadKinds.Child,
                AllowAppInstall = kind == IpadKinds.Family, AllowInAppPurchases = kind == IpadKinds.Family, AllowAccountChanges = kind == IpadKinds.Family,
            };
            await lp.Sync(p);
            db.Ipads.Add(p);
            await db.SaveChangesAsync();
            await audit.Log(u.UserName(), "ipad.create", p.Name);
            engine.Kick(); await live.Changed("ipads");
            return Results.Ok(new { p.Id });
        });

        adm.MapDelete("/ipads/{id:int}", async (int id, AppDb db, AuditService audit, ClaimsPrincipal u, IpadEngine engine, Live live) =>
        {
            var p = await db.Ipads.FindAsync(id);
            if (p is null) return Results.NotFound();
            await IpadRelease.Remove(db, p);
            await audit.Log(u.UserName(), "ipad.delete", p.Name);
            engine.Kick(); await live.Changed("ipads");
            return Results.Ok();
        });

        MapStatic(app, fam, adm);
        MapWatch(fam);
        MapRelease(adm);
        MapApps(fam, adm);

        adm.MapGet("/family/settings", async (SettingsService s, FamilySettings fs, IpadEngine engine) => Results.Ok(new
        {
            adguardUrl = await s.Get("adguard_url"), adguardUser = await s.Get("adguard_user"), hasPass = (await s.Get("adguard_pass")).Length > 0,
            dohHost = await fs.DohHost(), schoolGroups = string.Join(",", await fs.SchoolGroups()), lastError = engine.LastError, lastRun = engine.LastRun,
            watchHours = await IpadWatch.Hours(s),
        }));

        adm.MapPut("/family/settings", async (FamilySettingsInput i, SettingsService s, FamilySettings fs, AuditService audit, ClaimsPrincipal u, IpadEngine engine, AppDb db, LockedProfiles lp) =>
        {
            if (i.AdguardUrl is { } url) await s.Set("adguard_url", url.Trim().TrimEnd('/'));
            if (i.AdguardUser is { } usr) await s.Set("adguard_user", usr.Trim());
            if (i.AdguardPass is { Length: > 0 } pw) await fs.SetPassword(pw);
            if (i.DohHost is { } h) await s.Set("doh_host", h.Trim().Replace("https://", "").Split('/')[0]);
            if (i.SchoolGroups is { } g) await s.Set("school_groups", g.Trim());
            if (i.WatchHours is { } wh) await s.Set(IpadWatch.HoursSetting, IpadWatch.Hours(wh).ToString(System.Globalization.CultureInfo.InvariantCulture));
            // DNS-adressen ligger i de låste profiler: ny adresse = ny version
            var host = await fs.DohHost();
            foreach (var p in await db.Ipads.ToListAsync()) lp.Sync(p, host);
            await db.SaveChangesAsync();
            await audit.Log(u.UserName(), "settings.family");
            engine.Kick();
            return Results.Ok();
        });

        // Selvtjek: forbindelse til AdGuard, version, og om den offentlige DNS-adresse svarer
        adm.MapPost("/family/test", async (FamilySettings fs, CancellationToken ct) =>
        {
            var steps = new List<object>();
            if (await fs.Client() is not { } ag) return Results.Ok(new { ok = false, steps = new[] { new { ok = false, text = "AdGuard er ikke sat op (adresse, bruger og adgangskode)" } } });
            var ok = true;
            try
            {
                var st = await ag.Status(ct);
                steps.Add(new { ok = true, text = $"AdGuard Home {st?["version"]?.GetValue<string>()} svarer" });
            }
            catch (Exception e) when (e is InvalidOperationException or HttpRequestException or TaskCanceledException) { steps.Add(new { ok = false, text = e.Message }); ok = false; }
            var (dohOk, dohText) = await DohProbe.Run(await fs.DohHost(), ct);
            steps.Add(new { ok = dohOk, text = dohText });
            ok &= dohOk;
            return Results.Ok(new { ok, steps });
        });
    }

    private static async Task<IResult> Act(int id, ClaimsPrincipal u, AppDb db, AuditService audit, IpadEngine engine, Live live, string perm, Func<Ipad, string> f)
    {
        var sc = await FamilyScope.Of(u, db);
        var p = await db.Ipads.FindAsync(id);
        if (p is null || !sc.Sees(id)) return Results.NotFound();
        if (!sc.Can(perm)) return Results.Problem("Det har du ikke adgang til", statusCode: 403);
        var what = f(p);
        await db.SaveChangesAsync();
        await audit.Log(u.UserName(), "ipad.action", $"{p.Name}: {what}");
        engine.Kick(); await live.Changed("ipads");
        return Results.Ok(new { msg = what });
    }

    private static DateTime Tomorrow(int hour)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, IpadEngine.Tz).Date.AddDays(1).AddHours(hour);
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), IpadEngine.Tz);
    }

    /// <summary>Navnet på en anden enhed med samme faste IP-adresse (AdGuard kan kun have én klient pr. IP), ellers null.</summary>
    private static async Task<string?> LanIpTaken(AppDb db, string? ip, int exceptId) =>
        string.IsNullOrEmpty(ip) ? null : await db.Ipads.Where(x => x.LanIp == ip && x.Id != exceptId).Select(x => x.Name).FirstOrDefaultAsync();

    private static string NewClientId()
    {
        const string a = "abcdefghijkmnpqrstuvwxyz23456789";
        // første tegn efter "fd" er aldrig et hextegn: et ID af rene hextegn gemmer AdGuard som en MAC-adresse (se AdGuardClient.LooksLikeMac)
        const string notHex = "ghijkmnpqrstuvwxyz";
        return "fd" + notHex[System.Security.Cryptography.RandomNumberGenerator.GetInt32(notHex.Length)]
            + new string(Enumerable.Range(0, 13).Select(_ => a[System.Security.Cryptography.RandomNumberGenerator.GetInt32(a.Length)]).ToArray());
    }

    private static async Task<Dictionary<int, int>> Today(AppDb db)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, IpadEngine.Tz).Date;
        var from = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), IpadEngine.Tz);
        return await db.IpadSlots.AsNoTracking().Where(s => s.Service == "*" && s.Slot >= from).GroupBy(s => s.IpadId)
            .Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N * 5);
    }

    /// <param name="tech">Administrator: også IP-adresse og AdGuard-fejl (forældre ser kun barnets tilstand).</param>
    private static object Row(Ipad i, IpadState st, int minutesToday, int outdatedApps, bool tech, double watchHours, DateTime now) => new
    {
        i.Id, i.Name, i.Kind, i.DeviceType, i.Owner, i.Model, LanIp = tech ? i.LanIp : null, i.LastSeen, ApplyError = tech ? i.ApplyError : null, applied = !tech || i.Applied is not null,
        mode = st.Mode, until = st.Until, nextChange = st.NextChange, nextMode = st.NextMode, extra = st.Extra,
        yt = st.Yt, ytMode = i.YtMode, ytUsed = st.YtUsed, ytLimit = st.YtLimit,
        kids = st.Kids, kidsMode = i.KidsMode, kidsLinked = i.KidsLinked, kidsUsed = st.KidsUsed, kidsLimit = st.KidsLimit, minutesToday, blockedServices = i.BlockedServices,
        outdatedApps,
        control = IpadWatch.Status(i, now, watchHours),
    };
}
