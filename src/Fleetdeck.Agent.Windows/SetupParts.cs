using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Fleetdeck.Shared;

namespace Fleetdeck.Agent.Windows;

/// <summary>
/// Byggeklodserne til opsætningsvinduet i forslag B's opbygning, men med Windows 11-udtryk: ikonerne fra <see cref="SetupIcons"/> tegnet med
/// GDI+ (via <see cref="SvgPath"/>), farvede fliser, navigationsrækker med accentstreg, grupperede kort, valgrækker, beskeder med ikon,
/// en ProgressRing og kodefliser. Alle mål er logiske (96 DPI) og ganges med <see cref="Ui.Scale"/>.
/// </summary>
public static class SetupDraw
{
    private static readonly Dictionary<string, List<SvgPath.Figure>[]> Cache = [];

    /// <summary>Tegn et ikon (24×24-koordinater) i rektanglet r.</summary>
    public static void Icon(Graphics g, string name, RectangleF r, Color color, float stroke = 1.7f)
    {
        if (!Cache.TryGetValue(name, out var parts))
            Cache[name] = parts = SetupIcons.Paths[name].Select(p => SvgPath.Parse(p.Length > 1 && p[1] == ':' ? p[2..] : p)).ToArray();
        var kinds = SetupIcons.Paths[name].Select(p => p.StartsWith("F:") ? 'F' : p.StartsWith("W:") ? 'W' : 'S').ToArray();
        var s = r.Width / 24f;
        var state = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TranslateTransform(r.X, r.Y); g.ScaleTransform(s, s);
        for (var i = 0; i < parts.Length; i++)
        {
            using var path = new GraphicsPath();
            foreach (var f in parts[i])
            {
                path.StartFigure();
                var pts = f.Points.Select(p => new PointF((float)p.X, (float)p.Y)).ToArray();
                if (pts.Length >= 4) path.AddBeziers(pts);
                if (f.Closed) path.CloseFigure();
            }
            if (kinds[i] == 'F') { using var b = new SolidBrush(color); g.FillPath(b, path); }
            else
            {
                using var pen = new Pen(kinds[i] == 'W' ? Color.White : color, kinds[i] == 'W' ? 2f : stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                g.DrawPath(pen, path);
            }
        }
        g.Restore(state);
    }

    /// <summary>Farvet flise med hvidt ikon (navigationen 22, rækker 28, overskriften 52) – lodret forløb som i forslag B.</summary>
    public static void Tile(Graphics g, RectangleF r, string icon, Color c, float alpha = 1f)
    {
        var top = UiDraw.Blend(c, Color.White, 0.18f);
        var rad = r.Width >= 46 ? r.Width / 4 : r.Width >= 28 ? 7 * r.Width / 28 : 6 * r.Width / 22;
        using var path = UiDraw.Round(r, rad);
        using var b = new LinearGradientBrush(r, A(top, alpha), A(c, alpha), LinearGradientMode.Vertical);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.FillPath(b, path);
        var k = r.Width >= 46 ? 0.58f : 0.62f;
        var w = r.Width * k;
        Icon(g, icon, new RectangleF(r.X + (r.Width - w) / 2, r.Y + (r.Height - w) / 2, w, w), A(Color.White, alpha), r.Width >= 46 ? 1.9f : 2f);
    }

    public static Color A(Color c, float a) => Color.FromArgb((int)(c.A * a), c.R, c.G, c.B);
    public static Color Hex(string hex) => ColorTranslator.FromHtml(hex);

    public static Color Tone(Theme t, SetupTone tone) => tone switch { SetupTone.Ok => t.Ok, SetupTone.Warn => t.Warn, SetupTone.Bad => t.Bad, _ => t.Accent };
    public static string ToneIcon(SetupTone tone) => tone switch { SetupTone.Ok => "checkc", SetupTone.Warn => "warn", SetupTone.Bad => "xc", _ => "info" };
}

/// <summary>Et ikon eller en flise som kontrol.</summary>
public sealed class IconBox : Control
{
    private readonly string icon;
    private readonly Color color;
    private readonly bool tile;
    public IconBox(string icon, Color color, int size, bool tile = false)
    {
        this.icon = icon; this.color = color; this.tile = tile; Size = new Size(size, size); Margin = Padding.Empty; TabStop = false;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var r = new RectangleF(0, 0, Width, Height);
        if (tile) SetupDraw.Tile(e.Graphics, r, icon, color); else SetupDraw.Icon(e.Graphics, icon, r, color);
    }
}

/// <summary>Windows 11's ProgressRing: en bue, der drejer (søgning, installation, venten).</summary>
public sealed class Ring : Control
{
    private readonly Theme t;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 33 };
    private float angle;
    public Ring(Theme theme, int size = 18)
    {
        t = theme; Size = new Size(size, size); Margin = Padding.Empty; TabStop = false;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        timer.Tick += (_, _) => { angle = (angle + 12) % 360; Invalidate(); };
        HandleCreated += (_, _) => timer.Start();
        HandleDestroyed += (_, _) => timer.Stop();
    }
    protected override void Dispose(bool disposing) { if (disposing) timer.Dispose(); base.Dispose(disposing); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        var s = Ui.Scale(this); var w = 2.2f * s;
        var r = new RectangleF(w, w, Width - 2 * w, Height - 2 * w);
        using var pen = new Pen(t.Accent, w) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawArc(pen, r, angle, 100 + 60 * MathF.Sin(angle * MathF.PI / 180));
    }
}

