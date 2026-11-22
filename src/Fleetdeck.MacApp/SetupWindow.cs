using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fleetdeck.Shared;

[assembly: InternalsVisibleTo("Fleetdeck.SetupShots")]

namespace Fleetdeck.MacApp;

/// <summary>
/// Opsætningsvinduet (første gang Fleetdeck.app åbnes – eller "Skift server" fra menulinjen), forslag B "macOS-indbygget":
/// sidebjælke med trinene Velkommen → Server → Tilmelding → Profil og tilvalg → Installation → Tilladelser, én skærm ad gangen,
/// fejl ved det felt, de handler om, og indholdet helt op under trafiklysene med macOS' vibrancy i sidebjælken.
/// Trin, skærme og tekster kommer fra <see cref="SetupFlow"/>/<see cref="SetupTexts"/> (samme som Windows og Linux).
/// Er agenten installeret, vises en oversigt med sidebjælke (Oversigt, Server og tilmelding, Profil, Tilvalg, Tilladelser):
/// rigtig tilstand, skift server/profil (nøglen skal kun bruges der), udvidelser installeres med det samme, og afinstallér.
/// </summary>
public sealed class SetupWindow : Window
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    internal SetupFlow Flow { get; }
    private readonly bool _demo;
    private readonly Action _installed;

    private readonly TextBox _server = new() { PlaceholderText = SetupTexts.ServerPlaceholder };
    private readonly TextBox _key = new() { PlaceholderText = SetupTexts.KeyPlaceholder, PasswordChar = '•' };
    private readonly TextBox _message = new()
    {
        PlaceholderText = SetupWords.For(SetupOs.Mac).Example, MaxLength = 200, TextWrapping = TextWrapping.Wrap, Height = 58, VerticalContentAlignment = VerticalAlignment.Top,
    };
    internal TextBox ServerBox => _server;
    internal TextBox KeyBox => _key;
    private readonly ContentControl _page = new();
    private Button? _primary;
    private SetupScreen? _shown;
    private string? _shownSection;
    private string _refreshSig = "";

    // data
    internal List<ProfileOption> Profiles { get; set; } = [];
    internal int? ProfileId { get; set; }
    private int? _wantedProfile;
    private string _profilesFor = "";
    internal Dictionary<string, bool> ExtOn { get; } = Fleetdeck.Shared.Extensions.BuiltIn.Where(e => e.Mac is not null)
        .ToDictionary(e => e.Id, e => Fleetdeck.Shared.Extensions.DefaultOn.Contains(e.Id));
    private List<MacSetup.ExtensionRow> _extRows = [];
    private readonly Dictionary<string, (DateTime Since, bool Seen)> _watch = [];
    private bool _extBusy;
    internal TrayStatus? Status { get; set; }
    internal string Section { get; set; } = "overview";
    internal (string Text, SetupTone Tone, string? Sub)? Note { get; set; }
    internal bool Busy { get; set; }
    internal int InstallAt { get; set; }
    internal bool DetailsOpen { get; set; }
    private void ToggleDetails() { DetailsOpen = !DetailsOpen; Rerender(); }
    internal bool? DemoGranted { get; set; }
    internal string? Pasted { get; set; }
    /// <summary>Skærmbilleder: indholdet er flyttet til et andet vindue – tegn ikke igen (sene hændelser ville tage siden med).</summary>
    internal bool Frozen { get; set; }

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _waitTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    public SetupWindow(Action installed) : this(installed, demo: false) { }

    /// <summary>demo: skærmbilleder uden en Mac (tools/Fleetdeck.SetupShots) – ingen tjeneste, ingen timere, opdigtet status.</summary>
    internal SetupWindow(Action installed, bool demo, bool demoInstalled = false, TrayStatus? demoStatus = null)
    {
        _installed = installed; _demo = demo;
        var inst = demo ? demoInstalled : MacSys.Installed;
        Status = demo ? demoStatus : inst ? MacSys.ReadStatus() : null;
        Flow = new SetupFlow(SetupOs.Mac, inst);
        Title = "Fleetdeck"; Icon = Ui.AppIcon; Width = 780; Height = 600; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        // indholdet op under trafiklysene (som Systemindstillinger) og ægte vibrancy bag sidebjælken
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaTitleBarHeightHint = 52;
        TransparencyLevelHint = [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur, WindowTransparencyLevel.None];
        Background = Brushes.Transparent;
        if (inst && Status?.Server is { Length: > 0 } s) _server.Text = s;
        _extRows = Rows();

        _server.TextChanged += (_, _) =>
        {
            if (Has(_server.Text)) FillFromCommand(_server.Text!);
            if (Flow.ServerCheck is not null && !Flow.Installed) { Flow.ServerChanged(); if (Flow.Screen == SetupScreen.ServerError) Rerender(); }
            Flow.Server = _server.Text ?? "";
            UpdateButtons();
        };
        _key.TextChanged += (_, _) =>
        {
            if (Has(_key.Text)) FillFromCommand(_key.Text!);
            if (Flow.KeyCheck is not null) { var was = Flow.Screen; Flow.KeyChanged(); if (was != Flow.Screen || Flow.Installed) Rerender(); }
            UpdateButtons();
        };
        ActualThemeVariantChanged += (_, _) => Rerender();
        _waitTimer.Tick += (_, _) => WaitTick();
        if (inst && !demo) { _timer.Tick += (_, _) => Refresh(); _timer.Start(); }
        Closed += (_, _) => { _timer.Stop(); _waitTimer.Stop(); };
        // fokusringen på valgrækkerne vises kun, når man bruger tastaturet (som i macOS)
        AddHandler(KeyDownEvent, (_, _) => Mac.KeyboardNav = true, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerPressedEvent, (_, _) => Mac.KeyboardNav = false, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        Rerender();
    }

    // ================================================================ tegning

    private MacPal P => MacPal.For(ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark);

    /// <summary>Tegn vinduet igen ud fra modellen (overgang kun, når skærmen skifter).</summary>
    internal void Rerender()
    {
        if (Frozen) return;
        var p = P;
        if (!Flow.Installed) Flow.Server = Discovery.Normalize(_server.Text ?? "");
        var focused = _server.IsFocused ? _server : _key.IsFocused ? _key : _message.IsFocused ? _message : null;
        // fokus på en valgrække/knap: sæt det tilbage på den nye udgave (ellers mister tastatur og VoiceOver deres plads ved hver gentegning)
        var focusKey = focused is null ? (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Control)?.Tag as string : null;
        var screen = Flow.Screen;
        // samme side tegnet igen: bliv stående samme sted på siden
        var keepScroll = _page.Content is ScrollViewer old && _shown == screen && _shownSection == Section ? old.Offset : (Vector?)null;
        var vibrancy = _demo || ActualTransparencyLevel != WindowTransparencyLevel.None;
        var root = new Grid { ColumnDefinitions = new ColumnDefinitions("220,*") };
        root.Children.Add(screen == SetupScreen.Installed ? InstalledSidebar(p, vibrancy) : StepSidebar(p, vibrancy));

        var main = new DockPanel { Background = p.Pane };
        var top = new Border { Height = 52, Background = Brushes.Transparent };
        if (screen == SetupScreen.Installed)
            top.Child = new TextBlock { Text = SetupTexts.Sections(SetupOs.Mac).First(s => s.Id == Section).Title, FontSize = 13, FontWeight = FontWeight.SemiBold,
                Foreground = p.Label2, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        top.PointerPressed += (_, e) => { if (!_demo && e.GetCurrentPoint(top).Properties.IsLeftButtonPressed) BeginMoveDrag(e); };
        DockPanel.SetDock(top, Dock.Top); main.Children.Add(top);
        var foot = Foot(p);
        DockPanel.SetDock(foot, Dock.Bottom); main.Children.Add(foot);
        var body = new StackPanel { MaxWidth = 468, Margin = new Thickness(44, 0, 44, 12) };
        foreach (var c in Body(p)) body.Children.Add(c);
        var page = new ScrollViewer { Content = body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        if (!_demo && _shown != screen)
        {
            // ny skærm: blødt ind (som macOS' egne sideskift); samme skærm tegnes igen uden blink
            page.Opacity = 0;
            page.Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(160) }];
            page.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => page.Opacity = 1);
        }
        if (keepScroll is { } offset) page.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => page.Offset = offset, DispatcherPriority.Loaded);
        _page.Content = page;
        _shown = screen; _shownSection = Section;
        Mac.Detach(_page);
        main.Children.Add(_page);
        Grid.SetColumn(main, 1); root.Children.Add(main);
        Content = root;
        UpdateButtons();
        focused?.Focus();
        if (focusKey is not null)
            Dispatcher.UIThread.Post(() => root.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => Equals(c.Tag, focusKey) && c.IsEffectivelyVisible && c.Focusable)
                ?.Focus(Mac.KeyboardNav ? NavigationMethod.Tab : NavigationMethod.Unspecified), DispatcherPriority.Loaded);
    }

    private Control StepSidebar(MacPal p, bool vibrancy)
    {
        var side = SidebarFrame(p, vibrancy, SetupTexts.Subtitle(SetupOs.Mac), null);
        var steps = (StackPanel)((DockPanel)side.Child!).Children[^1];
        foreach (var st in SetupTexts.Steps(SetupOs.Mac))
        {
            var state = Flow.StateOf(st.Step);
            var note = Flow.StepNote(st.Step);
            steps.Children.Add(SideRow(p, st.Icon, st.Color, st.Title, note, state));
        }
        var footer = (StackPanel)((DockPanel)side.Child!).Children[0];
        if (Flow.Screen is not (SetupScreen.Welcome or SetupScreen.Searching or SetupScreen.Found or SetupScreen.NoServer or SetupScreen.ServerError) && Flow.ServerName() is { } srv)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(Mac.Dot(Flow.Screen is SetupScreen.Waiting or SetupScreen.NoConnection ? p.Orange : Flow.Screen == SetupScreen.Rejected ? p.Label3 : p.Green));
            row.Children.Add(Mac.T(srv, 11.5, p.Label, wrap: false));
            footer.Children.Insert(0, row);
        }
        return side;
    }

    private Control InstalledSidebar(MacPal p, bool vibrancy)
    {
        var (head, kind) = MacSetup.Headline(Status, DateTime.UtcNow);
        var sub = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        sub.Children.Add(Mac.Dot(kind switch { 'o' => p.Green, 'w' => p.Orange, _ => p.Red }));
        sub.Children.Add(Mac.T(head, 11, p.Label2, wrap: false));
        var side = SidebarFrame(p, vibrancy, null, sub);
        var steps = (StackPanel)((DockPanel)side.Child!).Children[^1];
        foreach (var s in SetupTexts.Sections(SetupOs.Mac))
        {
            var id = s.Id;
            steps.Children.Add(Mac.Clickable(p, SideRow(p, s.Icon, s.Color, s.Title, null, Section == id ? SetupStepState.Current : SetupStepState.Todo, plain: true),
                () => { Section = id; Rerender(); }, s.Title, Section == id, "side:" + id));
        }
        var footer = (StackPanel)((DockPanel)side.Child!).Children[0];
        footer.Children.Add(Mac.T("Opdateres hvert 3. sekund", 11, p.Label2));
        return side;
    }

    /// <summary>Sidebjælken: plads til trafiklysene, logo + navn, rækkerne og versionen nederst.</summary>
    private static Border SidebarFrame(MacPal p, bool vibrancy, string? subtitle, Control? subControl)
    {
        var dock = new DockPanel { Margin = new Thickness(10, 0, 10, 12) };
        var footer = new StackPanel { Spacing = 4, Margin = new Thickness(8, 8, 8, 0) };
        footer.Children.Add(Mac.T($"Fleetdeck {MacSys.Version}", 11, p.Label2));
        DockPanel.SetDock(footer, Dock.Bottom); dock.Children.Add(footer);
        var ident = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(8, 52, 8, 14) };
        var logo = Mac.Logo(32); logo.Margin = new Thickness(0, 0, 10, 0); ident.Children.Add(logo);
        var col = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        col.Children.Add(Mac.T("Fleetdeck", 13, p.Label, FontWeight.SemiBold));
        if (subtitle is not null) col.Children.Add(Mac.T(subtitle, 11, p.Label2));
        if (subControl is not null) col.Children.Add(subControl);
        Grid.SetColumn(col, 1); ident.Children.Add(col);
        DockPanel.SetDock(ident, Dock.Top); dock.Children.Add(ident);
        dock.Children.Add(new StackPanel { Spacing = 2 });
        return new Border
        {
            Background = p.Sidebar(vibrancy), BorderBrush = p.SidebarLine, BorderThickness = new Thickness(0, 0, 0.5, 0), Child = dock,
        };
    }

    private static Control SideRow(MacPal p, string icon, string color, string title, string? note, SetupStepState state, bool plain = false)
    {
        var cur = state == SetupStepState.Current;
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(6, 0, 8, 0), MinHeight = note is null ? 30 : 42 };
        var tile = Mac.Tile(icon, color, 22);
        if (state == SetupStepState.Todo && !plain) tile.Opacity = 0.45;
        tile.Margin = new Thickness(0, 0, 9, 0); tile.VerticalAlignment = VerticalAlignment.Center;
        g.Children.Add(tile);
        var lbl = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        lbl.Children.Add(Mac.T(title, 13, cur ? Brushes.White : state == SetupStepState.Todo && !plain ? p.Label2 : p.Label, wrap: false));
        if (note is not null) lbl.Children.Add(Mac.T(note, 10.5, cur ? new SolidColorBrush(Colors.White, 0.8) : p.Label2, wrap: false));
        Grid.SetColumn(lbl, 1); g.Children.Add(lbl);
        if (state == SetupStepState.Done && !plain) { var c = Mac.Icon("check", 13, cur ? Brushes.White : p.Label2, 2.2); Grid.SetColumn(c, 2); g.Children.Add(c); }
        return new Border { CornerRadius = new CornerRadius(6), Background = cur ? p.AccentBrush : Brushes.Transparent, Child = g };
    }

    /// <summary>Knaplinjen: ?-hjælp til venstre, Tilbage/handling til højre.</summary>
    private Control Foot(MacPal p)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto,Auto"), Margin = new Thickness(20, 0) };
        var help = new Button { Content = "?", Width = 22, Height = 22, MinHeight = 22, MinWidth = 22, Padding = new Thickness(0), CornerRadius = new CornerRadius(11), FontWeight = FontWeight.SemiBold, Foreground = p.Label2 };
        help.Click += async (_, _) =>
        {
            var (t, x) = SetupTexts.HelpFor(SetupOs.Mac, Flow.Step);
            await new ConfirmDialog(t, x, "OK", cancel: false).ShowDialog<bool>(this);
        };
        g.Children.Add(help);
        _primary = null;
        var page = Flow.Page(Status?.Machine, Status?.Profile);
        if (Flow.Screen == SetupScreen.Installed)
        {
            var remove = Mac.Link(SetupTexts.Uninstall, p.Red); remove.Margin = new Thickness(12, 0, 0, 0);
            remove.Click += async (_, _) => await Remove();
            Grid.SetColumn(remove, 1); g.Children.Add(remove);
            var test = Mac.Btn(SetupTexts.TestConnection); test.Click += async (_, _) => await InstalledTest();
            test.IsEnabled = !Busy;
            test.Margin = new Thickness(0, 0, 10, 0); Grid.SetColumn(test, 3); g.Children.Add(test);
            _primary = Mac.Btn(SetupTexts.SaveAndReconnect, def: true); _primary.Click += async (_, _) => await Connect();
            Grid.SetColumn(_primary, 4); g.Children.Add(_primary);
        }
        else
        {
            if (page.Secondary is { } sec)
            {
                var b = Mac.Btn(sec); b.Margin = new Thickness(0, 0, 10, 0); b.IsEnabled = !Busy;
                b.Click += (_, _) => Secondary();
                Grid.SetColumn(b, 3); g.Children.Add(b);
            }
            if (page.Primary is { } pri)
            {
                _primary = Mac.Btn(Busy && Flow.Screen != SetupScreen.Installing ? BusyLabel() : pri, def: true);
                _primary.Click += async (_, _) => await Primary();
                Grid.SetColumn(_primary, 4); g.Children.Add(_primary);
            }
        }
        return new Border { Height = 56, BorderBrush = p.Sep, BorderThickness = new Thickness(0, 0.5, 0, 0), Child = g };
    }

    private string BusyLabel() => Flow.Screen switch
    {
        SetupScreen.Request => "Sender…",
        SetupScreen.Profile => "Henter…",
        _ => "Tjekker…",
    };

    private void UpdateButtons()
    {
        if (_primary is null) return;
        var has = Discovery.Normalize(_server.Text ?? "").Length > 0;
        var key = EnrollKey.Parse(_key.Text).Key.Length > 0;
        _primary.IsEnabled = !Busy && (Flow.Screen == SetupScreen.Installed ? Ready()
            : Flow.Screen == SetupScreen.Enrolled ? Flow.Granted || DemoGranted == true
            : Flow.CanContinue(has, key, ProfileId is not null));
    }

    // ---------------------------------------------------------------- skærmene

    private IEnumerable<Control> Body(MacPal p)
    {
        var page = Flow.Page(Status?.Machine, Status?.Profile);
        Control HeroTile(bool compact = false) => Mac.Hero(p, Mac.Tile(page.Icon, page.Color, compact ? 46 : 52), page.Title, page.Text, compact: compact);
        switch (Flow.Screen)
        {
            case SetupScreen.Welcome:
            {
                var logo = new Border { CornerRadius = new CornerRadius(20), ClipToBounds = true, Child = Mac.Logo(88), BoxShadow = BoxShadows.Parse("0 6 16 0 #4D2563eb") };
                yield return Mac.Hero(p, logo, page.Title, page.Text, big: true);
                var feats = new StackPanel { Margin = new Thickness(18, 2), Spacing = 12 };
                foreach (var (icon, title, text) in SetupTexts.Features(SetupOs.Mac))
                {
                    var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
                    var i = Mac.Icon(icon, 22, p.AccentBrush); i.VerticalAlignment = VerticalAlignment.Top; i.Margin = new Thickness(0, 1, 12, 0); g.Children.Add(i);
                    var c = new StackPanel(); c.Children.Add(Mac.T(title, 13, p.Label, FontWeight.SemiBold)); c.Children.Add(Mac.T(text, 12, p.Label2));
                    Grid.SetColumn(c, 1); g.Children.Add(c);
                    feats.Children.Add(g);
                }
                yield return feats;
                var paste = Mac.Btn(SetupTexts.Paste, small: true);
                paste.Click += async (_, _) => await PasteLink();
                yield return Mac.Tip(p, "clip", SetupTexts.PasteTitle, SetupTexts.PasteText, paste);
                if (Pasted is { } pasted) yield return Mac.Msg(p, SetupTone.Ok, "Udfyldt fra udklipsholderen.", pasted);
                if (Note is { } n) yield return Mac.Msg(p, n.Tone, n.Text, n.Sub);
                break;
            }
            case SetupScreen.Searching or SetupScreen.Found or SetupScreen.NoServer:
            {
                yield return HeroTile();
                Control? again = null;
                if (Flow.Screen != SetupScreen.Searching)
                {
                    var l = Mac.Link(SetupTexts.SearchAgain, p.AccentBrush, "refresh", 12); l.Click += async (_, _) => await Search();
                    again = l;
                }
                yield return Mac.Sec(p, SetupTexts.OnNetwork, Flow.Screen == SetupScreen.Found ? again : null);
                yield return Flow.Screen switch
                {
                    SetupScreen.Searching => Mac.Group(p, [Centered(p, new Spinner(p.Label2), Mac.T(SetupTexts.SearchingRow, 13, p.Label2))]),
                    SetupScreen.Found => FoundList(p),
                    _ => NoServerBox(p),
                };
                yield return Mac.Sec(p, SetupTexts.OrTypeAddress);
                yield return Mac.FieldGroup(p, _server, false);
                yield return Mac.Note(p, SetupTexts.WhereIsAddress);
                break;
            }
            case SetupScreen.ServerError:
            {
                yield return HeroTile();
                yield return Mac.Sec(p, "Adresse");
                yield return Mac.FieldGroup(p, _server, true);
                if (Flow.ErrorFor(SetupField.Server) is { } e)
                {
                    if (SetupProbe.IsCertificateText(e)) yield return Mac.WarnBox(p, e, DetailsOpen, ToggleDetails);
                    else yield return Mac.Msg(p, SetupTone.Bad, e.Title, e.Text, e.Detail, DetailsOpen, ToggleDetails);
                }
                if (Flow.Found.Count > 0) { yield return Mac.Sec(p, SetupTexts.OnNetwork); yield return FoundList(p); }
                break;
            }
            case SetupScreen.Key or SetupScreen.KeyError or SetupScreen.Request:
            {
                yield return HeroTile();
                yield return Choices(p);
                if (Flow.Screen == SetupScreen.Request)
                {
                    var flow = new StackPanel { Spacing = 7, Margin = new Thickness(4, 14, 4, 0) };
                    for (var i = 0; i < SetupTexts.RequestFlow.Length; i++)
                    {
                        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
                        g.Children.Add(new Border
                        {
                            Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Background = p.AccentSoft, Margin = new Thickness(0, 0, 9, 0),
                            Child = new TextBlock { Text = (i + 1).ToString(), FontSize = 11, FontWeight = FontWeight.Bold, Foreground = p.AccentBrush, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                        });
                        var t = Mac.T(SetupTexts.RequestFlow[i], 12.5, p.Label); t.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(t, 1); g.Children.Add(t);
                        flow.Children.Add(g);
                    }
                    yield return flow;
                    yield return Mac.Sec(p, SetupTexts.MessageSection, light: SetupTexts.Optional);
                    yield return Mac.FieldGroup(p, _message, false);
                    if (Busy) yield return BusyMsg(p, "Installerer og sender anmodningen – macOS beder om din adgangskode…");
                }
                else
                {
                    yield return Mac.Sec(p, SetupTexts.KeySection);
                    var paste = Mac.Btn(SetupTexts.Paste, small: true); paste.Click += async (_, _) => await PasteKey();
                    yield return Mac.FieldGroup(p, _key, Flow.ErrorFor(SetupField.Key) is not null, paste);
                    if (Flow.KeyCheck is { Ok: true } ok) yield return Mac.Msg(p, SetupTone.Ok, ok.Title, ok.Text);
                    else if (Flow.ErrorFor(SetupField.Key) is { } e) yield return Mac.Msg(p, SetupTone.Bad, e.Title, e.Text, e.Detail, DetailsOpen, ToggleDetails);
                    else if (Busy) yield return BusyMsg(p, "Tjekker nøglen…");
                }
                if (Flow.ActionError is { } a) yield return Mac.Msg(p, SetupTone.Bad, a.Title, a.Text, a.Detail, DetailsOpen, ToggleDetails);
                break;
            }
            case SetupScreen.Waiting:
            {
                yield return HeroTile();
                yield return CodeTiles(p, Flow.Code ?? "…");
                var copy = Mac.Btn(SetupTexts.CopyCode, small: true, icon: "copy", iconColor: p.Label);
                copy.HorizontalAlignment = HorizontalAlignment.Center; copy.Margin = new Thickness(0, 0, 0, 16);
                copy.Click += async (_, _) => { if (Clipboard is { } cb) await cb.SetTextAsync(Flow.Code ?? ""); };
                yield return copy;
                var (t, s, tone) = SetupTexts.WaitingRow(SetupOs.Mac, Status?.AccessRequest);
                yield return Mac.Group(p,
                [
                    Mac.Row(p, tone == SetupTone.Warn ? Mac.Icon("warn", 16, p.Orange) : new Spinner(p.Label2), t, s),
                    Mac.Row(p, Mac.Icon("laptop", 18, p.Label2), "Du kan godt lukke vinduet", "Mac'en tilmelder sig selv, når anmodningen er godkendt."),
                ]);
                break;
            }
            case SetupScreen.Rejected:
            {
                yield return HeroTile();
                if (Flow.Reason is { Length: > 0 } r)
                {
                    yield return Mac.Sec(p, "Besked fra administratoren");
                    var row = Mac.Row(p, null, $"“{r}”", titleColor: p.Label2);
                    ((TextBlock)((StackPanel)row.Children[0]).Children[0]).FontStyle = FontStyle.Italic;
                    yield return Mac.Group(p, [row]);
                }
                yield return Mac.Note(p, "Send en ny anmodning med en besked, så administratoren kan se, hvilken Mac det er.");
                break;
            }
            case SetupScreen.Profile:
            {
                yield return HeroTile(compact: true);
                yield return ProfileList(p);
                yield return Mac.Sec(p, SetupTexts.ExtensionsSection);
                yield return ExtensionList(p, installed: false);
                if (Flow.ActionError is { } a) yield return Mac.Msg(p, SetupTone.Bad, a.Title, a.Text, a.Detail, DetailsOpen, ToggleDetails);
                break;
            }
            case SetupScreen.Installing or SetupScreen.NoConnection:
            {
                yield return HeroTile();
                var stuck = Flow.Screen == SetupScreen.NoConnection;
                yield return Mac.Bar(p, stuck ? 0.92 : (InstallAt + 0.5) / 4.0, stuck ? p.Orange : null);
                var steps = SetupTexts.InstallSteps(SetupOs.Mac);
                var rows = new List<Control>();
                for (var i = 0; i < steps.Count; i++)
                {
                    var (title, sub) = steps[i];
                    if (stuck && i == 3)
                    {
                        rows.Add(Mac.Row(p, Mac.Icon("warn", 16, p.Orange), "Ingen forbindelse efter 2 minutter",
                            "Agenten prøver selv igen. Tjek, at Mac'en er på samme netværk som serveren." + (Status?.Error is { Length: > 0 } err ? $" ({err})" : ""), minHeight: 36));
                        continue;
                    }
                    var done = stuck || i < InstallAt;
                    Control lead = done ? Mac.Icon("checkc", 16, p.Green) : i == InstallAt ? new Spinner(p.Label2) : Mac.Radio(p, false);
                    if (!done && i > InstallAt) lead.Opacity = 0.6;
                    rows.Add(Mac.Row(p, lead, title, i == InstallAt && !stuck ? sub : null, minHeight: 36, titleColor: !done && i > InstallAt ? p.Label3 : null));
                }
                yield return Mac.Group(p, rows);
                break;
            }
            case SetupScreen.Enrolled:
            {
                var pill = new Border
                {
                    CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 4), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 12),
                    Background = new SolidColorBrush(((ISolidColorBrush)p.Green).Color, 0.14),
                };
                var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                sp.Children.Add(Mac.Icon("checkc", 14, p.Green)); sp.Children.Add(Mac.T(SetupTexts.EnrolledAs(Status?.Machine, Status?.Profile), 12, p.Label, wrap: false));
                pill.Child = sp;
                yield return pill;
                yield return HeroTile();
                yield return Access(p);
                break;
            }
            case SetupScreen.Done:
            {
                var check = Mac.Icon("checkc", 72, p.Green);
                yield return Mac.Hero(p, check, page.Title, page.Text, big: true);
                var fda = Status?.MacAccess?.FullDiskAccess == true || Flow.Granted || DemoGranted == true;
                yield return Mac.Group(p,
                [
                    Mac.ValueRow(p, "Server", Flow.ServerName() ?? Status?.Server ?? "–"),
                    Mac.ValueRow(p, "Mac'ens navn", Status?.Machine is { Length: > 0 } m ? m : "–"),
                    Mac.ValueRow(p, "Profil", MacSetup.ProfileLabel(Status?.Profile)),
                    Mac.ValueRow(p, "Fuld diskadgang", fda ? "Slået til" : "Mangler – kan gøres fra menulinjen", fda ? Mac.Icon("checkc", 14, p.Green) : Mac.Icon("warn", 14, p.Orange)),
                ]);
                yield return MenuBarTip(p);
                break;
            }
            case SetupScreen.Installed:
                foreach (var c in InstalledBody(p)) yield return c;
                break;
        }
    }

    private static Control Centered(MacPal p, params Control[] items)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        foreach (var i in items) sp.Children.Add(i);
        return new Border { MinHeight = 96, Child = sp };
    }

    private Control BusyMsg(MacPal p, string text)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(2, 10, 2, 0) };
        sp.Children.Add(new Spinner(p.Label2, 14)); sp.Children.Add(Mac.T(text, 12, p.Label2));
        return sp;
    }

    private Control FoundList(MacPal p)
    {
        var sel = Discovery.Normalize(_server.Text ?? "");
        var rows = Flow.Found.Select(f =>
        {
            var on = string.Equals(Discovery.Normalize(f.Url), sel, StringComparison.OrdinalIgnoreCase);
            var row = Mac.Row(p, Mac.Tile("server", on ? SetupColors.Blue : SetupColors.Gray, 28), f.Name.Length > 0 ? f.Name : SetupTexts.HostPort(f.Url), SetupTexts.FoundLine(f),
                on ? Mac.Icon("check", 16, p.AccentBrush, 2.4) : null, bold: on);
            var name = f.Name.Length > 0 ? f.Name : SetupTexts.HostPort(f.Url);
            return Mac.Clickable(p, row, () => { _server.Text = f.Url; Rerender(); }, $"{name}, {SetupTexts.FoundLine(f)}", on, "srv:" + f.Url, 0);
        });
        return Mac.Group(p, rows, indent: 50);
    }

    private Control NoServerBox(MacPal p)
    {
        var sp = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(20, 18) };
        sp.Children.Add(Mac.Icon("wifix", 30, p.Label3));
        var t = Mac.T(SetupTexts.NoServerTitle, 13, p.Label, FontWeight.SemiBold); t.TextAlignment = TextAlignment.Center; sp.Children.Add(t);
        var x = Mac.T(SetupTexts.NoServerText, 12, p.Label2); x.TextAlignment = TextAlignment.Center; x.MaxWidth = 330; sp.Children.Add(x);
        var again = Mac.Btn(SetupTexts.SearchAgain, small: true); again.HorizontalAlignment = HorizontalAlignment.Center; again.Click += async (_, _) => await Search();
        sp.Children.Add(again);
        return Mac.Group(p, [sp]);
    }

    private Control Choices(MacPal p)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,*") };
        Control Card(bool ask, string icon, string color, string title, string sub)
        {
            var on = Flow.Ask == ask;
            var inner = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(10, 9) };
            var tile = Mac.Tile(icon, color, 26); tile.Margin = new Thickness(0, 0, 9, 0); inner.Children.Add(tile);
            var col = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            col.Children.Add(Mac.T(title, 13, p.Label, FontWeight.SemiBold)); col.Children.Add(Mac.T(sub, 11.5, p.Label2));
            Grid.SetColumn(col, 1); inner.Children.Add(col);
            var r = Mac.Radio(p, on); r.Margin = new Thickness(6, 0, 0, 0); Grid.SetColumn(r, 2); inner.Children.Add(r);
            var card = new Border
            {
                Background = p.Group, CornerRadius = new CornerRadius(10), Child = inner,
                BoxShadow = new BoxShadows(new BoxShadow { Spread = on ? 2 : 0.5, Color = on ? p.Accent : p.GroupLine }),
            };
            return Mac.Clickable(p, card, () => { if (Busy) return; Flow.ChooseAsk(ask); Rerender(); }, $"{title}. {sub}", on, "ask:" + ask, 10);
        }
        g.Children.Add(Card(false, "key", SetupColors.Gray, SetupTexts.HaveKey, SetupTexts.HaveKeySub));
        var a = Card(true, "plane", SetupColors.Teal, SetupTexts.AskAccess, SetupTexts.AskAccessSub); Grid.SetColumn(a, 2); g.Children.Add(a);
        return g;
    }

    private static Control CodeTiles(MacPal p, string code)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 12) };
        foreach (var ch in code)
        {
            if (ch == '-') { sp.Children.Add(new Border { Width = 10, Height = 2.5, CornerRadius = new CornerRadius(1), Background = p.Label3, VerticalAlignment = VerticalAlignment.Center }); continue; }
            sp.Children.Add(new Border
            {
                Width = 38, Height = 48, CornerRadius = new CornerRadius(8), Background = p.Group,
                BoxShadow = new BoxShadows(new BoxShadow { Spread = 0.5, Color = p.GroupLine }),
                Child = new TextBlock { Text = ch.ToString(), FontSize = 26, FontWeight = FontWeight.Bold, Foreground = p.Label, FontFamily = Mac.Mono,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            });
        }
        return sp;
    }

    private Control ProfileList(MacPal p)
    {
        var items = new List<(int Id, string Title, string Sub)> { (0, SetupTexts.NoProfile, SetupTexts.NoProfileSub) };
        items.AddRange(Profiles.Select(x => (x.Id, x.Name, SetupTexts.ProfileLine(x))));
        var rows = items.Select(i =>
        {
            var on = ProfileId == i.Id;
            var row = Mac.Row(p, Mac.Radio(p, on), i.Title, i.Sub, bold: on, minHeight: 36);
            return Mac.Clickable(p, row, () => { ProfileId = i.Id; Rerender(); }, $"{i.Title}. {i.Sub}", on, "prof:" + i.Id, 0);
        });
        return Mac.Group(p, rows, indent: 38);
    }

    private static (string Icon, string Color) ExtIcon(string id) => id switch
    {
        "fjernskrivebord" => ("remote", SetupColors.Blue),
        "homebrew" => ("mug", SetupColors.Orange),
        "mas" => ("appstore", SetupColors.Teal),
        "temperatur" => ("thermo", SetupColors.Orange),
        "diskhelbred" => ("disk", SetupColors.Blue),
        Fleetdeck.Shared.Extensions.UsbIpadId => ("ipad", SetupColors.Gray),
        _ => ("pkg", SetupColors.Gray),
    };

    private Control ExtensionList(MacPal p, bool installed)
    {
        var rows = new List<Control>();
        foreach (var r in _extRows)
        {
            var (icon, color) = ExtIcon(r.Id);
            var sw = Mac.Switch(installed ? r.Installed || r.Pending : ExtOn.GetValueOrDefault(r.Id));
            var row = r;
            if (installed)
            {
                sw.IsEnabled = r.CanInstall && !_extBusy && !_demo;
                sw.IsCheckedChanged += async (_, _) => { if (sw.IsChecked == true && row.CanInstall && !_extBusy) await ApplyExtension(row); };
            }
            else sw.IsCheckedChanged += (_, _) => ExtOn[row.Id] = sw.IsChecked == true;
            var grid = Mac.Row(p, Mac.Tile(icon, color, 28), r.Name, installed && r.Pending ? null : installed ? r.Note : r.Gives, sw, minHeight: 36);
            if (installed && r.Pending)
            {
                var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
                sp.Children.Add(new Spinner(p.Label2, 11)); sp.Children.Add(Mac.T("Installeres…", 11.5, p.Label2, wrap: false));
                ((StackPanel)grid.Children[1]).Children.Add(sp);
            }
            rows.Add(grid);
        }
        return Mac.Group(p, rows, indent: 50);
    }

    private AccessPanel Access(MacPal p)
    {
        var a = new AccessPanel(p, _demo, DemoGranted);
        a.Granted += () => { Flow.AccessGranted(); UpdateButtons(); };
        return a;
    }

    /// <summary>"Her bor Fleetdeck nu": en lille menulinje med ikonet fremhævet + hjælp, hvis ikonet ikke ses (notch).</summary>
    private Control MenuBarTip(MacPal p)
    {
        var strip = new Border
        {
            CornerRadius = new CornerRadius(7), Padding = new Thickness(10, 4), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            Background = MacPal.B(p.Dark ? "#33FFFFFF" : "#14000000"),
        };
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        var fd = new Border
        {
            Width = 26, Height = 20, CornerRadius = new CornerRadius(5), Background = MacPal.B("#47808080"),
            BoxShadow = new BoxShadows(new BoxShadow { Spread = 2, Color = p.Accent }), Child = new Border { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Child = Mac.Icon("menubar", 15, p.Label, 2) },
        };
        sp.Children.Add(fd);
        sp.Children.Add(Mac.Icon("wifix", 15, p.Label2, 2));
        sp.Children.Add(Mac.T(DateTime.Now.ToString("HH.mm"), 12, p.Label2, wrap: false));
        strip.Child = sp;
        var tip = Mac.Tip(p, "info", "Her bor Fleetdeck nu", "Klik på ikonet i menulinjen for status, opdateringer og hjælp.", strip);
        var box = new StackPanel();
        box.Children.Add(tip);
        var (t, x) = SetupTexts.TrayTip(SetupOs.Mac);
        var hint = Mac.Note(p, $"{t} {x}");
        hint.Margin = new Thickness(4, 8, 4, 0);
        box.Children.Add(hint);
        // den lange forklaring (notch, Systemindstillinger → Menulinje, Spotlight) som tekst, der kan foldes ud – ikke kun som tooltip
        var more = Mac.Link("Sådan finder du ikonet", p.Label2, DetailsOpen ? "chevd" : "chev", 12);
        more.HorizontalAlignment = HorizontalAlignment.Left; more.Margin = new Thickness(2, 4, 0, 0);
        more.Click += (_, _) => ToggleDetails();
        box.Children.Add(more);
        if (DetailsOpen) { var full = Mac.Note(p, MacSetup.MenuBarHint); full.Margin = new Thickness(4, 4, 4, 0); box.Children.Add(full); }
        return box;
    }

    // ---------------------------------------------------------------- installeret: oversigten

    private IEnumerable<Control> InstalledBody(MacPal p)
    {
        switch (Section)
        {
            case "overview":
            {
                var (head, kind) = MacSetup.Headline(Status, DateTime.UtcNow);
                var card = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(14, 12) };
                var lap = Mac.Icon("laptop", 44, p.Label2, 1.3); lap.Margin = new Thickness(0, 0, 12, 0); card.Children.Add(lap);
                var col = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
                col.Children.Add(Mac.T(Status?.Machine is { Length: > 0 } m ? m : "Denne Mac", 15, p.Label, FontWeight.SemiBold));
                var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
                line.Children.Add(Mac.Dot(kind switch { 'o' => p.Green, 'w' => p.Orange, _ => p.Red }));
                line.Children.Add(Mac.T(kind == 'o' && Status?.Server is { Length: > 0 } sv ? $"{head} til {SetupTexts.HostPort(sv)}" : head, 12, p.Label2, wrap: false));
                col.Children.Add(line);
                Grid.SetColumn(col, 1); card.Children.Add(col);
                yield return Mac.Group(p, [card]);
                yield return Mac.Sec(p, "Status");
                yield return Mac.Group(p, MacSetup.Summary(Status, DateTime.UtcNow).Select(r => (Control)Mac.ValueRow(p, r.Label, r.Value)));
                if (Status?.Job is { } j)
                    yield return Mac.Msg(p, SetupTone.Info, $"I gang: {j.Title}{(j.Percent is int pc ? $" – {pc} %" : "")}", j.Step);
                if (TrayView.NeedsFullDiskAccess(Status))
                {
                    var open = Mac.Link("Giv Fuld diskadgang…", p.AccentBrush, "chev", 12); open.Margin = new Thickness(2, 8, 0, 0);
                    open.Click += (_, _) => { Section = "access"; Rerender(); };
                    yield return open;
                }
                yield return Mac.Sec(p, SetupTexts.DiagnoseSection);
                var diag = Mac.Btn(SetupTexts.SaveDiagnose, small: true, icon: "pulse");
                diag.HorizontalAlignment = HorizontalAlignment.Left; diag.IsEnabled = !Busy;
                diag.Click += async (_, _) => await SaveDiagnose();
                yield return diag;
                yield return Mac.Note(p, SetupTexts.DiagnoseNote);
                break;
            }
            case "server":
            {
                yield return Mac.Sec(p, "Adresse");
                yield return Mac.FieldGroup(p, _server, Flow.ErrorFor(SetupField.Server) is not null);
                if (Flow.ErrorFor(SetupField.Server) is { } se) yield return Mac.Msg(p, SetupTone.Bad, se.Title, se.Text, se.Detail, DetailsOpen, ToggleDetails);
                yield return Mac.Sec(p, SetupTexts.KeySection);
                var paste = Mac.Btn(SetupTexts.Paste, small: true); paste.Click += async (_, _) => await PasteKey();
                _key.PlaceholderText = MacSetup.IsEnrolled(Status) ? "Nøgle gemt ✓" : SetupTexts.KeyPlaceholder;
                yield return Mac.FieldGroup(p, _key, Flow.ErrorFor(SetupField.Key) is not null, paste);
                if (Flow.KeyCheck is { Ok: true } ok) yield return Mac.Msg(p, SetupTone.Ok, ok.Title, ok.Text + " Vælg den nye profil under Profil – eller tryk \"Gem og forbind igen\" ved ny server.");
                else if (Flow.ErrorFor(SetupField.Key) is { } ke) yield return Mac.Msg(p, SetupTone.Bad, ke.Title, ke.Text, ke.Detail, DetailsOpen, ToggleDetails);
                else yield return Mac.Note(p, KeyHint());
                break;
            }
            case "profile":
            {
                yield return Mac.Sec(p, "Nuværende profil");
                yield return Mac.Group(p, [Mac.ValueRow(p, "Profil", MacSetup.ProfileLabel(Status?.Profile))]);
                if (_profilesFor.Length > 0)
                {
                    yield return Mac.Sec(p, "Skift til");
                    yield return ProfileList(p);
                    if (ProfileId is not null && !Ready() && !MacSetup.ServerChanged(Status?.Server, _server.Text))
                        yield return Mac.Msg(p, SetupTone.Info, "Det er den nuværende profil og server – der er intet at ændre.");
                }
                else yield return Mac.Note(p, "Skift profil: indsæt tilmeldingsnøglen under Server og tilmelding, og tryk \"Test forbindelse\".");
                break;
            }
            case "extensions":
                yield return Mac.Sec(p, SetupTexts.ExtensionsSection);
                yield return ExtensionList(p, installed: true);
                yield return Mac.Note(p, "En udvidelse, du slår til, installeres med det samme. Mac'en beder om din adgangskode.");
                if (Status?.Job is { } j2) yield return Mac.Msg(p, SetupTone.Info, $"I gang: {j2.Title}{(j2.Percent is int pc2 ? $" – {pc2} %" : "")}", j2.Step);
                break;
            case "access":
                yield return Mac.Hero(p, Mac.Tile("hand", SetupColors.Indigo, 46), "Fuld diskadgang", "Så kan Fleetdeck se og opdatere alle programmer. macOS kræver, at du selv slår det til.", compact: true);
                yield return Access(p);
                yield return Mac.Sec(p, "macOS-opdateringer");
                yield return Mac.Note(p, "Fleetdeck henter macOS-opdateringer selv. På Mac'er med Apple silicon kræver macOS, at ejeren bekræfter installationen: " +
                                         "Fleetdeck åbner Softwareopdatering, og du trykker \"Genstart nu\" og skriver din adgangskode.");
                break;
        }
        if (Note is { } n) yield return Mac.Msg(p, n.Tone, n.Text, n.Sub);
        if (Busy) yield return BusyMsg(p, "Et øjeblik…");
    }

    // ================================================================ handlinger

    private async Task Primary()
    {
        if (Busy) return;
        switch (Flow.Screen)
        {
            case SetupScreen.Welcome:
                Note = null;
                if (Discovery.Normalize(_server.Text ?? "").Length > 0 && EnrollKey.Parse(_key.Text).Key.Length > 0) { await FastPath(); return; }
                Flow.Start(); Rerender();
                await Search();
                break;
            case SetupScreen.Searching or SetupScreen.Found or SetupScreen.NoServer or SetupScreen.ServerError:
                await TestServer();
                break;
            case SetupScreen.Key or SetupScreen.KeyError:
                await TestKeyAndLoad();
                break;
            case SetupScreen.Request:
                await SendRequest();
                break;
            case SetupScreen.Waiting:
                Close();
                break;
            case SetupScreen.Rejected:
                Flow.AskAgain(); Rerender();
                break;
            case SetupScreen.Profile:
                await Connect();
                break;
            case SetupScreen.NoConnection:
                Flow.InstallStarted(); InstallAt = 3; Rerender();
                await Verify();
                break;
            case SetupScreen.Enrolled:
                Flow.Finish(); Rerender();
                break;
            case SetupScreen.Done:
                Close();
                break;
        }
    }

    private void Secondary()
    {
        switch (Flow.Screen)
        {
            case SetupScreen.Rejected or SetupScreen.NoConnection: Close(); break;
            case SetupScreen.Enrolled: Flow.Finish(); Rerender(); break;
            default: Flow.Back(); Rerender(); break;
        }
    }

    /// <summary>Link/kommando indsat på velkomstskærmen: server, nøgle og profil – direkte til Profil og tilvalg, hvis alt passer.</summary>
    private async Task FastPath()
    {
        Busy = true; Rerender();
        var url = Discovery.Normalize(_server.Text ?? ""); var k = EnrollKey.Parse(_key.Text).Key;
        Flow.Server = url;
        var c = await SetupProbe.Test(Http, url, k, SetupOs.Mac);
        Busy = false;
        Flow.Start();
        Flow.ServerTested(c.Field == SetupField.Server ? c : SetupCheck.Good("Forbindelse OK.", url, c.Version));
        if (c.Field != SetupField.Server) { Flow.KeyTested(c); if (c.Ok) await LoadProfilesAndGo(url, k); }
        Rerender();
    }

    private async Task PasteLink()
    {
        var x = (Clipboard is { } cb ? await cb.TryGetTextAsync() : null)?.Trim() ?? "";
        if (FillFromCommand(x))
        {
            Pasted = $"Server {Discovery.Normalize(_server.Text ?? "")}, nøgle" + (_wantedProfile is int pr ? $" og profil {pr}" : "") + ". Tryk Fortsæt.";
            Note = null;
        }
        else { Pasted = null; Note = ("Der er intet Fleetdeck-link i udklipsholderen.", SetupTone.Warn, "Kopiér kommandoen eller linket fra Fleetdeck under Tilføj maskine, og prøv igen."); }
        Rerender();
    }

    private async Task PasteKey()
    {
        var x = (Clipboard is { } cb ? await cb.TryGetTextAsync() : null)?.Trim() ?? "";
        if (!FillFromCommand(x)) _key.Text = x;
        if (!Flow.Installed && Flow.Screen is SetupScreen.Key or SetupScreen.KeyError && EnrollKey.Parse(_key.Text).Key.Length > 0) await TestKeyOnly();
    }

    private static bool Has(string? t) => t is not null && (t.Contains("key=") || t.Contains("#k="));

    private bool FillFromCommand(string text)
    {
        var (srv, k) = EnrollKey.Parse(text);
        if (k == text.Trim() && srv is null) return false;
        if (srv is not null && _server.Text != srv) _server.Text = srv;
        if (_key.Text != k) _key.Text = k;
        if (Regex.Match(text, @"[?&]profile=(\d+)") is { Success: true } pm) _wantedProfile = int.Parse(pm.Groups[1].Value);
        return true;
    }

    private async Task Search()
    {
        Flow.SearchStarted(); Rerender();
        var list = await Discovery.Search(TimeSpan.FromSeconds(2.5));
        Flow.SearchDone(list);
        if (_server.Text is not { Length: > 0 } && Flow.Server.Length > 0) _server.Text = Flow.Server;
        Rerender();
    }

    private async Task TestServer()
    {
        var url = Discovery.Normalize(_server.Text ?? "");
        Flow.Server = url;
        Busy = true; Rerender();
        var c = await SetupProbe.Test(Http, url, null, SetupOs.Mac);
        Busy = false;
        Flow.ServerTested(c);
        Rerender();
    }

    private async Task TestKeyOnly()
    {
        var url = Discovery.Normalize(_server.Text ?? ""); var k = EnrollKey.Parse(_key.Text).Key;
        Busy = true; Rerender();
        var c = await SetupProbe.Test(Http, url, k, SetupOs.Mac);
        Busy = false;
        Flow.KeyTested(c);
        Rerender();
    }

    private async Task TestKeyAndLoad()
    {
        var url = Discovery.Normalize(_server.Text ?? ""); var k = EnrollKey.Parse(_key.Text).Key;
        if (k.Length == 0) { Flow.KeyTested(SetupCheck.Bad(SetupField.Key, "Indsæt tilmeldingsnøglen.", SetupTexts.HaveKeySub + ".")); Rerender(); return; }
        Busy = true; Rerender();
        if (Flow.KeyCheck is not { Ok: true }) Flow.KeyTested(await SetupProbe.Test(Http, url, k, SetupOs.Mac));
        if (Flow.KeyCheck is { Ok: true }) await LoadProfilesAndGo(url, k);
        Busy = false;
        Rerender();
    }

    private async Task LoadProfilesAndGo(string url, string k)
    {
        if (await LoadProfiles(url, k)) Flow.ProfilesLoaded();
        else { Flow.ProfilesFailed(); Flow.KeyTested(Flow.KeyCheck!); }
    }

    private async Task<bool> LoadProfiles(string url, string k)
    {
        if (_profilesFor == $"{url}|{k}") return true;
        try
        {
            Profiles = await MacSys.Profiles(url, k);
            var same = Flow.Installed && Status is not null && !MacSetup.ServerChanged(Status.Server, url);
            ProfileId = Profiles.FirstOrDefault(x => x.Id == _wantedProfile)?.Id
                        ?? (same ? Profiles.FirstOrDefault(x => x.Name == Status!.Profile)?.Id ?? (Status!.Profile is { Length: > 0 } ? null : 0) : null);
            _profilesFor = $"{url}|{k}";
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or UriFormatException or InvalidOperationException or System.Text.Json.JsonException) { return false; }
    }

    private string? ProfileName(int? id) => id is null or 0 ? null : Profiles.FirstOrDefault(x => x.Id == id)?.Name;

    // ---------------------------------------------------------------- installér og forbind

    private string Current() => $"{Discovery.Normalize(_server.Text ?? "")}|{EnrollKey.Parse(_key.Text).Key}";
    private bool ProfileChange() => ProfileId is not null && MacSetup.ProfileChanged(Status?.Profile, ProfileName(ProfileId));

    /// <summary>Installeret: "Gem og forbind igen" kræver en testet nøgle, en valgt profil – og at der er noget at ændre (server eller profil).</summary>
    private bool Ready() => _profilesFor.Length > 0 && _profilesFor == Current() && ProfileId is not null && MacSetup.NeedsKey(Status, _server.Text, ProfileChange());

    private string KeyHint() =>
        !MacSetup.NeedsKey(Status, _server.Text, ProfileChange()) ? "Mac'en er tilmeldt – nøglen skal kun bruges, hvis du skifter server eller profil."
        : MacSetup.IsEnrolled(Status) ? "Du skifter server eller profil: indsæt tilmeldingsnøglen fra Fleetdeck, og tryk \"Test forbindelse\"."
        : "Agenten er ikke tilmeldt endnu: indsæt tilmeldingsnøglen, og tryk \"Test forbindelse\".";

    /// <summary>Installeret: test server og nøgle, og hent profilerne (så kan der skiftes profil eller server).</summary>
    private async Task InstalledTest()
    {
        var url = Discovery.Normalize(_server.Text ?? ""); var k = EnrollKey.Parse(_key.Text).Key;
        Note = null;
        if (k.Length == 0)
        {
            Section = "server";
            Flow.KeyTested(SetupCheck.Bad(SetupField.Key, "Indsæt tilmeldingsnøglen.", "Nøglen skal kun bruges, hvis du skifter server eller profil."));
            Rerender(); return;
        }
        Busy = true; Rerender();
        var c = await SetupProbe.Test(Http, url, k, SetupOs.Mac);
        if (c.Ok && !await LoadProfiles(url, k)) c = SetupCheck.Bad(SetupField.Key, "Profilerne kunne ikke hentes.", "Forbindelsen virker, men profilerne kunne ikke hentes. Prøv igen.");
        Busy = false;
        Flow.KeyTested(c);   // installeret: Flow bliver i oversigten, fejlen står ved feltet
        Section = c.Ok ? "profile" : "server";
        Rerender();
    }

    private async Task Connect()
    {
        if (Busy || ProfileId is not int pid) return;
        if (Flow.Installed && !Ready()) return;
        var url = Discovery.Normalize(_server.Text ?? ""); var k = EnrollKey.Parse(_key.Text).Key;
        Flow.InstallStarted(); InstallAt = 0; Rerender();
        string agent, sha;
        try { (agent, sha) = await MacSys.FetchAgent(url); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException or IOException)
        {
            Flow.InstallFinished(false, error: SetupCheck.Bad(SetupField.None, "Kunne ikke hente agenten.", e.Message)); Rerender(); return;
        }
        InstallAt = 2; Rerender();
        var on = ExtOn.Where(e => e.Value).Select(e => e.Key);
        // installeret: kun nye udvidelser (ikke serverens standardudvidelser igen)
        var script = Flow.Installed ? MacInstall.InstallScript(agent, sha, url, k, pid, MacSetup.ToInstall(_extRows, []), MacSys.AppBundle, exactExtensions: true)
                                    : MacInstall.InstallScript(agent, sha, url, k, pid, on, MacSys.AppBundle);
        var (code, output) = await MacSys.Elevated(script, "Fleetdeck installerer sin tjeneste, så Mac'en kan holdes opdateret.");
        try { Directory.Delete(Path.GetDirectoryName(agent)!, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        if (code != 0)
        {
            var why = MacInstall.Explain(code, output);
            Flow.InstallFinished(false, error: code == 3 ? SetupCheck.Bad(SetupField.Key, "Nøglen blev afvist.", why) : SetupCheck.Bad(SetupField.None, "Installationen blev ikke gennemført.", why));
            Rerender(); return;
        }
        InstallAt = 3; Rerender();
        await Verify();
    }

    /// <summary>Vent på, at tjenesten melder sig forbundet (status.json) – som "verify" i Windows.</summary>
    private async Task Verify()
    {
        var until = DateTime.UtcNow.AddMinutes(2);
        var start = DateTime.UtcNow.AddSeconds(-5);
        TrayStatus? s = null;
        while (DateTime.UtcNow < until)
        {
            s = MacSys.ReadStatus();
            if (s is { Connected: true } && s.Written > start) break;
            await Task.Delay(2000);
        }
        if (s is not null) Status = s;
        if (s is { Connected: true }) _installed();
        Flow.InstallFinished(s is { Connected: true });
        if (s?.MacAccess?.FullDiskAccess == true) { Flow.AccessGranted(); Flow.Finish(); }
        Rerender();
    }

    // ---------------------------------------------------------------- "Anmod om adgang"

    private async Task SendRequest()
    {
        var url = Discovery.Normalize(_server.Text ?? "");
        Busy = true; Rerender();
        string agent, sha;
        try { (agent, sha) = await MacSys.FetchAgent(url); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException or IOException)
        {
            Busy = false; Flow.ActionFailed(SetupCheck.Bad(SetupField.None, "Kunne ikke hente agenten.", e.Message)); Rerender(); return;
        }
        var on = ExtOn.Where(e => e.Value).Select(e => e.Key);
        var sentAt = DateTime.UtcNow;
        var (code, output) = await MacSys.Elevated(MacInstall.RequestScript(agent, sha, url, _message.Text, on, MacSys.AppBundle),
            "Fleetdeck installerer sin tjeneste og beder administratoren om adgang.");
        try { Directory.Delete(Path.GetDirectoryName(agent)!, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        Busy = false;
        if (code != 0)
        {
            var why = code == 3 ? SetupProbe.LastLine(output) : MacInstall.Explain(code, output);
            Flow.ActionFailed(SetupCheck.Bad(SetupField.None, "Anmodningen blev ikke sendt.", why));
            Rerender(); return;
        }
        _installed();
        var shown = Regex.Match(output, "Kode: (" + AccessCode.Pattern + ")") is { Success: true } m ? m.Groups[1].Value : MacSys.ReadStatus()?.AccessRequest?.Code ?? "…";
        Flow.RequestSent(shown, sentAt);
        Rerender();
        _waitTimer.Start();
    }

    /// <summary>Hvert 2. sekund mens der ventes: godkendt → Tilladelser; afvist/udløbet → "Anmod igen".</summary>
    private void WaitTick()
    {
        var s = MacSys.ReadStatus();
        if (s is null) return;
        Status = s;
        var before = (Flow.Screen, Flow.Code, s.AccessRequest?.Text);
        Flow.RequestUpdate(s);
        if (Flow.Screen != SetupScreen.Waiting) _waitTimer.Stop();
        if (before != (Flow.Screen, Flow.Code, s.AccessRequest?.Text) || Flow.Screen != SetupScreen.Waiting) Rerender();
    }

    // ---------------------------------------------------------------- installeret: tilstand og udvidelser

    private List<MacSetup.ExtensionRow> Rows() =>
        MacSetup.ExtensionRows(Flow.Installed ? Status : null, Fleetdeck.Shared.Extensions.BuiltIn)
            .Select(r => _watch.ContainsKey(r.Id) && !r.Installed ? r with { Pending = true } : r).ToList();

    /// <summary>Hvert 3. sekund: læs tjenestens status igen (forbindelse, Fuld diskadgang, udvidelser, job i gang).</summary>
    private void Refresh()
    {
        if (!Flow.Installed || Flow.Screen != SetupScreen.Installed) return;
        Status = MacSys.ReadStatus();
        foreach (var (id, w) in _watch.ToList())
        {
            var x = Status?.Extensions.FirstOrDefault(e => e.Id == id);
            var name = x?.Name ?? id;
            if (x is { Installed: true }) { _watch.Remove(id); Note = ($"{name} er installeret.", SetupTone.Ok, null); }
            else if (x is { Pending: true }) { if (!w.Seen) _watch[id] = (w.Since, true); }
            else if (w.Seen) { _watch.Remove(id); Note = ($"{name} blev ikke installeret.", SetupTone.Bad, "Se jobbet i Fleetdeck."); }
            else if (DateTime.UtcNow - w.Since > TimeSpan.FromMinutes(3)) _watch.Remove(id);
        }
        if (!_extBusy) _extRows = Rows();
        // tegn kun igen, når noget synligt har ændret sig – og ikke mens der skrives i et felt (ellers mister man markøren)
        var sig = RefreshSignature();
        if (sig == _refreshSig || _server.IsFocused || _key.IsFocused || Busy) return;
        _refreshSig = sig;
        Rerender();
    }

    /// <summary>Det, oversigten viser ud fra status (uden tidsstemplerne, der skifter ved hver skrivning).</summary>
    private string RefreshSignature()
    {
        var now = DateTime.UtcNow;
        var node = Status is null ? null : System.Text.Json.JsonSerializer.SerializeToNode(Status) as System.Text.Json.Nodes.JsonObject;
        node?.Remove(nameof(TrayStatus.LastContact)); node?.Remove(nameof(TrayStatus.Written));
        return string.Join("\n", node?.ToJsonString() ?? "", MacSetup.Headline(Status, now).ToString(),
            string.Join("|", MacSetup.Summary(Status, now).Select(r => r.Label + "=" + r.Value)), Note?.ToString() ?? "", string.Join("|", _extRows));
    }

    /// <summary>Slå en udvidelse til på en installeret Mac: administrator-ordren "extension" (agenten beder serveren om den).</summary>
    private async Task ApplyExtension(MacSetup.ExtensionRow r)
    {
        _extBusy = true;
        Note = ($"Installerer {r.Name} – Mac'en beder om din adgangskode…", SetupTone.Info, null); Rerender();
        var (code, output) = await MacSys.Elevated(MacInstall.AdminCommandScript(new TrayCommand { Action = "extension", Id = r.Id }),
            $"Fleetdeck installerer udvidelsen {r.Name}.");
        _extBusy = false;
        if (code == 0) { _watch[r.Id] = (DateTime.UtcNow, false); Note = ($"{r.Name} er sendt til Fleetdeck og installeres om lidt.", SetupTone.Ok, "Følg med her."); }
        else Note = code == -128 || output.Contains("(-128)") ? ($"Afbrudt – {r.Name} blev ikke installeret.", SetupTone.Warn, null) : ("Kunne ikke sende ordren.", SetupTone.Bad, output.Trim());
        _extRows = Rows();
        Rerender();
    }

    /// <summary>"Gem diagnose…": filen lægges på skrivebordet og vises i Finder.</summary>
    private async Task SaveDiagnose()
    {
        if (_demo) { Note = ("Diagnosen er gemt på skrivebordet.", SetupTone.Ok, MacDiagnose.FileName(new DateTime(2026, 11, 21, 10, 30, 0))); Rerender(); return; }
        Busy = true; Rerender();
        try
        {
            var (path, error) = await Diagnose.Save();
            Note = path is not null
                ? ("Diagnosen er gemt på skrivebordet.", SetupTone.Ok, $"{Path.GetFileName(path)} – Finder viser filen, så du kan sende den.")
                : ("Diagnosen kunne ikke gemmes.", SetupTone.Bad, error);
        }
        finally { Busy = false; Rerender(); }
    }

    private async Task Remove()
    {
        var confirm = new ConfirmDialog("Afinstallér Fleetdeck?", "Agenten og tjenesten fjernes fra Mac'en, og den kan ikke længere styres fra Fleetdeck. " +
            "Maskinen bliver stående i Fleetdeck, til du sletter den dér.", "Afinstallér");
        if (!await confirm.ShowDialog<bool>(this)) return;
        Busy = true; Note = ("Fjerner Fleetdeck – Mac'en beder om din adgangskode…", SetupTone.Info, null); Rerender();
        var (code, output) = await MacSys.Elevated(MacInstall.UninstallScript(), "Fleetdeck fjerner sin tjeneste fra Mac'en.");
        Busy = false;
        if (code == 0)
        {
            _timer.Stop(); Status = null; _watch.Clear(); _profilesFor = ""; Profiles = []; ProfileId = null;
            Flow.Uninstalled(); _extRows = Rows();
            Note = ("Fleetdeck er fjernet fra Mac'en.", SetupTone.Ok, "Træk Fleetdeck.app fra Programmer til papirkurven, hvis du heller ikke vil have appen.");
        }
        else Note = code == -128 ? ("Afbrudt.", SetupTone.Warn, null) : ("Kunne ikke fjerne Fleetdeck.", SetupTone.Bad, output.Trim());
        Rerender();
    }

    /// <summary>Skærmbilleder: sæt udvidelsernes rækker fra en opdigtet status.</summary>
    internal void RefreshRows() => _extRows = Rows();
}

/// <summary>Lille ja/nej-vindue (også ?-hjælpen med kun "OK").</summary>
public sealed class ConfirmDialog : Window
{
    public ConfirmDialog(string title, string text, string yes, bool cancel = true)
    {
        var p = MacPal.Current;
        Title = "Fleetdeck"; Icon = Ui.AppIcon; Width = 420; SizeToContent = SizeToContent.Height; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = p.Pane;
        var sp = new StackPanel { Margin = new Thickness(22, 18), Spacing = 10 };
        var logo = Mac.Logo(48); logo.HorizontalAlignment = HorizontalAlignment.Left; sp.Children.Add(logo);
        sp.Children.Add(Mac.T(title, 15, p.Label, FontWeight.Bold));
        sp.Children.Add(Mac.T(text, 12.5, p.Label2));
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10, Margin = new Thickness(0, 6, 0, 0) };
        if (cancel) { var no = Mac.Btn("Annullér"); no.IsCancel = true; no.Click += (_, _) => Close(false); row.Children.Add(no); }
        var ok = Mac.Btn(yes, def: true); ok.Click += (_, _) => Close(true);
        row.Children.Add(ok);
        sp.Children.Add(row);
        Content = sp;
    }
}
