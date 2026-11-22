using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fleetdeck.Shared;

namespace Fleetdeck.Agent.Windows;

public sealed class AgentOptions
{
    public string Dir { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Fleetdeck");
    public bool DryRun { get; set; }
    public bool Once { get; set; }
    public bool IsService { get; set; }
    public string Work => Path.Combine(Dir, "work");
    public string UserWork => Path.Combine(Dir, "userwork");
    public string ConfigFile => Path.Combine(Dir, "config.json");
    public string LogFile => Path.Combine(Dir, "agent.log");
    public static bool IsSystem => WindowsIdentity.GetCurrent().User?.Value == "S-1-5-18";
}

/// <summary>Agentens konfiguration. Tokenet er krypteret med DPAPI (kun denne maskine kan læse det).</summary>
public sealed class AgentConfig : ITrustStore
{
    public string Server { get; set; } = "";
    public int MachineId { get; set; }
    public string Token { get; set; } = "";            // DPAPI-beskyttet, base64
    public string? PendingKey { get; set; }            // tilmelding der venter på netværk
    public bool PendingProvision { get; set; }
    /// <summary>Fast adgangskode til RustDesk (ubemandet adgang), lavet af agenten.</summary>
    public string? RemotePassword { get; set; }
    public int? PendingProfile { get; set; }           // udrulning fra netværket: valgt profil
    public int? PendingDeploy { get; set; }
    /// <summary>Det gamle token (DPAPI-beskyttet) fra før en geninstallation, til tilmeldingen når der er netværk.</summary>
    public string? PendingPreviousToken { get; set; }
    /// <summary>"Anmod om adgang", der venter på administratoren. Hemmeligheden er DPAPI-beskyttet ligesom tokenet.</summary>
    public PendingAccess? Access { get; set; }
    /// <summary>Sidste svar på anmodningen, når den er afsluttet uden tilmelding (afvist, udløbet …) – vises i ikonet og opsætningen.</summary>
    public string? AccessState { get; set; }
    public string? AccessReason { get; set; }
    public DateTime? PausedUntil { get; set; }
    /// <summary>ESP Flasher på denne pc (http://localhost:porten): null = standard (8587), 0 = slået fra.</summary>
    public int? LocalWebPort { get; set; }
    /// <summary>Serverens offentlige nøgle (PEM), husket ved første kontakt: kun agentfiler signeret med den installeres.</summary>
    public string? SigningKey { get; set; }        // sat fra ikonet i proceslinjen (kun administrator): ingen nye jobs indtil da
    /// <summary>Serverens jobnøgle (PEM), pinnet ved tilmelding eller første signerede svar: herefter afvises usignerede jobs.</summary>
    public string? JobKey { get; set; }
    /// <summary>Fastlåst TLS-nøgle for https (selvsigneret certifikat), SHA-256 af SPKI i base64.</summary>
    public string? TlsPin { get; set; }
    /// <summary>Adressen før seneste flytning: bruges, hvis https-forbindelsen holder op med at virke (kun med signeret svar).</summary>
    public string? PreviousServer { get; set; }
    /// <summary>Netværksdrev, Fleetdeck selv har forbundet, pr. bruger ("DOMÆNE\bruger" → bogstav → \\server\mappe). Kun de må fjernes igen.</summary>
    public Dictionary<string, Dictionary<string, string>>? MappedDrives { get; set; }
    /// <summary>Printere, Fleetdeck selv har lagt på (nøgle "p12" → navn, adresse, indstillinger). Kun de må ændres og fjernes igen.</summary>
    public Dictionary<string, OwnedPrinter>? Printers { get; set; }

    private static readonly JsonSerializerOptions Opt = new() { WriteIndented = true };

