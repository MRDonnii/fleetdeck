using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Fleetdeck.Server.Data;
using Fleetdeck.Shared;

namespace Fleetdeck.Server.Services;

public sealed record BackupInfo(string Name, string? Date, string? Computer, double? AgeDays);

/// <summary>Adgang til WinSetup-sharen (monteret i containeren).</summary>
public sealed class WinSetupStore(Paths paths, ILogger<WinSetupStore> log)
{
    public bool Available => Directory.Exists(paths.WinSetup);

    private static JsonElement? ReadJson(string path)
    {
        try { return File.Exists(path) ? JsonDocument.Parse(File.ReadAllText(path).TrimStart('﻿')).RootElement.Clone() : null; }
        catch (Exception) { return null; }
    }
    private static string? Str(JsonElement? e, string prop) =>
        e is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(prop, out var v) ? v.ToString() : null;

    public Dictionary<string, BackupInfo> Backups()
    {
        var res = new Dictionary<string, BackupInfo>(StringComparer.OrdinalIgnoreCase);
        var dir = Path.Combine(paths.WinSetup, "Backup");
        if (!Directory.Exists(dir)) return res;
        foreach (var d in Directory.GetDirectories(dir))
        {
            var j = ReadJson(Path.Combine(d, "backup.json"));
            if (j is null) continue;
            var date = Str(j, "Dato");
            double? age = DateTime.TryParseExact(date, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt)
                ? (DateTime.Now - dt).TotalDays : null;
            var name = Path.GetFileName(d);
            res[name] = new BackupInfo(name, date, Str(j, "Computer"), age);
        }
        return res;
    }

    public object Status(string share)
    {
        if (!Available) return new { available = false, path = paths.WinSetup, share };
        var s = Path.Combine(paths.WinSetup, "Settings");
        var appearance = ReadJson(Path.Combine(s, "appearance.json"));
        var drv = Path.Combine(paths.WinSetup, "Drivers");
        var logs = Path.Combine(paths.WinSetup, "Logs");
        return new
        {
            available = true, path = paths.WinSetup, share,
            appearance = appearance is null ? null : new
            {
                wallpaperFile = Str(appearance, "WallpaperFile"), wallpaperStyle = Str(appearance, "WallpaperStyle"),
                captured = Str(appearance, "Captured"), capturedFrom = Str(appearance, "CapturedFrom"),
                lockScreen = Str(appearance, "LockScreen") == "True",
            },
            drivers = new
            {
                count = Directory.Exists(drv) ? Directory.GetDirectories(drv).Length : 0,
                date = Directory.Exists(drv) ? Directory.GetLastWriteTime(drv).ToString("yyyy-MM-dd HH:mm") : null,
            },
            installers = Installers(),
            backups = Backups().Values.OrderBy(b => b.Name),
            logs = Directory.Exists(logs) ? Directory.GetFiles(logs, "*.log").Select(Path.GetFileName).OrderDescending().Take(40) : [],
            appsJson = File.Exists(Path.Combine(paths.WinSetup, "apps.json")),
        };
    }

    public IEnumerable<object> Installers() => Directory.Exists(paths.Installers)
        ? new DirectoryInfo(paths.Installers).GetFiles().Where(f => !f.Name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Name).Select(f => (object)new { name = f.Name, sizeMb = Math.Round(f.Length / 1048576.0, 1) })
        : [];

    public string? WallpaperPath()
    {
        var a = ReadJson(Path.Combine(paths.WinSetup, "Settings", "appearance.json"));
        var f = Str(a, "WallpaperFile");
        var p = f is null ? null : Path.Combine(paths.WinSetup, "Settings", Path.GetFileName(f));
        return p is not null && File.Exists(p) ? p : null;
    }

    public string? SafeFile(string folder, string name)
    {
        var baseDir = Path.GetFullPath(Path.Combine(paths.WinSetup, folder));
        var p = Path.GetFullPath(Path.Combine(baseDir, Path.GetFileName(name)));
        return p.StartsWith(baseDir + Path.DirectorySeparatorChar) && File.Exists(p) ? p : null;
    }

