using Fleetdeck.Shared;

namespace Fleetdeck.Tests;

/// <summary>Beskeder på Mac og Linux: svarvinduet (yad/zenity/kdialog og Fleetdeck.app) og ordrerne til tjenesten.</summary>
public class BeskedsvarUnixTests
{
    private static readonly DateTime At = new(2026, 11, 19, 13, 2, 0, DateTimeKind.Utc);

    private static HelpdeskInbox Inbox(bool buttons = true)
    {
        var inbox = new HelpdeskInbox();
        inbox.Merge([new HelpdeskNote { Id = 7, From = "IT", Text = "Må vi genstarte <nu> & her?", At = At,
            Buttons = buttons ? [new HelpdeskButton { Label = "Ja, genstart nu", Action = "reboot" }, new HelpdeskButton { Label = "Senere" }] : [] }]);
        return inbox;
    }

    [Fact]
    public void Picks_the_best_dialog_tool_that_exists()
    {
        Assert.Equal(LinuxReplyDialog.Tool.Yad, LinuxReplyDialog.Pick(t => t is "yad" or "zenity"));
        Assert.Equal(LinuxReplyDialog.Tool.Zenity, LinuxReplyDialog.Pick(t => t is "zenity" or "kdialog"));
        Assert.Equal(LinuxReplyDialog.Tool.Kdialog, LinuxReplyDialog.Pick(t => t == "kdialog"));
        Assert.Equal(LinuxReplyDialog.Tool.None, LinuxReplyDialog.Pick(_ => false));
    }

    [Theory]
    [InlineData("4.2.1", true)]
    [InlineData("4.2", true)]
    [InlineData("5.0", true)]
    [InlineData("4.1.90", false)]
    [InlineData("3.42.1", false)]
    [InlineData("", false)]
    public void Zenity_gets_a_multiline_field_from_4_2(string version, bool multiline) =>
        Assert.Equal(multiline, LinuxReplyDialog.ZenityMultiline(version));

    [Fact]
    public void Zenity_window_has_a_reply_field_send_close_and_the_answer_buttons()
    {
        var a = LinuxReplyDialog.Args(LinuxReplyDialog.Tool.Zenity, Inbox(), multiline: true, TimeZoneInfo.Utc);
        Assert.Equal("zenity", a[0]);
        Assert.Contains("--forms", a);
        Assert.Contains("--add-multiline-entry=Dit svar", a);
        Assert.Contains("--ok-label=Send svar", a);
        Assert.Contains("--cancel-label=Luk", a);
        Assert.Contains("--extra-button=Ja, genstart nu", a);
        Assert.Contains("--extra-button=Senere", a);
        Assert.Contains("--extra-button=Send logfiler", a);
        // samtalen vises som ren tekst (escapet), support med fed
        var text = a.Single(x => x.StartsWith("--text=", StringComparison.Ordinal));
        Assert.Contains("<b>Må vi genstarte &lt;nu&gt; &amp; her?</b>", text);
        Assert.Contains("IT · 13:02", text);
        Assert.DoesNotContain("<nu>", text);
        // ældre zenity: ét linjefelt
        Assert.Contains("--add-entry=Dit svar", LinuxReplyDialog.Args(LinuxReplyDialog.Tool.Zenity, Inbox(), multiline: false));
    }

    [Fact]
    public void Zenity_answers_are_parsed()
    {
        var inbox = Inbox();
        var z = LinuxReplyDialog.Tool.Zenity;
        Assert.Equal(new HelpdeskAnswer(HelpdeskAnswer.Reply, "Ja tak\nmen om lidt"), LinuxReplyDialog.Parse(z, 0, "Ja tak\nmen om lidt\n", inbox));
        Assert.Equal(new HelpdeskAnswer(HelpdeskAnswer.Reply, "", 1), LinuxReplyDialog.Parse(z, 1, "Senere\n", inbox));
        Assert.Equal(new HelpdeskAnswer(HelpdeskAnswer.Logs), LinuxReplyDialog.Parse(z, 1, "Send logfiler\n", inbox));
        Assert.Equal(HelpdeskAnswer.Close, LinuxReplyDialog.Parse(z, 1, "", inbox));        // Luk
        Assert.Equal(HelpdeskAnswer.Close, LinuxReplyDialog.Parse(z, 5, "", inbox));        // tiden løb ud
        Assert.Equal(HelpdeskAnswer.Close, LinuxReplyDialog.Parse(z, 0, "  \n", inbox));    // "Send svar" uden tekst
        Assert.Equal(HelpdeskAnswer.Failed, LinuxReplyDialog.Parse(z, 255, "", inbox).Kind); // ukendt tilvalg
    }

