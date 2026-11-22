using System.Text;

namespace Fleetdeck.Shared;

// Beskeder til maskinen – en lille helpdesk. Serveren lægger nye beskeder i check-in-svaret (signeret), agenten
// viser dem for brugeren og kvitterer (leveret/læst). Brugeren svarer med tekst, en svarknap og evt. et skærmbillede.

/// <summary>Hvad en svarknap gør, når brugeren trykker på den. Kun serveren udfører handlingen – agenten melder bare valget.</summary>
public static class HelpdeskActions
{
    public const string None = "", Reboot = "reboot", Shutdown = "shutdown";
    public static readonly string[] All = [None, Reboot, Shutdown];
}

/// <summary>En svarknap på en besked (fx "OK", "Ja, genstart nu", "Senere").</summary>
public sealed class HelpdeskButton
{
    public string Label { get; set; } = "";
    /// <summary><see cref="HelpdeskActions"/>: tom = kun et svar.</summary>
    public string Action { get; set; } = "";
}

/// <summary>En besked til maskinen (i check-in-svaret).</summary>
public sealed class HelpdeskNote
{
    public int Id { get; set; }
    /// <summary>Afsendernavnet fra indstillingerne (fx "IT-support").</summary>
    public string From { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTime At { get; set; }
    public List<HelpdeskButton> Buttons { get; set; } = [];
}

/// <summary>Agentens kvittering: beskeder, den har modtaget, og beskeder, brugeren har set.</summary>
public sealed class HelpdeskAck
{
    public List<int> Delivered { get; set; } = [];
    public List<int> Read { get; set; } = [];
}

/// <summary>Et svar eller en ny sag fra brugeren ved maskinen.</summary>
public sealed class HelpdeskReply
{
    /// <summary>Beskeden, der svares på (null = brugeren starter selv en sag).</summary>
    public int? ReplyTo { get; set; }
    public string Text { get; set; } = "";
    /// <summary>Den valgte svarknap (indeks i beskedens knapper).</summary>
    public int? Button { get; set; }
    /// <summary>Den indloggede bruger, der skrev.</summary>
    public string? User { get; set; }
    /// <summary>Skærmbillede som base64 (JPEG eller PNG), kun når brugeren selv har trykket.</summary>
    public string? Screenshot { get; set; }
    /// <summary>Logfiler som base64 (zip fra <see cref="LogBundle"/>), kun når brugeren selv har trykket "Send logfiler".</summary>
    public string? Logs { get; set; }
}

/// <summary>Logfiler, admin har bedt om med "Hent logfiler" (jobbet collect_logs). Sendes af agenten til /api/agent/logs.</summary>
public sealed class LogUpload
{
    public int JobId { get; set; }
    /// <summary>Zip fra <see cref="LogBundle"/> som base64.</summary>
    public string Data { get; set; } = "";
}

/// <summary>Ren tekst: ingen styretegn, Unix-linjeskift og en øvre grænse. HTML lades stå som tekst (det vises aldrig som HTML).</summary>
public static class HelpdeskText
{
    public const int MaxText = 4000, MaxLabel = 40, MaxButtons = 4, MaxScreenshotBytes = 4_000_000;

    public static string Clean(string? s, int max = MaxText)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(Math.Min(s.Length, max));
        var t = s.Replace("\r\n", "\n").Replace('\r', '\n');
        foreach (var ch in t)
        {
            if (ch == '\n' || ch == '\t') sb.Append(ch);
            else if (char.IsControl(ch) || ch is '​' or '‎' or '‏' or '‪' or '‫' or '‬' or '‭' or '‮' or '⁦' or '⁧' or '⁨' or '⁩') continue;
            else sb.Append(ch);
        }
        var r = sb.ToString().Trim();
        while (r.Contains("\n\n\n")) r = r.Replace("\n\n\n", "\n\n");
        return r.Length > max ? r[..max].TrimEnd() : r;
    }

