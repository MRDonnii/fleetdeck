using Fleetdeck.Shared;

namespace Fleetdeck.Agent.Unix;

/// <summary>
/// Reservekort på en Raspberry Pi (logikken ligger i Fleetdeck.Shared/SpareCard.cs): agenten melder, hvilke USB-diske Pi'en kan se,
/// og kører den natlige kopi, når Fleetdeck sender et spare_card-job. Uden et valgt og bekræftet kort kommer der aldrig et job.
/// </summary>
internal static class SpareCardAgent
{
    public static bool IsPi => Collect.PiSerialNumber() is not null;

    /// <summary>USB-diske, der kan bruges som reservekort (til helbredet). Null på alt andet end en Pi.</summary>
    public static List<SpareDisk>? Candidates()
    {
        if (!OperatingSystem.IsLinux() || !IsPi) return null;
        var h = new RealSpareHost();
        var ls = h.Run("lsblk", ["-J", "-b", "-o", SpareCard.LsblkColumns], timeoutMin: 1);
        if (ls.Exit != 0) return [];
        var byId = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var n in h.ListDir("/dev/disk/by-id"))
            if (h.RealPath("/dev/disk/by-id/" + n) is { } t) byId[n] = t;
        return SpareCard.Candidates(SpareCard.ParseLsblk(ls.Output), byId);
    }

    public static async Task<JobResult> Run(Api api, AgentJob j, CancellationToken ct)
    {
        if (!IsPi) return Fail("Reservekort findes kun på Raspberry Pi");
        if (j.Params.Spare is not { } p) return Fail("Jobbet mangler indstillingerne for reservekortet");
        var h = new RealSpareHost();
        var log = new System.Text.StringBuilder();
        var rep = await Task.Run(() => SpareCard.Run(h, p, l => { log.AppendLine(l); Log.Info("Reservekort: " + l); }), ct);
        rep.JobId = j.Id;

        // netboot-nødsystemet: kerne, opstartsfiler og moduler sendes kun, når kernen er en anden end nødsystemets (læses kun)
        if (rep.Status != "fejl" && p.Netboot && rep.Kernel is { Length: > 0 } kver)
        {
            try
            {
                var boot = Directory.Exists("/boot/firmware") && File.Exists("/boot/firmware/config.txt") ? "/boot/firmware" : "/boot";
                var files = Directory.GetFiles(boot).Select(f => (Name: Path.GetFileName(f), Size: new FileInfo(f).Length)).ToList();
                var sig = SpareCard.NetbootSignature(kver, files);
                if (sig != p.NetbootSignature)
                {
                    var tar = $"{UnixRoot.EnsureRootOnly(SpareCard.Work)}/netboot.tar.gz";
                    var paths = SpareCard.NetbootPaths(boot, files.Select(f => f.Name), kver).Where(x => Path.Exists("/" + x)).ToList();
                    var t = h.Run("nice", ["-n", "19", "tar", "-czf", tar, "-C", "/", .. paths], timeoutMin: 30);
                    if (t.Exit == 0)
                    {
                        await api.PutFile($"/api/agent/sparecard/netboot?jobId={j.Id}&signature={Uri.EscapeDataString(sig)}&kernel={Uri.EscapeDataString(kver)}", tar, ct);
                        rep.NetbootSignature = sig;
                        log.AppendLine($"Netboot: kerne {kver} sendt til Fleetdeck");
                    }
                    else log.AppendLine("Netboot: pakken kunne ikke laves: " + t.Output.Trim());
                    try { File.Delete(tar); } catch (IOException) { }
                }
            }
            catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException) { log.AppendLine("Netboot: " + e.Message); }
        }

        try { await api.Post("/api/agent/sparecard", rep, ct); }
        catch (HttpRequestException e) { log.AppendLine("Status kunne ikke sendes til Fleetdeck: " + e.Message); }
        return new JobResult { Status = rep.Status == "fejl" ? JobStatus.Failed : JobStatus.Success, ExitCode = rep.Status == "fejl" ? 1 : 0, Output = log.ToString() };
    }

    private static JobResult Fail(string text) => new() { Status = JobStatus.Failed, ExitCode = 1, Output = text };
}

/// <summary>Den rigtige maskine: kommandoer gennem Shell.Run (hvert argument citeret for sig, så jobbet kan stoppes fra Fleetdeck).</summary>
internal sealed class RealSpareHost : ISpareHost
{
    private static string Q(string s) => "'" + s.Replace("'", "'\\''") + "'";

    public SpareCmd Run(string file, IReadOnlyList<string> args, string? stdin = null, int timeoutMin = 30)
    {
        string? tmp = null;
        try
        {
            var cmd = string.Join(' ', new[] { file }.Concat(args).Select(Q));
            if (stdin is not null)
            {
                UnixRoot.EnsureRootOnly(SpareCard.Work);
                tmp = Path.Combine(SpareCard.Work, $"stdin-{Guid.NewGuid():N}.txt");
                File.WriteAllText(tmp, stdin);
                cmd += " < " + Q(tmp);
            }
            var r = Shell.Run(cmd, TimeSpan.FromMinutes(timeoutMin), null, 2_000_000);
            return new SpareCmd(r.TimedOut ? 124 : r.ExitCode, r.Output);
        }
        finally { if (tmp is not null) try { File.Delete(tmp); } catch (IOException) { } }
    }

    public string? ReadText(string path) => File.Exists(path) ? File.ReadAllText(path) : null;
    public void WriteText(string path, string text) => File.WriteAllText(path, text);
    public bool Exists(string path) => Path.Exists(path);
    public void MakeDir(string path) => Directory.CreateDirectory(path);
    public string? RealPath(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists && fi.LinkTarget is null) return null;
            return fi.ResolveLinkTarget(true)?.FullName ?? fi.FullName;
        }
        catch (IOException) { return null; }
    }
    public string[] ListDir(string path) => Directory.Exists(path) ? Directory.GetFileSystemEntries(path).Select(Path.GetFileName).OfType<string>().ToArray() : [];

    public List<string> ListTree(string root)
    {
        UnixRoot.EnsureRootOnly(SpareCard.Work);
        var file = Path.Combine(SpareCard.Work, $"list-{Guid.NewGuid():N}.txt");
        try
        {
            Shell.Run($"nice -n 19 ionice -c3 find {Q(root)} -xdev -printf '%P\\n' > {Q(file)} 2>/dev/null", TimeSpan.FromMinutes(60));
            return File.Exists(file) ? File.ReadLines(file).ToList() : [];
        }
        finally { try { File.Delete(file); } catch (IOException) { } }
    }
}
