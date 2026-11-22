using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Fleetdeck.Shared;

namespace Fleetdeck.Agent.Unix;

/// <summary>
/// iPads tilsluttet Mac'en med kabel (statisk tilstand): Apple Configurators kommandolinjeværktøj cfgutil finder iPads, installerer
/// Fleetdecks låste profil, tager krypterede backups, opdaterer iPadOS, gendanner backups og gør iPad'en overvåget. Kun macOS.
/// Overvågningsidentiteten (certifikat og nøgle i DER-format) kommer fra Fleetdeck med jobbets engangskode og ligger kun i en
/// root-mappe, mens jobbet kører. En ældre, manuelt eksporteret identitet i /usr/local/fleetdeck/ios bruges stadig som reserve.
/// Der køres altid præcis én iPad (-e ECID) ad gangen, med tidsgrænse. Alle cfgutil-kommandoer bygges i <see cref="CfgutilCommands"/>.
/// </summary>
public static partial class Ios
{
    public const string IdentityDir = Mac.Dir + "/ios";
    public static string CertFile => Path.Combine(IdentityDir, "supervision-cert.der");
    public static string KeyFile => Path.Combine(IdentityDir, "supervision-key.der");
    private const string TmpDir = UnixRoot.IosWork;   // root-ejet (aldrig /var/tmp, hvor en bruger kan lave mappen først)
    /// <summary>Hver del af en backup, der sendes til Fleetdeck (under 1,8 GB).</summary>
    private const long PartSize = 1700L * 1024 * 1024;
    private const UnixFileMode RootOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>Symlinket fra "Installér automatiseringsværktøjer", ellers selve programmet (også det gamle navn "Apple Configurator 2").</summary>
    private static readonly string[] Candidates = ["/usr/local/bin/cfgutil", "/Applications/Apple Configurator.app/Contents/MacOS/cfgutil",
        "/Applications/Apple Configurator 2.app/Contents/MacOS/cfgutil"];
    public static string? Tool => Candidates.FirstOrDefault(File.Exists);
    public static bool HasIdentity => File.Exists(CertFile) && File.Exists(KeyFile);

    /// <summary>Et certifikat og en nøgle (DER) til cfgutil -C/-K.</summary>
    private sealed record Ident(string Cert, string Key, string Label);
    private static Ident? Local => HasIdentity ? new Ident(CertFile, KeyFile, "Mac'ens egen identitet") : null;

    private const string NoTool = "cfgutil blev ikke fundet. Installér Apple Configurator fra App Store på Mac'en, åbn den én gang, og vælg menuen " +
                                  "Apple Configurator → \"Installér automatiseringsværktøjer\".";
    private const string NoDiskAccess = "Agenten kan ikke læse backup-mappen. Giv den fuld diskadgang: Systemindstillinger → Anonymitet og sikkerhed → " +
                                        "Fuld diskadgang → + → agentens programfil i /usr/local/fleetdeck (tryk Cmd+Shift+G for at skrive stien).";
    private const string Recover = "Backuppen ligger på Mac'en og i Fleetdeck – brug \"Gendan backup\", når iPad'en er tilsluttet igen.";

