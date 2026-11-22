namespace Fleetdeck.Shared;

/// <summary>
/// Udvidelser: ekstra dele, som bestemte funktioner kræver (fx temperaturmåling eller fjernskrivebord).
/// De installeres fra opsætningsvinduet (flueben) eller med ét klik på maskinens side i Fleetdeck.
/// Flere kan tilføjes uden ny kode i /data/extensions.json (samme felter som her).
/// </summary>
public sealed record ExtensionDef(
    string Id, string Name, string Description, string Gives,
    ExtensionInstall? Windows = null, ExtensionInstall? Linux = null, ExtensionInstall? Mac = null);

/// <param name="Type">winget | pkg (distroens pakke) | flatpak | brew (Mac) | script | helper (et script fra hjælperen; Identifier = rettelsens id)</param>
/// <param name="Identifier">winget-id, pakkenavn ("apt:x dnf:y …") eller Flathub-id</param>
/// <param name="Script">Script der køres efter installationen (Windows: PowerShell, Linux: bash) – eller selve installationen ved Type=script</param>
/// <param name="Detect">Regulært udtryk mod de installerede programmers navne; "winget" = winget virker (Windows)</param>
public sealed record ExtensionInstall(string Type, string? Identifier = null, string? Script = null, string? Detect = null);

public static class Extensions
{
    /// <summary>Slået til fra start i opsætningsvinduerne og standard for nye maskiner (kan ændres under Indstillinger).</summary>
    public static readonly string[] DefaultOn = ["diskhelbred", "fjernskrivebord"];

    public static readonly ExtensionDef[] BuiltIn =
    [
        // Homebrew først: på Mac installeres flere af de andre med Homebrew, og udvidelserne installeres i denne rækkefølge
        new("homebrew", "Homebrew", "Pakkehåndteringen til Mac: giver adgang til tusindvis af programmer, som Fleetdeck kan installere og opdatere.",
            "Installation og opdatering af programmer",
            Mac: new("script", Script: HomebrewMac, Detect: "^homebrew$")),
        new("temperatur", "Temperaturmåling", "Måler CPU- og GPU-temperatur, så Fleetdeck kan vise den og advare ved varme.",
            "CPU- og GPU-temperatur",
            Windows: new("helper", "temps_enable", Detect: "LibreHardwareMonitor"),   // samme afprøvede script som hjælperen "Slå temperaturmåling til"
            Mac: new("helper", "temps_enable_mac", Detect: MacTemps.Detect)),         // indbygget i agenten (IOKit/powermetrics) – hjælperen slår den til
        new("diskhelbred", "Diskhelbred (SMART)", "Læser diskenes helbred, slid og temperatur, så en disk på vej til at gå i stykker opdages i tide.",
            "Diskhelbred og -temperatur",
            Linux: new("pkg", "smartmontools", Detect: "^smartmontools$"),
            Mac: new("helper", "disk_tools_mac", Detect: "^smartmontools$")),   // smartmontools fra Homebrew (uden Homebrew: forklaring, ingen fejl)
        new("sensorer", "Sensorer (lm-sensors)", "Flere temperaturer og blæserhastigheder på Linux (CPU-temperaturen virker også uden).",
            "Ekstra temperaturer og blæsere",
            Linux: new("pkg", "apt:lm-sensors dnf:lm_sensors pacman:lm_sensors zypper:sensors", Detect: "^(lm-sensors|lm_sensors|sensors)$")),
        new("fjernskrivebord", "Fjernskrivebord (RustDesk)", "Se og styr skærmen på maskinen hjemmefra eller fra en anden pc.",
            "Fjernskrivebord",
            Windows: new("script", Script: RustDeskWindows, Detect: "RustDesk"),
            Mac: new("brew", "cask:rustdesk", Detect: "^(RustDesk|rustdesk)$"),
            Linux: new("script", Script: RustDeskLinux, Detect: "^(rustdesk|com\\.rustdesk\\.RustDesk)$")),
        new("winget", "Programinstallation (winget)", "Installerer Microsofts winget, hvis det mangler (fx på Windows Server eller LTSC). Uden det kan der ikke installeres programmer.",
            "Installation og opdatering af programmer",
            Windows: new("script", Script: WingetRepair, Detect: "winget")),
        new("mas", "App Store (mas)", "Lader Fleetdeck installere og opdatere programmer fra Mac App Store (brugeren skal være logget ind i App Store).",
            "Programmer fra App Store",
            Mac: new("brew", "formula:mas", Detect: "^mas$")),
        new("flatpak", "Flatpak-programmer", "Giver adgang til tusindvis af programmer fra Flathub på alle Linux-distroer.",
            "Programmer fra Flathub",
            Linux: new("pkg", "flatpak", Detect: "^flatpak$",
                Script: "flatpak remote-add --if-not-exists --system flathub https://dl.flathub.org/repo/flathub.flatpakrepo && echo 'Flathub er klar'")),
        new(UsbIpadId, "iPad via USB", "Sæt en iPad i kablet på denne pc og styr den fra Fleetdeck: låst profil, krypteret backup og gendannelse. " +
                "Installerer Apples USB-tjeneste (Windows: Apple Mobile Device Support, Linux: usbmuxd) og pymobiledevice3 i sin egen mappe.",
            "iPads i kabel på denne pc",
            Windows: new("script", Script: UsbIpadWindows, Detect: UsbIpadDetect),
            Linux: new("script", Script: UsbIpadUnix, Detect: UsbIpadDetect),
            Mac: new("script", Script: UsbIpadUnix, Detect: UsbIpadDetect)),
    ];

