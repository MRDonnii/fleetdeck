using System.Globalization;
using System.Text;

namespace Fleetdeck.Shared;

/// <summary>
/// Beskedvinduet på maskinen, uafhængigt af platform: overskrift, samtalens linjer og svarknapper – som vinduet i Windows'
/// proceslinje. Bruges af vinduet på Mac (Fleetdeck.app) og af svardialogen på Linux.
/// </summary>
public static class HelpdeskView
{
    /// <summary>Antal linjer fra samtalen, der vises (de seneste).</summary>
    public const int Shown = 12;

    public static string Sender(HelpdeskInbox inbox) => inbox.Lines.LastOrDefault(l => !l.Mine)?.Who is { Length: > 0 } w ? w : "support";
    public static string Title(HelpdeskInbox inbox) => $"Besked fra {Sender(inbox)}";
    public static string Sub(HelpdeskInbox inbox) => $"Svar herunder. Svaret går direkte til {Sender(inbox)}.";
    public const string ReplyHint = "Skriv et svar… (Enter sender, Skift+Enter giver ny linje)";
    public const string SendLabel = "Send svar";
    public const string LogsLabel = "Send logfiler";
    public const string CloseLabel = "Luk";

    /// <summary>Den seneste besked fra support med svarknapper, der ikke er besvaret endnu.</summary>
    public static HelpdeskLine? Ask(HelpdeskInbox inbox) => inbox.Lines.LastOrDefault(l => !l.Mine && l.Buttons.Count > 0 && l.Answered is null);

    /// <summary>Den seneste besked fra support (svar knyttes til den).</summary>
    public static int? LastFromSupport(HelpdeskInbox inbox) => inbox.Lines.LastOrDefault(l => !l.Mine)?.Id;

    /// <summary>Linjen over en besked: "Support · 14:02 · sendes…".</summary>
    public static string Head(HelpdeskLine l, TimeZoneInfo? tz = null)
    {
        var who = l.Mine ? "Dig" : l.Who.Length > 0 ? l.Who : "Support";
        var at = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(l.At, DateTimeKind.Utc), tz ?? TimeZoneInfo.Local);
        return $"{who} · {at.ToString("HH:mm", CultureInfo.InvariantCulture)}{(l.Pending ? " · sendes…" : "")}{(l.Screenshot ? " · skærmbillede" : "")}{(l.Logs ? " · logfiler" : "")}";
    }

    public static string Body(HelpdeskLine l) => l.Text.Length > 0 ? l.Text : l.Logs ? "(logfiler)" : "(skærmbillede)";

    /// <summary>"Du svarede: …" under en besked, hvis brugeren har trykket en af dens knapper.</summary>
    public static string? Answered(HelpdeskLine l) => !l.Mine && l.Answered is int a && a < l.Buttons.Count ? $"Du svarede: {l.Buttons[a].Label}" : null;

    /// <summary>Enter i svarfeltet sender (som i en chat); Skift+Enter giver en ny linje. Samme regel som i Windows.</summary>
    public static bool EnterSends(bool shift, bool ctrl, bool alt) => !alt && (!shift || ctrl);

    public static List<HelpdeskLine> Recent(HelpdeskInbox inbox, int max = Shown) => inbox.Lines.TakeLast(max).ToList();
}

/// <summary>Hvad brugeren gjorde i svarvinduet.</summary>
public sealed record HelpdeskAnswer(string Kind, string Text = "", int? Button = null)
{
    public const string Reply = "reply", Logs = "logs", Closed = "closed", Failed = "failed";
    public static readonly HelpdeskAnswer Close = new(Closed);
}

/// <summary>
/// Svarvinduet på Linux i brugerens grafiske session: yad (alt i ét vindue), zenity (formular med tekstfelt og knapper) eller
/// kdialog (vælg svar, derefter tekstfelt). Ren logik – argumenter og fortolkning af svaret – så den kan testes uden skærm.
/// Al tekst fra samtalen vises som ren tekst (escapes til Pango/HTML).
/// </summary>
public static class LinuxReplyDialog
{
    public enum Tool { None, Yad, Zenity, Kdialog }

    /// <summary>Sekunder, vinduet står åbent uden svar.</summary>
    public const int TimeoutSeconds = 3600;
    private const int Width = 480;

    /// <summary>Det bedste værktøj, der findes (yad &gt; zenity &gt; kdialog).</summary>
    public static Tool Pick(Func<string, bool> has) =>
        has("yad") ? Tool.Yad : has("zenity") ? Tool.Zenity : has("kdialog") ? Tool.Kdialog : Tool.None;

