using Fleetdeck.Shared;
namespace Fleetdeck.Server.Services;

/// <summary>Risiko for en rettelse: safe = må køre automatisk, confirm = kræver klik, danger = kræver at man skriver maskinens navn.</summary>
public static class Risk
{
    public const string Safe = "safe", Confirm = "confirm", Danger = "danger";
}

/// <summary>En rettelse eller et værktøj som hjælperen kan køre på en maskine.</summary>
public sealed record Fix(
    string Id, string Title, string Description, string Risk, string Group,
    string Kind,                 // script | job | winsetup | retry | wake | multi | debloat | services
    string RunAs = "system", string? Script = null, string? JobType = null, string? Step = null, int TimeoutMin = 30,
    bool Tool = true, bool AutoAllowed = false, string Button = "Ret det");

/// <summary>
/// Forklaring af en alarm: hvad den betyder (Why), trinene til at løse den (Steps, 1-4 korte trin) og den rettelse, knappen kører (FixId).
/// What er trinene i én tekst (til ældre visninger). Button overstyrer rettelsens knaptekst (fx "Start tjenesterne").
/// </summary>
public sealed record Advice(string Why, string? FixId, IReadOnlyList<string> Steps, string? Button = null)
{
    public string What => string.Join(" ", Steps);
}

public static class HelperCatalog
{
    // ------------------------------------------------------------------ PowerShell-scripts (Windows PowerShell 5.1, kører som SYSTEM)
    private const string FindWinget = """
        $wg = Get-ChildItem "$env:ProgramFiles\WindowsApps" -Directory -Filter 'Microsoft.DesktopAppInstaller_*_x64__8wekyb3d8bbwe' -ErrorAction SilentlyContinue |
            Where-Object { Test-Path (Join-Path $_.FullName 'winget.exe') } | Sort-Object { [version]($_.Name.Split('_')[1]) } -Descending | Select-Object -First 1
        $wg = if ($wg) { Join-Path $wg.FullName 'winget.exe' } else { (Get-Command winget.exe -ErrorAction SilentlyContinue).Source }

        """;

    public const string DefenderEnable = """
        $ErrorActionPreference = 'Continue'
        $done = New-Object System.Collections.ArrayList
        $pol = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows Defender'
        foreach ($n in 'DisableAntiSpyware', 'DisableAntiVirus', 'DisableRoutinelyTakingAction') {
            if ($null -ne (Get-ItemProperty $pol -Name $n -ErrorAction SilentlyContinue)) { Remove-ItemProperty $pol -Name $n -ErrorAction SilentlyContinue; [void]$done.Add("Fjernede politikken $n") }
        }
        $rtp = "$pol\Real-Time Protection"
        foreach ($n in 'DisableRealtimeMonitoring', 'DisableBehaviorMonitoring', 'DisableOnAccessProtection', 'DisableIOAVProtection', 'DisableScanOnRealtimeEnable') {
            if ($null -ne (Get-ItemProperty $rtp -Name $n -ErrorAction SilentlyContinue)) { Remove-ItemProperty $rtp -Name $n -ErrorAction SilentlyContinue; [void]$done.Add("Fjernede politikken $n") }
        }
        # "Debloat"-værktøjer slår også Defender fra i dens egen nøgle (ikke politik) – den overstyrer alt andet
        $own = 'HKLM:\SOFTWARE\Microsoft\Windows Defender'
        foreach ($p in @(@($own, 'DisableAntiSpyware'), @($own, 'DisableAntiVirus'), @("$own\Real-Time Protection", 'DisableRealtimeMonitoring'),
                         @("$own\Real-Time Protection", 'DisableBehaviorMonitoring'), @("$own\Real-Time Protection", 'DisableOnAccessProtection'))) {
            $v = (Get-ItemProperty $p[0] -Name $p[1] -ErrorAction SilentlyContinue).($p[1])
            if ($v -and $v -ne 0) {
                try { Set-ItemProperty $p[0] -Name $p[1] -Value 0 -Type DWord -ErrorAction Stop; [void]$done.Add("Slog $($p[1]) fra i Defenders egen opsætning") }
                catch { [void]$done.Add("Kunne ikke rette $($p[1]) ($($_.Exception.Message))") }
            }
        }
        # Tjenester og drivere tilbage til Windows' standard (0 = start ved boot, 2 = automatisk, 3 = efter behov, 4 = slået fra)
        $needReboot = $false
        $std = [ordered]@{ WdBoot = 0; WdFilter = 0; WdNisDrv = 3; WinDefend = 2; WdNisSvc = 3; SecurityHealthService = 3; wscsvc = 2; Sense = 3; MDCoreSvc = 3 }
        foreach ($svc in $std.Keys) {
            $k = "HKLM:\SYSTEM\CurrentControlSet\Services\$svc"
            if (-not (Test-Path $k)) { continue }
            $start = (Get-ItemProperty $k -Name Start -ErrorAction SilentlyContinue).Start
            $want = $std[$svc]
            # kun det der er slået fra (4) – og Defenders filterdriver skal starte ved boot, ellers virker realtidsbeskyttelsen ikke
            if ($start -eq 4 -or ($svc -in 'WdFilter', 'WdBoot' -and $start -ne $want)) {
                try { Set-ItemProperty $k -Name Start -Value $want -ErrorAction Stop; [void]$done.Add("$svc var slået fra ($start) - sat til standard ($want)"); $needReboot = $true }
                catch { [void]$done.Add("Kunne ikke ændre $svc ($($_.Exception.Message))") }
            }
        }
        Start-Service WinDefend -ErrorAction SilentlyContinue
        try { Set-MpPreference -DisableRealtimeMonitoring $false -ErrorAction Stop } catch { }
        try { Update-MpSignature -ErrorAction Stop | Out-Null; [void]$done.Add('Virusdefinitioner opdateret') } catch { }
        if ($done.Count) { 'Ændringer:'; $done | ForEach-Object { "  - $_" } } else { 'Ingen politikker eller slåede-fra tjenester fundet.' }
        Get-CimInstance -Namespace root/SecurityCenter2 -ClassName AntiVirusProduct -ErrorAction SilentlyContinue | ForEach-Object { "Registreret antivirus: $($_.displayName)" }
        $s = Get-MpComputerStatus -ErrorAction SilentlyContinue
        if ($s) { "Defender nu: tilstand $($s.AMRunningMode), antivirus $($s.AntivirusEnabled), realtid $($s.RealTimeProtectionEnabled)" }
        if ($s -and $s.RealTimeProtectionEnabled) { 'RESULTAT: Microsoft Defender er slået til.'; exit 0 }
        if ($needReboot) { 'RESULTAT: Defender er sat op igen. Genstart pc''en, så Defenders drivere og tjeneste starter. Kør "Tjek helbred" bagefter.'; exit 0 }
        'RESULTAT: Defender kører stadig ikke. Åbn Windows Sikkerhed -> Virus- og trusselsbeskyttelse. Tjek om et andet antivirus eller et "debloat"-værktøj har fjernet eller blokeret Defender.'
        exit 1
        """;

    public const string DefenderSignatures = """
        Update-MpSignature -ErrorAction Stop
        $s = Get-MpComputerStatus
        "Virusdefinitioner opdateret: version $($s.AntivirusSignatureVersion), $($s.AntivirusSignatureLastUpdated)"
        """;

    public const string DefenderScan = """
        'Starter hurtig scanning med Microsoft Defender ...'
        Update-MpSignature -ErrorAction SilentlyContinue
        Start-MpScan -ScanType QuickScan -ErrorAction Stop
        $t = @(Get-MpThreatDetection -ErrorAction SilentlyContinue | Where-Object { $_.InitialDetectionTime -gt (Get-Date).AddDays(-30) })
        "Scanning færdig. Trusler de sidste 30 dage: $($t.Count)"
        $t | Select-Object -First 15 | ForEach-Object { "  - $($_.InitialDetectionTime): $((Get-MpThreat -ThreatID $_.ThreatID -ErrorAction SilentlyContinue).ThreatName) - $($_.Resources -join ', ')" }
        """;

    public const string DiskCleanup = """
        function Free { [math]::Round((Get-PSDrive C).Free / 1GB, 2) }
        $before = Free
        $targets = @("$env:windir\Temp\*", 'C:\Users\*\AppData\Local\Temp\*', "$env:windir\SoftwareDistribution\Download\*",
                     'C:\Users\*\AppData\Local\CrashDumps\*', "$env:windir\Logs\CBS\*.cab", 'C:\ProgramData\Microsoft\Windows\WER\ReportArchive\*')
        Stop-Service wuauserv, bits -Force -ErrorAction SilentlyContinue
        foreach ($p in $targets) { Remove-Item $p -Recurse -Force -ErrorAction SilentlyContinue }
        Start-Service bits, wuauserv -ErrorAction SilentlyContinue
        'Rydder op i Windows'' komponentlager (kan tage nogle minutter) ...'
        Dism.exe /Online /Cleanup-Image /StartComponentCleanup /Quiet | Out-Null
        $after = Free
        "Fri plads på C: før $before GB, efter $after GB. Frigjort: $([math]::Round($after - $before, 2)) GB."
        'Papirkurven og dine egne filer er ikke rørt.'
        """;

