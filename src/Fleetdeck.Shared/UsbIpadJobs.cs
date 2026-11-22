using System.Formats.Tar;
using System.IO.Compression;
using System.Text;

namespace Fleetdeck.Shared;

/// <summary>Det, agenten skal bruge fra serveren til usb_ipad-jobs (hver platform har sin egen HTTP-klient).</summary>
/// <param name="Secret">Byt jobbets engangsbillet til én hemmelighed.</param>
/// <param name="PutFile">Send en fil som rå data – streamet, ikke læst ind i hukommelsen (backup-dele er op til 1,5 GB).</param>
/// <param name="Download">Hent en fil.</param>
/// <param name="Post">Send JSON til serveren (fx hvilket backup-sæt "Overvåg uden datatab" bruger).</param>
public sealed record UsbIpadIo(
    Func<UsbIpadSecretRequest, CancellationToken, Task<UsbIpadSecretResponse>> Secret,
    Func<string, string, CancellationToken, Task> PutFile,
    Func<string, string, CancellationToken, Task> Download,
    Func<string, object, CancellationToken, Task>? Post = null);

/// <summary>
/// usb_ipad-jobs på agenten (Windows, Linux og Mac): oplysninger, låst profil, krypteret backup til Fleetdeck, gendannelse og – afprøves –
/// overvågning og sletning. Én iPad ad gangen (UDID), med tidsgrænse. Hemmeligheder hentes med jobbets engangsbillet, skrives kun til
/// pymobiledevice3's stdin eller til en midlertidig fil, som kun systemet kan læse, og slettes bagefter.
/// </summary>
public static partial class UsbIpadJobs
{
    /// <summary>Backuppen sendes i dele på højst 1,5 GB (store dele i én upload har givet "out of memory" på macOS med curl).</summary>
    public const long PartBytes = 1536L * 1024 * 1024;

    public static async Task<JobResult> Run(AgentJob j, UsbIpadIo io, CancellationToken ct)
    {
        if (!Pmd3Runner.Installed) return Fail("Udvidelsen \"iPad via USB\" er ikke installeret på denne pc. Installér den på maskinens side i Fleetdeck.");
        var p = j.Params;
        if (p.Udid is not { Length: > 0 } udid) return Fail("Jobbet mangler iPad'ens UDID.");
        using var hold = Pmd3Runner.Hold();   // vagten holder pause imens
        LiveJob.Phase(0, 5, "Finder iPad'en");
        var (found, err) = await Pmd3Runner.List(ct);
        if (found is null) return Fail(err!);
        if (found.FirstOrDefault(d => d.Udid == udid) is not { } basic)
            return Fail("iPad'en er ikke sat i denne pc lige nu. Sæt kablet i, lås iPad'en op og prøv igen.");
        LiveJob.Phase(5, 10, "Læser iPad'en");
        var dev = await Pmd3Runner.Details(basic, ct);
        if (dev.Paired == false && p.Action is not (UsbIpadActions.Erase or UsbIpadActions.Supervise or UsbIpadActions.RemoveProfile or UsbIpadActions.SuperviseNoWipe))
            return Fail("iPad'en har ikke godkendt pc'en endnu. Lås iPad'en op, tryk \"Stol på\" og skriv koden – og prøv igen.", dev);
        return p.Action switch
        {
            UsbIpadActions.Info => Ok(Describe(dev), dev),
            UsbIpadActions.InstallProfile => await InstallProfile(j, io, dev, ct),
            UsbIpadActions.Backup => await Backup(j, io, dev, ct),
            UsbIpadActions.Restore => await Restore(j, io, dev, ct),
            UsbIpadActions.Supervise => await Supervise(j, io, dev, ct),
            UsbIpadActions.Erase => await Erase(dev, ct),
            UsbIpadActions.RemoveProfile => await RemoveProfiles(j, io, dev, ct),
            UsbIpadActions.Release => await Release(j, io, dev, ct),
            UsbIpadActions.SuperviseNoWipe => await SuperviseNoWipe(j, io, dev, ct),
            _ => Fail($"Ukendt iPad-handling: {p.Action}"),
        };
    }

    private static TimeSpan Minutes(JobParams p, int fallback) => TimeSpan.FromMinutes(Math.Clamp(p.TimeoutMin ?? fallback, 5, 360));

