using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;
using Fleetdeck.Server.Data;
using Fleetdeck.Shared;
using Microsoft.EntityFrameworkCore;

namespace Fleetdeck.Server.Services;

/// <summary>
/// Reservekort på en Raspberry Pi: vælg et USB-kort (fra agentens liste), bekræft én gang, at alt på det må slettes, og få en kopi
/// af hele systemet hver nat (Fleetdeck.Shared/SpareCard.cs kører på Pi'en). Slået fra som standard: uden et bekræftet kort laves der aldrig et job.
/// Status og alder vises på maskinens side og som sensorer i Home Assistant; over 48 timer gammel giver en alarm.
/// </summary>
public sealed class SpareCardService(AppDb db, JobService jobs, AuditService audit, SettingsService settings, Live live, Paths paths)
{
    public sealed record Input(bool? Enabled, string? Time, bool? Netboot);
    public sealed record Choose(string DiskId, bool Confirm);

    public static bool IsPi(Machine m) => Json.Des<SystemFacts>(m.FactsJson).PiSerial is not null
        || (Json.Des<SystemFacts>(m.FactsJson).Model ?? "").Contains("Raspberry Pi", StringComparison.OrdinalIgnoreCase);

    public async Task<object?> View(int machineId)
    {
        var m = await db.Machines.AsNoTracking().FirstOrDefaultAsync(x => x.Id == machineId);
        if (m is null) return null;
        var s = await db.SpareCards.AsNoTracking().FirstOrDefaultAsync(x => x.MachineId == machineId) ?? new SpareCardState { MachineId = machineId };
        var h = Json.Des<HealthReport>(m.HealthJson);
        var running = await db.Jobs.AsNoTracking().Where(j => j.MachineId == machineId && j.Type == JobTypes.SpareCard && (j.Status == JobStatus.Pending || j.Status == JobStatus.Running))
            .OrderByDescending(j => j.Id).Select(j => new { j.Id, j.Status, j.Progress, j.Step }).FirstOrDefaultAsync();
        var now = DateTime.UtcNow;
        var chosen = s.ConfirmedAt is not null && s.DiskId.Length > 0;
        var seen = h.SpareDisks?.FirstOrDefault(d => d.Id == s.DiskId);
        return new
        {
            machineId, isPi = IsPi(m), reported = h.SpareDisks is not null,
            candidates = (h.SpareDisks ?? []).Select(d => new { d.Id, d.Path, d.Size, d.Model, d.Vendor, d.Serial, d.Partitions, d.Mounted, d.Empty, label = SpareCard.Label(d) }),
            chosen, s.Enabled, s.Time, netbootOn = s.Netboot, s.DiskId, s.DiskLabel, s.Serial, s.Size, s.ConfirmedAt, s.ConfirmedBy,
            present = chosen ? seen is not null && !seen.Empty : (bool?)null,
            s.LastRun, s.LastOk, s.Status, s.Message, s.Kernel, s.Bytes,
            ageHours = s.LastOk is { } ok ? Math.Round((now - ok).TotalHours, 1) : (double?)null,
            stale = chosen && s.Enabled && SpareCard.Stale(s.LastOk, s.ConfirmedAt, now),
            next = chosen && s.Enabled ? Schedule.Next("daily", s.Time, 0) : null,
            job = running,
            netbootSync = new { status = s.NetbootStatus, message = s.NetbootMessage, at = s.NetbootAt, signature = s.NetbootSignature },
        };
    }

    /// <summary>Vælg og bekræft kortet. Alt på det slettes ved første kopi; kortet, Pi'en kører fra, kan ikke vælges (agenten viser det aldrig).</summary>
    public async Task<string?> Pick(int machineId, Choose c, string by)
    {
        var m = await db.Machines.FirstOrDefaultAsync(x => x.Id == machineId);
        if (m is null) return "Maskinen findes ikke";
        if (!IsPi(m)) return "Reservekort findes kun på Raspberry Pi";
        if (!c.Confirm) return "Bekræft, at alt på kortet må slettes";
        var d = Json.Des<HealthReport>(m.HealthJson).SpareDisks?.FirstOrDefault(x => x.Id == c.DiskId);
        if (d is null || !SpareCard.SafeId(d.Id)) return "Kortet er ikke på Pi'ens liste over USB-diske. Sæt kortlæseren i, og vent et øjeblik.";
        if (SpareCard.IsInternalCard(d.Path)) return "Pi'ens eget kortstik kan ikke bruges";
        if (d.Empty) return "Der sidder intet kort i kortlæseren";
        if (d.Mounted) return "Kortet er monteret på Pi'en. Afmonter det først.";
        var s = await Get(machineId);
        s.DiskId = d.Id; s.DiskLabel = SpareCard.Label(d); s.Serial = d.Serial; s.Size = d.Size;
        s.ConfirmedAt = DateTime.UtcNow; s.ConfirmedBy = by;
        s.KnownDiskId = null; s.LastOk = null; s.LastRun = null; s.Status = ""; s.Message = ""; s.Bytes = 0;
        await db.SaveChangesAsync();
        await audit.Log(by, "sparecard.choose", $"{m.Label}: {s.DiskLabel} ({d.Id}) – alt på kortet må slettes", m.Id);
        await live.Changed("machines", m.Id);
        return null;
    }