    public const string AutostartRepair = """
        $n = 'Fleetdeck'
        $cmd = "cmd.exe /c sc query $n | findstr /i RUNNING >nul || sc start $n"
        sc.exe config $n start= delayed-auto | Out-Null
        sc.exe failure $n reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null
        sc.exe failureflag $n 1 | Out-Null
        schtasks.exe /Create /F /RU SYSTEM /RL HIGHEST /SC MINUTE /MO 5 /TN "Fleetdeck\Vagthund" /TR $cmd | Out-Null
        schtasks.exe /Create /F /RU SYSTEM /RL HIGHEST /SC ONSTART /DELAY 0001:30 /TN "Fleetdeck\Vagthund ved opstart" /TR $cmd | Out-Null
        $s = Get-CimInstance Win32_Service -Filter "Name='$n'"
        "Tjeneste: start=$($s.StartMode) (forsinket), tilstand=$($s.State)"
        "Vagthund: " + ((schtasks.exe /Query /TN "Fleetdeck\Vagthund" /FO LIST | Select-String 'Status') -replace '\s+',' ')
        """;

    /// <summary>Linux: værktøjer til diskhelbred (SMART for SATA og NVMe) – derefter læses disken ved næste helbredstjek.</summary>
    public const string DiskToolsLinux = """
        set -e
        # Kun diske, der kan give SMART (ikke SD-kort/eMMC, RAM/loop, RAID/LVM, optiske): ellers installeres intet,
        # for smartd fejler uden diske at overvåge (fx på en Raspberry Pi).
        disks=$(lsblk -dno NAME,TYPE | awk '$2=="disk"{print $1}' | grep -Ev '^(mmcblk|zram|loop|ram|md|dm-|nbd|sr|fd|zd)' || true)
        if [ -z "$disks" ]; then echo "Ingen diske med SMART (kun SD-kort/eMMC eller virtuelle) – intet at installere."; exit 0; fi
        if command -v apt-get >/dev/null; then DEBIAN_FRONTEND=noninteractive apt-get install -y -qq smartmontools nvme-cli >/dev/null
        elif command -v dnf >/dev/null; then dnf install -y -q smartmontools nvme-cli
        elif command -v zypper >/dev/null; then zypper -n -q install smartmontools nvme-cli
        elif command -v pacman >/dev/null; then pacman -S --noconfirm --needed smartmontools nvme-cli
        else echo "Ukendt pakkesystem – installér smartmontools manuelt"; exit 1; fi
        # nvme-cli bringer NVMe-over-Fabrics-tjenester med; uden en opsætning i /etc/nvme har de intet at lave og står som fejlet.
        if ! grep -qsv '^[[:space:]]*\(#\|$\)' /etc/nvme/discovery.conf /etc/nvme/config.json 2>/dev/null; then
          for u in nvmf-autoconnect.service nvmefc-boot-connections.service; do
            if systemctl list-unit-files "$u" --no-legend 2>/dev/null | grep -q .; then systemctl disable --now "$u" >/dev/null 2>&1 || true; systemctl mask "$u" >/dev/null 2>&1 || true; fi
          done
        fi
        # smartd uden noget at overvåge stopper med fejl: så slås den fra (Fleetdeck læser selv SMART med smartctl).
        if [ -z "$(smartctl --scan 2>/dev/null)" ]; then
          for u in smartmontools.service smartd.service; do systemctl disable --now "$u" >/dev/null 2>&1 || true; done
        fi
        systemctl reset-failed nvmf-autoconnect.service nvmefc-boot-connections.service smartmontools.service smartd.service 2>/dev/null || true
        for d in $disks; do echo "== /dev/$d"; smartctl -H /dev/$d 2>/dev/null | grep -i -E "result|status" | head -1; done
        """;

    /// <summary>macOS: programfirewallen (Indstillinger → Netværk → Firewall) slås til med Apples eget værktøj.</summary>
    public const string FirewallEnableMac = """
        FW=/usr/libexec/ApplicationFirewall/socketfilterfw
        $FW --setglobalstate on
        $FW --getglobalstate
        """;

    /// <summary>
    /// macOS: temperaturmålingen er indbygget i agenten (Apple silicon: IOKit-sensorerne; Intel: SMC'en eller Apples powermetrics) –
    /// intet program at installere. Scriptet fjerner "slået fra"-markøren og viser den første måling.
    /// </summary>
    public const string TempsEnableMac = $"""
        rm -f '{MacTemps.OffFile}'
        echo "Temperaturmåling er slået til (indbygget i Fleetdeck – intet ekstra program)."
        A='{MacPaths.AgentExe}'
        if [ -x "$A" ]; then "$A" sensors 2>/dev/null | grep -E '^Temperatur' || echo "Agenten kan ikke måle endnu – opdatér den."; fi
        """;

    /// <summary>macOS: smartmontools fra Homebrew (som brugeren, Homebrew må ikke køre som root). Uden Homebrew: en forklaring, ingen fejl.</summary>
    public const string DiskToolsMac = """
        B=""
        for x in /opt/homebrew/bin/brew /usr/local/bin/brew; do if [ -x "$x" ]; then B="$x"; break; fi; done
        if [ -z "$B" ]; then
          echo "Homebrew mangler på Mac'en – slå udvidelsen \"Homebrew\" til først. Indtil da viser Fleetdeck kun diskens SMART-status fra diskutil."
          exit 0
        fi
        U=$(stat -f %Su /dev/console 2>/dev/null)
        if [ -z "$U" ] || [ "$U" = root ]; then U=$(stat -f %Su "$(dirname "$(dirname "$B")")" 2>/dev/null); fi
        if [ -z "$U" ] || [ "$U" = root ]; then echo "Ingen bruger at køre Homebrew som (log ind på Mac'en)." >&2; exit 1; fi
        sudo -u "$U" -H env HOMEBREW_NO_AUTO_UPDATE=1 HOMEBREW_NO_INSTALL_CLEANUP=1 NONINTERACTIVE=1 "$B" install smartmontools || exit 1
        S="$(dirname "$B")/smartctl"
        if [ -x "$S" ]; then "$S" -H disk0 2>/dev/null | grep -iE "result|status" | head -1; fi
        echo "Diskhelbred er slået til – slid og temperatur ses under Helbred efter næste rapport."
        """;

    public const string FirewallEnable = """
        Set-NetFirewallProfile -Profile Domain, Private, Public -Enabled True -ErrorAction Stop
        Get-NetFirewallProfile | ForEach-Object { "Firewall $($_.Name): $(if ($_.Enabled) { 'til' } else { 'FRA' })" }
        """;

