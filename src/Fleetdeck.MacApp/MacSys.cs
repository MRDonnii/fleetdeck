using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Fleetdeck.Shared;

namespace Fleetdeck.MacApp;

/// <summary>Alt, appen gør mod macOS og tjenesten: læs status, send ordrer, osascript (beskeder og administrator), hent agenten.</summary>
public static class MacSys
{
    public static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>Agenten er installeret (launchd-plisten kan alle læse; /etc/fleetdeck kun root).</summary>
    public static bool Installed => File.Exists(MacPaths.DaemonPlist);

    public static TrayStatus? ReadStatus()
    {
        try { return File.Exists(MacPaths.StatusFile) ? JsonSerializer.Deserialize<TrayStatus>(File.ReadAllText(MacPaths.StatusFile), TrayCommands.Json) : null; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Den .app, vi kører fra (…/Fleetdeck.app/Contents/MacOS/Fleetdeck) – null, hvis vi ikke kører fra en pakke (fx under udvikling).</summary>
    public static string? AppBundle
    {
        get
        {
            var exe = Environment.ProcessPath ?? "";
            var macos = Path.GetDirectoryName(exe);
            var contents = macos is null ? null : Path.GetDirectoryName(macos);
            var app = contents is null ? null : Path.GetDirectoryName(contents);
            return app is not null && app.EndsWith(".app", StringComparison.OrdinalIgnoreCase) ? app : null;
        }
    }

    public static string Version => typeof(MacSys).Assembly.GetName().Version?.ToString(3) ?? "";

    public static bool IsAdmin
    {
        get
        {
            var (code, outp) = Run("/usr/bin/id", ["-Gn"]).GetAwaiter().GetResult();
            return code == 0 && outp.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains("admin");
        }
    }

    public static async Task<(int Code, string Output)> Run(string file, IEnumerable<string> args, TimeSpan? timeout = null)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(5));
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch (InvalidOperationException) { } return (-1, "Tiden løb ud"); }
            return (p.ExitCode, (await o) + (await e));
        }
        catch (System.ComponentModel.Win32Exception e) { return (-1, e.Message); }
    }

    /// <summary>Kør et shell-script som root: macOS spørger efter en administrators adgangskode. Returnerer scriptets egen exit-kode.</summary>
    public static async Task<(int Code, string Output)> Elevated(string script, string? prompt = null)
    {
        var (code, outp) = await Run("/usr/bin/osascript", MacInstall.Elevated(script, prompt), TimeSpan.FromMinutes(10));
        // exit-koden læses af den rå tekst ("… (3)"); brugeren ser kun scriptets egen besked (uden "0:767: execution error:" og "(3)")
        return (MacInstall.ScriptExitCode(code, outp), MacInstall.CleanOsaOutput(outp));
    }

    public static void Open(string target) => _ = Run("/usr/bin/open", [target]);
    public static void Reveal(string path) => _ = Run("/usr/bin/open", ["-R", path]);

    /// <summary>Besked øverst til højre (Notifikationscenter).</summary>
    public static void Notify(string text, string? subtitle = null)
    {
        if (!Settings.Current.Notifications) return;
        var script = $"display notification {MacInstall.AppleScript(text)} with title \"Fleetdeck\"" + (subtitle is { Length: > 0 } ? $" subtitle {MacInstall.AppleScript(subtitle)}" : "");
        _ = Run("/usr/bin/osascript", ["-e", script]);
    }

    /// <summary>En ordre, alle brugere må give (opdatér alt, synkronisér, udsæt, tjek adgang, svar på beskeder).
    /// Skrives som .tmp og omdøbes, så tjenesten aldrig læser en halv ordre.</summary>
    public static bool UserCommand(TrayCommand c)
    {
        var name = Path.Combine(MacPaths.UserCmdDir, $"{Guid.NewGuid():N}");
        try { File.WriteAllText(name + ".tmp", TrayCommands.Serialize(c)); File.Move(name + ".tmp", name + ".json"); return true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { try { File.Delete(name + ".tmp"); } catch (Exception) { } return false; }
    }

    /// <summary>Beskeder: samtalen, som tjenesten skriver (null, når der ikke er nogen endnu).</summary>
    public static HelpdeskInbox? ReadHelpdesk()
    {
        try { return File.Exists(MacPaths.HelpdeskFile) ? JsonSerializer.Deserialize<HelpdeskInbox>(File.ReadAllText(MacPaths.HelpdeskFile), TrayCommands.Json) : null; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>En administrator-ordre (pause, udvidelser): agenten lægger den selv i roots mappe – med adgangskoden.</summary>
    public static async Task<bool> AdminCommand(TrayCommand c, string prompt) =>
        (await Elevated(MacInstall.AdminCommandScript(c), prompt)).Code == 0;

    /// <summary>
    /// Hent agenten fra serveren og tjek den, som agenten selv tjekker opdateringer: kontrolsum og signatur med serverens nøgle,
    /// og at det er en Fleetdeck-agent. Returnerer filen (i brugerens egen midlertidige mappe) og dens fulde SHA-256.
    /// </summary>
    public static async Task<(string File, string Sha256)> FetchAgent(string server, CancellationToken ct = default)
    {
        var arch = MacInstall.ThisArch;
        var pem = (await Http.GetStringAsync($"{server}/api/agent/signing-key", ct)).Trim();
        var dir = Directory.CreateTempSubdirectory("fleetdeck-").FullName;
        var file = Path.Combine(dir, "fleetdeck-agent");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        using var res = await http.GetAsync($"{server}/api/agent/download?platform={Platforms.Mac}&arch={arch}", HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"Serveren har ikke Mac-agenten ({(int)res.StatusCode}). Byg serverens Docker-billede igen.");
        await using (var fs = File.Create(file)) await res.Content.CopyToAsync(fs, ct);
        string? H(string n) => res.Headers.TryGetValues(n, out var v) ? v.FirstOrDefault() : null;
        if (AgentSigning.Check(file, pem, H(AgentSigning.HashHeader), H(AgentSigning.SignatureHeader)) is { Ok: false } bad)
            throw new InvalidOperationException($"Agenten fra serveren blev afvist: {bad.Why}");
        var bytes = await File.ReadAllBytesAsync(file, ct);
        if (bytes.AsSpan().IndexOf("fleetdeck-agent.dll"u8) < 0) throw new InvalidOperationException("Serveren udleverer ikke en Fleetdeck-agent (er serveren opgraderet til Fleetdeck?).");
        return (file, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    public static async Task<List<ProfileOption>> Profiles(string url, string key) =>
        await Http.GetFromJsonAsync<List<ProfileOption>>($"{url}/api/agent/profiles?key={Uri.EscapeDataString(key)}", TrayCommands.Json) ?? [];

    /// <summary>De sidste linjer af agentens log (/var/log/fleetdeck-agent.log kan læses af alle).</summary>
    public static string LogTail(int lines = 60)
    {
        try
        {
            if (!File.Exists(MacPaths.Log)) return "(ingen log endnu)";
            using var fs = new FileStream(MacPaths.Log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var start = Math.Max(0, fs.Length - 64 * 1024);
            fs.Position = start;
            var text = new StreamReader(fs).ReadToEnd();
            return string.Join('\n', text.Split('\n').TakeLast(lines));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return $"(kan ikke læse loggen: {e.Message})"; }
    }
}

/// <summary>Brugerens egne valg (~/Library/Application Support/Fleetdeck/settings.json) – som HKCU\Software\Fleetdeck i Windows.</summary>
public sealed class Settings
{
    public bool Notifications { get; set; } = true;
    public bool Autostart { get; set; } = true;

    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "Fleetdeck");
    private static string File => Path.Combine(Dir, "settings.json");
    private static Settings? _current;
    public static Settings Current => _current ??= Load();

    private static Settings Load()
    {
        try { return System.IO.File.Exists(File) ? JsonSerializer.Deserialize<Settings>(System.IO.File.ReadAllText(File), TrayCommands.Json) ?? new() : new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    public void Save()
    {
        try { Directory.CreateDirectory(Dir); System.IO.File.WriteAllText(File, JsonSerializer.Serialize(this, TrayCommands.Json)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
