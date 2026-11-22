using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Fleetdeck.Server.Services;

/// <summary>En gendannelse, der er pakket ud og kontrolleret og venter på næste start (restore-pending/restore.json).</summary>
public sealed class RestorePlan
{
    public DateTime Staged { get; set; }
    public string Source { get; set; } = "";
    public string Kind { get; set; } = "";
    public DateTime BackupCreated { get; set; }
    public string BackupVersion { get; set; } = "";
    public string? Migration { get; set; }
    public List<string> Contents { get; set; } = [];
}

/// <summary>Resultatet af seneste gendannelse (restore-result.json), vises i Backup-fanen.</summary>
public sealed class RestoreResult
{
    public bool Ok { get; set; }
    public DateTime At { get; set; }
    public string Message { get; set; } = "";
    public DateTime? BackupCreated { get; set; }
    public string? BackupVersion { get; set; }
    public string? PreRestore { get; set; }
}

public static partial class SelfBackup
{
    private static readonly JsonSerializerOptions Compact = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Trin 1 af en gendannelse: pak backuppen ud i <c>restore-pending/</c>, kontrollér databasen og at den passer til
    /// denne version, og skriv planen til sidst (så en halvfærdig udpakning aldrig bliver lagt ind).
    /// Selve gendannelsen sker ved næste start, før databasen åbnes (<see cref="ApplyPending"/>).
    /// </summary>
    public static RestorePlan Stage(string dataDir, string file, BackupSecret secret, IEnumerable<string> knownMigrations, string sourceName)
    {
        var staging = Path.Combine(dataDir, StagingDir);
        TryDeleteDir(staging);
        Directory.CreateDirectory(staging);
        try
        {
            var files = Path.Combine(staging, "files");
            var (h, m) = Extract(file, secret, files);
            var migration = PrepareDb(Path.Combine(files, DbFile), knownMigrations);
            var plan = new RestorePlan
            {
                Staged = DateTime.UtcNow, Source = sourceName, Kind = h.Kind, BackupCreated = h.Created, BackupVersion = h.AppVersion,
                Migration = migration, Contents = h.Contents,
            };
            var tmp = Path.Combine(staging, "restore.json.tmp");
            File.WriteAllText(tmp, JsonSerializer.Serialize(plan, Compact));
            File.Move(tmp, Path.Combine(staging, "restore.json"));
            return plan;
        }
        catch
        {
            TryDeleteDir(staging);
            throw;
        }
    }

    /// <summary>Venter der en gendannelse på næste start?</summary>
    public static RestorePlan? Pending(string dataDir)
    {
        var f = Path.Combine(dataDir, StagingDir, "restore.json");
        try { return File.Exists(f) ? JsonSerializer.Deserialize<RestorePlan>(File.ReadAllText(f), Compact) : null; }
        catch (Exception e) when (e is JsonException or IOException) { return null; }
    }

    public static void CancelPending(string dataDir) => TryDeleteDir(Path.Combine(dataDir, StagingDir));

    /// <summary>
    /// Kontrollerer databasen i en backup: SQLite's integritetstjek, at det er en Fleetdeck-database, og at dens seneste
    /// migration kendes af denne version (en backup fra en nyere version afvises). Slår WAL til som på en ny database.
    /// </summary>
    public static string PrepareDb(string db, IEnumerable<string> knownMigrations)
    {
        string? migration;
        try
        {
            var cs = new SqliteConnectionStringBuilder { DataSource = db, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString();
            using (var c = new SqliteConnection(cs))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "PRAGMA integrity_check";
                if (cmd.ExecuteScalar() as string != "ok") throw new BackupException("Databasen i backuppen er beskadiget", "tampered");
                cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name IN ('Users','Settings')";
                if (Convert.ToInt32(cmd.ExecuteScalar()) != 2) throw new BackupException("Backuppen indeholder ikke en Fleetdeck-database", "invalid");
                cmd.CommandText = "PRAGMA journal_mode=WAL";
                cmd.ExecuteNonQuery();
            }
            migration = LatestMigration(db);
        }
        catch (SqliteException e) { throw new BackupException($"Databasen i backuppen kan ikke læses ({e.SqliteErrorCode})", "tampered"); }
        if (migration is null) throw new BackupException("Backuppen indeholder ikke en Fleetdeck-database", "invalid");
        if (!knownMigrations.Contains(migration))
            throw new BackupException("Backuppen er fra en nyere Fleetdeck-version end denne. Opdatér Fleetdeck først, og gendan så igen.", "newer");
        return migration;
    }

