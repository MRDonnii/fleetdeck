using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Fleetdeck.Agent.Windows;

public static class Ui
{
    public static readonly string FontName = FontFamily.Families.Any(f => f.Name == "Segoe UI Variable Text") ? "Segoe UI Variable Text" : "Segoe UI";
    public static Font F(float size, FontStyle st = FontStyle.Regular) => new(FontName, size, st);

    public static GraphicsPath Round(RectangleF r, float rad) => UiDraw.Round(r, rad);

    /// <summary>DPI-faktor for en kontrol (1 = 100 %, 1,5 = 150 %). Følger skærmen, vinduet står på.</summary>
    public static float Scale(Control c) => c.DeviceDpi / 96f;

    /// <summary>
    /// Den uigennemsigtige farve bag en kontrol: første forælder, der ikke er gennemsigtig. Afrundede hjørner skal males med
    /// den – Color.Transparent i en dobbeltbuffer bliver sort på skærmen (de sorte trekanter i hjørnerne).
    /// </summary>
    public static Color Surface(Control c, Color fallback)
    {
        for (var p = c.Parent; p is not null; p = p.Parent)
            if (p.BackColor.A == 255) return p.BackColor;
        return UiDraw.Opaque(fallback);
    }

    /// <summary>
    /// Vinduer bygges i logiske mål (96 DPI); WinForms skalerer størrelser, margener og min./maks.-mål efter skærmens DPI,
    /// når layoutet genoptages. Kald i starten af konstruktøren og <see cref="EndDpi"/> til sidst.
    /// </summary>
    public static void BeginDpi(Form f)
    {
        f.SuspendLayout();
        f.AutoScaleDimensions = new SizeF(96f, 96f);
        f.AutoScaleMode = AutoScaleMode.Dpi;
    }
    public static void EndDpi(Form f) { f.ResumeLayout(false); f.PerformLayout(); }

    /// <summary>Kontroller, der tilføjes efter vinduet er skaleret, skaleres ikke af WinForms – det gør denne (logiske mål → skærmens DPI).</summary>
    public static T Scaled<T>(T c, Control parent) where T : Control
    {
        var f = parent.DeviceDpi / 96f;
        if (Math.Abs(f - 1f) > 0.001f) c.Scale(new SizeF(f, f));
        return c;
    }

    /// <summary>Logoet i præcis px × px, skaleret ned fra den største udgave (skarpt ved 125–200 %).</summary>
    public static Bitmap Logo(int px)
    {
        using var big = AppIcon(256).ToBitmap();
        var bmp = new Bitmap(px, px);
        using var g = Graphics.FromImage(bmp);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.HighQuality; g.SmoothingMode = SmoothingMode.AntiAlias;
        g.DrawImage(big, 0, 0, px, px);
        return bmp;
    }

    /// <summary>Mørk titellinje på Windows 11, når temaet er mørkt.</summary>
    public static void TitleBar(Form f, Theme t)
    {
        try { var on = t.Dark ? 1 : 0; DwmSetWindowAttribute(f.Handle, 20, ref on, 4); } catch (Exception) { }
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);

    /// <summary>Programmets ikon (indlejret fleetdeck.ico).</summary>
    public static Icon AppIcon(int size = 32)
    {
        using var s = typeof(Ui).Assembly.GetManifestResourceStream("fleetdeck.ico");
        return s is null ? SystemIcons.Application : new Icon(s, size, size);
    }

    public static Label Text(string text, Theme t, float size = 10f, FontStyle st = FontStyle.Regular, bool muted = false, int maxWidth = 0) => new()
    {
        Text = text, AutoSize = true, UseMnemonic = false, Font = F(size, st), ForeColor = muted ? t.Muted : t.Text, BackColor = Color.Transparent,
        MaximumSize = new Size(maxWidth, 0), Margin = new Padding(0, 0, 0, 2),
    };

