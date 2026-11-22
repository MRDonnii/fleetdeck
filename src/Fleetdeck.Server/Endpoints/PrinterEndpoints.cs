using System.Security.Claims;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Services;
using Fleetdeck.Server.Services.Printers;
using Fleetdeck.Shared;
using Microsoft.EntityFrameworkCore;

namespace Fleetdeck.Server.Endpoints;

public sealed record PrinterInput(string Name, string Uri, string? Location, string? Paper, string? Duplex, bool? Color, bool? NeedsDriver, string? Driver,
    int? DriverPackageId, string? MakeModel, string? Source, List<int>? ProfileIds, List<int>? MachineIds, bool? MakeDefault);
public sealed record PrinterAssignInput(int PrinterId, int? ProfileId, int? MachineId, bool? MakeDefault, bool? Keep);
public sealed record PrinterAssignUpdate(bool? MakeDefault, bool? Keep);
public sealed record PrinterDiscoverInput(string? Subnet);
public sealed record PrinterSearchInput(bool? Auto, double? Hours, string? Subnet);
public sealed record PrinterProbeInput(string Uri);

/// <summary>Siden Printere: printere i Fleetdeck, fund på netværket og hvor de lægges på (profiler eller maskiner).</summary>
public static class PrinterEndpoints
{
    private static IResult Bad(string msg) => Results.Problem(msg, statusCode: 400);

    public static void MapPrinterEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/printers").RequireAuthorization("user");
        var adm = app.MapGroup("/api/printers").RequireAuthorization("admin");

        api.MapGet("", async (AppDb db, PrinterDiscovery disc, SettingsService s) =>
        {
            var printers = await db.Printers.AsNoTracking().Include(p => p.Assignments).OrderBy(p => p.Name).ToListAsync();
            var profileRows = await db.Profiles.AsNoTracking().OrderBy(p => p.Name).Select(p => new { p.Id, p.Name }).ToListAsync();
            var profiles = profileRows.ToDictionary(p => p.Id, p => p.Name);
            var machines = await db.Machines.AsNoTracking().Select(m => new { m.Id, m.Name, m.DisplayName, m.ProfileId, m.HealthJson, m.Platform, m.LastSeen }).ToListAsync();
            var perProfile = machines.Where(m => m.ProfileId != null).GroupBy(m => m.ProfileId!.Value).ToDictionary(g => g.Key, g => g.Count());
            var auto = await s.Get(PrinterDiscovery.AutoSetting) != "0";
            var hours = Math.Clamp(await s.GetNum(PrinterDiscovery.HoursSetting, 6), 1, 168);
            // første åbning: Fleetdeck søger selv (i baggrunden), så listen fylder sig uden at nogen skal trykke
            if (auto && disc.LastRun is null && disc.LastAttempt is null) disc.Kick("first");
            // fund: seneste søgning + de printere, agenterne melder lige nu (installerede og fundne med Bonjour/WSD)
            var agentSeen = await PrinterDiscovery.AgentFinds(db);
            var uris = printers.Select(p => p.Uri).ToList();
            var found = PrinterFinds.Merge(disc.Last, agentSeen).Where(f => !PrinterFinds.Known(f, uris)).ToList();
            var healths = machines.ToDictionary(m => m.Id, m => Json.Des<HealthReport>(m.HealthJson));
            return Results.Ok(new
            {
                printers = printers.Select(p => new
                {
                    p.Id, p.Name, p.Uri, p.Location, p.MakeModel, p.Paper, p.Duplex, p.Color, p.NeedsDriver, p.Driver, p.DriverPackageId, p.Source, p.Created,
                    host = PrinterUri.Host(p.Uri), driverless = !p.NeedsDriver, status = PrinterService.StatusView(p),
                    assignments = p.Assignments.OrderBy(a => a.ProfileId == null).ThenBy(a => a.Id).Select(a => new
                    {
                        a.Id, a.ProfileId, a.MachineId, a.MakeDefault, a.Keep,
                        label = a.ProfileId is int pid ? $"Profilen {profiles.GetValueOrDefault(pid, "?")}" : machines.FirstOrDefault(m => m.Id == a.MachineId) is { } mm ? mm.DisplayName ?? mm.Name : "?",
                        machines = a.ProfileId is int pid2 ? perProfile.GetValueOrDefault(pid2) : 1,
                    }),
                    machines = p.Assignments.SelectMany(a => a.ProfileId is int pid ? machines.Where(m => m.ProfileId == pid).Select(m => m.Id) : [a.MachineId ?? 0])
                        .Where(id => id > 0).Distinct().Count(),
                }),
                found,
                // hvor printere kan sættes op: pc'er, hvor agenten kan lægge printere på, og profiler
                targets = new
                {
                    machines = machines.Where(m => PrinterService.Supported(new Machine { Platform = m.Platform }, healths[m.Id]))
                        .OrderBy(m => m.DisplayName ?? m.Name, StringComparer.CurrentCultureIgnoreCase)
                        .Select(m => new { m.Id, name = m.DisplayName ?? m.Name, m.Platform, m.ProfileId, profile = m.ProfileId is int pp ? profiles.GetValueOrDefault(pp) : null, m.LastSeen }),
                    profiles = profileRows.Select(p => new { p.Id, p.Name, machines = perProfile.GetValueOrDefault(p.Id) }),
                },
                search = new
                {
                    auto, hours, running = disc.Running, lastRun = disc.LastRun, error = disc.LastError, networkOff = PrinterDiscovery.NetworkOff,
                    nextRun = auto && disc.LastRun is { } lr ? lr.AddHours(hours) : (DateTime?)null,
                    subnets = disc.Summary?.Subnets ?? [], bonjour = disc.Summary?.Bonjour ?? 0, scan = disc.Summary?.Scan ?? 0,
                    agents = healths.Values.Count(h => h.NetPrintersAt is not null),
                    ownSubnets = PrinterDiscovery.NetworkOff ? [] : PrinterFinds.OwnSubnets(PrinterFinds.LocalAddresses()),
                },
                scanning = disc.Running, lastScan = disc.LastRun, subnet = await s.Get(PrinterDiscovery.SubnetSetting),
            });
        });

