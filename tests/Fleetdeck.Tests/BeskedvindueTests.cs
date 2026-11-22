using System.Text.RegularExpressions;
using Fleetdeck.Agent.Windows;

namespace Fleetdeck.Tests;

/// <summary>
/// Beskedvinduet på Windows: svarfeltet skal altid være med og have en højde. WinForms kan ikke køre på Linux, så her
/// testes den rene logik, vinduet bygges efter, og at alle steder, der åbner vinduet, giver mulighed for at svare.
/// </summary>
public class BeskedvindueTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public void Reply_field_is_always_there_when_the_user_can_answer(bool screenshot, bool extra)
    {
        var rows = MessageLayout.Rows(canReply: true, screenshot, extra);
        Assert.Contains(MessageLayout.Row.Reply, rows);
        Assert.Contains(MessageLayout.Row.State, rows);
        // under samtalen og over knapperne
        Assert.True(rows.ToList().IndexOf(MessageLayout.Row.Reply) > rows.ToList().IndexOf(MessageLayout.Row.Conversation));
        Assert.True(rows.ToList().IndexOf(MessageLayout.Row.Reply) < rows.ToList().IndexOf(MessageLayout.Row.Buttons));
        Assert.Equal(screenshot, rows.Contains(MessageLayout.Row.Screenshot));
        Assert.Equal(MessageLayout.Row.Buttons, rows[^1]);
    }

    [Fact]
    public void Without_send_there_is_no_reply_field()
    {
        var rows = MessageLayout.Rows(canReply: false, screenshot: true, extra: true);
        Assert.DoesNotContain(MessageLayout.Row.Reply, rows);
        Assert.DoesNotContain(MessageLayout.Row.Screenshot, rows);
        Assert.Contains(MessageLayout.Row.Extra, rows);
    }

    [Fact]
    public void Fixed_width_never_gives_the_reply_field_height_zero()
    {
        // fejlen: FixWidth satte MaximumSize = (bredde, 0), og WinForms afkorter højden til maks.-højden – også ved DPI-skalering
        Assert.Equal(0, MessageLayout.HeightAfterMaximum(MessageLayout.ReplyHeight, 0));
        // nu får kontroller med fast højde (svarfeltet er ikke AutoSize) kun bredden – højden bevares
        Assert.False(MessageLayout.LimitWidth(autoSize: false));
        Assert.True(MessageLayout.LimitWidth(autoSize: true));
        var height = MessageLayout.LimitWidth(autoSize: false) ? MessageLayout.HeightAfterMaximum(MessageLayout.ReplyHeight, 0) : MessageLayout.ReplyHeight;
        Assert.True(height > 0);
    }

    [Theory]
    [InlineData(96, 84)]
    [InlineData(120, 105)]
    [InlineData(144, 126)]
    [InlineData(192, 168)]
    public void Reply_field_height_follows_dpi_and_is_restored_when_too_small(int dpi, int want)
    {
        Assert.Equal(want, MessageLayout.ReplyHeightAt(dpi));
        Assert.True(MessageLayout.ReplyTooSmall(0, dpi));
        Assert.False(MessageLayout.ReplyTooSmall(want, dpi));
    }

    [Fact]
    public void Every_place_that_opens_the_message_window_lets_the_user_answer()
    {
        var src = File.ReadAllText(Path.Combine(Root(), "src", "Fleetdeck.Agent.Windows", "HelpdeskWindow.cs"));
        var tray = src[src.IndexOf("public sealed class HelpdeskTray", StringComparison.Ordinal)..];
        // ny besked, "Beskeder" i menuen og klik på notifikationen går gennem Open(); "Skriv til support…" gennem NewCase()
        var opens = Regex.Matches(tray, @"new MessageWindow\((?<body>.*?)\}\);", RegexOptions.Singleline);
        Assert.Equal(2, opens.Count);
        Assert.All(opens, m => Assert.Contains("Send = ", m.Groups["body"].Value));
        // opdateringer (ny besked, svar på en knap) beholder Send fra det åbne vindue
        Assert.Contains("Send = o.Send", src);
        Assert.Contains("BalloonTipClicked", tray);
    }

    [Fact]
    public void Fixed_width_controls_keep_their_height()
    {
        var ui = File.ReadAllText(Path.Combine(Root(), "src", "Fleetdeck.Agent.Windows", "Ui.cs"));
        var fix = ui[ui.IndexOf("public static T FixWidth", StringComparison.Ordinal)..];
        fix = fix[..fix.IndexOf("return c;", StringComparison.Ordinal)];
        // MaximumSize med højden 0 må kun sættes på AutoSize-kontroller
        Assert.Contains("MessageLayout.LimitWidth(c.AutoSize)", fix);
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Fleetdeck.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