    public static Label Section(string text, Theme t) => new()
    {
        Text = text.ToUpperInvariant(), AutoSize = true, UseMnemonic = false, Font = F(8.25f, FontStyle.Bold), ForeColor = t.Muted, BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, 6),
    };

    /// <summary>
    /// Fast bredde. AutoSize-paneler låses med min./maks.-bredde (ellers krymper de til indholdet, og kortene får forskellig
    /// bredde); deres højde følger indholdet. Kontroller med fast højde (svarfelt, rammer) får kun bredden: WinForms afkorter
    /// højden til MaximumSize.Height, når MaximumSize sættes – også når DPI-skaleringen sætter den igen – så (bredde, 0) gav
    /// dem højden 0, og de forsvandt (se <see cref="MessageLayout.LimitWidth"/>).
    /// </summary>
    public static T FixWidth<T>(T c, int width) where T : Control
    {
        if (MessageLayout.LimitWidth(c.AutoSize)) { c.MinimumSize = new Size(width, 0); c.MaximumSize = new Size(width, 0); }
        c.Width = width;
        return c;
    }

    /// <summary>Lodret stak (én kolonne) med fast bredde, der vokser i højden med indholdet.</summary>
    public static TableLayoutPanel Stack(int width, Padding? pad = null) => FixWidth(new TableLayoutPanel
    {
        ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = pad ?? Padding.Empty,
        Margin = Padding.Empty, BackColor = Color.Transparent, GrowStyle = TableLayoutPanelGrowStyle.AddRows,
    }, width);
}


/// <summary>Kort med afrundede hjørner (som felterne i Windows' Indstillinger).</summary>
public sealed class Card : TableLayoutPanel
{
    private readonly Theme t;
    public Card(Theme theme, int width)
    {
        t = theme; ColumnCount = 1; AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink; Ui.FixWidth(this, width);
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Padding = new Padding(16, 14, 16, 14); Margin = new Padding(0, 0, 0, 10); BackColor = t.Card; GrowStyle = TableLayoutPanelGrowStyle.AddRows;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
    }
    // BackColor (kortets farve) er det, gennemsigtige børn arver; selve formen og hjørnerne tegnes her
    protected override void OnPaintBackground(PaintEventArgs e) => UiDraw.Card(e.Graphics, Size, t, Ui.Surface(this, t.Bg), Ui.Scale(this));
}

/// <summary>Til/fra-kontakt i Windows 11-stil (40×20 spor; luft omkring til fokusringen). Mellemrum skifter, når den har fokus.</summary>
public sealed class Toggle : Control
{
    private readonly Theme t;
    private bool on, hover, down;
    public event EventHandler? CheckedChanged;
    public Toggle(Theme theme)
    {
        t = theme; Size = new Size(50, 30); Cursor = Cursors.Hand; Margin = new Padding(12, 0, 0, 0); TabStop = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw
               | ControlStyles.Selectable | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
    }
    public bool Checked { get => on; set { if (on == value) return; on = value; Invalidate(); CheckedChanged?.Invoke(this, EventArgs.Empty); } }
    protected override void OnClick(EventArgs e) { if (Enabled) { Focus(); Checked = !Checked; } base.OnClick(e); }
    protected override void OnKeyUp(KeyEventArgs e) { if (Enabled && e.KeyCode == Keys.Space) Checked = !Checked; base.OnKeyUp(e); }
    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { down = true; Invalidate(); } base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); Cursor = Enabled ? Cursors.Hand : Cursors.Default; base.OnEnabledChanged(e); }
    protected override void OnPaintBackground(PaintEventArgs e) { }   // alt males (uigennemsigtigt) i OnPaint
    protected override void OnPaint(PaintEventArgs e) =>
        UiDraw.Toggle(e.Graphics, Size, t, Ui.Surface(this, t.Card), Ui.Scale(this), on, hover, down, Enabled, Focused && ShowFocusCues);
}

/// <summary>Flad knap med afrundede hjørner; Primary = accentfarvet. Hover, tryk, deaktiveret og fokusring (inden i formen).</summary>
public sealed class FlatBtn : Button
{
    private readonly Theme t;
    private bool hover, down;
    public bool Primary { get; set; }
    public FlatBtn(Theme theme, string text, bool primary = false)
    {
        t = theme; Text = text; Primary = primary; AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink; MinimumSize = new Size(80, 34); Padding = new Padding(14, 0, 14, 0);
        Font = Ui.F(10f, primary ? FontStyle.Bold : FontStyle.Regular); Cursor = Cursors.Hand; Margin = new Padding(8, 0, 0, 0); UseMnemonic = false;
        FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; BackColor = Color.Transparent; UseVisualStyleBackColor = false;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
    }
    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { down = true; Invalidate(); } base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); Cursor = Enabled ? Cursors.Hand : Cursors.Default; base.OnEnabledChanged(e); }
    protected override void OnPaintBackground(PaintEventArgs e) { }   // alt males (uigennemsigtigt) i OnPaint
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var fg = UiDraw.Button(g, Size, t, Ui.Surface(this, t.Card), Ui.Scale(this), Primary, hover, down, Enabled, Focused && ShowFocusCues);
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, fg,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
    }
}