    public const string UsbIpadId = "ipad-usb";
    /// <summary>Særlig Detect-værdi: installeret = agenten melder, at pymobiledevice3 virker (ikke et navn i programlisten).</summary>
    public const string UsbIpadDetect = "usbipad";

    public static ExtensionInstall? For(this ExtensionDef d, string? platform) => platform switch
    {
        Platforms.Linux => d.Linux, Platforms.Mac => d.Mac, _ => d.Windows,
    };

    /// <summary>winget via Microsofts eget modul (Repair-WinGetPackageManager), også uden Microsoft Store.</summary>
    private const string WingetRepair = """
        $ErrorActionPreference = 'Stop'; $ProgressPreference = 'SilentlyContinue'
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Install-PackageProvider -Name NuGet -MinimumVersion 2.8.5.201 -Force -Scope AllUsers | Out-Null
        Set-PSRepository -Name PSGallery -InstallationPolicy Trusted
        Install-Module Microsoft.WinGet.Client -Force -Scope AllUsers -AllowClobber
        Import-Module Microsoft.WinGet.Client
        Repair-WinGetPackageManager -AllUsers -Force -Latest
        Write-Output 'winget er installeret.'
        """;

    /// <summary>Homebrews officielle .pkg kan installeres uden spørgsmål og lægger Homebrew til brugeren på skærmen.</summary>
    private const string HomebrewMac = """
        set -e
        if [ -x /opt/homebrew/bin/brew ] || [ -x /usr/local/bin/brew ]; then echo "Homebrew er allerede installeret"; exit 0; fi
        U=$(curl -fsSL https://api.github.com/repos/Homebrew/brew/releases/latest | grep -oE 'https://[^"]+/Homebrew(-[0-9.]+)?\.pkg' | head -1)   # pakken hed Homebrew-x.y.z.pkg, nu Homebrew.pkg
        [ -n "$U" ] || { echo "Fandt ikke Homebrews installationspakke" >&2; exit 1; }
        T=$(mktemp -d); trap 'rm -rf "$T"' EXIT
        # Homebrew kræver Apples kommandolinjeværktøjer (CLT): installér dem lydløst via Softwareopdatering, som Homebrews eget script gør
        if ! xcode-select -p >/dev/null 2>&1; then
          echo "Installerer Apples kommandolinjeværktøjer (Command Line Tools) – det kan tage 5–15 minutter"
          F=/tmp/.com.apple.dt.CommandLineTools.installondemand.in-progress; touch "$F"
          P=$(softwareupdate -l 2>/dev/null | grep -E '^ *\* Label: Command Line Tools|^ *\* Command Line Tools' | sed -E 's/^ *\* (Label: )?//' | sort -V | tail -1)
          if [ -n "$P" ]; then softwareupdate -i "$P" --verbose || true; fi
          rm -f "$F"
          xcode-select -p >/dev/null 2>&1 || { echo "Kommandolinjeværktøjerne kunne ikke installeres automatisk. Kør 'xcode-select --install' på Mac'en og prøv igen." >&2; exit 1; }
        fi
        curl -fsSL -o "$T/Homebrew.pkg" "$U"
        if ! installer -pkg "$T/Homebrew.pkg" -target /; then
          # den nyeste pakke afviser ældre macOS (fx 13): brug den nyeste 4.x-pakke, som understøtter den – brew opdaterer sig selv bagefter
          echo "Den nyeste Homebrew-pakke kan ikke bruges på macOS $(sw_vers -productVersion) – prøver den seneste 4.x-pakke"
          U=$(curl -fsSL "https://api.github.com/repos/Homebrew/brew/releases?per_page=100" | grep -oE 'https://[^"]+/download/4\.[0-9.]+/Homebrew-4[0-9.]*\.pkg' | head -1)
          [ -n "$U" ] || { echo "Fandt ingen ældre Homebrew-pakke" >&2; exit 1; }
          curl -fsSL -o "$T/Homebrew.pkg" "$U"
          installer -pkg "$T/Homebrew.pkg" -target /
        fi
        echo "Homebrew er installeret."
        """;