    public static AgentConfig? Load(string file) =>
        File.Exists(file) ? JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(file), Opt) : null;
    public void Save(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(this, Opt));
    }
    public string PlainToken() => Token.Length == 0 ? "" :
        Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(Token), null, DataProtectionScope.LocalMachine));
    public string? PlainPreviousToken()
    {
        if (PendingPreviousToken is not { Length: > 0 } t) return null;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(t), null, DataProtectionScope.LocalMachine)); }
        catch (Exception e) when (e is CryptographicException or FormatException) { return null; }
    }
    public void SetAccess(PendingAccess p)
    {
        Access = new PendingAccess
        {
            Id = p.Id, Code = p.Code, Pin = p.Pin, PinNeeded = p.PinNeeded, Name = p.Name, Created = p.Created, Expires = p.Expires,
            Secret = Protect(p.Secret), PrivateKey = Protect(p.PrivateKey),
        };
        AccessState = null; AccessReason = null;
    }
    /// <summary>Anmodningen med hemmelighed og privat nøgle i klartekst (null = ingen, eller de kan ikke læses).</summary>
    public PendingAccess? PlainAccess()
    {
        if (Access is not { } a || Unprotect(a.Secret) is not { } secret || Unprotect(a.PrivateKey) is not { } priv) return null;
        return new PendingAccess
        {
            Id = a.Id, Code = a.Code, Pin = a.Pin, PinNeeded = a.PinNeeded, Name = a.Name, Created = a.Created, Expires = a.Expires, Secret = secret, PrivateKey = priv,
        };
    }
    private static string Protect(string plain) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.LocalMachine));
    private static string? Unprotect(string? t)
    {
        if (t is not { Length: > 0 }) return null;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(t), null, DataProtectionScope.LocalMachine)); }
        catch (Exception e) when (e is CryptographicException or FormatException) { return null; }
    }
    public void SetToken(string plain) =>
        Token = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.LocalMachine));
}

public static class AgentJson
{
    public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>HTTP-klient mod Fleetdeck-serveren.</summary>
public sealed class ServerClient(HttpClient http, AgentConfig cfg)
{
    private string Url(string path) => cfg.Server.TrimEnd('/') + path;
    private HttpRequestMessage Req(HttpMethod m, string path, object? body = null)
    {
        var r = new HttpRequestMessage(m, Url(path));
        r.Headers.Authorization = new("Bearer", cfg.PlainToken());
        if (body is not null) r.Content = JsonContent.Create(body, body.GetType(), options: AgentJson.Web);
        return r;
    }

    public async Task<T> Post<T>(string path, object body, CancellationToken ct = default)
    {
        using var res = await http.SendAsync(Req(HttpMethod.Post, path, body), ct);
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<T>(AgentJson.Web, ct))!;
    }
    public async Task<T> Get<T>(string path, CancellationToken ct = default)
    {
        using var res = await http.SendAsync(Req(HttpMethod.Get, path), ct);
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<T>(AgentJson.Web, ct))!;
    }
    public async Task Post(string path, object body, CancellationToken ct = default)
    {
        using var res = await http.SendAsync(Req(HttpMethod.Post, path, body), ct);
        res.EnsureSuccessStatusCode();
    }
    /// <summary>Store filer (iPad-backup i dele på op til 1,5 GB): streamet og uden den almindelige tidsgrænse på 10 minutter.</summary>
    public async Task PutLargeFile(string path, string file, CancellationToken ct = default)
    {
        await using var fs = File.OpenRead(file);
        using var req = Req(HttpMethod.Put, path);
        req.Content = new StreamContent(fs, 1 << 20);
        req.Content.Headers.ContentType = new("application/octet-stream");
        req.Content.Headers.ContentLength = fs.Length;
        using var res = await Large.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
    }
    private static readonly HttpClient Large = new() { Timeout = Timeout.InfiniteTimeSpan };
    /// <summary>bios_settings_apply: byt jobbets engangsbillet til BIOS-adgangskoden (kun i hukommelsen).</summary>
    public async Task<BiosSecretResponse?> BiosSecret(BiosSecretRequest r, CancellationToken ct) => await Post<BiosSecretResponse>("/api/agent/bios/secret", r, ct);
    /// <summary>bios_update: kopien før opdateringen, meldinger (også efter genstart) og pakken til netop denne kørsel.</summary>
    public BiosUpdateIo BiosUpdateIo => new(
        async (r, ct) => await Post<BiosUpdatePostResponse>("/api/agent/bios/update", r, ct),
        async (path, file, ct) => await Download(path, file, ct));
    /// <summary>Det, iPad-jobs (iPad via USB) skal bruge fra serveren.</summary>
    public UsbIpadIo UsbIo => new(
        (r, ct) => Post<UsbIpadSecretResponse>("/api/agent/usb-ipad/secret", r, ct),
        PutLargeFile,
        async (path, file, ct) => await Download(path, file, ct),
        (path, body, ct) => Post(path, body, ct));

    /// <summary>Hent en fil; svaret giver serverens kontrolsum og signatur (til agentfiler).</summary>
    public async Task<(string? Hash, string? Signature)> Download(string path, string file, CancellationToken ct = default)
    {
        using var res = await http.SendAsync(Req(HttpMethod.Get, path), HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();
        await using (var fs = File.Create(file)) await res.Content.CopyToAsync(fs, ct);
        return (Header(res, AgentSigning.HashHeader), Header(res, AgentSigning.SignatureHeader));
    }
    private static string? Header(HttpResponseMessage r, string name) => r.Headers.TryGetValues(name, out var v) ? v.FirstOrDefault() : null;
    public async Task<string> GetText(string path, CancellationToken ct = default)
    {
        using var res = await http.SendAsync(Req(HttpMethod.Get, path), ct);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsStringAsync(ct);
    }

    public static async Task<EnrollResponse> Enroll(HttpClient http, string server, EnrollRequest req, CancellationToken ct = default)
    {
        using var res = await http.PostAsJsonAsync(server.TrimEnd('/') + "/api/agent/enroll", req, AgentJson.Web, ct);
        if (res.StatusCode == System.Net.HttpStatusCode.Forbidden) throw new UnauthorizedAccessException("Forkert tilmeldingsnøgle");
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<EnrollResponse>(AgentJson.Web, ct))!;
    }
}

