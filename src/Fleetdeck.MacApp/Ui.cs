using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Fleetdeck.MacApp;

/// <summary>Små byggeklodser til vinduerne (kort, tekst, rækker med til/fra, statuslinje) – samme udtryk som Windows-vinduerne.</summary>
public static class Ui
{
    public static readonly IBrush CardBg = new SolidColorBrush(Color.Parse("#14808080"));
    public static readonly IBrush CardBorder = new SolidColorBrush(Color.Parse("#30808080"));
    public static readonly IBrush Ok = new SolidColorBrush(Color.Parse("#16a34a"));
    public static readonly IBrush Warn = new SolidColorBrush(Color.Parse("#d97706"));
    public static readonly IBrush Bad = new SolidColorBrush(Color.Parse("#dc2626"));
    public static readonly IBrush Info = new SolidColorBrush(Color.Parse("#2563eb"));

    private static Bitmap? _logo;
    public static Bitmap Logo => _logo ??= new Bitmap(AssetLoader.Open(new Uri("avares://Fleetdeck/Assets/fleetdeck.png")));
    public static WindowIcon AppIcon => new(Logo);

    public static TextBlock Text(string text, double size = 13, bool muted = false, FontWeight weight = FontWeight.Normal) => new()
    {
        Text = text, FontSize = size, FontWeight = weight, Opacity = muted ? 0.7 : 1, TextWrapping = TextWrapping.Wrap,
    };

    public static TextBlock Section(string text) => new()
    {
        Text = text.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeight.SemiBold, Opacity = 0.6, Margin = new Thickness(0, 0, 0, 6), LetterSpacing = 0.5,
    };

    public static Border Card(params Control[] children)
    {
        var sp = new StackPanel { Spacing = 4 };
        foreach (var c in children) sp.Children.Add(c);
        return new Border { Background = CardBg, BorderBrush = CardBorder, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 12), Child = sp };
    }

    public static StackPanel CardBody(Border card) => (StackPanel)card.Child!;

    /// <summary>Overskrift med logo (som Hero i Windows).</summary>
    public static Control Hero(TextBlock title, TextBlock sub)
    {
        title.FontSize = 20; title.FontWeight = FontWeight.SemiBold;
        sub.Opacity = 0.75;
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 0, 0, 4) };
        g.Children.Add(new Image { Source = Logo, Width = 48, Height = 48, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center });
        var col = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        col.Children.Add(title); col.Children.Add(sub);
        Grid.SetColumn(col, 1); g.Children.Add(col);
        return g;
    }

    /// <summary>Række: titel + undertekst til venstre, kontrol til højre.</summary>
    public static Control Row(string title, string sub, Control right, bool divider = true)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, divider ? 8 : 0, 0, 0) };
        var col = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        col.Children.Add(Text(title, 13));
        if (sub.Length > 0) col.Children.Add(Text(sub, 11.5, muted: true));
        g.Children.Add(col);
        right.VerticalAlignment = VerticalAlignment.Center; right.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(right, 1); g.Children.Add(right);
        if (!divider) return g;
        var wrap = new StackPanel();
        wrap.Children.Add(new Border { Height = 1, Background = CardBorder });
        wrap.Children.Add(g);
        return wrap;
    }

    /// <summary>Højst skærmens højde (med indholdet i en ScrollViewer): på en lille skærm kommer nederste del ellers aldrig frem.</summary>
    public static void FitToScreen(Window w)
    {
        if ((w.Screens.ScreenFromVisual(w) ?? w.Screens.Primary) is not { } sc || sc.Scaling <= 0) return;
        w.MaxHeight = Math.Max(320, sc.WorkingArea.Height / sc.Scaling - 60);
    }

    public static ToggleSwitch Toggle(bool on = false) => new() { IsChecked = on, OnContent = "", OffContent = "", MinWidth = 0 };

    public static Button Button(string text, bool primary = false)
    {
        var b = new Button { Content = text, Padding = new Thickness(14, 6), HorizontalContentAlignment = HorizontalAlignment.Center };
        if (primary) b.Classes.Add("accent");
        return b;
    }

    public static Control Pair(Control left, Control right)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        g.Children.Add(left);
        right.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(right, 1); g.Children.Add(right);
        return g;
    }
}

/// <summary>Statuslinjen nederst i vinduerne ('i' info, 'o' ok, 'w' advarsel, 'b' fejl).</summary>
public sealed class Pill : Border
{
    private readonly TextBlock _text = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12.5 };
    public Pill()
    {
        CornerRadius = new CornerRadius(8); Padding = new Thickness(12, 8); IsVisible = false; Child = _text;
    }
    public void Set(string text, char kind)
    {
        var c = kind switch { 'o' => "#16a34a", 'w' => "#d97706", 'b' => "#dc2626", _ => "#2563eb" };
        Background = new SolidColorBrush(Color.Parse(c), 0.14);
        BorderBrush = new SolidColorBrush(Color.Parse(c), 0.5); BorderThickness = new Thickness(1);
        _text.Text = text; IsVisible = text.Length > 0;
    }
}
