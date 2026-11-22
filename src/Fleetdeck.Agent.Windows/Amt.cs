using System.Diagnostics;
using System.Security.Cryptography;
using System.ServiceProcess;
using System.Text.Json;
using Fleetdeck.Shared;

namespace Fleetdeck.Agent.Windows;

/// <summary>
/// Intel AMT (vPro) via Intels open source-værktøj rpc-go (device-management-toolkit). Værktøjet hentes kun på maskiner med AMT,
/// i en fastlåst version, og kontrolsummen tjekkes, før det bruges.
/// </summary>
internal static class Amt
{
    private const string Version = "v2.55.0";
    private const string Url = "https://github.com/device-management-toolkit/rpc-go/releases/download/" + Version + "/rpc_windows_x64.exe";
    private const string Sha256 = "4b00db59944cdb4f9fba7c3d9435d66c5394ed6de63a2f4146cd5375368b4d6b";
    private static AmtInfo? _cache;
    private static readonly SemaphoreSlim Gate = new(1);

    private static string Tool => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Fleetdeck", "tools", $"rpc-{Version}.exe");

    /// <summary>Har maskinen Intel AMT? (LMS-tjenesten findes kun, når AMT er en del af firmwaren.)</summary>
    public static bool Present()
    {
        try { using var s = new ServiceController("LMS"); _ = s.Status; return true; }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>Status til helbredsrapporten – højst hver 6. time, eller når forceRefresh (efter aktivering).</summary>
    public static async Task<AmtInfo?> Info(bool forceRefresh = false, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows() || !Present()) return null;
        if (!forceRefresh && _cache is { } c && DateTime.UtcNow - c.Checked < TimeSpan.FromHours(6)) return c;
        var (code, output) = await Run(["amtinfo", "-json"], TimeSpan.FromMinutes(2), ct);
        var info = new AmtInfo { Present = true };
        if (code != 0 && !output.TrimStart().StartsWith('{')) info.Error = Last(output);
        else Parse(output, info);
        _cache = info;
        return info;
    }

    /// <summary>Aktivér AMT i klient-tilstand (CCM) med den adgangskode, Fleetdeck har lavet til maskinen.</summary>
    public static async Task<(bool Ok, string Output)> Activate(string password, CancellationToken ct)
    {
        var (code, output) = await Run(["activate", "-local", "-ccm", "-password", password, "-json"], TimeSpan.FromMinutes(5), ct);
        _cache = null;
        var ok = code == 0;
        // KVM, SOL og IDE-R slås til, så fjernskærm via AMT kan bruges senere (i CCM kræver det altid en kode på skærmen)
        if (ok) await Run(["configure", "amtfeatures", "-kvm", "-sol", "-ider", "-password", password, "-json"], TimeSpan.FromMinutes(2), ct);
        return (ok, Clean(output, password));
    }

    public static async Task<(bool Ok, string Output)> Deactivate(string password, CancellationToken ct)
    {
        var (code, output) = await Run(["deactivate", "-local", "-password", password, "-json"], TimeSpan.FromMinutes(5), ct);
        _cache = null;
        return (code == 0, Clean(output, password));
    }

    private static string Clean(string s, string pw)
    {
        var t = (pw.Length > 0 ? s.Replace(pw, "********") : s).Trim();
        return t.Length > 3000 ? t[^3000..] : t;
    }

    private static string Last(string s) => s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";

    private static void Parse(string json, AmtInfo info)
    {
        try
        {
            using var doc = JsonDocument.Parse(json[json.IndexOf('{')..]);
            var flat = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            void Walk(JsonElement e, string prefix)
            {
                switch (e.ValueKind)
                {
                    case JsonValueKind.Object: foreach (var p in e.EnumerateObject()) Walk(p.Value, prefix.Length == 0 ? p.Name : $"{prefix}.{p.Name}"); break;
                    case JsonValueKind.Array: var i = 0; foreach (var x in e.EnumerateArray()) Walk(x, $"{prefix}[{i++}]"); break;
                    default: flat.TryAdd(prefix, e.ToString()); break;
                }
            }
            Walk(doc.RootElement, "");
            string? Find(params string[] keys) => flat.FirstOrDefault(kv => keys.Any(k => kv.Key.EndsWith(k, StringComparison.OrdinalIgnoreCase))).Value;
            info.Version = Find("amt", "version");
            info.Mode = Find("controlMode", "mode");
            info.Uuid = Find("uuid");
            var wired = flat.Where(kv => kv.Key.Contains("wired", StringComparison.OrdinalIgnoreCase) && !kv.Key.Contains("wireless", StringComparison.OrdinalIgnoreCase)).ToList();
            info.AmtIp = wired.FirstOrDefault(kv => kv.Key.EndsWith("ipAddress", StringComparison.OrdinalIgnoreCase) && !kv.Key.Contains("os", StringComparison.OrdinalIgnoreCase)).Value;
            info.LinkUp = wired.Any(kv => kv.Key.EndsWith("linkStatus", StringComparison.OrdinalIgnoreCase) && kv.Value.Equals("up", StringComparison.OrdinalIgnoreCase));
        }
        catch (JsonException e) { info.Error = $"Kunne ikke læse AMT-status: {e.Message}"; }
        catch (ArgumentOutOfRangeException) { info.Error = "Kunne ikke læse AMT-status"; }
    }

    /// <summary>Henter værktøjet (én gang) og kører det skjult. Kontrolsummen tjekkes hver gang før brug.</summary>
    private static async Task<(int Code, string Output)> Run(string[] args, TimeSpan timeout, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            if (!await EnsureTool(ct)) return (-1, "Intels AMT-værktøj kunne ikke hentes eller havde forkert kontrolsum");
            var psi = new ProcessStartInfo(Tool) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEndAsync(ct); var e = p.StandardError.ReadToEndAsync(ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(timeout);
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch (InvalidOperationException) { } return (-2, "AMT-værktøjet svarede ikke i tide"); }
            return (p.ExitCode, (await o) + (await e));
        }
        finally { Gate.Release(); }
    }

    private static async Task<bool> EnsureTool(CancellationToken ct)
    {
        if (File.Exists(Tool) && Hash(Tool) == Sha256) return true;
        Directory.CreateDirectory(Path.GetDirectoryName(Tool)!);
        var tmp = Tool + ".download";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            await using (var src = await http.GetStreamAsync(Url, ct))
            await using (var dst = File.Create(tmp)) await src.CopyToAsync(dst, ct);
            if (Hash(tmp) != Sha256) { File.Delete(tmp); return false; }
            File.Move(tmp, Tool, true);
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException or UnauthorizedAccessException) { return false; }
    }

    private static string Hash(string path) { using var f = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(f)); }
}