    public const string CrashAnalysis = """
        $codes = @{
            '0x00000116' = 'VIDEO_TDR_FAILURE: grafikdriveren holdt op med at svare. Opdatér eller geninstallér grafikdriveren, og fjern eventuel GPU-overclock.'
            '0x00000117' = 'VIDEO_TDR_TIMEOUT_DETECTED: grafikdriveren svarede for langsomt. Samme råd som ovenfor.'
            '0x00000124' = 'WHEA_UNCORRECTABLE_ERROR: hardwarefejl. Ofte ustabil overclock, CPU-spænding, RAM/XMP eller strømforsyning.'
            '0x00000101' = 'CLOCK_WATCHDOG_TIMEOUT: CPU''en svarede ikke. Ofte overclock eller for lav spænding.'
            '0x00000133' = 'DPC_WATCHDOG_VIOLATION: en driver eller SSD-firmware hang. Opdatér chipset- og SSD-firmware.'
            '0x0000009f' = 'DRIVER_POWER_STATE_FAILURE: en driver fejlede ved dvale eller strømskift.'
            '0x000000d1' = 'DRIVER_IRQL_NOT_LESS_OR_EQUAL: fejl i en driver (ofte netkort, lyd eller USB).'
            '0x0000000a' = 'IRQL_NOT_LESS_OR_EQUAL: driver- eller RAM-fejl.'
            '0x0000003b' = 'SYSTEM_SERVICE_EXCEPTION: fejl i en driver eller systemfil.'
            '0x00000050' = 'PAGE_FAULT_IN_NONPAGED_AREA: RAM- eller driverfejl. Test RAM og slå XMP fra som test.'
            '0x0000001a' = 'MEMORY_MANAGEMENT: typisk RAM-fejl eller for aggressiv RAM-hastighed.'
            '0x000000ef' = 'CRITICAL_PROCESS_DIED: en vigtig Windows-proces døde. Kør "Reparér Windows".'
            '0x0000007e' = 'SYSTEM_THREAD_EXCEPTION_NOT_HANDLED: fejl i en driver.'
            '0x00000139' = 'KERNEL_SECURITY_CHECK_FAILURE: driver- eller hukommelsesfejl.'
            '0x0000001e' = 'KMODE_EXCEPTION_NOT_HANDLED: fejl i en driver.'
            '0x00000019' = 'BAD_POOL_HEADER: driver- eller RAM-fejl.'
        }
        $since = (Get-Date).AddDays(-30)
        $bug  = @(Get-WinEvent -FilterHashtable @{ LogName = 'System'; Id = 1001; ProviderName = 'Microsoft-Windows-WER-SystemErrorReporting'; StartTime = $since } -ErrorAction SilentlyContinue)
        $kp   = @(Get-WinEvent -FilterHashtable @{ LogName = 'System'; Id = 41; ProviderName = 'Microsoft-Windows-Kernel-Power'; StartTime = $since } -ErrorAction SilentlyContinue)
        $whea = @(Get-WinEvent -FilterHashtable @{ LogName = 'System'; ProviderName = 'Microsoft-Windows-WHEA-Logger'; StartTime = $since } -ErrorAction SilentlyContinue)
        $seen = @{}
        "== Blå skærme de sidste 30 dage: $($bug.Count)"
        foreach ($e in $bug | Select-Object -First 15) {
            $c = ([regex]::Match($e.Message, '0x[0-9a-fA-F]{8}')).Value.ToLower()
            $seen[$c] = 1 + [int]$seen[$c]
            "  $($e.TimeCreated.ToString('yyyy-MM-dd HH:mm'))  $c  $($codes[$c])"
        }
        "== Uventede nedlukninger (strøm forsvandt eller pc'en frøs): $($kp.Count)"
        $power = 0
        foreach ($e in $kp | Select-Object -First 15) {
            $bc = try { [int64]$e.Properties[0].Value } catch { 0 }
            if ($bc -eq 0) { $power++ }
            "  $($e.TimeCreated.ToString('yyyy-MM-dd HH:mm'))  " + $(if ($bc -eq 0) { 'ingen blå skærm: strømtab, frysning eller hård genstart' } else { 'efter blå skærm 0x{0:x8}' -f $bc })
        }
        "== Hardwarefejl (WHEA): $($whea.Count)"
        $whea | Select-Object -First 5 | ForEach-Object { "  $($_.TimeCreated.ToString('yyyy-MM-dd HH:mm'))  $((($_.Message -split "`n")[0]).Trim())" }
        $dumps = @(Get-ChildItem "$env:windir\Minidump" -Filter *.dmp -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending)
        "== Crash-dumps: $($dumps.Count)" + $(if ($dumps) { " (nyeste $($dumps[0].Name), $($dumps[0].LastWriteTime.ToString('yyyy-MM-dd HH:mm')))" })
        ''
        '== Vurdering'
        if (-not $bug.Count -and -not $kp.Count -and -not $whea.Count) { 'Ingen nedbrud eller hardwarefejl de sidste 30 dage.' }
        if ($whea.Count -or $seen['0x00000124'] -or $seen['0x00000101']) { '* Hardwarefejl. Er CPU, RAM eller GPU overclocket, så gå tilbage til standard og test igen. Tjek også temperaturer og strømforsyning.' }
        if ($seen['0x00000116'] -or $seen['0x00000117']) { '* Grafikdriveren er den sandsynlige årsag. Installér den nyeste anbefalede driver (Fleetdeck: installér pakken AMD Software).' }
        if ($power -gt 0) { "* $power nedlukning(er) uden blå skærm. Det tyder på strømtab, en frysning eller at nogen holdt tænd/sluk-knappen inde. Ved overclock: test med standardindstillinger." }
        if ($seen['0x000000ef'] -or $seen['0x0000003b']) { '* Kør værktøjet "Reparér Windows" (DISM + SFC).' }
        """;

    public const string RepairWindows = """
        '== 1/2 DISM: reparerer Windows-komponentlageret (kan tage 10-30 minutter) ...'
        $d = Dism.exe /Online /Cleanup-Image /RestoreHealth /NoRestart 2>&1
        $d | Where-Object { $_ -match '\S' -and $_ -notmatch '^\[=*' } | Select-Object -Last 6
        '== 2/2 SFC: kontrollerer systemfiler ...'
        $s = (sfc.exe /scannow 2>&1 | Out-String) -replace "`0", ''
        ($s -split "`r?`n") | Where-Object { $_ -match '\S' -and $_ -notmatch '%' } | Select-Object -Last 4
        if ($s -match 'did not find any integrity violations|fandt ingen integritetsovertrædelser|ikke fundet nogen') { 'RESULTAT: Windows er i orden.' }
        elseif ($s -match 'successfully repaired|reparerede dem|repareret') { 'RESULTAT: Fejl fundet og repareret. Genstart pc''en.' }
        else { 'RESULTAT: Se detaljer i C:\Windows\Logs\CBS\CBS.log.' }
        """;

    public const string NetworkReset = """
        ipconfig /flushdns | Out-Null; 'DNS-cache tømt'
        netsh winsock reset | Out-Null; 'Winsock nulstillet'
        netsh int ip reset | Out-Null; 'TCP/IP nulstillet'
        Get-NetAdapter -Physical | Where-Object Status -eq 'Up' | ForEach-Object { "Netkort: $($_.InterfaceDescription) $($_.LinkSpeed)" }
        'RESULTAT: Genstart pc''en, så nulstillingen træder i kraft.'
        """;

    // LibreHardwareMonitor (med PawnIO-driveren) som skjult baggrundsproces fra opstart – så agenten kan læse CPU-, GPU- og disktemperaturer via WMI
    public const string TempsEnable = FindWinget + """
        $dest = "$env:ProgramFiles\Fleetdeck\LibreHardwareMonitor"
        $exe = Join-Path $dest 'LibreHardwareMonitor.exe'
        if (-not (Test-Path $exe)) {
            $src = @(Get-ChildItem 'C:\Users\*\AppData\Local\Microsoft\WinGet\Packages\LibreHardwareMonitor.LibreHardwareMonitor*', "$env:ProgramFiles\WinGet\Packages\LibreHardwareMonitor.LibreHardwareMonitor*", "$env:ProgramFiles\LibreHardwareMonitor" -Directory -ErrorAction SilentlyContinue |
                Where-Object { Test-Path (Join-Path $_.FullName 'LibreHardwareMonitor.exe') }) | Select-Object -First 1
            if (-not $src -and $wg) {
                'Installerer LibreHardwareMonitor med winget ...'
                & $wg install --id LibreHardwareMonitor.LibreHardwareMonitor --exact --silent --scope machine --accept-package-agreements --accept-source-agreements --disable-interactivity 2>&1 | Select-Object -Last 2
                $src = Get-ChildItem "$env:ProgramFiles\WinGet\Packages\LibreHardwareMonitor.LibreHardwareMonitor*" -Directory -ErrorAction SilentlyContinue | Select-Object -First 1
            }
            if (-not $src) { 'RESULTAT: LibreHardwareMonitor kunne ikke findes eller installeres. Installér den fra Pakker og spil, og prøv igen.'; exit 1 }
            New-Item -ItemType Directory -Force $dest | Out-Null
            robocopy $src.FullName $dest /E /NFL /NDL /NJH /NJS /NP | Out-Null
            "LibreHardwareMonitor lagt i $dest"
        }
        # PawnIO er driveren, som giver adgang til CPU-sensorerne (nyere LibreHardwareMonitor bruger den i stedet for WinRing0)
        if (-not (Get-Service PawnIO -ErrorAction SilentlyContinue) -and $wg) {
            'Installerer PawnIO-driveren ...'
            & $wg install --id namazso.PawnIO --exact --silent --accept-package-agreements --accept-source-agreements --disable-interactivity 2>&1 | Select-Object -Last 2
        }
        # Start minimeret uden vindue. Den lokale webserver (port 8085) er den måde, agenten læser temperaturerne på –
        # nyere LibreHardwareMonitor har ikke længere WMI. Windows Firewall blokerer den udefra, så den kun nås fra pc'en selv.
        @'
        <?xml version="1.0" encoding="utf-8"?>
        <configuration>
          <appSettings>
            <add key="startMinMenuItem" value="true" />
            <add key="minTrayMenuItem" value="true" />
            <add key="minCloseMenuItem" value="true" />
            <add key="runWebServerMenuItem" value="true" />
            <add key="listenerPort" value="8085" />
            <add key="startupMenuItem" value="false" />
          </appSettings>
        </configuration>
        '@ | Set-Content -Path (Join-Path $dest 'LibreHardwareMonitor.config') -Encoding UTF8
        # Genstart den, så den nye opsætning gælder
        Get-Process LibreHardwareMonitor -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$dest*" } | Stop-Process -Force -ErrorAction SilentlyContinue
        $name = 'Fleetdeck Temperaturmaaling'
        $action = New-ScheduledTaskAction -Execute $exe -WorkingDirectory $dest
        $trigger = New-ScheduledTaskTrigger -AtStartup
        $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
        $set = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable
        Register-ScheduledTask -TaskName $name -Action $action -Trigger $trigger -Principal $principal -Settings $set -Force | Out-Null
        "Planlagt opgave '$name' kører LibreHardwareMonitor skjult fra opstart."
        if (-not (Get-Process LibreHardwareMonitor -ErrorAction SilentlyContinue)) { Start-ScheduledTask -TaskName $name }
        # Vent på målinger fra den lokale webserver
        $found = @()
        for ($i = 0; $i -lt 20 -and $found.Count -eq 0; $i++) {
            Start-Sleep 3
            try {
                $j = Invoke-RestMethod -Uri 'http://127.0.0.1:8085/data.json' -TimeoutSec 3 -ErrorAction Stop
                $stack = New-Object System.Collections.Stack; $stack.Push($j)
                while ($stack.Count) { $n = $stack.Pop(); if ($n.SensorId -like '*/temperature/*') { $found += "  $($n.Text): $($n.Value)" }; foreach ($c in @($n.Children)) { if ($c) { $stack.Push($c) } } }
            } catch { }
        }
        if ($found.Count -eq 0) { 'RESULTAT: LibreHardwareMonitor kører, men der kom ingen temperaturer endnu. Genstart pc''en, og kør "Tjek nu" under Helbred.'; exit 1 }
        "Temperaturer fundet ($($found.Count) sensorer):"
        $found | Select-Object -First 12
        'RESULTAT: Temperaturmåling er slået til. Tallene vises under Helbred ved næste helbredstjek.'
        """;

