using System.Text;
using System.Text.RegularExpressions;

namespace Fleetdeck.Shared;

/// <summary>
/// "Gem diagnose…" i Fleetdeck.app (Installeret → Oversigt): én tekstfil, ejeren kan sende, når ikonet eller tjenesten driller på en Mac.
/// Samler det, et diagnosejob ellers skulle hente: appens og menuens version, om ikonet kører, launchctl-status for menuen og agenten,
/// tjenestens status og de sidste linjer af agentens log. Uden hemmeligheder: nøgler, tokens, adgangskoder og lange tilfældige
/// strenge skjules (<see cref="Redact"/>), og fra status.json tages kun udvalgte felter.
/// </summary>
public static partial class MacDiagnose
{
    /// <summary>Så mange linjer af agentens log (de nyeste) og af hver launchctl-udskrift.</summary>
    public const int LogLines = 300, LaunchctlLines = 80;
    public const string Hidden = "[skjult]";

    public static string FileName(DateTime localNow) => $"Fleetdeck-diagnose-{localNow:yyyy-MM-dd-HHmm}.txt";

    /// <summary>launchctl-argumenterne for menuen hos brugeren og for tjenesten (kan læses uden administrator).</summary>
    public static string[] MenuArgs(int uid) => ["print", $"gui/{uid}/{MacPaths.MenuBarLabel}"];
    public static readonly string[] AgentArgs = ["print", $"system/{MacPaths.AgentLabel}"];

    public sealed record Input
    {
        public DateTime At { get; init; }
        public string AppVersion { get; init; } = "";
        public string? Os { get; init; }
        public string? Arch { get; init; }
        public string User { get; init; } = "";
        public int Uid { get; init; }
        public bool AgentInstalled { get; init; }
        public bool LoginItem { get; init; }
        public bool NativeMenu { get; init; }
        public bool NativeMenuProcess { get; init; }
        public bool AutostartAtLogin { get; init; } = true;
        public TrayStatus? Status { get; init; }
        public string? StatusError { get; init; }
        public (int Code, string Output) MenuLaunchctl { get; init; }
        public (int Code, string Output) AgentLaunchctl { get; init; }
        public string? AgentLog { get; init; }
        public string? AgentLogError { get; init; }
    }

    /// <summary>Kører ikonet? Menuens launchd-job kører hos brugeren, eller FleetdeckMenu-processen findes.</summary>
    public static bool IconRunning(Input i) => i.NativeMenuProcess || (i.MenuLaunchctl.Code == 0 && MacMenuLaunch.Running(i.MenuLaunchctl.Output));

    public static string Build(Input i)
    {
        var sb = new StringBuilder();
        void Line(string k, object? v) => sb.Append(k.PadRight(26)).Append(": ").AppendLine(v?.ToString() is { Length: > 0 } s ? s : "–");
        static string YesNo(bool b) => b ? "ja" : "nej";
        sb.AppendLine("Fleetdeck – diagnose fra Mac'en");
        sb.AppendLine($"Lavet {i.At:yyyy-MM-dd HH:mm:ss} UTC. Uden nøgler, tokens og adgangskoder (de er erstattet med {Hidden}).");
        sb.AppendLine();
        sb.AppendLine("== Mac og app ==");
        Line("Fleetdeck.app", i.AppVersion);
        Line("macOS", i.Os);
        Line("Processor", i.Arch);
        Line("Bruger", $"{i.User} (uid {i.Uid})");
        Line("Tjenesten installeret", YesNo(i.AgentInstalled));
        Line("Ikon ved login (alle)", YesNo(i.LoginItem) + (i.LoginItem ? "" : $" – {MacPaths.MenuBarPlist} mangler"));
        Line("Vis ved login (brugeren)", i.AutostartAtLogin ? "til" : "fra (ikonet starter ikke ved login)");
        Line("Native menu", i.NativeMenu ? $"ja ({MacMenuStamp.Marker})" : "nej – appen viser selv ikonet");
        var running = IconRunning(i);
        Line("Ikonet kører", running ? "ja" : "nej");
        if (!running) Line("  Hvorfor (launchctl)", MenuState(i.MenuLaunchctl));
        sb.AppendLine();
        sb.AppendLine("== Tjenestens status (status.json) ==");
        if (i.Status is { } s)
        {
            Line("Maskine", s.Machine);
            Line("Server", s.Server);
            Line("Forbundet", YesNo(s.Connected));
            Line("Tilmeldt", YesNo(s.Enrolled));
            Line("Sidste kontakt", s.LastContact?.ToString("yyyy-MM-dd HH:mm:ss 'UTC'"));
            Line("Agentversion", s.Version);
            Line("Profil", s.Profile);
            Line("Fejl", s.Error);
            Line("Helbred", s.Health);
            Line("Sikkerhed", s.Security);
            if (s.Job is { } j) Line("Job i gang", $"{j.Title}{(j.Percent is int pc ? $" – {pc} %" : "")}{(j.Step is { Length: > 0 } st ? $" · {st}" : "")}");
            if (s.PausedUntil is { } pu) Line("På pause til", pu.ToString("yyyy-MM-dd HH:mm 'UTC'"));
            if (s.AccessRequest is { } ar) Line("Anmodning om adgang", ar.State);
        }
        else Line("status.json", i.StatusError ?? "findes ikke (tjenesten har ikke skrevet status endnu)");
        sb.AppendLine();
        Section(sb, $"launchctl print gui/{i.Uid}/{MacPaths.MenuBarLabel}", i.MenuLaunchctl);
        Section(sb, $"launchctl print system/{MacPaths.AgentLabel}", i.AgentLaunchctl);
        sb.AppendLine($"== Agentens log ({MacPaths.Log}, de sidste {LogLines} linjer) ==");
        sb.AppendLine(i.AgentLog is { } log ? Tail(log, LogLines) : i.AgentLogError ?? "kunne ikke læses");
        return Redact(sb.ToString());
    }

