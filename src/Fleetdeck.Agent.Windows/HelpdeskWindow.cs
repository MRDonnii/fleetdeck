using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;
using Fleetdeck.Shared;

namespace Fleetdeck.Agent.Windows;

/// <summary>En knap i <see cref="MessageWindow"/>. Lukker vinduet bagefter, medmindre <paramref name="Close"/> er falsk.</summary>
public sealed record MessageAction(string Label, Action Click, bool Primary = false, bool Close = true);

/// <summary>Hvad <see cref="MessageWindow"/> skal vise. Alt er valgfrit ud over titlen.</summary>
public sealed class MessageWindowOptions
{
    public string Sub { get; init; } = "";
    /// <summary>Samtalen (ældste først); de seneste vises.</summary>
    public IReadOnlyList<HelpdeskLine>? Conversation { get; init; }
    /// <summary>Eget indhold under samtalen (fx en nedtælling).</summary>
    public Control? Extra { get; init; }
    /// <summary>Svarfelt med "Send" – kaldes med teksten og om der skal vedhæftes et skærmbillede.</summary>
    public Action<string, bool>? Send { get; init; }
    /// <summary>"Send logfiler" – kaldes med teksten i svarfeltet (må være tom).</summary>
    public Action<string>? SendLogs { get; init; }
    public string SendLabel { get; init; } = "Send svar";
    public string ReplyHint { get; init; } = "Skriv et svar… (Enter sender, Skift+Enter giver ny linje)";
    public bool Screenshot { get; init; } = true;
    public IReadOnlyList<MessageAction> Actions { get; init; } = [];
    public bool TopMost { get; init; } = true;
    /// <summary>Linjen under svarfeltet (fx "Sendes…" eller "Sendt kl. 14.02.").</summary>
    public string State { get; init; } = "";
}

/// <summary>Hvad en tast i svarfeltet gør. Ren logik (uden WinForms), så den kan testes.</summary>
public static class ReplyKeys
{
    public enum Act { None, Send, NewLine }
    /// <summary>Enter sender (som i en chat). Skift+Enter giver en ny linje. Ctrl+Enter sender også.</summary>
    public static Act OnKey(bool enter, bool shift, bool ctrl, bool alt) =>
        !enter || alt ? Act.None : shift && !ctrl ? Act.NewLine : Act.Send;
}

/// <summary>Vinduets opbygning og mål. Ren logik (uden WinForms), så den kan testes.</summary>
public static class MessageLayout
{
    public enum Row { Hero, Conversation, Extra, Reply, State, Screenshot, Buttons }

    /// <summary>Svarfeltets højde i logiske punkter (96 DPI).</summary>
    public const int ReplyHeight = 84;

    /// <summary>Rækkerne oppefra. Kan der svares, er svarfeltet altid med – lige under samtalen og over knapperne.</summary>
    public static IReadOnlyList<Row> Rows(bool canReply, bool screenshot, bool extra)
    {
        var rows = new List<Row> { Row.Hero, Row.Conversation };
        if (extra) rows.Add(Row.Extra);
        if (canReply)
        {
            rows.Add(Row.Reply); rows.Add(Row.State);
            if (screenshot) rows.Add(Row.Screenshot);
        }
        rows.Add(Row.Buttons);
        return rows;
    }

    /// <summary>
    /// Får en kontrol med fast bredde også min./maks.-mål? Kun AutoSize-kontroller (deres højde beregnes igen). WinForms
    /// afkorter nemlig højden til MaximumSize.Height, når MaximumSize sættes (<see cref="HeightAfterMaximum"/>) – også når
    /// DPI-skaleringen sætter den igen. Maks.-højden 0 ("ingen grænse") gav derfor svarfeltet højden 0: det forsvandt.
    /// </summary>
    public static bool LimitWidth(bool autoSize) => autoSize;

    /// <summary>Højden, WinForms giver en kontrol, når MaximumSize sættes (CommonProperties.SetMaximumSize: Math.Min, 0 tæller med).</summary>
    public static int HeightAfterMaximum(int height, int maxHeight) => Math.Min(height, maxHeight);

