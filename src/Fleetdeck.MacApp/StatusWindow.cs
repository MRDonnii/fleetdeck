using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Fleetdeck.Shared;

namespace Fleetdeck.MacApp;

/// <summary>Statusvinduet (som StatusForm i Windows): maskine og forbindelse, nøgletal, hvad der kører, til/fra, Fuld diskadgang og udvidelser.</summary>
public sealed class StatusWindow : Window
{
    private readonly Tray _tray;
    private readonly TextBlock _title = Ui.Text("Fleetdeck"), _sub = Ui.Text("Forbinder…");
    private readonly TextBlock _kUp = Kpi(), _kAl = Kpi(), _kHe = Kpi();
    private readonly TextBlock _jobTitle = Ui.Text("Ingen opgaver kører"), _jobStep = Ui.Text("", 11.5, muted: true), _foot = Ui.Text("", 11.5, muted: true);
    private readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 100, Height = 6, IsVisible = false, Margin = new Thickness(0, 4, 0, 0) };
    private readonly ToggleSwitch _pause = Ui.Toggle(), _notify = Ui.Toggle(Settings.Current.Notifications), _auto = Ui.Toggle(Settings.Current.Autostart);
    private readonly TextBlock _tempLine = Ui.Text("", 12.5), _extLine = Ui.Text("", 11.5, muted: true);
    // "Hold vågen på strøm": alle må slå den til/fra; tjenesten bekræfter i status.json inden for få sekunder
    private readonly ToggleSwitch _keepAwake = Ui.Toggle();
    private readonly TextBlock _keepAwakeState = Ui.Text("", 11.5, muted: true);
    private readonly Border _keepAwakeCard;
    private (bool On, DateTime At)? _keepAwakeSent;
    private readonly Border _access;
    private bool _loading;

    private static TextBlock Kpi() => new() { Text = "–", FontSize = 22, FontWeight = FontWeight.Bold };

    public StatusWindow(Tray tray)
    {
        _tray = tray;
        Title = "Fleetdeck"; Icon = Ui.AppIcon; Width = 460; SizeToContent = SizeToContent.Height; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var root = new StackPanel { Margin = new Thickness(24, 18), Spacing = 10 };
        root.Children.Add(Ui.Hero(_title, _sub));

        // Fuld diskadgang mangler – ret det
        var fix = Ui.Button("Ret det…", primary: true);
        fix.Click += (_, _) => _tray.ShowAccess();
        _access = Ui.Card(Ui.Pair(new StackPanel { Children = { Ui.Text("Fuld diskadgang mangler", 13, weight: FontWeight.SemiBold), Ui.Text("Fleetdeck kan ikke tage iPad-backup eller se hele Mac'en.", 11.5, muted: true) } }, fix));
        _access.BorderBrush = Ui.Warn; _access.IsVisible = false;
        root.Children.Add(_access);

        var kpis = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*") };
        (TextBlock v, string l)[] k = [(_kUp, "opdateringer"), (_kAl, "alarmer"), (_kHe, "helbred")];
        for (var i = 0; i < 3; i++)
        {
            var c = Ui.Card(k[i].v, Ui.Text(k[i].l, 11.5, muted: true));
            c.Margin = new Thickness(i == 0 ? 0 : 5, 0, i == 2 ? 0 : 5, 0);
            Grid.SetColumn(c, i); kpis.Children.Add(c);
        }
        root.Children.Add(kpis);
        root.Children.Add(Ui.Card(Ui.Section("I gang nu"), _jobTitle, _jobStep, _bar));

        var admin = MacSys.IsAdmin;
        root.Children.Add(Ui.Card(
            Ui.Row("Sæt på pause i 1 time", admin ? "Ingen nye installationer, mens du arbejder (kræver din adgangskode)" : "Kræver en administrators adgangskode", _pause, divider: false),
            Ui.Row("Vis besked ved installationer", "Lille besked øverst til højre", _notify),
            Ui.Row("Vis Fleetdeck ved login", "Ikonet i menulinjen", _auto)));
        root.Children.Add(Ui.Text(MacSetup.MenuBarHint, 11, muted: true));
        _pause.IsCheckedChanged += async (_, _) =>
        {
            if (_loading) return;
            var on = _pause.IsChecked == true;
            if (!await _tray.SetPause(on)) { _loading = true; _pause.IsChecked = !on; _loading = false; }
        };
        _notify.IsCheckedChanged += (_, _) => { Settings.Current.Notifications = _notify.IsChecked == true; Settings.Current.Save(); };
        _auto.IsCheckedChanged += (_, _) => { Settings.Current.Autostart = _auto.IsChecked == true; Settings.Current.Save(); };

        _keepAwakeCard = Ui.Card(
            Ui.Row("Hold vågen på strøm (også med lukket låg)", "Fleetdeck kan bruge Mac'en, når den er sat til strøm. På batteri sover den altid normalt.", _keepAwake, divider: false),
            _keepAwakeState);
        _keepAwakeCard.IsVisible = false;
        root.Children.Add(_keepAwakeCard);
        _keepAwake.IsCheckedChanged += (_, _) =>
        {
            if (_loading) return;
            var on = _keepAwake.IsChecked == true;
            if (MacSys.UserCommand(new TrayCommand { Action = "keepawake", On = on })) { _keepAwakeSent = (on, DateTime.UtcNow); _keepAwakeState.Text = "Sendes til Fleetdeck-tjenesten…"; }
            else { _loading = true; _keepAwake.IsChecked = !on; _loading = false; _keepAwakeState.Text = "Kunne ikke sende ordren – kører Fleetdeck-tjenesten?"; }
        };

        // Funktioner (som udvidelserne i Windows): et kort overblik her, alt slås til/fra og installeres i vinduet "Funktioner"
        var features = Ui.Button("Funktioner…", primary: true);
        features.Click += (_, _) => _tray.ShowFeatures();
        root.Children.Add(Ui.Card(Ui.Section("Funktioner og hjælpeværktøjer"),
            Ui.Pair(new StackPanel { Spacing = 2, Children = { _tempLine, _extLine } }, features)));

        // Kontakt admin: altid her i vinduet (også når menulinjen er en ældre udgave uden "Skriv til support…")
        var contact = Ui.Button("Kontakt admin…");
        contact.Click += (_, _) => _tray.ShowCompose();
        root.Children.Add(Ui.Card(Ui.Pair(new StackPanel { Children = { Ui.Text("Brug for hjælp?", 13, weight: FontWeight.SemiBold),
            Ui.Text("Skriv til din administrator. Svaret kommer her på Mac'en.", 11.5, muted: true) } }, contact)));

        var open = Ui.Button("Åbn Fleetdeck", primary: true);
        open.Click += (_, _) => _tray.OpenWeb();
        _foot.VerticalAlignment = VerticalAlignment.Center;
        root.Children.Add(Ui.Pair(_foot, open));
        // på en lille skærm (fx en 13" MacBook) er vinduet højere end skærmen: så kan der rulles
        Content = new ScrollViewer { Content = root, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Opened += (_, _) => Ui.FitToScreen(this);
        Closing += (_, e) => { if (!_tray.Quitting) { e.Cancel = true; Hide(); } };
    }

    public void Show(TrayStatus? s)
    {
        _loading = true;
        var now = DateTime.UtcNow;
        _title.Text = s?.Machine is { Length: > 0 } m ? m : Environment.MachineName;
        _sub.Text = TrayView.Of(s, now) is TrayView.State.Job or TrayView.State.Power ? $"Forbundet{(s!.Profile is { Length: > 0 } p ? $" · profil {p}" : "")}" : TrayView.Headline(s, now);
        _access.IsVisible = TrayView.NeedsFullDiskAccess(s);
        if (s is null) { _loading = false; return; }
        _kUp.Text = s.Upgrades.ToString(); _kUp.Foreground = s.Upgrades > 0 ? Ui.Warn : null;
        _kAl.Text = s.Alerts.ToString(); _kAl.Foreground = s.Alerts > 0 ? Ui.Bad : null;
        _kHe.Text = TrayView.Health(s.Health);
        _kHe.Foreground = s.Health switch { "ok" => Ui.Ok, "warning" => Ui.Warn, "critical" => Ui.Bad, _ => null };
        if (s.Job is { } j) { _jobTitle.Text = j.Title; _jobStep.Text = j.Step; _bar.IsVisible = true; _bar.IsIndeterminate = j.Percent is null; _bar.Value = j.Percent ?? 0; }
        else if (s.PowerAt is { } pa && pa > now)
        { _jobTitle.Text = $"{(s.PowerMode == JobTypes.Reboot ? "Genstarter" : "Slukker")} kl. {pa.ToLocalTime():HH:mm}"; _jobStep.Text = "Planlagt fra Fleetdeck – gem dit arbejde"; _bar.IsVisible = false; }
        else { _jobTitle.Text = s.PausedUntil is { } u && u > now ? $"På pause til {u.ToLocalTime():HH:mm}" : "Ingen opgaver kører"; _jobStep.Text = ""; _bar.IsVisible = false; }
        _pause.IsChecked = s.PausedUntil > now;
        _keepAwakeCard.IsVisible = s.KeepAwake is not null;
        if (s.KeepAwake is { } ka)
        {
            // lige efter et klik: vis det valgte, til tjenesten har nået at tage ordren (højst 15 s)
            var waiting = _keepAwakeSent is { } sent && sent.On != ka.Setting && now - sent.At < TimeSpan.FromSeconds(15);
            if (!waiting) { _keepAwakeSent = null; _keepAwake.IsChecked = ka.Setting; _keepAwakeState.Text = MacKeepAwake.Text(ka) + (ka.ChangedFrom == "mac" ? " · ændret på Mac'en" : ""); }
        }
        _foot.Text = s.LastContact is { } lc ? $"Sidst i kontakt: {TrayView.Ago(lc, now)}" : s.Error ?? "";
        var (temp, tools) = MacFeatures.Summary(s, Fleetdeck.Shared.Extensions.BuiltIn);
        _tempLine.Text = temp; _extLine.Text = tools;
        _loading = false;
    }
}

