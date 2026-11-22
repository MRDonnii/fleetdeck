using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Fleetdeck.Shared;

namespace Fleetdeck.MacApp;

/// <summary>
/// Farverne fra forslag B (macOS 14/15 i lyst og mørkt): etiketter i tre styrker, ruden, de grupperede lister, sidebjælken og kontrollerne.
/// Avalonia kan ikke bruge AppKit-kontroller, så alt tegnes, så det ligner – Fluent-temaet bliver liggende under og får macOS' farver.
/// </summary>
public sealed class MacPal
{
    public bool Dark { get; }
    public Color Accent { get; }
    public IBrush AccentBrush { get; }
    public IBrush Label { get; }
    public IBrush Label2 { get; }
    public IBrush Label3 { get; }
    public IBrush Sep { get; }
    public IBrush Pane { get; }
    public IBrush Group { get; }
    public Color GroupLine { get; }
    public IBrush Btn { get; }
    public IBrush BtnLine { get; }
    public IBrush Field { get; }
    public IBrush FieldLine { get; }
    public IBrush Green { get; }
    public IBrush Orange { get; }
    public IBrush Red { get; }
    public IBrush AccentSoft { get; }
    public Color Ring { get; }

    private MacPal(bool dark)
    {
        Dark = dark;
        Accent = Color.Parse(dark ? "#0a84ff" : "#007aff");
        AccentBrush = new SolidColorBrush(Accent);
        Label = B(dark ? "#E0FFFFFF" : "#D9000000");
        Label2 = B(dark ? "#8CFFFFFF" : "#8C000000");
        Label3 = B(dark ? "#47FFFFFF" : "#4D000000");
        Sep = B(dark ? "#14FFFFFF" : "#17000000");
        Pane = B(dark ? "#1f1f21" : "#f4f4f5");
        Group = B(dark ? "#0EFFFFFF" : "#ffffff");
        GroupLine = Color.Parse(dark ? "#10FFFFFF" : "#12000000");
        Btn = B(dark ? "#2BFFFFFF" : "#ffffff");
        BtnLine = B(dark ? "#14FFFFFF" : "#33000000");
        Field = B(dark ? "#10FFFFFF" : "#ffffff");
        FieldLine = B(dark ? "#24FFFFFF" : "#29000000");
        Green = B(dark ? "#32d74b" : "#28cd41");
        Orange = B(dark ? "#ff9f0a" : "#ff9500");
        Red = B(dark ? "#ff453a" : "#ff3b30");
        AccentSoft = new SolidColorBrush(Accent, dark ? 0.20 : 0.12);
        Ring = Color.FromArgb(dark ? (byte)140 : (byte)115, Accent.R, Accent.G, Accent.B);
    }

    private static readonly MacPal L = new(false), D = new(true);
    public static MacPal For(bool dark) => dark ? D : L;
    public static MacPal Current => For(Application.Current?.ActualThemeVariant == ThemeVariant.Dark);

    /// <summary>Sidebjælken: halvgennemsigtig over macOS' vibrancy – eller fast, hvis vinduet ikke kan være gennemsigtigt.</summary>
    public IBrush Sidebar(bool vibrancy) => B(vibrancy ? (Dark ? "#A828282E" : "#9EECECF0") : (Dark ? "#2a2a2e" : "#e9e9ed"));
    public IBrush SidebarLine => B(Dark ? "#73000000" : "#14000000");
    public IBrush Tone(SetupTone t) => t switch { SetupTone.Ok => Green, SetupTone.Warn => Orange, SetupTone.Bad => Red, _ => AccentBrush };

    public static IBrush B(string hex) => new SolidColorBrush(Color.Parse(hex));
}

/// <summary>Udseendet: Fluent-temaet med macOS' farver og former (knapper, felter, kontakter), lyst og mørkt efter macOS.</summary>
public static class MacStyle
{
    /// <summary>Lægges på appen én gang (App og skærmbilledværktøjet).</summary>
    public static void Apply(Application app)
    {
        app.Styles.Add(new FluentTheme());
        app.Resources.ThemeDictionaries[ThemeVariant.Light] = Resources(MacPal.For(false));
        app.Resources.ThemeDictionaries[ThemeVariant.Dark] = Resources(MacPal.For(true));
        foreach (var s in Styles()) app.Styles.Add(s);
    }

