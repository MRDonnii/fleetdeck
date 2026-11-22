using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Fleetdeck.Shared;

namespace Fleetdeck.MacApp;

/// <summary>
/// Fleetdeck i menulinjen (som TrayApp i Windows): ikon med status-prik, menu, statusvindue og beskeder.
/// Data kommer fra tjenestens status.json; ordrer går som filer til tjenesten (administrator-ordrer med adgangskoden).
/// </summary>
public sealed class Tray
{
    private readonly IClassicDesktopStyleApplicationLifetime _life;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly Dictionary<string, WindowIcon> _icons = [];
    private TrayIcon? _icon;
    private bool _started;
    private int _idle;
    /// <summary>
    /// Den native menulinje-hjælper er installeret og har det rigtige versionsstempel (så ejer den ikonet). En forældet hjælper
    /// (uden "Beskeder…"/"Skriv til support…") regnes ikke med: så viser appen selv sin menu, og tjenesten fjerner den gamle.
    /// </summary>
    public static bool NativeMenu => MacMenuStamp.IsCurrent(MacPaths.NativeMenu);
    private TrayStatus? _st;
    private string? _lastJob, _menuKey;
    private DateTime? _toldPower;
    private bool _toldAccess;
    private StatusWindow? _status;
    private FeaturesWindow? _features;
    private MessageWindow? _messages;
    private HelpdeskInbox? _hd;
    private DateTime _hdWritten;
    private readonly HashSet<int> _announced = [];
    private AboutWindow? _about;
    private AccessWindow? _access;
    private SetupWindow? _setup;
    public bool Quitting { get; private set; }

    public Tray(IClassicDesktopStyleApplicationLifetime life) => _life = life;

    /// <summary>Vis ikonet i menulinjen (når agenten er installeret).</summary>
    public void Start()
    {
        if (_icon is not null || _started) return;
        _started = true;
        if (NativeMenu)
        {
            // Den indbyggede menulinje-hjælper (FleetdeckMenu) viser ikonet – her kun vinduerne, ellers står Fleetdeck der to gange
            _timer.Tick += (_, _) => Refresh();
            _timer.Start();
            Refresh();
            return;
        }
        // Menuen sættes én gang og opdateres derefter kun indholdsmæssigt: på macOS fejler Avalonia ("The menu being updated
        // does not match"), hvis TrayIcon.Menu udskiftes med en ny NativeMenu.
        _icon = new TrayIcon { ToolTipText = "Fleetdeck", Icon = IconFor("#9ca3af"), Menu = _menu, IsVisible = true };
        _icon.Clicked += (_, _) => ShowStatus();
        TrayIcon.SetIcons(Application.Current!, [_icon]);
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
    }

    private void Refresh()
    {
        _st = MacSys.ReadStatus();
        var now = DateTime.UtcNow;
        var state = TrayView.Of(_st, now);
        var ic = IconFor(TrayView.Color(state));
        if (_icon is not null)
        {
            if (!ReferenceEquals(_icon.Icon, ic)) _icon.Icon = ic;
            _icon.ToolTipText = $"Fleetdeck – {TrayView.Headline(_st, now)}";
        }
        else if (Idle)
        {
            // uden eget ikon lever appen kun, mens et vindue er åbent (to stille tjek i træk, så den ikke lukker under opstart)
            if (++_idle >= 2) { Quit(); return; }
        }
        else _idle = 0;
        // beskeder, når et job starter og slutter
        var job = _st?.Job?.Title;
        if (TrayView.JobNotice(_lastJob, job) is { } n && (job is not null || _lastJob is not null)) MacSys.Notify(n);
        _lastJob = job;
        // planlagt genstart/slukning: besked straks (tjenesten viser selv nedtællingen de sidste 5 minutter)
        if (_st?.PowerAt is { } at && at > now && _toldPower != at)
        {
            _toldPower = at;
            MacSys.Notify($"Mac'en {(_st.PowerMode == JobTypes.Reboot ? "genstarter" : "slukker")} kl. {at.ToLocalTime():HH:mm}. Gem dit arbejde.", "Planlagt fra Fleetdeck");
        }
        if (TrayView.NeedsFullDiskAccess(_st) && !_toldAccess)
        {
            _toldAccess = true;
            MacSys.Notify("Fleetdeck mangler Fuld diskadgang – åbn Fleetdeck i menulinjen og vælg \"Ret det\".");
        }
        PollMessages();
        BuildMenu(now);
        if (_status is { IsVisible: true }) _status.Show(_st);
        if (_features is { IsVisible: true }) _features.Show(_st);
    }

