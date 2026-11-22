using System.Diagnostics;
using System.Drawing;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Fleetdeck.Shared;

namespace Fleetdeck.Agent.Windows;

/// <summary>
/// Opsætningsvinduet (dobbeltklik på Fleetdeck.exe, når Fleetdeck ikke er installeret – eller "Skift server" fra ikonet) med forslag B's
/// opbygning og Windows 11-udtryk: navigation til venstre med trinene Velkommen → Server → Tilmelding → Profil og tilvalg → Installation →
/// Færdig, én skærm ad gangen, fejl ved det felt, de handler om, Segoe UI Variable, afrundede kort, accentfarve, Mica og lyst/mørkt efter
/// Windows. Trin, skærme og tekster kommer fra <see cref="SetupFlow"/>/<see cref="SetupTexts"/> – de samme som på Mac og Linux.
/// Er Fleetdeck installeret, vises en oversigt (Oversigt, Server og tilmelding, Tilvalg) med "Skift server eller profil…".
/// </summary>
public sealed class SetupForm : Form
{
    private const int W = 480;   // indholdets bredde (logiske pixels)
    private readonly Theme t = Theme.Current;
    private readonly SetupFlow flow;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly Panel side, scroll, foot;
    private Control? page;
    private FlatBtn? primary;

    // det, brugeren har skrevet (felterne bygges igen, når skærmen skifter)
    private string server = "", key = "", message = "", machineName = Environment.MachineName;
    private List<ProfileOption> profiles = [];
    private int? profileId, wantedProfile;
    private string profilesFor = "";
    private readonly Dictionary<string, bool> extOn = Extensions.BuiltIn.Where(e => e.Windows is not null && e.Id != "winget").ToDictionary(e => e.Id, e => Extensions.DefaultOn.Contains(e.Id));
    private bool provision, smartApp = SmartAppControl.Active;
    private TrayStatus? status;
    private string section = "overview";
    private (string Text, SetupTone Tone, string? Sub)? note;
    private bool busy, details;
    private int installAt;
    private System.Windows.Forms.Timer? waiting;   // efter en anmodning: følger status.json, til pc'en er tilmeldt eller afvist

