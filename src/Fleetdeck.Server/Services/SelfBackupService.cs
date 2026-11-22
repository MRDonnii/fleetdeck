using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Fleetdeck.Server.Data;

namespace Fleetdeck.Server.Services;

public sealed record BackupSchedule(bool Enabled, int Hour, int Keep, string Dir, string ExtraPath, bool IncludeKits, bool IncludeIpad, bool HasPassword);

/// <summary>Ændring af tidsplanen. <see cref="Password"/>: null = uændret, "" = fjern, ellers ny adgangskode.</summary>
public sealed record BackupScheduleUpdate(bool Enabled, int Hour, int Keep, string? Dir, string? ExtraPath, bool IncludeKits, bool IncludeIpad, string? Password);

public sealed record BackupFileInfo(string Name, string Kind, long Size, DateTime Created, string AppVersion, string Encryption, List<string> Contents, string Folder);

public sealed record BackupStatus(DateTime? LastOk, DateTime? LastAttempt, string LastError, string? LastName, string? Alert, DateTime? NextRun);

/// <summary>
/// Backup af Fleetdeck selv: manuelle backups, den daglige planlagte backup (med N kopier og evt. en ekstra mappe),
/// alarm/besked hvis den fejler eller er for gammel, gendannelse (udpak → genstart) og backup før migrationer.
/// </summary>
public sealed partial class SelfBackupService(Paths paths, IServiceScopeFactory scopes, IDataProtectionProvider dp, Notifier notifier,
    IHostApplicationLifetime life, ILogger<SelfBackupService> log) : BackgroundService
{
    public const int RestartExitCode = 75;
    /// <summary>Procesens slutkode: 75 når serveren skal genstartes for at lægge en gendannelse ind.</summary>
    public static int ExitCode { get; private set; }
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(2);

    private const string KEnabled = "backup_enabled", KHour = "backup_hour", KKeep = "backup_keep", KDir = "backup_dir",
        KExtra = "backup_extra_path", KKits = "backup_include_kits", KIpad = "backup_include_ipad", KPassword = "backup_password",
        KLastOk = "backup_last_ok", KLastAttempt = "backup_last_attempt", KLastError = "backup_last_error", KLastName = "backup_last_name",
        KSince = "backup_since", KAlertState = "backup_alert_state";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IDataProtector _protector = dp.CreateProtector("Fleetdeck.SelfBackup.Password");
    private (DateTime At, long Kits, long Ipad)? _sizes;

    public string DataDir => paths.Data;
    public string SafetyFolder => Path.Combine(paths.Data, SelfBackup.SafetyDir);
    public string MainFolder(BackupSchedule s) => Path.Combine(paths.Data, s.Dir);
    private IEnumerable<string> Folders(BackupSchedule s) => new[] { MainFolder(s), SafetyFolder }.Select(Path.GetFullPath).Distinct();

    // ---------- indstillinger ----------

    private async Task<T> WithSettings<T>(Func<SettingsService, Task<T>> f)
    {
        using var scope = scopes.CreateScope();
        return await f(scope.ServiceProvider.GetRequiredService<SettingsService>());
    }

    public Task<BackupSchedule> Schedule() => WithSettings(async s =>
    {
        async Task<string> G(string k, string d) => (await s.Get(k)) is { Length: > 0 } v ? v : d;
        return new BackupSchedule(
            await G(KEnabled, "1") == "1",
            int.TryParse(await G(KHour, "3"), out var h) && h is >= 0 and < 24 ? h : 3,
            int.TryParse(await G(KKeep, "7"), out var k) && k is >= 1 and <= 100 ? k : 7,
            ValidDir(await G(KDir, "backups")) ? await G(KDir, "backups") : "backups",
            await s.Get(KExtra),
            await s.Get(KKits) == "1", await s.Get(KIpad) == "1",
            (await s.Get(KPassword)).Length > 0);
    });

    [GeneratedRegex(@"^[A-Za-z0-9._-]+(/[A-Za-z0-9._-]+)*$")]
    private static partial Regex DirRx();
    private static readonly string[] ReservedDirs = ["keys", TerminalVault.Dir, "kits", "ipad-backups", SelfBackup.StagingDir, "opdatering-backup"];

    public static bool ValidDir(string d) =>
        DirRx().IsMatch(d) && d.Split('/').All(p => p is not ("." or "..")) && !ReservedDirs.Contains(d.Split('/')[0], StringComparer.OrdinalIgnoreCase)
        && !d.StartsWith("restore-old-", StringComparison.Ordinal);

    /// <summary>Gemmer tidsplanen. Returnerer en fejltekst, hvis noget er ugyldigt.</summary>
    public Task<string?> SaveSchedule(BackupScheduleUpdate u) => WithSettings<string?>(async s =>
    {
        var dir = (u.Dir ?? "backups").Trim().Trim('/');
        if (dir.Length == 0) dir = "backups";
        if (!ValidDir(dir)) return "Backup-mappen skal være en undermappe i datamappen (bogstaver, tal, . _ - og /)";
        if (u.Hour is < 0 or > 23) return "Tidspunktet skal være mellem 0 og 23";
        if (u.Keep is < 1 or > 100) return "Antal backups skal være mellem 1 og 100";
        var extra = (u.ExtraPath ?? "").Trim();
        if (extra.Length > 0)
        {
            if (!Path.IsPathFullyQualified(extra)) return "Den ekstra mappe skal være en fuld sti, fx /mnt/backup/fleetdeck";
            if (!Directory.Exists(extra)) return "Den ekstra mappe findes ikke (er drevet eller sharet monteret i containeren?)";
            var full = Path.GetFullPath(extra).TrimEnd('/', '\\') + Path.DirectorySeparatorChar;
            if ((Path.GetFullPath(paths.Data).TrimEnd('/', '\\') + Path.DirectorySeparatorChar).StartsWith(full, StringComparison.Ordinal))
                return "Den ekstra mappe må ikke være datamappen eller en mappe over den";
        }
        if (u.Password is { Length: > 0 } pw && pw.Length < SelfBackup.MinPasswordLength)
            return $"Adgangskoden skal være på mindst {SelfBackup.MinPasswordLength} tegn";
        await s.Set(KEnabled, u.Enabled ? "1" : "0");
        await s.Set(KHour, u.Hour.ToString(CultureInfo.InvariantCulture));
        await s.Set(KKeep, u.Keep.ToString(CultureInfo.InvariantCulture));
        await s.Set(KDir, dir);
        await s.Set(KExtra, extra);
        await s.Set(KKits, u.IncludeKits ? "1" : "0");
        await s.Set(KIpad, u.IncludeIpad ? "1" : "0");
        if (u.Password is not null) await s.Set(KPassword, u.Password.Length == 0 ? "" : _protector.Protect(u.Password));
        return null;
    });

    private Task<string?> StoredPassword() => WithSettings<string?>(async s =>
    {
        var v = await s.Get(KPassword);
        if (v.Length == 0) return null;
        try { return _protector.Unprotect(v); }
        catch (CryptographicException) { return null; }
    });

    private static DateTime? ParseUtc(string v) =>
        DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    public Task<BackupStatus> Status() => WithSettings(async s =>
    {
        var sched = await Schedule();
        var lastOk = ParseUtc(await s.Get(KLastOk));
        var (_, alert) = Evaluate(sched, lastOk, await s.Get(KLastError), ParseUtc(await s.Get(KSince)), DateTime.UtcNow);
        return new BackupStatus(lastOk, ParseUtc(await s.Get(KLastAttempt)), await s.Get(KLastError),
            (await s.Get(KLastName)) is { Length: > 0 } n ? n : null, alert, sched.Enabled ? NextRun(sched, lastOk, DateTime.Now) : null);
    });

    /// <summary>Alarmtilstand: "" (ok), "failed" (seneste fejlede) eller "stale" (seneste vellykkede er over 2 dage gammel).</summary>
    public static (string State, string? Text) Evaluate(BackupSchedule s, DateTime? lastOk, string lastError, DateTime? since, DateTime nowUtc)
    {
        if (!s.Enabled) return ("", null);
        var reference = lastOk ?? since;
        if (reference is { } r && nowUtc - r > StaleAfter)
            return ("stale", lastOk is null ? "Der er endnu ikke lavet en vellykket backup af Fleetdeck." : $"Seneste vellykkede backup af Fleetdeck er fra {lastOk.Value.ToLocalTime():dd-MM-yyyy HH:mm} – over 2 dage gammel.");
        if (lastError.Length > 0) return ("failed", $"Seneste backup af Fleetdeck fejlede: {lastError}");
        return ("", null);
    }

    /// <summary>Næste planlagte kørsel (lokal tid → UTC). Er dagens tidspunkt passeret uden backup, er det nu.</summary>
    public static DateTime NextRun(BackupSchedule s, DateTime? lastOkUtc, DateTime nowLocal)
    {
        var slot = nowLocal.Date.AddHours(s.Hour);
        if (nowLocal < slot) slot = slot.AddDays(-1);
        if (lastOkUtc is null || lastOkUtc.Value < slot.ToUniversalTime()) return nowLocal.ToUniversalTime();
        return slot.AddDays(1).ToUniversalTime();
    }

    // ---------- liste ----------

    public async Task<List<BackupFileInfo>> List()
    {
        var s = await Schedule();
        var o = new List<BackupFileInfo>();
        foreach (var dir in Folders(s).Where(Directory.Exists))
            foreach (var f in Directory.EnumerateFiles(dir, "*" + SelfBackup.Extension))
            {
                var name = Path.GetFileName(f);
                if (!SelfBackup.NameRx().IsMatch(name) || o.Any(x => x.Name == name)) continue;
                try
                {
                    var h = SelfBackup.ReadHeader(f);
                    o.Add(new(name, h.Kind, new FileInfo(f).Length, h.Created, h.AppVersion, h.Kdf == "local" ? "local" : "password", h.Contents,
                        Path.GetFullPath(dir) == Path.GetFullPath(MainFolder(s)) ? "main" : "safety"));
                }
                catch (Exception e) when (e is BackupException or IOException) { }
            }
        return o.OrderByDescending(x => x.Created).ToList();
    }

    /// <summary>Fuld sti til en gemt backup ud fra navnet (kun kendte navne i backup-mapperne).</summary>
    public async Task<string?> Find(string name)
    {
        if (!SelfBackup.NameRx().IsMatch(name)) return null;
        var s = await Schedule();
        return Folders(s).Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists);
    }

    /// <summary>Størrelse af de store, valgfrie dele (gendannelsessæt og iPad-backups), gemt i 5 minutter.</summary>
    public (long Kits, long Ipad) LargeSizes()
    {
        if (_sizes is { } c && DateTime.UtcNow - c.At < TimeSpan.FromMinutes(5)) return (c.Kits, c.Ipad);
        static long Size(string d) => Directory.Exists(d) ? Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories).Sum(f => { try { return new FileInfo(f).Length; } catch (IOException) { return 0L; } }) : 0;
        var r = (Size(Path.Combine(paths.Data, "kits")), Size(Path.Combine(paths.Data, "ipad-backups")));
        _sizes = (DateTime.UtcNow, r.Item1, r.Item2);
        return r;
    }

    // ---------- oprettelse ----------

    /// <summary>"Tag backup nu": med den angivne adgangskode (eller den gemte). Returnerer filnavnet.</summary>
    public async Task<string> CreateManual(string? password, bool useStored, BackupOptions opt)
    {
        var pw = useStored ? await StoredPassword() ?? throw new BackupException("Der er ingen gemt backup-adgangskode", "password") : password;
        if (string.IsNullOrEmpty(pw) || pw.Length < SelfBackup.MinPasswordLength)
            throw new BackupException($"Adgangskoden skal være på mindst {SelfBackup.MinPasswordLength} tegn", "password");
        var s = await Schedule();
        var name = SelfBackup.FileName("manual", DateTime.UtcNow, ThisVersion.Full);
        await _gate.WaitAsync();
        try
        {
            await Task.Run(() => SelfBackup.Create(paths.Data, Path.Combine(MainFolder(s), name), BackupSecret.FromPassword(pw), "manual", opt, ThisVersion.Full));
            SelfBackup.Prune(MainFolder(s), "manual", s.Keep);
        }
        finally { _gate.Release(); }
        return name;
    }

    /// <summary>Den planlagte backup (også "Kør nu"). Returnerer null ved succes, ellers fejlteksten.</summary>
    public async Task<string?> RunScheduled(CancellationToken ct = default)
    {
        var s = await Schedule();
        await _gate.WaitAsync(ct);
        string? error = null;
        string? name = null;
        try
        {
            var pw = await StoredPassword();
            var secret = pw is not null ? BackupSecret.FromPassword(pw) : BackupSecret.FromLocalKey(SelfBackup.LocalKey(paths.Data));
            name = SelfBackup.FileName("auto", DateTime.UtcNow, ThisVersion.Full);
            var file = Path.Combine(MainFolder(s), name);
            await Task.Run(() => SelfBackup.Create(paths.Data, file, secret, "auto", new(s.IncludeKits, s.IncludeIpad), ThisVersion.Full), ct);
            SelfBackup.Prune(MainFolder(s), "auto", s.Keep);
            log.LogInformation("Backup af Fleetdeck gemt: {Name}", name);
            if (s.ExtraPath.Length > 0)
            {
                try
                {
                    if (!Directory.Exists(s.ExtraPath)) throw new IOException("mappen findes ikke (er den monteret?)");
                    var part = Path.Combine(s.ExtraPath, name + ".part");
                    await Task.Run(() => { File.Copy(file, part, true); File.Move(part, Path.Combine(s.ExtraPath, name), true); }, ct);
                    SelfBackup.Prune(s.ExtraPath, "auto", s.Keep);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    error = $"Backuppen er gemt i datamappen, men kunne ikke kopieres til {s.ExtraPath}: {e.Message}";
                }
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            error = e is BackupException or IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException ? e.Message : $"{e.GetType().Name}: {e.Message}";
            name = null;
            log.LogError(e, "Backup af Fleetdeck fejlede");
        }
        finally { _gate.Release(); }

        await WithSettings(async st =>
        {
            var now = DateTime.UtcNow.ToString("O");
            await st.Set(KLastAttempt, now);
            if (name is not null) { await st.Set(KLastOk, now); await st.Set(KLastName, name); }
            await st.Set(KLastError, error ?? "");
            return 0;
        });
        await CheckAlert();
        return error;
    }

    // ---------- gendannelse ----------

    /// <summary>Pak en backup ud og kontrollér den; den lægges ind ved næste start (se <see cref="RequestRestart"/>).</summary>
    public async Task<RestorePlan> Stage(string file, string? password, string source, AppDb db)
    {
        var known = db.Database.GetMigrations().ToList();
        await _gate.WaitAsync();
        try
        {
            var secret = BackupSecret.Either(password, SelfBackup.LocalKey(paths.Data));
            return await Task.Run(() => SelfBackup.Stage(paths.Data, file, secret, known, source));
        }
        finally { _gate.Release(); }
    }

    /// <summary>Stopper serveren lidt efter svaret er sendt, med slutkode 75, så Docker/systemd starter den igen.</summary>
    public void RequestRestart()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(1500);
            log.LogWarning("Genstarter Fleetdeck for at gendanne fra backup");
            ExitCode = RestartExitCode;
            life.StopApplication();
        });
    }

    public string UploadFolder => Path.Combine(SafetyFolder, ".uploads");

    /// <summary>Gemmer en uploadet backup (kun én ad gangen) og returnerer id og hoved.</summary>
    public async Task<(string Id, BackupHeader Header)> SaveUpload(Stream body, CancellationToken ct)
    {
        SelfBackup.TryDeleteDir(UploadFolder);
        Directory.CreateDirectory(UploadFolder);
        var id = Guid.NewGuid().ToString("N");
        var file = Path.Combine(UploadFolder, id + SelfBackup.Extension);
        await using (var f = File.Create(file)) await body.CopyToAsync(f, ct);
        try { return (id, SelfBackup.ReadHeader(file)); }
        catch { SelfBackup.TryDelete(file); throw; }
    }

    public string? UploadFile(string id) =>
        Regex.IsMatch(id, "^[0-9a-f]{32}$") && Path.Combine(UploadFolder, id + SelfBackup.Extension) is var f && File.Exists(f) ? f : null;

    // ---------- tidsplan og alarm ----------

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(2), stop); } catch (OperationCanceledException) { return; }
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        do
        {
            try { await Tick(stop); }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogError(e, "Fejl i backup-tidsplanen"); }
        } while (await timer.WaitForNextTickAsync(stop));
    }

    private async Task Tick(CancellationToken stop)
    {
        var s = await Schedule();
        using (var scope = scopes.CreateScope())
        {
            // tom server (ingen brugere endnu, måske på vej til at blive gendannet): intet at tage backup af
            if (!await scope.ServiceProvider.GetRequiredService<AppDb>().Users.AnyAsync(stop)) return;
            var st = scope.ServiceProvider.GetRequiredService<SettingsService>();
            if ((await st.Get(KSince)).Length == 0) await st.Set(KSince, DateTime.UtcNow.ToString("O"));
            if (s.Enabled)
            {
                var lastOk = ParseUtc(await st.Get(KLastOk));
                var lastAttempt = ParseUtc(await st.Get(KLastAttempt));
                var due = NextRun(s, lastOk, DateTime.Now) <= DateTime.UtcNow;
                // fejlet: prøv igen efter en time
                if (due && (lastAttempt is null || DateTime.UtcNow - lastAttempt.Value >= TimeSpan.FromHours(1) || lastOk >= lastAttempt))
                {
                    await RunScheduled(stop);
                    return;
                }
            }
        }
        await CheckAlert();
    }

    /// <summary>Sender besked (Home Assistant / webhook) når alarmtilstanden skifter.</summary>
    public async Task CheckAlert()
    {
        var s = await Schedule();
        await WithSettings(async st =>
        {
            var (state, text) = Evaluate(s, ParseUtc(await st.Get(KLastOk)), await st.Get(KLastError), ParseUtc(await st.Get(KSince)), DateTime.UtcNow);
            var old = await st.Get(KAlertState);
            if (state == old) return 0;
            await st.Set(KAlertState, state);
            if (state.Length > 0) notifier.Send($"🔴 Fleetdeck-backup\n{text}", "alert");
            else if (old.Length > 0) notifier.Send("🟢 Fleetdeck-backup\nBackuppen kører igen", "resolved");
            return 0;
        });
    }

    // ---------- før migration ----------

    /// <summary>
    /// Ved opstart: har databasen migrationer, der venter, tages først en backup (låst med serverens egen nøgle,
    /// så den kan gendannes uden adgangskode på denne server). De 3 nyeste gemmes.
    /// </summary>
    public static async Task<string?> BeforeMigrate(AppDb db, string dataDir, string version, ILogger log)
    {
        if (!File.Exists(Path.Combine(dataDir, SelfBackup.DbFile))) return null;
        var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
        if (pending.Count == 0 || !(await db.Database.GetAppliedMigrationsAsync()).Any()) return null;
        var dir = Path.Combine(dataDir, SelfBackup.SafetyDir);
        var name = SelfBackup.FileName("pre-migration", DateTime.UtcNow, version);
        try
        {
            SelfBackup.Create(dataDir, Path.Combine(dir, name), BackupSecret.FromLocalKey(SelfBackup.LocalKey(dataDir)), "pre-migration", new(), version);
            SelfBackup.Prune(dir, "pre-migration", SelfBackup.SafetyKeep);
            log.LogInformation("Backup før {Count} migration(er) gemt: {Name}", pending.Count, name);
            return name;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Opstarten fortsætter (ellers kan serveren slet ikke starte) – men det skal kunne ses i loggen
            log.LogError(e, "Backup før migration fejlede – migrationerne køres alligevel");
            return null;
        }
    }
}
