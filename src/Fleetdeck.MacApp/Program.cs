// Fleetdeck.app – menulinjen og installationen på Mac.
//   Fleetdeck            (dobbeltklik) installationsvinduet, hvis agenten ikke er installeret – ellers ikonet og statusvinduet
//   Fleetdeck --tray     (LaunchAgent ved login) kun ikonet; slår brugeren "Vis Fleetdeck ved login" fra, lukker den straks
//   Fleetdeck --setup    installationsvinduet (Skift server / afinstallér)
//   Fleetdeck --messages / --new-message   Beskeder: samtalen med support / "Skriv til support…"
// Kun én kopi pr. bruger: en ny kopi beder den kørende om at vise sig (unix-socket) og lukker.
// Appen står i Dock og Cmd-Tab, så længe den kører (se Shared/MacDock.cs); ikonet i menulinjen er der stadig.
// Hver start logges i ~/Library/Logs/Fleetdeck/app-start.log, og en fejl ved start vises i et vindue (StartupGuard).
using System.Net.Sockets;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Fleetdeck.Shared;

namespace Fleetdeck.MacApp;

public static class Program
{
    public static string Mode { get; private set; } = "show";

    [STAThread]
    public static int Main(string[] args)
    {
        Mode = args.Contains("--tray") ? "tray" : args.Contains("--setup") ? "setup" : args.Contains("--messages") ? "messages" : args.Contains("--new-message") ? "compose" : "show";
        StartupGuard.Begin(Mode);
        try
        {
            if (Mode == "tray" && !Settings.Current.Autostart) return 0;   // brugeren har slået ikonet ved login fra
            if (SingleInstance.HandOff(Mode)) return 0;
            return AppBuilder.Configure<App>().UsePlatformDetect().With(new MacOSPlatformOptions { ShowInDock = MacDock.ShowInDock(Mode) })
                .StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
        }
        catch (Exception e)
        {
            // aldrig en stille fejl: i loggen og i et vindue ("Fleetdeck kunne ikke starte: …")
            StartupGuard.Fail("opstart", e);
            return 1;
        }
    }
}

public sealed class App : Application
{
    public override void Initialize()
    {
        Name = "Fleetdeck";
        RequestedThemeVariant = ThemeVariant.Default;   // følger macOS (lys/mørk)
        MacStyle.Apply(this);   // Fluent-temaet med macOS' farver og former (forslag B)
        // Programmenuen (appen står i Dock, så macOS viser menulinjen "Fleetdeck"): uden den viser Avalonia "About Avalonia".
        // macOS tilføjer selv Skjul / Skjul andre / Vis alle / Slut Fleetdeck.
        var menu = new NativeMenu();
        void Add(string header, Action<Tray> click) { var i = new NativeMenuItem(header); i.Click += (_, _) => { if (_tray is { } t) click(t); }; menu.Items.Add(i); }
        Add("Om Fleetdeck…", t => t.ShowAbout());
        Add("Status…", t => t.ShowStatus());
        Add("Funktioner…", t => t.ShowFeatures());
        Add("Skift server…", t => t.ShowSetup());
        NativeMenu.SetMenu(this, menu);
    }

    private static Tray? _tray;

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime life)
        {
            var tray = _tray = new Tray(life);
            MacNative.EnsurePolicy(MacDock.Policy(Program.Mode));   // i Dock og Cmd-Tab, også hvis Avalonia ikke selv nåede det
            // klik på Dock-ikonet: et åbent vindue frem, ellers opsætningen/status
            if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime act)
                act.Activated += (_, e) => { if (e.Kind == ActivationKind.Reopen) tray.DockClicked(); };
            // "Slut" fra Dock, Cmd-Q eller log ud: luk rigtigt (statusvinduet gemmer sig ellers i stedet for at lukke og stopper afslutningen)
            life.ShutdownRequested += (_, _) => tray.PrepareQuit();
            SingleInstance.Listen(msg => Dispatcher.UIThread.Post(() => Handle(tray, msg)));
            Handle(tray, Program.Mode);
            StartupGuard.Ready();
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static void Handle(Tray tray, string mode)
    {
        switch (mode)
        {
            case "tray":
                // den native hjælper ejer ikonet i menulinjen – en gammel login-opstart med --tray skal ikke give et ikon nummer to
                if (Tray.NativeMenu) { tray.QuitIfIdle(); break; }
                if (MacSys.Installed) tray.Start();
                break;
            case "setup": if (MacSys.Installed) tray.Start(); tray.ShowSetup(); break;
            // Beskeder: tjenesten (ny besked) eller menulinjen åbner samtalen / "Skriv til support…"
            case "messages": if (MacSys.Installed) tray.Start(); tray.ShowMessages(); break;
            case "compose": if (MacSys.Installed) tray.Start(); tray.ShowCompose(); break;
            default: tray.Reopen(); break;
        }
    }
}

/// <summary>Én kopi pr. bruger: ~/Library/Application Support/Fleetdeck/app.sock.</summary>
public static class SingleInstance
{
    private static string Sock => Path.Combine(Settings.Dir, "app.sock");
    private static Socket? _listener;

    public static bool HandOff(string msg)
    {
        try
        {
            using var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            s.Connect(new UnixDomainSocketEndPoint(Sock));
            s.Send(Encoding.UTF8.GetBytes(msg));
            return true;
        }
        catch (Exception e) when (e is SocketException or IOException) { return false; }
    }

    public static void Listen(Action<string> onMessage)
    {
        try
        {
            Directory.CreateDirectory(Settings.Dir);
            if (File.Exists(Sock)) File.Delete(Sock);
            var listener = _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(Sock));
            listener.Listen(4);
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    try
                    {
                        using var c = await listener.AcceptAsync();
                        var buf = new byte[64];
                        var n = await c.ReceiveAsync(buf);
                        onMessage(Encoding.UTF8.GetString(buf, 0, n));
                    }
                    catch (SocketException) { }
                    catch (ObjectDisposedException) { return; }
                }
            });
        }
        catch (Exception e) when (e is SocketException or IOException or UnauthorizedAccessException) { }
    }
}
