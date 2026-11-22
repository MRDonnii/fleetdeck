using Fleetdeck.Server.Data;
using Fleetdeck.Shared;
using Microsoft.EntityFrameworkCore;

namespace Fleetdeck.Server.Services;

/// <summary>
/// Følger genstart- og sluk-jobs, til maskinen er tilbage (eller slukket), og melder "hænger" med besked.
/// Kaldes af baggrundstråden hvert 30. sekund. Selve afgørelsen står i <see cref="PowerWatch"/>.
/// </summary>
public sealed class PowerWatchService(AppDb db, SettingsService settings, HomeAssistantApi ha, Notifier notifier, AuditService audit, Live live,
    ILogger<PowerWatchService> log)
{
    /// <summary>Agenten har meldt en planlagt genstart/slukning: jobbet står som "genstarter", til vagten afgør det.</summary>
    public static bool StartWatch(Job j, Machine m, JobResult r, DateTime now)
    {
        if (j.Type is not (JobTypes.Reboot or JobTypes.Shutdown) || r.Status != JobStatus.Success) return false;
        // "Ingen genstart nødvendig" / "pc'en er i brug": ingen ny plan, så intet at følge
        if (r.PowerAt is not { } at || r.PowerMode != j.Type || (j.Started is { } st && at < st.AddMinutes(-1))) return false;
        var (boot, precise) = PowerWatch.Boot(Json.Des<HealthReport>(m.HealthJson).Live, m.LastSeen);
        j.Status = JobStatus.Restarting;
        j.Finished = null;
        j.PowerAt = DateTime.SpecifyKind(at, DateTimeKind.Utc);
        j.BootBefore = boot; j.BootBeforePrecise = precise;
        j.Step = j.Type == JobTypes.Reboot ? "Venter på, at maskinen er tilbage" : "Venter på, at maskinen er slukket";
        j.ProgressAt = now;
        return true;
    }

    public async Task Tick(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var follow = now - PowerWatch.FollowHung;
        var jobs = await db.Jobs.Include(j => j.Machine)
            .Where(j => (j.Status == JobStatus.Restarting || (j.Status == JobStatus.Hung && j.HungAt > follow)) && j.PowerAt != null)
            .ToListAsync(ct);
        if (jobs.Count == 0) return;
        var hang = await ha.HangMinutes();
        var poll = (int)await settings.GetNum("poll_seconds", 30);
        var changed = new HashSet<int>();
        foreach (var j in jobs)
        {
            if (j.Machine is not { } m) continue;
            // en genstart, der kan installere en klar BIOS: længere frist, og ingen opfordring til at slå strømmen fra og til
            var bios = await Bios.BiosUpdateService.MayBeFlashing(db, m.Id, now);
            var jh = bios ? Math.Max(hang, Bios.BiosUpdateService.FlashHangMinutes) : hang;
            var p = Json.Des<JobParams>(j.ParamsJson);
            var liveFacts = Json.Des<HealthReport>(m.HealthJson).Live;
            var (bootNow, precise) = PowerWatch.Boot(liveFacts, m.LastSeen);
            // strømmåleren læses kun, hvor den gør en forskel: slukning (er den slukket?) og når fristen er gået
            double? watts = null;
            if (m.PowerSensor is { Length: > 0 } && (j.Type == JobTypes.Shutdown || now > j.PowerAt!.Value.AddMinutes(jh)))
                watts = await ha.Watts(m.PowerSensor, ct);
            var input = new PowerWatchInput(j.Type, j.PowerAt!.Value, j.ProgressAt ?? j.PowerAt.Value, j.Started, j.BootBefore, j.BootBeforePrecise,
                bootNow, precise, m.LastSeen, liveFacts.PowerMode, liveFacts.PowerAt, now, jh, poll, watts, m.PowerMinWatts, p.WolTest == true, j.WakeSentAt);
            var v = PowerWatch.Evaluate(input);
            if (await Apply(j, m, v, jh, now, bios)) changed.Add(m.Id);
        }
        await db.SaveChangesAsync(ct);
        foreach (var id in changed) await live.Changed("jobs", id);
    }

    private async Task<bool> Apply(Job j, Machine m, PowerVerdict v, int hang, DateTime now, bool bios = false)
    {
        var text = PowerWatch.Describe(v, j.Type, hang, (m.LastSeen ?? now).ToLocalTime());
        void Note() => j.Output = (j.Output.TrimEnd() + $"\n[Fleetdeck {now.ToLocalTime():HH:mm}] {text}").TrimStart('\n');
        switch (v.Kind)
        {
            case PowerVerdictKind.Wait:
                return false;
            case PowerVerdictKind.Moved:
                j.PowerAt = v.NewPlannedAt; Note(); return true;
            case PowerVerdictKind.SendWake:
                var sent = await WakeOnLan.Send(m.Mac, m.Ip);
                j.WakeSentAt = now;
                j.Step = sent ? "Wake-on-LAN sendt – venter på, at pc'en kommer op" : "Kunne ikke sende Wake-on-LAN (MAC-adressen kendes ikke)";
                Note();
                if (!sent) { j.Status = JobStatus.Failed; j.Finished = now; }
                return true;
            case PowerVerdictKind.Back or PowerVerdictKind.Off or PowerVerdictKind.WakeOk:
                var wasHung = j.Status == JobStatus.Hung;
                j.Status = JobStatus.Success; j.Finished = now; j.Step = ""; j.ExitCode ??= 0;
                Note();
                if (wasHung) notifier.Send($"🟢 {m.Label}: er tilbage\n{text}", "info");
                return true;
            case PowerVerdictKind.Cancelled:
                j.Status = JobStatus.Cancelled; j.Finished = now; j.Step = ""; Note(); return true;
            case PowerVerdictKind.WakeFailed:
                j.Status = JobStatus.Failed; j.Finished = now; j.Step = ""; Note();
                await audit.Log("system", "power.wol_failed", $"{m.Label}: {text}", m.Id);
                return true;
            case PowerVerdictKind.Hung:
                if (j.Status == JobStatus.Hung) return false;   // allerede meldt
                j.Status = JobStatus.Hung; j.HungAt = now; j.Step = "Hænger"; Note();
                var cycle = bios ? $" {Bios.BiosUpdateService.FlashingText}"
                    : m.PowerSwitch is { Length: > 0 } && PowerWatch.CycleRefusal(m.PowerSwitch, m.PowerSensor, v.Value, m.PowerMinWatts) is null
                    ? " Åbn maskinen i Fleetdeck for at slukke og tænde strømmen." : "";
                notifier.Send($"🔴 {m.Label}: {(j.Type == JobTypes.Reboot ? "genstarten" : "slukningen")} hænger\n{text}{cycle}");
                await audit.Log("system", "power.hung", $"{m.Label}: {text}", m.Id);
                log.LogWarning("{Machine}: {Text}", m.Label, text);
                return true;
        }
        return false;
    }

    /// <summary>
    /// Brugeren trykkede "Genstart nu"/"Sluk nu" på maskinen: skriv det i jobbet og revisionsloggen, og flyt vagtens tidspunkt til nu.
    /// Kun når der faktisk står et genstart/sluk-job og venter – ellers sker der intet.
    /// </summary>
    public async Task<bool> UserChoseNow(Machine m, string? mode)
    {
        if (mode is not (JobTypes.Reboot or JobTypes.Shutdown)) return false;
        var now = DateTime.UtcNow;
        var j = await db.Jobs.Where(x => x.MachineId == m.Id && x.Type == mode && x.Status == JobStatus.Restarting)
            .OrderByDescending(x => x.Id).FirstOrDefaultAsync();
        var note = PlannedPower.NowNote(mode, now.ToLocalTime());
        await audit.Log($"agent:{m.Label}", "power.now", $"{m.Label}: {note}", m.Id);
        if (j is null) return false;
        j.Output = (j.Output.TrimEnd() + $"\n{note}").TrimStart('\n');
        j.PowerAt = now;
        await db.SaveChangesAsync();
        await live.Changed("jobs", m.Id);
        return true;
    }

    /// <summary>
    /// "Sluk og tænd strømmen" via kontakten i Home Assistant. Kun med kontakt og måler, og kun når måleren viser, at maskinen
    /// trækker strøm. Bekræftelsen sker i brugerfladen; her kontrolleres resten. Et job, der hænger, følges igen bagefter.
    /// </summary>
    public async Task<string> PowerCycle(Machine m, string by, CancellationToken ct = default)
    {
        if (!await ha.Configured()) throw new InvalidOperationException("Home Assistant-API er ikke sat op (Indstillinger).");
        if (await Bios.BiosUpdateService.MayBeFlashing(db, m.Id, DateTime.UtcNow)) throw new InvalidOperationException(Bios.BiosUpdateService.FlashingText);
        var watts = await ha.Watts(m.PowerSensor, ct);
        if (PowerWatch.CycleRefusal(m.PowerSwitch, m.PowerSensor, watts, m.PowerMinWatts) is { } why) throw new InvalidOperationException(why);
        await ha.Switch(m.PowerSwitch!, on: false, ct);
        await Task.Delay(TimeSpan.FromSeconds(10), ct);
        await ha.Switch(m.PowerSwitch!, on: true, ct);
        var now = DateTime.UtcNow;
        var note = $"Strømmen blev slået fra og til af {by} kl. {now.ToLocalTime():HH:mm} (målt {Math.Round(watts!.Value)} W før).";
        await audit.Log(by, "power.cycle", $"{m.Label}: {note}", m.Id);
        var j = await db.Jobs.Where(x => x.MachineId == m.Id && (x.Status == JobStatus.Hung || x.Status == JobStatus.Restarting) && x.PowerAt != null)
            .OrderByDescending(x => x.Id).FirstOrDefaultAsync(ct);
        if (j is not null)
        {
            // genstart: følg igen, til den er tilbage. Slukning: tænd ikke igen af sig selv – jobbet er gjort færdigt
            j.Output = (j.Output.TrimEnd() + $"\n{note}").TrimStart('\n');
            if (j.Type == JobTypes.Reboot) { j.Status = JobStatus.Restarting; j.PowerAt = now; j.HungAt = null; j.Step = "Strømmen er slået fra og til – venter på, at maskinen er tilbage"; }
            await db.SaveChangesAsync(ct);
            await live.Changed("jobs", m.Id);
        }
        // tænder BIOS ikke selv efter strømsvigt ("Restore on AC Power Loss"), kan Wake-on-LAN gøre det
        var mac = m.Mac; var ip = m.Ip;
        if (j?.Type != JobTypes.Shutdown) _ = Task.Run(async () => { await Task.Delay(TimeSpan.FromSeconds(45)); await WakeOnLan.Send(mac, ip); });
        return note;
    }
}
