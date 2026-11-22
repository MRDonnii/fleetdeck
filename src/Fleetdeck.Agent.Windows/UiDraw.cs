using System.Drawing;
using System.Drawing.Drawing2D;
using Microsoft.Win32;

namespace Fleetdeck.Agent.Windows;

/// <summary>Farver i Windows 11-stil: lyst (som Windows' Indstillinger) og mørkt (som Fleetdecks webside).</summary>
public sealed record Theme(bool Dark, Color Bg, Color Card, Color Text, Color Muted, Color Border, Color Accent, Color AccentText,
    Color Ok, Color OkBg, Color Warn, Color WarnBg, Color Bad, Color BadBg, Color Field)
{
    public static readonly Theme Light = new(false, C(0xf3f3f3), C(0xffffff), C(0x1b1b1b), C(0x5e5e5e), C(0xe3e3e3), C(0x0f6cbd), Color.White,
        C(0x0f7b0f), C(0xdff6dd), C(0x8a5300), C(0xfff4ce), C(0xc42b1c), C(0xfde7e9), C(0xfbfbfb));
    public static readonly Theme DarkTheme = new(true, C(0x1f2023), C(0x2a2b2f), C(0xf1f1f1), C(0xa3a6ad), C(0x3a3c42), C(0x4c9bff), Color.White,
        C(0x6ccb5f), C(0x233a24), C(0xf5c35a), C(0x3d3220), C(0xff8a80), C(0x442726), C(0x24252a));

    private static Color C(int rgb) => Color.FromArgb(255, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);

    /// <summary>Indstilling (HKCU\Software\Fleetdeck\Theme = auto|light|dark), ellers Windows' eget app-tema.</summary>
    public static Theme Current
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return Light;
            var pick = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Fleetdeck", "Theme", "auto") as string ?? "auto";
            if (pick == "light") return Light;
            if (pick == "dark") return DarkTheme;
            return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int v && v == 0 ? DarkTheme : Light;
        }
    }
}

/// <summary>
/// Tegningen af de afrundede kontroller – kun System.Drawing, så den også kan tegnes og efterses uden for Windows.
/// Alle mål er logiske (96 DPI) og ganges med <c>s</c> (DPI/96). Hjørnerne uden for den afrundede form males altid
/// med den uigennemsigtige baggrund bag kontrollen ("surface"): en gennemsigtig farve i en dobbeltbuffer bliver sort på skærmen.
/// </summary>
public static class UiDraw
{
    public const float CardRadius = 8f, ControlRadius = 5f;

    /// <summary>Kantblødgøring, og pixelcentre på halve koordinater, så en 1 px-streg ved x + 0,5 bliver skarp.</summary>
    public static void Prepare(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
    }

    /// <summary>Stregbredde for kanter: 1 px op til 175 %, 2 px fra 200 %.</summary>
    public static float Stroke(float s) => MathF.Max(1f, MathF.Floor(s));

    public static Color Opaque(Color c) => c.A == 255 ? c : Color.FromArgb(255, c.R, c.G, c.B);

    /// <summary>Blander a mod b (0 = a, 1 = b).</summary>
    public static Color Blend(Color a, Color b, float amount)
    {
        amount = Math.Clamp(amount, 0f, 1f);
        int M(int x, int y) => (int)MathF.Round(x + (y - x) * amount);
        return Color.FromArgb(255, M(a.R, b.R), M(a.G, b.G), M(a.B, b.B));
    }

    public static GraphicsPath Round(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        rad = MathF.Max(0f, MathF.Min(rad, MathF.Min(r.Width, r.Height) / 2f));
        if (r.Width <= 0 || r.Height <= 0) return p;
        if (rad < 0.5f) { p.AddRectangle(r); return p; }
        // kvartcirkler som Bézier-kurver (samme resultat som AddArc, men ens i alle GDI+-udgaver)
        var k = rad * (1 - 0.5523f); float l = r.Left, t = r.Top, ri = r.Right, b = r.Bottom;
        p.AddBezier(l, t + rad, l, t + k, l + k, t, l + rad, t);
        p.AddBezier(ri - rad, t, ri - k, t, ri, t + k, ri, t + rad);
        p.AddBezier(ri, b - rad, ri, b - k, ri - k, b, ri - rad, b);
        p.AddBezier(l + rad, b, l + k, b, l, b - k, l, b - rad);
        p.CloseFigure();
        return p;
    }