    public const string WingetRepair = FindWinget + """
        if (-not $wg) { 'winget findes ikke. Installér "App Installer" fra Microsoft Store på pc''en.'; exit 1 }
        "winget: $wg"
        & $wg source reset --force --disable-interactivity 2>&1 | Select-Object -Last 2
        & $wg source update --disable-interactivity 2>&1 | Select-Object -Last 3
        & $wg --version
        'RESULTAT: winget-kilderne er nulstillet og opdateret.'
        """;

    // Reparationerne herunder er sammenholdt med ChrisTitusTech/winutil (MIT): Invoke-WPFFixesUpdate.ps1, Install-WinUtilWinget.ps1 og tweaks.json (WPFTweaksDiskCleanup)

    // Nulstiller Windows Update's lokale filer. Politikker, tjenesternes starttyper og sikkerhedsindstillinger røres ikke.
    public const string WindowsUpdateReset = """
        $svcs = 'wuauserv', 'bits', 'cryptsvc', 'appidsvc'
        'Stopper Windows Update-tjenesterne ...'
        foreach ($s in $svcs) { Stop-Service -Name $s -Force -ErrorAction SilentlyContinue }
        $failed = 0
        try {
            Remove-Item "$env:ProgramData\Microsoft\Network\Downloader\qmgr*.dat" -Force -ErrorAction SilentlyContinue
            foreach ($dir in "$env:windir\SoftwareDistribution", "$env:windir\System32\catroot2") {
                $bak = "$dir.fleetdeck-bak"
                if (Test-Path -LiteralPath $bak) { Remove-Item -LiteralPath $bak -Recurse -Force -ErrorAction SilentlyContinue }
                if (-not (Test-Path -LiteralPath $dir)) { continue }
                try { Rename-Item -LiteralPath $dir -NewName (Split-Path $bak -Leaf) -ErrorAction Stop; "Nulstillet: $dir" }
                catch { $failed++; "Kunne ikke nulstille $dir`: $($_.Exception.Message)" }
            }
        } finally {
            'Starter tjenesterne igen ...'
            foreach ($s in $svcs) { Start-Service -Name $s -ErrorAction SilentlyContinue }
        }
        foreach ($s in 'wuauserv', 'bits', 'cryptsvc') { "$s`: $((Get-Service -Name $s -ErrorAction SilentlyContinue).Status)" }
        if ($failed) { 'RESULTAT: Ikke alt kunne nulstilles. Genstart pc''en, og kør værktøjet igen.'; exit 1 }
        'RESULTAT: Windows Update er nulstillet. Listen over tidligere opdateringer i Indstillinger starter forfra; de installerede opdateringer er der stadig.'
        """;

    // Microsofts egen vej til at installere winget igen for alle brugere
    public const string WingetReinstall = """
        $ProgressPreference = 'SilentlyContinue'
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        try {
            'Henter Microsofts winget-modul fra PowerShell Gallery ...'
            Install-PackageProvider -Name NuGet -MinimumVersion 2.8.5.201 -Force -Scope AllUsers -ErrorAction Stop | Out-Null
            Install-Module -Name Microsoft.WinGet.Client -Repository PSGallery -Force -Scope AllUsers -ErrorAction Stop
            Import-Module Microsoft.WinGet.Client -Force -ErrorAction Stop
            'Installerer winget igen for alle brugere (kan tage nogle minutter) ...'
            Repair-WinGetPackageManager -AllUsers -Latest -Force -ErrorAction Stop | Out-Null
        } catch { "RESULTAT: winget kunne ikke installeres igen: $($_.Exception.Message)"; exit 1 }

        """ + FindWinget + """
        if (-not $wg) { 'RESULTAT: winget blev ikke fundet efter installationen. Genstart pc''en, og prøv igen.'; exit 1 }
        "RESULTAT: winget $(& $wg --version) er installeret."
        """;

    // Windows' Diskoprydning med en fast liste. Overførsler, papirkurven, Windows.old og gamle drivere er med vilje ikke på listen.
    public const string DiskCleanupFull = """
        function Free { [math]::Round((Get-PSDrive C).Free / 1GB, 2) }
        $before = Free
        $root = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches'
        $flag = 'StateFlags0777'
        $safe = 'Temporary Files', 'Temporary Setup Files', 'Thumbnail Cache', 'Update Cleanup', 'Windows Error Reporting Files', 'Delivery Optimization Files',
                'Internet Cache Files', 'Downloaded Program Files', 'Setup Log Files', 'System error memory dump files', 'System error minidump files',
                'D3D Shader Cache', 'Old ChkDsk Files', 'Temporary Sync Files', 'Upgrade Discarded Files', 'Windows Upgrade Log Files'
        try {
            foreach ($n in $safe) { if (Test-Path -LiteralPath "$root\$n") { Set-ItemProperty -LiteralPath "$root\$n" -Name $flag -Value 2 -Type DWord } }
            'Kører Diskoprydning (kan tage op til en halv time) ...'
            $p = Start-Process -FilePath "$env:windir\System32\cleanmgr.exe" -ArgumentList '/sagerun:777' -WindowStyle Hidden -PassThru
            if (-not $p.WaitForExit(45 * 60 * 1000)) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue; 'Diskoprydning tog for lang tid og blev stoppet.' }
        } finally {
            foreach ($n in $safe) { Remove-ItemProperty -LiteralPath "$root\$n" -Name $flag -ErrorAction SilentlyContinue }
        }
        $after = Free
        "RESULTAT: Fri plads på C: før $before GB, efter $after GB. Frigjort: $([math]::Round($after - $before, 2)) GB."
        'Overførsler, papirkurven og den tidligere Windows-version (Windows.old) er ikke rørt.'
        """;

    public const string EventlogSummary = """
        $since = (Get-Date).AddDays(-1)
        foreach ($log in 'System', 'Application') {
            $ev = @(Get-WinEvent -FilterHashtable @{ LogName = $log; Level = 1, 2; StartTime = $since } -MaxEvents 3000 -ErrorAction SilentlyContinue)
            "== $log-loggen: $($ev.Count) fejl de sidste 24 timer"
            $ev | Group-Object ProviderName | Sort-Object Count -Descending | Select-Object -First 10 | ForEach-Object {
                $m = (($_.Group[0].Message -split "`n")[0]).Trim()
                if ($m.Length -gt 150) { $m = $m.Substring(0, 150) + '...' }
                "  {0,4} x {1}: {2}" -f $_.Count, $_.Name, $m
            }
        }
        """;

