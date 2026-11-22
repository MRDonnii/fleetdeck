using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Fleetdeck.Shared;

namespace Fleetdeck.MacApp;

/// <summary>
/// "Giv Fleetdeck Fuld diskadgang" (forslag B): tre nummererede trin i en grupperet liste – Åbn Systemindstillinger, ＋ og vælg agenten
/// (stien kan kopieres, agenten vises i Finder), slå kontakten til – og en levende linje, der holder øje med tjenestens status, til adgangen
/// ses (✓). Ser tjenesten den ikke, genstartes den (først af sig selv, ellers med administratoradgangskoden), for macOS giver først adgangen
/// til en ny proces. Bruges efter installationen, i oversigten og fra menulinjen ("Fuld diskadgang mangler – ret det").
/// </summary>
public sealed class AccessPanel : StackPanel
{
    private readonly MacPal _p;
    private readonly bool _demo;
    private readonly Border _state = new() { Margin = new Thickness(2, 12, 2, 0) };
    private readonly Button _check;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _busy, _granted;
    public event Action? Granted;

    public AccessPanel() : this(MacPal.Current) { }

    public AccessPanel(MacPal p, bool demo = false, bool? demoGranted = null)
    {
        _p = p; _demo = demo;
        var open = Mac.Btn("Åbn", small: true);
        open.Click += async (_, _) => { await Copy(MacPaths.AgentExe); MacSys.Open(MacPaths.FullDiskAccessPane); };
        var reveal = Mac.Btn("Vis i Finder", small: true);
        reveal.Click += (_, _) => MacSys.Reveal(MacPaths.AgentExe);

        var chip = Mac.Link(MacPaths.AgentExe, p.Label, "copy", 11);
        chip.FontFamily = Mac.Mono;
        var chipBox = new Border { CornerRadius = new CornerRadius(5), Padding = new Thickness(6, 2), Margin = new Thickness(0, 4, 0, 0), Background = p.Sep, Child = chip, HorizontalAlignment = HorizontalAlignment.Left };
        chip.Click += async (_, _) => { await Copy(MacPaths.AgentExe); Set(SetupTone.Info, $"Stien er kopieret: {MacPaths.AgentExe}"); };
        ToolTip.SetTip(chip, "Kopiér stien – indsæt den med ⌘ ⇧ G i vinduet, der åbner, når du trykker ＋.");

        var step2 = Mac.Row(p, Num("2"), "Tryk ＋ og vælg agenten", null, reveal);
        ((StackPanel)step2.Children[1]).Children.Add(chipBox);
        var illus = Mac.Switch(true); illus.IsHitTestVisible = false;
        Children.Add(Mac.Group(p,
        [
            Mac.Row(p, Num("1"), "Åbn Fuld diskadgang i Systemindstillinger", "Anonymitet og sikkerhed → Fuld diskadgang.", open),
            step2,
            Mac.Row(p, Num("3"), "Slå kontakten til ved fleetdeck-agent", "Lås op med din adgangskode eller Touch ID, hvis Mac'en spørger.", illus),
        ], indent: 46));
        Children.Add(_state);
        _check = Mac.Link("Jeg har slået det til – tjek igen", p.AccentBrush, "refresh", 12);
        _check.HorizontalAlignment = HorizontalAlignment.Center; _check.Margin = new Thickness(0, 8, 0, 0);
        _check.Click += async (_, _) => await Recheck();
        Children.Add(_check);
        if (demo)
        {
            if (demoGranted == true) Done(); else Set(SetupTone.Info, "Venter på adgang – Fleetdeck tjekker af sig selv.", spin: true);
            return;
        }
        _timer.Tick += (_, _) => Poll();
        AttachedToVisualTree += (_, _) => { Poll(); _timer.Start(); };
        DetachedFromVisualTree += (_, _) => _timer.Stop();
    }