    /// <summary>Rektanglet for en streg med bredden w helt inden for size (stregens midte ligger w/2 inde).</summary>
    public static RectangleF Inset(SizeF size, float w) => new(w / 2f, w / 2f, size.Width - w, size.Height - w);
    public static RectangleF Inset(RectangleF r, float by) => new(r.X + by, r.Y + by, r.Width - 2 * by, r.Height - 2 * by);

    /// <summary>Fylder og (evt.) kanter en afrundet form; formen ligger helt inden for r.</summary>
    public static void Shape(Graphics g, RectangleF r, float radius, Color fill, Color? border, float stroke)
    {
        var w = border is null ? 0f : stroke;
        using var path = Round(Inset(r, w / 2f), radius - w / 2f);
        using (var b = new SolidBrush(fill)) g.FillPath(b, path);
        if (border is { } bc) { using var p = new Pen(bc, w); g.DrawPath(p, path); }
    }

    /// <summary>Fokusring inden i en afrundet form (ingen stiplet Windows-firkant).</summary>
    public static void FocusRing(Graphics g, RectangleF r, float radius, Color color, float s)
    {
        var w = MathF.Max(2f, MathF.Round(1.5f * s)); var inset = MathF.Round(2f * s);
        using var path = Round(Inset(r, inset + w / 2f), radius - inset - w / 2f);
        using var p = new Pen(color, w); g.DrawPath(p, path);
    }

    /// <summary>Kort: hvid/mørk flade med tynd kant og radius 8.</summary>
    public static void Card(Graphics g, Size size, Theme t, Color surface, float s)
    {
        Prepare(g); g.Clear(Opaque(surface));
        Shape(g, new RectangleF(0, 0, size.Width, size.Height), CardRadius * s, t.Card, t.Border, Stroke(s));
    }

    /// <summary>Knap. Returnerer tekstfarven.</summary>
    public static Color Button(Graphics g, Size size, Theme t, Color surface, float s, bool primary, bool hover, bool pressed, bool enabled, bool focused)
    {
        Prepare(g); g.Clear(Opaque(surface));
        Color fill, text; Color? border;
        if (!enabled)
        {
            fill = primary ? (t.Dark ? Blend(t.Card, Color.White, 0.16f) : Blend(t.Field, Color.Black, 0.22f)) : (t.Dark ? Blend(t.Card, Color.White, 0.03f) : t.Field);
            text = primary ? (t.Dark ? Blend(t.Card, Color.White, 0.5f) : Color.White) : Blend(t.Muted, surface, 0.35f);
            border = primary ? null : t.Border;
        }
        else if (primary)
        {
            fill = pressed ? Blend(t.Accent, surface, 0.22f) : hover ? Blend(t.Accent, surface, 0.12f) : t.Accent;
            text = pressed ? Blend(t.AccentText, t.Accent, 0.2f) : t.AccentText; border = null;
        }
        else
        {
            var b = t.Dark ? Blend(t.Card, Color.White, 0.05f) : t.Field;
            fill = pressed ? (t.Dark ? Blend(t.Card, Color.White, 0.02f) : Blend(t.Field, Color.Black, 0.04f))
                 : hover ? (t.Dark ? Blend(t.Card, Color.White, 0.10f) : Blend(t.Field, Color.Black, 0.025f)) : b;
            text = pressed ? t.Muted : t.Text; border = t.Border;
        }
        var r = new RectangleF(0, 0, size.Width, size.Height);
        Shape(g, r, ControlRadius * s, fill, border, Stroke(s));
        if (focused && enabled) FocusRing(g, r, ControlRadius * s, primary ? t.AccentText : t.Accent, s);
        return text;
    }

    /// <summary>Kontaktens spor (40×20) centreret i kontrollen; resten af fladen er luft til fokusringen.</summary>
    public static RectangleF ToggleTrack(Size size, float s)
    {
        var w = MathF.Round(40 * s); var h = MathF.Round(20 * s);
        return new RectangleF(MathF.Round((size.Width - w) / 2f), MathF.Round((size.Height - h) / 2f), w, h);
    }