    public const string PxeReinstall = """
        $fw = (bcdedit.exe /enum firmware | Out-String)
        $blocks = $fw -split '(?:\r?\n){2,}'
        $pxe = $blocks | Where-Object { $_ -match 'IPv4|PXE|Network|Netv|LAN|Ethernet' -and $_ -notmatch 'IPv6' -and $_ -match '\{[0-9a-fA-F-]{36}\}' } | Select-Object -First 1
        if (-not $pxe) { 'Fandt ingen netværksboot i UEFI. Slå "Network Stack" / PXE til i BIOS, og prøv igen.'; exit 1 }
        $id = ([regex]::Match($pxe, '\{[0-9a-fA-F-]{36}\}')).Value
        $desc = (($pxe -split "`n") | Where-Object { $_ -match 'IPv4|PXE|Network|Netv|LAN|Ethernet' } | Select-Object -First 1).Trim()
        bcdedit.exe /set '{fwbootmgr}' bootsequence $id | Out-Null
        if ($LASTEXITCODE) { 'bcdedit kunne ikke sætte netværksboot.'; exit 1 }
        "Næste opstart sker én gang fra: $desc $id"
        shutdown.exe /r /t 120 /c "Fleetdeck: pc'en genstarter om 2 minutter og geninstalleres fra netværket. Gem dit arbejde." /d p:4:1
        'RESULTAT: Pc''en genstarter om 2 minutter og starter Windows-installationen fra netværket.'
        """;

    // ------------------------------------------------------------------ katalog
    public static readonly IReadOnlyList<Fix> All =
    [
        new("defender_enable", "Slå Microsoft Defender til", "Fjerner politikker, der holder Defender slukket, og slår tjenester, der er slået fra, til igen. Starter realtidsbeskyttelse og henter nye definitioner.",
            Risk.Confirm, "Sikkerhed", "script", Script: DefenderEnable, TimeoutMin: 15, Button: "Slå Defender til"),
        new("defender_signatures", "Opdatér virusdefinitioner", "Henter de nyeste virusdefinitioner til Microsoft Defender.",
            Risk.Safe, "Sikkerhed", "script", Script: DefenderSignatures, TimeoutMin: 15, AutoAllowed: true, Button: "Hent definitioner"),
        new("defender_scan", "Hurtig virusscanning", "Opdaterer definitioner og kører en hurtig scanning med Defender.",
            Risk.Safe, "Sikkerhed", "script", Script: DefenderScan, TimeoutMin: 60, AutoAllowed: true, Button: "Scan nu"),
        new("autostart_repair", "Sikr at Fleetdeck starter selv", "Automatisk start uden login, genstart ved fejl og en vagthund, der starter tjenesten, hvis den står stille.",
            Risk.Safe, "Fleetdeck", "script", Script: AutostartRepair, TimeoutMin: 5, AutoAllowed: true),
        new("firewall_enable", "Slå firewall til", "Slår Windows Firewall til for alle netværksprofiler.",
            Risk.Confirm, "Sikkerhed", "script", Script: FirewallEnable, TimeoutMin: 5, Button: "Slå firewall til"),
        new("disk_tools_linux", "Installér diskhelbred", "Installerer smartmontools og nvme-cli, så diskens helbred, slid og temperatur kan læses.",
            Risk.Safe, "Diske", "script", Script: DiskToolsLinux, TimeoutMin: 10, Button: "Installér værktøjet"),
        new("firewall_enable_mac", "Slå firewall til (Mac)", "Slår macOS' firewall til (Systemindstillinger → Netværk → Firewall).",
            Risk.Confirm, "Sikkerhed", "script", Script: FirewallEnableMac, TimeoutMin: 5, Button: "Slå firewall til"),
        new("windows_update", "Installér Windows-opdateringer", "Henter og installerer alle ventende Windows-opdateringer.",
            Risk.Confirm, "Opdateringer", "job", JobType: "windows_update", TimeoutMin: 180, Button: "Installér opdateringer"),
        new("system_update", "Installér systemopdateringer", "Installerer ventende opdateringer til styresystemet og programmerne (apt, dnf, Homebrew m.fl.).",
            Risk.Confirm, "Opdateringer", "job", JobType: "windows_update", TimeoutMin: 180, Tool: false, Button: "Installér opdateringer"),
        new("upgrade_all", "Opdatér alle programmer", "Opdaterer alle programmer med winget (undtagelserne i profilen respekteres).",
            Risk.Safe, "Opdateringer", "job", JobType: "upgrade", TimeoutMin: 180, AutoAllowed: true, Button: "Opdatér programmerne"),
        new("reboot_pending", "Genstart med nedtælling", "Genstarter pc'en om 5 minutter med en besked til brugeren.",
            Risk.Confirm, "Opdateringer", "job", JobType: "reboot", TimeoutMin: 10, Button: "Genstart med varsel"),
        new("disk_cleanup", "Ryd op på disken", "Sletter midlertidige filer, Windows Update-cache og gamle fejlrapporter, og rydder op i komponentlageret. Dine egne filer og papirkurven røres ikke.",
            Risk.Safe, "Vedligehold", "script", Script: DiskCleanup, TimeoutMin: 60, AutoAllowed: true, Button: "Ryd op"),
        new("crash_analysis", "Analysér nedbrud", "Læser blå skærme, uventede nedlukninger og hardwarefejl fra de sidste 30 dage og forklarer den sandsynlige årsag.",
            Risk.Safe, "Fejlfinding", "script", Script: CrashAnalysis, TimeoutMin: 10, AutoAllowed: true, Button: "Analysér nedbrud"),
        new("repair_windows", "Reparér Windows", "Kører DISM RestoreHealth og SFC, der finder og reparerer ødelagte systemfiler. Tager 15-45 minutter.",
            Risk.Confirm, "Fejlfinding", "script", Script: RepairWindows, TimeoutMin: 90),
        new("eventlog_summary", "Gennemgå hændelsesloggen", "Viser de hyppigste fejl i System- og Application-loggen de sidste 24 timer.",
            Risk.Safe, "Fejlfinding", "script", Script: EventlogSummary, TimeoutMin: 10, AutoAllowed: true),
        new("network_reset", "Nulstil netværk", "Tømmer DNS-cachen og nulstiller Winsock og TCP/IP. Kræver genstart.",
            Risk.Confirm, "Fejlfinding", "script", Script: NetworkReset, TimeoutMin: 10),
        new("temps_enable", "Slå temperaturmåling til", "Lægger LibreHardwareMonitor (og PawnIO-driveren) fast på pc'en og kører den skjult fra opstart, så CPU-, GPU- og disktemperaturer kan ses under Helbred.",
            Risk.Confirm, "Vedligehold", "script", Script: TempsEnable, TimeoutMin: 15),
        new("temps_enable_mac", "Slå temperaturmåling til (Mac)", "Slår Fleetdecks indbyggede temperaturmåling til på Mac'en: Apple silicon-sensorerne, på Intel SMC'en eller Apples powermetrics. Intet ekstra program.",
            Risk.Safe, "Vedligehold", "script", Script: TempsEnableMac, TimeoutMin: 5, AutoAllowed: true, Button: "Slå temperaturmåling til"),
        new("disk_tools_mac", "Installér diskhelbred (Mac)", "Installerer smartmontools med Homebrew, så diskens slid, temperatur og fejl kan læses (kræver Homebrew).",
            Risk.Safe, "Diske", "script", Script: DiskToolsMac, TimeoutMin: 15, Button: "Installér værktøjet"),
        new("winget_repair", "Reparér winget", "Nulstiller og opdaterer winget-kilderne, når programinstallationer fejler.",
            Risk.Safe, "Fejlfinding", "script", Script: WingetRepair, TimeoutMin: 15, AutoAllowed: true),
        new("winget_reinstall", "Geninstallér winget", "Installerer winget forfra for alle brugere med Microsofts eget modul, når \"Reparér winget\" ikke er nok.",
            Risk.Confirm, "Fejlfinding", "script", Script: WingetReinstall, TimeoutMin: 30),
        new("windows_update_reset", "Nulstil Windows Update", "Stopper tjenesterne, nulstiller Windows Updates cache og signaturer og starter dem igen. Brug det, når opdateringer bliver ved med at fejle.",
            Risk.Confirm, "Fejlfinding", "script", Script: WindowsUpdateReset, TimeoutMin: 20),
        new("disk_cleanup_full", "Grundig diskoprydning", "Kører Windows' Diskoprydning med en fast liste over sikre kategorier. Overførsler, papirkurven og Windows.old røres ikke.",
            Risk.Confirm, "Vedligehold", "script", Script: DiskCleanupFull, TimeoutMin: 60),
        new("retry_failed", "Prøv fejlede programmer igen", "Prøver igen med de programmer i profilen, som ikke kunne installeres.",
            Risk.Safe, "Programmer", "retry", TimeoutMin: 90, AutoAllowed: true, Button: "Prøv igen"),
        new("backup_now", "Tag backup nu", "Tager backup af filer og programindstillinger til Unraid (kræver at brugeren er logget ind).",
            Risk.Safe, "Backup", "winsetup", RunAs: "user_admin", Step: "Backup", TimeoutMin: 240, AutoAllowed: true, Button: "Tag backup nu"),
        new("prepare_reinstall", "Forbered geninstallation", "Gemmer baggrund og opsætning, drivere og en frisk backup på Unraid, så pc'en kan geninstalleres uden tab.",
            Risk.Confirm, "Geninstallation", "multi", TimeoutMin: 240),
        new("pxe_reinstall", "Geninstallér fra netværket", "Sætter pc'en til at boote én gang fra netværket og genstarter om 2 minutter. Windows installeres forfra, og alt lægges tilbage automatisk. Kræver en backup fra det sidste døgn.",
            Risk.Danger, "Geninstallation", "script", Script: PxeReinstall, TimeoutMin: 10),
        new("debloat_reapply", "Anvend debloat igen", "Anvender de debloat-punkter, der er vendt tilbage, igen – præcis som da de blev anvendt første gang.",
            Risk.Safe, "Vedligehold", "debloat", TimeoutMin: 30, Tool: false),
        new("service_start", "Start stoppede tjenester", "Starter de tjenester, alarmen nævner, igen. Andre tjenester røres ikke.",
            Risk.Confirm, "Fejlfinding", "services", TimeoutMin: 10, Tool: false, Button: "Start tjenesten"),
        new("wake", "Tænd med Wake-on-LAN", "Sender en magic packet til maskinen.",
            Risk.Safe, "Strøm", "wake", Tool: false, Button: "Tænd maskinen"),
    ];