    /// <summary>Kort forklaring ud fra launchctl print: ikke indlæst, sidst afsluttet med kode N, eller tilstanden.</summary>
    public static string MenuState((int Code, string Output) p)
    {
        if (p.Code != 0) return "ikke indlæst for brugeren (slået fra under Loginemner, eller tjenesten har ikke startet det endnu)";
        var exit = LastExitRx().Match(p.Output) is { Success: true } m ? m.Groups[1].Value.Trim() : null;
        var state = StateRx().Match(p.Output) is { Success: true } s ? s.Groups[1].Value.Trim() : "ukendt";
        return exit == "0" ? $"{state} – lukket af brugeren eller \"Vis ved login\" er slået fra (kode 0)"
            : exit is not null ? $"{state} – sidst afsluttet med {exit}" : state;
    }

    private static void Section(StringBuilder sb, string title, (int Code, string Output) p)
    {
        sb.AppendLine($"== {title} (kode {p.Code}) ==");
        sb.AppendLine(Head(p.Output.Trim(), LaunchctlLines));
        sb.AppendLine();
    }

    /// <summary>De sidste <paramref name="n"/> linjer.</summary>
    public static string Tail(string text, int n)
    {
        var lines = text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        return lines.Length <= n ? string.Join('\n', lines) : $"… ({lines.Length - n} ældre linjer udeladt)\n" + string.Join('\n', lines[^n..]);
    }

    private static string Head(string text, int n)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        return lines.Length <= n ? text : string.Join('\n', lines[..n]) + $"\n… ({lines.Length - n} linjer mere udeladt)";
    }

    /// <summary>
    /// Skjul hemmeligheder: værdier efter key/token/secret/password/pin (som key=…, "token": "…", --key …), nøgler i adresser
    /// (?key=, #k=), "Bearer …", lange tilfældige strenge (mindst 32 tegn med både bogstaver og tal) og nøgler/tokens uden navn foran
    /// (mindst 20 tegn med store og små bogstaver og tal, som base64url).
    /// </summary>
    public static string Redact(string text)
    {
        text = BearerRx().Replace(text, m => m.Groups[1].Value + Hidden);
        text = UrlKeyRx().Replace(text, m => m.Groups[1].Value + Hidden);
        text = CliRx().Replace(text, m => m.Groups[1].Value + Hidden);
        text = NamedRx().Replace(text, m => m.Groups[1].Value + (m.Groups[2].Success ? $"\"{Hidden}\"" : Hidden));
        text = LongRx().Replace(text, Hidden);
        return TokenRx().Replace(text, Hidden);
    }

    [GeneratedRegex(@"(\bBearer\s+)\S+", RegexOptions.IgnoreCase)] private static partial Regex BearerRx();
    [GeneratedRegex(@"([?&#](?:key|k|token|secret)=)[^&\s""']+", RegexOptions.IgnoreCase)] private static partial Regex UrlKeyRx();
    [GeneratedRegex(@"(--(?:key|token|secret|password|tls-pin)\s+)(?:""[^""]*""|'[^']*'|\S+)", RegexOptions.IgnoreCase)] private static partial Regex CliRx();
    // navn (evt. i anførselstegn) = eller : værdi (evt. i anførselstegn); navnet må have mere omkring sig (enroll_key, SigningKey, jobKey …)
    // "pin" kun som eget ord eller ordled (pin, tls-pin, tls_pin, tlsPin/TlsPin) – ikke inde i almindelige ord som "mapping" og "spinning"
    [GeneratedRegex(@"(""?\b(?:[\w-]*(?:key|token|secret|password|passwd)[\w-]*|(?:[\w]*[-_])?pin|[A-Za-z]*(?-i:Pin))""?\s*[:=]\s*)(?!\[skjult\])(?:""([^""]*)""|[^\s,;}&""]+)", RegexOptions.IgnoreCase)]
    private static partial Regex NamedRx();
    // tilmeldingsnøgler og tokens uden navn foran (Crypto.Token(18) = 24 tegn base64url): 20+ tegn med store og små bogstaver og tal
    [GeneratedRegex(@"(?<![\w+/-])(?=[A-Za-z0-9+_-]*\d)(?=[A-Za-z0-9+_-]*[a-z])(?=[A-Za-z0-9+_-]*[A-Z])[A-Za-z0-9+_-]{20,}={0,2}(?![\w+/-])")] private static partial Regex TokenRx();
    [GeneratedRegex(@"(?<![\w+/-])(?=[A-Za-z0-9+_-]*\d)(?=[A-Za-z0-9+_-]*[A-Za-z])[A-Za-z0-9+_-]{32,}={0,2}")] private static partial Regex LongRx();
    [GeneratedRegex(@"^\s*state\s*=\s*(.+)$", RegexOptions.Multiline)] private static partial Regex StateRx();
    [GeneratedRegex(@"^\s*last exit code\s*=\s*(.+)$", RegexOptions.Multiline)] private static partial Regex LastExitRx();
}
