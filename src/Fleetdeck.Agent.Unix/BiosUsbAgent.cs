using System.Text.Json;
using Fleetdeck.Shared;

namespace Fleetdeck.Agent.Unix;

/// <summary>
/// USB-stik med BIOS-opdateringen (logikken ligger i Fleetdeck.Shared/BiosUsb.cs): usb_disks tager øjebliksbilledet og venter på et
/// nyt stik; bios_usb_write skriver kun det stik, brugeren har bekræftet her på pc'en (/_fd/confirm) inden for 5 minutter.
/// Browseren på samme pc taler med agenten gennem LocalWeb (kun loopback, kun samme side) – agentens token forlader aldrig agenten.
/// </summary>
internal static class BiosUsbAgent
{
    private static (List<BlockDev> Devs, Dictionary<string, string> ById)? Scan(ISpareHost h)
    {
        var ls = h.Run("lsblk", ["-J", "-b", "-o", SpareCard.LsblkColumns], timeoutMin: 1);
        if (ls.Exit != 0) return null;
        var byId = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var n in h.ListDir("/dev/disk/by-id"))
            if (h.RealPath("/dev/disk/by-id/" + n) is { } t) byId[n] = t;
        return (BiosUsb.WithSeq(SpareCard.ParseLsblk(ls.Output), h), byId);
    }

    public static async Task<JobResult> Run(Api api, AgentJob j, CancellationToken ct)
    {
        if (j.Params.BiosUsb is not { SessionId.Length: > 0 } o) return Fail("Jobbet mangler guidens session.");
        if (Mac.Is) return await RunMac(api, j, o, ct);
        var h = new RealSpareHost();
        var local = BiosUsbLocal.Shared;
        if (j.Type == JobTypes.UsbDisks)
        {
            var rep = new BiosUsbReport { SessionId = o.SessionId, Phase = BiosUsbPhases.List };
            if (local.Baseline(o.SessionId) is null)
            {
                if (await Task.Run(() => Scan(h), ct) is not { } first) return Fail("lsblk fejlede.");
                local.SetBaseline(o.SessionId, first.Devs.Where(d => d.Type == "disk").Select(BiosUsb.Key));
                rep.Baseline = true;
                rep.Disks = BiosUsb.Evaluate(first.Devs, first.ById, local.Baseline(o.SessionId), o.MaxBytes);
                rep.Message = "Øjebliksbilledet er taget. Sæt USB-stikket i nu.";
                return Ok(rep);
            }
            // vent op til 90 sekunder på et stik, der ikke sad i før
            var until = DateTime.UtcNow.AddSeconds(90);
            while (true)
            {
                if (await Task.Run(() => Scan(h), ct) is not { } s) return Fail("lsblk fejlede.");
                local.Forget(o.SessionId, s.Devs.Where(d => d.Type == "disk").Select(BiosUsb.Key));
                rep.Disks = BiosUsb.Evaluate(s.Devs, s.ById, local.Baseline(o.SessionId), o.MaxBytes);
                if (rep.Disks.Any(d => d.Why != BiosUsb.NotNew) || DateTime.UtcNow > until) break;
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
            rep.Message = rep.Disks.Any(d => d.Selectable || d.Large) ? "Stikket er fundet." : "Intet nyt stik fundet.";
            return Ok(rep);
        }

        // bios_usb_write: hent pakken til en mappe kun root kan læse, tjek den og skriv stikket
        var work = Path.Combine(LinuxCapsuleUpdater.DefaultWork, "usb-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(work, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var file = Path.Combine(work, "package.bin");
            LiveJob.Phase(0, 5, "Henter pakken …");
            try { await api.Download($"/api/agent/bios/usb/{Uri.EscapeDataString(o.SessionId)}/package", file, ct); }
            catch (HttpRequestException e) { return Fail("Pakken kunne ikke hentes fra Fleetdeck: " + e.Message); }
            var log = new List<string>();
            var rep = await Task.Run(() => BiosUsb.Write(h, o, file, local, log.Add, LiveJob.Phase), ct);
            return new JobResult
            {
                Status = rep.Outcome == "ok" ? JobStatus.Success : JobStatus.Failed, ExitCode = rep.Outcome == "ok" ? 0 : 1,
                Output = string.Join('\n', log), BiosUsb = rep,
            };
        }
        finally { try { Directory.Delete(work, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
    }

    /// <summary>
    /// /_fd/claim og /_fd/confirm fra browseren på denne pc (LocalWeb har allerede tjekket loopback, Host, Origin og X-Fleetdeck).
    /// claim: send engangskoden til serveren over agentens egen kanal, så Fleetdeck ved, hvilken pc guiden kører på.
    /// confirm: brugeren har bekræftet stikket her – gem det i 5 minutter og giv en kvittering til jobbet.
    /// </summary>
    public static async Task<LocalWeb.LocalReply> Local(Api api, string path, IReadOnlyDictionary<string, string> q, CancellationToken ct)
    {
        switch (path)
        {
            case "/_fd/claim":
                if (!q.TryGetValue("nonce", out var nonce) || nonce.Length is < 16 or > 128) return LocalWeb.LocalReply.Error(400, "Engangskoden mangler.");
                try
                {
                    var r = await api.Post<BiosUsbClaimResponse>("/api/agent/bios/usb/claim", new BiosUsbClaimRequest { Nonce = nonce }, ct);
                    return LocalWeb.LocalReply.Json(r);
                }
                catch (HttpRequestException e) { return LocalWeb.LocalReply.Error(409, e.StatusCode is null ? "Fleetdeck kan ikke nås fra denne pc." : "Engangskoden er ikke gyldig længere. Start guiden igen."); }
            case "/_fd/confirm":
            {
                if (!q.TryGetValue("session", out var sid) || !q.TryGetValue("disk", out var disk) || !q.TryGetValue("serial", out var serial)
                    || !q.TryGetValue("size", out var sz) || !long.TryParse(sz, out var size) || !SpareCard.SafeId(disk) || sid.Length is 0 or > 64)
                    return LocalWeb.LocalReply.Error(400, "Ugyldig bekræftelse.");
                var h = new RealSpareHost();
                if (Mac.Is) return await ConfirmMac(h, q, sid, disk, serial, size, ct);
                if (await Task.Run(() => Scan(h), ct) is not { } s) return LocalWeb.LocalReply.Error(500, "lsblk fejlede.");
                var max = q.TryGetValue("max", out var m) && long.TryParse(m, out var mm) && mm > 0 ? mm : BiosUsb.DefaultMaxBytes;
                var d = BiosUsb.Evaluate(s.Devs, s.ById, BiosUsbLocal.Shared.Baseline(sid), max).FirstOrDefault(x => x.Id == disk);
                if (d is null) return LocalWeb.LocalReply.Error(409, "Stikket er ikke sat i længere.");
                if (d.Serial != serial || d.Size != size) return LocalWeb.LocalReply.Error(409, "Stikket er ikke det samme som det, der blev vist.");
                if (!d.Usable(q.TryGetValue("large", out var lg) && lg == "1", max)) return LocalWeb.LocalReply.Error(409, d.Why ?? "Stikket kan ikke bruges.");
                return LocalWeb.LocalReply.Json(new { hash = BiosUsbLocal.Shared.Confirm(sid, disk, serial, size) });
            }
            default: return LocalWeb.LocalReply.Error(404, "Ukendt.");
        }
    }

    // ------------------------------------------------------------------ Mac (diskutil, se Fleetdeck.Shared/BiosUsbMac.cs)

    /// <summary>Agentens egen mappe på Mac'en (kun root): pakken hentes og stikket monteres her.</summary>
    private const string MacWork = MacPaths.AgentDir + "/bios-usb";

    private static void MacWorkDir()
    {
        const UnixFileMode RootOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        Directory.CreateDirectory(MacWork, RootOnly);
        File.SetUnixFileMode(MacWork, RootOnly);   // også hvis mappen fandtes i forvejen
    }

    private static async Task<JobResult> RunMac(Api api, AgentJob j, BiosUsbOrder o, CancellationToken ct)
    {
        var h = new RealSpareHost();
        var local = BiosUsbLocal.Shared;
        if (j.Type == JobTypes.UsbDisks)
        {
            var rep = new BiosUsbReport { SessionId = o.SessionId, Phase = BiosUsbPhases.List };
            if (local.Baseline(o.SessionId) is null)
            {
                if (await Task.Run(() => BiosUsbMac.Scan(h), ct) is not { } first) return Fail(BiosUsbMac.ListFailed);
                local.SetBaseline(o.SessionId, first.Disks.Select(BiosUsbMac.Key));
                rep.Baseline = true;
                rep.Disks = BiosUsbMac.Evaluate(first, local.Baseline(o.SessionId), o.MaxBytes);
                rep.Message = "Øjebliksbilledet er taget. Sæt USB-stikket i nu.";
                return Ok(rep);
            }
            var until = DateTime.UtcNow.AddSeconds(90);
            while (true)
            {
                // en mislykket læsning glemmer intet (ellers ville diske, der sad i før guiden, se nye ud) – prøv igen til tiden er gået
                if (await Task.Run(() => BiosUsbMac.Scan(h), ct) is not { } s)
                {
                    if (DateTime.UtcNow > until) return Fail(BiosUsbMac.ListFailed);
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                    continue;
                }
                local.Forget(o.SessionId, s.Disks.Select(BiosUsbMac.Key));
                rep.Disks = BiosUsbMac.Evaluate(s, local.Baseline(o.SessionId), o.MaxBytes);
                if (rep.Disks.Any(d => d.Why != BiosUsb.NotNew) || DateTime.UtcNow > until) break;
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
            rep.Message = rep.Disks.Any(d => d.Selectable || d.Large) ? "Stikket er fundet." : "Intet nyt stik fundet.";
            return Ok(rep);
        }

        // bios_usb_write
        var work = Path.Combine(MacWork, "usb-" + Guid.NewGuid().ToString("N"));
        try
        {
            MacWorkDir();
            Directory.CreateDirectory(work, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var file = Path.Combine(work, "package.bin");
            LiveJob.Phase(0, 5, "Henter pakken …");
            try { await api.Download($"/api/agent/bios/usb/{Uri.EscapeDataString(o.SessionId)}/package", file, ct); }
            catch (HttpRequestException e) { return Fail("Pakken kunne ikke hentes fra Fleetdeck: " + e.Message); }
            var log = new List<string>();
            var media = BiosUsbMac.TakeMedia(o.ConfirmHash);
            var rep = await Task.Run(() => BiosUsbMac.Write(h, o, file, local, media, log.Add, LiveJob.Phase, MacWork), ct);
            return new JobResult
            {
                Status = rep.Outcome == "ok" ? JobStatus.Success : JobStatus.Failed, ExitCode = rep.Outcome == "ok" ? 0 : 1,
                Output = string.Join('\n', log), BiosUsb = rep,
            };
        }
        catch (UnauthorizedAccessException) { return Fail(BiosUsbMac.MacAccess); }
        finally { try { Directory.Delete(work, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
    }

    /// <summary>/_fd/confirm på en Mac: samme tjek som på Linux, og medienavnet, agenten selv læser nu, bindes til kvitteringen.</summary>
    private static async Task<LocalWeb.LocalReply> ConfirmMac(RealSpareHost h, IReadOnlyDictionary<string, string> q, string sid, string disk, string serial,
        long size, CancellationToken ct)
    {
        if (!BiosUsbMac.SafeWhole(disk)) return LocalWeb.LocalReply.Error(400, "Ugyldig bekræftelse.");
        if (await Task.Run(() => BiosUsbMac.Scan(h), ct) is not { } s) return LocalWeb.LocalReply.Error(500, BiosUsbMac.ListFailed);
        var max = q.TryGetValue("max", out var m) && long.TryParse(m, out var mm) && mm > 0 ? mm : BiosUsb.DefaultMaxBytes;
        var d = BiosUsbMac.Evaluate(s, BiosUsbLocal.Shared.Baseline(sid), max).FirstOrDefault(x => x.Id == disk);
        if (d is null) return LocalWeb.LocalReply.Error(409, "Stikket er ikke sat i længere.");
        if (d.Serial != serial || d.Size != size || q.TryGetValue("model", out var model) && model != d.Model)
            return LocalWeb.LocalReply.Error(409, "Stikket er ikke det samme som det, der blev vist.");
        if (!d.Usable(q.TryGetValue("large", out var lg) && lg == "1", max)) return LocalWeb.LocalReply.Error(409, d.Why ?? "Stikket kan ikke bruges.");
        var hash = BiosUsbLocal.Shared.Confirm(sid, disk, serial, size);
        BiosUsbMac.RememberMedia(hash, d.Model);
        return LocalWeb.LocalReply.Json(new { hash });
    }

    private static JobResult Ok(BiosUsbReport r) => new() { Status = JobStatus.Success, ExitCode = 0, Output = r.Message, BiosUsb = r };
    private static JobResult Fail(string text) => new() { Status = JobStatus.Failed, ExitCode = 1, Output = text };
}