    public async Task Clear(int machineId, string by)
    {
        if (await db.SpareCards.FirstOrDefaultAsync(x => x.MachineId == machineId) is not { } s) return;
        var label = s.DiskLabel;
        db.SpareCards.Remove(s);
        await db.SaveChangesAsync();
        await audit.Log(by, "sparecard.clear", $"Reservekort fravalgt ({label})", machineId);
        await live.Changed("machines", machineId);
    }

    public async Task<string?> Update(int machineId, Input i, string by)
    {
        var s = await Get(machineId);
        if (i.Enabled == true && (s.ConfirmedAt is null || s.DiskId.Length == 0)) return "Vælg og bekræft et reservekort først";
        if (i.Time is { } t) { if (!TimeOnly.TryParse(t, out var to)) return "Skriv tidspunktet som TT:MM"; s.Time = to.ToString("HH:mm"); }
        if (i.Netboot is { } nb) s.Netboot = nb;
        if (i.Enabled is { } en)
        {
            if (en != s.Enabled) await audit.Log(by, en ? "sparecard.on" : "sparecard.off", $"Kopi hver nat kl. {s.Time}", machineId);
            s.Enabled = en;
        }
        await db.SaveChangesAsync();
        await live.Changed("machines", machineId);
        return null;
    }

    /// <summary>Lav et kopijob nu (eller fra planlæggeren). Aldrig uden et bekræftet kort, og aldrig to på én gang.</summary>
    public async Task<(Job? Job, string? Error)> Start(int machineId, string source, string by)
    {
        var s = await db.SpareCards.FirstOrDefaultAsync(x => x.MachineId == machineId);
        if (s is null || s.ConfirmedAt is null || !SpareCard.SafeId(s.DiskId)) return (null, "Vælg og bekræft et reservekort først");
        if (await db.Jobs.AnyAsync(j => j.MachineId == machineId && j.Type == JobTypes.SpareCard && (j.Status == JobStatus.Pending || j.Status == JobStatus.Running)))
            return (null, "En kopi er allerede i gang");
        var p = new JobParams
        {
            TimeoutMin = 360,
            Spare = new SpareCardJob { DiskId = s.DiskId, Serial = s.Serial.Length > 0 ? s.Serial : null, KnownDiskId = s.KnownDiskId, Netboot = s.Netboot, NetbootSignature = s.NetbootSignature },
        };
        var j = await jobs.Create(machineId, JobTypes.SpareCard, "Kopi til reservekort", p, RunAs.System, null, source, by, false);
        s.Status = "kører"; s.LastRun = DateTime.UtcNow; s.Message = "Venter på Pi'en";
        await jobs.Commit(machineId);
        s.JobId = j.Id;
        await db.SaveChangesAsync();
        return (j, null);
    }

    /// <summary>Planlæggeren (hvert 30. sekund): én kopi pr. nat pr. Pi, inden for en time efter det valgte tidspunkt.</summary>
    public async Task Tick(DateTime nowLocal)
    {
        await Reconcile();
        var today = DateOnly.FromDateTime(nowLocal).ToString("O");
        foreach (var s in await db.SpareCards.AsNoTracking().Where(x => x.Enabled && x.ConfirmedAt != null).ToListAsync())
        {
            if (!Schedule.Due("daily", s.Time, 0, nowLocal)) continue;
            var key = $"sched:sparecard:{s.MachineId}";
            if (await settings.Get(key) == today) continue;
            await settings.Set(key, today);
            var (_, err) = await Start(s.MachineId, "schedule", "planlægger");
            if (err is null) await audit.Log("planlægger", "schedule.sparecard", "Kopi til reservekort", s.MachineId);
        }
    }

