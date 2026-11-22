using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Fleetdeck.Server.Services;

/// <summary>Fejl ved backup/gendannelse. <see cref="Code"/>: password | foreign | tampered | invalid | newer | io.</summary>
public sealed class BackupException(string message, string code = "invalid") : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>Hvad en backup skal have med ud over det nødvendige (database, nøgler, egne udvidelser).</summary>
public sealed record BackupOptions(bool Kits = false, bool Ipad = false);

/// <summary>Adgangskode eller serverens lokale backup-nøgle (kun automatiske sikkerhedskopier).</summary>
public sealed class BackupSecret
{
    public string? Password { get; private init; }
    public byte[]? LocalKey { get; private init; }
    public static BackupSecret FromPassword(string pw) => new() { Password = pw };
    public static BackupSecret FromLocalKey(byte[] key) => new() { LocalKey = key };
    /// <summary>Adgangskode hvis den er givet; lokal nøgle som reserve (så automatiske backups kan gendannes uden kode).</summary>
    public static BackupSecret Either(string? pw, byte[] key) => new() { Password = string.IsNullOrEmpty(pw) ? null : pw, LocalKey = key };
}

/// <summary>Ukrypteret hoved i .fdbackup-filen (fdbackup.json). Hele hovedet er autentificeret (AAD), så ændringer afvises.</summary>
public sealed class BackupHeader
{
    public string Format { get; set; } = SelfBackup.FormatName;
    public int FormatVersion { get; set; } = 1;
    public string AppVersion { get; set; } = "";
    public DateTime Created { get; set; }
    /// <summary>manual | auto | pre-migration | pre-restore</summary>
    public string Kind { get; set; } = "manual";
    public string? Migration { get; set; }
    public List<string> Contents { get; set; } = [];
    /// <summary>pbkdf2-sha512 (adgangskode) eller local (serverens egen nøgle, kun gendannelig på samme server)</summary>
    public string Kdf { get; set; } = "pbkdf2-sha512";
    public int Iterations { get; set; }
    public string Salt { get; set; } = "";
    public string? KeyId { get; set; }
    public string Cipher { get; set; } = "aes-256-gcm-chunked";
    public int Chunk { get; set; }
    public string Nonce { get; set; } = "";
    /// <summary>Kontrolværdi afledt af nøglen: skelner "forkert adgangskode" fra "filen er ændret".</summary>
    public string Check { get; set; } = "";
}

public sealed class BackupManifest
{
    public string AppVersion { get; set; } = "";
    public DateTime Created { get; set; }
    public string Kind { get; set; } = "";
    public string? Migration { get; set; }
    public string Host { get; set; } = "";
    public List<BackupManifestFile> Files { get; set; } = [];
}

public sealed record BackupManifestFile(string Path, long Size);

/// <summary>
/// Backup og gendannelse af Fleetdeck selv. En .fdbackup-fil er en zip med
/// <c>fdbackup.json</c> (hovedet, ukrypteret men autentificeret) og <c>payload.bin</c>: en indre zip
/// (database fra <c>VACUUM INTO</c>, <c>keys/</c>, <c>extensions.json</c> og evt. gendannelsessæt/iPad-backups),
/// krypteret med AES-256-GCM i blokke på 1 MiB. Nøglen afledes af adgangskoden med PBKDF2-HMAC-SHA512.
/// </summary>
public static partial class SelfBackup
{
    public const string FormatName = "fleetdeck-backup";
    public const string Extension = ".fdbackup";
    public const string DbFile = "fleetdeck.db";
    public const string StagingDir = "restore-pending";
    public const string ResultFile = "restore-result.json";
    public const string LocalKeyFile = "backup-local.key";
    public const string SafetyDir = "backups";
    public const int MinPasswordLength = 10;
    public const int Iterations = 600_000;
    internal const int MinIterations = 100_000, MaxIterations = 10_000_000;
    private const int ChunkSize = 1 << 20;
    private const int TagSize = 16;
    private const uint FinalFlag = 0x8000_0000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Kendte typer og hvor mange der gemmes som standard (sikkerhedskopierne: altid 3).</summary>
    public static readonly string[] Kinds = ["manual", "auto", "pre-migration", "pre-restore"];
    public const int SafetyKeep = 3;

