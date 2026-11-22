using System.Net.Http;
using System.Security.Authentication;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Fleetdeck.MacApp;
using Fleetdeck.Shared;

// Rigtige skærmbilleder af Mac-opsætningsvinduet (og med "html <mappe>" tro HTML-gengivelser af Windows og Linux, se Html.cs) (forslag B), renderet af Avalonia selv (Skia, headless) på Linux.
// Kun macOS' egne dele simuleres: skrivebordet, menulinjen, trafiklysene og vibrancy (sløret baggrund bag sidebjælken).
// Skriften er Inter i stedet for SF Pro. Alle data er opdigtede.

if (args is ["html", var htmlDir, ..]) { Html.Write(htmlDir); return; }
var outDir = args.Length > 0 ? args[0] : "shots";
Directory.CreateDirectory(outDir);
AppBuilder.Configure<ShotApp>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .WithInterFont()
    .With(new FontManagerOptions { DefaultFamilyName = "avares://Avalonia.Fonts.Inter/Assets#Inter" })
    .SetupWithoutStarting();
Fleetdeck.MacApp.Spinner.Frozen = true;
Mac.Mono = new FontFamily("DejaVu Sans Mono, Liberation Mono, avares://Avalonia.Fonts.Inter/Assets#Inter");

var only = args.Length > 1 ? args[1] : null;
var n = 0;
foreach (var (name, make) in Shots.All)
{
    n++;
    if (only is not null && !name.Contains(only)) continue;
    foreach (var dark in new[] { false, true })
    {
        var file = System.IO.Path.Combine(outDir, $"mac-{n:00}-{name}-{(dark ? "moerk" : "lys")}.png");
        Shots.Render(make, dark, file, name);
        Console.WriteLine(file);
    }
}

internal sealed class ShotApp : Application
{
    public override void Initialize() => MacStyle.Apply(this);
}

internal static class Shots
{
    private static readonly Discovery.Found Home = new("Fleetdeck · hjemme", "http://192.168.1.10:8585", "3.41.3", true);
    private static readonly Discovery.Found Shop = new("Fleetdeck · værksted", "http://192.168.1.24:8585", "3.40.1", true);
    private const string Url = "http://192.168.1.10:8585";

    private static TrayStatus Status(Action<TrayStatus>? edit = null)
    {
        var s = new TrayStatus
        {
            Machine = "MacBook-Air-stuen", Server = Url, Connected = true, Enrolled = true, Profile = "Hjemme-Mac", LastContact = DateTime.UtcNow.AddSeconds(-3),
            Written = DateTime.UtcNow, Version = "3.41.3", MacAccess = new MacAccessInfo { FullDiskAccess = true },
        };
        edit?.Invoke(s);
        return s;
    }

    private static SetupWindow New(bool installed = false, TrayStatus? st = null) => new(() => { }, demo: true, demoInstalled: installed, demoStatus: st);

    private static SetupWindow AtKey(Action<SetupWindow>? more = null)
    {
        var w = New();
        w.Flow.Start(); w.Flow.SearchDone([Home, Shop]); w.ServerBox.Text = Url;
        w.Flow.ServerTested(SetupCheck.Good("Forbindelse OK.", $"Fleetdeck 3.41.3 på {Url}.", "3.41.3"));
        more?.Invoke(w);
        return w;
    }

    private static SetupWindow AtProfile(Action<SetupWindow>? more = null) => AtKey(w =>
    {
        w.KeyBox.Text = "fd_9d3c1b7e2a8f4c60b5e1";
        w.Flow.KeyTested(SetupCheck.Good("Nøglen passer.", $"Forbundet til {Url}, version 3.41.3.", "3.41.3"));
        w.Profiles = [new ProfileOption(3, "Hjemme-Mac", 3, 12), new ProfileOption(5, "Kontor", 5, 18)];
        w.ProfileId = 3;
        w.Flow.ProfilesLoaded();
        more?.Invoke(w);
    });