/// <summary>"Om / Fejlfinding": versioner, forbindelse, Fuld diskadgang og signatur, agentens log – og knapperne, der hjælper.</summary>
public sealed class AboutWindow : Window
{
    private readonly TextBox _log = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, Height = 220, FontSize = 11, FontFamily = new FontFamily("Menlo, Monaco, monospace") };
    private readonly TextBlock _facts = Ui.Text("", 12);
    private readonly Pill _status = new();

    public AboutWindow(Tray tray)
    {
        Title = "Fleetdeck – Om og fejlfinding"; Icon = Ui.AppIcon; Width = 640; SizeToContent = SizeToContent.Height; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var root = new StackPanel { Margin = new Thickness(24, 18), Spacing = 10 };
        root.Children.Add(Ui.Hero(Ui.Text("Fleetdeck til Mac"), Ui.Text($"App {MacSys.Version} · {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture} · macOS {Environment.OSVersion.Version}")));
        root.Children.Add(Ui.Card(Ui.Section("Status"), _facts));
        root.Children.Add(Ui.Card(Ui.Section($"Agentens log ({MacPaths.Log})"), _log));
        var copy = Ui.Button("Kopiér fejlfindingsinfo");
        copy.Click += async (_, _) => { if (Clipboard is { } cb) { await cb.SetTextAsync(Report()); _status.Set("Kopieret – indsæt den i en mail eller besked.", 'o'); } };
        var console = Ui.Button("Åbn loggen i Konsol");
        console.Click += (_, _) => _ = MacSys.Run("/usr/bin/open", ["-a", "Console", MacPaths.Log]);
        var restart = Ui.Button("Genstart agenten");
        restart.Click += async (_, _) =>
        {
            var (code, outp) = await MacSys.Elevated(MacInstall.RestartScript(), "Fleetdeck genstarter sin tjeneste.");
            _status.Set(code == 0 ? "Agenten er genstartet." : code == -128 ? "Afbrudt." : $"Kunne ikke genstarte: {outp.Trim()}", code == 0 ? 'o' : 'b');
        };
        var access = Ui.Button("Fuld diskadgang…"); access.Click += (_, _) => tray.ShowAccess();
        var setup = Ui.Button("Skift server / afinstallér…"); setup.Click += (_, _) => tray.ShowSetup();
        var updates = Ui.Button("Softwareopdatering"); updates.Click += (_, _) => MacSys.Open(MacPaths.SoftwareUpdatePane);
        var row = new WrapPanel();
        foreach (var b in new[] { copy, console, restart, access, setup, updates }) { b.Margin = new Thickness(0, 0, 8, 6); row.Children.Add(b); }
        root.Children.Add(row);
        root.Children.Add(_status);
        root.Children.Add(Ui.Text("Første start: macOS kan ikke kontrollere appen, fordi den ikke er købt-signeret hos Apple. Åbn den med højreklik → Åbn, " +
                                  "eller Systemindstillinger → Anonymitet og sikkerhed → \"Åbn alligevel\". " +
                                  "macOS-opdateringer på Apple silicon skal bekræftes af ejeren i Softwareopdatering.", 11, muted: true));
        Content = root;
        Opened += (_, _) => Refresh();
    }

    public void Refresh()
    {
        var s = MacSys.ReadStatus();
        var now = DateTime.UtcNow;
        var a = s?.MacAccess;
        _facts.Text = string.Join('\n', new[]
        {
            $"Agent installeret: {(MacSys.Installed ? "ja" : "nej")}  ·  status skrevet: {(s is null ? "aldrig" : TrayView.Ago(s.Written, now))}",
            $"Maskine: {s?.Machine ?? Environment.MachineName}  ·  server: {s?.Server ?? "–"}",
            $"Forbindelse: {TrayView.Headline(s, now)}{(s?.Error is { Length: > 0 } e ? $" ({e})" : "")}",
            $"Agent-version: {(s?.Version is { Length: > 0 } v ? v : "–")}  ·  sidst i kontakt: {(s?.LastContact is { } lc ? TrayView.Ago(lc, now) : "–")}",
            $"Fuld diskadgang: {(a?.FullDiskAccess switch { true => "ja", false => "MANGLER", _ => "ukendt" })}  ·  signatur: {a?.Signature ?? "–"}",
        });
        _log.Text = MacSys.LogTail();
        _log.CaretIndex = _log.Text?.Length ?? 0;
    }

    private string Report() => $"Fleetdeck-app {MacSys.Version} på macOS {Environment.OSVersion.Version} ({System.Runtime.InteropServices.RuntimeInformation.OSArchitecture})\n{_facts.Text}\n\n--- log ---\n{_log.Text}";
}
