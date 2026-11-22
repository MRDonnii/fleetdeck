using System.Drawing;
using System.Windows.Forms;
using Fleetdeck.Shared;

namespace Fleetdeck.Agent.Windows;

/// <summary>Vindue øverst på skærmen med nedtælling til en planlagt genstart/slukning, "Udsæt" og "Genstart nu"/"Sluk nu".</summary>
public sealed class CountdownForm : Form
{
    private readonly Theme t = Theme.Current;
    private readonly Label head, left;
    private readonly Bar bar;
    private readonly System.Windows.Forms.Timer tick = new() { Interval = 1000 };
    private DateTime at;
    private readonly double total;

    public CountdownForm(string mode, DateTime atUtc, Action postpone, Action now)
    {
        at = atUtc;
        total = Math.Max(60, (atUtc - DateTime.UtcNow).TotalSeconds);
        Ui.BeginDpi(this);   // alle mål herunder er logiske (100 %) og skaleres efter skærmen
        Text = "Fleetdeck"; Icon = Ui.AppIcon(32); BackColor = t.Bg; Font = Ui.F(10f);
        FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false; MinimizeBox = false; StartPosition = FormStartPosition.Manual;
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink; ShowInTaskbar = true; TopMost = true;
        const int W = 380;
        var root = Ui.Stack(W + 48, new Padding(24, 18, 24, 18));
        var reboot = mode == JobTypes.Reboot;
        root.Controls.Add(new Hero(t, reboot ? "Pc'en genstarter snart" : "Pc'en slukker snart", "Gem dit arbejde og luk dine programmer.", W));
        var card = new Card(t, W);
        head = Ui.Text("", t, 10f, maxWidth: W - 32);
        left = Ui.Text("", t, 22f, FontStyle.Bold);
        bar = new Bar(t) { Width = W - 32 };
        card.Controls.Add(head); card.Controls.Add(left); card.Controls.Add(bar);
        root.Controls.Add(card);
        var buttons = Ui.FixWidth(new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0, 2, 0, 0) }, W);
        var ok = new FlatBtn(t, "OK", primary: true); ok.Click += (_, _) => Close();
        var later = new FlatBtn(t, $"Udsæt {PlannedPower.PostponeMinutes} min.");
        later.Click += (_, _) => { postpone(); Close(); };
        var go = new FlatBtn(t, PlannedPower.NowLabel(mode));
        go.Click += (_, _) => { now(); Close(); };
        buttons.Controls.Add(ok); buttons.Controls.Add(later); buttons.Controls.Add(go);
        root.Controls.Add(buttons);
        Controls.Add(root);
        Ui.EndDpi(this);
        Load += (_, _) =>
        {
            Ui.TitleBar(this, t);
            var wa = Screen.PrimaryScreen!.WorkingArea;   // øverst i midten, så den ikke overses
            Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Top + 40);
            SetTime(at);
        };
        tick.Tick += (_, _) => SetTime(at);
        tick.Start();
        FormClosed += (_, _) => tick.Dispose();
    }

    public void SetTime(DateTime atUtc)
    {
        at = atUtc;
        var rem = at - DateTime.UtcNow;
        if (rem < TimeSpan.Zero) rem = TimeSpan.Zero;
        head.Text = $"Kl. {at.ToLocalTime():HH:mm} – om";
        left.Text = $"{(int)rem.TotalMinutes}:{rem.Seconds:00}";
        bar.Value = (int)Math.Clamp(100 - 100 * rem.TotalSeconds / total, 0, 100);
    }
}