public sealed record RunResult(int? ExitCode, string Output, bool NoUser = false, bool TimedOut = false, bool Cancelled = false, string? Stopped = null);

/// <summary>Kører programmer og PowerShell som SYSTEM eller i den indloggede brugers session, med opsamlet output.</summary>
public sealed class Runner(AgentOptions opt, ILogger<Runner> log)
{
    /// <param name="watch">Installationsprogram: vagten stopper det, hvis det viser et vindue og venter på et klik.</param>
    public async Task<RunResult> Run(string exe, string args, string runAs, TimeSpan timeout, string? workDir = null, CancellationToken ct = default, bool watch = false)
    {
        // Aldrig med SYSTEM- eller administratorrettigheder fra en placering, som almindelige brugere kan ændre
        if (runAs != RunAs.User && Refused(exe) is { } refused) return refused;
        if (runAs == RunAs.System || !AgentOptions.IsSystem)
        {
            if (runAs != RunAs.System && Native.ConsoleUser() is null) return new RunResult(null, "", NoUser: true);
            return await RunDirect(exe, args, timeout, workDir, ct, watch);
        }
        // I brugerens session: output via fil (userwork\out, hvor brugere kun kan oprette filer), da processen kører i en anden session
        var outDir = Path.Combine(opt.UserWork, "out");
        if (!Directory.Exists(outDir)) Hardening.EnsureDropboxes(opt, m => log.LogWarning("{Msg}", m));
        var logFile = Path.Combine(outDir, $"{Guid.NewGuid():N}.log");
        try
        {
            var cmd = $"cmd.exe /d /c \"\"{exe}\" {args} > \"{logFile}\" 2>&1\"";
            var job = LiveJob.Current;
            using var tailStop = new CancellationTokenSource();
            var tail = job == 0 ? Task.CompletedTask : TailLog(logFile, job, tailStop.Token);
            (int ExitCode, bool TimedOut)? r;
            try { r = await Task.Run(() => Native.RunAsConsoleUser(cmd, runAs == RunAs.UserAdmin, timeout, workDir), ct); }
            catch (OperationCanceledException) { return new RunResult(-4, "[Agent] Annulleret fra Fleetdeck.", Cancelled: true); }
            finally { tailStop.Cancel(); await tail; }
            if (r is null) return new RunResult(null, "", NoUser: true);
            return new RunResult(r.Value.ExitCode, Clean(ReadText(logFile)), TimedOut: r.Value.TimedOut);
        }
        finally { TryDelete(logFile); }
    }