    private Control Num(string n) => new Border
    {
        Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Background = _p.AccentSoft,
        Child = new TextBlock { Text = n, FontSize = 12, FontWeight = FontWeight.Bold, Foreground = _p.AccentBrush, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
    };

    /// <summary>Linjen under trinene: spinner mens der ventes, ✓ når adgangen er givet, ⚠/✕ ved problemer.</summary>
    private void Set(SetupTone tone, string text, bool spin = false)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, HorizontalAlignment = HorizontalAlignment.Center };
        sp.Children.Add(spin ? new Spinner(_p.Label2, 14) : Mac.Icon(tone switch { SetupTone.Ok => "checkc", SetupTone.Warn => "warn", SetupTone.Bad => "xc", _ => "info" }, 15, _p.Tone(tone)));
        var t = Mac.T(text, 12, tone == SetupTone.Ok ? _p.Label : _p.Label2); t.MaxWidth = 420; sp.Children.Add(t);
        _state.Child = sp;
    }

    private async Task Copy(string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } cb) await cb.SetTextAsync(text);
    }

    private void Poll()
    {
        if (_busy || _demo) return;
        var s = MacSys.ReadStatus();
        var fda = s?.MacAccess?.FullDiskAccess;
        if (fda == true) Done();
        else if (s is null || DateTime.UtcNow - s.Written > TrayView.Stale) Set(SetupTone.Info, "Venter på, at Fleetdeck-tjenesten starter…", spin: true);
        else if (fda == false) Set(SetupTone.Info, "Venter på adgang – Fleetdeck tjekker af sig selv.", spin: true);
        else Set(SetupTone.Info, "Venter på, at tjenesten tjekker adgangen…", spin: true);
    }

    private void Done()
    {
        Set(SetupTone.Ok, "Fuld diskadgang er givet. Fleetdeck kan nu tage iPad-backup og se hele Mac'en.");
        _check.IsVisible = false;
        if (_granted) return;
        _granted = true;
        Granted?.Invoke();
    }

    /// <summary>Bed tjenesten tjekke igen (den genstarter selv, hvis den ikke ser adgangen); hjælper det ikke, genstartes den med adgangskoden.</summary>
    private async Task Recheck()
    {
        if (_demo) return;
        _busy = true; _check.IsEnabled = false;
        try
        {
            Set(SetupTone.Info, "Tjekker adgangen…", spin: true);
            MacSys.UserCommand(new TrayCommand { Action = "recheck_access" });
            if (await WaitFor(TimeSpan.FromSeconds(15))) { Done(); return; }
            Set(SetupTone.Info, "Tjenesten skal genstartes for at få adgangen. Mac'en beder om din adgangskode…", spin: true);
            var (code, outp) = await MacSys.Elevated(MacInstall.RestartScript(), "Fleetdeck genstarter sin tjeneste, så den får Fuld diskadgang.");
            if (code != 0) { Set(code == -128 ? SetupTone.Warn : SetupTone.Bad, code == -128 ? "Genstart afbrudt." : $"Kunne ikke genstarte tjenesten: {outp.Trim()}"); return; }
            if (await WaitFor(TimeSpan.FromSeconds(45))) { Done(); return; }
            Set(SetupTone.Warn, "Fleetdeck ser stadig ikke Fuld diskadgang. Tjek, at fleetdeck-agent står i listen og er slået til – og prøv igen.");
        }
        finally { _busy = false; _check.IsEnabled = true; }
    }

    private static async Task<bool> WaitFor(TimeSpan max)
    {
        var until = DateTime.UtcNow + max;
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(1500);
            if (MacSys.ReadStatus()?.MacAccess?.FullDiskAccess == true) return true;
        }
        return false;
    }
}

/// <summary>Fuld diskadgang i sit eget vindue (fra menulinjen) – samme udtryk som opsætningsvinduet.</summary>
public sealed class AccessWindow : Window
{
    public AccessWindow()
    {
        var p = MacPal.Current;
        Title = "Fleetdeck – Fuld diskadgang"; Icon = Ui.AppIcon; Width = 540; SizeToContent = SizeToContent.Height; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = p.Pane;
        var sp = new StackPanel { Margin = new Thickness(32, 24, 32, 20) };
        sp.Children.Add(Mac.Hero(p, Mac.Tile("hand", SetupColors.Indigo, 52), "Giv Fleetdeck Fuld diskadgang",
            "Så kan Fleetdeck se og opdatere alle programmer og tage backup af iPads. macOS kræver, at du selv slår det til."));
        sp.Children.Add(new AccessPanel(p));
        var close = Mac.Btn("Luk", def: true); close.HorizontalAlignment = HorizontalAlignment.Right; close.Margin = new Thickness(0, 16, 0, 0);
        close.Click += (_, _) => Close();
        sp.Children.Add(close);
        Content = sp;
    }
}