    /// <summary>
    /// Trin 2 (ved opstart, før databasen og nøglerne åbnes): læg en ventende gendannelse ind. De nuværende data gemmes
    /// først som en <c>pre-restore</c>-backup; går noget galt undervejs, lægges de oprindelige filer tilbage.
    /// </summary>
    public static RestoreResult? ApplyPending(string dataDir, string appVersion, Action<string> log)
    {
        var staging = Path.Combine(dataDir, StagingDir);
        if (!Directory.Exists(staging)) return null;
        var plan = Pending(dataDir);
        var files = Path.Combine(staging, "files");
        if (plan is null || !File.Exists(Path.Combine(files, DbFile)))
        {
            TryDeleteDir(staging);
            log("Gendannelse: en ufuldstændig udpakning blev fundet og fjernet");
            return null;
        }
        log($"Gendannelse: lægger backuppen fra {plan.BackupCreated:u} (v{plan.BackupVersion}) ind");
        var kits = Directory.Exists(Path.Combine(files, "kits"));
        var ipad = Directory.Exists(Path.Combine(files, "ipad-backups"));
        List<string> items = [DbFile, DbFile + "-wal", DbFile + "-shm", "keys", "extensions.json"];
        if (Directory.Exists(Path.Combine(files, TerminalVault.Dir))) items.Add(TerminalVault.Dir);
        if (kits) items.Add("kits");
        if (ipad) items.Add("ipad-backups");
        var result = new RestoreResult { At = DateTime.UtcNow, BackupCreated = plan.BackupCreated, BackupVersion = plan.BackupVersion };

        // 1. De nuværende data sikres først (kun gendannelig på denne server; ingen adgangskode nødvendig)
        if (File.Exists(Path.Combine(dataDir, DbFile)))
        {
            try
            {
                var dir = Path.Combine(dataDir, SafetyDir);
                var name = FileName("pre-restore", DateTime.UtcNow, appVersion);
                Create(dataDir, Path.Combine(dir, name), BackupSecret.FromLocalKey(LocalKey(dataDir)), "pre-restore", new(kits, ipad), appVersion, rawDbFallback: true);
                Prune(dir, "pre-restore", SafetyKeep);
                result.PreRestore = name;
                log($"Gendannelse: de hidtidige data er gemt som {name}");
            }
            catch (Exception e)
            {
                result.Message = $"Gendannelsen blev ikke udført, fordi de nuværende data ikke kunne sikres først: {e.Message}";
                log("Gendannelse: " + result.Message);
                WriteResult(dataDir, result);
                TryDeleteDir(staging);
                return result;
            }
        }

        // 2. Byt filerne – flyt (ikke kopiér), så det går hurtigt og kan rulles tilbage
        var old = Path.Combine(dataDir, $"restore-old-{DateTime.UtcNow:yyyyMMddHHmmss}");
        var movedOut = new List<string>();
        var movedIn = new List<string>();
        try
        {
            Directory.CreateDirectory(old);
            foreach (var it in items)
                if (MoveAny(Path.Combine(dataDir, it), Path.Combine(old, it))) movedOut.Add(it);
            foreach (var it in items)
                if (MoveAny(Path.Combine(files, it), Path.Combine(dataDir, it))) movedIn.Add(it);
            result.Ok = true;
            result.Message = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Gendannet fra backuppen fra {plan.BackupCreated:yyyy-MM-dd HH:mm} UTC (v{plan.BackupVersion}).");
            log("Gendannelse: færdig");
        }
        catch (Exception e)
        {
            log($"Gendannelse fejlede ({e.Message}) – lægger de oprindelige filer tilbage");
            foreach (var it in movedIn) { var p = Path.Combine(dataDir, it); TryDelete(p); TryDeleteDir(p); }
            var rollbackOk = true;
            foreach (var it in movedOut)
            {
                try { MoveAny(Path.Combine(old, it), Path.Combine(dataDir, it)); }
                catch (Exception re) { rollbackOk = false; log($"Kunne ikke lægge {it} tilbage: {re.Message} (ligger i {old})"); }
            }
            result.Message = $"Gendannelsen fejlede: {e.Message}." + (rollbackOk ? " De oprindelige data er lagt tilbage." : $" Nogle filer ligger stadig i {Path.GetFileName(old)}.");
            if (!rollbackOk) { WriteResult(dataDir, result); TryDeleteDir(staging); return result; }
        }
        WriteResult(dataDir, result);
        TryDeleteDir(staging);
        TryDeleteDir(old);
        return result;
    }

    private static bool MoveAny(string from, string to)
    {
        if (File.Exists(from)) { File.Move(from, to); return true; }
        if (Directory.Exists(from)) { Directory.Move(from, to); return true; }
        return false;
    }

    private static void WriteResult(string dataDir, RestoreResult r)
    {
        try { File.WriteAllText(Path.Combine(dataDir, ResultFile), JsonSerializer.Serialize(r, Compact)); }
        catch (IOException) { }
    }

    public static RestoreResult? LastResult(string dataDir)
    {
        var f = Path.Combine(dataDir, ResultFile);
        try { return File.Exists(f) ? JsonSerializer.Deserialize<RestoreResult>(File.ReadAllText(f), Compact) : null; }
        catch (Exception e) when (e is JsonException or IOException) { return null; }
    }

    public static void ClearResult(string dataDir) => TryDelete(Path.Combine(dataDir, ResultFile));
}