    private static ResourceDictionary Resources(MacPal p)
    {
        var accentHover = Shade(p.Accent, p.Dark ? 1.08 : 0.94);
        var accentPress = Shade(p.Accent, p.Dark ? 0.92 : 0.86);
        var press = MacPal.B(p.Dark ? "#40FFFFFF" : "#ececec");
        return new ResourceDictionary
        {
            ["SystemAccentColor"] = p.Accent,
            ["SystemAccentColorLight1"] = p.Accent, ["SystemAccentColorLight2"] = p.Accent, ["SystemAccentColorLight3"] = p.Accent,
            ["SystemAccentColorDark1"] = p.Accent, ["SystemAccentColorDark2"] = p.Accent, ["SystemAccentColorDark3"] = p.Accent,
            ["ButtonBackground"] = p.Btn, ["ButtonBackgroundPointerOver"] = p.Btn, ["ButtonBackgroundPressed"] = press, ["ButtonBackgroundDisabled"] = p.Btn,
            ["ButtonForeground"] = p.Label, ["ButtonForegroundPointerOver"] = p.Label, ["ButtonForegroundPressed"] = p.Label, ["ButtonForegroundDisabled"] = p.Label3,
            ["ButtonBorderBrush"] = p.BtnLine, ["ButtonBorderBrushPointerOver"] = p.BtnLine, ["ButtonBorderBrushPressed"] = p.BtnLine, ["ButtonBorderBrushDisabled"] = p.BtnLine,
            ["AccentButtonBackground"] = p.AccentBrush, ["AccentButtonBackgroundPointerOver"] = new SolidColorBrush(accentHover),
            ["AccentButtonBackgroundPressed"] = new SolidColorBrush(accentPress), ["AccentButtonBackgroundDisabled"] = p.Btn,
            ["AccentButtonForeground"] = Brushes.White, ["AccentButtonForegroundPointerOver"] = Brushes.White, ["AccentButtonForegroundPressed"] = Brushes.White,
            ["AccentButtonForegroundDisabled"] = p.Label3,
            ["AccentButtonBorderBrush"] = MacPal.B("#1F000000"), ["AccentButtonBorderBrushPointerOver"] = MacPal.B("#1F000000"),
            ["AccentButtonBorderBrushPressed"] = MacPal.B("#1F000000"), ["AccentButtonBorderBrushDisabled"] = p.BtnLine,
            ["TextControlBackground"] = p.Field, ["TextControlBackgroundPointerOver"] = p.Field, ["TextControlBackgroundFocused"] = p.Field,
            ["TextControlBorderBrush"] = p.FieldLine, ["TextControlBorderBrushPointerOver"] = p.FieldLine, ["TextControlBorderBrushFocused"] = p.AccentBrush,
            ["TextControlForeground"] = p.Label, ["TextControlForegroundFocused"] = p.Label, ["TextControlForegroundPointerOver"] = p.Label,
            ["TextControlPlaceholderForeground"] = p.Label3, ["TextControlPlaceholderForegroundFocused"] = p.Label3, ["TextControlPlaceholderForegroundPointerOver"] = p.Label3,
            ["TextControlBorderThemeThickness"] = new Thickness(0.5), ["TextControlBorderThemeThicknessFocused"] = new Thickness(1),
            ["ToggleSwitchFillOn"] = p.AccentBrush, ["ToggleSwitchFillOnPointerOver"] = p.AccentBrush, ["ToggleSwitchFillOnPressed"] = p.AccentBrush,
            ["ToggleSwitchStrokeOn"] = p.AccentBrush, ["ToggleSwitchStrokeOnPointerOver"] = p.AccentBrush,
            ["ToggleSwitchFillOff"] = MacPal.B(p.Dark ? "#24FFFFFF" : "#1A000000"), ["ToggleSwitchFillOffPointerOver"] = MacPal.B(p.Dark ? "#2EFFFFFF" : "#22000000"),
            ["ToggleSwitchStrokeOff"] = MacPal.B("#00000000"), ["ToggleSwitchStrokeOffPointerOver"] = MacPal.B("#00000000"),
            ["ToggleSwitchKnobFillOff"] = Brushes.White, ["ToggleSwitchKnobFillOffPointerOver"] = Brushes.White, ["ToggleSwitchKnobFillOn"] = Brushes.White,
            ["ToggleSwitchKnobFillOnPointerOver"] = Brushes.White,
        };
    }