        // Søg nu: Bonjour, serverens eget net og (valgfrit) et område, brugeren selv skriver ind; agenterne søger ved næste check-in
        adm.MapPost("/discover", async (PrinterDiscoverInput i, PrinterDiscovery disc, SettingsService s, ClaimsPrincipal u, AuditService audit) =>
        {
            if (i.Subnet is not null)
            {
                var subnet = i.Subnet.Trim();
                if (subnet.Length > 0 && PrinterFinds.Hosts(subnet) is null) return Bad("Skriv området som 192.168.1.0/24 (højst /22)");
                if (subnet.Length > 0 && !PrinterFinds.IsPrivateRange(subnet)) return Bad("Kun private adresser kan scannes (10.x, 172.16–31.x og 192.168.x)");
                await s.Set(PrinterDiscovery.SubnetSetting, subnet);
            }
            var started = disc.Kick("manual");
            await audit.Log(u.UserName(), "printer.discover", started ? "Søgning startet" : "En søgning kører allerede");
            return Results.Ok(new { started });
        });

        // Indstillinger for søgningen: automatisk til/fra, interval og et ekstra område
        adm.MapPut("/search", async (PrinterSearchInput i, SettingsService s, ClaimsPrincipal u, AuditService audit, Live live) =>
        {
            if (i.Subnet is { } sub)
            {
                sub = sub.Trim();
                if (sub.Length > 0 && PrinterFinds.Hosts(sub) is null) return Bad("Skriv området som 192.168.1.0/24 (højst /22)");
                if (sub.Length > 0 && !PrinterFinds.IsPrivateRange(sub)) return Bad("Kun private adresser kan scannes (10.x, 172.16–31.x og 192.168.x)");
                await s.Set(PrinterDiscovery.SubnetSetting, sub);
            }
            if (i.Auto is bool a) await s.Set(PrinterDiscovery.AutoSetting, a ? "1" : "0");
            if (i.Hours is double h) await s.Set(PrinterDiscovery.HoursSetting, Math.Clamp(Math.Round(h), 1, 168).ToString(System.Globalization.CultureInfo.InvariantCulture));
            await audit.Log(u.UserName(), "printer.search", $"Automatisk søgning {(await s.Get(PrinterDiscovery.AutoSetting) != "0" ? "til" : "fra")}");
            await live.Changed("printers");
            return Results.Ok();
        });

