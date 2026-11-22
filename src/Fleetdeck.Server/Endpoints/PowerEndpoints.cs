using System.Security.Claims;
using Fleetdeck.Server.Data;
using Fleetdeck.Server.Services;
using Fleetdeck.Shared;
using Microsoft.EntityFrameworkCore;

namespace Fleetdeck.Server.Endpoints;

public sealed record PowerConfigInput(string? Switch, string? Sensor, int? MinWatts);
public sealed record PowerCycleInput(bool Confirm, string? ConfirmName);
public sealed record HaApiInput(string? Url, string? Token, int? HangMinutes);

/// <summary>
/// Strøm og genstart på maskinens side: vagten over genstart/sluk (status), kontakt med strømmåler i Home Assistant
/// ("Sluk og tænd strømmen", når en genstart hænger), "Gør klar til Wake-on-LAN" med backup og en test.
/// </summary>
public static class PowerEndpoints
{
    private static IResult Bad(string msg) => Results.BadRequest(new { error = msg });

    public static void MapPowerEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization("user");
        var adm = app.MapGroup("/api").RequireAuthorization("admin");

        // ---- Home Assistant-API (Indstillinger): adresse, nøgle (kun skrivbar) og "hænger efter X min."
        api.MapGet("/power/ha", async (HomeAssistantApi ha) => Results.Ok(new
        {
            url = await ha.Url(), hasToken = await ha.HasToken(), hangMinutes = await ha.HangMinutes(),
        }));
        adm.MapPut("/power/ha", async (HaApiInput r, HomeAssistantApi ha, AuditService audit, ClaimsPrincipal u) =>
        {
            try { await ha.Save(r.Url, r.Token, r.HangMinutes); }
            catch (ArgumentException e) { return Bad(e.Message); }
            await audit.Log(u.UserName(), "settings.update", $"Home Assistant-API{(r.Token is { Length: > 0 } ? " (ny nøgle)" : "")}{(r.HangMinutes is int h ? $", hænger efter {h} min." : "")}");
            return Results.Ok();
        });
        adm.MapPost("/power/ha/test", async (HomeAssistantApi ha, CancellationToken ct) =>
        {
            try { return Results.Ok(new { status = await ha.Test(ct) }); }
            catch (Exception e) when (e is HttpRequestException or InvalidOperationException or TaskCanceledException) { return Results.Ok(new { status = $"Fejl: {e.Message}" }); }
        });