    [Fact]
    public void Yad_window_and_answers_keep_the_text_with_a_button()
    {
        var inbox = Inbox();
        var a = LinuxReplyDialog.Args(LinuxReplyDialog.Tool.Yad, inbox);
        Assert.Contains("--form", a);
        Assert.Contains(a, x => x.StartsWith("--field=", StringComparison.Ordinal) && x.EndsWith(":TXT", StringComparison.Ordinal));
        Assert.Contains("--button=Ja, genstart nu:10", a);
        Assert.Contains("--button=Send svar:0", a);
        var y = LinuxReplyDialog.Tool.Yad;
        Assert.Equal(new HelpdeskAnswer(HelpdeskAnswer.Reply, "Linje 1\nLinje 2"), LinuxReplyDialog.Parse(y, 0, "Linje 1\\nLinje 2|\n", inbox));
        Assert.Equal(new HelpdeskAnswer(HelpdeskAnswer.Reply, "gem først", 0), LinuxReplyDialog.Parse(y, 10, "gem først|\n", inbox));
        Assert.Equal(new HelpdeskAnswer(HelpdeskAnswer.Logs, ""), LinuxReplyDialog.Parse(y, 3, "|\n", inbox));
        Assert.Equal(HelpdeskAnswer.Close, LinuxReplyDialog.Parse(y, 252, "", inbox));
        Assert.Equal(HelpdeskAnswer.Close, LinuxReplyDialog.Parse(y, 70, "", inbox));
    }

    [Fact]
    public void Kdialog_asks_for_the_answer_first_and_then_shows_a_text_field()
    {
        var inbox = Inbox();
        var k = LinuxReplyDialog.Tool.Kdialog;
        var menu = LinuxReplyDialog.Args(k, inbox);
        Assert.Contains("--menu", menu);
        Assert.Contains("Ja, genstart nu", menu);
        Assert.Contains("Skriv et svar…", menu);
        Assert.StartsWith("<qt>", menu[4]);
        Assert.Contains("&lt;nu&gt;", menu[4]);
        Assert.Equal(new HelpdeskAnswer(HelpdeskAnswer.Reply, "", 0), LinuxReplyDialog.Parse(k, 0, "b0\n", inbox, kdialogMenu: true));
        Assert.Equal("text", LinuxReplyDialog.Parse(k, 0, "text\n", inbox, kdialogMenu: true).Kind);
        Assert.Equal(new HelpdeskAnswer(HelpdeskAnswer.Logs), LinuxReplyDialog.Parse(k, 0, "logs\n", inbox, kdialogMenu: true));
        Assert.Equal(new HelpdeskAnswer(HelpdeskAnswer.Reply, "Tak"), LinuxReplyDialog.Parse(k, 0, "Tak\n", inbox));
        // uden svarknapper: tekstfeltet med det samme
        Assert.Contains("--textinputbox", LinuxReplyDialog.Args(k, Inbox(buttons: false)));
    }

    [Fact]
    public void A_dialog_that_closed_at_once_was_never_shown()
    {
        Assert.True(LinuxReplyDialog.NeverShown(TimeSpan.FromMilliseconds(200)));
        Assert.False(LinuxReplyDialog.NeverShown(TimeSpan.FromSeconds(4)));
    }

    [Fact]
    public void View_matches_the_windows_window()
    {
        var inbox = Inbox();
        Assert.Equal("Besked fra IT", HelpdeskView.Title(inbox));
        Assert.Equal(7, HelpdeskView.Ask(inbox)!.Id);
        Assert.Equal(7, HelpdeskView.LastFromSupport(inbox));
        var mine = inbox.AddMine("bruger", "", 7, 0, false, At.AddMinutes(1));
        Assert.Equal("Dig · 13:03 · sendes…", HelpdeskView.Head(mine, TimeZoneInfo.Utc));
        Assert.Null(HelpdeskView.Ask(inbox));   // besvaret: knapperne forsvinder
        Assert.Equal("Du svarede: Ja, genstart nu", HelpdeskView.Answered(inbox.Lines[0]));
        // Enter sender, Skift+Enter giver ny linje (som Windows' ReplyKeys)
        Assert.True(HelpdeskView.EnterSends(shift: false, ctrl: false, alt: false));
        Assert.False(HelpdeskView.EnterSends(shift: true, ctrl: false, alt: false));
        Assert.True(HelpdeskView.EnterSends(shift: false, ctrl: true, alt: false));
        Assert.False(HelpdeskView.EnterSends(shift: false, ctrl: false, alt: true));
    }

    [Fact]
    public void Mac_users_may_answer_messages_but_not_pause()
    {
        foreach (var a in new[] { "helpdesk_reply", "helpdesk_read", "helpdesk_logs" }) Assert.True(TrayCommands.UserAllowed(a), a);
        Assert.False(TrayCommands.UserAllowed("pause"));
        Assert.StartsWith(MacPaths.SupportDir, MacPaths.HelpdeskFile);
    }

    [Fact]
    public void Mac_app_opens_the_message_window_from_the_service_and_the_menu()
    {
        var root = Root();
        var program = File.ReadAllText(Path.Combine(root, "src", "Fleetdeck.MacApp", "Program.cs"));
        Assert.Contains("--messages", program);
        Assert.Contains("--new-message", program);
        var agent = File.ReadAllText(Path.Combine(root, "src", "Fleetdeck.Agent.Unix", "Helpdesk.cs"));
        Assert.Contains("--messages", agent);
        var swift = File.ReadAllText(Path.Combine(root, "src", "Fleetdeck.MacMenu", "main.swift"));
        Assert.Contains("\"--messages\"", swift);
        Assert.Contains("\"--new-message\"", swift);
        var window = File.ReadAllText(Path.Combine(root, "src", "Fleetdeck.MacApp", "MessageWindow.cs"));
        Assert.Contains("AcceptsReturn = true", window);
        Assert.Contains("root.Children.Add(_reply)", window);
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Fleetdeck.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