    /// <summary>Et kopijob, der sluttede uden svar fra Pi'en (annulleret, for gammel agent, Pi'en genstartet): vis det som en fejl
    /// i stedet for "kopierer" for evigt.</summary>
    public async Task Reconcile()
    {
        var hanging = await db.SpareCards.Where(x => x.Status == "kører" && x.JobId != null).ToListAsync();
        if (hanging.Count == 0) return;
        foreach (var s in hanging)
        {
            var j = await db.Jobs.AsNoTracking().Where(x => x.Id == s.JobId).Select(x => new { x.Status, x.Output }).FirstOrDefaultAsync();
            if (j is not null && j.Status is JobStatus.Pending or JobStatus.Running) continue;
            var last = j?.Output.Trim().Split('\n').LastOrDefault()?.Trim() ?? "";
            s.Status = "fejl";
            s.Message = Cut(last.Length > 0 ? $"Kopijobbet stoppede uden svar fra Pi'en: {last}" : "Kopijobbet stoppede uden svar fra Pi'en", 400);
        }
        await db.SaveChangesAsync();
    }

    /// <summary>Agentens svar efter en kopi.</summary>
    public async Task<bool> FromAgent(Machine m, SpareCardReport r)
    {
        var s = await db.SpareCards.FirstOrDefaultAsync(x => x.MachineId == m.Id);
        if (s is null || s.ConfirmedAt is null) return false;
        if (!await db.Jobs.AnyAsync(j => j.Id == r.JobId && j.MachineId == m.Id && j.Type == JobTypes.SpareCard)) return false;
        Apply(s, r, DateTime.UtcNow);
        await db.SaveChangesAsync();
        await live.Changed("machines", m.Id);
        return true;
    }

    /// <summary>Gem resultatet (ren logik, så den kan testes).</summary>
    public static void Apply(SpareCardState s, SpareCardReport r, DateTime now)
    {
        s.Status = r.Status is "ok" or "fejl" ? r.Status : "fejl";
        s.Message = Cut(r.Message, 400);
        s.LastRun = now;
        if (s.Status == "ok") { s.LastOk = now; s.Bytes = r.Bytes; }
        if (r.DiskId is { Length: > 0 and <= 40 } id && s.Status == "ok") s.KnownDiskId = id;
        if (r.Kernel is { Length: > 0 } k) s.Kernel = Cut(k, 80);
    }

    private static string Cut(string s, int n) => s.Length > n ? s[..n] : s;

    /// <summary>Netboot-pakken fra Pi'en: kerne og moduler til nødsystemet. Kun mens et kopijob for maskinen kører.</summary>
    public async Task<(bool Ok, string? Error)> NetbootUpload(Machine m, int jobId, string signature, string kernel, Stream body, CancellationToken ct)
    {
        var s = await db.SpareCards.FirstOrDefaultAsync(x => x.MachineId == m.Id, ct);
        if (s is null || !s.Netboot) return (false, "Netboot er ikke slået til for reservekortet");
        if (!await db.Jobs.AnyAsync(j => j.Id == jobId && j.MachineId == m.Id && j.Type == JobTypes.SpareCard && j.Status == JobStatus.Running, ct)) return (false, "Intet kopijob kører");
        var serial = Json.Des<SystemFacts>(m.FactsJson).PiSerial;
        if (serial is null || !PiNetboot.Serial().IsMatch(serial)) return (false, "Fleetdeck kender ikke Pi'ens serienummer");
        var dir = Path.Combine(paths.Pxe, "pi", "sync", serial);
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, "netboot.tar.gz.upload");
        await using (var f = File.Create(tmp)) await body.CopyToAsync(f, ct);
        var res = NetbootSync.Apply(Path.Combine(paths.Pxe, "tftp", serial), Path.Combine(paths.Pxe, "pi", "nfsroot", serial), tmp, kernel);
        try { File.Delete(tmp); } catch (IOException) { }
        s.NetbootStatus = res.Status; s.NetbootMessage = Cut(res.Message, 300); s.NetbootAt = DateTime.UtcNow;
        if (res.Status == "ok") s.NetbootSignature = Cut(signature, 120);
        await db.SaveChangesAsync(ct);
        await audit.Log("agent", "sparecard.netboot", $"{m.Label}: {res.Message}", m.Id);
        return (true, null);
    }

    private async Task<SpareCardState> Get(int machineId)
    {
        var s = await db.SpareCards.FirstOrDefaultAsync(x => x.MachineId == machineId);
        if (s is null) { s = new SpareCardState { MachineId = machineId }; db.SpareCards.Add(s); }
        return s;
    }

    /// <summary>Alarmen: kopien er over 48 timer gammel (eller er aldrig lykkedes 48 timer efter, at kortet blev valgt).</summary>
    public static Finding? Finding(SpareCardState? s, DateTime now)
    {
        if (s is null || !s.Enabled || s.ConfirmedAt is null || !SpareCard.Stale(s.LastOk, s.ConfirmedAt, now)) return null;
        var detail = s.LastOk is { } ok ? $"Seneste gode kopi for {(now - ok).TotalHours:0} timer siden" : "Ingen kopi er lykkedes endnu";
        if (s.Status == "fejl" && s.Message.Length > 0) detail += $". Sidst: {s.Message}";
        return new Finding("sparecard", "warning", "Reservekortet er for gammelt", detail);
    }
}