    private static IEnumerable<IStyle> Styles()
    {
        yield return new Style(x => x.OfType<Button>())
        {
            Setters =
            {
                new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(6)), new Setter(TemplatedControl.PaddingProperty, new Thickness(13, 3)),
                new Setter(Layoutable.MinHeightProperty, 24d), new Setter(TemplatedControl.FontSizeProperty, 13d),
                new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(0.75)),
                new Setter(ContentControl.HorizontalContentAlignmentProperty, HorizontalAlignment.Center),
                new Setter(ContentControl.VerticalContentAlignmentProperty, VerticalAlignment.Center),
            },
        };
        yield return new Style(x => x.OfType<Button>().Class("accent"))
        {
            Setters = { new Setter(Layoutable.MinWidthProperty, 84d), new Setter(TemplatedControl.FontWeightProperty, FontWeight.Medium) },
        };
        yield return new Style(x => x.OfType<Button>().Class("sm"))
        {
            Setters =
            {
                new Setter(TemplatedControl.PaddingProperty, new Thickness(9, 1)), new Setter(Layoutable.MinHeightProperty, 20d),
                new Setter(TemplatedControl.FontSizeProperty, 12d), new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(5)),
            },
        };
        // links ("Søg igen", "Afinstallér …"): ingen flade, ingen kant – kun farven
        foreach (var state in new[] { "", ":pointerover", ":pressed", ":disabled" })
        {
            var st = state;
            yield return new Style(x => (st.Length == 0 ? x.OfType<Button>().Class("link") : x.OfType<Button>().Class("link").Class(st)).Template().Name("PART_ContentPresenter"))
            {
                Setters =
                {
                    new Setter(ContentPresenter.BackgroundProperty, Brushes.Transparent), new Setter(ContentPresenter.BorderBrushProperty, Brushes.Transparent),
                },
            };
        }
        yield return new Style(x => x.OfType<Button>().Class("link"))
        {
            Setters = { new Setter(TemplatedControl.PaddingProperty, new Thickness(0)), new Setter(Layoutable.MinHeightProperty, 0d), new Setter(TemplatedControl.BorderThicknessProperty, new Thickness(0)) },
        };
        yield return new Style(x => x.OfType<TextBox>())
        {
            Setters =
            {
                new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(6)), new Setter(Layoutable.MinHeightProperty, 26d),
                new Setter(TemplatedControl.PaddingProperty, new Thickness(8, 4)), new Setter(TemplatedControl.FontSizeProperty, 13d),
            },
        };
        yield return new Style(x => x.OfType<ToggleSwitch>())
        {
            Setters = { new Setter(Layoutable.MinWidthProperty, 0d), new Setter(ToggleSwitch.OnContentProperty, ""), new Setter(ToggleSwitch.OffContentProperty, "") },
        };
    }

    private static Color Shade(Color c, double f) =>
        Color.FromRgb((byte)Math.Clamp(c.R * f, 0, 255), (byte)Math.Clamp(c.G * f, 0, 255), (byte)Math.Clamp(c.B * f, 0, 255));
}

/// <summary>Byggeklodserne fra forslag B: ikoner, fliser, grupperede lister, rækker, felter, beskeder, spinner og fremdriftslinje.</summary>
public static class Mac
{
    /// <summary>Fast bredde (koden, stien, tekniske detaljer). Skærmbilledværktøjet sætter Linux' skrift.</summary>
    public static FontFamily Mono { get; set; } = new("Menlo, Monaco, monospace");

    // ---------------------------------------------------------------- ikoner (SVG-stierne fra SetupIcons)

    public static Control Icon(string name, double size, IBrush color, double stroke = 1.7)
    {
        var canvas = new Canvas { Width = 24, Height = 24 };
        foreach (var d in SetupIcons.Paths[name])
        {
            var fill = d.StartsWith("F:"); var white = d.StartsWith("W:");
            var data = Geometry.Parse(fill || white ? d[2..] : d);
            canvas.Children.Add(fill
                ? new Avalonia.Controls.Shapes.Path { Data = data, Fill = color }
                : new Avalonia.Controls.Shapes.Path { Data = data, Stroke = white ? Brushes.White : color, StrokeThickness = white ? 2 : stroke, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round });
        }
        return new Viewbox { Width = size, Height = size, Child = canvas, VerticalAlignment = VerticalAlignment.Center };
    }