    // ---------- navne og mapper ----------

    [GeneratedRegex(@"^fleetdeck-(manual|auto|pre-migration|pre-restore)-\d{8}-\d{6}-\d{3}(-v[0-9A-Za-z.\-]{1,40})?\.fdbackup$")]
    public static partial Regex NameRx();

    public static string FileName(string kind, DateTime utc, string version) =>
        $"fleetdeck-{kind}-{utc:yyyyMMdd-HHmmss-fff}-v{Regex.Replace(version, "[^0-9A-Za-z.\\-]", "")}{Extension}";

    public static string KindOf(string name) => Kinds.FirstOrDefault(k => name.StartsWith($"fleetdeck-{k}-", StringComparison.Ordinal)) ?? "";

    /// <summary>Serverens egen backup-nøgle (32 tilfældige bytes, kun læsbar for serveren). Den er ikke med i backups og overskrives aldrig ved gendannelse.</summary>
    public static byte[] LocalKey(string dataDir)
    {
        var f = Path.Combine(dataDir, LocalKeyFile);
        if (File.Exists(f))
        {
            var b = File.ReadAllBytes(f);
            if (b.Length == 32) return b;
        }
        var key = RandomNumberGenerator.GetBytes(32);
        var tmp = f + ".tmp";
        File.WriteAllBytes(tmp, key);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(tmp, f, true);
        return key;
    }

    internal static string KeyId(byte[] localKey) => Convert.ToHexStringLower(SHA256.HashData(localKey))[..16];

    // ---------- oprettelse ----------