    /// <summary>zenity kan et tekstfelt med flere linjer fra version 4.2 (ellers ét linjefelt).</summary>
    public static bool ZenityMultiline(string version)
    {
        var parts = version.Trim().Split('.');
        return parts.Length >= 2 && int.TryParse(parts[0], out var major) && int.TryParse(parts[1], out var minor) && (major > 4 || major == 4 && minor >= 2);
    }

    public static string EscapeMarkup(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>Samtalen som Pango-markup (zenity/yad): support med fed, egne svar normalt, overskrifterne små og grå.</summary>
    public static string Markup(HelpdeskInbox inbox, TimeZoneInfo? tz = null, int max = 8)
    {
        var sb = new StringBuilder();
        sb.Append("<b><big>").Append(EscapeMarkup(HelpdeskView.Title(inbox))).Append("</big></b>\n").Append(EscapeMarkup(HelpdeskView.Sub(inbox))).Append('\n');
        foreach (var l in HelpdeskView.Recent(inbox, max))
        {
            sb.Append("\n<small><span foreground=\"gray\">").Append(EscapeMarkup(HelpdeskView.Head(l, tz))).Append("</span></small>\n");
            var body = EscapeMarkup(HelpdeskView.Body(l));
            sb.Append(l.Mine ? body : $"<b>{body}</b>").Append('\n');
            if (HelpdeskView.Answered(l) is { } a) sb.Append("<small>").Append(EscapeMarkup(a)).Append("</small>\n");
        }
        if (HelpdeskText.SendState(inbox.Lines, DateTime.UtcNow) is { Length: > 0 } st) sb.Append("\n<small>").Append(EscapeMarkup(st)).Append("</small>");
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>Samtalen som HTML (kdialog viser rig tekst; alt escapes, så det vises som ren tekst).</summary>
    public static string Html(HelpdeskInbox inbox, TimeZoneInfo? tz = null, int max = 8)
    {
        static string H(string s) => EscapeMarkup(s).Replace("\n", "<br>");
        var sb = new StringBuilder("<qt>");
        sb.Append("<b>").Append(H(HelpdeskView.Title(inbox))).Append("</b><br>").Append(H(HelpdeskView.Sub(inbox))).Append("<br>");
        foreach (var l in HelpdeskView.Recent(inbox, max))
        {
            sb.Append("<br><small>").Append(H(HelpdeskView.Head(l, tz))).Append("</small><br>");
            sb.Append(l.Mine ? H(HelpdeskView.Body(l)) : $"<b>{H(HelpdeskView.Body(l))}</b>").Append("<br>");
            if (HelpdeskView.Answered(l) is { } a) sb.Append("<small>").Append(H(a)).Append("</small><br>");
        }
        return sb.Append("</qt>").ToString();
    }

    /// <summary>yad tolker "!" i en knaptekst som ikon/værktøjstip og ":" som knap-id.</summary>
    private static string YadLabel(string s) => s.Replace("!", "").Replace(":", " ");

    /// <summary>Første (og for yad/zenity eneste) vindue. Knapper: svarknapperne, "Send logfiler", "Luk" og "Send svar".</summary>
    public static List<string> Args(Tool tool, HelpdeskInbox inbox, bool multiline = true, TimeZoneInfo? tz = null)
    {
        var buttons = HelpdeskView.Ask(inbox)?.Buttons.Select(b => b.Label).ToList() ?? [];
        switch (tool)
        {
            case Tool.Yad:
            {
                var a = new List<string> { "yad", "--form", "--title=Fleetdeck", $"--width={Width}", "--center", "--on-top", "--window-icon=dialog-information",
                    $"--text={Markup(inbox, tz)}", $"--timeout={TimeoutSeconds}", "--field=" + YadLabel(HelpdeskView.ReplyHint) + ":TXT", "" };
                for (var i = 0; i < buttons.Count; i++) a.Add($"--button={YadLabel(buttons[i])}:{10 + i}");
                a.Add($"--button={HelpdeskView.LogsLabel}:3");
                a.Add($"--button={HelpdeskView.CloseLabel}:1");
                a.Add($"--button={HelpdeskView.SendLabel}:0");
                return a;
            }
            case Tool.Zenity:
            {
                var a = new List<string> { "zenity", "--forms", "--title=Fleetdeck", $"--width={Width}", $"--text={Markup(inbox, tz)}",
                    multiline ? "--add-multiline-entry=Dit svar" : "--add-entry=Dit svar",
                    $"--ok-label={HelpdeskView.SendLabel}", $"--cancel-label={HelpdeskView.CloseLabel}", $"--timeout={TimeoutSeconds}" };
                foreach (var b in buttons) a.Add($"--extra-button={b}");
                a.Add($"--extra-button={HelpdeskView.LogsLabel}");
                return a;
            }
            case Tool.Kdialog when buttons.Count > 0:
            {
                // kdialog har ikke egne knapper i et tekstfelt: vælg først svaret (eller "Skriv et svar…")
                var a = new List<string> { "kdialog", "--title", "Fleetdeck", "--menu", Html(inbox, tz) };
                for (var i = 0; i < buttons.Count; i++) { a.Add($"b{i}"); a.Add(buttons[i]); }
                a.Add("text"); a.Add("Skriv et svar…");
                a.Add("logs"); a.Add(HelpdeskView.LogsLabel);
                return a;
            }
            case Tool.Kdialog:
                return TextArgs(inbox, tz);
            default:
                return [];
        }
    }

    /// <summary>kdialog: tekstfeltet (efter "Skriv et svar…", eller med det samme, når beskeden ikke har knapper).</summary>
    public static List<string> TextArgs(HelpdeskInbox inbox, TimeZoneInfo? tz = null) =>
        ["kdialog", "--title", "Fleetdeck", "--textinputbox", Html(inbox, tz), ""];

    /// <summary>
    /// Svaret fra dialogen. <paramref name="kdialogMenu"/>: svaret kom fra kdialogs valgmenu (Kind "text" betyder: vis tekstfeltet).
    /// </summary>
    public static HelpdeskAnswer Parse(Tool tool, int exit, string stdout, HelpdeskInbox inbox, bool kdialogMenu = false)
    {
        var buttons = HelpdeskView.Ask(inbox)?.Buttons.Select(b => b.Label).ToList() ?? [];
        var output = stdout.EndsWith('\n') ? stdout[..^1] : stdout;
        switch (tool)
        {
            case Tool.Yad:
            {
                if (exit is 1 or 70 or 252) return HelpdeskAnswer.Close;   // Luk, tiden løb ud, vinduet lukket
                // én formularlinje: "tekst|" – linjeskift i tekstfeltet kommer som \n
                var text = output.EndsWith('|') ? output[..^1] : output;
                text = text.Replace("\\n", "\n").Replace("\\t", "\t");
                if (exit == 0) return Text(text);
                if (exit == 3) return new HelpdeskAnswer(HelpdeskAnswer.Logs, text);
                if (exit >= 10 && exit - 10 < buttons.Count) return new HelpdeskAnswer(HelpdeskAnswer.Reply, text, exit - 10);
                return new HelpdeskAnswer(HelpdeskAnswer.Failed);
            }
            case Tool.Zenity:
            {
                if (exit == 0) return Text(output);
                if (exit == 1 && output.Trim() is { Length: > 0 } label)
                {
                    // en ekstra knap: zenity skriver knappens tekst (det skrevne går tabt – svaret er knappen)
                    var i = buttons.IndexOf(label);
                    if (i >= 0) return new HelpdeskAnswer(HelpdeskAnswer.Reply, "", i);
                    if (label == HelpdeskView.LogsLabel) return new HelpdeskAnswer(HelpdeskAnswer.Logs);
                    return new HelpdeskAnswer(HelpdeskAnswer.Failed);
                }
                return exit is 1 or 5 or -1 ? HelpdeskAnswer.Close : new HelpdeskAnswer(HelpdeskAnswer.Failed);
            }
            case Tool.Kdialog when kdialogMenu:
            {
                if (exit != 0) return HelpdeskAnswer.Close;
                var key = output.Trim();
                if (key == "text") return new HelpdeskAnswer("text");
                if (key == "logs") return new HelpdeskAnswer(HelpdeskAnswer.Logs);
                if (key.StartsWith('b') && int.TryParse(key[1..], out var i) && i >= 0 && i < buttons.Count) return new HelpdeskAnswer(HelpdeskAnswer.Reply, "", i);
                return new HelpdeskAnswer(HelpdeskAnswer.Failed);
            }
            case Tool.Kdialog:
                return exit == 0 ? Text(output) : HelpdeskAnswer.Close;
            default:
                return new HelpdeskAnswer(HelpdeskAnswer.Failed);
        }
    }

    /// <summary>"Lukket" så hurtigt, at vinduet ikke kan have været vist (fx "Failed to open display", der også giver kode 1).</summary>
    public static bool NeverShown(TimeSpan took) => took < TimeSpan.FromSeconds(1);

    private static HelpdeskAnswer Text(string text) =>
        string.IsNullOrWhiteSpace(text) ? HelpdeskAnswer.Close : new HelpdeskAnswer(HelpdeskAnswer.Reply, text.TrimEnd('\n'));
}