    /// <summary>Svarfeltets højde i pixels ved skærmens DPI.</summary>
    public static int ReplyHeightAt(int dpi) => (int)Math.Round(ReplyHeight * Math.Max(96, dpi) / 96f);

    /// <summary>Sikkerhedsnet: er svarfeltet blevet lavere end halvdelen af sin højde, sættes det tilbage.</summary>
    public static bool ReplyTooSmall(int height, int dpi) => height < ReplyHeightAt(dpi) / 2;
}

/// <summary>
/// Lille vindue i brugerens session: overskrift, evt. en samtale, et svarfelt med "Vedhæft skærmbillede" og knapper.
/// Bruges til Beskeder (helpdesk) og kan bruges af andre advarsler (fx genstart) – al tekst vises som ren tekst.
/// </summary>
public sealed class MessageWindow : Form
{
    private const int W = 420;
    private readonly Theme t = Theme.Current;
    private readonly TableLayoutPanel convo;
    private readonly Panel convoBox;
    private readonly FlowLayoutPanel buttons;
    private readonly TextArea? reply;
    private readonly CheckBox? shot;
    private readonly Label? state;
    private MessageWindowOptions o;
    /// <summary>Sand, når vinduet er skaleret til skærmens DPI: kontroller, der bygges derefter, skal skaleres selv.</summary>
    private bool scaled;