        // Spørg en printer (til formularen): model, farve, tosidet, uden driver?
        adm.MapPost("/probe", async (PrinterProbeInput i, CancellationToken ct) =>
        {
            if (PrinterUri.Normalize(i.Uri) is not { } uri) return Bad("Skriv adressen som ipp://printer/ipp/print");
            var info = await PrinterDiscovery.Probe(uri, ct);
            return Results.Ok(new { uri, info.MakeModel, info.Info, info.Location, info.Color, info.Duplex, info.Driverless, info.Error, status = PrinterStatus.Describe(info).Text });
        });

        adm.MapPost("", async (PrinterInput i, AppDb db, AuditService audit, Live live, ClaimsPrincipal u) =>
        {
            if (PrinterService.Validate(i.Name, i.Uri, i.Paper, i.Duplex) is { } err) return Bad(err);
            var uri = PrinterUri.Normalize(i.Uri)!;
            if (await db.Printers.AnyAsync(p => p.Uri == uri)) return Bad("Den printer er allerede tilføjet");
            var p = new Printer { Created = DateTime.UtcNow, Source = i.Source is "bonjour" or "scan" or "agent" ? i.Source : "manual" };
            if (Apply(p, i) is { } bad) return Bad(bad);
            db.Printers.Add(p);
            await db.SaveChangesAsync();
            var n = await PrinterService.Place(db, p.Id, i.ProfileIds, i.MachineIds, i.MakeDefault == true);
            await audit.Log(u.UserName(), "printer.add", $"{p.Name} ({p.Uri}){(n > 0 ? $", lagt på {n} sted(er)" : "")}");
            await live.Changed("printers"); await live.Changed("machines");
            return Results.Ok(new { p.Id });
        });

        adm.MapPut("/{id:int}", async (int id, PrinterInput i, AppDb db, AuditService audit, Live live, ClaimsPrincipal u) =>
        {
            var p = await db.Printers.FindAsync(id);
            if (p is null) return Results.NotFound();
            if (PrinterService.Validate(i.Name, i.Uri, i.Paper, i.Duplex) is { } err) return Bad(err);
            var uri = PrinterUri.Normalize(i.Uri)!;
            if (await db.Printers.AnyAsync(x => x.Id != id && x.Uri == uri)) return Bad("En anden printer har den adresse");
            if (Apply(p, i) is { } bad) return Bad(bad);
            await db.SaveChangesAsync();
            await audit.Log(u.UserName(), "printer.update", $"{p.Name} ({p.Uri})");
            await live.Changed("printers"); await live.Changed("machines");
            return Results.Ok();
        });

        // Slet: agenterne fjerner printeren igen (kun der, hvor Fleetdeck selv har lagt den på)
        adm.MapDelete("/{id:int}", async (int id, AppDb db, AuditService audit, Live live, ClaimsPrincipal u) =>
        {
            var p = await db.Printers.FindAsync(id);
            if (p is null) return Results.NotFound();
            db.Printers.Remove(p);
            await db.SaveChangesAsync();
            await audit.Log(u.UserName(), "printer.remove", $"{p.Name} ({p.Uri})");
            await live.Changed("printers"); await live.Changed("machines");
            return Results.Ok();
        });

        api.MapGet("/assignments", async (int? profileId, int? machineId, AppDb db) =>
            await db.PrinterAssignments.AsNoTracking().Include(a => a.Printer)
                .Where(a => (profileId != null && a.ProfileId == profileId) || (machineId != null && a.MachineId == machineId))
                .OrderBy(a => a.Printer!.Name)
                .Select(a => new { a.Id, a.PrinterId, a.ProfileId, a.MachineId, a.MakeDefault, a.Keep, name = a.Printer!.Name, uri = a.Printer.Uri, makeModel = a.Printer.MakeModel })
                .ToListAsync());