    /// <summary>Kører en PowerShell-blok. Output er altid UTF-8, så æøå bevares.</summary>
    /// <remarks>
    /// Scriptet skrives altid i den beskyttede work-mappe (kun SYSTEM og administratorer). Køres det i brugerens session, ligger
    /// det i en ny undermappe, som kun den bruger må læse – ingen bruger kan ændre det, før det køres.
    /// </remarks>
    public async Task<RunResult> PowerShell(string body, string runAs, TimeSpan timeout, CancellationToken ct = default)
    {
        var forUser = runAs != RunAs.System && AgentOptions.IsSystem;
        var dir = forUser ? Hardening.NewRunDir(opt, forUser: true) : opt.Work;
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"{Guid.NewGuid():N}.ps1");
        var pre = "[Console]::OutputEncoding = [Text.Encoding]::UTF8\r\n$ProgressPreference = 'SilentlyContinue'\r\n";
        try
        {
            await using (var fs = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await using (var w = new StreamWriter(fs, new UTF8Encoding(true))) await w.WriteAsync(pre + body);
            if (Refused(file) is { } refused) return refused;
            return await Run("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{file}\"", runAs, timeout, dir, ct);
        }
        finally
        {
            TryDelete(file);
            if (forUser) try { WindowsAcl.RemoveNoFollow(dir); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Null, hvis filen må køres med forhøjede rettigheder; ellers et afvist resultat (og en advarsel i loggen).</summary>
    public RunResult? Refused(string file)
    {
        if (!OperatingSystem.IsWindows() || WinPath.Normalize(file) is null) return null;   // "powershell.exe" o.l. findes i System32
        if (ExecGuard.Check(file, Acl) is not { } why) return null;
        log.LogWarning("Afvist: {File}: {Why}", file, why);
        return new RunResult(-5, $"[Agent] Afvist af sikkerhedshensyn: {why}. Filen ligger et sted, hvor almindelige brugere kan ændre den, " +
            "og køres derfor ikke med SYSTEM- eller administratorrettigheder.");
    }
    private static readonly ISecurityReader Acl = OperatingSystem.IsWindows() ? new WindowsAcl() : new NoAcl();
    private sealed class NoAcl : ISecurityReader { public PathSecurity? Read(string path) => null; }

    private async Task<RunResult> RunDirect(string exe, string args, TimeSpan timeout, string? workDir, CancellationToken ct, bool watch)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workDir ?? Environment.SystemDirectory,
        };
        using var p = new Process { StartInfo = psi };
        var sb = new StringBuilder();
        var job = LiveJob.Current;   // fremdrift til det job, der kører kommandoen
        var lastOut = DateTime.UtcNow;
        p.OutputDataReceived += (_, e) => { if (e.Data is null) return; lastOut = DateTime.UtcNow; if (LiveJob.Line(job, e.Data)) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is null) return; lastOut = DateTime.UtcNow; if (LiveJob.Line(job, e.Data)) lock (sb) sb.AppendLine(e.Data); };
        p.Start();
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        // Vagten kører for alt, agenten starter som SYSTEM (session 0's skjulte skrivebord): ingen kan se eller klikke på en dialog dér
        var dog = watch || AgentOptions.IsSystem ? new Headless.Watchdog(p, () => lastOut, said =>
        {
            log.LogWarning("{Exe}: {Said}", exe, said);
            lock (sb) sb.AppendLine("[Agent] " + said);
            LiveJob.Line(job, said);
        }) : null;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        Headless.Verdict? verdict = null;
        DateTime? exitedAt = null;
        try
        {
            var exit = p.WaitForExitAsync(cts.Token);
            while (true)
            {
                if (await Task.WhenAny(exit, Task.Delay(10_000, cts.Token)) == exit) { await exit; break; }
                // Processen er færdig, men output-forbindelsen lukker ikke: et program, den startede (fx appen efter en
                // opdatering), har arvet den. Vent ikke på det – ellers hænger jobbet, så længe programmet kører.
                if (p.HasExited)
                {
                    exitedAt ??= DateTime.UtcNow;
                    if (DateTime.UtcNow - exitedAt > TimeSpan.FromSeconds(10)) { log.LogInformation("{Exe} er færdig; et program, den startede, kører videre", exe); break; }
                    continue;
                }
                if (dog?.Check() is { } v) { verdict = v; break; }
            }
        }
        catch (OperationCanceledException) { }
        if (!p.HasExited)
        {
            try { p.Kill(entireProcessTree: true); p.WaitForExit(15_000); } catch (InvalidOperationException) { }
            string Out() { lock (sb) return Clean(sb.ToString()); }
            if (verdict is not null)
            {
                log.LogWarning("Installation stoppet af vagten: {Exe}: {Reason}", exe, verdict.Reason);
                return new RunResult(-3, Out() + "\n[Agent] " + verdict.Reason, Stopped: verdict.Reason);
            }
            if (ct.IsCancellationRequested)
            {
                log.LogWarning("Annulleret: {Exe}", exe);
                return new RunResult(-4, Out() + "\n[Agent] Annulleret fra Fleetdeck.", Cancelled: true);
            }
            log.LogWarning("Timeout efter {Timeout}: {Exe}", timeout, exe);
            return new RunResult(-1, Out() + $"\n[Agent] Timeout efter {timeout.TotalMinutes:0} min.", TimedOut: true);
        }
        p.WaitForExit(5000);   // højst 5 sek. på resten af outputtet (aldrig uendeligt)
        string text; lock (sb) text = sb.ToString();
        return new RunResult(p.ExitCode, Clean(text));
    }