    public MessageWindow(string title, MessageWindowOptions options)
    {
        o = options;
        Ui.BeginDpi(this);
        Text = "Fleetdeck"; Icon = Ui.AppIcon(32); BackColor = t.Bg; Font = Ui.F(10f);
        FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false; MinimizeBox = true; StartPosition = FormStartPosition.Manual;
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink; ShowInTaskbar = true; TopMost = o.TopMost;
        var root = Ui.Stack(W + 48, new Padding(24, 18, 24, 18));
        convo = Ui.Stack(W - 40);
        convoBox = Ui.FixWidth(new Panel { AutoScroll = true, Height = 10, BackColor = t.Card, Padding = new Padding(12, 10, 12, 6), Margin = new Padding(0, 0, 0, 10) }, W);
        convoBox.Controls.Add(convo);
        buttons = Ui.FixWidth(new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, WrapContents = true, BackColor = Color.Transparent, Margin = new Padding(0, 2, 0, 0) }, W);
        foreach (var row in MessageLayout.Rows(o.Send is not null, o.Screenshot, o.Extra is not null))
            switch (row)
            {
                case MessageLayout.Row.Hero: root.Controls.Add(new Hero(t, title, o.Sub, W)); break;
                case MessageLayout.Row.Conversation: root.Controls.Add(convoBox); break;
                case MessageLayout.Row.Extra: root.Controls.Add(o.Extra!); break;
                case MessageLayout.Row.Reply:
                    // et rigtigt tekstfelt med flere linjer: hele feltet kan klikkes og skrives i
                    reply = Ui.FixWidth(new TextArea(t, o.ReplyHint, MessageLayout.ReplyHeight), W);
                    reply.Box.MaxLength = HelpdeskText.MaxText;
                    reply.Box.KeyDown += (_, e) =>
                    {
                        switch (ReplyKeys.OnKey(e.KeyCode == Keys.Enter, e.Shift, e.Control, e.Alt))
                        {
                            case ReplyKeys.Act.Send: e.Handled = e.SuppressKeyPress = true; Submit(); break;
                            case ReplyKeys.Act.NewLine: break;   // tekstfeltet laver selv linjeskiftet
                        }
                    };
                    reply.Margin = new Padding(0, 0, 0, 4);
                    root.Controls.Add(reply);
                    break;
                case MessageLayout.Row.State:
                    state = Ui.Text(o.State, t, 8.75f, muted: true, maxWidth: W);
                    state.Margin = new Padding(2, 0, 0, 8);
                    root.Controls.Add(state);
                    break;
                case MessageLayout.Row.Screenshot:
                    shot = new CheckBox { Text = "Vedhæft et skærmbillede (tages, når du trykker Send)", AutoSize = true, ForeColor = t.Text, BackColor = Color.Transparent,
                        Font = Ui.F(9.5f), UseMnemonic = false, Margin = new Padding(0, 0, 0, 12) };
                    root.Controls.Add(shot);
                    break;
                case MessageLayout.Row.Buttons: root.Controls.Add(buttons); break;
            }
        Controls.Add(root);
        Fill();
        Ui.EndDpi(this);
        scaled = true;
        Load += (_, _) =>
        {
            Ui.TitleBar(this, t);
            EnsureReply();
            var wa = Screen.PrimaryScreen!.WorkingArea;   // øverst i midten, så den ses
            Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Top + 40);
            ScrollToEnd();
        };
        // markøren står i svarfeltet, så man kan skrive med det samme
        Shown += (_, _) => { EnsureReply(); FocusReply(); };
        DpiChanged += (_, _) => BeginInvoke(EnsureReply);
        Activated += (_, _) => { if (ActiveControl is null or FlatBtn) FocusReply(); };
    }

    /// <summary>Svarfeltet skal altid kunne ses: er det (af layout eller DPI-skalering) blevet for lavt, får det sin højde igen.</summary>
    private void EnsureReply()
    {
        if (reply is null || IsDisposed) return;
        if (!reply.Visible) reply.Visible = true;
        if (MessageLayout.ReplyTooSmall(reply.Height, DeviceDpi)) reply.Height = MessageLayout.ReplyHeightAt(DeviceDpi);
    }

    public void FocusReply()
    {
        if (reply is null) return;
        ActiveControl = reply.Box;
        reply.Box.Focus();
    }

    /// <summary>Ny samtale og nye knapper (fx når en ny besked kommer, mens vinduet er åbent). Teksten i svarfeltet og fokus bevares.</summary>
    public void Update(MessageWindowOptions options)
    {
        o = new MessageWindowOptions
        {
            Sub = options.Sub, Conversation = options.Conversation, Actions = options.Actions, Send = o.Send, SendLogs = o.SendLogs, SendLabel = o.SendLabel,
            ReplyHint = o.ReplyHint, Screenshot = o.Screenshot, Extra = o.Extra, TopMost = o.TopMost, State = options.State,
        };
        var typing = reply?.Box.Focused == true;
        var caret = reply?.Box.SelectionStart ?? 0;
        SuspendLayout(); Fill(); ResumeLayout(true);
        ScrollToEnd();
        if (typing && reply is not null) { reply.Box.Focus(); reply.Box.SelectionStart = Math.Min(caret, reply.Box.TextLength); }
    }

    /// <summary>Linjen under svarfeltet (sendes/sendt/venter).</summary>
    public void SetState(string text)
    {
        if (state is null || state.Text == text) return;
        state.Text = text;
    }

    /// <summary>Kontroller, der bygges, efter vinduet er skaleret, får samme DPI-skalering som resten.</summary>
    private T S<T>(T c) where T : Control => scaled ? Ui.Scaled(c, this) : c;

    private void Fill()
    {
        convo.Controls.Clear();
        var lines = o.Conversation?.TakeLast(12).ToList() ?? [];
        foreach (var l in lines)
        {
            var who = l.Mine ? "Dig" : l.Who.Length > 0 ? l.Who : "Support";
            var head = $"{who} · {l.At.ToLocalTime():HH:mm}{(l.Pending ? " · sendes…" : "")}{(l.Screenshot ? " · skærmbillede" : "")}{(l.Logs ? " · logfiler" : "")}";
            convo.Controls.Add(S(Ui.Text(head, t, 8.5f, muted: true, maxWidth: W - 40)));
            var body = Ui.Text(l.Text.Length > 0 ? l.Text : l.Logs ? "(logfiler)" : "(skærmbillede)", t, 10f, l.Mine ? FontStyle.Regular : FontStyle.Bold, maxWidth: W - 40);
            body.Margin = new Padding(0, 0, 0, 10);
            convo.Controls.Add(S(body));
            if (!l.Mine && l.Answered is int a && a < l.Buttons.Count)
                convo.Controls.Add(S(Ui.Text($"Du svarede: {l.Buttons[a].Label}", t, 9f, muted: true, maxWidth: W - 40)));
        }
        convoBox.Visible = lines.Count > 0;
        var max = scaled ? (int)Math.Round(260 * DeviceDpi / 96f) : 260;
        convoBox.Height = Math.Min(max, convo.PreferredSize.Height + convoBox.Padding.Vertical);
        SetState(o.State);
        buttons.Controls.Clear();
        if (o.Send is not null && reply is not null)
        {
            var sendBtn = S(new FlatBtn(t, o.SendLabel, primary: true));
            sendBtn.Click += (_, _) => Submit();
            buttons.Controls.Add(sendBtn);
        }
        foreach (var a in o.Actions.Reverse())
        {
            var b = S(new FlatBtn(t, a.Label, a.Primary && o.Send is null));
            b.Click += (_, _) => { a.Click(); if (a.Close) Close(); };
            buttons.Controls.Add(b);
        }
        if (o.SendLogs is { } logs && reply is not null)
        {
            var lb = S(new FlatBtn(t, "Send logfiler"));
            lb.Click += (_, _) => { logs(reply.Box.Text); reply.Box.Text = ""; FocusReply(); };
            buttons.Controls.Add(lb);
        }
    }

    /// <summary>"Send svar" eller Enter i svarfeltet.</summary>
    private void Submit()
    {
        if (o.Send is not { } send || reply is null) return;
        var text = reply.Box.Text;
        if (string.IsNullOrWhiteSpace(text) && shot?.Checked != true) { SetState("Skriv et svar først (eller vælg et skærmbillede)."); FocusReply(); return; }
        send(text, shot?.Checked == true);
        if (IsDisposed) return;
        reply.Box.Text = ""; if (shot is not null) shot.Checked = false;
        FocusReply();
    }

    /// <summary>Teksten i svarfeltet (sendes med, når brugeren trykker en svarknap).</summary>
    public string ReplyText => reply?.Box.Text ?? "";
    public bool WantsScreenshot => shot?.Checked == true;
    /// <summary>Efter en svarknap: tekst og skærmbillede er sendt med, så feltet tømmes.</summary>
    public void ClearReply() { if (reply is not null) reply.Box.Text = ""; if (shot is not null) shot.Checked = false; }

    private void ScrollToEnd()
    {
        if (convo.Controls.Count > 0) convoBox.ScrollControlIntoView(convo.Controls[^1]);
    }

    /// <summary>Skærmbillede af alle skærme (uden dette vindue), nedskaleret og gemt som JPEG. Null, hvis det ikke kunne tages.</summary>
    public byte[]? CaptureScreen()
    {
        var was = Opacity;
        try
        {
            Opacity = 0; Application.DoEvents(); Thread.Sleep(250);
            return CaptureAll();
        }
        finally { Opacity = was; }
    }

    public static byte[]? CaptureAll()
    {
        try
        {
            var vs = SystemInformation.VirtualScreen;
            using var full = new Bitmap(vs.Width, vs.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(full)) g.CopyFromScreen(vs.Left, vs.Top, 0, 0, vs.Size);
            var scale = Math.Min(1.0, 1920.0 / Math.Max(1, vs.Width));
            using var small = new Bitmap(full, Math.Max(1, (int)(vs.Width * scale)), Math.Max(1, (int)(vs.Height * scale)));
            using var ms = new MemoryStream();
            var jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            using var ep = new EncoderParameters(1);
            ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 70L);
            small.Save(ms, jpeg, ep);
            return ms.Length <= HelpdeskText.MaxScreenshotBytes ? ms.ToArray() : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or ExternalException or ArgumentException) { return null; }
    }
}