    // ------------------------------------------------------------------ låst profil
    /// <param name="keybag">Overvågningsnøglen, hvis jobbet allerede har hentet den (billetten kan kun bruges én gang pr. slags).</param>
    private static async Task<JobResult> InstallProfile(AgentJob j, UsbIpadIo io, IosDeviceInfo dev, CancellationToken ct, string? keybag = null)
    {
        var p = j.Params; var log = new StringBuilder();
        var dir = WorkDir($"profile-{j.Id}");
        var file = Path.Combine(dir, "locked.mobileconfig"); var kb = Path.Combine(dir, "keybag.pem");
        try
        {
            LiveJob.Phase(10, 25, "Henter den låste profil fra Fleetdeck");
            try { await io.Download($"/api/agent/usb-ipad/profile/{j.Id}", file, ct); }
            catch (HttpRequestException e) { return Fail($"Kunne ikke hente profilen fra Fleetdeck: {e.Message}", dev); }
            Private(file);

            // den gamle DNS-profil fra QR-koden: to DNS-profiler på én gang giver bøvl
            foreach (var old in (p.ReplaceIds ?? []).Where(id => dev.Profiles?.Any(x => x.Identifier == id) == true))
            {
                LiveJob.Phase(25, 35, "Fjerner den gamle DNS-profil");
                var rr = await Pmd3Runner.Run(dev.Udid, Pmd3.RemoveProfile(old), TimeSpan.FromMinutes(2), ct);
                log.AppendLine(rr.Ok ? "Den gamle DNS-profil er fjernet."
                    : $"Den gamle DNS-profil kunne ikke fjernes ({rr.Message}) – fjern den på iPad'en: Indstillinger → Generelt → VPN og enhedsadministration.");
            }

            LiveJob.Phase(35, 85, "Installerer den låste profil");
            var silent = false;
            if (dev.Supervised == true)
            {
                var key = keybag ?? await Secret(io, j, UsbIpadSecretKinds.Keybag, ct);
                if (key is null) log.AppendLine("Overvågningsnøglen kunne ikke hentes – profilen skal godkendes på iPad'en.");
                else
                {
                    await WritePrivate(kb, key);
                    var r = await Pmd3Runner.Run(dev.Udid, Pmd3.InstallProfile(file, kb), TimeSpan.FromMinutes(5), ct);
                    if (r.Ok) silent = true;
                    else log.AppendLine($"Lydløs installation lykkedes ikke ({Explain(r.Message)}). iPad'en er måske overvåget af en anden (fx Apple Configurator på en Mac) " +
                                        "– profilen sendes i stedet til godkendelse på iPad'en.");
                }
            }
            if (!silent)
            {
                var r = await Pmd3Runner.Run(dev.Udid, Pmd3.InstallProfile(file, null), TimeSpan.FromMinutes(5), ct);
                if (!r.Ok) return Fail(log + "Profilen blev ikke installeret: " + Explain(r.Message), dev);
                log.AppendLine("Profilen er sendt til iPad'en. Godkend den på iPad'en: Indstillinger → \"Profil hentet\" → Installér, og skriv iPad'ens kode.");
                if (dev.Supervised != true)
                    log.AppendLine("Bemærk: iPad'en er ikke overvåget. De fleste begrænsninger og låsen virker først på en overvåget iPad.");
            }

            LiveJob.Phase(85, 100, "Tjekker iPad'en");
            var after = await Refresh(dev, ct);
            var hit = after.Profiles?.FirstOrDefault(x => x.Identifier == p.ProfileIdentifier);
            log.AppendLine(hit is not null ? $"Den låste profil er installeret på {after.Name ?? "iPad'en"}: {hit.Name ?? hit.Identifier}."
                : silent ? "Profilen er sendt, men står ikke på listen endnu – tjek iPad'en (Indstillinger → Generelt → VPN og enhedsadministration)."
                : "Profilen venter på godkendelse på iPad'en.");
            return Ok(log.ToString(), after);
        }
        finally { Wipe(dir); }
    }