/// <summary>
/// Hold netboot-nødsystemet friskt: kerne, dtb'er, overlays og firmware lægges i PXE/tftp/&lt;serienummer&gt;/, og modulerne i
/// NFS-roden – men kun hvis NFS-roden er sat ind i containeren under PXE/pi/nfsroot/&lt;serienummer&gt;. Ellers skiftes kernen ikke,
/// for en ny kerne uden sine moduler starter ikke. config.txt og cmdline.txt røres aldrig. Den Pi, der kører, ændres ikke.
/// </summary>
public static class NetbootSync
{
    public sealed record Result(string Status, string Message);

    public static Result Apply(string tftpDir, string nfsRoot, string tarGz, string kernel)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(kernel, @"^[\w.+\-]{1,80}$")) return new("fejl", "Ugyldig kerneversion");
        if (!Directory.Exists(tftpDir) || (!File.Exists(Path.Combine(tftpDir, "start.elf")) && !Directory.EnumerateFiles(tftpDir, "kernel*.img").Any()))
            return new("sprunget", "Netboot er ikke sat op for Pi'en (ingen opstartsfiler i PXE/tftp)");
        var modulesBase = Directory.Exists(Path.Combine(nfsRoot, "usr", "lib", "modules")) ? Path.Combine(nfsRoot, "usr", "lib", "modules")
            : Directory.Exists(Path.Combine(nfsRoot, "lib", "modules")) ? Path.Combine(nfsRoot, "lib", "modules") : null;
        if (modulesBase is null)
            return new("venter", $"NFS-roden er ikke sat ind i Fleetdeck (PXE/pi/nfsroot/{Path.GetFileName(nfsRoot)}). Kernen i nødsystemet er ikke skiftet.");

        var stage = tftpDir.TrimEnd('/') + ".stage";
        if (Directory.Exists(stage)) Directory.Delete(stage, true);
        Directory.CreateDirectory(stage);
        try
        {
            var (boot, mods) = Extract(tarGz, stage, kernel);
            if (boot == 0 || !Directory.EnumerateFiles(Path.Combine(stage, "boot"), "kernel*.img").Any()) return new("fejl", "Pakken fra Pi'en har ingen kerne");
            if (mods == 0) return new("fejl", $"Pakken fra Pi'en har ingen moduler til {kernel}");
            // først modulerne (en ny kerne uden moduler starter ikke) – andre kerners moduler bliver liggende
            CopyTree(Path.Combine(stage, "modules"), Path.Combine(modulesBase, kernel));
            CopyTree(Path.Combine(stage, "boot"), tftpDir);
            return new("ok", $"Nødsystemet har nu kerne {kernel} ({boot} opstartsfiler, {mods} modulfiler)");
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or FormatException)
        {
            return new("fejl", "Nødsystemet kunne ikke opdateres: " + e.Message);
        }
        finally { try { Directory.Delete(stage, true); } catch (IOException) { } }
    }

    /// <summary>Pak kun de tilladte filer ud: opstartsfiler (ikke config.txt/cmdline.txt), overlays og modulerne til netop den kerne.
    /// Ingen links, ingen "..", ingen absolutte stier.</summary>
    public static (int Boot, int Modules) Extract(string tarGz, string stage, string kernel)
    {
        int boot = 0, mods = 0;
        using var fs = File.OpenRead(tarGz);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var tr = new TarReader(gz);
        while (tr.GetNextEntry() is { } e)
        {
            if (e.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)) continue;
            var name = e.Name.Replace('\\', '/').TrimStart('.', '/');
            if (name.Split('/').Any(p => p is ".." or "")) continue;
            string? to = null;
            foreach (var b in new[] { "boot/firmware/", "boot/" })
            {
                if (!name.StartsWith(b, StringComparison.Ordinal)) continue;
                var rest = name[b.Length..];
                if (!rest.Contains('/') && SpareCard.NetbootBootFile(rest)) to = Path.Combine(stage, "boot", rest);
                else if (rest.StartsWith("overlays/", StringComparison.Ordinal) && rest.Count(ch => ch == '/') == 1) to = Path.Combine(stage, "boot", rest);
                break;
            }
            foreach (var mb in new[] { $"lib/modules/{kernel}/", $"usr/lib/modules/{kernel}/" })
                if (name.StartsWith(mb, StringComparison.Ordinal)) to = Path.Combine(stage, "modules", name[mb.Length..]);
            if (to is null) continue;
            var full = Path.GetFullPath(to);
            if (!full.StartsWith(Path.GetFullPath(stage) + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            e.ExtractToFile(full, true);
            if (full.StartsWith(Path.GetFullPath(Path.Combine(stage, "modules")) + Path.DirectorySeparatorChar, StringComparison.Ordinal)) mods++; else boot++;
        }
        return (boot, mods);
    }

    private static void CopyTree(string from, string to)
    {
        if (!Directory.Exists(from)) return;
        foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(to, Path.GetRelativePath(from, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(f, dest + ".new", true);
            File.Move(dest + ".new", dest, true);
        }
    }
}

/// <summary>Reservekortet som sensorer på Pi'ens enhed i Home Assistant (MQTT discovery).</summary>
public static class SpareCardHa
{
    public static string StateTopic(string @base, int machineId) => $"{@base}/{machineId}/sparecard";

    public static IEnumerable<string> ConfigTopics(string prefix, string @base, int machineId)
    {
        yield return $"{prefix}/sensor/{@base}_{machineId}/sparecard_status/config";
        yield return $"{prefix}/sensor/{@base}_{machineId}/sparecard_age/config";
        yield return $"{prefix}/sensor/{@base}_{machineId}/sparecard_last/config";
        yield return $"{prefix}/binary_sensor/{@base}_{machineId}/sparecard_stale/config";
    }

    public static List<(string Topic, string Payload)> Configs(string prefix, string @base, int machineId, string slug)
    {
        var state = StateTopic(@base, machineId);
        var device = new Dictionary<string, object?> { ["identifiers"] = new[] { $"{@base}_{machineId}" } };
        var avail = new[] { new { topic = $"{@base}/status" } };
        string Conf(string obj, string name, Dictionary<string, object?> extra)
        {
            var c = new Dictionary<string, object?>
            {
                ["name"] = name, ["unique_id"] = $"{@base}_{machineId}_{obj}", ["object_id"] = $"{@base}_{slug}_{obj}",
                ["device"] = device, ["availability"] = avail, ["state_topic"] = state,
            };
            foreach (var (k, v) in extra) if (v is not null) c[k] = v;
            return JsonSerializer.Serialize(c);
        }
        var t = ConfigTopics(prefix, @base, machineId).ToList();
        return
        [
            (t[0], Conf("sparecard_status", "Reservekort", new() { ["value_template"] = "{{ value_json.status }}", ["icon"] = "mdi:sd" })),
            (t[1], Conf("sparecard_age", "Reservekort alder", new() { ["value_template"] = "{{ value_json.age_h }}", ["unit_of_measurement"] = "h", ["state_class"] = "measurement", ["icon"] = "mdi:timer-sand" })),
            (t[2], Conf("sparecard_last", "Reservekort seneste kopi", new() { ["value_template"] = "{{ value_json.last_ok }}", ["device_class"] = "timestamp" })),
            (t[3], Conf("sparecard_stale", "Reservekort for gammelt", new() { ["value_template"] = "{{ value_json.stale }}", ["device_class"] = "problem" })),
        ];
    }

    public static string State(SpareCardState s, DateTime now) => JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["status"] = !s.Enabled ? "slået fra" : s.Status switch { "ok" => "ok", "fejl" => "fejl", "kører" => "kopierer", _ => "venter" },
        ["age_h"] = s.LastOk is { } ok ? Math.Round((now - ok).TotalHours, 1) : null,
        ["last_ok"] = s.LastOk?.ToUniversalTime().ToString("o"),
        ["stale"] = s.Enabled && SpareCard.Stale(s.LastOk, s.ConfirmedAt, now) ? "ON" : "OFF",
    });
}