    /// <summary>agent.json + agent-exe på sharen, så USB-nøglen kan installere og tilmelde nye pc'er.</summary>
    public void PublishAgent(string serverUrl, string key, string? agentExe)
    {
        if (!Available || string.IsNullOrEmpty(serverUrl)) return;
        try
        {
            File.WriteAllText(Path.Combine(paths.WinSetup, "agent.json"), Json.Ser(new { server = serverUrl, key }));
            if (agentExe is not null && File.Exists(agentExe))
            {
                var dir = Path.Combine(paths.WinSetup, "Agent");
                Directory.CreateDirectory(dir);
                var dst = Path.Combine(dir, Path.GetFileName(agentExe));
                if (!File.Exists(dst) || new FileInfo(dst).Length != new FileInfo(agentExe).Length || File.GetLastWriteTimeUtc(dst) < File.GetLastWriteTimeUtc(agentExe))
                    File.Copy(agentExe, dst, true);
            }
        }
        catch (Exception e) { log.LogWarning("Kunne ikke skrive agent til sharen: {Message}", e.Message); }

        // SetupComplete.cmd: til USB-nøglen (Lib.ps1 kopierer den) og til netværksinstallationen (PXE\win11)
        try
        {
            var cmd = SetupCompleteCmd(serverUrl, key);
            var agentDir = Path.Combine(paths.WinSetup, "Agent");
            Directory.CreateDirectory(agentDir);
            WriteIfChanged(Path.Combine(agentDir, "SetupComplete.cmd"), cmd);
            var oem = Path.Combine(paths.Pxe, "win11", "sources", "$OEM$");
            if (!Directory.Exists(Path.Combine(paths.Pxe, "win11", "sources"))) return;
            var scripts = Path.Combine(oem, "$$", "Setup", "Scripts");
            Directory.CreateDirectory(scripts);
            WriteIfChanged(Path.Combine(scripts, "SetupComplete.cmd"), cmd);
            if (agentExe is not null && File.Exists(agentExe)) CopyIfNewer(agentExe, Path.Combine(scripts, "Fleetdeck.exe"));
            var boot = Path.Combine(oem, "$1", "WinSetupBoot");
            Directory.CreateDirectory(boot);
            var bootstrap = Path.Combine(paths.WinSetup, "USB", "Bootstrap.ps1");
            if (File.Exists(bootstrap)) CopyIfNewer(bootstrap, Path.Combine(boot, "Bootstrap.ps1"));
            WriteIfChanged(Path.Combine(boot, "agent.json"), Json.Ser(new { server = serverUrl, key }));
        }
        catch (Exception e) { log.LogWarning("Kunne ikke skrive SetupComplete.cmd: {Message}", e.Message); }
    }

    private static void WriteIfChanged(string path, string content)
    {
        if (!File.Exists(path) || File.ReadAllText(path) != content) File.WriteAllText(path, content, System.Text.Encoding.ASCII);
    }

    private static void CopyIfNewer(string src, string dst)
    {
        if (!File.Exists(dst) || new FileInfo(dst).Length != new FileInfo(src).Length || File.GetLastWriteTimeUtc(dst) < File.GetLastWriteTimeUtc(src))
            File.Copy(src, dst, true);
    }

