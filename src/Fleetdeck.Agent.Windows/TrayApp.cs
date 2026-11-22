using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Security.Principal;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Win32;
using Fleetdeck.Shared;

namespace Fleetdeck.Agent.Windows;

/// <summary>
/// Fleetdeck i proceslinjen (kører som brugeren): ikon med status-prik, menu og et statusvindue med til/fra.
/// Data kommer fra tjenestens status.json; ordrer går som filer til tjenesten (administrator-ordrer via UAC).
/// </summary>
public sealed class TrayApp : ApplicationContext
{
    private static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Fleetdeck", "userwork");
    public static string StatusFile => Path.Combine(Dir, "status.json");
    private readonly NotifyIcon icon = new() { Visible = true, Text = "Fleetdeck" };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 3000 };
    private TrayStatus? st;
    private string? lastJob;
    private StatusForm? form;
    private CountdownForm? countdown;
    private readonly HelpdeskTray helpdesk;
    private DateTime? shownFor, toldFor;

    public static bool IsAdmin
    {
        get
        {
            // et administrator-login (også uden UAC-hævning) må styre: gruppen er med som "kun afvis" i det begrænsede token
            using var id = WindowsIdentity.GetCurrent();
            return id.Groups?.Any(g => g.Value == "S-1-5-32-544") == true || new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public static bool Notifications
    {
        get => Registry.GetValue(@"HKEY_CURRENT_USER\Software\Fleetdeck", "Notifications", 1) is not int v || v != 0;
        set => Registry.SetValue(@"HKEY_CURRENT_USER\Software\Fleetdeck", "Notifications", value ? 1 : 0);
    }
    public static bool Autostart
    {
        get => Registry.GetValue(@"HKEY_CURRENT_USER\Software\Fleetdeck", "Tray", 1) is not int v || v != 0;
        set => Registry.SetValue(@"HKEY_CURRENT_USER\Software\Fleetdeck", "Tray", value ? 1 : 0);
    }

    public TrayApp(bool showWindow)
    {
        helpdesk = new HelpdeskTray(icon);
        icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowStatus(); };
        icon.ContextMenuStrip = BuildMenu();
        timer.Tick += (_, _) => Refresh();
        timer.Start();
        Refresh();
        if (showWindow) ShowStatus();
    }

    private ContextMenuStrip BuildMenu()
    {
        var m = new ContextMenuStrip { ShowImageMargin = false, Font = Ui.F(9.5f) };
        var head = new ToolStripMenuItem("Fleetdeck") { Enabled = false };
        m.Items.Add(head);
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("Åbn status…", null, (_, _) => ShowStatus());
        m.Items.Add("Skriv til support…", null, (_, _) => helpdesk.NewCase());
        var messages = new ToolStripMenuItem("Beskeder", null, (_, _) => helpdesk.Open());
        m.Items.Add(messages);
        m.Items.Add("Opdatér alt nu", null, (_, _) => Command(new TrayCommand { Action = "upgrade_all" }, admin: false));
        var pause = new ToolStripMenuItem("Pause i 1 time", null, (_, _) => Command(new TrayCommand { Action = st?.PausedUntil is null ? "pause" : "resume", Minutes = 60 }, admin: true));
        m.Items.Add(pause);
        // kun mens Fleetdeck har planlagt en genstart/slukning på denne pc
        var powerSep = new ToolStripSeparator();
        var powerLater = new ToolStripMenuItem("", null, (_, _) => Command(new TrayCommand { Action = "power_postpone" }, admin: false));
        var powerNow = new ToolStripMenuItem("", null, (_, _) => { if (st?.PowerMode is { } pm) Command(new TrayCommand { Action = "power_now", Id = pm }, admin: false); });
        m.Items.Add(powerSep); m.Items.Add(powerLater); m.Items.Add(powerNow);
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("Åbn Fleetdeck i browseren", null, (_, _) => OpenWeb());
        m.Items.Add("ESP Flasher", null, (_, _) => OpenEsp());
        m.Items.Add("Skift server…", null, (_, _) => Elevated("setup"));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("Skjul ikonet", null, (_, _) =>
        {
            // ellers starter tjenesten det igen om lidt; "Fleetdeck" i Start-menuen åbner det igen
            Autostart = false;
            MessageBox.Show("Ikonet er skjult. Åbn \"Fleetdeck\" fra Start-menuen for at se status eller få ikonet igen.", "Fleetdeck", MessageBoxButtons.OK, MessageBoxIcon.Information);
            icon.Visible = false; ExitThread();
        });
        m.Opening += (_, _) =>
        {
            head.Text = st is null ? "Fleetdeck – starter…" : st.AccessRequest is { } ar ? $"Fleetdeck · {ar.Text}"
                : $"Fleetdeck · {st.Machine} · {(st.Connected ? "forbundet" : "ingen forbindelse")}";
            pause.Text = st?.PausedUntil is { } until ? $"Genoptag (pause til {until.ToLocalTime():HH:mm})" : "Pause i 1 time";
            pause.Visible = IsAdmin;
            var planned = st?.PowerAt is { } pa && pa > DateTime.UtcNow && st.PowerMode is JobTypes.Reboot or JobTypes.Shutdown;
            powerSep.Visible = powerLater.Visible = powerNow.Visible = planned;
            if (planned)
            {
                var reboot = st!.PowerMode == JobTypes.Reboot;
                powerLater.Text = $"Udsæt {(reboot ? "genstarten" : "slukningen")} {PlannedPower.PostponeMinutes} min. (kl. {st.PowerAt!.Value.ToLocalTime():HH:mm})";
                powerNow.Text = PlannedPower.NowLabel(st.PowerMode!);
            }
            messages.Visible = helpdesk.HasConversation;
            messages.Text = helpdesk.Unread > 0 ? $"Beskeder ({helpdesk.Unread} ulæste)" : "Beskeder";
        };
        return m;
    }

    private void Refresh()
    {
        try
        {
            if (File.Exists(StatusFile)) st = JsonSerializer.Deserialize<TrayStatus>(File.ReadAllText(StatusFile), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        var stale = st is null || DateTime.UtcNow - st.Written > TimeSpan.FromMinutes(5);
        // "Anmod om adgang": gul, mens den venter på administratoren; rød, når den er afvist eller udløbet
        var color = !stale && st?.AccessRequest is { } wait ? (AccessStates.Open(wait.State) ? Color.FromArgb(217, 119, 6) : Color.FromArgb(220, 38, 38))
            : stale || st is null || !st.Connected ? Color.FromArgb(220, 38, 38)
            : st.Job is not null ? Color.FromArgb(37, 99, 235)
            : st.Alerts > 0 || st.PausedUntil is not null || st.Security is not null ? Color.FromArgb(217, 119, 6) : Color.FromArgb(22, 163, 74);
        SetIcon(color);
        var text = st is null ? "Fleetdeck – tjenesten svarer ikke" : st.AccessRequest is { } req ? $"Fleetdeck – {req.Text}" : st.Job is { } j ? $"Fleetdeck – {j.Title}" : $"Fleetdeck – {st.Machine}: {(st.Connected ? "forbundet" : "ingen forbindelse")}";
        icon.Text = text.Length > 63 ? text[..63] : text;
        // beskeder, når et job starter og slutter
        var job = st?.Job?.Title;
        if (Notifications && job != lastJob)
        {
            if (job is not null) icon.ShowBalloonTip(4000, "Fleetdeck", job, ToolTipIcon.Info);
            else if (lastJob is not null) icon.ShowBalloonTip(4000, "Fleetdeck", $"Færdig: {lastJob}", ToolTipIcon.Info);
        }
        lastJob = job;
        PowerCountdown();
        helpdesk.Poll();
        if (form is { IsDisposed: false, Visible: true } && st is not null) form.Show(st);
    }

    /// <summary>Planlagt genstart/slukning: besked straks, og et vindue med nedtælling de sidste 5 minutter.</summary>
    private void PowerCountdown()
    {
        if (st?.PowerAt is not { } at || st.PowerMode is not { } mode || at < DateTime.UtcNow)
        {
            if (countdown is { IsDisposed: false }) countdown.Close();
            return;
        }
        var verb = mode == JobTypes.Reboot ? "genstarter" : "slukker";
        icon.Text = $"Fleetdeck – pc'en {verb} kl. {at.ToLocalTime():HH:mm}";
        SetIcon(Color.FromArgb(217, 119, 6));
        if (toldFor != at)
        {
            toldFor = at;
            icon.ShowBalloonTip(8000, "Fleetdeck", $"Pc'en {verb} kl. {at.ToLocalTime():HH:mm}. Gem dit arbejde.", ToolTipIcon.Warning);
        }
        if (at - DateTime.UtcNow > TimeSpan.FromMinutes(5)) return;
        if (countdown is { IsDisposed: false }) { countdown.SetTime(at); return; }
        if (shownFor == at) return;   // lukket med OK: vis ikke igen for samme tidspunkt
        shownFor = at;
        countdown = new CountdownForm(mode, at, () => Command(new TrayCommand { Action = "power_postpone" }, admin: false),
            () => Command(new TrayCommand { Action = "power_now", Id = mode }, admin: false));
        countdown.Show(); countdown.Activate();
    }

    private Icon? current;
    private void SetIcon(Color dot)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var logo = Ui.AppIcon(32).ToBitmap();
            g.DrawImage(logo, 0, 0, 32, 32);
            using var ring = new SolidBrush(Color.FromArgb(32, 32, 32)); using var b = new SolidBrush(dot);
            g.FillEllipse(ring, 18, 18, 14, 14); g.FillEllipse(b, 20, 20, 10, 10);
        }
        var h = bmp.GetHicon();
        var old = current; current = Icon.FromHandle(h); icon.Icon = current;
        if (old is not null) { DestroyIcon(old.Handle); old.Dispose(); }
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr h);

    public void ShowFromOther() => ShowStatus();

    private void ShowStatus()
    {
        if (form is null || form.IsDisposed) form = new StatusForm(this);
        if (st is not null) form.Show(st);
        form.Show(); form.WindowState = FormWindowState.Normal; form.Activate();
    }

    public void OpenWeb()
    {
        var url = st?.WebUrl ?? st?.Server;
        if (url is { Length: > 0 } && url.StartsWith("http")) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    /// <summary>ESP Flasher gennem agenten på http://localhost (en sikker side uden certifikat), så USB virker i Chrome og Edge.</summary>
    public void OpenEsp()
    {
        if (LocalWeb.EspUrl(st?.LocalUrl, st?.WebUrl ?? st?.Server) is { } url) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    /// <summary>En ordre til tjenesten. Administrator-ordrer (pause, udvidelser) skrives med UAC i en mappe, kun administratorer må skrive i.</summary>
    public void Command(TrayCommand c, bool admin)
    {
        var json = JsonSerializer.Serialize(c, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (!admin)
        {
            try { Directory.CreateDirectory(Path.Combine(Dir, "cmd")); File.WriteAllText(Path.Combine(Dir, "cmd", $"{Guid.NewGuid():N}.json"), json); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { icon.ShowBalloonTip(3000, "Fleetdeck", $"Kunne ikke sende: {e.Message}", ToolTipIcon.Error); return; }
            icon.ShowBalloonTip(2500, "Fleetdeck", c.Action == "upgrade_all" ? "Opdatér alt er sat i gang" : "Sendt", ToolTipIcon.Info);
            return;
        }
        Elevated($"tray-cmd {Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json))}");
    }

    private static void Elevated(string args)
    {
        try { Process.Start(new ProcessStartInfo(Environment.ProcessPath!, args) { UseShellExecute = true, Verb = "runas" }); }
        catch (System.ComponentModel.Win32Exception) { }   // brugeren sagde nej
    }

    /// <summary>"tray-cmd": køres med UAC og lægger ordren i administratorernes mappe.</summary>
    public static int WriteAdminCommand(string b64)
    {
        var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64));
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Fleetdeck", "cmd");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"{Guid.NewGuid():N}.json"), json);
        return 0;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { timer.Dispose(); icon.Visible = false; icon.Dispose(); form?.Dispose(); current?.Dispose(); }
        base.Dispose(disposing);
    }
}

/// <summary>Statusvinduet: maskine og forbindelse, nøgletal, hvad der kører, til/fra og udvidelser.</summary>
public sealed class StatusForm : Form
{
    private readonly TrayApp app;
    private readonly Theme t = Theme.Current;
    private readonly Hero hero;
    private readonly Label kUp, kAl, kHe, jobTitle, jobStep, foot;
    private readonly Bar bar;
    private readonly Toggle pause, notify, auto;
    private readonly TableLayoutPanel extList;
    private string extKey = "";
    private bool loading;
    private const int W = 400;

    public StatusForm(TrayApp app)
    {
        this.app = app;
        Ui.BeginDpi(this);   // alle mål herunder er logiske (100 %) og skaleres efter skærmen
        Text = "Fleetdeck"; Icon = Ui.AppIcon(32); BackColor = t.Bg; Font = Ui.F(10f);
        FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false; StartPosition = FormStartPosition.Manual;
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink; ShowInTaskbar = true;
        var root = Ui.Stack(W + 48, new Padding(24, 18, 24, 18));
        hero = new Hero(t, "Fleetdeck", "Forbinder…", W); root.Controls.Add(hero);

        var kpis = Ui.FixWidth(new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Margin = new Padding(0, 0, 0, 10), BackColor = Color.Transparent }, W);
        for (var i = 0; i < 3; i++) kpis.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        Label Kpi(int col, string label)
        {
            // tre lige brede kort med 10 px imellem; lidt mindre luft og tal, så "Advarsel" kan stå på én linje
            var side = (W - 20) / 3;   // kortene fylder præcis bredden: kanterne flugter med kortene nedenunder
            var c = new Card(t, col == 1 ? W - 20 - 2 * side : side) { Margin = new Padding(col == 0 ? 0 : 5, 0, col == 2 ? 0 : 5, 0), Padding = new Padding(12, 10, 12, 10) };
            var v = Ui.Text("–", t, 16f, FontStyle.Bold); c.Controls.Add(v); c.Controls.Add(Ui.Text(label, t, 8.75f, muted: true));
            kpis.Controls.Add(c, col, 0); return v;
        }
        kUp = Kpi(0, "opdateringer"); kAl = Kpi(1, "alarmer"); kHe = Kpi(2, "helbred");
        root.Controls.Add(kpis);

        var job = new Card(t, W); job.Controls.Add(Ui.Section("I gang nu", t));
        jobTitle = Ui.Text("Ingen opgaver kører", t, 10f, maxWidth: W - 32); jobStep = Ui.Text("", t, 8.75f, muted: true, maxWidth: W - 32);
        bar = new Bar(t) { Width = W - 32, Visible = false };
        job.Controls.Add(jobTitle); job.Controls.Add(jobStep); job.Controls.Add(bar);
        root.Controls.Add(job);

        var tog = new Card(t, W);
        pause = new Toggle(t); notify = new Toggle(t) { Checked = TrayApp.Notifications }; auto = new Toggle(t) { Checked = TrayApp.Autostart };
        tog.Controls.Add(new Row(t, "Pause i 1 time", TrayApp.IsAdmin ? "Ingen nye installationer, mens du arbejder" : "Kræver en administrator", pause, W - 32, divider: false));
        tog.Controls.Add(new Row(t, "Vis besked ved installationer", "Lille besked nede ved uret", notify, W - 32));
        tog.Controls.Add(new Row(t, "Vis Fleetdeck ved login", "Ikonet i proceslinjen", auto, W - 32));
        pause.Enabled = TrayApp.IsAdmin;
        pause.CheckedChanged += (_, _) => { if (!loading) app.Command(new TrayCommand { Action = pause.Checked ? "pause" : "resume", Minutes = 60 }, admin: true); };
        notify.CheckedChanged += (_, _) => TrayApp.Notifications = notify.Checked;
        auto.CheckedChanged += (_, _) => TrayApp.Autostart = auto.Checked;
        root.Controls.Add(tog);

        var ext = new Card(t, W); ext.Controls.Add(Ui.Section("Udvidelser", t));
        extList = Ui.Stack(W - 32); ext.Controls.Add(extList);
        root.Controls.Add(ext);

        var bottom = Ui.FixWidth(new TableLayoutPanel { ColumnCount = 2, AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 2, 0, 0) }, W);
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        foot = Ui.Text("", t, 8.75f, muted: true, maxWidth: W - 150); foot.Anchor = AnchorStyles.Left; foot.Margin = Padding.Empty;
        var open = new FlatBtn(t, "Åbn Fleetdeck", primary: true); open.Click += (_, _) => app.OpenWeb();
        bottom.Controls.Add(foot, 0, 0); bottom.Controls.Add(open, 1, 0);
        root.Controls.Add(bottom);
        Controls.Add(root);
        Ui.EndDpi(this);
        Load += (_, _) =>
        {
            Ui.TitleBar(this, t);
            var wa = Screen.PrimaryScreen!.WorkingArea;   // nederst til højre, ved uret
            Location = new Point(wa.Right - Width - 12, wa.Bottom - Height - 12);
        };
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
    }

    public void Show(TrayStatus s)
    {
        loading = true;
        hero.Title.Text = s.Machine;
        hero.Sub.Text = s.AccessRequest is { } ar ? ar.Text
            : !s.Connected ? "Ingen forbindelse til serveren"
            : s.Security is { Length: > 0 } sec ? $"Sikkerhed: {sec}"
            : $"Forbundet{(s.Profile is { Length: > 0 } p ? $" · profil {p}" : "")}";
        kUp.Text = s.Upgrades.ToString(); kUp.ForeColor = s.Upgrades > 0 ? t.Warn : t.Text;
        kAl.Text = s.Alerts.ToString(); kAl.ForeColor = s.Alerts > 0 ? t.Bad : t.Text;
        kHe.Text = s.Health switch { "ok" => "Sund", "warning" => "Advarsel", "critical" => "Kritisk", _ => "–" };
        kHe.ForeColor = s.Health switch { "ok" => t.Ok, "warning" => t.Warn, "critical" => t.Bad, _ => t.Text };
        if (s.Job is { } j) { jobTitle.Text = j.Title; jobStep.Text = j.Step; bar.Visible = true; bar.Value = j.Percent; }
        else if (s.PowerAt is { } pa && pa > DateTime.UtcNow)
        { jobTitle.Text = $"{(s.PowerMode == JobTypes.Reboot ? "Genstarter" : "Slukker")} kl. {pa.ToLocalTime():HH:mm}"; jobStep.Text = "Planlagt fra Fleetdeck"; bar.Visible = false; }
        else { jobTitle.Text = s.PausedUntil is { } u ? $"På pause til {u.ToLocalTime():HH:mm}" : "Ingen opgaver kører"; jobStep.Text = ""; bar.Visible = false; }
        pause.Checked = s.PausedUntil is not null;
        foot.Text = s.LastContact is { } lc ? $"Sidst i kontakt: {Ago(lc)}" : "";
        var key = string.Join("|", s.Extensions.Select(e => $"{e.Id}{e.Installed}{e.Pending}"));
        if (key != extKey)
        {
            extKey = key;
            extList.SuspendLayout();
            foreach (var old in extList.Controls.Cast<Control>().ToList()) old.Dispose();
            extList.Controls.Clear();
            var first = true;
            foreach (var e in s.Extensions)
            {
                Control right;
                if (e.Installed) right = Ui.Text("✔ Installeret", t, 9f, muted: true);
                else if (e.Pending) right = Ui.Text("Installeres…", t, 9f, muted: true);
                else if (TrayApp.IsAdmin)
                {
                    var b = new FlatBtn(t, "Installér"); var id = e.Id;
                    b.Click += (_, _) => app.Command(new TrayCommand { Action = "extension", Id = id }, admin: true);
                    right = b;
                }
                else right = Ui.Text("Mangler", t, 9f, muted: true);
                extList.Controls.Add(Ui.Scaled(new Row(t, e.Name, e.Gives, right, W - 32, divider: !first), this));
                first = false;
            }
            if (s.Extensions.Count == 0) extList.Controls.Add(Ui.Scaled(Ui.Text("Ingen udvidelser til denne maskine.", t, 9f, muted: true), this));
            extList.ResumeLayout(true);
        }
        loading = false;
    }

    private static string Ago(DateTime utc)
    {
        var d = DateTime.UtcNow - utc;
        return d.TotalSeconds < 60 ? "lige nu" : d.TotalMinutes < 60 ? $"{(int)d.TotalMinutes} min. siden" : d.TotalHours < 24 ? $"{(int)d.TotalHours} t siden" : utc.ToLocalTime().ToString("dd-MM HH:mm");
    }
}