    /// <summary>Farvet flise med hvidt ikon (sidebjælken 22, rækker 28, overskrifter 52).</summary>
    public static Border Tile(string icon, string color, double size = 22)
    {
        var c = Color.Parse(color);
        var top = Color.FromRgb((byte)(c.R + (255 - c.R) * 0.18), (byte)(c.G + (255 - c.G) * 0.18), (byte)(c.B + (255 - c.B) * 0.18));
        var radius = size >= 46 ? size / 4 : size >= 28 ? 7 : 6;
        return new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(radius),
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(top, 0), new GradientStop(c, 1) },
            },
            BoxShadow = size >= 46 ? BoxShadows.Parse("0 1 2 0 #26000000") : default,
            Child = new Border { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Child = Icon(icon, size * (size >= 46 ? 0.58 : 0.62), Brushes.White, size >= 46 ? 1.9 : 2) },
        };
    }

    public static Image Logo(double size) => new() { Source = Ui.Logo, Width = size, Height = size };

    // ---------------------------------------------------------------- tekst

    public static TextBlock T(string text, double size, IBrush color, FontWeight weight = FontWeight.Normal, bool wrap = true) => new()
    {
        Text = text, FontSize = size, Foreground = color, FontWeight = weight, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis, LineHeight = Math.Round(size * 1.36),
    };

    /// <summary>Overskriften midt på skærmen: stort ikon, titel og en kort tekst.</summary>
    public static Control Hero(MacPal p, Control icon, string title, string text, bool big = false, bool compact = false)
    {
        var s = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, compact ? 12 : 18) };
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        s.Children.Add(icon);
        var h = T(title, big ? 26 : 22, p.Label, FontWeight.Bold);
        h.TextAlignment = TextAlignment.Center; h.LetterSpacing = -0.3; h.Margin = new Thickness(0, big ? 16 : compact ? 9 : 12, 0, 5);
        s.Children.Add(h);
        var t = T(text, 13, p.Label2);
        t.TextAlignment = TextAlignment.Center; t.MaxWidth = 400; t.HorizontalAlignment = HorizontalAlignment.Center;
        s.Children.Add(t);
        return s;
    }

    /// <summary>Overskrift over en gruppe ("På netværket" … "Søg igen").</summary>
    public static Control Sec(MacPal p, string text, Control? right = null, string? light = null)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(2, 14, 2, 6) };
        var t = new TextBlock { FontSize = 13, Foreground = p.Label, VerticalAlignment = VerticalAlignment.Center };
        t.Inlines!.Add(new Avalonia.Controls.Documents.Run(text) { FontWeight = FontWeight.SemiBold });
        if (light is not null) t.Inlines.Add(new Avalonia.Controls.Documents.Run("  " + light) { Foreground = p.Label2 });
        g.Children.Add(t);
        if (right is not null) { Grid.SetColumn(right, 1); g.Children.Add(right); }
        return g;
    }

    public static TextBlock Note(MacPal p, string text) { var t = T(text, 11.5, p.Label2); t.Margin = new Thickness(2, 6, 2, 0); return t; }

    // ---------------------------------------------------------------- grupperede lister (som Systemindstillinger)

    /// <summary>Hvid (mørk: let) gruppe med afrundede hjørner og tynde skillelinjer mellem rækkerne.</summary>
    public static Border Group(MacPal p, IEnumerable<Control> rows, double indent = 12)
    {
        var sp = new StackPanel();
        var first = true;
        foreach (var r in rows)
        {
            if (!first) sp.Children.Add(new Border { Height = 0.5, Background = p.Sep, Margin = new Thickness(indent, 0, 0, 0) });
            sp.Children.Add(r); first = false;
        }
        return new Border
        {
            Background = p.Group, CornerRadius = new CornerRadius(10), ClipToBounds = true, Child = sp,
            BoxShadow = new BoxShadows(new BoxShadow { Spread = 0.5, Color = p.GroupLine }),
        };
    }

    /// <summary>Række: [ikon/radio] titel + undertekst [værdi/knap/kontakt].</summary>
    public static Grid Row(MacPal p, Control? lead, string title, string? sub = null, Control? trail = null, bool bold = false, double minHeight = 40, IBrush? titleColor = null)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), MinHeight = minHeight, Margin = new Thickness(12, 6) };
        if (lead is not null) { lead.Margin = new Thickness(0, 0, 10, 0); lead.VerticalAlignment = VerticalAlignment.Center; g.Children.Add(lead); }
        var col = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
        col.Children.Add(T(title, 13, titleColor ?? p.Label, bold ? FontWeight.SemiBold : FontWeight.Normal));
        if (sub is { Length: > 0 }) col.Children.Add(T(sub, 11.5, p.Label2));
        Grid.SetColumn(col, 1); g.Children.Add(col);
        if (trail is not null) { trail.Margin = new Thickness(10, 0, 0, 0); trail.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(trail, 2); g.Children.Add(trail); }
        return g;
    }

    /// <summary>Række med "Etiket ............ værdi".</summary>
    public static Grid ValueRow(MacPal p, string label, string value, Control? after = null)
    {
        var v = T(value, 13, p.Label2, wrap: false); v.MaxWidth = 300;
        Control trail = v;
        if (after is not null) { var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 }; sp.Children.Add(after); sp.Children.Add(v); trail = sp; }
        return Row(p, null, label, null, trail, minHeight: 28);
    }

    /// <summary>
    /// Sat af vinduet: sidste input var tastaturet (Tab, pile, mellemrum) – så tegnes fokusringen. Med musen vises den ikke (som i macOS).
    /// </summary>
    public static bool KeyboardNav { get; set; }

    /// <summary>
    /// Klikbar række (valg): en rigtig radioknap uden Fluent-udseende, så den kan nås med Tab, vælges med mellemrum/Enter og læses af
    /// VoiceOver med navn og valgt-tilstand. key: fast id, så fokus kan sættes tilbage, når vinduet tegnes igen.
    /// </summary>
    public static Control Clickable(MacPal p, Control row, Action click, string name, bool selected, string key, double radius = 8)
    {
        var ring = new Border { Background = Brushes.Transparent, CornerRadius = new CornerRadius(radius), Child = row, ClipToBounds = false };
        var rb = new ChoiceButton
        {
            IsChecked = selected, Focusable = true, IsTabStop = true, Tag = key, Cursor = new Cursor(StandardCursorType.Hand), ClipToBounds = false,
            HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(0), MinHeight = 0, MinWidth = 0,
            GroupName = "fd-" + key,   // egen gruppe: valget styres af modellen, ikke af Avalonias radiogrupper
            Template = new FuncControlTemplate<RadioButton>((_, _) => ring),
        };
        AutomationProperties.SetName(rb, name);
        void Set() => ring.BoxShadow = rb.IsFocused && KeyboardNav ? new BoxShadows(new BoxShadow { Spread = 3, Color = p.Ring }) : default;
        rb.GotFocus += (_, _) => Set(); rb.LostFocus += (_, _) => Set();
        rb.Click += (_, _) => click();
        return rb;
    }

    /// <summary>Radioknap, der ikke skifter tilstand selv (modellen bestemmer, og vinduet tegnes igen ved klik).</summary>
    private sealed class ChoiceButton : RadioButton
    {
        protected override void Toggle() { }
    }

    /// <summary>macOS' radioknap (tegnet).</summary>
    public static Control Radio(MacPal p, bool on)
    {
        var g = new Grid { Width = 16, Height = 16 };
        g.Children.Add(new Ellipse
        {
            Fill = on ? p.AccentBrush : p.Field, Stroke = on ? p.AccentBrush : p.FieldLine, StrokeThickness = on ? 0 : 1,
        });
        if (on) g.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = Brushes.White });
        return g;
    }

    public static ToggleSwitch Switch(bool on) => new() { IsChecked = on, OnContent = "", OffContent = "", MinWidth = 0, Padding = new Thickness(0) };

    public static Button Btn(string text, bool def = false, bool small = false, string? icon = null, IBrush? iconColor = null)
    {
        object content = text;
        if (icon is not null)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            sp.Children.Add(Icon(icon, small ? 12 : 13, iconColor ?? MacPal.Current.Label));
            sp.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            content = sp;
        }
        var b = new Button { Content = content, IsDefault = def, Tag = "btn:" + text };
        AutomationProperties.SetName(b, text);
        if (def) b.Classes.Add("accent");
        if (small) b.Classes.Add("sm");
        return b;
    }

    public static Button Link(string text, IBrush color, string? icon = null, double size = 13)
    {
        object content = text;
        if (icon is not null)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            sp.Children.Add(Icon(icon, 13, color));
            sp.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Foreground = color, FontSize = size });
            content = sp;
        }
        var b = new Button { Content = content, Foreground = color, FontSize = size, Cursor = new Cursor(StandardCursorType.Hand), Tag = "btn:" + text };
        AutomationProperties.SetName(b, text);
        b.Classes.Add("link");
        return b;
    }

    /// <summary>Tekstfelt med macOS' fokusring – rød ring ved en fejl, der hører til feltet.</summary>
    public static Border Field(MacPal p, TextBox tb, bool error)
    {
        Detach(tb);
        var ring = new Border { CornerRadius = new CornerRadius(7), Child = tb };
        void Set() => ring.BoxShadow = error
            ? new BoxShadows(new BoxShadow { Spread = 3, Color = Color.FromArgb(100, 255, 59, 48) })
            : tb.IsFocused ? new BoxShadows(new BoxShadow { Spread = 3, Color = p.Ring }) : default;
        tb.BorderBrush = error ? p.Red : p.FieldLine;
        tb.BorderThickness = new Thickness(error ? 1 : 0.75);
        tb.GotFocus += (_, _) => Set(); tb.LostFocus += (_, _) => Set();
        Set();
        return ring;
    }

    /// <summary>Gruppe med ét felt (og evt. en knap).</summary>
    public static Border FieldGroup(MacPal p, TextBox tb, bool error, Control? button = null)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(12, 9) };
        g.Children.Add(Field(p, tb, error));
        if (button is not null) { button.Margin = new Thickness(8, 0, 0, 0); button.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(button, 1); g.Children.Add(button); }
        return Group(p, [g]);
    }

    /// <summary>
    /// Kort besked lige under et felt: ✓ grøn eller ✕ rød med forklaring. detail: teknisk tekst under "Tekniske detaljer" (fold ud med
    /// toggle) – som tekst, ikke kun som tooltip, så den kan læses med tastatur og VoiceOver.
    /// </summary>
    public static Control Msg(MacPal p, SetupTone tone, string title, string? text = null, string? detail = null, bool open = false, Action? toggle = null)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(2, 8, 2, 0) };
        var icon = Icon(tone switch { SetupTone.Ok => "checkc", SetupTone.Bad => "xc", SetupTone.Warn => "warn", _ => "info" }, 15, p.Tone(tone));
        icon.VerticalAlignment = VerticalAlignment.Top; icon.Margin = new Thickness(0, 1, 7, 0);
        g.Children.Add(icon);
        var col = new StackPanel();
        var head = T(title, 12, tone == SetupTone.Bad ? p.Red : p.Label, tone == SetupTone.Bad ? FontWeight.SemiBold : FontWeight.Normal);
        // fejl og svar på en test læses op, når de dukker op
        if (tone is SetupTone.Bad or SetupTone.Ok) AutomationProperties.SetLiveSetting(head, tone == SetupTone.Bad ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite);
        col.Children.Add(head);
        if (text is { Length: > 0 }) col.Children.Add(T(text, 12, p.Label2));
        if (detail is { Length: > 0 } && toggle is not null)
        {
            var link = Link(SetupTexts.Details, p.Label2, open ? "chevd" : "chev", 12);
            link.HorizontalAlignment = HorizontalAlignment.Left; link.Margin = new Thickness(-2, 3, 0, 0); link.Tag = "btn:details";
            link.Click += (_, _) => toggle();
            col.Children.Add(link);
            if (open) col.Children.Add(T(detail, 11, p.Label2));
        }
        Grid.SetColumn(col, 1); g.Children.Add(col);
        return g;
    }

    /// <summary>Gul advarselsboks med "Tekniske detaljer" (fx certifikatfejl).</summary>
    public static Control WarnBox(MacPal p, SetupCheck c, bool open, Action toggle)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        var icon = Icon("warn", 22, p.Orange); icon.VerticalAlignment = VerticalAlignment.Top; icon.Margin = new Thickness(0, 0, 10, 0);
        g.Children.Add(icon);
        var col = new StackPanel { Spacing = 3 };
        col.Children.Add(T(c.Title, 13, p.Label, FontWeight.SemiBold));
        col.Children.Add(T(c.Text, 12, p.Label2));
        if (c.Detail is { Length: > 0 } d)
        {
            var link = Link(SetupTexts.Details, p.Label2, open ? "chevd" : "chev", 12);
            link.HorizontalAlignment = HorizontalAlignment.Left; link.Margin = new Thickness(0, 4, 0, 0);
            link.Click += (_, _) => toggle();
            col.Children.Add(link);
            if (open) { var t = T(d, 11, p.Label2); col.Children.Add(t); }
        }
        Grid.SetColumn(col, 1); g.Children.Add(col);
        return new Border
        {
            Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(12, 10), CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(((ISolidColorBrush)p.Orange).Color, p.Dark ? 0.14 : 0.10),
            BoxShadow = new BoxShadows(new BoxShadow { Spread = 0.5, Color = Color.FromArgb(90, 255, 149, 0) }), Child = g,
        };
    }

    /// <summary>Tip-boks med ikon (velkomst: "Har du fået et link …", færdig: "Kan du ikke se ikonet?").</summary>
    public static Border Tip(MacPal p, string icon, string title, string text, Control? button = null)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        var i = Icon(icon, 20, p.AccentBrush); i.VerticalAlignment = VerticalAlignment.Top; i.Margin = new Thickness(0, 1, 10, 0);
        g.Children.Add(i);
        var col = new StackPanel { Spacing = 1 };
        col.Children.Add(T(title, 12.5, p.Label, FontWeight.SemiBold));
        col.Children.Add(T(text, 12, p.Label2));
        Grid.SetColumn(col, 1); g.Children.Add(col);
        if (button is not null) { button.Margin = new Thickness(10, 0, 0, 0); button.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(button, 2); g.Children.Add(button); }
        return new Border { Margin = new Thickness(0, 16, 0, 0), Padding = new Thickness(12, 10), CornerRadius = new CornerRadius(10), Background = p.AccentSoft, Child = g };
    }

    /// <summary>Lille grøn/orange/grå prik.</summary>
    public static Ellipse Dot(IBrush c, double size = 7) => new() { Width = size, Height = size, Fill = c, VerticalAlignment = VerticalAlignment.Center };

    /// <summary>Fremdriftslinje (installation).</summary>
    public static Control Bar(MacPal p, double fraction, IBrush? color = null)
    {
        var track = new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = p.Sep, Margin = new Thickness(0, 4, 0, 16) };
        var fill = new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = color ?? p.AccentBrush, HorizontalAlignment = HorizontalAlignment.Left };
        track.Child = fill;
        track.SizeChanged += (_, e) => fill.Width = Math.Max(6, e.NewSize.Width * Math.Clamp(fraction, 0, 1));
        return track;
    }

    /// <summary>Fjern en kontrol fra sin forælder, så den kan sættes ind et nyt sted (felterne genbruges, når skærmen tegnes igen).</summary>
    public static void Detach(Control c)
    {
        switch (c.Parent)
        {
            case Panel pn: pn.Children.Remove(c); break;
            case Decorator d when d.Child == c: d.Child = null; break;
            case ContentControl cc when ReferenceEquals(cc.Content, c): cc.Content = null; break;
        }
    }
}