/// <summary>
/// Beskeder i ikonet: læser userwork\helpdesk.json, viser nye beskeder i <see cref="MessageWindow"/> og sender svar som
/// ordrer til tjenesten (userwork\cmd). "Skriv til support…" i menuen starter en ny sag.
/// </summary>
public sealed class HelpdeskTray
{
    private const string MessageTitle = "Besked fra ";
    private readonly NotifyIcon icon;

    public HelpdeskTray(NotifyIcon icon)
    {
        this.icon = icon;
        // et klik på notifikationen "Besked fra …" åbner samtalen – og giver vinduet lov til at tage fokus, så man kan skrive med det samme
        icon.BalloonTipClicked += (_, _) => { if (icon.BalloonTipTitle.StartsWith(MessageTitle, StringComparison.Ordinal)) Open(); };
    }

    private static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Fleetdeck", "userwork");
    private static string InboxFile => Path.Combine(Dir, "helpdesk.json");
    private static string CmdDir => Path.Combine(Dir, "cmd");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private HelpdeskInbox inbox = new();
    private DateTime written;
    private readonly HashSet<int> announced = [];
    private MessageWindow? win;
    private bool composing;
    /// <summary>En besked kom, mens brugeren skrev en ny sag: vises, når den er sendt eller lukket.</summary>
    private bool waiting;