    /// <summary>RustDesk er ikke (længere) i winget: hent den nyeste MSI fra RustDesks udgivelser og installér lydløst (tjeneste for hele maskinen).</summary>
    private const string RustDeskWindows = """
        $ErrorActionPreference = 'Stop'
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        $exe = Join-Path $env:ProgramFiles 'RustDesk\rustdesk.exe'
        if (Test-Path $exe) { 'RustDesk er allerede installeret'; exit 0 }
        $rel = Invoke-RestMethod -UseBasicParsing -Uri 'https://api.github.com/repos/rustdesk/rustdesk/releases/latest' -Headers @{ 'User-Agent' = 'Fleetdeck' }
        $asset = $rel.assets | Where-Object { $_.name -match '^rustdesk-[\d.]+-x86_64\.msi$' } | Select-Object -First 1
        if (-not $asset) { 'Fandt ingen MSI i RustDesks seneste udgivelse'; exit 1 }
        $msi = Join-Path $env:TEMP ("fleetdeck-" + [guid]::NewGuid().ToString('N') + '.msi')   # uforudsigeligt navn (TEMP er C:\Windows\Temp for SYSTEM)
        Invoke-WebRequest -UseBasicParsing -Uri $asset.browser_download_url -OutFile $msi
        "Installerer $($asset.name)"
        $p = Start-Process msiexec.exe -ArgumentList @('/i', "`"$msi`"", '/qn', '/norestart') -Wait -PassThru
        Remove-Item $msi -Force -ErrorAction SilentlyContinue
        if ($p.ExitCode -notin 0, 3010) { "msiexec gav kode $($p.ExitCode)"; exit $p.ExitCode }
        for ($i = 0; $i -lt 30 -and -not (Test-Path $exe); $i++) { Start-Sleep 2 }
        if (Test-Path $exe) { 'RustDesk er installeret' } else { 'RustDesk blev ikke fundet efter installationen'; exit 1 }
        """;

    /// <summary>RustDesk ligger ikke i distroernes pakkekilder: hent .deb/.rpm fra RustDesks udgivelser, ellers Flathub.</summary>
    private const string RustDeskLinux = """
        set -e
        if command -v rustdesk >/dev/null || (command -v flatpak >/dev/null && flatpak info com.rustdesk.RustDesk >/dev/null 2>&1); then echo "RustDesk er allerede installeret"; exit 0; fi
        case "$(uname -m)" in x86_64) A=x86_64 ;; aarch64) A=aarch64 ;; armv7l) A=armv7 ;; *) A=none ;; esac
        J=$(curl -fsSL https://api.github.com/repos/rustdesk/rustdesk/releases/latest || true)
        T=$(mktemp -d); trap 'rm -rf "$T"' EXIT   # aldrig faste navne i /tmp, når root installerer filen
        if command -v apt-get >/dev/null; then
          U=$(printf '%s' "$J" | grep -oE "https://[^\"]+/rustdesk-[0-9.]+-$A[^\"]*\.deb" | grep -v sciter | head -1)
          if [ -n "$U" ] && curl -fsSL -o "$T/rustdesk.deb" "$U"; then apt-get install -y "$T/rustdesk.deb"; exit 0; fi
        elif command -v dnf >/dev/null || command -v zypper >/dev/null; then
          U=$(printf '%s' "$J" | grep -oE "https://[^\"]+/rustdesk-[0-9.]+-[0-9]*\.?$A[^\"]*\.rpm" | grep -v suse | head -1)
          if command -v zypper >/dev/null; then U=$(printf '%s' "$J" | grep -oE "https://[^\"]+/rustdesk-[0-9.]+-[0-9]*\.?$A-suse[^\"]*\.rpm" | head -1); fi
          if [ -n "$U" ] && curl -fsSL -o "$T/rustdesk.rpm" "$U"; then
            if command -v dnf >/dev/null; then dnf install -y "$T/rustdesk.rpm"; else zypper -n --no-gpg-checks install "$T/rustdesk.rpm"; fi
            exit 0
          fi
        fi
        if command -v flatpak >/dev/null; then
          flatpak remote-add --if-not-exists --system flathub https://dl.flathub.org/repo/flathub.flatpakrepo
          flatpak install -y --noninteractive flathub com.rustdesk.RustDesk && exit 0
        fi
        echo "RustDesk kunne ikke installeres automatisk her. Installér udvidelsen Flatpak-programmer først, eller hent RustDesk fra rustdesk.com." >&2
        exit 1
        """;