    /// <summary>Det, et job har hentet fra Fleetdeck (i hukommelsen og i en root-mappe, der slettes bagefter), og jobbets log.</summary>
    private sealed class Ctx(AgentJob j, string tool, string ecid) : IDisposable
    {
        public AgentJob Job { get; } = j;
        public JobParams P => Job.Params;
        public string Tool { get; } = tool;
        public string Ecid { get; } = ecid;
        public IosSecrets? Secrets { get; set; }
        public Ident? Server { get; set; }
        public string? Dir { get; set; }
        public StringBuilder Log { get; } = new();
        public DateTime Deadline { get; init; } = DateTime.UtcNow.AddHours(4);
        public string? Password => Secrets?.BackupPassword is { Length: > 0 } pw ? pw : null;
        /// <summary>Skjul hemmelighederne i alt, der skrives til jobbets output.</summary>
        public string Clean(string? s) => CfgutilCommands.Redact(s, Password, Secrets?.KeyDer);
        public void Line(string s) => Log.AppendLine(Clean(s));
        /// <summary>Tidsgrænse for et trin: højst <paramref name="maxMinutes"/>, og aldrig ud over jobbets samlede tid.</summary>
        public TimeSpan Left(int maxMinutes) => TimeSpan.FromMinutes(Math.Max(1, Math.Min(maxMinutes, (Deadline - DateTime.UtcNow).TotalMinutes)));
        /// <summary>Identiteterne, der prøves i rækkefølge: Fleetdecks (den, iPads overvåges med herfra), så Mac'ens egen. [null] = ingen.</summary>
        public List<Ident?> Identities => new[] { Server, Local }.Where(x => x is not null).DistinctBy(x => x!.Cert).ToList() is { Count: > 0 } l ? l : [null];
        public void Dispose()
        {
            if (Dir is { } d) try { Directory.Delete(d, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    public static async Task<JobResult> Run(Api api, AgentJob j, CancellationToken ct)
    {
        if (!Mac.Is) return Fail("iPads via kabel kræver en Mac med Apple Configurator.");
        if (Tool is not { } tool) return Fail(NoTool);
        var p = j.Params;
        if (p.Action == IosActions.Help) return Help(tool);
        var ecid = Cfgutil.NormEcid(p.Ecid);
        if (p.Action != IosActions.List && ecid.Length == 0) return Fail("Jobbet mangler iPad'ens ECID – link iPad'en i Fleetdeck først.");
        using var jct = CancellationTokenSource.CreateLinkedTokenSource(ct, Shell.JobCancel);
        using var c = new Ctx(j, tool, ecid) { Deadline = DateTime.UtcNow.AddMinutes(Math.Clamp(p.TimeoutMin ?? 120, 5, 240) - 2) };
        CleanTmp(j.Id);
        try
        {
            if (IosActions.NeedSecrets.Contains(p.Action) && p.SecretNonce is { Length: > 0 })
            {
                Show(0, 2, "Henter nøgler fra Fleetdeck");
                if (await FetchSecrets(api, c, jct.Token) is { } err)
                {
                    if (p.Action is IosActions.Supervise or IosActions.Restore or IosActions.Release or IosActions.SuperviseNoWipe) return Fail(err);
                    c.Line("Advarsel: " + err);
                }
            }
            var r = p.Action switch
            {
                IosActions.List => List(tool),
                IosActions.InstallProfile => await Install(api, c, jct.Token),
                IosActions.Backup => await BackupAction(api, c, jct.Token),
                IosActions.Update => Update(tool, ecid, Minutes(p, 150)),
                IosActions.Supervise => await Supervise(api, c, jct.Token),
                IosActions.Restore => await RestoreAction(api, c, jct.Token),
                IosActions.RemoveProfile => RemoveLocked(c),
                IosActions.Release => await Release(api, c, jct.Token),
                IosActions.SuperviseNoWipe => await SuperviseNoWipe(api, c, jct.Token),
                _ => Fail($"Ukendt iPad-handling: {p.Action}"),
            };
            r.Output = c.Clean(r.Output);
            return r;
        }
        catch (OperationCanceledException) when (jct.IsCancellationRequested)
        {
            return new JobResult { Status = JobStatus.Cancelled, Output = c.Clean(c.Log + "Annulleret fra Fleetdeck.") };
        }
        finally { _step = null; }
    }

    /// <summary>Trinnet i "Gør overvåget" (fx "Trin 3/8: Sletter iPad'en"), som står foran alle under-trin, så websiden kan vise trinnene.</summary>
    private static string? _step;
    private static void Show(int from, int to, string text) => LiveJob.Phase(from, to, _step is { } s ? $"{s} – {text}" : text);

    private static TimeSpan Minutes(JobParams p, int fallback) => TimeSpan.FromMinutes(Math.Clamp(p.TimeoutMin ?? fallback, 5, 240));

    // ------------------------------------------------------------------ hemmeligheder
    /// <summary>Hent identitet og backup-adgangskode med engangskoden. Nøglen skrives kun til en root-mappe (0700), der slettes efter jobbet.</summary>
    private static async Task<string?> FetchSecrets(Api api, Ctx c, CancellationToken ct)
    {
        IosSecrets? s = null;
        for (var attempt = 0; attempt < 2 && s is null; attempt++)
        {
            try { s = JsonSerializer.Deserialize<IosSecrets>(await api.GetText($"/api/agent/ios-secrets/{c.Job.Id}?nonce={Uri.EscapeDataString(c.P.SecretNonce!)}", ct), Api.Json); }
            catch (HttpRequestException e) when (attempt == 0 && e.StatusCode == System.Net.HttpStatusCode.Forbidden) { await Task.Delay(3000, ct); }
            catch (HttpRequestException e) { return $"Kunne ikke hente nøglerne fra Fleetdeck: {e.Message}"; }
            catch (JsonException) { return "Fleetdeck sendte et ugyldigt svar på nøglerne."; }
        }
        if (s is null) return "Fleetdeck afviste engangskoden – start handlingen igen.";
        c.Secrets = s;
        if (s.CertDer is { Length: > 0 } cert && s.KeyDer is { Length: > 0 } key)
        {
            try
            {
                var dir = Path.Combine(TmpDir, $"job-{c.Job.Id}");
                RootDir(TmpDir);
                RootDir(dir);
                c.Dir = dir;
                var cf = Path.Combine(dir, "cert.der"); var kf = Path.Combine(dir, "key.der");
                WriteSecret(cf, Convert.FromBase64String(cert));
                WriteSecret(kf, Convert.FromBase64String(key));
                c.Server = new Ident(cf, kf, "Fleetdecks identitet");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException) { return $"Overvågningsidentiteten kunne ikke gemmes midlertidigt: {e.Message}"; }
        }
        return null;
    }

    private static void RootDir(string dir) => UnixRoot.EnsureRootOnly(dir);

    private static void WriteSecret(string file, byte[] data)
    {
        using var fs = new FileStream(file, new FileStreamOptions
        {
            Mode = FileMode.Create, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });
        fs.Write(data);
    }

    /// <summary>Rester fra jobs, der blev afbrudt (fx genstart): slet dem.</summary>
    private static void CleanTmp(int current)
    {
        try
        {
            if (!Directory.Exists(TmpDir)) return;
            foreach (var d in Directory.GetDirectories(TmpDir, "job-*").Where(d => Path.GetFileName(d) != $"job-{current}"))
                Directory.Delete(d, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // ------------------------------------------------------------------ find, hjælp og profil
    private static JobResult List(string tool)
    {
        Show(0, 20, "Leder efter tilsluttede iPads");
        var (found, err) = Discover(tool);
        if (found is null) return Fail($"cfgutil kunne ikke liste enhederne: {err}");
        var all = new List<IosDeviceInfo>();
        for (var i = 0; i < found.Count; i++)
        {
            Show(20 + 80 * i / found.Count, 20 + 80 * (i + 1) / found.Count, $"Læser {found[i].Name ?? found[i].Ecid}");
            all.Add(Details(tool, found[i]));
        }
        var sb = new StringBuilder();
        if (all.Count == 0) sb.AppendLine("Ingen iPads er tilsluttet Mac'en. Forbind iPad'en med et USB-kabel, lås den op og tryk \"Stol på\".");
        else
        {
            sb.AppendLine($"Fandt {all.Count} enhed(er):");
            foreach (var d in all) sb.AppendLine("• " + Describe(d));
        }
        return Ok(sb.ToString(), new IosReport { Full = true, Devices = all });
    }

    /// <summary>cfgutils egen hjælp til de kommandoer, Fleetdeck bruger (til fejlfinding og til at tilpasse kommandoerne).</summary>
    private static JobResult Help(string tool)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"cfgutil: {tool}");
        var v = Exec(tool, ["version"], TimeSpan.FromSeconds(30), null);
        sb.AppendLine($"Version: {(v.Out + v.Err).Trim()}");
        foreach (var cmd in new[] { "prepare", "restore-backup", "backup", "set-backup-password", "erase", "install-profile" })
        {
            var r = Exec(tool, CfgutilCommands.Help(cmd), TimeSpan.FromSeconds(30), null);
            sb.AppendLine().AppendLine($"===== cfgutil help {cmd} =====").AppendLine((r.Out + r.Err).Trim());
        }
        return Ok(sb.ToString());
    }

    private static async Task<JobResult> Install(Api api, Ctx c, CancellationToken ct)
    {
        Show(0, 15, "Finder iPad'en");
        var (dev, err) = Find(c.Tool, c.Ecid);
        if (dev is null) return Fail(c.Log + err!);
        if (dev.Paired == false && !Pair(c, c.Identities[0])) return Fail(c.Log.ToString(), dev);
        if (dev.Supervised == false)
            c.Line("Advarsel: iPad'en er ikke overvåget. Profilen installeres, men de fleste begrænsninger og låsen virker først på en overvåget iPad.");
        var (ok, after) = await InstallProfile(api, c, dev, 15, 100, ct);
        return ok ? Ok(c.Log.ToString(), new IosReport { Devices = [after] }) : Fail(c.Log.ToString(), after);
    }

    /// <summary>Hent den låste profil fra Fleetdeck og installér den (gammel version og den gamle DNS-profil fjernes først).</summary>
    private static async Task<(bool Ok, IosDeviceInfo Dev)> InstallProfile(Api api, Ctx c, IosDeviceInfo dev, int from, int to, CancellationToken ct)
    {
        var p = c.P;
        var span = to - from;
        Show(from, from + span / 5, "Henter den låste profil fra Fleetdeck");
        RootDir(TmpDir);
        var file = Path.Combine(TmpDir, $"locked-{c.Job.Id}.mobileconfig");
        try
        {
            try { await api.Download($"/api/agent/ios-profile/{c.Job.Id}", file, ct); }
            catch (HttpRequestException e) { c.Line($"Kunne ikke hente profilen fra Fleetdeck: {e.Message}"); return (false, dev); }
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);   // indeholder fjernelseskoden

            Res? r = null;
            foreach (var id in c.Identities)
            {
                // den gamle DNS-profil fra QR-koden m.fl.: to DNS-profiler på én gang giver bøvl
                var installed = dev.Profiles ?? [];
                foreach (var old in (p.ReplaceIds ?? []).Where(x => installed.Any(y => y.Identifier == x)))
                {
                    Show(from + span / 5, from + span * 2 / 5, "Fjerner den gamle DNS-profil");
                    var rr = Cfg(c.Tool, c.Ecid, id, TimeSpan.FromMinutes(2), null, CfgutilCommands.RemoveProfile(old));
                    c.Line(rr.Ok ? "Den gamle DNS-profil er fjernet."
                        : $"Den gamle DNS-profil kunne ikke fjernes ({rr.Message}) – fjern den på iPad'en: Indstillinger → Generelt → VPN og enhedsadministration.");
                }
                Show(from + span * 2 / 5, from + span * 4 / 5, "Installerer den låste profil");
                r = Cfg(c.Tool, c.Ecid, id, TimeSpan.FromMinutes(5), null, CfgutilCommands.InstallProfile(file));
                if (!r.Ok && r.Code == Cfgutil.ProfileAlreadyInstalled && p.ProfileIdentifier is { Length: > 0 } pid)
                {
                    // samme id er installeret: fjern den gamle version (kun den overvågende Mac kan) og installér igen
                    c.Line("En ældre version af profilen er installeret – den fjernes først.");
                    var rm = Cfg(c.Tool, c.Ecid, id, TimeSpan.FromMinutes(2), null, CfgutilCommands.RemoveProfile(pid));
                    if (rm.Ok) r = Cfg(c.Tool, c.Ecid, id, TimeSpan.FromMinutes(5), null, CfgutilCommands.InstallProfile(file));
                    else c.Line($"Den gamle profil kunne ikke fjernes{(id is null ? "" : $" med {id.Label}")}: {rm.Message}");
                }
                if (r.Ok) break;
                if (id is not null && c.Identities.Count > 1) c.Line($"Det lykkedes ikke med {id.Label}: {r.Message}");
            }
            if (r is not { Ok: true })
            {
                c.Line("Profilen blev ikke installeret: " + Explain(r?.Message ?? "ukendt fejl") +
                       "\nFjern evt. den gamle profil på iPad'en med fjernelseskoden (vises i Fleetdeck under Opsætning), og prøv igen.");
                return (false, dev);
            }
        }
        finally { try { File.Delete(file); } catch (IOException) { } }

        Show(from + span * 4 / 5, to, "Tjekker iPad'en");
        var after = Details(c.Tool, dev);
        var hit = after.Profiles?.FirstOrDefault(x => x.Identifier == p.ProfileIdentifier);
        c.Line(hit is not null ? $"Den låste profil er installeret på {after.Name ?? "iPad'en"}: {hit.Name}."
            : "Profilen er sendt til iPad'en, men står ikke på listen endnu – tjek iPad'en (Indstillinger → Generelt → VPN og enhedsadministration).");
        return (true, after);
    }

    // ------------------------------------------------------------------ backup
    private sealed record BackupDone(string Root, string Name, bool Encrypted, bool? Verified, UserSession? User, NoWipeDeviceState? State = null);

    private static string BackupRoot(UserSession? user) => $"{user?.Home ?? "/var/root"}/Library/Application Support/MobileSync/Backup";

    private static async Task<JobResult> BackupAction(Api api, Ctx c, CancellationToken ct)
    {
        Show(0, 5, "Finder iPad'en");
        var (dev, err) = Find(c.Tool, c.Ecid);
        if (dev is null) return Fail(c.Log + err!);
        var b = EncryptedBackup(c, dev, 5, 75, c.Left(115));
        if (b is null) return Fail(c.Log.ToString(), dev);
        if (c.P.IpadId is int)
        {
            var up = await Upload(api, c, b, dev, 75, 100, ct);
            c.Line(up is not null ? $"Backuppen er sendt til Fleetdeck ({Gb(up.Bytes)}, {up.Parts} del(e))."
                : "Advarsel: backuppen ligger på Mac'en, men kom ikke over i Fleetdeck.");
        }
        var after = Details(c.Tool, dev);
        return Ok(c.Log.ToString(), new IosReport { Devices = [after] });
    }

    /// <summary>
    /// Krypteret backup: sæt husstandens backup-adgangskode på iPad'en, tag backup som brugeren på skærmen (så den kan ses i Finder),
    /// og tjek bagefter, at backuppen er krypteret, og at adgangskoden låser den op. null = fejl (står i loggen).
    /// </summary>
    private static BackupDone? EncryptedBackup(Ctx c, IosDeviceInfo dev, int from, int to, TimeSpan timeout, bool require = false)
    {
        // aldrig en backup af en tom iPad (eller en i opsætningen) – den ville overskrive Mac'ens gode backup og skubbe den gode ud af Fleetdeck
        var state = BackupGuard(c, dev, out var refuse);
        if (refuse is not null) { c.Line(refuse + " Intet er overskrevet."); return null; }
        var pw = c.Password;
        if (pw is not null)
        {
            Show(from, from + 2, "Slår kryptering af backups til");
            var r = Cfg(c.Tool, c.Ecid, null, TimeSpan.FromMinutes(2), null, CfgutilCommands.SetBackupPassword(pw));
            c.Line(r.Ok ? "Kryptering af backups er slået til med husstandens backup-adgangskode."
                : $"Backup-adgangskoden blev ikke sat ({r.Message}) – den er måske sat i forvejen. Backuppen tjekkes bagefter.");
        }
        else if (require) { c.Line("Backup-adgangskoden mangler fra Fleetdeck."); return null; }

        var user = Mac.Session();
        if (!Snapshot(c, dev, BackupRoot(user))) return null;
        Show(from + 2, to, "Tager backup – lås iPad'en op og lad den være tilsluttet. Det kan tage lang tid");
        var rb = Cfg(c.Tool, c.Ecid, null, timeout, user, CfgutilCommands.Backup());
        if (!rb.Ok) { c.Line("Backuppen mislykkedes: " + Explain(rb.Message)); return null; }
        var root = BackupRoot(user);
        var name = dev.Udid is { Length: > 0 } u ? u : null;
        var dir = name is null ? null : Path.Combine(root, name);
        try
        {
            if (dir is null || !Directory.Exists(dir))
            {
                // UDID ukendt: den nyeste mappe
                var newest = Directory.Exists(root) ? new DirectoryInfo(root).GetDirectories().OrderByDescending(d => d.LastWriteTimeUtc).FirstOrDefault() : null;
                if (newest is null) { c.Line($"Backuppen blev ikke fundet i {root}. " + NoDiskAccess); return null; }
                dir = newest.FullName; name = newest.Name;
            }
        }
        catch (UnauthorizedAccessException) { c.Line(NoDiskAccess); return null; }

        var manifest = Path.Combine(dir, "Manifest.plist");
        var enc = Exec("/usr/bin/plutil", ["-extract", "IsEncrypted", "raw", "-o", "-", manifest], TimeSpan.FromSeconds(30), null);
        if (enc.Exit != 0 && enc.Err.Contains("not permitted", StringComparison.OrdinalIgnoreCase)) { c.Line(NoDiskAccess); return null; }
        var encrypted = enc.Out.Trim() == "true";
        bool? verified = null;
        if (encrypted && pw is not null)
        {
            Show(to, to, "Tjekker backup-adgangskoden mod backuppen");
            var kb = Exec("/usr/bin/plutil", ["-extract", "BackupKeyBag", "raw", "-o", "-", manifest], TimeSpan.FromSeconds(30), null);
            try { verified = kb.Exit == 0 ? IosBackupKeybag.VerifyPassword(Convert.FromBase64String(kb.Out.Trim()), pw) : null; }
            catch (FormatException) { verified = null; }
        }
        c.Line($"Backup af {dev.Name ?? "iPad'en"} er gemt på Mac'en i {dir}" + (user is null ? " (ingen var logget ind – den ligger i systemets mappe)." : "."));
        c.Line(!encrypted ? "Advarsel: backuppen er IKKE krypteret – adgangskoder og sundhedsdata er ikke med."
            : verified == true ? "Backuppen er krypteret, og husstandens backup-adgangskode låser den op."
            : verified == false ? "Backuppen er krypteret med en ANDEN adgangskode end husstandens (sat tidligere, fx i Finder)."
            : "Backuppen er krypteret (adgangskoden kunne ikke tjekkes automatisk).");
        return new BackupDone(root, name!, encrypted, verified, user, state);
    }

    /// <summary>
    /// Send backup-mappen til Fleetdeck som tar.gz i dele under 1,8 GB. Strømmes direkte fra tar til en PUT med chunked overførsel (som curl -T):
    /// intet holdes i hukommelsen (curl --data-binary @fil løber tør for hukommelse ved 1,8 GB), og der kræves ikke ekstra plads på Mac'en.
    /// </summary>
    private static async Task<IosBackupComplete?> Upload(Api api, Ctx c, BackupDone b, IosDeviceInfo dev, int from, int to, CancellationToken ct)
    {
        if (c.P.IpadId is not int ipadId) return null;
        var set = $"{IpadBackupNames.Prefix}{ipadId}-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
        var total = DirSize(Path.Combine(b.Root, b.Name));
        Show(from, to, "Sender backuppen til Fleetdeck");
        var psi = new ProcessStartInfo("/usr/bin/tar") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-czf", "-", "-C", b.Root, b.Name }) psi.ArgumentList.Add(a);
        using var tar = Process.Start(psi)!;
        var err = tar.StandardError.ReadToEndAsync(ct);
        var src = tar.StandardOutput.BaseStream;
        var parts = 0; long bytes = 0;
        try
        {
            while (true)
            {
                var first = src.ReadByte();
                if (first < 0) break;
                parts++;
                var no = parts;
                var part = new PartStream(src, PartSize, (byte)first, n =>
                {
                    var done = bytes + n;
                    // den komprimerede størrelse kendes ikke: vis fremdrift mod mappens størrelse
                    Show(from + (int)Math.Min(to - from - 1, (to - from) * (double)done / Math.Max(1, total)), to,
                        $"Sender backuppen til Fleetdeck – del {no}, {Gb(done)} sendt");
                });
                await api.PutStream($"/api/agent/kit?name={Uri.EscapeDataString(IpadBackupNames.Part(set, no))}", part, ct);
                bytes += part.Count;
            }
            await tar.WaitForExitAsync(ct);
            if (tar.ExitCode != 0)
            {
                var e = (await err).Trim();
                c.Line(e.Contains("not permitted", StringComparison.OrdinalIgnoreCase) ? NoDiskAccess : $"Backuppen kunne ikke pakkes: {e}");
                return null;
            }
            var done = new IosBackupComplete
            {
                Set = set, Parts = parts, Bytes = bytes, Encrypted = b.Encrypted, PasswordVerified = b.Verified, Udid = dev.Udid, DeviceName = dev.Name, Firmware = dev.Firmware,
                DeviceBytes = b.State?.ComparableBytes, DeviceEmpty = b.State is { } st ? NoWipeSafety.LooksEmpty(st) : null,
            };
            await api.Post($"/api/agent/ios-backup/{c.Job.Id}/complete", done, ct);
            return done;
        }
        catch (HttpRequestException e)
        {
            c.Line($"Backuppen kunne ikke sendes til Fleetdeck: {e.Message}");
            return null;
        }
        finally { if (!tar.HasExited) try { tar.Kill(true); } catch (InvalidOperationException) { } }
    }

    /// <summary>Højst <c>limit</c> byte fra en anden strøm (den første byte er allerede læst for at se, om der er mere).</summary>
    private sealed class PartStream(Stream src, long limit, byte first, Action<long> progress) : Stream
    {
        private bool _first = true;
        private DateTime _shown = DateTime.MinValue;
        public long Count { get; private set; }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            if (Count >= limit || buffer.Length == 0) return 0;
            if (_first) { _first = false; buffer[0] = first; Count = 1; return 1; }
            return Counted(src.Read(buffer[..(int)Math.Min(buffer.Length, limit - Count)]));
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (Count >= limit || buffer.Length == 0) return 0;
            if (_first) { _first = false; buffer.Span[0] = first; Count = 1; return 1; }
            return Counted(await src.ReadAsync(buffer[..(int)Math.Min(buffer.Length, limit - Count)], ct));
        }
        private int Counted(int n)
        {
            Count += n;
            if (DateTime.UtcNow - _shown > TimeSpan.FromSeconds(3)) { _shown = DateTime.UtcNow; progress(Count); }
            return n;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => Count; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Hent et backup-sæt fra Fleetdeck og pak det ud i MobileSync-mappen (en eksisterende backup af iPad'en flyttes til side).</summary>
    private static async Task<bool> DownloadBackup(Api api, Ctx c, string set, string root, string name, UserSession? user, int from, int to, CancellationToken ct)
    {
        Show(from, to, "Henter backuppen fra Fleetdeck");
        try
        {
            Directory.CreateDirectory(root);
            var target = Path.Combine(root, name);
            if (Directory.Exists(target))
            {
                var aside = $"{target}.old-{DateTime.Now:yyyyMMdd-HHmmss}";
                Directory.Move(target, aside);
                c.Line($"Mac'ens egen backup af iPad'en er flyttet til {aside}.");
            }
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException) { c.Line(NoDiskAccess + $" ({e.Message})"); return false; }
        var psi = new ProcessStartInfo("/usr/bin/tar") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-xzf", "-", "-C", root }) psi.ArgumentList.Add(a);
        using var tar = Process.Start(psi)!;
        var err = tar.StandardError.ReadToEndAsync(ct);
        var parts = 0;
        try
        {
            while (true)
            {
                Show(from, to, $"Henter backuppen fra Fleetdeck – del {parts + 1}");
                if (!await api.GetToStream($"/api/agent/kit/{Uri.EscapeDataString(IpadBackupNames.Part(set, parts + 1))}", tar.StandardInput.BaseStream, ct)) break;
                parts++;
            }
            tar.StandardInput.Close();
            await tar.WaitForExitAsync(ct);
        }
        catch (Exception e) when (e is HttpRequestException or IOException)
        {
            c.Line($"Backuppen kunne ikke hentes fra Fleetdeck: {e.Message}");
            try { tar.Kill(true); } catch (InvalidOperationException) { }
            return false;
        }
        finally { if (!tar.HasExited) try { tar.Kill(true); } catch (InvalidOperationException) { } }
        if (parts == 0) { c.Line("Fleetdeck har ingen dele af backuppen."); return false; }
        if (tar.ExitCode != 0)
        {
            var e = (await err).Trim();
            c.Line(e.Contains("not permitted", StringComparison.OrdinalIgnoreCase) ? NoDiskAccess : $"Backuppen kunne ikke pakkes ud: {e}");
            return false;
        }
        if (!Directory.Exists(Path.Combine(root, name))) { c.Line($"Backuppen i Fleetdeck indeholder ikke mappen {name}."); return false; }
        if (user is not null) Exec("/usr/sbin/chown", ["-R", $"{user.User}:staff", Path.Combine(root, name)], TimeSpan.FromMinutes(10), null);
        c.Line($"Backuppen {set} er hentet fra Fleetdeck ({parts} del(e)).");
        return true;
    }

    // ------------------------------------------------------------------ gendan
    private static async Task<JobResult> RestoreAction(Api api, Ctx c, CancellationToken ct)
    {
        Show(0, 5, "Finder iPad'en");
        var (dev, err) = Find(c.Tool, c.Ecid);
        if (dev is null) return Fail(err!);
        if (dev.Udid is not { Length: > 0 } udid) return Fail("iPad'ens UDID kunne ikke læses – lås den op, tryk \"Stol på\" og prøv igen.", dev);
        var user = Mac.Session();
        var root = BackupRoot(user);
        if (c.P.BackupSet is { Length: > 0 } set)
        {
            if (!await DownloadBackup(api, c, set, root, udid, user, 5, 25, ct)) return Fail(c.Log + "Intet er slettet.", dev);
        }
        else
        {
            try
            {
                if (!Directory.Exists(Path.Combine(root, udid)))
                    return Fail($"Mac'en har ingen backup af iPad'en i {root}. Tag backup først, eller vælg en backup fra Fleetdeck. Intet er slettet.", dev);
            }
            catch (UnauthorizedAccessException) { return Fail(NoDiskAccess, dev); }
        }
        var help = HelpFor(c.Tool, "restore-backup");
        if (!Erase(c, c.Identities[0], 25, 35)) return Fail(c.Log + "Intet er slettet.", dev);
        if (WaitReady(c, 35, 45, "Venter på, at iPad'en starter igen efter sletningen – lad den stå på \"Hej\"-skærmen") is null)
            return Fail(c.Log + Recover, dev);
        if (!Restore(c, udid, user, help, 45, 95)) return Fail(c.Log.ToString(), dev);
        var after = WaitReady(c, 95, 100, "Venter på, at iPad'en genstarter efter gendannelsen") ?? dev;
        c.Line("Gendannelsen er færdig. Følg opsætningen på iPad'en, og log ind på Apple-kontoen igen (Indstillinger → [navn]).");
        return Ok(c.Log.ToString(), new IosReport { Devices = [after] });
    }

    private static bool Restore(Ctx c, string source, UserSession? user, CfgutilHelp help, int from, int to)
    {
        Show(from, to, "Lægger backuppen tilbage – rør ikke iPad'en; den genstarter, når den er færdig");
        var cmd = CfgutilCommands.RestoreBackup(source, c.Password, help);
        if (c.Password is not null && !help.Has("--password"))
            c.Line("Bemærk: denne cfgutil nævner ikke --password ved restore-backup, så adgangskoden til den krypterede backup sendes ikke med.");
        c.Line("Kører: " + CfgutilCommands.Display([.. CfgutilCommands.Global(c.Ecid), .. cmd], c.Password));
        var r = Cfg(c.Tool, c.Ecid, null, c.Left(80), user, cmd);
        if (!r.Ok)
        {
            c.Line($"Gendannelsen mislykkedes: {Explain(r.Message)}\n{Recover} Den kan også gendannes i Finder med husstandens backup-adgangskode (vises i Fleetdeck).");
            return false;
        }
        c.Line("Backuppen er lagt tilbage på iPad'en.");
        return true;
    }

    private static bool Erase(Ctx c, Ident? id, int from, int to)
    {
        Show(from, to, "Sletter iPad'en – rør den ikke, og tag ikke kablet ud");
        var r = Cfg(c.Tool, c.Ecid, id, c.Left(15), null, CfgutilCommands.Erase());
        if (!r.Ok) { c.Line("iPad'en blev ikke slettet: " + Explain(r.Message)); return false; }
        c.Line("iPad'en er slettet.");
        return true;
    }

    /// <summary>Vent på, at iPad'en er startet igen (efter sletning/gendannelse forsvinder den et stykke tid). null = den kom ikke igen.</summary>
    private static IosDeviceInfo? WaitReady(Ctx c, int from, int to, string text, int maxMinutes = 25)
    {
        Show(from, to, text);
        var until = DateTime.UtcNow + c.Left(maxMinutes);
        var cancel = Shell.JobCancel;
        // den genstarter: giv den tid til at forsvinde, før der ledes
        if (cancel.WaitHandle.WaitOne(TimeSpan.FromSeconds(45))) throw new OperationCanceledException(cancel);
        while (DateTime.UtcNow < until)
        {
            var (all, _) = Discover(c.Tool);
            if (all?.FirstOrDefault(d => d.Ecid == c.Ecid) is { } basic)
            {
                var d = Details(c.Tool, basic);
                if (d.BootedState is null or "Booted") return d;
            }
            if (cancel.WaitHandle.WaitOne(TimeSpan.FromSeconds(10))) throw new OperationCanceledException(cancel);
        }
        c.Line("iPad'en kom ikke tilbage i tide. Tjek, at kablet sidder i, og at iPad'en er tændt.");
        return null;
    }

    // ------------------------------------------------------------------ overvågning
    /// <summary>
    /// Gør iPad'en overvåget uden Apple Configurators vindue: krypteret backup → send til Fleetdeck → slet → gendan → prepare --supervised →
    /// låst profil → tjek isSupervised (eller prepare før gendan, se <see cref="SupervisePlan"/>). Intet slettes, før backuppen er tjekket og sendt.
    /// </summary>
    private static async Task<JobResult> Supervise(Api api, Ctx c, CancellationToken ct)
    {
        if (c.Server is not { } ident) return Fail("Overvågningsidentiteten kom ikke fra Fleetdeck – start handlingen igen.");
        var org = c.Secrets?.Organization is { Length: > 0 } o ? o : "Familien";
        var order = SupervisePlan.Norm(c.P.Order);
        var restoreFirst = order == SupervisePlan.RestoreFirst;
        var steps = SupervisePlan.Steps(order).ToDictionary(s => s.Id);
        string Text(string id) => steps[id].Tap is { } t ? $"{steps[id].Title} – {t}" : steps[id].Title;
        void Phase(string id) { _step = null; Show(steps[id].From, steps[id].To, Text(id)); _step = steps[id].Title; }

        Show(0, 1, "Finder iPad'en");
        var (dev, err) = Find(c.Tool, c.Ecid);
        if (dev is null) return Fail(err!);
        if (dev.Udid is not { Length: > 0 }) return Fail("iPad'ens UDID kunne ikke læses – lås den op, tryk \"Stol på\" og prøv igen.", dev);
        if (dev.Paired == false && !Pair(c, null)) return Fail(c.Log.ToString(), dev);
        if (dev.Supervised == true) c.Line("iPad'en er allerede overvåget – den overvåges nu igen med Fleetdecks identitet.");

        var helpPrepare = HelpFor(c.Tool, "prepare");
        var helpRestore = HelpFor(c.Tool, "restore-backup");
        ShowHelpOnce(c, helpPrepare);
        c.Line($"Rækkefølge: {(restoreFirst ? "backup → slet → gendan → overvåg" : "backup → slet → overvåg → gendan")}. Organisation: {org}.");

        // 1. krypteret backup – den skal kunne låses op med husstandens kode, ellers slettes intet
        Phase(SupervisePlan.Backup);
        var b = EncryptedBackup(c, dev, steps[SupervisePlan.Backup].From, steps[SupervisePlan.Backup].To, c.Left(70), require: true);
        if (b is null) return Fail(c.Log + "Intet er slettet.", dev);
        if (!b.Encrypted) return Fail(c.Log + "Backuppen er ikke krypteret, så iPad'en slettes ikke. Intet er slettet.", dev);
        if (b.Verified == false)
            return Fail(c.Log + "iPad'en har en anden backup-adgangskode end Fleetdecks, så backuppen kunne ikke gendannes herfra. Slå \"Kryptér lokal backup\" fra i Finder " +
                        "(kræver den gamle kode), og prøv igen. Intet er slettet.", dev);

        // 2. kopi i Fleetdeck
        Phase(SupervisePlan.Upload);
        var up = await Upload(api, c, b, dev, steps[SupervisePlan.Upload].From, steps[SupervisePlan.Upload].To, ct);
        if (up is null) return Fail(c.Log + "Backuppen kom ikke over i Fleetdeck, så iPad'en slettes ikke. Intet er slettet.", dev);
        c.Line($"Backuppen er sendt til Fleetdeck ({Gb(up.Bytes)}, {up.Parts} del(e)): {up.Set}.");

        // 3. slet og vent
        Phase(SupervisePlan.Erase);
        if (!Erase(c, ident, steps[SupervisePlan.Erase].From, steps[SupervisePlan.Erase].To)) return Fail(c.Log + "Intet er slettet.", dev);
        if (WaitReady(c, steps[SupervisePlan.Wait].From, steps[SupervisePlan.Wait].To, Text(SupervisePlan.Wait)) is null)
            return Fail(c.Log + "iPad'en er slettet. " + Recover, dev);

        // 4. gendan og overvåg (rækkefølgen kan vælges)
        if (restoreFirst)
        {
            Phase(SupervisePlan.Restore);
            if (!Restore(c, b.Name, b.User, helpRestore, steps[SupervisePlan.Restore].From, steps[SupervisePlan.Restore].To)) return Fail(c.Log.ToString(), dev);
            if (WaitReady(c, steps[SupervisePlan.Restore].To, steps[SupervisePlan.Prepare].From, "Venter på, at iPad'en genstarter efter gendannelsen – rør den ikke") is null)
                return Fail(c.Log + Recover, dev);
            Phase(SupervisePlan.Prepare);
            if (!Prepare(c, ident, org, helpPrepare, skipSetup: true))
                return Fail(c.Log + "Backuppen er lagt tilbage, men iPad'en er ikke overvåget. Prøv igen med den anden rækkefølge (\"overvåg før gendan\").", dev);
        }
        else
        {
            Phase(SupervisePlan.Prepare);
            // opsætningen springes ikke over: iPad'en skal stå i opsætningen, når backuppen lægges tilbage
            if (!Prepare(c, ident, org, helpPrepare, skipSetup: false)) return Fail(c.Log + Recover, dev);
            Phase(SupervisePlan.Restore);
            if (!Restore(c, b.Name, b.User, helpRestore, steps[SupervisePlan.Restore].From, steps[SupervisePlan.Restore].To)) return Fail(c.Log.ToString(), dev);
        }
        var ready = WaitReady(c, steps[SupervisePlan.Profile].From, steps[SupervisePlan.Profile].From, "Venter på, at iPad'en er klar – rør den ikke");
        if (ready is null) return Fail(c.Log + "Tjek iPad'en, og tryk \"Find tilsluttede iPads\", når den er klar.", dev);
        if (ready.Paired == false) Pair(c, ident);

        // 5. låst profil – en fejl her stopper ikke: den kan installeres bagefter
        Phase(SupervisePlan.Profile);
        var (profileOk, withProfile) = await InstallProfile(api, c, ready, steps[SupervisePlan.Profile].From, steps[SupervisePlan.Profile].To, ct);
        if (!profileOk) c.Line("Den låste profil kan installeres bagefter med \"Installér låst profil\".");

        // 6. tjek
        Phase(SupervisePlan.Verify);
        var final = Details(c.Tool, withProfile);
        if (final.Supervised != true)
            return Fail(c.Log + $"iPad'en melder ikke, at den er overvåget (isSupervised = {final.Supervised?.ToString() ?? "ukendt"}). " +
                        (restoreFirst ? "Prøv igen med den anden rækkefølge (\"overvåg før gendan\")." : "Prøv igen med standard-rækkefølgen."), final);
        c.Line($"Færdig: {final.Name ?? "iPad'en"} er overvåget af \"{org}\"{(profileOk ? " og har den låste profil" : "")}.");
        c.Line("På iPad'en: følg evt. de sidste opsætningstrin, log ind på Apple-kontoen igen (Indstillinger → [navn]), og slå Find min iPad til igen.");
        return Ok(c.Log.ToString(), new IosReport { Devices = [final] });
    }

    // ------------------------------------------------------------------ frigiv iPad'en (barnet er gammelt nok)
    /// <summary>"Løsn styring": fjern den låste profil (og den gamle DNS-profil) med husstandens identitet. iPad'en slettes ikke.</summary>
    private static JobResult RemoveLocked(Ctx c)
    {
        Show(0, 10, "Finder iPad'en");
        var (dev, err) = Find(c.Tool, c.Ecid);
        if (dev is null) return Fail(c.Log + err!);
        if (dev.Paired == false && !Pair(c, c.Identities[0])) return Fail(c.Log.ToString(), dev);
        var ok = RemoveProfiles(c, dev, 10, 90);
        Show(90, 100, "Tjekker iPad'en");
        var after = Details(c.Tool, dev);
        if (!ok) return Fail(c.Log + "Fjern den på iPad'en i stedet: Indstillinger → Generelt → VPN og enhedsadministration → den låste profil → Fjern profil " +
                             "(fjernelseskoden står i Fleetdeck under Opsætning → Frigiv iPad).", after);
        return Ok(c.Log.ToString(), new IosReport { Devices = [after] });
    }

    /// <summary>Fjern Fleetdecks profiler (den låste og den gamle DNS-profil). true = den låste profil er ikke (længere) på iPad'en.</summary>
    private static bool RemoveProfiles(Ctx c, IosDeviceInfo dev, int from, int to)
    {
        var ids = new[] { c.P.ProfileIdentifier }.Concat(c.P.ReplaceIds ?? []).OfType<string>().Where(x => x.Length > 0).Distinct().ToList();
        // profilerne kunne ikke læses: prøv alligevel
        var targets = dev.Profiles is { } installed ? ids.Where(id => installed.Any(x => x.Identifier == id)).ToList() : ids;
        if (targets.Count == 0) { c.Line("Der er ingen Fleetdeck-profil på iPad'en."); return true; }
        var lockedOk = true;
        foreach (var pid in targets)
        {
            var isLocked = pid == c.P.ProfileIdentifier;
            Show(from, to, isLocked ? "Fjerner den låste profil" : "Fjerner den gamle DNS-profil");
            Res? r = null;
            foreach (var id in c.Identities)
            {
                r = Cfg(c.Tool, c.Ecid, id, TimeSpan.FromMinutes(2), null, CfgutilCommands.RemoveProfile(pid));
                if (r.Ok) break;
            }
            if (r is { Ok: true }) c.Line(isLocked ? "Den låste profil er fjernet." : "Den gamle DNS-profil er fjernet.");
            else
            {
                c.Line($"{(isLocked ? "Den låste profil" : "Den gamle DNS-profil")} kunne ikke fjernes: {Explain(r?.Message ?? "ukendt fejl")}");
                if (isLocked) lockedOk = false;
            }
        }
        return lockedOk;
    }

    /// <summary>
    /// "Frigiv helt": fjern den låste profil (så den ikke kommer med i backuppen) → krypteret backup → send til Fleetdeck → slet →
    /// gendan backuppen UDEN prepare, så iPad'en ikke er overvåget bagefter → tjek. Intet slettes, før backuppen er tjekket og sendt.
    /// </summary>
    private static async Task<JobResult> Release(Api api, Ctx c, CancellationToken ct)
    {
        if (c.Server is not { } ident) return Fail("Overvågningsidentiteten kom ikke fra Fleetdeck – start handlingen igen.");
        void Step(int no, string title) => _step = $"Trin {no}/6: {title}";

        Show(0, 2, "Finder iPad'en");
        var (dev, err) = Find(c.Tool, c.Ecid);
        if (dev is null) return Fail(err!);
        if (dev.Udid is not { Length: > 0 }) return Fail("iPad'ens UDID kunne ikke læses – lås den op, tryk \"Stol på\" og prøv igen.", dev);
        if (dev.Paired == false && !Pair(c, ident)) return Fail(c.Log.ToString(), dev);
        var helpRestore = HelpFor(c.Tool, "restore-backup");
        c.Line("Frigiv iPad: fjern profil → backup → send til Fleetdeck → slet → gendan uden overvågning → tjek.");

        // 1. profilerne væk først – ellers kommer de tilbage med backuppen
        Step(1, "Fjerner den låste profil");
        if (!RemoveProfiles(c, dev, 2, 6))
            return Fail(c.Log + "Den låste profil kunne ikke fjernes, så iPad'en slettes ikke (profilen ville komme tilbage med backuppen). Brug \"Løsn styring\" først. Intet er slettet.", dev);
        dev = Details(c.Tool, dev);

        // 2. krypteret backup, der kan låses op med husstandens kode
        Step(2, "Tager krypteret backup");
        var b = EncryptedBackup(c, dev, 6, 40, c.Left(70), require: true);
        if (b is null) return Fail(c.Log + "Intet er slettet.", dev);
        if (!b.Encrypted) return Fail(c.Log + "Backuppen er ikke krypteret, så iPad'en slettes ikke. Intet er slettet.", dev);
        if (b.Verified == false)
            return Fail(c.Log + "iPad'en har en anden backup-adgangskode end Fleetdecks, så backuppen kunne ikke gendannes herfra. Intet er slettet.", dev);

        // 3. kopi i Fleetdeck
        Step(3, "Sender backuppen til Fleetdeck");
        var up = await Upload(api, c, b, dev, 40, 55, ct);
        if (up is null) return Fail(c.Log + "Backuppen kom ikke over i Fleetdeck, så iPad'en slettes ikke. Intet er slettet.", dev);
        c.Line($"Backuppen er sendt til Fleetdeck ({Gb(up.Bytes)}, {up.Parts} del(e)): {up.Set}.");

        // 4. slet (fjerner overvågningen) og vent
        Step(4, "Sletter iPad'en");
        if (!Erase(c, ident, 55, 60)) return Fail(c.Log + "Intet er slettet.", dev);
        if (WaitReady(c, 60, 68, "Venter på, at iPad'en starter igen efter sletningen – lad den stå på \"Hej\"-skærmen") is null)
            return Fail(c.Log + "iPad'en er slettet. " + Recover, dev);

        // 5. gendan – uden prepare, så den ikke bliver overvåget igen
        Step(5, "Lægger indholdet tilbage");
        if (!Restore(c, b.Name, b.User, helpRestore, 68, 92)) return Fail(c.Log.ToString(), dev);
        var after = WaitReady(c, 92, 97, "Venter på, at iPad'en genstarter efter gendannelsen – rør den ikke");
        if (after is null) return Fail(c.Log + "Tjek iPad'en – den er slettet, og backuppen er sendt til gendannelse.", dev);

        // 6. tjek
        Step(6, "Tjekker iPad'en");
        Show(97, 100, "Tjekker, at iPad'en ikke længere er overvåget");
        var final = Details(c.Tool, after);
        if (final.Supervised == true)
            return Fail(c.Log + "Indholdet er lagt tilbage, men iPad'en melder stadig, at den er overvåget (overvågningen fulgte med backuppen). " +
                        "Slet den igen og sæt den op som ny, eller gendan fra iCloud. Fleetdeck har stadig iPad'en.", final);
        if (final.Profiles?.Any(x => x.Identifier == c.P.ProfileIdentifier) == true)
            return Fail(c.Log + "Den låste profil er kommet tilbage med backuppen. Fjern den med \"Løsn styring\". Fleetdeck har stadig iPad'en.", final);
        c.Line($"Færdig: {final.Name ?? "iPad'en"} har sit indhold igen og er ikke længere overvåget.");
        c.Line("På iPad'en: følg de sidste opsætningstrin, log ind på Apple-kontoen igen (Indstillinger → [navn]), og slå Find min iPad til igen.");
        return Ok(c.Log.ToString(), new IosReport { Devices = [final] });
    }

    private static bool Prepare(Ctx c, Ident ident, string org, CfgutilHelp help, bool skipSetup)
    {
        var lang = c.P.Language is { Length: > 0 } l ? l : "da";
        var region = c.P.Region is { Length: > 0 } rg ? rg : "DK";
        var cmd = CfgutilCommands.Prepare(new PrepareOptions(org, ident.Cert, lang, $"{lang}_{region}", region, skipSetup), help);
        c.Line("Kører: " + CfgutilCommands.Display([.. CfgutilCommands.Global(c.Ecid, "<cert.der>", "<key.der>"), .. cmd.Select(x => x == ident.Cert ? "<cert.der>" : x)]));
        var r = Cfg(c.Tool, c.Ecid, ident, c.Left(20), null, cmd);
        if (!r.Ok) { c.Line("Overvågningen mislykkedes: " + Explain(r.Message)); return false; }
        c.Line("iPad'en er gjort overvåget (prepare --supervised).");
        return true;
    }

    /// <summary>"cfgutil help &lt;kommando&gt;" – hvilke tilvalg denne version kender.</summary>
    private static CfgutilHelp HelpFor(string tool, string cmd)
    {
        var r = Exec(tool, CfgutilCommands.Help(cmd), TimeSpan.FromSeconds(30), null);
        return CfgutilHelp.Parse(r.Out + "\n" + r.Err);
    }

    /// <summary>Første gang (pr. cfgutil-version): skriv "cfgutil help prepare" i jobbets output, så kommandoen kan tilpasses.</summary>
    private static void ShowHelpOnce(Ctx c, CfgutilHelp help)
    {
        try
        {
            var v = Exec(c.Tool, ["version"], TimeSpan.FromSeconds(30), null);
            var version = (v.Out + v.Err).Trim();
            var marker = Path.Combine(IdentityDir, ".cfgutil-help-shown");
            if (File.Exists(marker) && File.ReadAllText(marker).Trim() == version) return;
            c.Line($"cfgutil {version} – \"cfgutil help prepare\" (vises første gang):");
            c.Log.AppendLine(help.Text.Trim()).AppendLine("-----");
            RootDir(IdentityDir);
            File.WriteAllText(marker, version);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static JobResult Update(string tool, string ecid, TimeSpan timeout)
    {
        Show(0, 5, "Finder iPad'en");
        var (dev, err) = Find(tool, ecid);
        if (dev is null) return Fail(err!);
        // i gendannelsestilstand sletter "update" iPad'en – så hellere lade være
        if (dev.BootedState != "Booted")
            return Fail($"iPad'en er ikke startet normalt ({dev.BootedState ?? "ukendt tilstand"}). En opdatering kunne slette den, så den er ikke startet. " +
                        "Start iPad'en normalt, eller opdatér den i Apple Configurator.", dev);
        Show(5, 95, "Henter og installerer iPadOS – det kan tage op til en time");
        var r = Cfg(tool, ecid, null, timeout, null, "update");
        if (!r.Ok) return Fail("Opdateringen mislykkedes: " + Explain(r.Message) +
                                "\nMac'en skal have internet, og Apple Configurator skal være åbnet én gang efter sidste opdatering.", dev);
        var after = Details(tool, dev);
        return Ok($"iPadOS er opdateret på {after.Name ?? "iPad'en"}: {dev.Firmware ?? "?"} → {after.Firmware ?? "?"}.", new IosReport { Devices = [after] });
    }

    // ------------------------------------------------------------------ hjælpere
    private static (List<IosDeviceInfo>? Devices, string? Error) Discover(string tool)
    {
        var r = Cfg(tool, null, null, TimeSpan.FromMinutes(1), null, "list");
        if (!r.Ok) return (null, r.Message);
        return (r.Json is { } j ? Cfgutil.ParseList(j) : [], null);
    }

    private static (IosDeviceInfo? Device, string? Error) Find(string tool, string ecid)
    {
        var (all, err) = Discover(tool);
        if (all is null) return (null, $"cfgutil kunne ikke liste enhederne: {err}");
        if (all.FirstOrDefault(d => d.Ecid == ecid) is not { } basic)
            return (null, "iPad'en er ikke tilsluttet Mac'en lige nu. Forbind den med et USB-kabel, lås den op og prøv igen.");
        return (Details(tool, basic), null);
    }

    /// <summary>Alle oplysninger om én iPad. Fejler nogle egenskaber, bruges resten (cfgutil giver så exit-kode 1, men stadig JSON).</summary>
    private static IosDeviceInfo Details(string tool, IosDeviceInfo basic)
    {
        var r = Cfg(tool, basic.Ecid, null, TimeSpan.FromMinutes(1), null, ["get", .. Cfgutil.GetProps]);
        return r.Json is { } j && Cfgutil.ParseGet(j, basic.Ecid) is { } d ? Cfgutil.Merge(basic, d) : basic;
    }

    private static bool Pair(Ctx c, Ident? id)
    {
        Show(10, 15, "Parrer med iPad'en – lås den op og tryk \"Stol på\"");
        var r = Cfg(c.Tool, c.Ecid, id ?? c.Identities[0], TimeSpan.FromSeconds(90), null, "pair");
        if (r.Ok) { c.Line("iPad'en er parret med Mac'en."); return true; }
        c.Line($"iPad'en er ikke parret med Mac'en ({r.Message}). Lås iPad'en op, tryk \"Stol på\" og skriv koden – og prøv igen.");
        return false;
    }

    private static string Describe(IosDeviceInfo d)
    {
        var parts = new List<string> { d.Name ?? "Uden navn" };
        if (d.DeviceType is { Length: > 0 } t) parts.Add(t);
        if (d.Firmware is { Length: > 0 } f) parts.Add($"iPadOS {f}");
        parts.Add(d.Supervised switch { true => "overvåget", false => "ikke overvåget", _ => "overvågning ukendt" });
        if (d.Paired == false) parts.Add("ikke parret (tryk \"Stol på\" på iPad'en)");
        if (d.Battery is int b) parts.Add($"batteri {b} %");
        return string.Join(" · ", parts);
    }

    private static string Explain(string msg)
    {
        var m = msg.ToLowerInvariant();
        if (m.Contains("find my") || m.Contains("activation lock")) return msg + "\nSlå Find min iPad fra på iPad'en (Indstillinger → [navn] → Find) og prøv igen.";
        if (m.Contains("interaction")) return msg + "\niPad'en vil have, at nogen godkender på skærmen. Det sker, når iPad'en ikke er overvåget af denne Mac.";
        if (m.Contains("pair") || m.Contains("trust")) return msg + "\nLås iPad'en op, tryk \"Stol på\" og prøv igen.";
        if (m.Contains("passcode") || m.Contains("locked")) return msg + "\nLås iPad'en op og prøv igen.";
        if (m.Contains("operation not permitted") || m.Contains("not permitted"))
            return msg + "\nFleetdeck-agenten mangler Fuld diskadgang på Mac'en: Systemindstillinger → Anonymitet og sikkerhed → Fuld diskadgang. " +
                   "Fjern fleetdeck-agent med −, tilføj /usr/local/fleetdeck/fleetdeck-agent med + (Cmd+Shift+G), slå den til, og genstart agenten.";
        if (m.Contains("password")) return msg + "\nTjek backup-adgangskoden (vises i Fleetdeck under Opsætning).";
        return msg;
    }

    private static string Gb(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : $"{bytes / (double)(1L << 20):0} MB";

    private static long DirSize(string dir)
    {
        try { return new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
    }

    private static JobResult Ok(string text, IosReport? report = null) => new() { Status = JobStatus.Success, ExitCode = 0, Output = text.TrimEnd(), Ios = report };
    private static JobResult Fail(string text, IosDeviceInfo? dev = null) => new()
    {
        Status = JobStatus.Failed, ExitCode = 1, Output = text.TrimEnd(), Ios = dev is null ? null : new IosReport { Devices = [dev] },
    };

    private sealed record Res(int Exit, string Out, string Err, bool TimedOut, bool Cancelled)
    {
        public JsonElement? Json { get; } = Cfgutil.ExtractJson(Out) ?? Cfgutil.ExtractJson(Err);
        private (string Message, int? Code)? Error => Json is { } j ? Cfgutil.Error(j) : null;
        public bool Ok => Exit == 0 && Error is null && !TimedOut && !Cancelled;
        public int? Code => Error?.Code;
        public string Message => TimedOut ? "tiden løb ud" : Cancelled ? "annulleret fra Fleetdeck"
            : Error is { } e ? e.Message + (e.Code is int c ? $" (kode {c})" : "")
            : LastLines(Err) is { Length: > 0 } el ? el : LastLines(Out) is { Length: > 0 } ol ? ol : $"cfgutil sluttede med kode {Exit}";
        private static string LastLines(string s) => string.Join(" ", s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(3));
    }

    /// <summary>
    /// Kør cfgutil uden skal (argumenterne sendes som de er). Identiteten (-C/-K) sendes kun med, når kommandoen skal bruge den – og ikke,
    /// når cfgutil kører som brugeren på skærmen (nøglefilerne kan kun læses af root).
    /// </summary>
    private static Res Cfg(string tool, string? ecid, Ident? id, TimeSpan timeout, UserSession? asUser, params string[] cmd)
    {
        var args = CfgutilCommands.Global(ecid, asUser is null ? id?.Cert : null, asUser is null ? id?.Key : null);
        args.AddRange(cmd);
        return Exec(tool, args, timeout, asUser);
    }

    /// <summary>Kør et program med en argumentliste (evt. som brugeren på skærmen), med tidsgrænse og annullering fra Fleetdeck.</summary>
    private static Res Exec(string file, IEnumerable<string> args, TimeSpan timeout, UserSession? asUser)
    {
        var psi = new ProcessStartInfo { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        if (asUser is not null)
        {
            // som brugeren på skærmen (backup havner i brugerens MobileSync-mappe)
            psi.FileName = "/bin/launchctl";
            foreach (var a in new[] { "asuser", asUser.Uid.ToString(), "/usr/bin/sudo", "-u", asUser.User, "-H", file }) psi.ArgumentList.Add(a);
        }
        else psi.FileName = file;
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        try { p.StandardInput.Close(); } catch (IOException) { }
        var o = new StringBuilder(); var e = new StringBuilder();
        p.OutputDataReceived += (_, x) => { if (x.Data is not null) lock (o) o.AppendLine(x.Data); };
        p.ErrorDataReceived += (_, x) => { if (x.Data is not null) lock (e) e.AppendLine(x.Data); };
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        var until = DateTime.UtcNow + timeout;
        var cancel = Shell.JobCancel;
        bool done, cancelled = false;
        while (!(done = p.WaitForExit(500)))
        {
            if (cancel.IsCancellationRequested) { cancelled = true; break; }
            if (DateTime.UtcNow >= until) break;
        }
        if (!done) { try { p.Kill(true); } catch (InvalidOperationException) { } }
        p.WaitForExit(5000);
        string so, se; lock (o) so = o.ToString(); lock (e) se = e.ToString();
        return new Res(done ? p.ExitCode : -1, so, se, !done && !cancelled, cancelled);
    }
}