    public static Fix? Get(string id) => All.FirstOrDefault(f => f.Id == id);

    /// <summary>"Tryk \"Knap\"": trinnet, der peger på rettelsens knap (samme tekst som knappen).</summary>
    private static string Press(string fixId, string rest, string? button = null) => $"Tryk \"{button ?? Get(fixId)?.Button ?? "Ret det"}\"{rest}";

    /// <summary>
    /// Forklaring, trin og anbefalet rettelse ud fra alarmens nøgle. Alle nøgler, Rules kan give, har et svar (se HelperTests).
    /// detail bruges kun til at vælge mellem rettelser (vigtige opdateringer: Windows Update eller programmer; tjenester: kun Unraid).
    /// </summary>
    public static Advice? AdviceFor(string key, string severity, string? platform = null, string? detail = null)
    {
        var a = Raw(key, severity, platform, detail);
        // en rettelse, der ikke kan køre på platformen (fx Defender-scripts på Linux), vises ikke – og trinnet med knappen heller ikke
        if (a?.FixId is not { } id || platform is null || Get(id) is not { } fix || HelperService.PlatformBlocker(fix, platform) is null) return a;
        var press = $"Tryk \"{a.Button ?? fix.Button}\"";
        var steps = a.Steps.Where(s => !s.StartsWith(press, StringComparison.Ordinal)).ToList();
        return a with { FixId = null, Button = null, Steps = steps.Count > 0 ? steps : ["Det kan ikke rettes fra Fleetdeck på denne platform."] };
    }

