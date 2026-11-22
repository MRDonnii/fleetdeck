using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Fleetdeck.Shared;

namespace Fleetdeck.MacApp;

/// <summary>
/// "Funktioner" (som udvidelserne i Windows' statusvindue): temperaturmåling til/fra og hjælpeværktøjerne, der kan installeres.
/// Hver ændring kræver en administrators adgangskode og sendes til tjenesten som en administrator-ordre; tjenesten melder den til Fleetdeck.
/// </summary>
public sealed class FeaturesWindow : Window
{
    private readonly Tray _tray;
    private readonly StackPanel _measure = new() { Spacing = 0 }, _tools = new() { Spacing = 0 };
    private readonly Pill _result = new();
    private readonly Dictionary<string, (bool On, DateTime At)> _sent = [];
    private readonly Dictionary<string, DateTime> _lastPending = [];
    private TrayStatus? _st;
    private string _key = "";
    /// <summary>Venter på adgangskoden: rækken og det valgte (så kontakten ikke hopper tilbage imens).</summary>
    private (string Id, bool On)? _ask;

    public FeaturesWindow(Tray tray)
    {
        _tray = tray;
        Title = "Fleetdeck – Funktioner"; Icon = Ui.AppIcon; Width = 480; SizeToContent = SizeToContent.Height; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var root = new StackPanel { Margin = new Thickness(24, 18), Spacing = 10 };
        root.Children.Add(Ui.Hero(Ui.Text("Funktioner"), Ui.Text("Slå funktioner til og fra, og installér hjælpeværktøjer på denne Mac.")));
        root.Children.Add(Ui.Card(Ui.Section(MacFeatures.Measuring), _measure));
        root.Children.Add(Ui.Card(Ui.Section(MacFeatures.Tools), _tools));
        root.Children.Add(_result);
        root.Children.Add(Ui.Text("Hver ændring kræver en administrators adgangskode. Fleetdeck-tjenesten melder ændringen til Fleetdeck, " +
                                  "så maskinens side viser det samme. Installationer følges under maskinen → Jobs.", 11, muted: true));
        var close = Ui.Button("Luk");
        close.Click += (_, _) => Hide();
        close.HorizontalAlignment = HorizontalAlignment.Right;
        root.Children.Add(close);
        Content = new ScrollViewer { Content = root, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Opened += (_, _) => Ui.FitToScreen(this);
        Closing += (_, e) => { if (!_tray.Quitting) { e.Cancel = true; Hide(); } };
    }

    public void Show(TrayStatus? s)
    {
        _st = s;
        var now = DateTime.UtcNow;
        foreach (var e in s?.Extensions ?? []) if (e.Pending) _lastPending[e.Id] = now;
        var rows = MacFeatures.Rows(s, Fleetdeck.Shared.Extensions.BuiltIn, _sent, now);
        // færdige ændringer: én besked, og så glemmes de
        foreach (var r in rows)
        {
            if (!_sent.TryGetValue(r.Id, out var x)) continue;
            if (MacFeatures.Outcome(r, x, _lastPending.TryGetValue(r.Id, out var lp) ? lp : null, now) is not { } o) continue;
            _result.Set(o.Text, o.Tone);
            _sent.Remove(r.Id); _lastPending.Remove(r.Id);
        }
        var key = string.Join("|", rows.Select(r => $"{r.Id}{r.On}{r.Busy}{r.CanChange}{r.Sub}")) + _ask;
        if (key == _key) return;
        _key = key;
        _measure.Children.Clear(); _tools.Children.Clear();
        foreach (var r in rows)
        {
            var panel = r.Group == MacFeatures.Measuring ? _measure : _tools;
            panel.Children.Add(Ui.Row(r.Title, r.Sub, Right(r), divider: panel.Children.Count > 0));
        }
        if (_tools.Children.Count == 0) _tools.Children.Add(Ui.Text("Ingen hjælpeværktøjer til denne Mac.", 12, muted: true));
    }

    private Control Right(MacFeatures.Row r)
    {
        if (r.Kind == MacFeatures.Kind.Toggle)
        {
            var t = Ui.Toggle(_ask is { } a && a.Id == r.Id ? a.On : r.On);
            t.IsEnabled = r.CanChange && _ask is null;
            t.IsCheckedChanged += async (_, _) => await Toggle(r, t.IsChecked == true);
            return t;
        }
        if (r.On) return Ui.Text("✔ Installeret", 12, muted: true);
        if (r.Busy) return Ui.Text("Installeres…", 12, muted: true);
        var b = Ui.Button("Installér");
        b.IsEnabled = r.CanChange && _ask is null;
        b.Click += async (_, _) => await Install(r);
        return b;
    }

    private async Task Toggle(MacFeatures.Row r, bool on)
    {
        if (_ask is not null || on == r.On) return;
        _ask = (r.Id, on);
        _result.Set(on ? $"Slår {r.Title.ToLowerInvariant()} til – skriv en administrators adgangskode…" : $"Slår {r.Title.ToLowerInvariant()} fra – skriv en administrators adgangskode…", 'i');
        var ok = await _tray.SetFeature(r.Id, on);
        _ask = null;
        if (ok) { _sent[r.Id] = (on, DateTime.UtcNow); _result.Set("Sendt til Fleetdeck-tjenesten – venter på svar…", 'i'); }
        else _result.Set("Ikke ændret (annulleret, eller adgangskoden var ikke en administrators).", 'w');
        _key = "";
        Show(_st);
    }

    private async Task Install(MacFeatures.Row r)
    {
        if (_ask is not null) return;
        _ask = (r.Id, true);
        _result.Set($"Installerer {r.Title} – skriv en administrators adgangskode…", 'i');
        _key = ""; Show(_st);
        var ok = await _tray.InstallExtension(r.Id);
        _ask = null;
        if (ok) { _sent[r.Id] = (true, DateTime.UtcNow); _result.Set($"{r.Title} installeres – det kan tage nogle minutter. Følg med her.", 'i'); }
        else _result.Set("Ikke installeret (annulleret, eller adgangskoden var ikke en administrators).", 'w');
        _key = "";
        Show(_st);
    }
}