    /// <summary>Én linje til oversigter og notifikationer.</summary>
    public static string Preview(string? s, int max = 120)
    {
        var one = string.Join(' ', Clean(s).Split(['\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return one.Length > max ? one[..(max - 1)].TrimEnd() + "…" : one;
    }

    /// <summary>Knapperne, som de må være: højst 4, korte, uden dubletter og kun kendte handlinger.</summary>
    public static List<HelpdeskButton> Buttons(IEnumerable<HelpdeskButton>? list)
    {
        var res = new List<HelpdeskButton>();
        foreach (var b in list ?? [])
        {
            var label = Preview(b?.Label, MaxLabel);
            if (label.Length == 0 || res.Any(x => string.Equals(x.Label, label, StringComparison.OrdinalIgnoreCase))) continue;
            res.Add(new HelpdeskButton { Label = label, Action = HelpdeskActions.All.Contains(b!.Action ?? "") ? b.Action ?? "" : HelpdeskActions.None });
            if (res.Count == MaxButtons) break;
        }
        return res;
    }

    /// <summary>
    /// Linjen under svarfeltet på maskinen: om brugerens seneste svar er på vej, sendt eller venter på forbindelsen.
    /// Tom, når der ikke er noget at sige.
    /// </summary>
    public static string SendState(IReadOnlyList<HelpdeskLine> lines, DateTime nowUtc)
    {
        var last = lines.LastOrDefault(l => l.Mine);
        if (last is null) return "";
        var age = nowUtc - last.At;
        if (last.Pending)
            return age < TimeSpan.FromSeconds(45) ? (last.Logs ? "Samler og sender logfiler…" : "Sendes…")
                : "Ikke sendt endnu – det sendes, så snart pc'en har forbindelse til Fleetdeck.";
        return age < TimeSpan.FromHours(12) ? $"Sendt kl. {last.At.ToLocalTime():HH:mm}{(last.Logs ? " (med logfiler)" : "")}." : "";
    }

    /// <summary>Et skærmbillede: JPEG eller PNG og ikke for stort. Null = ikke et billede.</summary>
    public static string? ImageType(ReadOnlySpan<byte> b)
    {
        if (b.Length < 8 || b.Length > MaxScreenshotBytes) return null;
        if (b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "image/jpeg";
        if (b[0] == 0x89 && b[1] == (byte)'P' && b[2] == (byte)'N' && b[3] == (byte)'G') return "image/png";
        return null;
    }
}

/// <summary>Samtalen på maskinen (agenten skriver den i userwork\helpdesk.json, ikonet viser den). Højst de seneste 40 linjer.</summary>
public sealed class HelpdeskInbox
{
    public List<HelpdeskLine> Lines { get; set; } = [];
    public DateTime Written { get; set; }

    public const int Keep = 40;

    /// <summary>Nye beskeder fra serveren lægges ind (samme id kun én gang). Giver de id'er, der er nye.</summary>
    public List<int> Merge(IEnumerable<HelpdeskNote> notes)
    {
        var added = new List<int>();
        foreach (var n in notes.OrderBy(n => n.At).ThenBy(n => n.Id))
        {
            if (n.Id <= 0 || Lines.Any(l => !l.Mine && l.Id == n.Id)) continue;
            Lines.Add(new HelpdeskLine { Id = n.Id, Who = n.From, Text = HelpdeskText.Clean(n.Text), At = n.At, Buttons = HelpdeskText.Buttons(n.Buttons) });
            added.Add(n.Id);
        }
        Trim();
        return added;
    }

    /// <summary>Brugerens eget svar (vises med det samme, også før det er sendt).</summary>
    public HelpdeskLine AddMine(string who, string text, int? replyTo, int? button, bool screenshot, DateTime at, bool logs = false)
    {
        var line = new HelpdeskLine { Mine = true, Who = who, Text = HelpdeskText.Clean(text), At = at, ReplyTo = replyTo, Screenshot = screenshot, Logs = logs, Pending = true };
        if (replyTo is int id && button is int b && Lines.FirstOrDefault(l => !l.Mine && l.Id == id) is { } msg && b >= 0 && b < msg.Buttons.Count)
        {
            msg.Answered = b;
            if (line.Text.Length == 0) line.Text = msg.Buttons[b].Label;
        }
        Lines.Add(line);
        Trim();
        return line;
    }

    /// <summary>Beskeder fra support, brugeren ikke har set endnu.</summary>
    public IEnumerable<HelpdeskLine> Unread => Lines.Where(l => !l.Mine && !l.Read);

    /// <summary>Markér som set. Giver de id'er, der skal kvitteres som læst.</summary>
    public List<int> MarkRead(IEnumerable<int>? ids = null)
    {
        var want = ids?.ToHashSet();
        var res = new List<int>();
        foreach (var l in Lines.Where(l => !l.Mine && !l.Read && (want is null || want.Contains(l.Id)))) { l.Read = true; res.Add(l.Id); }
        return res;
    }

    /// <summary>Kan svarknappen bruges? Kun én gang og kun på beskedens egne knapper.</summary>
    public bool CanAnswer(int id, int button) =>
        Lines.FirstOrDefault(l => !l.Mine && l.Id == id) is { } m && m.Answered is null && button >= 0 && button < m.Buttons.Count;

    private void Trim()
    {
        if (Lines.Count > Keep) Lines.RemoveRange(0, Lines.Count - Keep);
    }
}

public sealed class HelpdeskLine
{
    /// <summary>Serverens id for beskeder fra support (0 for brugerens egne linjer).</summary>
    public int Id { get; set; }
    /// <summary>Skrevet af brugeren på maskinen.</summary>
    public bool Mine { get; set; }
    public string Who { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTime At { get; set; }
    public List<HelpdeskButton> Buttons { get; set; } = [];
    /// <summary>Den svarknap, brugeren valgte (indeks).</summary>
    public int? Answered { get; set; }
    public bool Read { get; set; }
    public int? ReplyTo { get; set; }
    public bool Screenshot { get; set; }
    /// <summary>Brugeren sendte logfiler med.</summary>
    public bool Logs { get; set; }
    /// <summary>Brugerens svar, der endnu ikke er sendt til serveren.</summary>
    public bool Pending { get; set; }
}