/// <summary>Afrundet ramme om en indtastningskontrol (tekstfelt, rulleliste), centreret lodret, med accentlinje forneden ved fokus.</summary>
public class Frame : Panel
{
    private readonly Theme t;
    protected Control Inner { get; }
    public Frame(Theme theme, Control inner)
    {
        t = theme; Inner = inner; Height = 34; Padding = new Padding(10, 0, 8, 0); BackColor = t.Field; Margin = new Padding(0);
        inner.BackColor = t.Field; inner.ForeColor = t.Text;
        Controls.Add(inner);
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
        inner.GotFocus += (_, _) => Invalidate(); inner.LostFocus += (_, _) => Invalidate();
    }
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        var h = Inner.Height; var top = Math.Max(0, (Height - h) / 2);
        Inner.SetBounds(Padding.Left, top, Math.Max(0, Width - Padding.Horizontal), h, BoundsSpecified.Location | BoundsSpecified.Width);
    }
    protected override void OnPaintBackground(PaintEventArgs e) => UiDraw.Field(e.Graphics, Size, t, Ui.Surface(this, t.Card), Ui.Scale(this), Inner.Focused);
}

/// <summary>Tekstfelt med afrundet ramme og accentlinje forneden (som i Windows 11).</summary>
public sealed class Field : Frame
{
    public TextBox Box => (TextBox)Inner;
    public Field(Theme theme, string placeholder, bool password = false, bool mono = true)
        : base(theme, new TextBox { BorderStyle = BorderStyle.None, PlaceholderText = placeholder, UseSystemPasswordChar = password, Font = mono ? new Font("Consolas", 10.5f) : Ui.F(10f) })
    { }
    public override string Text { get => Box.Text; set => Box.Text = value; }
}

/// <summary>
/// Tekstfelt med flere linjer i samme ramme som <see cref="Field"/>. Tekstfeltet fylder hele rammen (minus luften), og et
/// klik på rammen giver det fokus. <see cref="Frame"/> sætter kun placering og bredde (til felter med én linje, hvis højde
/// følger skrifttypen); her følger højden også rammen, så det tegnede felt og det felt, man kan skrive i, altid er det samme.
/// </summary>
public sealed class TextArea : Frame
{
    public TextBox Box => (TextBox)Inner;
    public TextArea(Theme theme, string placeholder, int height)
        : base(theme, new TextBox { BorderStyle = BorderStyle.None, Multiline = true, AcceptsReturn = true, AcceptsTab = false, WordWrap = true,
            ScrollBars = ScrollBars.Vertical, PlaceholderText = placeholder, Font = Ui.F(10f) })
    {
        Height = height; Padding = new Padding(10, 7, 4, 7); Cursor = Cursors.IBeam;
    }
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        Inner.SetBounds(Padding.Left, Padding.Top, Math.Max(1, Width - Padding.Horizontal), Math.Max(1, Height - Padding.Vertical));
    }
    protected override void OnMouseDown(MouseEventArgs e) { Box.Focus(); base.OnMouseDown(e); }
    public override string Text { get => Box.Text; set => Box.Text = value; }
}

/// <summary>Statuslinje med farvet baggrund: ok / advarsel / fejl / info. Skjult, når teksten er tom.</summary>
public sealed class Pill : Label
{
    private readonly Theme t;
    private Color fill;
    private int fitWidth = -1;
    public Pill(Theme theme)
    {
        t = theme; fill = t.Card; AutoSize = false; Visible = false; Padding = new Padding(12, 9, 12, 9); Font = Ui.F(9.5f); Margin = new Padding(0, 0, 0, 10);
        UseMnemonic = false; BackColor = Color.Transparent;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
    }
    public void Set(string text, char kind)
    {
        Text = text;
        (fill, ForeColor) = kind switch { 'o' => (t.OkBg, t.Ok), 'w' => (t.WarnBg, t.Warn), 'b' => (t.BadBg, t.Bad), _ => (t.Card, t.Muted) };
        Visible = text.Length > 0;
        Fit(); Invalidate();
    }
    private const TextFormatFlags Flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl;
    /// <summary>Højden følger teksten ved den aktuelle bredde (også efter DPI-skalering).</summary>
    private void Fit()
    {
        fitWidth = Width;
        if (Text.Length == 0) return;
        Height = TextRenderer.MeasureText(Text, Font, new Size(Math.Max(1, Width - Padding.Horizontal), 0), Flags).Height + Padding.Vertical;
    }
    protected override void OnResize(EventArgs e) { base.OnResize(e); if (Width != fitWidth) Fit(); }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); Fit(); }
    protected override void OnPaintBackground(PaintEventArgs e) { }   // alt males (uigennemsigtigt) i OnPaint
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        UiDraw.Pill(g, Size, t, Ui.Surface(this, t.Bg), fill, Ui.Scale(this));
        if (Text.Length == 0) return;
        var r = new Rectangle(Padding.Left, Padding.Top, Width - Padding.Horizontal, Height - Padding.Vertical);
        TextRenderer.DrawText(g, Text, Font, r, ForeColor, Flags);
    }
}

