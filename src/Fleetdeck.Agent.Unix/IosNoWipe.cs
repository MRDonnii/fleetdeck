using Fleetdeck.Shared;

namespace Fleetdeck.Agent.Unix;

/// <summary>"Overvåg uden datatab" via Mac'en: cfgutil sletter og overvåger, pymobiledevice3 (udvidelsen "iPad via USB") lægger backuppen tilbage.</summary>
public static partial class Ios
{
    /// <summary>
    /// Forløbet står i <see cref="NoWipeFlow"/>. Her: cfgutil erase → cfgutil prepare --supervised (uden --skip-all, så iPad'en står i opsætningen) →
    /// pymobiledevice3 backup2 restore --no-system (macOS' usbmuxd og Mac'ens parring) → cfgutil get isSupervised → parring med husstandens
    /// identitet (cfgutil -C/-K pair) → låst profil.
    /// </summary>
    private static async Task<JobResult> SuperviseNoWipe(Api api, Ctx c, CancellationToken ct)
    {
        if (c.Server is not { } ident) return Fail("Overvågningsidentiteten kom ikke fra Fleetdeck – start handlingen igen.");
        if (c.Password is not { } pw) return Fail("Husstandens backup-kode kom ikke fra Fleetdeck – start handlingen igen.");
        var org = c.Secrets?.Organization is { Length: > 0 } o ? o : "Familien";
        Show(0, 1, "Gør pymobiledevice3 klar (udvidelsen \"iPad via USB\")");
        if (!EnsurePmd3(c)) return Fail(c.Log + "Intet er slettet.");
        using var hold = Pmd3Runner.Hold();   // USB-vagten holder pause imens
        var helpPrepare = HelpFor(c.Tool, "prepare");
        var work = Path.Combine(UsbIpadPaths.Work, $"nowipe-{c.Job.Id}");
        string? udid = null;
        IosDeviceInfo? last = null;

        var ops = new NoWipeOps
        {
            Line = c.Line,
            Phase = (f, t, s) => { _step = null; Show(f, t, s); _step = s; },
            Read = _ =>
            {
                var (dev, err) = Find(c.Tool, c.Ecid);
                if (dev is null) { c.Line(err!); return Task.FromResult<IosDeviceInfo?>(null); }
                if (dev.Udid is { Length: > 0 } u) udid = u;
                else if (udid is null) { c.Line("iPad'ens UDID kunne ikke læses – lås den op, tryk \"Stol på\" og prøv igen."); return Task.FromResult<IosDeviceInfo?>(null); }
                return Task.FromResult<IosDeviceInfo?>(last = dev);
            },
            Probe = (d, t) => NoWipeCommands.Probe(udid!, d.ActivationState, t),
            FreshBackup = async (d, f, t, token) =>
            {
                var b = EncryptedBackup(c, d, f, f + (t - f) * 2 / 3, c.Left(70), require: true);
                if (b is null) return null;
                if (!b.Encrypted || b.Verified == false) { c.Line("Den nye backup kan ikke låses op med husstandens kode, så den bruges ikke."); return null; }
                var up = await Upload(api, c, b, d, f + (t - f) * 2 / 3, t, token);
                if (up is null) return null;
                return new NoWipeCandidate
                {
                    Store = NoWipeCandidate.Mac, Set = up.Set, Created = DateTime.UtcNow, Bytes = up.Bytes, Parts = up.Parts, Encrypted = up.Encrypted,
                    Verified = up.PasswordVerified, SourceEmpty = up.DeviceEmpty, SourceUdid = up.Udid, DeviceName = up.DeviceName,
                };
            },
            Chosen = async (cand, token) =>
            {
                try { await api.Post($"/api/agent/nowipe/{c.Job.Id}/chosen", new { store = cand.Store, set = cand.Set }, token); }
                catch (HttpRequestException e) { c.Line($"Bemærk: Fleetdeck fik ikke besked om det valgte sæt ({e.Message})."); }
            },
            Fetch = (cand, dest, f, t, token) => NoWipeFlow.FetchParts(cand, c.Job.Id, dest, async (path, file, x) => await api.Download(path, file, x),
                LiveJob.Phase, c.Line, f, t, token),
            Erase = (f, t, _) => Task.FromResult(Erase(c, ident, f, t)),
            WaitBack = (f, t, text, _) => Task.FromResult(WaitReady(c, f, t, text)),
            Supervise = (skipSetup, f, t, _) =>
            {
                Show(f, t, _step ?? "Gør iPad'en overvåget");
                return Task.FromResult(Prepare(c, ident, org, helpPrepare, skipSetup));
            },
            EnsurePaired = async token =>
            {
                if (udid is null) return false;
                if ((await Pmd3Runner.Run(udid, Pmd3.Info(), TimeSpan.FromSeconds(60), token)).Ok) return true;
                Pair(c, ident);
                if ((await Pmd3Runner.Run(udid, Pmd3.Info(), TimeSpan.FromSeconds(60), token)).Ok) return true;
                c.Line("pymobiledevice3 kan ikke tale med iPad'en via Mac'ens parring. Lås iPad'en op og tryk \"Stol på\", hvis den spørger.");
                return false;
            },
            Restore = async (dir, source, system, f, t, token) =>
            {
                var cmd = NoWipeCommands.Restore(dir, source, system);
                c.Line($"Kører: pymobiledevice3 {string.Join(' ', cmd)}");
                var step = _step;
                var r = await Pmd3Runner.Run(udid, cmd, c.Left(150), token, [pw], pct => LiveJob.Phase(f + (t - f) * pct / 100, t, $"{step} ({pct} %)"));
                if (!r.Ok) c.Line("Gendannelsen mislykkedes: " + Explain(r.Message));
                return r.Ok;
            },
            PairSupervised = _ =>
            {
                // en overvåget iPad lader den overvågende vært parre uden "Stol på"
                var r = Cfg(c.Tool, c.Ecid, ident, TimeSpan.FromSeconds(90), null, "pair");
                if (!r.Ok) c.Line($"Parring med husstandens overvågningscertifikat lykkedes ikke: {Explain(r.Message)}");
                return Task.FromResult<bool?>(r.Ok);
            },
            InstallProfile = async (d, f, t, token) => (await InstallProfile(api, c, d, f, t, token)).Ok,
        };
        try
        {
            var p = c.P;
            var req = new NoWipeRequest(NoWipeMethods.Norm(p.Method), p.FreshBackup == true, p.Candidates ?? [], p.BackupStore, p.BackupSet, work, pw, org);
            var res = await NoWipeFlow.Run(req, ops, ct);
            var report = res.Device ?? last;
            return res.Ok ? Ok(c.Log.ToString(), report is null ? null : new IosReport { Devices = [report] }) : Fail(c.Log.ToString(), report);
        }
        finally { try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
    }

    /// <summary>pymobiledevice3 fra udvidelsen "iPad via USB" – installeres her, hvis den mangler (macOS' egen usbmuxd bruges).</summary>
    private static bool EnsurePmd3(Ctx c)
    {
        if (Pmd3Runner.Installed) return true;
        var script = Extensions.BuiltIn.FirstOrDefault(x => x.Id == Extensions.UsbIpadId)?.For(Platforms.Mac)?.Script;
        if (script is null) { c.Line("Udvidelsen \"iPad via USB\" findes ikke til Mac."); return false; }
        c.Line("Installerer udvidelsen \"iPad via USB\" (pymobiledevice3) på Mac'en …");
        var r = Exec("/bin/bash", ["-c", script], TimeSpan.FromMinutes(20), null);
        if (r.Exit == 0 && Pmd3Runner.Installed) { c.Line("pymobiledevice3 er installeret."); return true; }
        c.Line($"pymobiledevice3 kunne ikke installeres: {r.Message}. Installér udvidelsen \"iPad via USB\" på Mac'ens side i Fleetdeck og prøv igen.");
        return false;
    }

    /// <summary>Tjek før en backup: er iPad'en tom eller i opsætningen, tages der ingen (refuse = hvorfor). Uden pymobiledevice3 bruges kun aktiveringen.</summary>
    private static NoWipeDeviceState? BackupGuard(Ctx c, IosDeviceInfo dev, out string? refuse)
    {
        refuse = null;
        if (dev.ActivationState is "Unactivated")
        {
            refuse = "iPad'en er ikke aktiveret (den er slettet og står i opsætningen), så der tages ikke en backup – den ville være tom.";
            return null;
        }
        if (!Pmd3Runner.Installed || dev.Udid is not { Length: > 0 } udid) return null;
        var st = NoWipeCommands.Probe(udid, dev.ActivationState, CancellationToken.None).GetAwaiter().GetResult();
        c.Line(NoWipeCommands.Describe(st));
        refuse = NoWipeSafety.RefuseBackup(st);
        return st;
    }

    /// <summary>Kopi af Mac'ens backup af iPad'en (MobileSync/Backup/&lt;UDID&gt; → &lt;UDID&gt;.fleetdeck-&lt;tid&gt;), før cfgutil tager en ny. APFS-klon (cp -c), så den ikke fylder.</summary>
    /// <returns>false = kopien kunne ikke laves (så tages der ingen ny backup).</returns>
    private static bool Snapshot(Ctx c, IosDeviceInfo dev, string root)
    {
        if (dev.Udid is not { Length: > 0 } udid) return true;
        var src = Path.Combine(root, udid);
        try { if (!Directory.Exists(src)) return true; }
        catch (UnauthorizedAccessException) { c.Line(NoDiskAccess); return false; }
        var dst = Path.Combine(root, MobileSyncSnapshot.Name(udid, DateTime.Now));
        var r = Exec("/bin/cp", ["-cpR", src, dst], TimeSpan.FromMinutes(30), null);
        if (r.Exit != 0) r = Exec("/bin/cp", ["-pR", src, dst], TimeSpan.FromMinutes(60), null);
        c.Line(r.Exit == 0 ? $"Mac'ens tidligere backup af iPad'en er gemt som {Path.GetFileName(dst)} (den røres ikke)."
            : $"Mac'ens tidligere backup kunne ikke kopieres ({r.Message}), så der tages ikke en ny backup oven i den.");
        return r.Exit == 0;
    }
}