    /// <summary>
    /// Laver en backup af datamappen i <paramref name="destFile"/>. Databasen tages med <c>VACUUM INTO</c>, så den er
    /// konsistent, selv om serveren kører. <paramref name="rawDbFallback"/>: kopiér filerne direkte, hvis VACUUM fejler
    /// (kun når intet andet har databasen åben, fx ved opstart før gendannelse).
    /// </summary>
    public static BackupHeader Create(string dataDir, string destFile, BackupSecret secret, string kind, BackupOptions opt,
        string appVersion, bool rawDbFallback = false)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(destFile))!;
        Directory.CreateDirectory(dir);
        var snap = Path.Combine(dir, $".snapshot-{Guid.NewGuid():N}.db");
        var part = destFile + ".part";
        var dbPath = Path.Combine(dataDir, DbFile);
        var rawFiles = new List<string>();
        try
        {
            if (!File.Exists(dbPath)) throw new BackupException("Databasen findes ikke", "io");
            try { VacuumInto(dbPath, snap); }
            catch (SqliteException) when (rawDbFallback)
            {
                // Databasen kan ikke læses af SQLite (måske beskadiget): tag filerne, som de er
                foreach (var sfx in new[] { "", "-wal", "-shm" })
                    if (File.Exists(dbPath + sfx)) rawFiles.Add(DbFile + sfx);
            }
            var migration = rawFiles.Count == 0 ? LatestMigration(snap) : null;

            var salt = RandomNumberGenerator.GetBytes(16);
            var prefix = RandomNumberGenerator.GetBytes(7);
            var h = new BackupHeader
            {
                AppVersion = appVersion, Created = DateTime.UtcNow, Kind = kind, Migration = migration,
                Salt = Convert.ToBase64String(salt), Nonce = Convert.ToBase64String(prefix), Chunk = ChunkSize,
            };
            if (secret.Password is { } pw)
            {
                if (pw.Length < MinPasswordLength) throw new BackupException($"Adgangskoden skal være på mindst {MinPasswordLength} tegn", "password");
                h.Kdf = "pbkdf2-sha512"; h.Iterations = Iterations;
            }
            else if (secret.LocalKey is { } lk) { h.Kdf = "local"; h.KeyId = KeyId(lk); }
            else throw new BackupException("Ingen adgangskode", "password");
            h.Contents.Add("db"); h.Contents.Add("keys");
            if (File.Exists(Path.Combine(dataDir, "extensions.json"))) h.Contents.Add("extensions");
            if (opt.Kits && Directory.Exists(Path.Combine(dataDir, "kits"))) h.Contents.Add("kits");
            if (opt.Ipad && Directory.Exists(Path.Combine(dataDir, "ipad-backups"))) h.Contents.Add("ipad-backups");

            var km = DeriveKey(h, secret);
            h.Check = CheckValue(km);
            var headerBytes = JsonSerializer.SerializeToUtf8Bytes(h, Json);
            var aad = SHA256.HashData(headerBytes);

            var manifest = new BackupManifest { AppVersion = appVersion, Created = h.Created, Kind = kind, Migration = migration, Host = Environment.MachineName };
            using (var fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(part, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                using var outer = new ZipArchive(fs, ZipArchiveMode.Create);
                using (var hs = outer.CreateEntry("fdbackup.json", CompressionLevel.NoCompression).Open()) hs.Write(headerBytes);
                using var es = outer.CreateEntry("payload.bin", CompressionLevel.NoCompression).Open();
                using var enc = new GcmChunkWriter(es, km.AsSpan(0, 32).ToArray(), prefix, aad, ChunkSize);
                using (var inner = new ZipArchive(enc, ZipArchiveMode.Create, leaveOpen: true))
                {
                    if (rawFiles.Count == 0) Add(inner, manifest, snap, DbFile, CompressionLevel.Optimal);
                    else foreach (var f in rawFiles) Add(inner, manifest, Path.Combine(dataDir, f), f, CompressionLevel.Optimal);
                    AddDir(inner, manifest, dataDir, "keys", CompressionLevel.Optimal);
                    AddDir(inner, manifest, dataDir, TerminalVault.Dir, CompressionLevel.Optimal);   // nøgleboksens hovednøgle: kun i den krypterede backup
                    if (h.Contents.Contains("extensions")) Add(inner, manifest, Path.Combine(dataDir, "extensions.json"), "extensions.json", CompressionLevel.Optimal);
                    if (h.Contents.Contains("kits")) AddDir(inner, manifest, dataDir, "kits", CompressionLevel.Fastest);
                    if (h.Contents.Contains("ipad-backups")) AddDir(inner, manifest, dataDir, "ipad-backups", CompressionLevel.Fastest);
                    using var ms = inner.CreateEntry("manifest.json", CompressionLevel.Optimal).Open();
                    JsonSerializer.Serialize(ms, manifest, Json);
                }
            }
            File.Move(part, destFile, true);
            return h;
        }
        finally
        {
            TryDelete(snap);
            TryDelete(part);
        }
    }

    private static void Add(ZipArchive z, BackupManifest m, string file, string name, CompressionLevel level)
    {
        var e = z.CreateEntry(name, level);
        e.LastWriteTime = File.GetLastWriteTime(file);
        using var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var dst = e.Open();
        src.CopyTo(dst);
        m.Files.Add(new(name, src.Length));
    }

    private static void AddDir(ZipArchive z, BackupManifest m, string dataDir, string sub, CompressionLevel level)
    {
        var root = Path.Combine(dataDir, sub);
        if (!Directory.Exists(root)) return;
        foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var rel = Path.GetRelativePath(dataDir, f).Replace('\\', '/');
            if (rel.EndsWith(".part", StringComparison.Ordinal) || rel.EndsWith(".tmp", StringComparison.Ordinal)) continue;   // halvfærdige uploads
            try { Add(z, m, f, rel, level); }
            catch (FileNotFoundException) { }   // slettet undervejs
        }
    }

    /// <summary>Konsistent kopi af en kørende SQLite-database (online, uden at låse skrivere ude).</summary>
    public static void VacuumInto(string db, string dest)
    {
        TryDelete(dest);
        var cs = new SqliteConnectionStringBuilder { DataSource = db, Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 60 }.ToString();
        using var c = new SqliteConnection(cs);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "VACUUM INTO $p";
        cmd.Parameters.AddWithValue("$p", dest);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Seneste anvendte EF-migration i en databasefil (null hvis ingen).</summary>
    public static string? LatestMigration(string db)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = db, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
        using var c = new SqliteConnection(cs);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='__EFMigrationsHistory'";
        if (cmd.ExecuteScalar() is null) return null;
        cmd.CommandText = "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId DESC LIMIT 1";
        return cmd.ExecuteScalar() as string;
    }

    // ---------- nøgler ----------

    private static byte[] DeriveKey(BackupHeader h, BackupSecret s)
    {
        byte[] salt;
        try { salt = Convert.FromBase64String(h.Salt); } catch (FormatException) { throw Invalid(); }
        if (salt.Length < 16) throw Invalid();
        switch (h.Kdf)
        {
            case "pbkdf2-sha512":
                if (s.Password is null) throw new BackupException("Backuppen er beskyttet med en adgangskode – skriv den", "password");
                if (h.Iterations is < MinIterations or > MaxIterations) throw Invalid();
                return Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(s.Password.Normalize(NormalizationForm.FormC)), salt, h.Iterations, HashAlgorithmName.SHA512, 64);
            case "local":
                if (s.LocalKey is null || h.KeyId != KeyId(s.LocalKey))
                    throw new BackupException("Backuppen er lavet automatisk og er låst med en anden Fleetdeck-servers egen nøgle. Den kan kun gendannes på den server, der lavede den.", "foreign");
                return HKDF.DeriveKey(HashAlgorithmName.SHA512, s.LocalKey, 64, salt, "fleetdeck-backup"u8.ToArray());
            default: throw Invalid();
        }
    }

    private static string CheckValue(byte[] km) => Convert.ToBase64String(HMACSHA256.HashData(km.AsSpan(32, 32), "fleetdeck-backup-check"u8)[..16]);

    private static BackupException Invalid() => new("Filen er ikke en gyldig Fleetdeck-backup", "invalid");
    private static BackupException Tampered() => new("Backupfilen er beskadiget eller ændret og kan ikke bruges", "tampered");

    // ---------- læsning ----------

    /// <summary>Læser hovedet (uden adgangskode). Kaster <see cref="BackupException"/>, hvis det ikke er en Fleetdeck-backup.</summary>
    public static BackupHeader ReadHeader(string file) => ReadHeader(file, out _);

    private static BackupHeader ReadHeader(string file, out byte[] raw)
    {
        try
        {
            using var z = ZipFile.OpenRead(file);
            return ReadHeader(z, out raw);
        }
        catch (InvalidDataException) { throw Invalid(); }
    }

    private static BackupHeader ReadHeader(ZipArchive z, out byte[] raw)
    {
        var he = z.GetEntry("fdbackup.json") ?? throw Invalid();
        if (he.Length is <= 0 or > 64 * 1024) throw Invalid();
        using (var s = he.Open()) { raw = new byte[he.Length]; s.ReadExactly(raw); }
        BackupHeader? h;
        try { h = JsonSerializer.Deserialize<BackupHeader>(raw, Json); } catch (JsonException) { throw Invalid(); }
        if (h is null || h.Format != FormatName || h.Cipher != "aes-256-gcm-chunked" || h.Chunk is < 4096 or > 16 << 20) throw Invalid();
        if (h.FormatVersion > 1) throw new BackupException("Backuppen er lavet af en nyere Fleetdeck-version. Opdatér Fleetdeck først.", "newer");
        return h;
    }

    /// <summary>
    /// Dekrypterer og pakker backuppen ud i <paramref name="targetDir"/> (som skal være tom). Kontrollerer adgangskode,
    /// at intet er ændret (GCM-mærke for hver blok, rækkefølge og sidste blok) og at alle stier er tilladte.
    /// </summary>
    public static (BackupHeader Header, BackupManifest Manifest) Extract(string file, BackupSecret secret, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        var target = Path.GetFullPath(targetDir) + Path.DirectorySeparatorChar;
        var inner = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(targetDir.TrimEnd('/', '\\')))!, $".inner-{Guid.NewGuid():N}.zip");
        try
        {
            BackupHeader h;
            try
            {
                using var z = ZipFile.OpenRead(file);
                h = ReadHeader(z, out var raw);
                var km = DeriveKey(h, secret);
                if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(CheckValue(km)), Encoding.ASCII.GetBytes(h.Check)))
                    throw h.Kdf == "local" ? Tampered() : new BackupException("Forkert adgangskode til backuppen", "password");
                byte[] prefix;
                try { prefix = Convert.FromBase64String(h.Nonce); } catch (FormatException) { throw Invalid(); }
                if (prefix.Length != 7) throw Invalid();
                var pe = z.GetEntry("payload.bin") ?? throw Invalid();
                using var src = pe.Open();
                using var dst = new FileStream(inner, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
                Decrypt(src, dst, km.AsSpan(0, 32).ToArray(), prefix, SHA256.HashData(raw), h.Chunk);
            }
            catch (InvalidDataException) { throw Tampered(); }

            BackupManifest? manifest = null;
            try
            {
                using var iz = ZipFile.OpenRead(inner);
                foreach (var e in iz.Entries)
                {
                    if (e.FullName.EndsWith('/')) continue;
                    if (e.FullName == "manifest.json")
                    {
                        using var ms = e.Open();
                        manifest = JsonSerializer.Deserialize<BackupManifest>(ms, Json);
                        continue;
                    }
                    if (!AllowedEntry(e.FullName)) throw Invalid();
                    var dest = Path.GetFullPath(Path.Combine(targetDir, e.FullName));
                    if (!dest.StartsWith(target, StringComparison.Ordinal)) throw Invalid();
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    e.ExtractToFile(dest, false);
                    if (!OperatingSystem.IsWindows() && (e.FullName.StartsWith("keys/", StringComparison.Ordinal) || e.FullName.StartsWith(TerminalVault.Dir + "/", StringComparison.Ordinal) || e.FullName.StartsWith(DbFile, StringComparison.Ordinal)))
                        File.SetUnixFileMode(dest, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
            }
            catch (InvalidDataException) { throw Tampered(); }
            catch (JsonException) { throw Invalid(); }
            if (manifest is null || !File.Exists(Path.Combine(targetDir, DbFile))) throw Invalid();
            return (h, manifest);
        }
        finally { TryDelete(inner); }
    }

    [GeneratedRegex(@"^(fleetdeck\.db(-wal|-shm)?|extensions\.json|(keys|vault)/[^/]+|(kits|ipad-backups)/[^\\:*?""<>|]+)$")]
    private static partial Regex AllowedRx();

    public static bool AllowedEntry(string name) =>
        AllowedRx().IsMatch(name) && !name.Split('/').Any(p => p is "" or "." or "..") && !name.Contains('\\') && !Path.IsPathRooted(name);

    // ---------- kryptering i blokke ----------

    /// <summary>
    /// Hver blok: 4 bytes længde (big-endian, øverste bit = sidste blok), krypteret tekst og 16 bytes GCM-mærke.
    /// Nonce = 7 tilfældige bytes + bloknummer (4) + sidste-blok-flag (1); AAD = SHA-256 af hovedet.
    /// Byttede, fjernede eller tilføjede blokke og ændringer i hovedet giver derfor en fejl.
    /// </summary>
    private sealed class GcmChunkWriter(Stream inner, byte[] key, byte[] prefix, byte[] aad, int chunk) : Stream
    {
        private readonly AesGcm _aes = new(key, TagSize);
        private readonly byte[] _buf = new byte[chunk];
        private int _n;
        private uint _counter;
        private bool _done;

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> data)
        {
            while (data.Length > 0)
            {
                var take = Math.Min(data.Length, _buf.Length - _n);
                data[..take].CopyTo(_buf.AsSpan(_n));
                _n += take; data = data[take..];
                if (_n == _buf.Length) Emit(false);
            }
        }

        private void Emit(bool final)
        {
            if (_counter == uint.MaxValue) throw new BackupException("Backuppen er for stor", "io");
            var nonce = Nonce(prefix, _counter, final);
            var ct = new byte[_n];
            var tag = new byte[TagSize];
            _aes.Encrypt(nonce, _buf.AsSpan(0, _n), ct, tag, aad);
            Span<byte> len = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(len, (uint)_n | (final ? FinalFlag : 0));
            inner.Write(len); inner.Write(ct); inner.Write(tag);
            _counter++; _n = 0;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_done) { _done = true; Emit(true); _aes.Dispose(); }
            base.Dispose(disposing);
        }

        public override void Flush() { }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private static byte[] Nonce(byte[] prefix, uint counter, bool final)
    {
        var n = new byte[12];
        prefix.CopyTo(n, 0);
        BinaryPrimitives.WriteUInt32BigEndian(n.AsSpan(7), counter);
        n[11] = final ? (byte)1 : (byte)0;
        return n;
    }

    private static void Decrypt(Stream src, Stream dst, byte[] key, byte[] prefix, byte[] aad, int chunk)
    {
        using var aes = new AesGcm(key, TagSize);
        var lenBuf = new byte[4];
        var ct = new byte[chunk];
        var pt = new byte[chunk];
        var tag = new byte[TagSize];
        uint counter = 0;
        var final = false;
        while (true)
        {
            var r = src.ReadAtLeast(lenBuf, 4, throwOnEndOfStream: false);
            if (r == 0) break;
            if (r < 4 || final) throw Tampered();
            var raw = BinaryPrimitives.ReadUInt32BigEndian(lenBuf);
            final = (raw & FinalFlag) != 0;
            var len = (int)(raw & ~FinalFlag);
            if (len > chunk) throw Tampered();
            if (src.ReadAtLeast(ct.AsSpan(0, len), len, false) < len || src.ReadAtLeast(tag, TagSize, false) < TagSize) throw Tampered();
            try { aes.Decrypt(Nonce(prefix, counter, final), ct.AsSpan(0, len), tag, pt.AsSpan(0, len), aad); }
            catch (AuthenticationTagMismatchException) { throw Tampered(); }
            dst.Write(pt, 0, len);
            counter++;
        }
        if (!final) throw Tampered();
    }

    // ---------- oprydning ----------

    /// <summary>Beholder de <paramref name="keep"/> nyeste backups af en type i mappen og sletter resten.</summary>
    public static List<string> Prune(string dir, string kind, int keep)
    {
        var removed = new List<string>();
        if (!Directory.Exists(dir)) return removed;
        var files = Directory.EnumerateFiles(dir, $"fleetdeck-{kind}-*{Extension}")
            .Select(Path.GetFileName).OfType<string>()
            .Where(n => NameRx().IsMatch(n) && KindOf(n) == kind)
            .OrderByDescending(n => n, StringComparer.Ordinal).ToList();
        foreach (var n in files.Skip(Math.Max(1, keep)))
        {
            try { File.Delete(Path.Combine(dir, n)); removed.Add(n); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return removed;
    }

    internal static void TryDelete(string f)
    {
        try { if (File.Exists(f)) File.Delete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    internal static void TryDeleteDir(string d)
    {
        try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