/// <summary>Fremdriftsbjælke (et stykke på 35 %, når procenten er ukendt).</summary>
public sealed class Bar : Control
{
    private readonly Theme t;
    private int? value;
    public Bar(Theme theme)
    {
        t = theme; Height = 6; Margin = new Padding(0, 10, 0, 0); TabStop = false;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
    }
    public int? Value { get => value; set { if (this.value == value) return; this.value = value; Invalidate(); } }
    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override void OnPaint(PaintEventArgs e) => UiDraw.Bar(e.Graphics, Size, t, Ui.Surface(this, t.Card), Ui.Scale(this), value);
}

/// <summary>Logo + titel + undertitel øverst i et vindue.</summary>
public sealed class Hero : TableLayoutPanel
{
    public Label Title { get; }
    public Label Sub { get; }
    public Hero(Theme t, string title, string sub, int width)
    {
        ColumnCount = 2; RowCount = 1; AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink; Ui.FixWidth(this, width); BackColor = Color.Transparent;
        Margin = new Padding(0, 0, 0, 16);
        ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        // billedet i skærmens egne pixels (48 logiske) – PictureBox skalerer kun, hvis vinduet flyttes til en skærm med anden DPI
        var logo = new PictureBox { Image = Ui.Logo((int)Math.Round(48 * DeviceDpi / 96f)), SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(48, 48), Margin = new Padding(0, 0, 14, 0), BackColor = Color.Transparent, Anchor = AnchorStyles.Left };
        var col = Ui.Stack(width - 62); col.Anchor = AnchorStyles.Left;
        Title = Ui.Text(title, t, 15f, FontStyle.Bold, maxWidth: width - 62); Sub = Ui.Text(sub, t, 10f, muted: true, maxWidth: width - 62);
        col.Controls.Add(Title); col.Controls.Add(Sub);
        Controls.Add(logo, 0, 0); Controls.Add(col, 1, 0);
    }
}

/// <summary>En række med titel, forklaring og en kontrol til højre (fx en kontakt eller knap).</summary>
public sealed class Row : TableLayoutPanel
{
    /// <summary>
    /// Plads til kontrollen til højre i logiske pixels. Kontroller med AutoSize kender ikke deres bredde, før de er lagt ud
    /// (og så i skærmens pixels), så de får en fast reserve – teksten til venstre ombrydes i stedet for at blive klippet.
    /// </summary>
    public const int AutoReserve = 104;
    public Row(Theme t, string title, string? sub, Control right, int width, bool divider = true)
    {
        ColumnCount = 2; RowCount = 1; AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink; Ui.FixWidth(this, width); BackColor = Color.Transparent;
        Margin = new Padding(0); Padding = new Padding(0, 10, 0, 10);
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var reserve = (right.AutoSize ? AutoReserve : right.Width) + right.Margin.Horizontal;
        var textW = width - reserve;
        var col = Ui.Stack(textW); col.Anchor = AnchorStyles.Left;
        col.Controls.Add(Ui.Text(title, t, 10f, maxWidth: textW));
        if (!string.IsNullOrEmpty(sub)) col.Controls.Add(Ui.Text(sub, t, 8.75f, muted: true, maxWidth: textW));
        right.Anchor = AnchorStyles.Right;
        Controls.Add(col, 0, 0); Controls.Add(right, 1, 0);
        if (divider) Paint += (_, e) => UiDraw.Divider(e.Graphics, Width, t, Ui.Scale(this));
    }
}
