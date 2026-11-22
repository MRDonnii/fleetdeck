using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Fleetdeck.Shared;

namespace Fleetdeck.MacApp;

/// <summary>
/// Beskeder på Mac (som beskedvinduet i Windows): overskrift, samtalen, et svarfelt med flere linjer og knapperne
/// "Send svar", beskedens svarknapper, "Send logfiler" og "Luk". Svarene går som ordrer til tjenesten, som sender dem
/// til support. <c>compose</c> = "Skriv til support…" (en ny sag).
/// </summary>
public sealed class MessageWindow : Window
{
    private readonly bool _compose;
    private readonly TextBlock _title = Ui.Text(""), _sub = Ui.Text("");
    private readonly StackPanel _convo = new() { Spacing = 2 };
    private readonly ScrollViewer _scroll;
    private readonly Border _convoCard;
    private readonly TextBox _reply;
    private readonly TextBlock _state = Ui.Text("", 11.5, muted: true);
    private readonly WrapPanel _buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
    private HelpdeskInbox _inbox = new();
    private DateTime _written;
    /// <summary>Sat, når brugeren lige har sendt: linjen under feltet viser det, til tjenesten har skrevet samtalen igen.</summary>
    private string? _sentNote;

    public bool Compose => _compose;

    public MessageWindow(bool compose)
    {
        _compose = compose;
        Title = "Fleetdeck"; Icon = Ui.AppIcon; Width = 468; SizeToContent = SizeToContent.Height; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var root = new StackPanel { Margin = new Thickness(24, 18), Spacing = 10 };
        root.Children.Add(Ui.Hero(_title, _sub));
        _scroll = new ScrollViewer { MaxHeight = 260, Content = _convo, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        _convoCard = Ui.Card(_scroll);
        _convoCard.IsVisible = false;
        root.Children.Add(_convoCard);
        // svarfeltet er altid med: flere linjer, fast højde, synlig ramme
        _reply = new TextBox
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 84, MaxLength = HelpdeskText.MaxText,
            PlaceholderText = compose ? "Hvad er der galt? (Enter sender, Skift+Enter giver ny linje)" : HelpdeskView.ReplyHint,
            BorderThickness = new Thickness(1), Padding = new Thickness(10, 7),
        };
        // Enter sender, Skift+Enter giver ny linje – fanges før tekstfeltet selv laver linjeskiftet
        _reply.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Return)) return;
            var m = e.KeyModifiers;
            if (!HelpdeskView.EnterSends(m.HasFlag(KeyModifiers.Shift), m.HasFlag(KeyModifiers.Control) || m.HasFlag(KeyModifiers.Meta), m.HasFlag(KeyModifiers.Alt))) return;
            e.Handled = true;
            Submit();
        }, RoutingStrategies.Tunnel);
        root.Children.Add(_reply);
        root.Children.Add(_state);
        root.Children.Add(_buttons);
        Content = root;
        Opened += (_, _) => FocusReply();
        Activated += (_, _) => FocusReply();
        if (compose)
        {
            _title.Text = "Skriv til support";
            _sub.Text = "Beskriv problemet. Svaret kommer her i Fleetdeck.";
            Buttons();
        }
    }

    public void FocusReply() => Dispatcher.UIThread.Post(() => { _reply.Focus(); _reply.CaretIndex = _reply.Text?.Length ?? 0; }, DispatcherPriority.Input);

    /// <summary>Ny samtale fra tjenesten. Bygges kun om, når den er ændret; teksten i svarfeltet bevares.</summary>
    public void Show(HelpdeskInbox inbox)
    {
        if (_compose) return;
        if (inbox.Written == _written && _convo.Children.Count > 0) { _state.Text = _sentNote ?? HelpdeskText.SendState(inbox.Lines, DateTime.UtcNow); return; }
        _inbox = inbox; _written = inbox.Written; _sentNote = null;
        _title.Text = HelpdeskView.Title(inbox);
        _sub.Text = HelpdeskView.Sub(inbox);
        _convo.Children.Clear();
        foreach (var l in HelpdeskView.Recent(inbox))
        {
            _convo.Children.Add(Ui.Text(HelpdeskView.Head(l), 11.5, muted: true));
            var body = Ui.Text(HelpdeskView.Body(l), 13, weight: l.Mine ? FontWeight.Normal : FontWeight.SemiBold);
            body.Margin = new Thickness(0, 0, 0, 8);
            _convo.Children.Add(body);
            if (HelpdeskView.Answered(l) is { } a) _convo.Children.Add(Ui.Text(a, 12, muted: true));
        }
        _convoCard.IsVisible = _convo.Children.Count > 0;
        _state.Text = HelpdeskText.SendState(inbox.Lines, DateTime.UtcNow);
        Buttons();
        Dispatcher.UIThread.Post(() => _scroll.ScrollToEnd(), DispatcherPriority.Background);
        // set: tjenesten kvitterer som læst
        var unread = inbox.Unread.Select(l => l.Id).ToList();
        if (unread.Count > 0) MacSys.UserCommand(new TrayCommand { Action = "helpdesk_read", Ids = unread });
    }

    private void Buttons()
    {
        _buttons.Children.Clear();
        var list = new List<Button>();
        // som i Windows: knapperne står fra højre – "Send svar" yderst
        if (_compose)
        {
            list.Add(Btn(HelpdeskView.LogsLabel, () => Send("helpdesk_logs", null, allowEmpty: true)));
            list.Add(Btn("Annullér", Close));
            list.Add(Btn("Send", Submit, primary: true));
        }
        else
        {
            if (HelpdeskView.Ask(_inbox) is { } ask)
                for (var i = 0; i < ask.Buttons.Count; i++)
                {
                    var idx = i;
                    list.Add(Btn(ask.Buttons[i].Label, () => Send("helpdesk_reply", idx, allowEmpty: true, replyTo: ask.Id)));
                }
            else list.Add(Btn(HelpdeskView.CloseLabel, Close));
            list.Insert(0, Btn(HelpdeskView.LogsLabel, () => Send("helpdesk_logs", null, allowEmpty: true)));
            list.Add(Btn(HelpdeskView.SendLabel, Submit, primary: true));
        }
        foreach (var b in list) { b.Margin = new Thickness(8, 0, 0, 0); _buttons.Children.Add(b); }
    }

    private static Button Btn(string text, Action click, bool primary = false)
    {
        var b = Ui.Button(text, primary);
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>"Send svar" eller Enter i svarfeltet.</summary>
    private void Submit() => Send("helpdesk_reply", null, allowEmpty: false);

    private void Send(string action, int? button, bool allowEmpty, int? replyTo = null)
    {
        var text = _reply.Text ?? "";
        if (!allowEmpty && string.IsNullOrWhiteSpace(text)) { _state.Text = "Skriv et svar først."; FocusReply(); return; }
        var c = new TrayCommand { Action = action, Text = text, Button = button, ReplyTo = _compose ? null : replyTo ?? HelpdeskView.LastFromSupport(_inbox) };
        if (!MacSys.UserCommand(c)) { _state.Text = "Kunne ikke sendes – kører Fleetdeck-tjenesten?"; return; }
        _reply.Text = "";
        if (_compose)
        {
            MacSys.Notify(action == "helpdesk_logs" ? "Logfilerne sendes. Du får svar her." : "Sendt. Du får svar her.");
            Close();
            return;
        }
        _sentNote = action == "helpdesk_logs" ? "Samler og sender logfiler…" : "Sendes…";
        _state.Text = _sentNote;
        FocusReply();
    }
}