    /// <summary>Læser brugersessionens logfil løbende, så jobbets fremdrift kan følges, mens det kører.</summary>
    private static async Task TailLog(string file, int job, CancellationToken ct)
    {
        long pos = 0; var rest = "";
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { return; }
            try
            {
                if (!File.Exists(file)) continue;
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (fs.Length <= pos) continue;
                fs.Seek(pos, SeekOrigin.Begin);
                var buf = new byte[fs.Length - pos]; var n = await fs.ReadAsync(buf, ct); pos += n;
                var lines = (rest + Encoding.UTF8.GetString(buf, 0, n).TrimStart('﻿')).Split('\n');
                rest = lines[^1];
                foreach (var l in lines[..^1]) LiveJob.Line(job, l);
            }
            catch (IOException) { }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Læser output-filen, også hvis et program, kommandoen startede, stadig holder den åben.</summary>
    private static string ReadText(string f)
    {
        try
        {
            if (!File.Exists(f) || File.GetAttributes(f).HasFlag(FileAttributes.ReparsePoint)) return "";   // aldrig gennem en genvej
            using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            return sr.ReadToEnd().TrimStart('\uFEFF');
        }
        catch (IOException) { return ""; }
    }

    /// <summary>
    /// Starter et program for brugeren på skærmen (fx igen efter en opdatering) uden at vente på det,
    /// og uden omdirigeret output, som programmet ellers ville arve og holde åbent.
    /// </summary>
    public bool Launch(string exe)
    {
        if (!AgentOptions.IsSystem || Native.ConsoleUser() is null) return false;
        var r = Native.RunAsConsoleUser($"cmd.exe /d /c start \"\" \"{exe}\"", false, TimeSpan.FromSeconds(30), Path.GetDirectoryName(exe));
        return r is { TimedOut: false };
    }
    private static void TryDelete(string f) { try { File.Delete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    /// <summary>Fjerner winget's fremdriftslinjer og spinnere.</summary>
    public static string Clean(string text)
    {
        var lines = new List<string>();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var s = raw.Split('\r').LastOrDefault(x => x.Trim().Length > 0)?.TrimEnd();
            if (string.IsNullOrEmpty(s)) continue;
            if (s.Trim() is "-" or "\\" or "|" or "/" || s.Contains('█') || s.Contains('▒') || s.StartsWith("::ws ", StringComparison.Ordinal)) continue;
            lines.Add(s);
        }
        return string.Join('\n', lines).Trim();
    }

    public static string Quote(string s) => "'" + s.Replace("'", "''") + "'";
}

/// <summary>Simpel fil-logger (agent.log) – vises også under nyinstallation.</summary>
public sealed class FileLoggerProvider(string file) : ILoggerProvider
{
    private readonly object _lock = new();
    public ILogger CreateLogger(string category) => new FileLogger(this, category);
    public void Dispose() { }

    internal void Write(string line)
    {
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                var fi = new FileInfo(file);
                if (fi.Exists && fi.Length > 5 * 1024 * 1024) File.Move(file, file + ".1", true);
                File.AppendAllText(file, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (IOException) { }
        }
    }

    private sealed class FileLogger(FileLoggerProvider p, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Information && (!category.StartsWith("Microsoft") && !category.StartsWith("System.Net") || level >= LogLevel.Warning);
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> fmt)
        {
            if (!IsEnabled(level)) return;
            p.Write($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {(level >= LogLevel.Warning ? level.ToString().ToUpperInvariant() + " " : "")}{fmt(state, ex)}{(ex is null ? "" : " – " + ex.Message)}");
        }
    }
}
