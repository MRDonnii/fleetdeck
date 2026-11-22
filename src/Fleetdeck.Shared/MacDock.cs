using System.Globalization;
using System.Text;

namespace Fleetdeck.Shared;

/// <summary>
/// Fleetdeck.app i Dock og Cmd-Tab. Tidligere var appen kun et menulinje-program (LSUIElement + Avalonias ShowInDock=false), så
/// man ikke kunne se, om den var åben – særligt under opsætningen. Nu står den i Dock (og Cmd-Tab, med ikon og navnet "Fleetdeck")
/// hele tiden, den kører; ikonet i menulinjen er der stadig.
/// </summary>
public static class MacDock
{
    /// <summary>NSApplicationActivationPolicy: Regular = Dock-ikon, menulinje og Cmd-Tab; Accessory = kun menulinjen.</summary>
    public const long Regular = 0, Accessory = 1;

    /// <summary>Appen står i Dock, så længe den kører (ejerens ønske) – uanset hvordan den er startet.</summary>
    public static bool ShowInDock(string mode) => true;

    /// <summary>Den aktiveringspolitik, appen skal have.</summary>
    public static long Policy(string mode) => ShowInDock(mode) ? Regular : Accessory;

    public enum DockClick { BringToFront, OpenMain }

    /// <summary>Klik på Dock-ikonet: et åbent vindue hentes frem; ellers åbnes hovedvinduet (opsætningen, eller status når den er sat op).</summary>
    public static DockClick OnDockClick(int visibleWindows) => visibleWindows > 0 ? DockClick.BringToFront : DockClick.OpenMain;
}

/// <summary>
/// ~/Library/Logs/Fleetdeck/app-start.log: én linje pr. start (version, macOS, processor) og hele fejlen, hvis appen ikke kan starte –
/// så en Mac, hvor "intet sker", kan fejlsøges. Holdes lille (de sidste <see cref="MaxBytes"/>).
/// </summary>
public static class MacStartupLog
{
    public const int MaxBytes = 256 * 1024;
    public const string FileName = "app-start.log";

    public static string Dir(string home) => Path.Combine(home, "Library", "Logs", "Fleetdeck");

    public static string StartLine(DateTime at, string appVersion, string macOS, string arch, string runtime, bool legacy, string mode) =>
        $"{Ts(at)} start Fleetdeck {appVersion}{(legacy ? " (legacy)" : "")} · macOS {macOS} · {arch} · .NET {runtime} · {mode}";

    public static string ReadyLine(DateTime at, TimeSpan elapsed) => $"{Ts(at)} klar efter {elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s";

    private static string Ts(DateTime at) => at.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public static string CrashBlock(DateTime at, string where, Exception e)
    {
        var sb = new StringBuilder();
        sb.Append($"{Ts(at)} FEJL ({where}): {e.GetType().FullName}: {e.Message}\n");
        sb.Append(e.ToString().Replace("\r\n", "\n")).Append('\n');
        return sb.ToString();
    }

    /// <summary>Den korte tekst i fejlvinduet: "Fleetdeck kunne ikke starte: …" (første linje af fejlen, højst 300 tegn).</summary>
    public static string AlertText(Exception e)
    {
        var inner = e;
        while (inner is AggregateException or System.Reflection.TargetInvocationException or TypeInitializationException && inner.InnerException is not null) inner = inner.InnerException!;
        var msg = (inner.Message ?? inner.GetType().Name).Split('\n')[0].Trim();
        if (msg.Length > 300) msg = msg[..300] + "…";
        return $"Fleetdeck kunne ikke starte: {msg}";
    }

    /// <summary>Teksten skåret til de sidste <paramref name="max"/> bytes (fra en hel linje), så loggen ikke vokser for evigt.</summary>
    public static string Trim(string log, int max = MaxBytes)
    {
        if (Encoding.UTF8.GetByteCount(log) <= max) return log;
        var tail = log[^Math.Min(log.Length, max / 2)..];
        var nl = tail.IndexOf('\n');
        return nl >= 0 ? tail[(nl + 1)..] : tail;
    }
}