    public int Unread => inbox.Unread.Count();
    public bool HasConversation => inbox.Lines.Count > 0;
    private string Sender => inbox.Lines.LastOrDefault(l => !l.Mine)?.Who is { Length: > 0 } w ? w : "support";

    /// <summary>Kaldes hvert 3. sekund fra ikonet. Vinduet bygges kun om, når samtalen er ændret – ellers opdateres kun linjen under svarfeltet.</summary>
    public void Poll()
    {
        var changed = false;
        try
        {
            if (File.Exists(InboxFile))
            {
                var next = JsonSerializer.Deserialize<HelpdeskInbox>(File.ReadAllText(InboxFile), Json);
                if (next is not null && next.Written != written) { inbox = next; written = next.Written; changed = true; }
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        if (!changed)
        {
            if (win is { IsDisposed: false } && !composing) win.SetState(HelpdeskText.SendState(inbox.Lines, DateTime.UtcNow));
            return;
        }
        // også beskeder, der kom, mens ingen var logget ind (announced er tom efter login)
        var fresh = inbox.Unread.Where(l => announced.Add(l.Id)).ToList();
        if (fresh.Count > 0)
        {
            icon.ShowBalloonTip(8000, $"{MessageTitle}{Sender}", HelpdeskText.Preview(fresh[^1].Text, 200), ToolTipIcon.Info);
            // skriver brugeren en ny sag, lukkes den ikke midt i det: beskeden vises bagefter
            if (composing && win is { IsDisposed: false }) { waiting = true; return; }
            Open();
        }
        else if (win is { IsDisposed: false } && !composing) win.Update(Options());
    }

    /// <summary>Samtalen (fra menuen eller når en ny besked kommer).</summary>
    public void Open()
    {
        waiting = false;
        if (win is { IsDisposed: false } && !composing) { win.Update(Options()); Show(); return; }
        if (win is { IsDisposed: false }) win.Close();
        composing = false;
        var o = Options();
        win = new MessageWindow($"{MessageTitle}{Sender}", new MessageWindowOptions
        {
            Sub = o.Sub, Conversation = inbox.Lines, Actions = o.Actions, State = o.State,
            Send = (text, withShot) => Reply(text, withShot, LastFromSupport, null),
            SendLogs = text => Logs(text, LastFromSupport),
        });
        Show();
    }

    private int? LastFromSupport => inbox.Lines.LastOrDefault(l => !l.Mine)?.Id;

    /// <summary>"Skriv til support…": en ny sag. Pc'ens grundoplysninger sendes med af serveren.</summary>
    public void NewCase()
    {
        if (win is { IsDisposed: false }) win.Close();
        composing = true;
        MessageWindow? me = null;
        void Sent(string what)
        {
            me?.Close();
            icon.ShowBalloonTip(4000, "Fleetdeck", $"{what} Du får svar her.", ToolTipIcon.Info);
        }
        me = win = new MessageWindow("Skriv til support", new MessageWindowOptions
        {
            Sub = "Beskriv problemet. Pc'ens navn, Windows-version, oppetid og seneste fejl sendes med.", ReplyHint = "Hvad er der galt? (Enter sender, Skift+Enter giver ny linje)", SendLabel = "Send",
            Send = (text, withShot) => { Reply(text, withShot, null, null); Sent("Sendt."); },
            SendLogs = text => { Logs(text, null); Sent("Logfilerne sendes."); },
            Actions = [new MessageAction("Annullér", () => { })],
        });
        me.FormClosed += (_, _) =>
        {
            if (win != me) return;
            composing = false;
            if (waiting) Open();
        };
        Show();
    }

    private void Show()
    {
        if (win is null) return;
        win.Show(); win.WindowState = FormWindowState.Normal; win.Activate(); win.FocusReply();
        Seen();
    }

    /// <summary>Svarknapperne fra den seneste ubesvarede besked (fx OK / Ja, genstart nu / Senere) og "Luk".</summary>
    private MessageWindowOptions Options()
    {
        var ask = inbox.Lines.LastOrDefault(l => !l.Mine && l.Buttons.Count > 0 && l.Answered is null);
        var actions = new List<MessageAction>();
        if (ask is not null)
            for (var i = 0; i < ask.Buttons.Count; i++)
            {
                var idx = i;
                actions.Add(new MessageAction(ask.Buttons[i].Label, () =>
                {
                    var text = win?.ReplyText ?? ""; var withShot = win?.WantsScreenshot == true;
                    win?.ClearReply();
                    Reply(text, withShot, ask.Id, idx);
                }, Primary: i == 0, Close: false));
            }
        else actions.Add(new MessageAction("Luk", () => { }));
        return new MessageWindowOptions
        {
            Sub = $"Svar herunder. Svaret går direkte til {Sender}.", Conversation = inbox.Lines, Actions = actions,
            State = HelpdeskText.SendState(inbox.Lines, DateTime.UtcNow),
        };
    }

    private void Seen()
    {
        var ids = inbox.Unread.Select(l => l.Id).ToList();
        if (ids.Count == 0) return;
        foreach (var l in inbox.Unread.ToList()) l.Read = true;
        Write(new TrayCommand { Action = "helpdesk_read", Ids = ids });
    }

    private void Reply(string text, bool withShot, int? replyTo, int? button)
    {
        string? file = null;
        if (withShot && (win is { IsDisposed: false } w ? w.CaptureScreen() : MessageWindow.CaptureAll()) is { } jpg)
        {
            file = $"{Guid.NewGuid():N}.jpg";
            try { Directory.CreateDirectory(CmdDir); File.WriteAllBytes(Path.Combine(CmdDir, file), jpg); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { file = null; }
        }
        // vis svaret med det samme; tjenesten skriver samtalen igen, når det er sendt
        var mine = inbox.AddMine("", text, replyTo, button, file is not null, DateTime.UtcNow);
        if (mine.Text.Length == 0 && file is null) { inbox.Lines.Remove(mine); return; }
        if (!Write(new TrayCommand { Action = "helpdesk_reply", Text = text, ReplyTo = replyTo, Button = button, File = file })) { inbox.Lines.Remove(mine); return; }
        if (win is { IsDisposed: false } && !composing) win.Update(Options());
    }

    /// <summary>"Send logfiler": tjenesten samler agentens log, seneste fejl og systeminfo og sender dem (med teksten, hvis der er skrevet noget).</summary>
    private void Logs(string text, int? replyTo)
    {
        var mine = inbox.AddMine("", text, replyTo, null, false, DateTime.UtcNow, logs: true);
        if (!Write(new TrayCommand { Action = "helpdesk_logs", Text = text, ReplyTo = replyTo })) { inbox.Lines.Remove(mine); return; }
        if (win is { IsDisposed: false } && !composing) win.Update(Options());
    }

    /// <summary>Ordren skrives som .tmp og omdøbes til .json, når den er færdig, så tjenesten aldrig læser en halv ordre.</summary>
    private bool Write(TrayCommand c)
    {
        try
        {
            Directory.CreateDirectory(CmdDir);
            var name = Path.Combine(CmdDir, $"{Guid.NewGuid():N}");
            File.WriteAllText(name + ".tmp", JsonSerializer.Serialize(c, Json));
            File.Move(name + ".tmp", name + ".json");
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            icon.ShowBalloonTip(4000, "Fleetdeck", $"Kunne ikke sende: {e.Message}", ToolTipIcon.Error);
            win?.SetState($"Kunne ikke sendes: {e.Message}");
            return false;
        }
    }
}