/// <summary>Tekst med et lille ikon foran (beskeder ved felterne: ✓ grøn, ✕ rød, ⚠ orange, ℹ blå). Højden følger teksten.</summary>
public sealed class IconText : Control
{
    private readonly Theme t;
    private readonly string icon;
    private readonly Color iconColor;
    private readonly string title;
    private readonly string? text;
    private readonly bool spin;
    private readonly Ring? ring;
    private const TextFormatFlags Flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl;
    public IconText(Theme theme, SetupTone tone, string title, string? text, int width, bool spin = false)
    {
        t = theme; icon = SetupDraw.ToneIcon(tone); iconColor = SetupDraw.Tone(t, tone); this.title = title; this.text = text; this.spin = spin;
        Width = width; Margin = new Padding(0, 8, 0, 0); TabStop = false; Font = Ui.F(9.5f);
        ForeColor = tone == SetupTone.Bad ? t.Bad : t.Text;
        Tone = tone; Text = title; AccessibleName = title; AccessibleDescription = text;
        AccessibleRole = tone == SetupTone.Bad ? AccessibleRole.Alert : AccessibleRole.StaticText;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent;
        if (spin) { ring = new Ring(t, 16) { Location = new Point(0, 1) }; Controls.Add(ring); }
        Fit();
    }
    public SetupTone Tone { get; }
    /// <summary>Det, en skærmlæser skal sige, når beskeden dukker op.</summary>
    public string Spoken => string.IsNullOrEmpty(text) ? title : $"{title} {text}";
    /// <summary>Titlen er fed ved fejl (som "Nøglen passer ikke.").</summary>
    private Font Bold() => new(Font, ForeColor == t.Bad ? FontStyle.Bold : FontStyle.Regular);
    private void Fit()
    {
        var w = Math.Max(1, Width - (int)(24 * Ui.Scale(this)));
        using var bold = Bold();
        var h = TextRenderer.MeasureText(title, bold, new Size(w, 0), Flags).Height;
        if (!string.IsNullOrEmpty(text)) h += TextRenderer.MeasureText(text, Font, new Size(w, 0), Flags).Height;
        Height = Math.Max(h, (int)(18 * Ui.Scale(this)));
    }
    protected override void OnResize(EventArgs e) { base.OnResize(e); Fit(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; var s = Ui.Scale(this);
        if (!spin) SetupDraw.Icon(g, icon, new RectangleF(0, 1 * s, 16 * s, 16 * s), iconColor);
        var x = (int)(24 * s); var w = Width - x;
        using var bold = Bold();
        var h = TextRenderer.MeasureText(title, bold, new Size(w, 0), Flags).Height;
        TextRenderer.DrawText(g, title, bold, new Rectangle(x, 0, w, h), ForeColor, Flags);
        if (!string.IsNullOrEmpty(text)) TextRenderer.DrawText(g, text, Font, new Rectangle(x, h, w, Height - h), t.Muted, Flags);
    }
}

/// <summary>
/// Navigationsrække i sidebjælken (som NavigationView i Windows 11): flise + titel (+ lille undertekst), valgt = lys flade med accentstreg
/// til venstre, færdig = flueben, kommende = dæmpet.
/// </summary>
public sealed class NavRow : Control
{
    private readonly Theme t;
    private readonly string icon, title;
    private readonly string? note;
    private readonly Color color;
    private readonly SetupStepState state;
    private readonly bool plain;
    private bool hover;
    public NavRow(Theme theme, string icon, Color color, string title, string? note, SetupStepState state, int width, bool plain = false)
    {
        t = theme; this.icon = icon; this.color = color; this.title = title; this.note = note; this.state = state; this.plain = plain;
        Size = new Size(width, note is null ? 36 : 46); Margin = new Padding(0, 0, 0, 2); Font = Ui.F(10f); TabStop = plain;
        Cursor = plain ? Cursors.Hand : Cursors.Default;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
        if (plain) SetStyle(ControlStyles.Selectable, true);
        BackColor = Color.Transparent;
        // trinene læses som liste ("Server, nu" / "Adgang, kommer"); oversigtens sektioner som faner med valgt-tilstand
        Text = title; AccessibleName = title;
        AccessibleDescription = plain ? null : string.Join(", ", new[] { note, StateText(state) }.Where(x => !string.IsNullOrEmpty(x)));
        AccessibleRole = plain ? AccessibleRole.PageTab : AccessibleRole.ListItem;
    }
    public static string StateText(SetupStepState s) => s switch { SetupStepState.Done => "færdig", SetupStepState.Current => "nuværende trin", _ => "kommer" };
    protected override AccessibleObject CreateAccessibilityInstance() => plain
        ? new ChoiceAccessible(this, AccessibleRole.PageTab, () => state == SetupStepState.Current, () => OnClick(EventArgs.Empty))
        : base.CreateAccessibilityInstance();
    protected override void OnKeyUp(KeyEventArgs e) { if (plain && e.KeyCode is Keys.Space or Keys.Enter) OnClick(EventArgs.Empty); base.OnKeyUp(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnMouseEnter(EventArgs e) { hover = plain; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; var s = Ui.Scale(this); g.SmoothingMode = SmoothingMode.AntiAlias;
        var cur = state == SetupStepState.Current;
        if (cur || hover)
        {
            using var path = UiDraw.Round(new RectangleF(0, 0, Width, Height), 5 * s);
            using var b = new SolidBrush(t.Dark ? Color.FromArgb(cur ? 22 : 12, 255, 255, 255) : Color.FromArgb(cur ? 14 : 8, 0, 0, 0));
            g.FillPath(b, path);
        }
        if (cur)
        {
            using var bar = UiDraw.Round(new RectangleF(0, Height / 2f - 8 * s, 3 * s, 16 * s), 1.5f * s);
            using var ab = new SolidBrush(t.Accent);
            g.FillPath(ab, bar);
        }
        var todo = state == SetupStepState.Todo && !plain;
        SetupDraw.Tile(g, new RectangleF(10 * s, (Height - 22 * s) / 2, 22 * s, 22 * s), icon, color, todo ? 0.45f : 1f);
        var x = (int)(42 * s);
        var fg = todo ? t.Muted : t.Text;
        using var f = cur ? Ui.F(10f, FontStyle.Bold) : Ui.F(10f);
        var th = TextRenderer.MeasureText(title, f).Height;
        var y = note is null ? (Height - th) / 2 : (int)(5 * s);
        TextRenderer.DrawText(g, title, f, new Point(x, y), fg, TextFormatFlags.NoPrefix);
        if (note is not null) { using var small = Ui.F(8.25f); TextRenderer.DrawText(g, note, small, new Point(x, y + th), t.Muted, TextFormatFlags.NoPrefix); }
        if (state == SetupStepState.Done && !plain) SetupDraw.Icon(g, "check", new RectangleF(Width - 24 * s, (Height - 13 * s) / 2, 13 * s, 13 * s), t.Muted, 2.2f);
        if (plain && Focused && ShowFocusCues) UiDraw.FocusRing(g, UiDraw.Inset(Size, 1f * s), 5 * s, t.Accent, s);
    }
}

/// <summary>
/// Narrator/skærmlæsere: en egentegnet valgrække som radioknap (eller fane) med valgt-tilstand og "Vælg" som standardhandling – ellers læses
/// kun "ruden" op, og man kan ikke høre, hvilken profil eller server der er valgt.
/// </summary>
internal sealed class ChoiceAccessible(Control owner, AccessibleRole role, Func<bool> on, Action activate) : Control.ControlAccessibleObject(owner)
{
    public override AccessibleRole Role => role;
    public override AccessibleStates State
    {
        get
        {
            var s = base.State | AccessibleStates.Focusable;
            if (on()) s |= role == AccessibleRole.RadioButton ? AccessibleStates.Checked : AccessibleStates.Selected;
            return s;
        }
    }
    public override string DefaultAction => "Vælg";
    public override void DoDefaultAction() => activate();
}

/// <summary>Valgrække (radio): hele rækken kan klikkes; tilvalg: flise + titel + undertekst + radiocirkel.</summary>
public sealed class ChoiceRow : Control
{
    private readonly Theme t;
    private readonly string title;
    private readonly string? sub, tile;
    private readonly Color tileColor;
    private readonly bool on, card;
    private bool hover;
    public ChoiceRow(Theme theme, string title, string? sub, bool on, int width, string? tile = null, Color tileColor = default, bool card = false)
    {
        t = theme; this.title = title; this.sub = sub; this.on = on; this.tile = tile; this.tileColor = tileColor; this.card = card;
        Size = new Size(width, card ? 64 : sub is null ? 38 : 48); Margin = Padding.Empty; Cursor = Cursors.Hand; TabStop = true; Font = Ui.F(10f);
        Text = title; AccessibleName = title; AccessibleDescription = sub; AccessibleRole = AccessibleRole.RadioButton;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
    }
    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnKeyUp(KeyEventArgs e) { if (e.KeyCode is Keys.Space or Keys.Enter) OnClick(EventArgs.Empty); base.OnKeyUp(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override AccessibleObject CreateAccessibilityInstance() => new ChoiceAccessible(this, AccessibleRole.RadioButton, () => on, () => OnClick(EventArgs.Empty));
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics; var s = Ui.Scale(this);
        if (card)
        {
            UiDraw.Card(g, Size, t, Ui.Surface(this, t.Bg), s);
            if (on || Focused) UiDraw.FocusRing(g, UiDraw.Inset(Size, 1.5f * s), 7 * s, t.Accent, s);
        }
        else
        {
            using var b = new SolidBrush(hover ? UiDraw.Blend(t.Card, t.Text, 0.04f) : t.Card);
            g.FillRectangle(b, ClientRectangle);
        }
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; var s = Ui.Scale(this); g.SmoothingMode = SmoothingMode.AntiAlias;
        var x = (int)(14 * s);
        if (tile is not null) { SetupDraw.Tile(g, new RectangleF(x, (Height - 28 * s) / 2, 28 * s, 28 * s), tile, tileColor); x += (int)(38 * s); }
        else
        {
            // radiocirklen til venstre (profillisten)
            Radio(g, new RectangleF(x, (Height - 18 * s) / 2, 18 * s, 18 * s), s); x += (int)(30 * s);
        }
        var w = Width - x - (int)((tile is not null ? 40 : 14) * s);
        using var tf = Ui.F(10f, on ? FontStyle.Bold : FontStyle.Regular);
        using var sf = Ui.F(8.75f);
        var th = TextRenderer.MeasureText(title, tf).Height;
        var sh = sub is null ? 0 : TextRenderer.MeasureText(g, sub, sf, new Size(w, 0), TextFormatFlags.WordBreak).Height;
        var y = (Height - th - sh) / 2;
        TextRenderer.DrawText(g, title, tf, new Rectangle(x, y, w, th), t.Text, TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        if (sub is not null) TextRenderer.DrawText(g, sub, sf, new Rectangle(x, y + th, w, sh), t.Muted, TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak);
        if (tile is not null) Radio(g, new RectangleF(Width - 30 * s, (Height - 18 * s) / 2, 18 * s, 18 * s), s);
        if (!card && Focused && ShowFocusCues) UiDraw.FocusRing(g, UiDraw.Inset(Size, 1f * s), 4 * s, t.Accent, s);
    }
    private void Radio(Graphics g, RectangleF r, float s)
    {
        if (on)
        {
            using var b = new SolidBrush(t.Accent); g.FillEllipse(b, r);
            var d = 8 * s; using var w = new SolidBrush(Color.White);
            g.FillEllipse(w, r.X + (r.Width - d) / 2, r.Y + (r.Height - d) / 2, d, d);
        }
        else { using var p = new Pen(t.Muted, 1.2f * s); g.DrawEllipse(p, r); }
    }
}

/// <summary>Koden i store fliser (7K4-Q2M).</summary>
public sealed class CodeTiles : Control
{
    private readonly Theme t;
    private string code;
    public CodeTiles(Theme theme, string code, int width)
    {
        t = theme; this.code = code; Size = new Size(width, 56); Margin = new Padding(0, 0, 0, 8); TabStop = false;
        AccessibleRole = AccessibleRole.StaticText; AccessibleName = Spoken(code);
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
    }
    public string Code { get => code; set { code = value; AccessibleName = Spoken(value); Invalidate(); } }
    /// <summary>"Kode 7 K 4 Q 2 M" – tegn for tegn, så Narrator ikke læser koden som et ord.</summary>
    public static string Spoken(string code) => "Kode " + string.Join(" ", code.Where(char.IsLetterOrDigit));
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; var s = Ui.Scale(this); g.SmoothingMode = SmoothingMode.AntiAlias;
        float tw = 38 * s, th = 48 * s, gap = 6 * s, dash = 14 * s;
        var total = code.Sum(c => c == '-' ? dash + gap : tw + gap) - gap;
        var x = (Width - total) / 2; var y = (Height - th) / 2;
        using var f = new Font("Cascadia Mono", 20f, FontStyle.Bold);
        foreach (var c in code)
        {
            if (c == '-') { using var b = new SolidBrush(t.Muted); g.FillRectangle(b, x + 2 * s, y + th / 2 - 1.2f * s, dash - 4 * s, 2.4f * s); x += dash + gap; continue; }
            using var path = UiDraw.Round(new RectangleF(x, y, tw, th), 7 * s);
            using var fill = new SolidBrush(t.Card); using var pen = new Pen(t.Border, s);
            g.FillPath(fill, path); g.DrawPath(pen, path);
            TextRenderer.DrawText(g, c.ToString(), f, Rectangle.Round(new RectangleF(x, y, tw, th)), t.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            x += tw + gap;
        }
    }
}

/// <summary>Windows 11: Mica bag titellinjen, runde hjørner og mørk titellinje efter temaet.</summary>
public static class Backdrop
{
    private const int DwmUseImmersiveDarkMode = 20, DwmWindowCornerPreference = 33, DwmSystemBackdropType = 38;
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);

    public static void Apply(Form f, Theme t)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) { Ui.TitleBar(f, t); return; }
        try
        {
            var dark = t.Dark ? 1 : 0; DwmSetWindowAttribute(f.Handle, DwmUseImmersiveDarkMode, ref dark, 4);
            var round = 2; DwmSetWindowAttribute(f.Handle, DwmWindowCornerPreference, ref round, 4);
            var mica = 2; DwmSetWindowAttribute(f.Handle, DwmSystemBackdropType, ref mica, 4);
        }
        catch (Exception) { }
    }
}