    // ------------------------------------------------------------------ backup
    private static async Task<JobResult> Backup(AgentJob j, UsbIpadIo io, IosDeviceInfo dev, CancellationToken ct)
    {
        var log = new StringBuilder();
        LiveJob.Phase(10, 12, "Tjekker, at iPad'en ikke er tom");
        var state = await NoWipeCommands.Probe(dev.Udid!, dev.ActivationState, ct);
        if (NoWipeSafety.RefuseBackup(state) is { } why)
            return Fail(why + " Den seneste gode backup i Fleetdeck er urørt.", dev);
        var pw = await Secret(io, j, UsbIpadSecretKinds.BackupPassword, ct);
        var c = await TakeBackup(j, io, dev, pw, state, log, 12, 100, ct);
        if (c is null) return Fail(log.ToString(), dev);
        log.AppendLine($"Backup af {dev.Name ?? "iPad'en"} er gemt i Fleetdeck: {c.Describe()} – {c.Parts} del(e).");
        return Ok(log.ToString(), await Refresh(dev, ct));
    }

    /// <summary>
    /// Krypteret backup med husstandens kode → tjek (krypteret, koden passer) → pak → send til Fleetdeck. null = fejl (står i loggen).
    /// Kaldes kun, når <see cref="NoWipeSafety.RefuseBackup"/> har sagt god for iPad'en.
    /// </summary>
    private static async Task<NoWipeCandidate?> TakeBackup(AgentJob j, UsbIpadIo io, IosDeviceInfo dev, string? pw, NoWipeDeviceState state, StringBuilder log,
        int from, int to, CancellationToken ct)
    {
        var dir = WorkDir($"backup-{j.Id}"); var up = WorkDir($"upload-{j.Id}");
        int At(double f) => from + (int)((to - from) * f);
        try
        {
            LiveJob.Phase(from, At(0.05), "Tjekker backup-krypteringen");
            var st = await Pmd3Runner.Run(dev.Udid, Pmd3.EncryptionState(), TimeSpan.FromMinutes(1), ct);
            var on = st.Ok ? Pmd3Parse.EncryptionOn(st.Out) : null;
            if (on == false)
            {
                if (pw is null) { log.AppendLine("Husstandens backup-kode kunne ikke hentes fra Fleetdeck."); return null; }
                LiveJob.Phase(At(0.05), At(0.08), "Slår krypteret backup til – lås iPad'en op, og skriv koden, hvis den spørger");
                var r = await Pmd3Runner.Run(dev.Udid, Pmd3.EncryptionOn(), TimeSpan.FromMinutes(3), ct, [pw]);
                if (!r.Ok) { log.AppendLine("Krypteret backup kunne ikke slås til: " + Explain(r.Message)); return null; }
                log.AppendLine("Krypteret backup er slået til med husstandens backup-kode (se den under Indstillinger → iPads).");
            }
            else if (on == true) log.AppendLine("Krypteret backup var allerede slået til – koden tjekkes mod backuppen bagefter.");
            else log.AppendLine($"Krypteringens tilstand kunne ikke læses ({st.Message}) – backuppen tages og tjekkes bagefter.");

            LiveJob.Phase(At(0.08), At(0.7), "Tager backup – lås iPad'en op; det kan tage lang tid");
            var b = await Pmd3Runner.Run(dev.Udid, Pmd3.Backup(dir), Minutes(j.Params, 180), ct,
                progress: pct => LiveJob.Phase(At(0.08 + 0.62 * pct / 100), At(0.7), $"Tager backup ({pct} %)"));
            if (!b.Ok) { log.AppendLine("Backuppen mislykkedes: " + Explain(b.Message)); return null; }
            var src = Path.Combine(dir, dev.Udid!);
            if (!Directory.Exists(src)) src = Directory.GetDirectories(dir).FirstOrDefault() ?? "";
            if (src.Length == 0) { log.AppendLine("Backuppen blev ikke fundet på disken efter kørslen."); return null; }
            var info = IosBackupManifest.Inspect(src, pw);
            log.AppendLine(info.Encrypted == false ? "Advarsel: backuppen er IKKE krypteret – adgangskoder og sundhedsdata er ikke med."
                : info.Verified == true ? "Backuppen er krypteret, og husstandens backup-kode låser den op."
                : info.Verified == false ? "Backuppen er krypteret med en ANDEN kode end husstandens – den kan ikke gendannes herfra."
                : "Backuppen er krypteret (koden kunne ikke tjekkes).");

            LiveJob.Phase(At(0.7), At(0.8), "Pakker backuppen");
            var name = $"ipad-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
            var parts = await Task.Run(() => Pack(src, up, name), ct);
            Wipe(dir);   // pladsen skal bruges til delene
            long total = 0;
            var empty = NoWipeSafety.LooksEmpty(state);
            for (var i = 0; i < parts.Count; i++)
            {
                LiveJob.Phase(At(0.8 + 0.2 * i / parts.Count), At(0.8 + 0.2 * (i + 1) / parts.Count), $"Sender backuppen til Fleetdeck ({i + 1} af {parts.Count})");
                total += new FileInfo(parts[i]).Length;
                try
                {
                    await io.PutFile($"/api/agent/usb-ipad/backup?job={j.Id}&set={name}&part={i + 1}&parts={parts.Count}" +
                                     $"&udid={Uri.EscapeDataString(dev.Udid!)}{Meta("enc", info.Encrypted)}{Meta("verified", info.Verified)}{Meta("empty", empty)}" +
                                     (state.ComparableBytes is long cb ? $"&used={cb}" : ""), parts[i], ct);
                }
                catch (HttpRequestException e) { log.AppendLine($"Backuppen er taget, men kunne ikke sendes til Fleetdeck (del {i + 1}): {e.Message}"); return null; }
                File.Delete(parts[i]);
            }
            return new NoWipeCandidate
            {
                Store = NoWipeCandidate.Usb, Set = name, Created = DateTime.UtcNow, Bytes = total, Parts = parts.Count, Encrypted = info.Encrypted,
                Verified = info.Verified, SourceEmpty = empty, SourceUdid = dev.Udid, DeviceName = dev.Name,
            };
        }
        finally { Wipe(dir); Wipe(up); }
    }

    private static string Meta(string key, bool? v) => v is bool b ? $"&{key}={(b ? "true" : "false")}" : "";

    // ------------------------------------------------------------------ gendannelse
    private static async Task<JobResult> Restore(AgentJob j, UsbIpadIo io, IosDeviceInfo dev, CancellationToken ct)
    {
        var p = j.Params;
        if (p.Backup is not { Length: > 0 } name || p.Parts is not int n || n < 1 || p.SourceUdid is not { Length: > 0 } source)
            return Fail("Vælg en backup at gendanne.", dev);
        var down = WorkDir($"download-{j.Id}"); var dir = WorkDir($"restore-{j.Id}");
        try
        {
            var files = new List<string>();
            for (var i = 1; i <= n; i++)
            {
                LiveJob.Phase(10 + 30 * (i - 1) / n, 10 + 30 * i / n, $"Henter backuppen fra Fleetdeck ({i} af {n})");
                var f = Path.Combine(down, $"part{i:000}");
                try { await io.Download($"/api/agent/usb-ipad/backup/{Uri.EscapeDataString(name)}/{i}?job={j.Id}", f, ct); }
                catch (HttpRequestException e) { return Fail($"Kunne ikke hente backuppen (del {i}): {e.Message}", dev); }
                files.Add(f);
            }
            LiveJob.Phase(40, 50, "Pakker backuppen ud");
            await Task.Run(() => Unpack(files, dir), ct);
            Wipe(down);
            if (!Directory.Exists(Path.Combine(dir, source))) return Fail("Backuppen indeholder ikke den forventede iPad-mappe.", dev);

            var pw = await Secret(io, j, UsbIpadSecretKinds.BackupPassword, ct);
            if (pw is null) return Fail("Husstandens backup-kode kunne ikke hentes fra Fleetdeck.", dev);
            LiveJob.Phase(50, 98, "Gendanner – iPad'en genstarter bagefter");
            var r = await Pmd3Runner.Run(dev.Udid, Pmd3.Restore(dir, source), Minutes(p, 180), ct, [pw],
                pct => LiveJob.Phase(50 + 48 * pct / 100, 98, $"Gendanner ({pct} %)"));
            if (!r.Ok) return Fail("Gendannelsen mislykkedes: " + Explain(r.Message), dev);
            return Ok($"{name} er gendannet på {dev.Name ?? "iPad'en"}. iPad'en genstarter og henter derefter apps fra App Store (kræver wifi og Apple-kontoen).", dev);
        }
        finally { Wipe(down); Wipe(dir); }
    }

    // ------------------------------------------------------------------ afprøves: overvågning og sletning
    private static async Task<JobResult> Supervise(AgentJob j, UsbIpadIo io, IosDeviceInfo dev, CancellationToken ct)
    {
        if (dev.Supervised == true) return Ok("iPad'en er allerede overvåget – intet at gøre.", dev);
        var dir = WorkDir($"supervise-{j.Id}"); var kb = Path.Combine(dir, "keybag.pem");
        try
        {
            var key = await Secret(io, j, UsbIpadSecretKinds.Keybag, ct);
            if (key is null) return Fail("Overvågningsnøglen kunne ikke hentes fra Fleetdeck.", dev);
            await WritePrivate(kb, key);
            LiveJob.Phase(20, 80, "Gør iPad'en overvåget (afprøves)");
            var org = j.Params.Org is { Length: > 0 } o ? o : "Fleetdeck";
            var r = await Pmd3Runner.Run(dev.Udid, Pmd3.Supervise(org, kb), TimeSpan.FromMinutes(5), ct);
            if (!r.Ok) return Fail("Overvågning mislykkedes: " + Explain(r.Message) +
                                    "\nDet virker kun på en nulstillet iPad, der står i Opsætningsassistent (\"Hej\"-skærmen).", dev);
            LiveJob.Phase(80, 100, "Tjekker iPad'en");
            var after = await Refresh(dev, ct);
            return Ok($"{after.Name ?? "iPad'en"} er nu overvåget af \"{org}\" (afprøves). Gør opsætningen færdig på iPad'en, og installér derefter den låste profil.", after);
        }
        finally { Wipe(dir); }
    }

    private static async Task<JobResult> Erase(IosDeviceInfo dev, CancellationToken ct)
    {
        LiveJob.Phase(20, 90, "Sletter iPad'en (afprøves)");
        var r = await Pmd3Runner.Run(dev.Udid, Pmd3.Erase(), TimeSpan.FromMinutes(5), ct);
        if (!r.Ok) return Fail("Sletningen mislykkedes: " + Explain(r.Message), dev);
        return Ok($"{dev.Name ?? "iPad'en"} slettes nu og starter i Opsætningsassistent. Er Find min iPad slået til, skal Apple-kontoens kode bruges ved opsætningen " +
                  "(aktiveringslåsen kan ikke omgås).", dev);
    }

    // ------------------------------------------------------------------ frigiv iPad'en (barnet er gammelt nok)
    /// <summary>"Løsn styring": fjern den låste profil og den gamle DNS-profil. iPad'en slettes ikke.</summary>
    private static async Task<JobResult> RemoveProfiles(AgentJob j, UsbIpadIo io, IosDeviceInfo dev, CancellationToken ct)
    {
        var log = new StringBuilder();
        var dir = WorkDir($"remove-{j.Id}");
        try
        {
            var ok = await RemoveFleetdeckProfiles(j, io, dev, log, dir, null, 10, 90, ct);
            LiveJob.Phase(90, 100, "Tjekker iPad'en");
            var after = await Refresh(dev, ct);
            return ok ? Ok(log.ToString(), after)
                : Fail(log + "Fjern den på iPad'en i stedet: Indstillinger → Generelt → VPN og enhedsadministration → den låste profil → Fjern profil " +
                       "(fjernelseskoden står i Fleetdeck under Opsætning → Frigiv iPad).", after);
        }
        finally { Wipe(dir); }
    }

    /// <summary>
    /// Fjern Fleetdecks profiler. En profil med fjernelseskode kan kun fjernes af den overvågende vært – lykkes det ikke direkte,
    /// parres der som den (husstandens identitet som keybag), og der prøves igen. true = den låste profil er ikke (længere) på iPad'en.
    /// </summary>
    private static async Task<bool> RemoveFleetdeckProfiles(AgentJob j, UsbIpadIo io, IosDeviceInfo dev, StringBuilder log, string dir, string? keybag,
        int from, int to, CancellationToken ct)
    {
        var p = j.Params;
        var ids = new[] { p.ProfileIdentifier }.Concat(p.ReplaceIds ?? []).OfType<string>().Where(x => x.Length > 0).Distinct().ToList();
        var targets = dev.Profiles is { } installed ? ids.Where(id => installed.Any(x => x.Identifier == id)).ToList() : ids;
        if (targets.Count == 0) { log.AppendLine("Der er ingen Fleetdeck-profil på iPad'en."); return true; }
        var triedPair = false; var lockedOk = true;
        foreach (var pid in targets)
        {
            var isLocked = pid == p.ProfileIdentifier;
            var what = isLocked ? "Den låste profil" : "Den gamle DNS-profil";
            LiveJob.Phase(from, to, isLocked ? "Fjerner den låste profil" : "Fjerner den gamle DNS-profil");
            var r = await Pmd3Runner.Run(dev.Udid, Pmd3.RemoveProfile(pid), TimeSpan.FromMinutes(2), ct);
            if (!r.Ok && dev.Supervised == true && !triedPair)
            {
                triedPair = true;
                keybag ??= await Secret(io, j, UsbIpadSecretKinds.Keybag, ct);
                if (keybag is null) log.AppendLine("Overvågningsnøglen kunne ikke hentes fra Fleetdeck.");
                else
                {
                    var kb = Path.Combine(dir, "keybag.pem");
                    await WritePrivate(kb, keybag);
                    var pr = await Pmd3Runner.Run(dev.Udid, Pmd3.PairSupervised(kb), TimeSpan.FromMinutes(2), ct);
                    if (pr.Ok) r = await Pmd3Runner.Run(dev.Udid, Pmd3.RemoveProfile(pid), TimeSpan.FromMinutes(2), ct);
                    else log.AppendLine($"Parring som den overvågende vært lykkedes ikke ({Explain(pr.Message)}) – iPad'en er måske overvåget af en anden.");
                    try { File.Delete(kb); } catch (IOException) { }
                }
            }
            if (r.Ok) log.AppendLine($"{what} er fjernet.");
            else
            {
                log.AppendLine($"{what} kunne ikke fjernes: {Explain(r.Message)}");
                if (isLocked) lockedOk = false;
            }
        }
        return lockedOk;
    }

    /// <summary>
    /// "Frigiv helt" (afprøves): fjern profilen (så den ikke er med i backuppen) → krypteret backup med husstandens kode → send til Fleetdeck →
    /// slet → gendan backuppen uden at overvåge igen → tjek. Intet slettes, før backuppen er taget, tjekket og sendt.
    /// </summary>
    private static async Task<JobResult> Release(AgentJob j, UsbIpadIo io, IosDeviceInfo dev, CancellationToken ct)
    {
        var log = new StringBuilder();
        var work = WorkDir($"release-{j.Id}"); var dir = WorkDir($"release-backup-{j.Id}"); var up = WorkDir($"release-upload-{j.Id}");
        try
        {
            if (dev.Udid is not { Length: > 0 } udid) return Fail("iPad'ens UDID mangler.", dev);
            var key = await Secret(io, j, UsbIpadSecretKinds.Keybag, ct);
            var pw = await Secret(io, j, UsbIpadSecretKinds.BackupPassword, ct);
            if (pw is null) return Fail("Husstandens backup-kode kunne ikke hentes fra Fleetdeck. Intet er slettet.", dev);
            log.AppendLine("Frigiv iPad: fjern profil → backup → send til Fleetdeck → slet → gendan uden overvågning → tjek.");

            // 1. profilerne væk først – ellers kommer de tilbage med backuppen
            if (!await RemoveFleetdeckProfiles(j, io, dev, log, work, key, 10, 15, ct))
                return Fail(log + "Den låste profil kunne ikke fjernes, så iPad'en slettes ikke (den ville komme tilbage med backuppen). Brug \"Løsn styring\" først. Intet er slettet.", dev);

            // 2. kryptering med husstandens kode (en anden kode ville gøre backuppen ubrugelig herfra)
            LiveJob.Phase(15, 18, "Trin 2/6: Tjekker backup-krypteringen");
            var st = await Pmd3Runner.Run(udid, Pmd3.EncryptionState(), TimeSpan.FromMinutes(1), ct);
            var on = st.Ok ? Pmd3Parse.EncryptionOn(st.Out) : null;
            if (on == false)
            {
                var r = await Pmd3Runner.Run(udid, Pmd3.EncryptionOn(), TimeSpan.FromMinutes(3), ct, [pw]);
                if (!r.Ok) return Fail(log + "Krypteret backup kunne ikke slås til: " + Explain(r.Message) + " Intet er slettet.", dev);
                log.AppendLine("Krypteret backup er slået til med husstandens backup-kode.");
            }
            else
            {
                var r = await Pmd3Runner.Run(udid, Pmd3.ChangePassword(), TimeSpan.FromMinutes(3), ct, [pw, pw]);
                if (!r.Ok) return Fail(log + "iPad'ens backups er krypteret med en anden kode end husstandens, så backuppen kunne ikke gendannes herfra. " +
                                       "Intet er slettet.", dev);
                log.AppendLine("Backups er krypteret med husstandens backup-kode.");
            }

            LiveJob.Phase(18, 45, "Trin 2/6: Tager krypteret backup – lås iPad'en op; det kan tage lang tid");
            var b = await Pmd3Runner.Run(udid, Pmd3.Backup(dir), Minutes(j.Params, 120), ct,
                progress: pct => LiveJob.Phase(18 + 27 * pct / 100, 45, $"Trin 2/6: Tager backup ({pct} %)"));
            if (!b.Ok) return Fail(log + "Backuppen mislykkedes: " + Explain(b.Message) + " Intet er slettet.", dev);
            if (!Directory.Exists(Path.Combine(dir, udid))) return Fail(log + "Backuppen blev ikke fundet på disken. Intet er slettet.", dev);

            // 3. kopi i Fleetdeck
            LiveJob.Phase(45, 50, "Trin 3/6: Pakker backuppen");
            var name = $"ipad-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
            var parts = await Task.Run(() => Pack(Path.Combine(dir, udid), up, name), ct);
            for (var i = 0; i < parts.Count; i++)
            {
                LiveJob.Phase(50 + 10 * i / parts.Count, 50 + 10 * (i + 1) / parts.Count, $"Trin 3/6: Sender backuppen til Fleetdeck ({i + 1} af {parts.Count})");
                try { await io.PutFile($"/api/agent/usb-ipad/backup?job={j.Id}&set={name}&part={i + 1}&parts={parts.Count}&udid={Uri.EscapeDataString(udid)}", parts[i], ct); }
                catch (HttpRequestException e) { return Fail(log + $"Backuppen kom ikke over i Fleetdeck (del {i + 1}: {e.Message}), så iPad'en slettes ikke. Intet er slettet.", dev); }
                File.Delete(parts[i]);
            }
            log.AppendLine($"Backuppen er gemt krypteret i Fleetdeck: {name}.");

            // 4. slet (fjerner overvågningen)
            LiveJob.Phase(60, 64, "Trin 4/6: Sletter iPad'en – rør den ikke, og tag ikke kablet ud");
            var er = await Pmd3Runner.Run(udid, Pmd3.Erase(), TimeSpan.FromMinutes(5), ct);
            if (!er.Ok) return Fail(log + "Sletningen mislykkedes: " + Explain(er.Message) + " Intet er slettet.", dev);
            log.AppendLine("iPad'en er slettet.");
            var fresh = await WaitBack(udid, 64, 72, "Trin 4/6: Venter på, at iPad'en starter igen – lad den stå på \"Hej\"-skærmen", ct);
            if (fresh is null) return Fail(log + "iPad'en kom ikke tilbage i tide. Backuppen ligger i Fleetdeck – gendan den med \"Gendan\", når iPad'en er tilsluttet igen.", dev);
            if (fresh.ActivationState is "Unactivated")
            {
                var act = await Pmd3Runner.Run(udid, Pmd3.Activate(), TimeSpan.FromMinutes(3), ct);
                log.AppendLine(act.Ok ? "iPad'en er aktiveret." : $"Aktiveringen lykkedes ikke ({act.Message}) – gendannelsen prøves alligevel.");
            }
            if (fresh.Paired == false)
            {
                var pr = await Pmd3Runner.Run(udid, Pmd3.Pair(), TimeSpan.FromMinutes(2), ct);
                if (!pr.Ok) log.AppendLine($"Parringen efter sletningen lykkedes ikke ({Explain(pr.Message)}).");
            }

            // 5. gendan – uden at overvåge igen
            LiveJob.Phase(72, 95, "Trin 5/6: Lægger indholdet tilbage – iPad'en genstarter bagefter");
            var rs = await Pmd3Runner.Run(udid, Pmd3.Restore(dir, udid), Minutes(j.Params, 120), ct, [pw],
                pct => LiveJob.Phase(72 + 23 * pct / 100, 95, $"Trin 5/6: Lægger indholdet tilbage ({pct} %)"));
            if (!rs.Ok) return Fail(log + "Gendannelsen mislykkedes: " + Explain(rs.Message) + "\nBackuppen ligger i Fleetdeck – brug \"Gendan\", eller gendan i Finder/Apple Devices " +
                                    "med husstandens backup-kode. Fleetdeck har stadig iPad'en.", dev);

            // 6. tjek
            var after = await WaitBack(udid, 95, 99, "Trin 6/6: Venter på, at iPad'en genstarter efter gendannelsen", ct) ?? dev;
            after = await Refresh(after, ct);
            if (after.Supervised == true)
                return Fail(log + "Indholdet er lagt tilbage, men iPad'en melder stadig, at den er overvåget (overvågningen fulgte med backuppen). " +
                            "Slet den igen og sæt den op som ny, eller gendan fra iCloud. Fleetdeck har stadig iPad'en.", after);
            if (after.Profiles?.Any(x => x.Identifier == j.Params.ProfileIdentifier) == true)
                return Fail(log + "Den låste profil er kommet tilbage med backuppen. Fjern den med \"Løsn styring\". Fleetdeck har stadig iPad'en.", after);
            log.AppendLine($"Færdig: {after.Name ?? "iPad'en"} har sit indhold igen og er ikke længere overvåget. Log ind på Apple-kontoen igen og slå Find min iPad til.");
            return Ok(log.ToString(), after);
        }
        finally { Wipe(work); Wipe(dir); Wipe(up); }
    }

    /// <summary>Vent på, at iPad'en dukker op igen efter en sletning/gendannelse (den forsvinder en stund). null = den kom ikke i tide.</summary>
    private static async Task<IosDeviceInfo?> WaitBack(string udid, int from, int to, string text, CancellationToken ct, int maxMinutes = 25)
    {
        LiveJob.Phase(from, to, text);
        await Task.Delay(TimeSpan.FromSeconds(45), ct);
        var until = DateTime.UtcNow.AddMinutes(maxMinutes);
        while (DateTime.UtcNow < until)
        {
            var (all, _) = await Pmd3Runner.List(ct);
            if (all?.FirstOrDefault(d => d.Udid == udid) is { } basic) return await Pmd3Runner.Details(basic, ct);
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
        }
        return null;
    }

    // ------------------------------------------------------------------ hjælpere
    private static async Task<IosDeviceInfo> Refresh(IosDeviceInfo dev, CancellationToken ct)
    {
        try { return Cfgutil.Merge(dev, await Pmd3Runner.Details(new IosDeviceInfo { Udid = dev.Udid, Ecid = dev.Ecid }, ct)); }
        catch (Exception e) when (e is IOException or InvalidOperationException) { return dev; }
    }

    private static async Task<string?> Secret(UsbIpadIo io, AgentJob j, string kind, CancellationToken ct)
    {
        if (j.Params.Ticket is not { Length: > 0 } t) return null;
        try { return (await io.Secret(new UsbIpadSecretRequest { JobId = j.Id, Ticket = t, Kind = kind }, ct)).Value is { Length: > 0 } v ? v : null; }
        catch (HttpRequestException) { return null; }
    }

    /// <summary>Pak backup-mappen (tar.gz) i dele på højst <see cref="PartBytes"/>. Returnerer delenes filer i rækkefølge.</summary>
    public static List<string> Pack(string source, string outDir, string name, long partBytes = PartBytes)
    {
        Directory.CreateDirectory(outDir);
        using var split = new SplitWriteStream(outDir, name, partBytes);
        using (var gz = new GZipStream(split, CompressionLevel.Fastest, leaveOpen: true))
            TarFile.CreateFromDirectory(source, gz, includeBaseDirectory: true);
        split.Flush();
        return split.Files;
    }

    /// <summary>
    /// Saml delene og pak dem ud i <paramref name="dest"/>. Virker både med pc'ens (.NET) og Mac'ens (bsdtar) pakker: macOS' "._"-filer
    /// (AppleDouble) springes over, og intet må havne uden for <paramref name="dest"/>.
    /// </summary>
    public static void Unpack(IReadOnlyList<string> parts, string dest)
    {
        Directory.CreateDirectory(dest);
        var root = Path.GetFullPath(dest);
        if (!root.EndsWith(Path.DirectorySeparatorChar)) root += Path.DirectorySeparatorChar;
        using var cat = new ConcatReadStream(parts);
        using var gz = new GZipStream(cat, CompressionMode.Decompress);
        using var tar = new TarReader(gz);
        while (tar.GetNextEntry() is { } e)
        {
            var name = e.Name.Replace('\\', '/');
            if (name.Split('/').Any(x => x.StartsWith("._", StringComparison.Ordinal))) continue;
            var path = Path.GetFullPath(Path.Combine(root, name));
            if (!path.StartsWith(root, StringComparison.Ordinal) && path + Path.DirectorySeparatorChar != root)
                throw new InvalidDataException($"Ugyldig sti i backuppen: {name}");
            switch (e.EntryType)
            {
                case TarEntryType.Directory:
                    Directory.CreateDirectory(path);
                    break;
                case TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile:
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    e.ExtractToFile(path, overwrite: true);
                    break;
            }
        }
    }

    private static string WorkDir(string name)
    {
        var d = Path.Combine(UsbIpadPaths.Work, name);
        Wipe(d);
        Directory.CreateDirectory(d);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(d, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return d;
    }

    private static void Private(string file)
    {
        if (!OperatingSystem.IsWindows() && File.Exists(file)) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static async Task WritePrivate(string file, string text)
    {
        await File.WriteAllTextAsync(file, "");
        Private(file);
        await File.WriteAllTextAsync(file, text);
    }

    private static void Wipe(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public static string Describe(IosDeviceInfo d)
    {
        var parts = new List<string> { d.Name ?? "Uden navn" };
        if (d.DeviceType is { Length: > 0 } t) parts.Add(t);
        if (d.Firmware is { Length: > 0 } f) parts.Add($"iPadOS {f}");
        if (d.Serial is { Length: > 0 } s) parts.Add($"serienr. {s}");
        parts.Add(d.Supervised switch { true => "overvåget", false => "ikke overvåget", _ => "overvågning ukendt" });
        if (d.Battery is int b) parts.Add($"batteri {b} %{(d.Charging == true ? " (lader)" : "")}");
        if (d.ActivationState is { Length: > 0 } a) parts.Add($"aktivering: {a}");
        if (d.Profiles is { } pr) parts.Add($"{pr.Count} profil(er)");
        return string.Join(" · ", parts);
    }

    private static string Explain(string msg)
    {
        var m = msg.ToLowerInvariant();
        if (m.Contains("passcode") || m.Contains("locked")) return msg + "\nLås iPad'en op og prøv igen.";
        if (m.Contains("pair") || m.Contains("trust")) return msg + "\nLås iPad'en op, tryk \"Stol på\" og prøv igen.";
        if (m.Contains("find my") || m.Contains("findmy")) return msg + "\nSlå Find min iPad fra på iPad'en (Indstillinger → dit navn → Find) og prøv igen.";
        if (m.Contains("password") || m.Contains("decrypt")) return msg + "\nBackup-koden passer ikke. Er krypteringen sat op uden for Fleetdeck, skal den kode bruges.";
        if (m.Contains("no space") || m.Contains("disk full")) return msg + "\nDer er ikke plads nok på pc'ens disk til backuppen.";
        return msg;
    }

    private static JobResult Ok(string text, IosDeviceInfo dev) => new()
    {
        Status = JobStatus.Success, ExitCode = 0, Output = text.TrimEnd(), Ios = new IosReport { Devices = [dev] },
    };

    private static JobResult Fail(string text, IosDeviceInfo? dev = null) => new()
    {
        Status = JobStatus.Failed, ExitCode = 1, Output = text.TrimEnd(), Ios = dev is null ? null : new IosReport { Devices = [dev] },
    };
}

/// <summary>Skriver videre i en ny fil (name.part001, name.part002 …), når den nuværende er fuld.</summary>
public sealed class SplitWriteStream(string dir, string name, long partBytes) : Stream
{
    private FileStream? _cur;
    private long _left;
    public List<string> Files { get; } = [];

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        while (buffer.Length > 0)
        {
            if (_cur is null || _left == 0)
            {
                _cur?.Dispose();
                var f = Path.Combine(dir, $"{name}.part{Files.Count + 1:000}");
                _cur = new FileStream(f, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
                Files.Add(f); _left = partBytes;
            }
            var n = (int)Math.Min(_left, buffer.Length);
            _cur.Write(buffer[..n]);
            _left -= n; buffer = buffer[n..];
        }
    }
    public override void Flush() => _cur?.Flush();
    protected override void Dispose(bool disposing) { if (disposing) _cur?.Dispose(); _cur = null; base.Dispose(disposing); }
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

/// <summary>Læser flere filer efter hinanden, som var de én.</summary>
public sealed class ConcatReadStream(IReadOnlyList<string> files) : Stream
{
    private int _i = -1;
    private FileStream? _cur;

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        while (true)
        {
            if (_cur is null)
            {
                if (++_i >= files.Count) return 0;
                _cur = new FileStream(files[_i], FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            }
            var n = _cur.Read(buffer);
            if (n > 0) return n;
            _cur.Dispose(); _cur = null;
        }
    }
    protected override void Dispose(bool disposing) { if (disposing) _cur?.Dispose(); _cur = null; base.Dispose(disposing); }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