/// <summary>macOS' "aktivitets"-spinner: 12 streger, der drejer (ventetid, søgning, installation).</summary>
public sealed class Spinner : Canvas
{
    private readonly DispatcherTimer _t = new() { Interval = TimeSpan.FromMilliseconds(83) };
    private int _step;
    public static bool Frozen { get; set; }   // skærmbilleder: stillestående

    public Spinner(IBrush color, double size = 16)
    {
        Width = Height = size;
        var c = size / 2;
        for (var i = 0; i < 12; i++)
        {
            var a = i * Math.PI / 6;
            Children.Add(new Line
            {
                StartPoint = new Point(c + Math.Sin(a) * size * 0.2, c - Math.Cos(a) * size * 0.2),
                EndPoint = new Point(c + Math.Sin(a) * size * 0.4, c - Math.Cos(a) * size * 0.4),
                Stroke = color, StrokeThickness = size / 11, StrokeLineCap = PenLineCap.Round,
            });
        }
        Paint();
        _t.Tick += (_, _) => { _step = (_step + 1) % 12; Paint(); };
        AttachedToVisualTree += (_, _) => { if (!Frozen) _t.Start(); };
        DetachedFromVisualTree += (_, _) => _t.Stop();
        VerticalAlignment = VerticalAlignment.Center;
    }

    private void Paint()
    {
        for (var i = 0; i < 12; i++) Children[i].Opacity = 0.18 + 0.82 * ((12 - ((i - _step + 12) % 12)) % 12) / 11.0;
    }
}