        // ---- maskinens side
        api.MapGet("/machines/{id:int}/power", async (int id, AppDb db, HomeAssistantApi ha, CancellationToken ct) =>
        {
            if (await db.Machines.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) is not { } m) return Results.NotFound();
            var configured = await ha.Configured();
            double? watts = null; string? wattsError = null;
            if (m.PowerSensor is { Length: > 0 } && configured)
            {
                try { var st = await ha.Get(m.PowerSensor, ct); watts = HaEntity.Watts(st.Value, st.Unit); if (watts is null) wattsError = $"Måleren viser \"{st.Value}\""; }
                catch (Exception e) when (e is HttpRequestException or InvalidOperationException or TaskCanceledException or System.Text.Json.JsonException) { wattsError = e.Message; }
            }
            var job = await db.Jobs.AsNoTracking().Where(j => j.MachineId == id && (j.Type == JobTypes.Reboot || j.Type == JobTypes.Shutdown) && j.PowerAt != null)
                .OrderByDescending(j => j.Id).Select(j => new { j.Id, j.Type, j.Title, j.Status, j.Step, j.PowerAt, j.HungAt, j.Finished, j.ParamsJson }).FirstOrDefaultAsync(ct);
            var wol = await db.Jobs.AsNoTracking().Where(j => j.MachineId == id && j.Source == WolSource)
                .OrderByDescending(j => j.Id).Select(j => new { j.Id, j.Title, j.Status, j.Finished, j.Output }).Take(5).ToListAsync(ct);
            var cycleWhy = !configured ? "Home Assistant-API er ikke sat op (Indstillinger)."
                : await Services.Bios.BiosUpdateService.MayBeFlashing(db, id, DateTime.UtcNow) ? Services.Bios.BiosUpdateService.FlashingText
                : PowerWatch.CycleRefusal(m.PowerSwitch, m.PowerSensor, watts, m.PowerMinWatts);
            return Results.Ok(new
            {
                @switch = m.PowerSwitch, @protected = m.Protected, label = m.Label, sensor = m.PowerSensor, minWatts = m.PowerMinWatts, haConfigured = configured,
                watts, wattsError, hangMinutes = await ha.HangMinutes(),
                job = job is null ? null : new { job.Id, job.Type, job.Title, job.Status, job.Step, job.PowerAt, job.HungAt, job.Finished, wolTest = Json.Des<JobParams>(job.ParamsJson).WolTest == true },
                canCycle = cycleWhy is null, cycleWhy,
                wol = new
                {
                    supported = m.Platform == Platforms.Windows, hasMac = !string.IsNullOrEmpty(m.Mac),
                    prepared = wol.FirstOrDefault(j => j.Title == WolReady.PrepareTitle || j.Title == WolReady.RestoreTitle) is { Title: WolReady.PrepareTitle, Status: JobStatus.Success },
                    jobs = wol.Select(j => new { j.Id, j.Title, j.Status, j.Finished, output = j.Output.Length > 4000 ? j.Output[^4000..] : j.Output }),
                },
                bios = WolReady.Bios.Select(b => new { name = b.Name, value = b.Value, why = b.Why }),
            });
        });

        adm.MapPut("/machines/{id:int}/power", async (int id, PowerConfigInput r, AppDb db, AuditService audit, Live live, ClaimsPrincipal u) =>
        {
            if (await db.Machines.FindAsync(id) is not { } m) return Results.NotFound();
            try
            {
                m.PowerSwitch = HaEntity.Clean(r.Switch, HaEntity.IsSwitch);
                m.PowerSensor = HaEntity.Clean(r.Sensor, HaEntity.IsSensor);
            }
            catch (ArgumentException e) { return Bad($"{e.Message} – kontakten skal være switch.… og måleren sensor.…"); }
            if (r.MinWatts is int w) m.PowerMinWatts = Math.Clamp(w, 1, 2000);
            await db.SaveChangesAsync();
            await audit.Log(u.UserName(), "machine.power_switch", $"{m.Label}: kontakt {m.PowerSwitch ?? "ingen"}, måler {m.PowerSensor ?? "ingen"}, over {m.PowerMinWatts} W = tændt", m.Id);
            await live.Changed("machines", id);
            return Results.Ok();
        });

        adm.MapPost("/machines/{id:int}/power/cycle", async (int id, PowerCycleInput r, AppDb db, PowerWatchService watch, ClaimsPrincipal u, CancellationToken ct) =>
        {
            if (await db.Machines.FindAsync([id], ct) is not { } m) return Results.NotFound();
            if (PowerWatch.ConfirmRefusal(r.Confirm, r.ConfirmName, m.Protected, m.Label) is { } why) return Bad(why);
            try { return Results.Ok(new { note = await watch.PowerCycle(m, u.UserName(), ct) }); }
            catch (Exception e) when (e is InvalidOperationException or HttpRequestException or ArgumentException) { return Bad(e.Message); }
        });

        // ---- Wake-on-LAN: gør klar (med backup), fortryd, og test (luk ned → magic packet → mål om den kommer op)
        adm.MapPost("/machines/{id:int}/wol/{action}", async (int id, string action, AppDb db, JobService jobs, AuditService audit, Live live, ClaimsPrincipal u) =>
        {
            if (await db.Machines.FindAsync(id) is not { } m) return Results.NotFound();
            Job j;
            switch (action)
            {
                case "prepare" or "restore":
                    if (m.Platform != Platforms.Windows) return Bad("Kun Windows – på Linux og Mac sættes Wake-on-LAN i BIOS og netkortet.");
                    var prep = action == "prepare";
                    j = await jobs.Create(id, JobTypes.Script, prep ? WolReady.PrepareTitle : WolReady.RestoreTitle,
                        new JobParams { Body = prep ? WolReady.PrepareScript() : WolReady.RestoreScript(), TimeoutMin = 5 }, RunAs.System, null, WolSource, u.UserName());
                    break;
                case "test":
                    if (string.IsNullOrEmpty(m.Mac)) return Bad("Maskinens MAC-adresse kendes ikke endnu.");
                    if (await db.Jobs.AnyAsync(x => x.MachineId == id && (x.Type == JobTypes.Reboot || x.Type == JobTypes.Shutdown) && JobStatus.Active.Contains(x.Status)))
                        return Bad("Der er allerede en genstart eller slukning i gang.");
                    j = await jobs.Create(id, JobTypes.Shutdown, WolReady.TestTitle,
                        new JobParams { Delay = 60, Force = true, WolTest = true, Message = "Fleetdeck tester Wake-on-LAN: pc'en slukker om 1 minut og tændes igen med det samme. Gem dit arbejde." },
                        RunAs.System, null, WolSource, u.UserName());
                    break;
                default: return Results.NotFound();
            }
            await audit.Log(u.UserName(), "machine.wol", $"{m.Label}: {j.Title}", m.Id);
            await live.Changed("jobs", id);
            await live.Wake(id);
            return Results.Ok(new { jobId = j.Id });
        });
    }

    /// <summary>Kilden på Wake-on-LAN-jobs (klargøring, fortryd og test).</summary>
    public const string WolSource = "wol";
}