    public SetupForm(string? currentServer)
    {
        flow = new SetupFlow(SetupOs.Windows, installed: currentServer is not null);
        if (currentServer is { Length: > 0 }) server = currentServer;
        status = flow.Installed ? ReadStatus() : null;

        Ui.BeginDpi(this);   // alle mål herunder er logiske (100 %) og skaleres efter skærmen
        Text = "Fleetdeck"; Icon = Ui.AppIcon(32); BackColor = t.Bg; Font = Ui.F(10f);
        // kan ændre størrelse og holdes inden for skærmens arbejdsområde: ved høj skalering på en lav skærm (1080p ved 175-200 %,
        // 1366×768 ved 125 %) er 600 logiske pixels højere end skærmen, og bundlinjen med Fortsæt/Installér skal altid kunne nås
        FormBorderStyle = FormBorderStyle.Sizable; MaximizeBox = false; StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(820, 600);
        scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = t.Bg, Padding = new Padding(0, 28, 0, 12) };
        foot = new Panel { Dock = DockStyle.Bottom, Height = 64, BackColor = t.Bg };
        foot.Paint += (_, e) => { using var p = new Pen(t.Border, 1); e.Graphics.DrawLine(p, 0, 0, foot.Width, 0); };
        side = new Panel { Dock = DockStyle.Left, Width = 240, BackColor = SideColor(t), Padding = new Padding(10, 14, 10, 12) };
        side.Paint += (_, e) => { using var p = new Pen(t.Border, 1); e.Graphics.DrawLine(p, side.Width - 1, 0, side.Width - 1, side.Height); };
        Controls.Add(scroll); Controls.Add(foot); Controls.Add(side);
        Ui.EndDpi(this);
        scroll.Resize += (_, _) => Center();
        Load += (_, _) => { Backdrop.Apply(this, t); FitToScreen(); };
        DpiChanged += (_, _) => BeginInvoke(FitToScreen);
        ResizeEnd += (_, _) => FitToScreen();
        FormClosed += (_, _) => waiting?.Stop();
        Render();
    }

    /// <summary>
    /// Vinduet må aldrig være større end skærmens arbejdsområde (siden ruller, bundlinjen bliver stående). Mindstemålet er logisk 600×360,
    /// men aldrig større end skærmen.
    /// </summary>
    private void FitToScreen()
    {
        var wa = Screen.FromControl(this).WorkingArea;
        var sc = DeviceDpi / 96f;
        var chromeW = Width - ClientSize.Width; var chromeH = Height - ClientSize.Height;
        MinimumSize = new Size(Math.Min(wa.Width, (int)(600 * sc) + chromeW), Math.Min(wa.Height, (int)(360 * sc) + chromeH));
        var size = new Size(Math.Min(Width, wa.Width), Math.Min(Height, wa.Height));
        if (size != Size) Size = size;
        Location = new Point(Math.Max(wa.Left, Math.Min(Left, wa.Right - Width)), Math.Max(wa.Top, Math.Min(Top, wa.Bottom - Height)));
    }

    /// <summary>Navigationens flade: som Windows 11's Mica-tonede sidepanel (lidt mørkere/lysere end indholdet).</summary>
    public static Color SideColor(Theme t) => t.Dark ? Color.FromArgb(255, 0x1a, 0x1b, 0x1e) : Color.FromArgb(255, 0xeb, 0xee, 0xf3);

    // ================================================================ tegning

    private void Render()
    {
        // fokus (og markøren i et tekstfelt) sættes tilbage på den nye udgave af kontrollen – ellers forsvinder feltet, man skriver i,
        // og tastatur/Narrator starter forfra ved hver gentegning
        var focusKey = FocusKey(FocusedLeaf());
        var (selStart, selLen) = FocusedLeaf() is TextBox ftb ? (ftb.SelectionStart, ftb.SelectionLength) : (0, 0);
        SuspendLayout();
        foreach (Control c in side.Controls.Cast<Control>().ToList()) { side.Controls.Remove(c); c.Dispose(); }
        foreach (Control c in foot.Controls.Cast<Control>().ToList()) { foot.Controls.Remove(c); c.Dispose(); }
        if (page is not null) { scroll.Controls.Remove(page); page.Dispose(); }

        side.Controls.Add(Ui.Scaled(Sidebar(), this));
        page = Ui.Scaled(Body(), this);
        scroll.Controls.Add(page);
        var footer = Ui.Scaled(Footer(), this);
        footer.Dock = DockStyle.Fill;
        foot.Controls.Add(footer);
        Center();
        UpdateButtons();
        ResumeLayout(true);
        if (focusKey is not null && All(this).FirstOrDefault(c => FocusKey(c) == focusKey && c.CanFocus) is { } again)
        {
            again.Focus();
            if (again is TextBox tb) { tb.SelectionStart = Math.Min(selStart, tb.TextLength); tb.SelectionLength = Math.Min(selLen, tb.TextLength - tb.SelectionStart); }
        }
        Announce();
    }

    /// <summary>Den kontrol, der har fokus (inde i paneler).</summary>
    private Control? FocusedLeaf()
    {
        Control? c = ActiveControl;
        while (c is ContainerControl { ActiveControl: { } inner }) c = inner;
        return c;
    }

    /// <summary>Fast nøgle for en fokuserbar kontrol på tværs af gentegninger (type + navn).</summary>
    private static string? FocusKey(Control? c) => c switch
    {
        null => null,
        TextBox tb => "tb:" + tb.AccessibleName + "|" + tb.PlaceholderText,
        ChoiceRow or NavRow or FlatBtn or CheckBox => c.GetType().Name + ":" + (c.AccessibleName ?? c.Text),
        _ => null,
    };

    private static IEnumerable<Control> All(Control root)
    {
        foreach (Control c in root.Controls)
        {
            yield return c;
            foreach (var d in All(c)) yield return d;
        }
    }

    private string? announced;

    /// <summary>Nye fejl læses op af Narrator, når de dukker op (én gang – ikke ved hver gentegning).</summary>
    private void Announce()
    {
        var bad = page is null ? null : string.Join(" ", All(page).OfType<IconText>().Where(x => x.Tone == SetupTone.Bad).Select(x => x.Spoken));
        if (string.IsNullOrEmpty(bad)) { announced = null; return; }
        if (bad == announced || !IsHandleCreated) return;
        announced = bad;
        try { page!.AccessibilityObject.RaiseAutomationNotification(System.Windows.Forms.Automation.AutomationNotificationKind.Other,
            System.Windows.Forms.Automation.AutomationNotificationProcessing.ImportantMostRecent, bad); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }   // ingen UIA (fx ældre Windows): fejlen står stadig ved feltet
    }

    private void Center()
    {
        if (page is null) return;
        page.Left = Math.Max(0, (scroll.ClientSize.Width - page.Width) / 2);
        page.Top = scroll.Padding.Top + scroll.AutoScrollPosition.Y;
    }

    private Control Sidebar()
    {
        var sp = Ui.Stack(220);
        sp.Dock = DockStyle.Fill;
        var ident = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(8, 6, 0, 18) };
        ident.Controls.Add(new PictureBox { Image = Ui.Logo((int)Math.Round(36 * DeviceDpi / 96f)), SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(36, 36), Margin = new Padding(0, 0, 10, 0) }, 0, 0);
        var col = Ui.Stack(160);
        col.Controls.Add(Ui.Text("Fleetdeck", t, 10.5f, FontStyle.Bold));
        if (flow.Screen == SetupScreen.Installed)
        {
            var (head, kind) = Headline(status);
            var l = Ui.Text("● " + head, t, 8.5f); l.ForeColor = kind == 'o' ? t.Ok : kind == 'w' ? t.Warn : t.Bad; col.Controls.Add(l);
        }
        else col.Controls.Add(Ui.Text(SetupTexts.Subtitle(SetupOs.Windows), t, 8.5f, muted: true));
        ident.Controls.Add(col, 1, 0);
        sp.Controls.Add(ident);
        if (flow.Screen == SetupScreen.Installed)
            foreach (var s in SetupTexts.Sections(SetupOs.Windows))
            {
                var id = s.Id;
                var r = new NavRow(t, s.Icon, SetupDraw.Hex(s.Color), s.Title, null, section == id ? SetupStepState.Current : SetupStepState.Todo, 220, plain: true);
                r.Click += (_, _) => { section = id; Render(); };
                sp.Controls.Add(r);
            }
        else
            foreach (var st in SetupTexts.Steps(SetupOs.Windows))
                sp.Controls.Add(new NavRow(t, st.Icon, SetupDraw.Hex(st.Color), st.Title, flow.StepNote(st.Step), flow.StateOf(st.Step), 220));
        var ver = Ui.Text($"Fleetdeck {typeof(SetupForm).Assembly.GetName().Version?.ToString(3)}", t, 8.5f, muted: true);
        ver.Margin = new Padding(10, 24, 0, 0);
        if (flow.Screen is not (SetupScreen.Welcome or SetupScreen.Installed) && flow.ServerName() is { } srv && flow.Step > SetupStep.Server)
        {
            var sv = Ui.Text("● " + srv, t, 8.5f); sv.ForeColor = flow.Screen is SetupScreen.Waiting or SetupScreen.NoConnection ? t.Warn : t.Ok; sv.Margin = new Padding(10, 24, 0, 0);
            sp.Controls.Add(sv); ver.Margin = new Padding(10, 2, 0, 0);
        }
        sp.Controls.Add(ver);
        return sp;
    }

    private Control Footer()
    {
        var p = new TableLayoutPanel { ColumnCount = 5, RowCount = 1, BackColor = Color.Transparent, Padding = new Padding(20, 14, 20, 14), Margin = Padding.Empty };
        p.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); p.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        p.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); p.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var help = new FlatBtn(t, "?") { MinimumSize = new Size(34, 34), Padding = Padding.Empty, Margin = Padding.Empty };
        help.Click += (_, _) => { var (title, text) = SetupTexts.HelpFor(SetupOs.Windows, flow.Step); MessageBox.Show(this, text, title, MessageBoxButtons.OK, MessageBoxIcon.Information); };
        p.Controls.Add(help, 0, 0);
        var pg = flow.Page(status?.Machine, status?.Profile);
        if (flow.Installed && flow.Screen == SetupScreen.Installed)
        {
            var remove = new LinkLabel { Text = "Fjern Fleetdeck", AutoSize = true, LinkColor = t.Bad, ActiveLinkColor = t.Bad, VisitedLinkColor = t.Bad, LinkBehavior = LinkBehavior.HoverUnderline,
                UseMnemonic = false, BackColor = Color.Transparent, Anchor = AnchorStyles.Left, Font = Ui.F(9.5f), Margin = new Padding(14, 0, 0, 0) };
            remove.LinkClicked += async (_, _) => await Remove();
            p.Controls.Add(remove, 1, 0);
        }
        if (pg.Secondary is { } sec)
        {
            var b = new FlatBtn(t, sec) { Enabled = !busy };
            b.Click += (_, _) => Secondary();
            p.Controls.Add(b, 3, 0);
        }
        primary = null;
        if (pg.Primary is { } pri)
        {
            primary = new FlatBtn(t, busy && flow.Screen != SetupScreen.Installing ? "Et øjeblik…" : pri, primary: true);
            primary.Click += async (_, _) => await Primary();
            p.Controls.Add(primary, 4, 0);
            AcceptButton = primary;
        }
        return p;
    }

    private void UpdateButtons()
    {
        if (primary is null) return;
        primary.Enabled = !busy && (flow.Screen == SetupScreen.Installed || flow.CanContinue(Discovery.Normalize(server).Length > 0, EnrollKey.Parse(key).Key.Length > 0, profileId is not null));
    }

    // ---------------------------------------------------------------- byggeklodser

    private Control Hero(SetupPage pg, Control? icon = null, bool big = false)
    {
        var sp = Ui.Stack(W);
        var ic = icon ?? new IconBox(pg.Icon, SetupDraw.Hex(pg.Color), 52, tile: true);
        ic.Anchor = AnchorStyles.None; ic.Margin = new Padding(0, 0, 0, 12);
        sp.Controls.Add(ic);
        var title = Ui.Text(pg.Title, t, big ? 19f : 16f, FontStyle.Bold, maxWidth: W); title.Anchor = AnchorStyles.None; title.TextAlign = ContentAlignment.MiddleCenter;
        var text = Ui.Text(pg.Text, t, 10f, muted: true, maxWidth: 420); text.Anchor = AnchorStyles.None; text.TextAlign = ContentAlignment.MiddleCenter;
        sp.Controls.Add(title); sp.Controls.Add(text);
        sp.Margin = new Padding(0, 0, 0, 16);
        return sp;
    }

    private Label Sec(string text, string? light = null)
    {
        var l = Ui.Text(text + (light is null ? "" : "  " + light), t, 10f, FontStyle.Bold); l.Margin = new Padding(2, 14, 0, 6);
        return l;
    }

    private Label Note(string text) { var l = Ui.Text(text, t, 8.75f, muted: true, maxWidth: W); l.Margin = new Padding(2, 6, 0, 0); return l; }

    /// <summary>Kort med rækker uden luft (grupperet liste).</summary>
    private Card Group(params Control[] rows)
    {
        var c = new Card(t, W) { Padding = new Padding(1, 4, 1, 4) };
        foreach (var r in rows) { r.Margin = Padding.Empty; c.Controls.Add(r); }
        return c;
    }

    /// <summary>Række: ikon + titel + undertekst + kontrol til højre.</summary>
    private Control Line(Control? lead, string title, string? sub, Control? right = null, bool dim = false)
    {
        var p = new TableLayoutPanel { ColumnCount = 3, RowCount = 1, AutoSize = true, BackColor = Color.Transparent, Padding = new Padding(14, 8, 14, 8), Margin = Padding.Empty };
        Ui.FixWidth(p, W - 2);
        p.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); p.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        if (lead is not null) { lead.Margin = new Padding(0, 2, 12, 0); lead.Anchor = AnchorStyles.Left; p.Controls.Add(lead, 0, 0); }
        var tw = W - 2 - 28 - (lead is null ? 0 : 40) - (right is null ? 0 : 110);
        var col = Ui.Stack(tw); col.Anchor = AnchorStyles.Left;
        var tl = Ui.Text(title, t, 10f, maxWidth: tw); if (dim) tl.ForeColor = t.Muted;
        col.Controls.Add(tl);
        if (sub is { Length: > 0 }) col.Controls.Add(Ui.Text(sub, t, 8.75f, muted: true, maxWidth: tw));
        p.Controls.Add(col, 1, 0);
        if (right is not null) { right.Anchor = AnchorStyles.Right; p.Controls.Add(right, 2, 0); }
        return p;
    }

    private Field ServerField(bool error)
    {
        var f = new Field(t, SetupTexts.ServerPlaceholder, mono: false) { Text = server };
        f.Box.AccessibleName = "Serverens adresse"; f.Box.AccessibleDescription = flow.ErrorFor(SetupField.Server)?.Title;
        Ui.FixWidth(f, W - 32);
        f.Box.TextChanged += (_, _) =>
        {
            if (f.Text.Contains("key=") || f.Text.Contains("#k=")) { FillFromCommand(f.Text); Render(); return; }
            server = f.Text; flow.Server = Discovery.Normalize(server);
            if (flow.ServerCheck is not null) flow.ServerChanged();
            UpdateButtons();
        };
        if (error) f.BackColor = UiDraw.Blend(t.Field, t.Bad, 0.08f);
        return f;
    }

    private Card FieldCard(Control field, Control? button = null)
    {
        var c = new Card(t, W) { Padding = new Padding(16, 12, 16, 12) };
        if (button is null) { c.Controls.Add(field); return c; }
        var p = Ui.FixWidth(new TableLayoutPanel { ColumnCount = 2, RowCount = 1, AutoSize = true, BackColor = Color.Transparent, Margin = Padding.Empty }, W - 32);
        p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); p.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        field.Dock = DockStyle.Fill; field.Margin = Padding.Empty; button.Margin = new Padding(8, 0, 0, 0);
        p.Controls.Add(field, 0, 0); p.Controls.Add(button, 1, 0);
        c.Controls.Add(p);
        return c;
    }

    private IconText Msg(SetupTone tone, string title, string? text = null, bool spin = false) => new(t, tone, title, text, W, spin);

    // ---------------------------------------------------------------- skærmene

    private Control Body()
    {
        var sp = Ui.Stack(W);
        void Add(Control c) => sp.Controls.Add(c);
        var pg = flow.Page(status?.Machine, status?.Profile);
        switch (flow.Screen)
        {
            case SetupScreen.Welcome:
            {
                var logo = new PictureBox { Image = Ui.Logo((int)Math.Round(88 * DeviceDpi / 96f)), SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(88, 88), BackColor = Color.Transparent };
                Add(Hero(pg, logo, big: true));
                foreach (var (icon, title, text) in SetupTexts.Features(SetupOs.Windows))
                {
                    var l = Line(new IconBox(icon, t.Accent, 22), title, text);
                    l.Padding = new Padding(18, 4, 18, 4);
                    Add(l);
                }
                var paste = new FlatBtn(t, SetupTexts.Paste);
                paste.Click += (_, _) => PasteLink();
                var tip = new Card(t, W) { Padding = new Padding(2, 4, 2, 4), Margin = new Padding(0, 14, 0, 0) };
                tip.Controls.Add(Line(new IconBox("clip", t.Accent, 20), SetupTexts.PasteTitle, SetupTexts.PasteText, paste));
                Add(tip);
                if (note is { } n) Add(Msg(n.Tone, n.Text, n.Sub));
                break;
            }
            case SetupScreen.Searching or SetupScreen.Found or SetupScreen.NoServer or SetupScreen.ServerError:
            {
                Add(Hero(pg));
                if (flow.Screen == SetupScreen.ServerError)
                {
                    Add(Sec("Adresse"));
                    Add(FieldCard(ServerField(true)));
                    if (flow.ErrorFor(SetupField.Server) is { } e)
                    {
                        var cert = SetupProbe.IsCertificateText(e);
                        Add(Msg(cert ? SetupTone.Warn : SetupTone.Bad, e.Title, e.Text));
                        if (e.Detail is { Length: > 0 } d)
                        {
                            var link = new LinkLabel { Text = (details ? "▾ " : "▸ ") + SetupTexts.Details, AutoSize = true, LinkColor = t.Muted, ActiveLinkColor = t.Accent, VisitedLinkColor = t.Muted,
                                LinkBehavior = LinkBehavior.HoverUnderline, BackColor = Color.Transparent, Font = Ui.F(8.75f), Margin = new Padding(24, 4, 0, 0) };
                            link.LinkClicked += (_, _) => { details = !details; Render(); };
                            Add(link);
                            if (details) { var dl = Ui.Text(d, t, 8.5f, muted: true, maxWidth: W - 24); dl.Font = new Font("Cascadia Mono", 8.5f); dl.Margin = new Padding(24, 2, 0, 0); Add(dl); }
                        }
                    }
                    if (flow.Found.Count > 0) { Add(Sec(SetupTexts.OnNetwork)); Add(FoundList()); }
                    break;
                }
                var head = Ui.FixWidth(new TableLayoutPanel { ColumnCount = 2, AutoSize = true, BackColor = Color.Transparent, Margin = Padding.Empty }, W);
                head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                head.Controls.Add(Sec(SetupTexts.OnNetwork), 0, 0);
                if (flow.Screen == SetupScreen.Found)
                {
                    var again = new LinkLabel { Text = "⟳ " + SetupTexts.SearchAgain, AutoSize = true, LinkColor = t.Accent, ActiveLinkColor = t.Accent, VisitedLinkColor = t.Accent,
                        LinkBehavior = LinkBehavior.HoverUnderline, BackColor = Color.Transparent, Font = Ui.F(9.5f), Anchor = AnchorStyles.Right | AnchorStyles.Bottom, Margin = new Padding(0, 0, 0, 6) };
                    again.LinkClicked += async (_, _) => await Search();
                    head.Controls.Add(again, 1, 0);
                }
                Add(head);
                if (flow.Screen == SetupScreen.Searching)
                {
                    var c = new Card(t, W) { Padding = new Padding(16, 30, 16, 30) };
                    var row = new FlowLayoutPanel { AutoSize = true, BackColor = Color.Transparent, Anchor = AnchorStyles.None, Margin = Padding.Empty, WrapContents = false };
                    row.Controls.Add(new Ring(t, 18) { Margin = new Padding(0, 1, 8, 0) });
                    row.Controls.Add(Ui.Text(SetupTexts.SearchingRow, t, 10f, muted: true));
                    c.Controls.Add(row);
                    Add(c);
                }
                else if (flow.Screen == SetupScreen.Found) Add(FoundList());
                else
                {
                    var c = new Card(t, W) { Padding = new Padding(16, 18, 16, 18) };
                    var ic = new IconBox("wifix", t.Muted, 30) { Anchor = AnchorStyles.None };
                    var h = Ui.Text(SetupTexts.NoServerTitle, t, 10f, FontStyle.Bold); h.Anchor = AnchorStyles.None;
                    var x = Ui.Text(SetupTexts.NoServerText, t, 9f, muted: true, maxWidth: 340); x.Anchor = AnchorStyles.None; x.TextAlign = ContentAlignment.MiddleCenter;
                    var b = new FlatBtn(t, SetupTexts.SearchAgain) { Anchor = AnchorStyles.None, Margin = new Padding(0, 8, 0, 0) };
                    b.Click += async (_, _) => await Search();
                    foreach (var k in new Control[] { ic, h, x, b }) c.Controls.Add(k);
                    Add(c);
                }
                Add(Sec(SetupTexts.OrTypeAddress));
                Add(FieldCard(ServerField(false)));
                Add(Note(SetupTexts.WhereIsAddress));
                break;
            }
            case SetupScreen.Key or SetupScreen.KeyError or SetupScreen.Request:
            {
                Add(Hero(pg));
                Add(Choices());
                if (flow.Screen == SetupScreen.Request)
                {
                    for (var i = 0; i < SetupTexts.RequestFlow.Length; i++)
                    {
                        var l = Ui.Text($"{i + 1}.  {SetupTexts.RequestFlow[i]}", t, 9.5f); l.Margin = new Padding(4, i == 0 ? 14 : 4, 0, 0); Add(l);
                    }
                    Add(Sec("Pc'ens navn"));
                    var name = new Field(t, Environment.MachineName, mono: false) { Text = machineName };
                    name.Box.MaxLength = 31; name.Box.CharacterCasing = CharacterCasing.Upper; name.Box.AccessibleName = "Pc'ens navn";
                    name.Box.TextChanged += (_, _) => machineName = name.Text;
                    Ui.FixWidth(name, W - 32);
                    Add(FieldCard(name));
                    Add(Sec(SetupTexts.MessageSection, SetupTexts.Optional));
                    var msg = new TextArea(t, SetupWords.For(SetupOs.Windows).Example, 58) { Text = message };
                    msg.Box.MaxLength = 200; msg.Box.AccessibleName = SetupTexts.MessageSection; msg.Box.TextChanged += (_, _) => message = msg.Text;
                    Ui.FixWidth(msg, W - 32);
                    Add(FieldCard(msg));
                    if (busy) Add(Msg(SetupTone.Info, "Installerer og sender anmodningen…", "Windows spørger om lov til at installere.", spin: true));
                }
                else
                {
                    Add(Sec(SetupTexts.KeySection));
                    var f = new Field(t, SetupTexts.KeyPlaceholder, password: true) { Text = key };
                    var err = flow.ErrorFor(SetupField.Key);
                    f.Box.AccessibleName = SetupTexts.KeySection; f.Box.AccessibleDescription = err?.Title;
                    Control? result = null;   // "Nøglen passer" / fejlen under feltet
                    f.Box.TextChanged += (_, _) =>
                    {
                        if (f.Text.Contains("key=") || f.Text.Contains("#k=")) { FillFromCommand(f.Text); Render(); return; }
                        key = f.Text;
                        if (flow.KeyCheck is null) { UpdateButtons(); return; }
                        // nøglen er ændret efter testen: fjern svaret og fejlstilen på stedet – gentegn ikke siden midt i indtastningen
                        flow.KeyChanged();
                        f.BackColor = t.Field; f.Box.AccessibleDescription = null; f.Invalidate();
                        if (result is not null) { result.Parent?.Controls.Remove(result); result.Dispose(); result = null; }
                        announced = null;
                        UpdateButtons();
                    };
                    if (err is not null) f.BackColor = UiDraw.Blend(t.Field, t.Bad, 0.08f);
                    var paste = new FlatBtn(t, SetupTexts.Paste);
                    paste.Click += async (_, _) => { var x = Clipboard.ContainsText() ? Clipboard.GetText().Trim() : ""; if (!FillFromCommand(x)) key = x; Render(); await TestKeyOnly(); };
                    Add(FieldCard(f, paste));
                    if (flow.KeyCheck is { Ok: true } ok) Add(result = Msg(SetupTone.Ok, ok.Title, ok.Text));
                    else if (err is not null) Add(result = Msg(SetupTone.Bad, err.Title, err.Text));
                    else if (busy) Add(Msg(SetupTone.Info, "Tjekker nøglen…", null, spin: true));
                }
                if (flow.ActionError is { } a) Add(Msg(SetupTone.Bad, a.Title, a.Text));
                break;
            }
            case SetupScreen.Waiting:
            {
                Add(Hero(pg));
                Add(new CodeTiles(t, flow.Code ?? "…", W));
                var copy = new FlatBtn(t, SetupTexts.CopyCode) { Anchor = AnchorStyles.None, Margin = new Padding(0, 0, 0, 16) };
                copy.Click += (_, _) => { if (flow.Code is { Length: > 0 } c) Clipboard.SetText(c); };
                Add(copy);
                var (wt, ws, tone) = SetupTexts.WaitingRow(SetupOs.Windows, status?.AccessRequest);
                Add(Group(Line(tone == SetupTone.Warn ? new IconBox("warn", t.Warn, 18) : new Ring(t, 18), wt, ws),
                          Line(new IconBox("laptop", t.Muted, 20), "Du kan godt lukke vinduet", "Pc'en tilmelder sig selv, når anmodningen er godkendt.")));
                break;
            }
            case SetupScreen.Rejected:
                Add(Hero(pg));
                if (flow.Reason is { Length: > 0 } reason)
                {
                    Add(Sec("Besked fra administratoren"));
                    var c = new Card(t, W); var l = Ui.Text($"“{reason}”", t, 10f, FontStyle.Italic, muted: true, maxWidth: W - 32); c.Controls.Add(l); Add(c);
                }
                Add(Note("Send en ny anmodning med en besked, så administratoren kan se, hvilken pc det er."));
                break;
            case SetupScreen.Profile:
            {
                Add(Hero(pg));
                var rows = new List<Control>();
                foreach (var (id, title, sub) in new[] { (0, SetupTexts.NoProfile, SetupTexts.NoProfileSub) }.Concat(profiles.Select(x => (x.Id, x.Name, SetupTexts.ProfileLine(x)))))
                {
                    var r = new ChoiceRow(t, title, sub, profileId == id, W - 2);
                    r.Click += (_, _) => { profileId = id; Render(); };
                    rows.Add(r);
                }
                Add(Group([.. rows]));
                Add(Sec(SetupTexts.ExtensionsSection));
                var ext = new Card(t, W);
                var first = true;
                foreach (var x in Extensions.BuiltIn.Where(e => e.Windows is not null && e.Id != "winget"))
                {
                    var tg = new Toggle(t) { Checked = extOn.GetValueOrDefault(x.Id) };
                    var id = x.Id; tg.CheckedChanged += (_, _) => extOn[id] = tg.Checked;
                    ext.Controls.Add(new Row(t, x.Name, x.Gives, tg, W - 32, divider: !first)); first = false;
                }
                var prov = new Toggle(t) { Checked = provision }; prov.CheckedChanged += (_, _) => provision = prov.Checked;
                ext.Controls.Add(new Row(t, "Nyinstallation bagefter", "Kun en pc, der lige er sat op fra bunden", prov, W - 32));
                if (SmartAppControl.Active)
                {
                    var sac = new Toggle(t) { Checked = smartApp }; sac.CheckedChanged += (_, _) => smartApp = sac.Checked;
                    ext.Controls.Add(new Row(t, "Slå Smart App Control fra", $"Den er {SmartAppControl.Describe(SmartAppControl.State())} og blokerer programmer, Fleetdeck installerer. Du bekræfter først.", sac, W - 32));
                }
                Add(ext);
                if (flow.ActionError is { } a) Add(Msg(SetupTone.Bad, a.Title, a.Text));
                break;
            }
            case SetupScreen.Installing or SetupScreen.NoConnection:
            {
                Add(Hero(pg));
                var stuck = flow.Screen == SetupScreen.NoConnection;
                var steps = SetupTexts.InstallSteps(SetupOs.Windows);
                var bar = new Bar(t) { Value = stuck ? 92 : (int)((installAt + 0.5) * 100 / steps.Count), Margin = new Padding(0, 0, 0, 16) };
                Ui.FixWidth(bar, W);
                Add(bar);
                var rows = new List<Control>();
                for (var i = 0; i < steps.Count; i++)
                {
                    var (title, sub) = steps[i];
                    if (stuck && i == steps.Count - 1)
                    {
                        rows.Add(Line(new IconBox("warn", t.Warn, 18), "Ingen forbindelse efter 2 minutter",
                            "Agenten prøver selv igen. Tjek, at pc'en er på samme netværk som serveren." + (status?.Error is { Length: > 0 } er ? $" ({er})" : "")));
                        continue;
                    }
                    var done = stuck || i < installAt;
                    Control lead = done ? new IconBox("checkc", t.Ok, 18) : i == installAt ? new Ring(t, 18) : new IconBox("info", t.Border, 18);
                    rows.Add(Line(lead, title, i == installAt && !stuck ? sub : null, dim: !done && i > installAt));
                }
                Add(Group([.. rows]));
                break;
            }
            case SetupScreen.Done:
            {
                Add(Hero(pg, new IconBox(flow.Reboot ? "warn" : "checkc", flow.Reboot ? t.Warn : t.Ok, 72), big: true));
                Add(Group(Line(null, "Server", null, Value(flow.ServerName() ?? status?.Server ?? "–")),
                          Line(null, "Pc'ens navn", null, Value(status?.Machine is { Length: > 0 } m ? m : Environment.MachineName)),
                          Line(null, "Profil", null, Value(status?.Profile is { Length: > 0 } pr ? pr : ProfileName(profileId) ?? "Ingen profil"))));
                var (tt, tx) = SetupTexts.TrayTip(SetupOs.Windows);
                var tip = new Card(t, W) { Padding = new Padding(2, 4, 2, 4), Margin = new Padding(0, 14, 0, 0) };
                tip.Controls.Add(Line(new IconBox("info", t.Accent, 20), tt, tx));
                Add(tip);
                break;
            }
            case SetupScreen.Installed:
                InstalledBody(Add);
                break;
        }
        if (busy && flow.Screen is SetupScreen.Searching or SetupScreen.Found or SetupScreen.NoServer or SetupScreen.ServerError or SetupScreen.Profile)
            Add(Msg(SetupTone.Info, flow.Screen == SetupScreen.Profile ? "Henter profilerne…" : "Tjekker serveren…", null, spin: true));
        return sp;
    }

    private Label Value(string text) { var l = Ui.Text(text, t, 10f, muted: true); l.Margin = Padding.Empty; return l; }

    private Control FoundList()
    {
        var sel = Discovery.Normalize(server);
        return Group([.. flow.Found.Select(f =>
        {
            var on = string.Equals(Discovery.Normalize(f.Url), sel, StringComparison.OrdinalIgnoreCase);
            var r = new ChoiceRow(t, f.Name.Length > 0 ? f.Name : SetupTexts.HostPort(f.Url), SetupTexts.FoundLine(f), on, W - 2, "server", SetupDraw.Hex(on ? SetupColors.Blue : SetupColors.Gray));
            r.Click += (_, _) => { server = f.Url; flow.Server = Discovery.Normalize(f.Url); Render(); };
            return (Control)r;
        })]);
    }

    private Control Choices()
    {
        var p = Ui.FixWidth(new TableLayoutPanel { ColumnCount = 2, RowCount = 1, AutoSize = true, BackColor = Color.Transparent, Margin = Padding.Empty }, W);
        p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        var half = (W - 12) / 2;
        var k = new ChoiceRow(t, SetupTexts.HaveKey, SetupTexts.HaveKeySub, !flow.Ask, half, "key", SetupDraw.Hex(SetupColors.Gray), card: true) { Margin = new Padding(0, 0, 6, 0) };
        var a = new ChoiceRow(t, SetupTexts.AskAccess, SetupTexts.AskAccessSub, flow.Ask, half, "plane", SetupDraw.Hex(SetupColors.Teal), card: true) { Margin = new Padding(6, 0, 0, 0) };
        k.Click += (_, _) => { if (busy) return; flow.ChooseAsk(false); Render(); };
        a.Click += (_, _) => { if (busy) return; flow.ChooseAsk(true); Render(); };
        p.Controls.Add(k, 0, 0); p.Controls.Add(a, 1, 0);
        return p;
    }

    private void InstalledBody(Action<Control> add)
    {
        var title = Ui.Text(SetupTexts.Sections(SetupOs.Windows).First(s => s.Id == section).Title, t, 16f, FontStyle.Bold);
        title.Margin = new Padding(0, 0, 0, 12);
        add(title);
        switch (section)
        {
            case "overview":
                add(Group([.. Summary(status).Select(r => Line(null, r.Label, null, Value(r.Value)))]));
                if (status?.Job is { } j) add(Msg(SetupTone.Info, $"I gang: {j.Title}{(j.Percent is int pc ? $" – {pc} %" : "")}", j.Step));
                break;
            case "server":
                add(Group(Line(new IconBox("server", SetupDraw.Hex(SetupColors.Blue), 28, tile: true), status?.Server is { Length: > 0 } s ? SetupTexts.HostPort(s) : "–",
                    status is { Enrolled: true } ? "Tilmeldt – pc'en har sit eget login. Nøglen skal kun bruges, hvis du skifter server eller profil." : "Ikke tilmeldt endnu.")));
                add(Note("\"Skift server eller profil…\" fører dig gennem trinene igen. Fleetdeck installeres forfra, og pc'en tilmelder sig med den nye nøgle."));
                break;
            case "extensions":
            {
                var ext = status?.Extensions ?? [];
                add(Group([.. Extensions.BuiltIn.Where(e => e.Windows is not null && e.Id != "winget").Select(e =>
                {
                    var v = ext.FirstOrDefault(x => x.Id == e.Id);
                    return Line(null, e.Name, v is { Installed: true } ? "Installeret" : v is { Pending: true } ? "Installeres…" : e.Gives);
                })]));
                add(Note("Tilvalg slås til og fra i Fleetdeck under maskinen. Her ses, hvad pc'en har."));
                break;
            }
        }
        if (note is { } n) add(Msg(n.Tone, n.Text, n.Sub));
    }

    /// <summary>Linjerne i oversigten (som på Mac, uden Fuld diskadgang).</summary>
    public static List<(string Label, string Value)> Summary(TrayStatus? s) =>
        MacSetup.Summary(s, DateTime.UtcNow).Where(r => r.Label != "Fuld diskadgang").ToList();

    public static (string, char) Headline(TrayStatus? s) =>
        TrayView.Of(s, DateTime.UtcNow) switch
        {
            TrayView.State.NoService => (s is null ? "Tjenesten kører ikke" : "Tjenesten svarer ikke", 'b'),
            TrayView.State.Offline => ("Ikke forbundet", 'b'),
            _ => ("Forbundet", 'o'),
        };

    // ================================================================ handlinger

    private async Task Primary()
    {
        if (busy) return;
        switch (flow.Screen)
        {
            case SetupScreen.Welcome:
                note = null;
                if (Discovery.Normalize(server).Length > 0 && EnrollKey.Parse(key).Key.Length > 0) { await FastPath(); return; }
                flow.Start(); await Search();
                break;
            case SetupScreen.Installed:
                flow.Start(); await Search();
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
            case SetupScreen.Done when !flow.Reboot:
                Close();
                break;
            case SetupScreen.Done:
                if (MessageBox.Show(this, "Genstart pc'en nu? Gem dit arbejde først. Fleetdeck starter selv igen bagefter.", "Genstart", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 10 /c \"Fleetdeck: genstarter for at gøre installationen færdig\"") { CreateNoWindow = true, UseShellExecute = false });
                break;
            case SetupScreen.Rejected:
                flow.AskAgain(); Render();
                break;
            case SetupScreen.Profile:
                await Connect();
                break;
            case SetupScreen.NoConnection:
                flow.InstallStarted(); installAt = SetupTexts.InstallSteps(SetupOs.Windows).Count - 1; Render();
                var (rc, ro) = await RunSelf("verify");
                Finish(rc, ro);
                break;
        }
    }

    private void Secondary()
    {
        switch (flow.Screen)
        {
            case SetupScreen.Rejected or SetupScreen.NoConnection or SetupScreen.Done: Close(); break;
            default: flow.Back(); Render(); break;
        }
    }

    private bool FillFromCommand(string text)
    {
        var (srv, k) = EnrollKey.Parse(text);
        if (k == text.Trim() && srv is null) return false;
        if (srv is not null) { server = srv; flow.Server = Discovery.Normalize(srv); }
        key = k;
        if (Regex.Match(text, @"[?&]profile=(\d+)") is { Success: true } pm) wantedProfile = int.Parse(pm.Groups[1].Value);
        return true;
    }

    private void PasteLink()
    {
        var x = Clipboard.ContainsText() ? Clipboard.GetText().Trim() : "";
        note = FillFromCommand(x)
            ? ("Udfyldt fra udklipsholderen.", SetupTone.Ok, $"Server {Discovery.Normalize(server)}, nøgle" + (wantedProfile is int p ? $" og profil {p}" : "") + ". Tryk Fortsæt.")
            : ("Der er intet Fleetdeck-link i udklipsholderen.", SetupTone.Warn, "Kopiér kommandoen fra Fleetdeck under Tilføj maskine, og prøv igen.");
        Render();
    }

    private async Task FastPath()
    {
        busy = true; Render();
        var url = Discovery.Normalize(server); var k = EnrollKey.Parse(key).Key;
        flow.Server = url;
        var c = await SetupProbe.Test(Http, url, k, SetupOs.Windows);
        busy = false;
        flow.Start();
        flow.ServerTested(c.Field == SetupField.Server ? c : SetupCheck.Good("Forbindelse OK.", url, c.Version));
        if (c.Field != SetupField.Server) { flow.KeyTested(c); if (c.Ok) await LoadProfilesAndGo(url, k); }
        Render();
    }

    private async Task Search()
    {
        flow.SearchStarted(); Render();
        var list = await Discovery.Search(TimeSpan.FromSeconds(2.5));
        flow.SearchDone(list);
        if (server.Length == 0 && flow.Server.Length > 0) server = flow.Server;
        Render();
    }

    private async Task TestServer()
    {
        var url = Discovery.Normalize(server); flow.Server = url;
        busy = true; Render();
        var c = await SetupProbe.Test(Http, url, null, SetupOs.Windows);
        busy = false;
        flow.ServerTested(c);
        Render();
    }

    private async Task TestKeyOnly()
    {
        var url = Discovery.Normalize(server); var k = EnrollKey.Parse(key).Key;
        if (k.Length == 0) return;
        busy = true; Render();
        flow.KeyTested(await SetupProbe.Test(Http, url, k, SetupOs.Windows));
        busy = false; Render();
    }

    private async Task TestKeyAndLoad()
    {
        var url = Discovery.Normalize(server); var k = EnrollKey.Parse(key).Key;
        if (k.Length == 0) { flow.KeyTested(SetupCheck.Bad(SetupField.Key, "Indsæt tilmeldingsnøglen.", SetupTexts.HaveKeySub + ".")); Render(); return; }
        busy = true; Render();
        if (flow.KeyCheck is not { Ok: true }) flow.KeyTested(await SetupProbe.Test(Http, url, k, SetupOs.Windows));
        if (flow.KeyCheck is { Ok: true }) await LoadProfilesAndGo(url, k);
        busy = false; Render();
    }

    private async Task LoadProfilesAndGo(string url, string k)
    {
        if (await LoadProfiles(url, k)) flow.ProfilesLoaded();
        else { flow.ProfilesFailed(); flow.KeyTested(flow.KeyCheck!); }
    }

    /// <summary>Henter profilerne fra serveren (kræver nøglen). Der skal altid vælges aktivt (en indsat kommando kan forvælge én).</summary>
    private async Task<bool> LoadProfiles(string url, string k)
    {
        if (profilesFor == $"{url}|{k}") return true;
        try
        {
            profiles = await Http.GetFromJsonAsync<List<ProfileOption>>($"{url}/api/agent/profiles?key={Uri.EscapeDataString(k)}",
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)) ?? [];
            profileId = profiles.FirstOrDefault(p => p.Id == wantedProfile)?.Id;
            profilesFor = $"{url}|{k}";
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or UriFormatException or InvalidOperationException or System.Text.Json.JsonException) { return false; }
    }

    private string? ProfileName(int? id) => id is null or 0 ? null : profiles.FirstOrDefault(x => x.Id == id)?.Name;

    private async Task Connect()
    {
        if (profileId is not int pid) return;
        var url = Discovery.Normalize(server); var k = EnrollKey.Parse(key).Key;
        var sac = false;
        if (smartApp && SmartAppControl.Active)
        {
            var a = MessageBox.Show(this,
                "Smart App Control er slået til på denne pc og blokerer programmer, som Fleetdeck installerer og opdaterer.\n\n" +
                "Vil du slå den fra nu?\n\n" +
                "Bemærk: Microsoft tillader ikke at slå Smart App Control til igen uden at nulstille eller geninstallere Windows. " +
                "Microsoft Defender og resten af Windows-sikkerheden beskytter fortsat pc'en.\n\n" +
                "Ja = slå den fra og installér.   Nej = installér uden at slå den fra.",
                "Slå Smart App Control fra?", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (a == DialogResult.Cancel) return;
            sac = a == DialogResult.Yes;
        }
        flow.InstallStarted(); installAt = 1; Render();
        var ext = string.Join(',', extOn.Where(e => e.Value).Select(e => e.Key));
        var (code, output) = await RunSelf($"install --server \"{url}\" --key \"{k}\" --profile {pid}" + (provision ? " --provision" : "") + (sac ? " --disable-smartapp" : "") + (ext.Length > 0 ? $" --extensions {ext}" : ""));
        Finish(code, output);
    }

    // Resultatet af install/verify: 0 = forbundet, 8 = forbundet men genstart mangler, 7 = installeret uden forbindelse endnu, 6 = tjenesten vil ikke starte
    private void Finish(int code, string output)
    {
        var msg = SetupProbe.LastLine(output);
        if (code is 0 or 7 or 8)
        {
            // ikonet i proceslinjen som den indloggede bruger (Stifinder starter det uden administrator)
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Installer.Exe}\"") { UseShellExecute = false }); } catch (Exception) { }
            status = ReadStatus() ?? status;
            flow.InstallFinished(connected: code is 0 or 8, reboot: code == 8);
        }
        else flow.InstallFinished(false, error: code == 3
            ? SetupCheck.Bad(SetupField.Key, "Nøglen blev afvist.", "Hent den igen i Fleetdeck under Tilføj maskine – den kan være skiftet.")
            : SetupCheck.Bad(SetupField.None, code == 6 ? "Tjenesten kunne ikke starte." : $"Installationen fejlede (kode {code}).", msg));
        Render();
    }

    /// <summary>"Anmod om adgang": installér uden nøgle og bed administratoren om lov. Han vælger profilen, når han godkender.</summary>
    private async Task SendRequest()
    {
        var url = Discovery.Normalize(server);
        // allerede tilmeldt: en ny anmodning fjerner pc'ens token, til administratoren har godkendt den igen
        var enrolled = ReadStatus() is { Enrolled: true };
        if (enrolled && MessageBox.Show(this, "Pc'en er allerede tilmeldt Fleetdeck. Vil du virkelig starte forfra og bede om adgang igen?\n\n" +
                "Pc'en kan ikke styres, før administratoren har godkendt den.", "Fleetdeck", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        busy = true; Render();
        // hvert argument for sig (ArgumentList): en besked med anførselstegn eller en afsluttende \ kan ikke forskyde dem
        List<string> args = ["install", "--server", url, "--request", "--name", Clean(machineName, 31).ToUpperInvariant()];
        if (Clean(message, 200) is { Length: > 0 } m) args.AddRange(["--message", m]);
        if (enrolled) args.Add("--force");
        var sentAt = DateTime.UtcNow;
        var (code, output) = await RunSelf(args);
        busy = false;
        if (code != 9) { flow.ActionFailed(SetupCheck.Bad(SetupField.None, $"Anmodningen fejlede{(code == 3 ? "" : $" (kode {code})")}.", SetupProbe.LastLine(output))); Render(); return; }
        var accessCode = Regex.Match(output, "Kode: (" + AccessCode.Pattern + ")") is { Success: true } mc ? mc.Groups[1].Value : "?";
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Installer.Exe}\"") { UseShellExecute = false }); } catch (Exception) { }
        flow.RequestSent(accessCode, sentAt);
        Render();
        waiting?.Stop();
        waiting = new System.Windows.Forms.Timer { Interval = 3000 };
        waiting.Tick += (_, _) =>
        {
            if (ReadStatus() is not { } st) return;
            var before = (flow.Screen, flow.Code, st.AccessRequest?.Text);
            status = st;
            flow.RequestUpdate(st);
            if (flow.Screen != SetupScreen.Waiting) waiting!.Stop();
            if (before != (flow.Screen, flow.Code, st.AccessRequest?.Text)) Render();
        };
        waiting.Start();
    }

    private async Task Remove()
    {
        if (MessageBox.Show(this, "Fjerne Fleetdeck fra denne pc? Den kan så ikke længere styres.", "Fleetdeck", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        busy = true; Render();
        var (code, output) = await RunSelf("uninstall");
        busy = false;
        if (code == 0) { status = null; flow.Uninstalled(); note = ("Fleetdeck er fjernet fra denne pc.", SetupTone.Ok, null); }
        else note = ("Kunne ikke fjerne Fleetdeck.", SetupTone.Bad, SetupProbe.LastLine(output));
        Render();
    }

    /// <summary>Uden styretegn (teksten sendes som ét argument til agenten).</summary>
    private static string Clean(string s, int max)
    {
        var c = new string([.. s.Where(ch => !char.IsControl(ch))]).Trim();
        return c.Length > max ? c[..max] : c;
    }

    /// <summary>Ikonets status (skrevet af tjenesten), null = ingen eller kan ikke læses.</summary>
    private static TrayStatus? ReadStatus()
    {
        try
        {
            return File.Exists(TrayApp.StatusFile)
                ? System.Text.Json.JsonSerializer.Deserialize<TrayStatus>(File.ReadAllText(TrayApp.StatusFile), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))
                : null;
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Som <see cref="RunSelf(string)"/>, men hvert argument for sig (tekst fra brugeren kan ikke ødelægge kommandolinjen).</summary>
    private static Task<(int, string)> RunSelf(IEnumerable<string> arguments)
    {
        var psi = Psi("");
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        return Run(psi);
    }

    /// <summary>Kører agenten selv (install/uninstall/verify) skjult og returnerer exit-kode og output.</summary>
    private static Task<(int, string)> RunSelf(string arguments) => Run(Psi(arguments));

    private static ProcessStartInfo Psi(string arguments) =>
        new(Environment.ProcessPath!, arguments)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8,   // æøå i agentens beskeder
        };

    private static async Task<(int, string)> Run(ProcessStartInfo psi)
    {
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, (await o) + (await e));
    }
}