    public static readonly (string Name, Func<SetupWindow> Make)[] All =
    [
        ("velkomst", () => New()),
        ("soeger", () => { var w = New(); w.Flow.Start(); w.Flow.SearchStarted(); return w; }),
        ("fundet", () => { var w = New(); w.Flow.Start(); w.Flow.SearchDone([Home, Shop]); w.ServerBox.Text = Url; return w; }),
        ("fejl-ingen-server", () => { var w = New(); w.Flow.Start(); w.Flow.SearchDone([]); w.ServerBox.Text = "10.0.0."; return w; }),
        ("fejl-certifikat", () =>
        {
            var w = New(); w.Flow.Start(); w.Flow.SearchDone([]);
            const string u = "https://fleetdeck.local:8586";
            w.ServerBox.Text = u;
            w.Flow.ServerTested(SetupProbe.Unreachable(u, new HttpRequestException(HttpRequestError.SecureConnectionError, "The SSL connection could not be established, see inner exception.",
                new AuthenticationException("The remote certificate is invalid because of errors in the certificate chain: UntrustedRoot")), SetupOs.Mac));
            w.DetailsOpen = true;
            return w;
        }),
        ("noegle-ok", () => AtKey(w =>
        {
            w.KeyBox.Text = "fd_9d3c1b7e2a8f4c60b5e1";
            w.Flow.KeyTested(SetupCheck.Good("Nøglen passer.", $"Forbundet til {Url}, version 3.41.3.", "3.41.3"));
        })),
        ("fejl-noegle", () => AtKey(w =>
        {
            w.KeyBox.Text = "fd_0000wrong";
            w.Flow.KeyTested(SetupCheck.Bad(SetupField.Key, "Nøglen passer ikke.", "Hent den igen i Fleetdeck under Tilføj maskine – den kan være skiftet."));
        })),
        ("profil", () => AtProfile()),
        ("installerer", () => AtProfile(w => { w.Flow.InstallStarted(); w.InstallAt = 2; })),
        ("installeret-uden-forbindelse", () => AtProfile(w => { w.Flow.InstallStarted(); w.Flow.InstallFinished(false); })),
        ("anmod", () => AtKey(w => w.Flow.ChooseAsk(true))),
        ("venter", () => AtKey(w =>
        {
            w.Flow.ChooseAsk(true); w.Flow.RequestSent("7K4-Q2M");
            w.Status = Status(s => { s.Enrolled = false; s.Connected = false; s.LastContact = null; s.AccessRequest = new TrayAccessRequest { Code = "7K4-Q2M", State = AccessStates.Pending }; });
        })),
        ("afvist", () => AtKey(w =>
        {
            w.Flow.ChooseAsk(true); w.Flow.RequestSent("7K4-Q2M");
            w.Flow.RequestUpdate(Status(s =>
            {
                s.Enrolled = false; s.Connected = false;
                s.AccessRequest = new TrayAccessRequest { Code = "7K4-Q2M", State = AccessStates.Rejected, Reason = "Ukendt maskine – skriv lige, hvis det er din." };
            }));
        })),
        ("tilmeldt", () => AtProfile(w => { w.Status = Status(s => s.MacAccess = new MacAccessInfo { FullDiskAccess = false }); w.Flow.InstallStarted(); w.Flow.InstallFinished(true); })),
        ("faerdig", () => AtProfile(w =>
        {
            w.Status = Status(); w.Flow.InstallStarted(); w.Flow.InstallFinished(true); w.Flow.AccessGranted(); w.Flow.Finish(); w.DemoGranted = true;
        })),
        ("installeret", () => New(true, Status())),
        ("installeret-tilvalg", () =>
        {
            var w = New(true, Status(s => s.Extensions =
            [
                new ExtensionView { Id = "fjernskrivebord", Name = "Fjernskrivebord (RustDesk)", Gives = "Så administratoren kan hjælpe", Installed = true },
                new ExtensionView { Id = "homebrew", Name = "Homebrew", Gives = "Pakker fra kommandolinjen", Pending = true },
            ]));
            w.RefreshRows(); w.Section = "extensions";
            return w;
        }),
        ("installeret-server", () =>
        {
            var w = New(true, Status());
            w.Section = "server"; w.ServerBox.Text = "http://192.168.1.24:8585";
            return w;
        }),
    ];