    /// <summary>Til/fra-kontakt i Windows 11-stil (knoppen vokser ved hover).</summary>
    public static void Toggle(Graphics g, Size size, Theme t, Color surface, float s, bool on, bool hover, bool pressed, bool enabled, bool focused)
    {
        Prepare(g); g.Clear(Opaque(surface));
        var r = ToggleTrack(size, s); var stroke = Stroke(s);
        var off = enabled ? t.Muted : Blend(t.Border, t.Muted, 0.3f);
        if (on) Shape(g, r, r.Height / 2, enabled ? (hover ? Blend(t.Accent, surface, 0.12f) : t.Accent) : Blend(t.Border, t.Muted, 0.3f), null, 0);
        else Shape(g, r, r.Height / 2, hover && enabled ? Blend(surface, t.Text, 0.06f) : surface, off, stroke);
        var d = (pressed && enabled ? 14 : hover && enabled ? 14 : 12) * s; var kw = pressed && enabled ? 17 * s : d;
        var cy = r.Y + r.Height / 2; var cx = on ? r.Right - r.Height / 2 : r.X + r.Height / 2;
        if (pressed && enabled) cx += on ? -(kw - d) / 2 : (kw - d) / 2;
        Shape(g, new RectangleF(cx - kw / 2, cy - d / 2, kw, d), d / 2, on ? (enabled ? t.AccentText : surface) : off, null, 0);
        if (focused && enabled)
        {
            var w = MathF.Max(2f, MathF.Round(1.5f * s)); var gap = MathF.Round(2 * s);
            var fr = new RectangleF(r.X - gap - w, r.Y - gap - w, r.Width + 2 * (gap + w), r.Height + 2 * (gap + w));
            using var path = Round(Inset(fr, w / 2f), fr.Height / 2 - w / 2f);
            using var p = new Pen(t.Text, w); g.DrawPath(p, path);
        }
    }

    /// <summary>Ramme om et tekstfelt / en rulleliste: afrundet, med en accentlinje forneden ved fokus.</summary>
    public static void Field(Graphics g, Size size, Theme t, Color surface, float s, bool focused)
    {
        Prepare(g); g.Clear(Opaque(surface));
        var r = new RectangleF(0, 0, size.Width, size.Height); var stroke = Stroke(s); var rad = ControlRadius * s;
        Shape(g, r, rad, t.Field, t.Border, stroke);
        // bundlinjen klippes til formen, så den følger hjørnerne
        using var path = Round(r, rad);
        var state = g.Save();
        g.SetClip(path);
        var lh = focused ? MathF.Round(2 * s) : stroke;
        using (var b = new SolidBrush(focused ? t.Accent : Blend(t.Border, t.Muted, 0.55f))) g.FillRectangle(b, 0, size.Height - lh, size.Width, lh);
        g.Restore(state);
    }

    /// <summary>Statuslinje (ok/advarsel/fejl/info).</summary>
    public static void Pill(Graphics g, Size size, Theme t, Color surface, Color fill, float s)
    {
        Prepare(g); g.Clear(Opaque(surface));
        Shape(g, new RectangleF(0, 0, size.Width, size.Height), ControlRadius * s, fill, Blend(fill, t.Text, t.Dark ? 0.12f : 0.07f), Stroke(s));
    }

    /// <summary>Fremdriftsbjælke; value = null tegner et stykke på 35 % (ukendt fremdrift).</summary>
    public static void Bar(Graphics g, Size size, Theme t, Color surface, float s, int? value)
    {
        Prepare(g); g.Clear(Opaque(surface));
        var r = new RectangleF(0, 0, size.Width, size.Height);
        Shape(g, r, r.Height / 2, t.Border, null, 0);
        var w = value is int v ? (v <= 0 ? 0 : MathF.Max(r.Height, r.Width * Math.Min(v, 100) / 100f)) : r.Width * 0.35f;
        if (w > 0) Shape(g, new RectangleF(0, 0, w, r.Height), r.Height / 2, t.Accent, null, 0);
    }

    /// <summary>Tynd skillelinje (fx mellem rækker i et kort).</summary>
    public static void Divider(Graphics g, int width, Theme t, float s)
    {
        using var b = new SolidBrush(t.Border); g.FillRectangle(b, 0, 0, width, Stroke(s));
    }
}