    /// <summary>
    /// iPad via USB (Linux og Mac): usbmuxd fra distroen (macOS har sin egen), uv og pymobiledevice3 i en privat mappe med egen Python.
    /// Agentens vagt finder selv iPads, når Python-filen findes.
    /// </summary>
    private const string UsbIpadUnix = $$"""
        set -e
        : "${HOME:=/root}"; export HOME
        D=/opt/fleetdeck/ipad-usb; V={{UsbIpadPaths.Pmd3Version}}
        if [ "$(uname)" = Darwin ]; then D=/usr/local/fleetdeck/ipad-usb; fi
        if [ -f /etc/unraid-version ]; then echo "iPad via USB bruges ikke på Unraid." >&2; exit 1; fi
        if [ "$(uname)" != Darwin ] && ! command -v usbmuxd >/dev/null 2>&1; then
          echo "Installerer usbmuxd"
          if command -v apt-get >/dev/null; then
            export DEBIAN_FRONTEND=noninteractive
            apt-get install -y usbmuxd || { apt-get update && apt-get install -y usbmuxd; }
          elif command -v dnf >/dev/null; then dnf install -y usbmuxd
          elif command -v zypper >/dev/null; then zypper -n install usbmuxd
          elif command -v pacman >/dev/null; then pacman -S --noconfirm --needed usbmuxd
          else echo "Ukendt pakkehåndtering – installér usbmuxd selv." >&2; exit 1; fi
        fi
        mkdir -p "$D"; chmod 755 "$D"
        # tjenesten har ikke altid en skrivbar hjemmemappe (macOS: HOME=/root, som er skrivebeskyttet) – uv skal bruge én
        if [ -z "$HOME" ] || ! mkdir -p "$HOME/.config" 2>/dev/null; then export HOME="$D/home"; mkdir -p "$HOME"; fi
        U="$D/uv"
        if [ ! -x "$U" ]; then
          echo "Henter uv (Python-værktøj)"
          curl -LsSf https://astral.sh/uv/install.sh | env UV_UNMANAGED_INSTALL="$D/uvbin" UV_NO_MODIFY_PATH=1 INSTALLER_NO_MODIFY_PATH=1 sh
          for c in "$D/uvbin/uv" "$D/uvbin/bin/uv"; do if [ -x "$c" ]; then cp "$c" "$U"; break; fi; done
        fi
        [ -x "$U" ] || { echo "uv kunne ikke installeres." >&2; exit 1; }
        export UV_TOOL_DIR="$D/tools" UV_TOOL_BIN_DIR="$D/bin" UV_PYTHON_INSTALL_DIR="$D/python" UV_CACHE_DIR="$D/cache" UV_PYTHON_PREFERENCE=only-managed UV_NO_MODIFY_PATH=1
        echo "Installerer pymobiledevice3 $V"
        "$U" tool install --force --python 3.12 "pymobiledevice3==$V"
        "$D/tools/pymobiledevice3/bin/python" -I -c "import pymobiledevice3; print('pymobiledevice3 er installeret')"
        "$U" cache clean >/dev/null 2>&1 || true
        mkdir -p "$D/work"; chmod 700 "$D/work"
        echo "iPad via USB er klar. Sæt iPad'en i, lås den op og tryk \"Stol på\". Var den allerede sat i, så tag kablet ud og sæt det i igen."
        """;