    /// <summary>Beskeder: ny besked fra support åbner samtalen (som i Windows); et åbent vindue følger med.</summary>
    private void PollMessages()
    {
        var hd = MacSys.ReadHelpdesk();
        if (hd is null) return;
        var changed = hd.Written != _hdWritten;
        _hd = hd; _hdWritten = hd.Written;
        if (changed && hd.Unread.Where(l => _announced.Add(l.Id)).ToList() is { Count: > 0 } fresh)
        {
            MacSys.Notify(HelpdeskText.Preview(fresh[^1].Text, 200), HelpdeskView.Title(hd));
            // skriver brugeren en ny sag, lukkes den ikke midt i det: beskeden vises, når den er sendt eller lukket
            if (_messages is { Compose: true }) return;
            ShowMessages();
            return;
        }
        if (_messages is { Compose: false }) _messages.Show(hd);
    }

    /// <summary>Samtalen med support (fra menuen, når en besked kommer, eller fra tjenesten med --messages).</summary>
    public void ShowMessages()
    {
        var hd = _hd ?? MacSys.ReadHelpdesk() ?? new HelpdeskInbox();
        foreach (var l in hd.Unread) _announced.Add(l.Id);
        if (_messages is { Compose: true }) _messages.Close();
        if (_messages is null)
        {
            var w = _messages = new MessageWindow(compose: false);
            w.Closed += (_, _) => { if (_messages == w) _messages = null; };
        }
        _messages.Show(hd);
        Present(_messages);
    }

    /// <summary>"Skriv til support…": en ny sag.</summary>
    public void ShowCompose()
    {
        _messages?.Close();
        var w = _messages = new MessageWindow(compose: true);
        w.Closed += (_, _) =>
        {
            if (_messages != w) return;
            _messages = null;
            // kom der en besked, mens brugeren skrev, vises den nu
            if (_hd?.Unread.Any() == true) ShowMessages();
        };
        Present(w);
    }

    /// <summary>Menuen bygges kun igen, når indholdet ændrer sig (så den ikke blinker, mens den er åben).</summary>
    private void BuildMenu(DateTime now)
    {
        var st = _st;
        var paused = st?.PausedUntil > now;
        var power = st?.PowerAt > now;
        var needsAccess = TrayView.NeedsFullDiskAccess(st);
        var head = st is null ? "Fleetdeck – tjenesten kører ikke" : $"Fleetdeck · {st.Machine} · {(st.Connected ? "forbundet" : "ingen forbindelse")}";
        var line = TrayView.Headline(st, now);
        var key = $"{head}|{line}|{paused}|{st?.PausedUntil}|{power}|{needsAccess}|{st?.Upgrades}|{_hd?.Lines.Count}|{_hd?.Unread.Count()}";
        if (key == _menuKey) return;
        _menuKey = key;
        var m = _menu;
        m.Items.Clear();
        m.Items.Add(new NativeMenuItem(head) { IsEnabled = false });
        if (line != "Forbundet" && !line.StartsWith("Forbundet ·")) m.Items.Add(new NativeMenuItem(line) { IsEnabled = false });
        if (needsAccess) m.Items.Add(Item("⚠ Fuld diskadgang mangler – ret det…", ShowAccess));
        m.Items.Add(new NativeMenuItemSeparator());
        m.Items.Add(Item("Åbn status…", ShowStatus));
        m.Items.Add(Item("Funktioner…", ShowFeatures));
        if (_hd?.Lines.Count > 0) m.Items.Add(Item(_hd.Unread.Any() ? $"Beskeder ({_hd.Unread.Count()} ulæste)…" : "Beskeder…", ShowMessages));
        m.Items.Add(Item("Skriv til support…", ShowCompose));
        m.Items.Add(Item("Synkronisér nu", () => Send(new TrayCommand { Action = "sync" }, "Synkroniserer med Fleetdeck…")));
        m.Items.Add(Item(st?.Upgrades > 0 ? $"Opdatér alt nu ({st.Upgrades})" : "Opdatér alt nu", () => Send(new TrayCommand { Action = "upgrade_all" }, "Opdatér alt er sat i gang")));
        m.Items.Add(Item(paused ? $"Genoptag (pause til {st!.PausedUntil!.Value.ToLocalTime():HH:mm})" : "Sæt på pause i 1 time", async () => await SetPause(!paused)));
        if (power) m.Items.Add(Item($"Udsæt {(st!.PowerMode == JobTypes.Reboot ? "genstarten" : "slukningen")} {PlannedPower.PostponeMinutes} min.",
            () => Send(new TrayCommand { Action = "power_postpone" }, "Udsat")));
        if (power) m.Items.Add(Item(PlannedPower.NowLabel(st!.PowerMode ?? JobTypes.Reboot),
            () => Send(new TrayCommand { Action = "power_now", Id = st.PowerMode }, st.PowerMode == JobTypes.Reboot ? "Genstarter nu" : "Slukker nu")));
        m.Items.Add(new NativeMenuItemSeparator());
        m.Items.Add(Item("Åbn Fleetdeck i browseren", OpenWeb));
        m.Items.Add(Item("ESP Flasher", OpenEsp));
        m.Items.Add(Item("Skift server…", ShowSetup));
        m.Items.Add(Item("Om / Fejlfinding…", ShowAbout));
        m.Items.Add(new NativeMenuItemSeparator());
        m.Items.Add(Item("Afslut Fleetdeck", Quit));
    }