    /// <summary>
    /// Kører som SYSTEM lige efter Windows-installationen. Netværksinstallationen lægger deploy.cmd ved siden af
    /// (WS_MODE=clean|managed|profile, WS_PROFILE, WS_DEPLOY); en USB-installation har ingen og bliver "managed".
    /// </summary>
    public static string SetupCompleteCmd(string server, string key) => string.Join("\r\n",
        "@echo off",
        "rem Fleetdeck: installer agenten som Windows-tjeneste (koeres som SYSTEM efter Windows-installationen)",
        "set \"WS_MODE=managed\"",
        "set \"WS_PROFILE=\"",
        "set \"WS_DEPLOY=\"",
        "if exist \"%~dp0deploy.cmd\" call \"%~dp0deploy.cmd\"",
        "set \"LOG=%WINDIR%\\Setup\\Scripts\\Fleetdeck-install.log\"",
        "echo Udrulning: type=%WS_MODE% profil=%WS_PROFILE% udrulning=%WS_DEPLOY% >> \"%LOG%\"",
        "if /i \"%WS_MODE%\"==\"clean\" (",
        "  echo Ren Windows: Fleetdeck installeres ikke. >> \"%LOG%\"",
        // ingen automatisk login; brugeren vælger adgangskode ved første login
        @"  reg add ""HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon"" /v AutoAdminLogon /t REG_SZ /d 0 /f >nul 2>&1",
        @"  if defined WS_USER powershell.exe -NoProfile -ExecutionPolicy Bypass -Command ""net accounts /maxpwage:unlimited | Out-Null; Set-LocalUser -Name $env:WS_USER -PasswordNeverExpires $false; net user $env:WS_USER /logonpasswordchg:yes"" >> ""%LOG%"" 2>&1",
        "  del /f /q \"%~dp0Fleetdeck.exe\" >nul 2>&1",
        "  exit /b 0",
        ")",
        "set \"WS_ARGS=--provision --no-wait\"",   // under Windows-installationen er der ofte intet netværk endnu
        "if defined WS_PROFILE set \"WS_ARGS=%WS_ARGS% --profile %WS_PROFILE%\"",
        "if defined WS_DEPLOY set \"WS_ARGS=%WS_ARGS% --deploy %WS_DEPLOY%\"",
        $"\"%~dp0Fleetdeck.exe\" install --server \"{server}\" --key \"{key}\" %WS_ARGS% >> \"%LOG%\" 2>&1",
        "exit /b 0", "");
}

/// <summary>Agentens .exe som serveren udleverer, og dens version (hash).</summary>
public sealed class AgentBinary(Paths paths)
{
    private (DateTime Mtime, string Hash)? _cache;
    public string? ExePath
    {
        get { var p = Path.Combine(paths.AgentDir, "Fleetdeck.exe"); return File.Exists(p) ? p : null; }
    }
    public string? LinuxPath => LinuxPathFor("x64");

    /// <summary>Linux-agenten pr. processor: x64 (Intel/AMD), arm64 (Raspberry Pi 3/4/5 med 64-bit), arm (32-bit Raspberry Pi OS), musl-x64/musl-arm64 (Alpine).</summary>
    public string? LinuxPathFor(string? arch)
    {
        var file = NormalizeArch(arch) switch
        {
            "arm64" => "fleetdeck-agent-arm64", "arm" => "fleetdeck-agent-arm",
            "musl-x64" => "fleetdeck-agent-musl-x64", "musl-arm64" => "fleetdeck-agent-musl-arm64", _ => "fleetdeck-agent",
        };
        var p = Path.Combine(paths.AgentDir, file);
        return File.Exists(p) ? p : null;
    }

    /// <summary>Linux- eller Mac-agenten (Mac: Apple Silicon = arm64, Intel = x64).</summary>
    public string? UnixPathFor(string? platform, string? arch)
    {
        if (platform != Platforms.Mac) return LinuxPathFor(arch);
        var p = Path.Combine(paths.AgentDir, MacAgentFile(arch));
        return File.Exists(p) ? p : null;
    }

    /// <summary>Mac-agentens fil: Apple silicon, Intel, eller Intel med macOS 10.15–11 (legacy-bygget på .NET 8).</summary>
    public static string MacAgentFile(string? arch) => NormalizeArch(arch) switch
    {
        "arm64" => "fleetdeck-agent-mac-arm64", MacCompat.LegacyArch => "fleetdeck-agent-mac-x64-legacy", _ => "fleetdeck-agent-mac-x64",
    };

