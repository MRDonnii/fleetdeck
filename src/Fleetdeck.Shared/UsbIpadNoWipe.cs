using System.Text;

namespace Fleetdeck.Shared;

public static partial class UsbIpadJobs
{
    /// <summary>
    /// "Overvåg uden datatab" på en pc (pymobiledevice3): [ny backup, hvis iPad'en ikke er tom] → vælg backup efter reglerne → hent og tjek den →
    /// slet (profile erase-device) → par → overvåg (profile supervise med husstandens identitet) → gendan uden systemfiler
    /// (backup2 restore --no-system …) → tjek isSupervised og parring med husstandens certifikat (lockdown pair-supervised) → låst profil.
    /// </summary>
    private static async Task<JobResult> SuperviseNoWipe(AgentJob j, UsbIpadIo io, IosDeviceInfo dev, CancellationToken ct)
    {
        var p = j.Params;
        if (dev.Udid is not { Length: > 0 } udid) return Fail("iPad'ens UDID mangler.", dev);
        var log = new StringBuilder();
        var work = WorkDir($"nowipe-{j.Id}");
        var kb = Path.Combine(work, "keybag.pem");
        try
        {
            var pw = await Secret(io, j, UsbIpadSecretKinds.BackupPassword, ct);
            var key = await Secret(io, j, UsbIpadSecretKinds.Keybag, ct);
            if (pw is null) return Fail("Husstandens backup-kode kunne ikke hentes fra Fleetdeck. Intet er slettet.", dev);
            if (key is null) return Fail("Overvågningsnøglen kunne ikke hentes fra Fleetdeck. Intet er slettet.", dev);
            await WritePrivate(kb, key);
            var org = p.Org is { Length: > 0 } o ? o : "Familien";
            var current = dev;
            string? step = null;

            var ops = new NoWipeOps
            {
                Line = s => log.AppendLine(Pmd3.Mask(s, [pw])),
                Phase = (f, t, s) => { step = s; LiveJob.Phase(f, t, s); },
                Read = async c =>
                {
                    var (all, err) = await Pmd3Runner.List(c);
                    if (all?.FirstOrDefault(d => d.Udid == udid) is not { } basic)
                    {
                        log.AppendLine(err ?? "iPad'en er ikke sat i pc'en lige nu.");
                        return null;
                    }
                    return current = await Pmd3Runner.Details(basic, c);
                },
                Probe = (d, c) => NoWipeCommands.Probe(udid, d.ActivationState, c),
                FreshBackup = async (d, f, t, c) =>
                {
                    var state = await NoWipeCommands.Probe(udid, d.ActivationState, c);
                    return await TakeBackup(j, io, d, pw, state, log, f, t, c);
                },
                Chosen = async (cand, c) =>
                {
                    if (io.Post is null) return;
                    try { await io.Post($"/api/agent/nowipe/{j.Id}/chosen", new { store = cand.Store, set = cand.Set }, c); }
                    catch (HttpRequestException e) { log.AppendLine($"Bemærk: Fleetdeck fik ikke besked om det valgte sæt ({e.Message})."); }
                },
                Fetch = (cand, dest, f, t, c) => NoWipeFlow.FetchParts(cand, j.Id, dest, io.Download, LiveJob.Phase, s => log.AppendLine(s), f, t, c),
                Erase = async (f, t, c) =>
                {
                    LiveJob.Phase(f, t, step ?? "Sletter iPad'en");
                    var r = await Pmd3Runner.Run(udid, Pmd3.Erase(), TimeSpan.FromMinutes(5), c);
                    log.AppendLine(r.Ok ? "iPad'en er slettet." : "iPad'en blev ikke slettet: " + Explain(r.Message));
                    return r.Ok;
                },
                WaitBack = (f, t, text, c) => WaitBack(udid, f, t, text, c),
                Supervise = async (skipSetup, f, t, c) =>
                {
                    // efter sletningen står iPad'en i opsætningen: par, og "profile supervise" aktiverer den selv, hvis det mangler
                    if (!await Paired(udid, kb, log, c)) return false;
                    log.AppendLine($"Kører: pymobiledevice3 {string.Join(' ', Pmd3.Supervise(org, "<keybag.pem>"))}");
                    var r = await Pmd3Runner.Run(udid, Pmd3.Supervise(org, kb), TimeSpan.FromMinutes(5), c);
                    if (!r.Ok) { log.AppendLine("Overvågningen mislykkedes: " + Explain(r.Message)); return false; }
                    log.AppendLine($"iPad'en er gjort overvåget af \"{org}\" (profile supervise{(skipSetup ? "; opsætningen springes over" : "")}).");
                    return true;
                },
                EnsurePaired = c => Paired(udid, kb, log, c),
                Restore = async (dir, source, system, f, t, c) =>
                {
                    var cmd = NoWipeCommands.Restore(dir, source, system);
                    log.AppendLine($"Kører: pymobiledevice3 {string.Join(' ', cmd)}");
                    var r = await Pmd3Runner.Run(udid, cmd, Minutes(p, 180), c, [pw], pct => LiveJob.Phase(f + (t - f) * pct / 100, t, $"{step} ({pct} %)"));
                    if (!r.Ok) log.AppendLine("Gendannelsen mislykkedes: " + Explain(r.Message));
                    return r.Ok;
                },
                PairSupervised = async c =>
                {
                    var r = await Pmd3Runner.Run(udid, Pmd3.PairSupervised(kb), TimeSpan.FromMinutes(2), c);
                    if (!r.Ok) log.AppendLine($"Parring med husstandens overvågningscertifikat lykkedes ikke: {Explain(r.Message)}");
                    return r.Ok;
                },
                InstallProfile = async (d, f, t, c) =>
                {
                    var r = await InstallProfile(j, io, d, c, key);
                    log.AppendLine(r.Output);
                    return r.Status == JobStatus.Success;
                },
            };
            var req = new NoWipeRequest(NoWipeMethods.Norm(p.Method), p.FreshBackup == true, p.Candidates ?? [], p.BackupStore, p.BackupSet, work, pw, org);
            var res = await NoWipeFlow.Run(req, ops, ct);
            var after = res.Device ?? current;
            return res.Ok ? Ok(log.ToString(), after) : Fail(log.ToString(), after);
        }
        finally { Wipe(work); }
    }

    /// <summary>Par med iPad'en: almindeligt (opsætningen og en overvåget iPad tillader det uden "Stol på"), ellers som den overvågende vært.</summary>
    private static async Task<bool> Paired(string udid, string keybag, StringBuilder log, CancellationToken ct)
    {
        var info = await Pmd3Runner.Run(udid, Pmd3.Info(), TimeSpan.FromSeconds(60), ct);
        if (info.Ok) return true;
        var pr = await Pmd3Runner.Run(udid, Pmd3.Pair(), TimeSpan.FromMinutes(2), ct);
        if (pr.Ok) return true;
        var ps = await Pmd3Runner.Run(udid, Pmd3.PairSupervised(keybag), TimeSpan.FromMinutes(2), ct);
        if (ps.Ok) return true;
        log.AppendLine($"Pc'en kunne ikke parre med iPad'en ({Explain(pr.Message)}). Lås iPad'en op og tryk \"Stol på\", hvis den spørger.");
        return false;
    }
}