        adm.MapPost("/assignments", async (PrinterAssignInput i, AppDb db, AuditService audit, Live live, ClaimsPrincipal u) =>
        {
            if ((i.ProfileId is null) == (i.MachineId is null)) return Bad("Vælg enten en profil eller en maskine");
            if (await db.Printers.FindAsync(i.PrinterId) is not { } p) return Bad("Printeren findes ikke");
            if (i.ProfileId is int pid && !await db.Profiles.AnyAsync(x => x.Id == pid)) return Bad("Profilen findes ikke");
            if (i.MachineId is int mid && !await db.Machines.AnyAsync(x => x.Id == mid)) return Bad("Maskinen findes ikke");
            if (await db.PrinterAssignments.AnyAsync(a => a.PrinterId == i.PrinterId && a.ProfileId == i.ProfileId && a.MachineId == i.MachineId))
                return Bad("Printeren er allerede lagt på her");
            var a = new PrinterAssignment { PrinterId = p.Id, ProfileId = i.ProfileId, MachineId = i.MachineId, MakeDefault = i.MakeDefault == true, Keep = i.Keep != false };
            db.PrinterAssignments.Add(a);
            await db.SaveChangesAsync();
            await audit.Log(u.UserName(), "printer.assign", $"{p.Name} → {(i.ProfileId is not null ? $"profil {i.ProfileId}" : $"maskine {i.MachineId}")}");
            await live.Changed("printers"); await live.Changed("machines");
            return Results.Ok(new { a.Id });
        });

        adm.MapPut("/assignments/{id:int}", async (int id, PrinterAssignUpdate i, AppDb db, Live live) =>
        {
            var a = await db.PrinterAssignments.FindAsync(id);
            if (a is null) return Results.NotFound();
            if (i.MakeDefault is bool d) a.MakeDefault = d;
            if (i.Keep is bool k) a.Keep = k;
            await db.SaveChangesAsync();
            await live.Changed("printers"); await live.Changed("machines");
            return Results.Ok();
        });

        // Fjern fra profil/maskine: agenten fjerner printeren igen ved næste check-in
        adm.MapDelete("/assignments/{id:int}", async (int id, AppDb db, AuditService audit, Live live, ClaimsPrincipal u) =>
        {
            var a = await db.PrinterAssignments.Include(x => x.Printer).FirstOrDefaultAsync(x => x.Id == id);
            if (a is null) return Results.NotFound();
            db.PrinterAssignments.Remove(a);
            await db.SaveChangesAsync();
            await audit.Log(u.UserName(), "printer.unassign", $"{a.Printer?.Name} fra {(a.ProfileId is not null ? $"profil {a.ProfileId}" : $"maskine {a.MachineId}")}");
            await live.Changed("printers"); await live.Changed("machines");
            return Results.Ok();
        });

        // Spørg printerne om status nu (ellers hvert 10. minut)
        adm.MapPost("/refresh", async (AppDb db, Live live, CancellationToken ct) =>
        {
            await PrinterMonitor.PollAll(db, ct);
            await live.Changed("printers");
            return Results.Ok();
        });
    }

    private static string? Apply(Printer p, PrinterInput i)
    {
        p.Name = PrinterNames.Clean(i.Name);
        p.Uri = PrinterUri.Normalize(i.Uri)!;
        p.Location = Cut(PrinterNames.Clean(i.Location), 100);
        p.Paper = i.Paper ?? p.Paper;
        p.Duplex = i.Duplex ?? p.Duplex;
        p.Color = i.Color ?? p.Color;
        p.NeedsDriver = i.NeedsDriver ?? p.NeedsDriver;
        p.Driver = Cut(PrinterNames.Clean(i.Driver), 200);
        p.DriverPackageId = p.NeedsDriver ? i.DriverPackageId : null;
        if (i.MakeModel is { Length: > 0 } mm) p.MakeModel = Cut(PrinterNames.Clean(mm), 200);
        // uden driver kræver IPP; rå 9100/lpd kan kun bruges med en driver
        if (!p.NeedsDriver && !PrinterUri.IsIpp(p.Uri))
            return "Uden driver skal adressen være ipp:// eller ipps:// – ellers markér \"Kræver driver\"";
        if (p.NeedsDriver && p.Driver.Length == 0) return "Skriv driverens navn (Windows) eller CUPS-model";
        return null;
    }

    private static string Cut(string s, int n) => s.Length > n ? s[..n] : s;
}