    /// <summary>Mac-appens disk-image på serveren og navnet, brugeren får det hentet som.</summary>
    public static (string File, string Download) MacDmgFile(string? arch) => NormalizeArch(arch) switch
    {
        "arm64" => ("Fleetdeck-mac-arm64.dmg", "Fleetdeck-AppleSilicon.dmg"),
        MacCompat.LegacyArch => ("Fleetdeck-mac-x64-legacy.dmg", "Fleetdeck-Intel-macOS10.15-11.dmg"),
        _ => ("Fleetdeck-mac-x64.dmg", "Fleetdeck-Intel.dmg"),
    };

    /// <summary>Mac-appen som disk-image (Fleetdeck.app + installationsvindue), bygget i Docker-billedet: Apple Silicon eller Intel.</summary>
    public string? MacDmgPathFor(string? arch)
    {
        var p = Path.Combine(paths.AgentDir, MacDmgFile(arch).File);
        return File.Exists(p) ? p : null;
    }

    public static string NormalizeArch(string? arch) => (arch ?? "").ToLowerInvariant() switch
    {
        "arm64" or "aarch64" => "arm64",
        // Intel-Mac med macOS 10.15–11: .NET 8-udgaven af Mac-agenten og appen
        "x64-legacy" or "legacy-x64" or "x86_64-legacy" => MacCompat.LegacyArch,
        "arm" or "armv7l" or "armv7" or "armhf" => "arm",
        // musl (Alpine, Void-musl): egen agent, glibc-udgaven kan ikke starte
        "musl-x64" or "musl-x86_64" => "musl-x64",
        "musl-arm64" or "musl-aarch64" => "musl-arm64",
        _ => "x64",
    };

    private readonly Dictionary<string, (DateTime Mtime, string Hash)> _linuxCache = [];
    /// <summary>Agentfilen, som /api/agent/download udleverer til platformen (null = ikke bygget ind).</summary>
    public string? PathFor(string? platform, string? arch) => platform is Platforms.Linux or Platforms.Mac ? UnixPathFor(platform, arch) : ExePath;

    public string VersionFor(string? platform, string? arch = null)
    {
        if (platform is not (Platforms.Linux or Platforms.Mac)) return Version;
        var p = UnixPathFor(platform, arch);
        if (p is null) return "";
        var mt = File.GetLastWriteTimeUtc(p);
        lock (_linuxCache)
            if (_linuxCache.TryGetValue(p, out var c) && c.Mtime == mt) return c.Hash;
        using var fs = File.OpenRead(p);
        var h = Convert.ToHexStringLower(SHA256.HashData(fs))[..12];
        lock (_linuxCache) _linuxCache[p] = (mt, h);
        return h;
    }
    public string Version
    {
        get
        {
            var p = ExePath;
            if (p is null) return "";
            var mt = File.GetLastWriteTimeUtc(p);
            if (_cache is { } c && c.Mtime == mt) return c.Hash;
            using var fs = File.OpenRead(p);
            var h = Convert.ToHexStringLower(SHA256.HashData(fs))[..12];
            _cache = (mt, h);
            return h;
        }
    }
}

/// <summary>Søgning i Microsofts offentlige winget-indeks (hentes dagligt).</summary>
public sealed class WingetIndex(Paths paths, IHttpClientFactory http, ILogger<WingetIndex> log)
{
    private static readonly string[] Urls = ["https://cdn.winget.microsoft.com/cache/source2.msix", "https://cdn.winget.microsoft.com/cache/source.msix"];
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string DbPath => Path.Combine(paths.Data, "winget-index.db");

    public async Task<bool> Refresh(bool force = false)
    {
        if (!force && File.Exists(DbPath) && File.GetLastWriteTimeUtc(DbPath) > DateTime.UtcNow.AddDays(-1)) return true;
        await _lock.WaitAsync();
        try
        {
            foreach (var url in Urls)
            {
                var tmp = DbPath + ".download";
                try
                {
                    var c = http.CreateClient("winget");
                    await using (var s = await c.GetStreamAsync(url))
                    await using (var f = File.Create(tmp)) await s.CopyToAsync(f);
                    using (var z = ZipFile.OpenRead(tmp))
                    {
                        var e = z.Entries.First(x => x.FullName.EndsWith("index.db", StringComparison.OrdinalIgnoreCase));
                        e.ExtractToFile(DbPath + ".new", true);
                    }
                    SqliteConnection.ClearAllPools();
                    File.Move(DbPath + ".new", DbPath, true);
                    File.Delete(tmp);
                    log.LogInformation("winget-indeks hentet fra {Url}", url);
                    return true;
                }
                catch (Exception e)
                {
                    log.LogWarning("Kunne ikke hente {Url}: {Message}", url, e.Message);
                    foreach (var f in new[] { tmp, DbPath + ".new" }) if (File.Exists(f)) File.Delete(f);
                }
            }
            return File.Exists(DbPath);
        }
        finally { _lock.Release(); }
    }