    public static void Render(Func<SetupWindow> make, bool dark, string file, string name)
    {
        var variant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        Application.Current!.RequestedThemeVariant = variant;
        var w = make();
        w.RequestedThemeVariant = variant;
        w.Rerender();
        var root = (Control)w.Content!;
        w.Frozen = true;
        w.Content = null;

        var desk = new Panel { Width = 960, Height = 720, ClipToBounds = true };
        desk.Children.Add(Wallpaper(dark));
        // vinduet: afrundede hjørner, skygge og skrivebordet bag den halvgennemsigtige sidebjælke (vibrancy)
        var inner = new Panel();
        // (skrivebordets bløde farver skinner igennem sidebjælken; Avalonias BlurEffect kan ikke bruges i RenderTargetBitmap ved 2×)
        inner.Children.Add(root);
        inner.Children.Add(Lights(dark));
        var win = new Border
        {
            Width = 780, Height = 600, CornerRadius = new CornerRadius(12), ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(90, 64, 0, 0), Child = inner,
            BoxShadow = BoxShadows.Parse(dark ? "0 0 0 0.5 #B3000000, 0 24 70 0 #99000000" : "0 0 0 0.5 #38000000, 0 24 70 0 #52000000, 0 8 20 0 #1F000000"),
        };
        desk.Children.Add(win);
        desk.Children.Add(MenuBar(dark, name is "venter" or "tilmeldt" or "faerdig" or "installeret" or "installeret-tilvalg" or "installeret-server" or "installeret-uden-forbindelse", name == "faerdig"));

        // 2× (Retina) som skaleret layout ved 96 dpi: RenderTargetBitmap ved 192 dpi skalerer runde klip forkert
        var scaled = new LayoutTransformControl { LayoutTransform = new ScaleTransform(2, 2), Child = desk };
        var host = new Window { Width = 1920, Height = 1440, Content = scaled, RequestedThemeVariant = variant };
        host.Show();
        // flere runder: tekst, der ombrydes, kan kræve flere layout-pas, før alt står stille
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            host.UpdateLayout();
        }
        using var rtb = new RenderTargetBitmap(new PixelSize(1920, 1440), new Vector(96, 96));
        rtb.Render(scaled);
        rtb.Save(file);
        host.Close();
        w.Close();
    }

    private static Control Wallpaper(bool dark)
    {
        var p = new Panel { Background = Grad(dark ? ["#0b1636", "#170f30", "#24123a"] : ["#cfe0ff", "#f2d7ef", "#ffd8bd"]) };
        void Blob(double x, double y, string c, double r) => p.Children.Add(new Ellipse
        {
            Width = r * 2, Height = r * 2, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(x - r, y - r, 0, 0),
            Fill = new RadialGradientBrush { GradientStops = { new GradientStop(Color.Parse(c), 0), new GradientStop(Color.FromArgb(0, Color.Parse(c).R, Color.Parse(c).G, Color.Parse(c).B), 1) } },
        });
        if (dark) { Blob(140, 610, "#6a2a8f", 380); Blob(840, 130, "#1b3f9c", 420); Blob(530, 720, "#2a1f6b", 460); }
        else { Blob(170, 590, "#ffb38a", 380); Blob(820, 140, "#9db7ff", 420); Blob(580, 690, "#d59cff", 460); }
        return p;
    }

    private static IBrush Grad(string[] c) => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0.6, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse(c[0]), 0), new GradientStop(Color.Parse(c[1]), 0.55), new GradientStop(Color.Parse(c[2]), 1) },
    };

    private static Control Lights(bool dark)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(20, 20, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        foreach (var c in new[] { "#ff5f57", "#febc2e", dark ? "#59808080" : "#59808080" })
            sp.Children.Add(new Ellipse { Width = 12, Height = 12, Fill = new SolidColorBrush(Color.Parse(c)), Stroke = new SolidColorBrush(Color.Parse("#2E000000")), StrokeThickness = 0.5 });
        return sp;
    }

    private static Control MenuBar(bool dark, bool showIcon, bool highlight)
    {
        var fg = new SolidColorBrush(Color.Parse(dark ? "#EBFFFFFF" : "#D9000000"));
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Height = 26, VerticalAlignment = VerticalAlignment.Top, Background = new SolidColorBrush(Color.Parse(dark ? "#73161828" : "#8CF6F6FA")) };
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18, Margin = new Thickness(14, 0), VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new TextBlock { Text = "●", FontSize = 12, Foreground = fg });
        left.Children.Add(new TextBlock { Text = "Fleetdeck", FontSize = 13, FontWeight = FontWeight.Bold, Foreground = fg });
        foreach (var m in new[] { "Arkiv", "Rediger", "Vindue", "Hjælp" }) left.Children.Add(new TextBlock { Text = m, FontSize = 13, Foreground = fg });
        g.Children.Add(left);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Margin = new Thickness(14, 0), VerticalAlignment = VerticalAlignment.Center };
        if (showIcon)
        {
            var b = new Border
            {
                Width = 26, Height = 20, CornerRadius = new CornerRadius(5), Background = highlight ? new SolidColorBrush(Color.Parse("#47808080")) : Brushes.Transparent,
                BoxShadow = highlight ? BoxShadows.Parse(dark ? "0 0 0 2 #0a84ff, 0 0 0 7 #8C0a84ff" : "0 0 0 2 #007aff, 0 0 0 7 #73007aff") : default,
                Child = new Border { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Child = Mac.Icon("menubar", 16, fg, 2) },
            };
            right.Children.Add(b);
        }
        right.Children.Add(new TextBlock { Text = "fre. 20. nov. 14.32", FontSize = 13, Foreground = fg, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(right, 2); g.Children.Add(right);
        return g;
    }
}