    /// <summary>
    /// iPad via USB (Windows): Apples USB-driver og -tjeneste (winget Apple.AppleMobileDeviceSupport, ellers appen Apple Devices fra
    /// Microsoft Store, 9NP83LWLPZ9K), uv og pymobiledevice3 i %ProgramData%\Fleetdeck\ipad-usb med egen Python.
    /// </summary>
    private const string UsbIpadWindows = $$"""
        $ErrorActionPreference = 'Continue'; $ProgressPreference = 'SilentlyContinue'
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        $D = Join-Path $env:ProgramData 'Fleetdeck\ipad-usb'; $V = '{{UsbIpadPaths.Pmd3Version}}'
        New-Item -ItemType Directory -Force -Path $D | Out-Null
        function Test-Muxd { try { $c = New-Object Net.Sockets.TcpClient; $ok = $c.ConnectAsync('127.0.0.1', 27015).Wait(1500) -and $c.Connected; $c.Close(); $ok } catch { $false } }
        function Get-AppleSvc { Get-Service -ErrorAction SilentlyContinue | Where-Object { $_.Name -like '*Apple*Mobile*Device*' -or $_.DisplayName -like '*Apple*Mobile*Device*' } | Select-Object -First 1 }
        if (-not (Test-Muxd) -and -not (Get-AppleSvc)) {
          $wg = (Get-Command winget.exe -ErrorAction SilentlyContinue).Source
          if (-not $wg) { $wg = Get-ChildItem "$env:ProgramFiles\WindowsApps\Microsoft.DesktopAppInstaller_*_x64__8wekyb3d8bbwe\winget.exe" -ErrorAction SilentlyContinue | Sort-Object FullName | Select-Object -Last 1 -ExpandProperty FullName }
          if ($wg) {
            'Installerer Apples USB-driver (Apple Mobile Device Support)'
            & $wg install --id Apple.AppleMobileDeviceSupport -e --scope machine --silent --accept-package-agreements --accept-source-agreements --disable-interactivity
            if (-not (Get-AppleSvc)) {
              'Prøver appen Apple Devices fra Microsoft Store'
              & $wg install --id 9NP83LWLPZ9K --source msstore --silent --accept-package-agreements --accept-source-agreements --disable-interactivity
            }
          }
          if (-not (Get-AppleSvc) -and -not (Test-Muxd)) {
            'Apples USB-driver mangler. Installér appen "Apple Devices" fra Microsoft Store (eller iTunes) på pc''en, og installér udvidelsen igen.'
            exit 1
          }
        }
        $svc = Get-AppleSvc
        if ($svc -and $svc.Status -ne 'Running') { try { Set-Service $svc.Name -StartupType Automatic; Start-Service $svc.Name } catch { "Kunne ikke starte $($svc.DisplayName): $_" } }
        $U = Join-Path $D 'uv.exe'
        if (-not (Test-Path $U)) {
          'Henter uv (Python-værktøj)'
          $env:UV_INSTALL_DIR = Join-Path $D 'uvbin'; $env:UV_NO_MODIFY_PATH = '1'; $env:INSTALLER_NO_MODIFY_PATH = '1'
          powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "irm https://astral.sh/uv/install.ps1 | iex"
          $c = Get-ChildItem (Join-Path $D 'uvbin') -Recurse -Filter uv.exe -ErrorAction SilentlyContinue | Select-Object -First 1
          if ($c) { Copy-Item $c.FullName $U -Force }
        }
        if (-not (Test-Path $U)) { 'uv kunne ikke installeres.'; exit 1 }
        $env:UV_TOOL_DIR = "$D\tools"; $env:UV_TOOL_BIN_DIR = "$D\bin"; $env:UV_PYTHON_INSTALL_DIR = "$D\python"; $env:UV_CACHE_DIR = "$D\cache"
        $env:UV_PYTHON_PREFERENCE = 'only-managed'; $env:UV_NO_MODIFY_PATH = '1'
        "Installerer pymobiledevice3 $V"
        & $U tool install --force --python 3.12 "pymobiledevice3==$V"
        if ($LASTEXITCODE -ne 0) { "pymobiledevice3 kunne ikke installeres (kode $LASTEXITCODE)"; exit 1 }
        & "$D\tools\pymobiledevice3\Scripts\python.exe" -I -c "import pymobiledevice3; print('pymobiledevice3 er installeret')"
        if ($LASTEXITCODE -ne 0) { 'pymobiledevice3 kan ikke startes.'; exit 1 }
        & $U cache clean 2>$null | Out-Null
        New-Item -ItemType Directory -Force -Path "$D\work" | Out-Null
        'iPad via USB er klar. Sæt iPad''en i, lås den op og tryk "Stol på". Var den allerede sat i, så tag kablet ud og sæt det i igen.'
        """;
}