    private readonly NativeMenu _menu = new();

    private static NativeMenuItem Item(string header, Action click)
    {
        var i = new NativeMenuItem(header);
        i.Click += (_, _) => click();
        return i;
    }

    private void Send(TrayCommand c, string ok) => MacSys.Notify(MacSys.UserCommand(c) ? ok : "Kunne ikke sende til Fleetdeck-tjenesten (kører den?)");

    /// <summary>Pause/genoptag kræver en administrator (som UAC i Windows), så børn ikke kan sætte styringen på pause.</summary>
    public async Task<bool> SetPause(bool on)
    {
        var ok = await MacSys.AdminCommand(new TrayCommand { Action = on ? "pause" : "resume", Minutes = 60 },
            on ? "Fleetdeck sætter installationer på pause i 1 time." : "Fleetdeck genoptager installationer.");
        if (ok) MacSys.Notify(on ? "Fleetdeck er sat på pause i 1 time" : "Fleetdeck kører igen");
        _menuKey = null;
        return ok;
    }

    public async Task<bool> InstallExtension(string id)
    {
        var ok = await MacSys.AdminCommand(new TrayCommand { Action = "extension", Id = id }, "Fleetdeck installerer en udvidelse.");
        if (ok) MacSys.Notify("Udvidelsen installeres om lidt");
        return ok;
    }

    /// <summary>Funktioner: slå en funktion til/fra (kræver en administrator, så børn ikke kan slå overvågningen fra).</summary>
    public Task<bool> SetFeature(string id, bool on) =>
        MacSys.AdminCommand(MacFeatures.Command(id, on), on ? "Fleetdeck slår en funktion til." : "Fleetdeck slår en funktion fra.");

    public void OpenWeb()
    {
        var url = _st?.WebUrl ?? _st?.Server;
        if (url is { Length: > 0 } && url.StartsWith("http")) MacSys.Open(url);
    }

    /// <summary>ESP Flasher gennem agenten på http://localhost (en sikker side uden certifikat), så USB virker i Chrome og Edge.</summary>
    public void OpenEsp()
    {
        if (LocalWeb.EspUrl(_st?.LocalUrl, _st?.WebUrl ?? _st?.Server) is { } url) MacSys.Open(url);
    }

    public void ShowStatus()
    {
        _status ??= new StatusWindow(this);
        _status.Show(_st ?? MacSys.ReadStatus());
        Present(_status);
    }

    /// <summary>Funktioner: temperaturmåling til/fra og hjælpeværktøjerne (fra status, programmenuen og menulinjen).</summary>
    public void ShowFeatures()
    {
        _features ??= new FeaturesWindow(this);
        _features.Show(_st ?? MacSys.ReadStatus());
        Present(_features);
    }