    private static Advice? Raw(string key, string severity, string? platform, string? detail)
    {
        var k = key.Split(':')[0];
        var windows = platform is null or Platforms.Windows;
        var mac = platform == Platforms.Mac;
        string? W(string fixId) => windows ? fixId : null;   // PowerShell-rettelser findes kun til Windows
        // fejlloggen: forklaringen pr. type står også på fanen Logs
        if (k == "syslog") return key switch
        {
            "syslog:crash" => new("Maskinen er gået ned (blå skærm eller kernepanik). Det skyldes oftest en driver, ustabil hardware, varme eller strøm.",
                W("crash_analysis"), windows
                    ? [Press("crash_analysis", " – Fleetdeck læser nedbruddene og forklarer den sandsynlige årsag."), "Opdatér den driver eller BIOS, analysen peger på.", "Sker det igen, så se stopkoden under fanen Logs."]
                    : ["Se hændelsen og fejlteksten under fanen Logs.", "Opdatér systemet og firmware/BIOS.", "Sker det igen, så tjek RAM og temperaturer."]),
            "syslog:whea" or "syslog:whea-corrected" => new("Processor, hukommelse eller bundkort har meldt en hardwarefejl. Det kan give nedbrud og ødelagte data.", null,
                ["Fjern overclocking/XMP i BIOS, hvis det er slået til.", "Opdatér BIOS.", "Kør en hukommelsestest (fx Windows Hukommelsesdiagnosticering eller memtest86).", "Bliver det ved, så få hardwaren undersøgt."]),
            "syslog:disk" or "syslog:disk-errors" => new("Disken har haft læse- eller skrivefejl. Det er ofte første tegn på, at den er ved at gå i stykker.", "backup_now",
                [Press("backup_now", " med det samme."), "Se diskens tilstand under Helbred → Diske.", "Planlæg at skifte disken, og tjek kablet, hvis det er en SATA-disk."]),
            "syslog:power" => new("Maskinen er slukket uden at lukke ned flere gange. Ét strømsvigt er normalt; flere tyder på strøm, varme eller nedbrud.", null,
                ["Tjek strømkabel, stikdåse og evt. UPS.", "Se under fanen Logs, om der er blå skærme lige før.", "Tjek temperaturerne under Helbred."]),
            "syslog:oom" => new("Systemet har lukket programmer, fordi hukommelsen slap op. Det, der blev lukket, holdt op med at virke.", null,
                ["Se under fanen Logs, hvilket program der blev lukket.", "Luk eller begræns det program, der bruger mest hukommelse (fx en hukommelsesgrænse på containeren).", "Giv maskinen mere RAM eller swap."]),
            _ => new("Fejlloggen har fundet noget, der bør ses på.", null, ["Se hændelsen under fanen Logs."]),
        };
        if (k == "net") return key switch
        {
            "net:speed" => new("Netkortet og kablet kan mere, end forbindelsen bruger. Det giver langsommere filkopiering og backup, men alt virker.", null,
                ["Skift kablet til et Cat5e- eller Cat6-kabel.", "Prøv en anden port på switchen eller routeren.", "Tjek at stikket sidder helt i i begge ender."]),
            "net:wifi" => new("Signalet er svagt, så forbindelsen bliver langsom og kan falde ud. Maskinen står fast, så det bliver ikke bedre af sig selv.", null,
                ["Flyt routeren eller et access point tættere på, eller væk fra metal og vægge.", "Brug et netværkskabel, hvis det kan lade sig gøre."]),
            _ when key.StartsWith("net:errors:", StringComparison.Ordinal) => new("Mange pakker på netværket kommer frem ødelagte. Det gør forbindelsen langsom og ustabil – oftest et dårligt kabel.", null,
                ["Skift netværkskablet.", "Prøv en anden port på switchen eller routeren.", "Hjælper det ikke, så prøv et andet netkort eller en anden switch."]),
            _ when key.StartsWith("net:flaps:", StringComparison.Ordinal) => new("Forbindelsen går ned og op igen mange gange om dagen. Hver gang mister maskinen netværket et øjeblik.", null,
                ["Tjek at stikket sidder fast i begge ender (et knækket clips giver løse stik).", "Skift kablet.", "Prøv en anden port på switchen eller routeren."]),
            _ => new("Netværket opfører sig ikke, som det skal.", null, ["Tjek kabel og port på switchen eller routeren."]),
        };
        if (k == "important")
        {
            if (!windows) return new(mac ? "Der er sikkerhedsrettelser eller nye versioner af programmer, som angribere ofte går efter." : "Der er sikkerhedsrettelser til systemet eller programmer, som angribere ofte går efter.",
                "system_update", [Press("system_update", " – maskinen henter og installerer dem."), "Genstart, hvis maskinen beder om det."]);
            var os = detail is { Length: > 0 } && HealthService.IsImportantOs(detail);
            return os
                ? new("Der er sikkerhedsrettelser fra Microsoft, som lukker huller, angribere kender.", "windows_update",
                    [Press("windows_update", " – Windows Update installerer dem nu."), "Genstart pc'en, når den beder om det.", "Programmer kan opdateres under fanen Opdateringer."])
                : new("Der er nye versioner af programmer, som angribere ofte går efter (browsere, pdf-læsere, fjernadgang …).", "upgrade_all",
                    [Press("upgrade_all", " – programmerne opdateres med winget."), "Skal et program blive på sin version, så lås det under fanen Opdateringer (\"Opdatér aldrig\")."]);
        }
        if (k == "services")
        {
            if (detail is { } d && d.StartsWith("Unraid-", StringComparison.Ordinal))
                return new("Unraid melder, at arrayet ikke kører, eller at paritetstjekket har fundet fejl. Så er data ikke beskyttet som normalt.", null,
                    ["Åbn Unraids egen webside → Main og se arrayets status.", "Start arrayet dér, eller kør et nyt paritetstjek.", "Bliver fejlene ved, så tjek diskenes SMART-data."]);
            var canStart = platform is null or Platforms.Windows or Platforms.Linux;
            return new("En tjeneste, der skal køre, er gået ned og ikke startet igen. Det, der bruger den, virker ikke imens.", canStart ? "service_start" : null,
                canStart
                    ? [Press("service_start", " – Fleetdeck starter kun de tjenester, der står her."), "Stopper den igen inden for en time, så se fejlen under fanen Logs.", "Skal den altid køre, så sæt den på \"Hold kørende\" i profilen."]
                    : ["Genstart tjenesten på maskinen.", "Stopper den igen, så se fejlen under fanen Logs."]);
        }
        if (!windows)
        {
            switch (k)
            {
                case "winupdate":
                    return new(mac ? "Sikkerhedsrettelser fra Apple og programopdateringer er ikke installeret." : "Sikkerhedsrettelser til styresystemet og programmerne er ikke installeret.",
                        "system_update", [Press("system_update", " nu, når det passer – eller lad profilens plan gøre det."), "Genstart, hvis maskinen beder om det."]);
                case "disk":
                    return new("Når disken er næsten fuld, fejler opdateringer, og programmer kan gå i stå.", null, mac
                        ? ["På Mac'en: Systemindstillinger → Generelt → Lagring viser, hvad der fylder.", "Tøm papirkurven, og slet eller flyt store filer.", "Er det stadig for lidt, så brug en større disk."]
                        : ["Find de største mapper (fx med du -xh / | sort -h | tail).", "Slet gamle logfiler, downloads og ubrugte containere/images.", "Er det stadig for lidt, så udvid disken."]);
                case "diskdata":
                    return mac ? new("macOS giver kun en enkel status for disken. Det er normalt.", null, ["Intet at gøre.", "Vil du ikke se den igen, så brug Ignorér."])
                        : new("Uden diskhelbred kan Fleetdeck ikke advare, før en SSD eller harddisk går i stykker.", "disk_tools_linux",
                            [Press("disk_tools_linux", " – smartmontools og nvme-cli installeres."), "Diskenes helbred vises under Helbred → Diske efter næste helbredstjek."]);
                case "encryption":
                    return mac ? new("Uden FileVault kan alle filer læses, hvis Mac'en bliver stjålet eller mistet.", null,
                            ["På Mac'en: Systemindstillinger → Anonymitet og sikkerhed → FileVault → Slå til.", "Vælg en gendannelsesnøgle (ikke iCloud).",
                             "Tryk bagefter \"Ny gendannelsesnøgle til Fleetdeck\" under Kryptering på maskinens side, så Fleetdeck gemmer den."])
                        : new("Uden diskkryptering kan alle filer læses, hvis den bærbare bliver stjålet eller mistet.", null,
                            ["Kryptering (LUKS) vælges ved installationen.", "Tag backup, og geninstallér med \"Kryptér disken\" slået til."]);
                case "escrow":
                    return new("Glemmer brugeren sin adgangskode, eller skal disken læses i en anden maskine, er gendannelsesnøglen den eneste vej ind.", null,
                        ["Tryk \"Ny gendannelsesnøgle til Fleetdeck\" under Kryptering på maskinens side.", "Brugeren skriver sin adgangskode på Mac'en, og den nye nøgle gemmes krypteret i Fleetdeck.",
                         "Den gamle nøgle holder op med at virke."]);
                case "firewall":
                    return mac ? new("Uden firewall kan andre på netværket nå tjenester på Mac'en.", "firewall_enable_mac", [Press("firewall_enable_mac", " – macOS' firewall slås til.")])
                        : new("Uden firewall kan andre på netværket nå tjenester på maskinen.", null,
                            ["Slå firewallen til (fx sudo ufw enable).", "Pas på ikke at spærre for SSH og Fleetdeck – tillad dem først (fx sudo ufw allow ssh)."]);
            }
        }
        return k switch
        {
            "offline" => new("Maskinen har ikke meldt sig. Den er slukket, uden netværk, eller agenten kører ikke – så længe kan Fleetdeck hverken overvåge eller opdatere den.", "wake",
                [Press("wake", " – Fleetdeck sender et Wake-on-LAN-signal."), "Tænder den ikke, så tjek strøm og netværkskabel ved maskinen.", "Kører den, men melder sig stadig ikke, så genstart den eller installér agenten igen fra Tilføj maskine."]),
            "reservedns" => new("Reserve-DNS'en (AdGuard på en anden server) svarer ikke. Alt virker lige nu, men går hovedserveren ned, mister iPads internettet.", null,
                ["Tjek at serveren med reserven er tændt og på netværket.", "Åbn Indstillinger → iPads → Reserve-DNS og se status og fejl.", "Alarmen forsvinder af sig selv, når reserven svarer igen."]),
            "sparecard" => new("Reservekortet er en kopi af systemet, så maskinen hurtigt kan køre igen, hvis hovedkortet går i stykker. Kopien er for gammel til at stole på.", null,
                ["Tjek at reservekortet sidder i maskinen.", "Åbn maskinens Overblik → Reservekort og se seneste fejl.", "Er kortet fuldt eller defekt, så skift det og vælg det igen under Reservekort."]),
            "debloat_drift" => new("Noget, Fleetdeck har fjernet eller slået fra, er kommet tilbage. Pc'en virker fint – det er typisk reklame-apps eller sporing, som Windows lægger ind igen ved en opdatering.",
                W("debloat_reapply"),
                [Press("debloat_reapply", " – Fleetdeck anvender punkterne igen (tager under et minut)."), "Kommer det igen og igen? Åbn fanen Debloat & privatliv, og sæt \"Hold håndhævet\" til \"Anvend igen\", så retter Fleetdeck det selv."]),
            "defender" when severity == "info" => new("Et andet antivirus beskytter maskinen, så Defender står på pause. Det er normalt.", null,
                ["Intet at gøre.", "Vil du ikke se den igen, så brug Ignorér."]),
            "defender" => new("Uden antivirus kan virus og ransomware køre frit på maskinen.", "defender_enable",
                [Press("defender_enable", " – Fleetdeck fjerner det, der holder Defender slukket, og slår den til igen."), "Kør bagefter en hurtig virusscanning under fanen Hjælper."]),
            "defsig" => new("Gamle virusdefinitioner kender ikke de nyeste trusler.", "defender_signatures",
                [Press("defender_signatures", " – de nyeste definitioner hentes."), "Bliver de ved med at være gamle, så kør \"Nulstil Windows Update\" under fanen Hjælper."]),
            "threats" => new("Defender har fundet noget skadeligt inden for den sidste måned.", "defender_scan",
                [Press("defender_scan", " – en hurtig scanning med nye definitioner."), "Se under fanen Jobs, hvad der er fundet og fjernet.", "Er der fundet noget alvorligt, så skift de adgangskoder, der er brugt på pc'en."]),
            "firewall" => new("Uden firewall kan andre på netværket nå tjenester på pc'en.", "firewall_enable",
                [Press("firewall_enable", " – Windows Firewall slås til for alle netværksprofiler.")]),
            "encryption" => new("Uden BitLocker kan alle filer læses, hvis den bærbare bliver stjålet eller mistet. Fleetdeck slår det ikke selv til.", null,
                ["Står BitLocker på pause: tryk \"Genoptag beskyttelse\" i Kontrolpanel → BitLocker-drevkryptering.",
                 "Ellers: Indstillinger → Beskyttelse af personlige oplysninger og sikkerhed → Enhedskryptering (eller BitLocker på Windows Pro).",
                 "Agenten gemmer gendannelsesnøglen i Fleetdeck af sig selv bagefter."]),
            "escrow" => new("Spørger pc'en om gendannelsesnøglen (fx efter en BIOS-opdatering eller et nyt bundkort), er den eneste vej ind.", null,
                ["Vent på næste helbredstjek – agenten sender nøglen af sig selv.",
                 "Har drevet ingen numerisk gendannelsesadgangskode, så tilføj en som administrator: manage-bde -protectors -add C: -RecoveryPassword."]),
            "tpm" => new("TPM'en er den chip, der låser BitLocker op og som Windows 11 kræver. Pc'en virker, men BitLocker beder om gendannelsesnøglen, og Windows 11-opdateringer kan blive afvist.", null,
                ["Har pc'en BitLocker, så find gendannelsesnøglen under Kryptering på maskinens side, før du ændrer noget.",
                 "Gå ind i BIOS/UEFI (typisk F2 eller Del ved opstart) og slå TPM, PTT (Intel) eller fTPM (AMD) til.",
                 "Står der \"ikke klar\": kør tpm.msc på pc'en og vælg \"Forbered TPM\"."]),
            "winupdate" => new("Sikkerhedsrettelser fra Microsoft er ikke installeret.", "windows_update",
                [Press("windows_update", " nu – eller lad profilens plan gøre det om natten."), "Genstart pc'en, når den beder om det."]),
            "kept" => new("Tjenesten står på listen \"Hold kørende\" i profilen – den skal altid køre.", null,
                ["Agenten starter den igen hvert minut – vent et par minutter.", "Bliver den ved med at stoppe, så se fejlen under fanen Logs eller i jobloggen.",
                 "Hjælper intet, så tag et gendannelsespunkt og gendan (overskriver intet)."]),
            "drive" => new("Programmer og genveje, der bruger netværksdrevet, virker ikke, før det er forbundet.", null,
                ["Tjek at serveren med mappen er tændt.", "Tjek under Indstillinger → Netværk og drev, at brugeren har adgang til mappen.", "Agenten prøver igen hvert 10. minut."]),
            "autostart" => new("Starter agenten ikke selv, er maskinen væk fra Fleetdeck efter en genstart, indtil nogen logger ind eller starter den.", W("autostart_repair"),
                windows ? [Press("autostart_repair", " – automatisk start, genstart ved fejl og en vagthund sættes op igen.")]
                    : ["Genstart maskinen, og se om agenten kommer tilbage.", "Gør den ikke, så installér agenten igen fra Tilføj maskine."]),
            "fulldisk" => new("Uden Fuld diskadgang må agenten ikke læse brugerens Bibliotek-mappe på Mac'en, så iPad-backup og lignende fejler med \"Operation not permitted\".", null,
                ["På Mac'en: Systemindstillinger → Anonymitet og sikkerhed → Fuld diskadgang. Står fleetdeck-agent der allerede, så fjern den med −.",
                 "Klik +, tryk Cmd+Shift+G, skriv /usr/local/fleetdeck/fleetdeck-agent, og slå den til.",
                 "Genstart agenten (Terminal: sudo launchctl kickstart -k system/com.fleetdeck.agent) eller Mac'en."]),
            "macsign" => new("Uden fast signatur ser macOS hver ny version af agenten som et nyt program og fjerner Fuld diskadgang ved hver opdatering.", null,
                ["Agenten prøver igen ved næste opdatering – vent.", "Bliver den ved, så se /var/log/fleetdeck-agent.log på Mac'en, eller installér agenten igen fra Tilføj maskine."]),
            "agentupdate" => new("En agent, der ikke kan opdatere sig selv, får hverken rettelser eller nye funktioner.", null,
                ["Agenten prøver igen hvert halve minut – vent lidt.", "Bliver alarmen ved, så genstart maskinen.", "Hjælper det ikke, så installér agenten igen fra Tilføj maskine."]),
            "reboot" => new("Opdateringer og drivere bliver først færdige efter en genstart. Lang oppetid med ventende genstart giver mærkelige fejl.", "reboot_pending",
                [Press("reboot_pending", " – brugeren får 5 minutters varsel til at gemme."), "Eller genstart selv, når det passer."]),
            "disk" => new("Når disken er næsten fuld, fejler opdateringer, og Windows bliver langsom.", "disk_cleanup",
                [Press("disk_cleanup", " – midlertidige filer og cache slettes. Dine egne filer og papirkurven røres ikke."),
                 "Er der stadig for lidt plads, så kør \"Grundig diskoprydning\" under fanen Hjælper.", "Hjælper det ikke, så flyt store filer væk, eller sæt en større disk i."]),
            "diskdata" => new("Fleetdeck kan ikke læse diskens helbred, så den kan ikke advare, før disken går i stykker.", null,
                ["Intet at gøre for USB- og RAID-diske – de viser ikke helbred.", "Vil du ikke se den igen, så brug Ignorér."]),
            "crash" => new("Nedbrud skyldes oftest en driver, ustabil overclock, varme eller strøm.", W("crash_analysis"),
                windows ? [Press("crash_analysis", " – Fleetdeck forklarer den sandsynlige årsag."), "Opdatér den driver eller BIOS, analysen peger på."]
                    : ["Se nedbruddet under fanen Logs.", "Opdatér systemet og firmware."]),
            "compliance" => new("Nogle af profilens programmer kunne ikke installeres.", "retry_failed",
                [Press("retry_failed", " – de fejlede programmer installeres igen."), "Fejler det stadig, så åbn jobbet under fanen Jobs og se forklaringen og forslagene."]),
            "backup" => new("Uden en frisk backup kan filer gå tabt ved diskfejl eller geninstallation.", "backup_now",
                [Press("backup_now", " – filer og indstillinger gemmes på Unraid (brugeren skal være logget ind)."), "Fejler den, så se jobbet under fanen Jobs."]),
            "smart" => new("Disken melder selv, at den svigter. Den kan gå i stykker når som helst, og data kan gå tabt.", "backup_now",
                [Press("backup_now", " med det samme."), "Skift disken hurtigst muligt – det kan ikke rettes med software."]),
            "wear" => new("SSD'en har brugt det meste af sin levetid. Den virker endnu, men kan begynde at fejle.", "backup_now",
                [Press("backup_now", ", og hold backuppen ajour."), "Planlæg at skifte disken."]),
            "sectors" => new("Disken har dårlige steder, den har måttet flytte data fra eller ikke kan læse. Stiger tallet, er disken ved at gå i stykker.", "backup_now",
                [Press("backup_now", " med det samme."), "Hold øje med tallet under Helbred → Diske.", "Stiger det igen, så skift disken."]),
            "battery" => new("Batteriet kan rumme meget mindre end som nyt, så den bærbare holder kortere tid uden strøm og kan slukke pludseligt.", null,
                ["Find modellen under Overblik, og bestil et nyt batteri.", "Skift det, når det passer – det kan ikke rettes med software."]),
            "dtemp" => new("Varme diske slides hurtigere og kan sætte hastigheden ned.", null,
                ["Tjek at der er luft omkring maskinen, og at blæserne kører.", "Rens støv ud af kabinettet.", "Sidder disken i et lukket kabinet, så giv den køling."]),
            "cputemp" when mac => new("Høj CPU-temperatur gør Mac'en langsom (den skruer ned for farten) og kan få den til at slukke.", null,
                ["Se i Aktivitetsmonitor, hvilket program der bruger CPU'en.", "Sørg for at luftindtag og -udtag ikke er dækket (fx af dyne eller pude), og rens støv ud.",
                 "Intel-Mac: nulstil SMC'en (sluk, og hold Skift-Ctrl-Alt og tænd/sluk-knappen i 10 sekunder).", "Bliver den ved, så få blæseren undersøgt."]),
            "cputemp" => new("Høj CPU-temperatur giver lavere ydelse og kan få pc'en til at slukke.", W("crash_analysis"),
                ["Tjek at blæserne kører, og rens støv ud.", "Skift kølepasta, hvis pc'en er nogle år gammel.", "Fjern overclock som test.",
                 .. (windows ? new[] { Press("crash_analysis", ", hvis pc'en også er slukket eller gået ned af sig selv.") } : [])]),
            "notemp" => mac
                ? new("Uden temperaturmåling kan Fleetdeck ikke advare, når Mac'en bliver for varm.", "temps_enable_mac",
                    [Press("temps_enable_mac", " – målingen slås til, og den første måling vises i jobbet."), "Eller på Mac'en: Fleetdeck → Funktioner → Temperaturmåling."])
                : windows
                ? new("Uden temperaturmåling kan Fleetdeck ikke advare, når pc'en bliver for varm.", "temps_enable",
                    [Press("temps_enable", " – LibreHardwareMonitor lægges på pc'en og startes skjult."), "Eller installér udvidelsen Temperaturmåling under maskinen."])
                : new("Uden temperaturmåling kan Fleetdeck ikke advare, når maskinen bliver for varm.", null,
                    ["Installér udvidelsen Sensorer (lm-sensors) under maskinen.", "Virtuelle maskiner har ofte ingen temperatursensor."]),
            _ => null,
        };
    }
}