    public sealed record Hit(string Id, string Name, string? Version);

    public async Task<List<Hit>> Search(string q, int limit = 50)
    {
        if (!await Refresh()) throw new InvalidOperationException("winget-indekset kunne ikke hentes");
        await using var con = new SqliteConnection($"Data Source={DbPath};Mode=ReadOnly");
        await con.OpenAsync();
        var tables = new HashSet<string>();
        await using (var t = con.CreateCommand())
        {
            t.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
            await using var r = await t.ExecuteReaderAsync();
            while (await r.ReadAsync()) tables.Add(r.GetString(0));
        }
        await using var cmd = con.CreateCommand();
        if (tables.Contains("packages"))
            cmd.CommandText = "SELECT id, name, latest_version FROM packages WHERE id LIKE $q OR name LIKE $q LIMIT 400";
        else if (tables.Contains("manifest"))
            cmd.CommandText = "SELECT ids.id, names.name, versions.version FROM manifest JOIN ids ON manifest.id=ids.rowid " +
                              "JOIN names ON manifest.name=names.rowid JOIN versions ON manifest.version=versions.rowid " +
                              "WHERE ids.id LIKE $q OR names.name LIKE $q LIMIT 2000";
        else throw new InvalidOperationException("Ukendt indeks-format");
        cmd.Parameters.AddWithValue("$q", $"%{q}%");
        var best = new Dictionary<string, Hit>(StringComparer.OrdinalIgnoreCase);
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync())
            {
                var h = new Hit(r.GetString(0), r.IsDBNull(1) ? r.GetString(0) : r.GetString(1), r.IsDBNull(2) ? null : r.GetValue(2).ToString());
                if (!best.TryGetValue(h.Id, out var cur) || VerKey(h.Version) > VerKey(cur.Version)) best[h.Id] = h;
            }
        var ql = q.ToLowerInvariant();
        string[] unstable = [".beta", ".dev", ".nightly", ".canary", ".preview", ".msix", ".insider"];
        return best.Values.OrderBy(h =>
            {
                var id = h.Id.ToLowerInvariant(); var n = h.Name.ToLowerInvariant(); var last = id.Split('.')[^1];
                return n == ql || id == ql || last == ql ? 0 : n.StartsWith(ql) || last.StartsWith(ql) ? 1 : 2;
            })
            .ThenBy(h => unstable.Any(u => h.Id.Contains(u, StringComparison.OrdinalIgnoreCase)) ? 1 : 0)
            .ThenBy(h => h.Id.Length).Take(limit).ToList();
    }

    private static Version VerKey(string? v)
    {
        var parts = (v ?? "0").Split('.', '-', '+').Select(p => int.TryParse(p, out var i) ? i : 0).Take(4).ToList();
        while (parts.Count < 2) parts.Add(0);
        return new Version(string.Join('.', parts));
    }
}

/// <summary>Importér WinSetup\apps.json til kataloget første gang.</summary>
public sealed class SeedService(AppDb db, Paths paths, SettingsService settings, AuditService audit)
{
    private static readonly HashSet<string> PerUser = new(StringComparer.OrdinalIgnoreCase)
    {
        "Spotify.Spotify", "Discord.Discord", "Telegram.TelegramDesktop", "Anthropic.Claude", "OpenAI.ChatGPT",
        "GitHub.CopilotApp", "Microsoft.VisualStudioCode", "9PKTQ5699M62",
    };
    public static bool IsPerUser(string id) => PerUser.Contains(id);