    public void ShowAccess()
    {
        if (_access is null) { _access = new AccessWindow(); _access.Closed += (_, _) => _access = null; }
        Present(_access);
    }

    public void ShowAbout()
    {
        if (_about is null) { _about = new AboutWindow(this); _about.Closed += (_, _) => _about = null; }
        else _about.Refresh();
        Present(_about);
    }

    /// <summary>Installationsvinduet (første gang, "Skift server" og afinstallation). Lukkes det uden installation, og er der ikke noget ikon, afsluttes appen.</summary>
    public void ShowSetup()
    {
        if (_setup is null)
        {
            _setup = new SetupWindow(Start);
            _setup.Closed += (_, _) => { _setup = null; if (_icon is null) Quit(); };
        }
        Present(_setup);
    }

    /// <summary>
    /// Vis vinduet forrest: appen er måske startet i baggrunden (ved login, fra tjenesten eller en anden kopi), så bed macOS om at
    /// aktivere den (i Dock og Cmd-Tab) og hold vinduet øverst et øjeblik.
    /// </summary>
    private static void Present(Window w)
    {
        MacNative.EnsurePolicy(MacDock.Policy(Program.Mode));
        if (Application.Current?.TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime act) act.TryLeaveBackground();
        MacNative.Activate();
        if (!w.IsVisible) w.Show();
        w.WindowState = WindowState.Normal;
        w.Topmost = true;
        w.Activate();
        DispatcherTimer.RunOnce(() => w.Topmost = false, TimeSpan.FromMilliseconds(600));
    }

    /// <summary>Appen åbnet igen (dobbeltklik i Finder eller fra en anden kopi): vis status, eller installationen hvis agenten mangler.</summary>
    public void Reopen()
    {
        // ikke installeret – eller installeret uden at kunne tilmelde sig (fx "kun agenten", eller en afvist anmodning): opsætningen
        if (MacSetup.ShowSetupOnOpen(MacSys.Installed, MacSys.ReadStatus(), DateTime.UtcNow)) { if (MacSys.Installed) Start(); ShowSetup(); }
        else { Start(); ShowStatus(); }
    }

    private bool Idle => _status?.IsVisible != true && _features?.IsVisible != true && _access is null && _about is null && _setup is null && _messages is null;

    /// <summary>De vinduer, der er åbne og synlige lige nu (det forreste sidst).</summary>
    private List<Window> VisibleWindows() => _life.Windows.Where(w => w.IsVisible).ToList();

    /// <summary>Klik på Dock-ikonet: et åbent vindue hentes frem; ellers opsætningen eller status (som et dobbeltklik i Finder).</summary>
    public void DockClicked()
    {
        var open = VisibleWindows();
        if (MacDock.OnDockClick(open.Count) == MacDock.DockClick.BringToFront)
            Present(open.FirstOrDefault(w => w.IsActive) ?? open[^1]);
        else Reopen();
    }

    /// <summary>macOS afslutter appen (Dock → Slut, Cmd-Q, log ud): vinduerne må lukke i stedet for at gemme sig.</summary>
    public void PrepareQuit()
    {
        Quitting = true;
        _timer.Stop();
        if (_icon is not null) _icon.IsVisible = false;
    }

    /// <summary>Intet ikon og intet vindue åbent: afslut (den native hjælper står for menulinjen).</summary>
    public void QuitIfIdle() { if (_icon is null && Idle) Quit(); }

    public void Quit()
    {
        Quitting = true;
        _timer.Stop();
        if (_icon is not null) _icon.IsVisible = false;
        _life.Shutdown();
    }

    /// <summary>Logoet med en farvet prik nederst til højre (som ikonet i Windows' proceslinje).</summary>
    /// <summary>Færdigtegnede ikoner (logo + statusprik) fra Assets – på macOS gav en tegnet RenderTargetBitmap et usynligt ikon.</summary>
    private WindowIcon IconFor(string color)
    {
        if (_icons.TryGetValue(color, out var cached)) return cached;
        var name = color switch { "#dc2626" => "red", "#2563eb" => "blue", "#d97706" => "amber", "#16a34a" => "green", _ => "gray" };
        WindowIcon icon;
        try { icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri($"avares://Fleetdeck/Assets/tray-{name}.png"))); }
        catch (Exception) { icon = Ui.AppIcon; }
        return _icons[color] = icon;
    }
}