    public async Task<(int Added, int ProfileId)> ImportAppsJson(string by)
    {
        var file = Path.Combine(paths.WinSetup, "apps.json");
        if (!File.Exists(file)) throw new FileNotFoundException("apps.json findes ikke på sharen");
        using var doc = JsonDocument.Parse(File.ReadAllText(file).TrimStart('﻿'));
        // Profilen, apps.json importeres til: genkendes på beskrivelsen, så et omdøbt navn bevares
        var prof = await db.Profiles.FirstOrDefaultAsync(p => EF.Functions.Like(p.Description, "%importeret fra WinSetup%"));
        if (prof is null)
        {
            prof = new Profile { Name = Environment.GetEnvironmentVariable("IMPORT_PROFILE") ?? "Fra WinSetup", Description = "Importeret fra WinSetup (apps.json)", UpgradeMode = "daily", BackupMode = "weekly", WindowsUpdateMode = "weekly" };
            db.Profiles.Add(prof);
            await db.SaveChangesAsync();
        }
        var added = 0;
        foreach (var a in doc.RootElement.EnumerateArray())
        {
            string S(string n) => a.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            bool B(string n) => a.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;
            var id = S("Id"); var type = S("Type") is { Length: > 0 } t ? t : PackageTypes.Winget; var name = S("Name");
            var pkg = id.Length > 0
                ? await db.Packages.FirstOrDefaultAsync(p => p.Type == type && p.Identifier == id)
                : await db.Packages.FirstOrDefaultAsync(p => p.Name == name);
            if (pkg is null)
            {
                var args = S("Args");
                if (id == "Python.Python.3.12" && !args.Contains("--scope")) args = (args + " --scope machine").Trim();
                pkg = new Package
                {
                    Name = name, Type = type, Identifier = id, Args = args, Notes = S("Note"),
                    RunAs = B("NotAdmin") || IsPerUser(id) ? RunAs.User : RunAs.System, Category = Category(id, name),
                };
                db.Packages.Add(pkg);
                await db.SaveChangesAsync();
                added++;
            }
            if (!await db.ProfilePackages.AnyAsync(pp => pp.ProfileId == prof.Id && pp.PackageId == pkg.Id))
                db.ProfilePackages.Add(new ProfilePackage { ProfileId = prof.Id, PackageId = pkg.Id, Intent = B("Enabled") ? Intents.Required : Intents.Available });
        }
        await db.SaveChangesAsync();
        if (await settings.DefaultProfileId() is null) await settings.Set("default_profile_id", prof.Id.ToString());
        await audit.Log(by, "import.apps_json", $"{added} nye pakker, profil {prof.Name}");
        return (added, prof.Id);
    }

    private static string Category(string id, string name)
    {
        var i = (id + " " + name).ToLowerInvariant();
        (string Cat, string[] Keys)[] g =
        [
            ("Runtime", ["vcredist", "dotnet", "directx", "gameinput", "pawnio", "winfsp"]),
            ("Spil", ["steam", "epic", "battlenet", "playstation", "codmunity"]),
            ("Udvikling", ["git", "python", "visualstudiocode", "notepad", "arduino", "putty", "copilot"]),
            ("Hardware", ["cpu-z", "gpu-z", "coretemp", "crystaldisk", "hardwaremonitor", "latencymon", "presentmon", "msicenter", "ghub", "goxlr", "amd"]),
            ("Netværk", ["anydesk", "vnc", "ipscanner", "filezilla", "unraid", "raspberry"]),
            ("Kontor", ["libreoffice", "acrobat", "thunderbird", "greenshot", "7zip", "winrar", "chrome", "icloud"]),
            ("Kommunikation", ["discord", "telegram", "claude", "chatgpt", "spotify", "vlc"]),
            ("Design", ["fusion", "bambu", "rew", "ajax"]),
        ];
        return g.FirstOrDefault(x => x.Keys.Any(i.Contains)).Cat ?? "Andet";
    }
}
