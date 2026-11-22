# Lib.ps1 - fælles funktioner til Windows Opsætning
# Bruges af WinSetup.ps1 (vinduet) og Run.ps1 (selve arbejdet).

$script:Root = $PSScriptRoot
$script:P = @{
    Apps       = Join-Path $PSScriptRoot 'apps.json'
    Config     = Join-Path $PSScriptRoot 'config.json'
    Installers = Join-Path $PSScriptRoot 'Installers'
    Drivers    = Join-Path $PSScriptRoot 'Drivers'
    Settings   = Join-Path $PSScriptRoot 'Settings'
    Logs       = Join-Path $PSScriptRoot 'Logs'
    Usb        = Join-Path $PSScriptRoot 'USB'
}
$script:Warnings = New-Object System.Collections.ArrayList

# ---------------------------------------------------------------- generelt
function Read-Json([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    $o = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
    return $o
}
function Save-Json($Object, [string]$Path) {
    $dir = Split-Path $Path
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
    $json = ConvertTo-Json -InputObject $Object -Depth 8
    [IO.File]::WriteAllText($Path, $json, (New-Object Text.UTF8Encoding $false))
}
# Første gang: kopiér eksempelfilen (config.example.json / apps.example.json), så der er noget at rette i.
# De rigtige filer er personlige og ligger kun på sharet (de er ikke i det delte repo).
function Use-Example([string]$Path) {
    $ex = $Path -replace '\.json$', '.example.json'
    if (-not (Test-Path -LiteralPath $Path) -and (Test-Path -LiteralPath $ex)) { Copy-Item -LiteralPath $ex $Path }
}
function Get-Config { Use-Example $P.Config; Read-Json $P.Config }
function Get-Apps   { Use-Example $P.Apps; @(Read-Json $P.Apps) }
function Get-SafeName([string]$n) { $n -replace '[\\/:*?"<>|]', '_' }
function Get-BackupDir {
    $c = Get-Config
    $n = if ($c.BackupName) { $c.BackupName } else { $env:COMPUTERNAME }
    Join-Path $Root "Backup\$n"
}

function Write-Step([string]$Text) { Write-Host ''; Write-Host "==> $Text" -ForegroundColor Cyan }
function Write-Ok([string]$Text)   { Write-Host "    OK  $Text" -ForegroundColor Green }
function Write-Skip([string]$Text) { Write-Host "    --  $Text" -ForegroundColor DarkGray }
function Write-Warn2([string]$Text) {
    Write-Host "    !!  $Text" -ForegroundColor Yellow
    [void]$script:Warnings.Add($Text)
}

function Test-Admin {
    ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

# Et drevbogstav (Z:\...) findes ikke i en administrator-session, så brug UNC-stien.
function Convert-ToUnc([string]$Path) {
    if ($Path -match '^([A-Za-z]):\\(.*)$') {
        $letter = $Matches[1]; $rest = $Matches[2]
        $d = Get-PSDrive -Name $letter -ErrorAction SilentlyContinue
        if ($d -and $d.DisplayRoot -like '\\*') { return (Join-Path $d.DisplayRoot $rest) }
    }
    $Path
}

function Start-Elevated([string]$Script, [string]$Arguments = '', [switch]$Sta, [switch]$Hidden) {
    $s = Convert-ToUnc $Script
    $a = '-NoProfile -ExecutionPolicy Bypass '
    if ($Sta)    { $a += '-STA ' }
    if ($Hidden) { $a += '-WindowStyle Hidden ' }
    $a += "-File `"$s`" $Arguments"
    Start-Process powershell.exe -Verb RunAs -ArgumentList $a
}

# Kør et program som almindelig bruger (uden administrator). Nogle installere, fx Spotify,
# nægter at køre som administrator.
function Invoke-NonElevated([string]$Exe, [string]$Arguments, [int]$TimeoutMin = 30) {
    $name = 'WinSetup-User-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
    $act  = New-ScheduledTaskAction -Execute $Exe -Argument $Arguments
    $who  = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    $prin = New-ScheduledTaskPrincipal -UserId $who -LogonType Interactive -RunLevel Limited
    Register-ScheduledTask -TaskName $name -Action $act -Principal $prin -Force | Out-Null
    Start-ScheduledTask -TaskName $name
    Start-Sleep 3
    $deadline = (Get-Date).AddMinutes($TimeoutMin)
    while ((Get-ScheduledTask -TaskName $name).State -in 'Running', 'Queued' -and (Get-Date) -lt $deadline) { Start-Sleep 2 }
    $code = (Get-ScheduledTaskInfo -TaskName $name).LastTaskResult
    Unregister-ScheduledTask -TaskName $name -Confirm:$false
    $code
}

function Invoke-Robo([string]$Src, [string]$Dst, [switch]$Mirror, [switch]$SkipOlder) {
    $a = @($Src, $Dst)
    $a += if ($Mirror) { '/MIR' } else { '/E' }
    if ($SkipOlder) { $a += '/XO' }
    $a += '/R:1', '/W:1', '/XJ', '/MT:16', '/NP', '/NFL', '/NDL', '/NJH', '/NJS', '/XF', 'desktop.ini'
    robocopy @a | Out-Null
    $LASTEXITCODE -lt 8
}

# Øjebliksbillede (VSS) af et drev, så filer som åbne programmer holder låst (fx Thunderbird) kan kopieres sikkert.
# Kræver administrator. Returnerer $null, hvis det ikke kan lade sig gøre – så kopieres direkte som før.
function New-Snapshot([string]$Drive = "$env:SystemDrive\") {
    if (-not (Test-Admin)) { return $null }
    try {
        $r = (Get-WmiObject -List Win32_ShadowCopy -ErrorAction Stop).Create($Drive, 'ClientAccessible')
        if ($r.ReturnValue -ne 0) { return $null }
        $sc = Get-WmiObject Win32_ShadowCopy -Filter "ID='$($r.ShadowID)'" -ErrorAction Stop
        $link = Join-Path $env:SystemDrive "WinSetupSnap_$PID"
        if (Test-Path $link) { cmd /c rmdir "$link" 2>$null | Out-Null }
        cmd /c mklink /d "$link" "$($sc.DeviceObject)\" 2>$null | Out-Null
        if (-not (Test-Path $link)) { $sc.Delete(); return $null }
        [pscustomobject]@{ Shadow = $sc; Link = $link; Drive = $Drive.TrimEnd('\') }
    } catch { $null }
}
function Remove-Snapshot($Snap) {
    if (-not $Snap) { return }
    cmd /c rmdir "$($Snap.Link)" 2>$null | Out-Null
    try { $Snap.Shadow.Delete() } catch { }
}
# Sti i øjebliksbilledet for en sti på drevet (andre drev kopieres direkte)
function Get-SnapPath($Snap, [string]$Path) {
    if ($Snap -and $Path -like "$($Snap.Drive)\*") { Join-Path $Snap.Link $Path.Substring($Snap.Drive.Length + 1) } else { $Path }
}

function Get-UserFolder([string]$Name) {
    $map = @{ Desktop = 'Desktop'; Documents = 'Personal'; Pictures = 'My Pictures'; Videos = 'My Video'
              Music = 'My Music'; Downloads = '{374DE290-123F-4565-9164-39C4925E467B}' }
    $v = (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders').($map[$Name])
    if (-not $v) { $v = Join-Path $env:USERPROFILE $Name }
    [Environment]::ExpandEnvironmentVariables($v)
}

# ---------------------------------------------------------------- winget
function Initialize-Winget {
    if (Get-Command winget.exe -ErrorAction SilentlyContinue) { return $true }
    Write-Host '    Gør winget klar (App Installer)...'
    try { Add-AppxPackage -RegisterByFamilyName -MainPackage Microsoft.DesktopAppInstaller_8wekyb3d8bbwe -ErrorAction Stop } catch {}
    for ($i = 0; $i -lt 30 -and -not (Get-Command winget.exe -ErrorAction SilentlyContinue); $i++) { Start-Sleep 2 }
    if (-not (Get-Command winget.exe -ErrorAction SilentlyContinue)) {
        Write-Host '    Henter App Installer fra Microsoft...'
        $f = Join-Path $env:TEMP 'AppInstaller.msixbundle'
        try {
            $ProgressPreference = 'SilentlyContinue'
            Invoke-WebRequest 'https://aka.ms/getwinget' -OutFile $f -UseBasicParsing
            Add-AppxPackage $f -ErrorAction Stop
        } catch { Write-Warn2 "Kunne ikke installere winget: $_" }
    }
    $ok = [bool](Get-Command winget.exe -ErrorAction SilentlyContinue)
    if ($ok) { winget.exe source update --disable-interactivity | Out-Null }
    $ok
}

# 0 = ok, 0x8A15002B = ingen nyere version, 0x8A150061 = allerede installeret, 0x8A150109 = genstart kræves
$script:WingetOk = @(0, -1978335189, -1978335135, -1978334967)

function Install-Apps {
    $apps = @(Get-Apps | Where-Object { $_.Enabled })
    Write-Step "Installerer $($apps.Count) programmer"
    $haveWinget = Initialize-Winget
    $n = 0
    foreach ($a in $apps) {
        $n++
        Write-Host ''
        Write-Host ("[{0}/{1}] {2}" -f $n, $apps.Count, $a.Name) -ForegroundColor White

        if ($a.Type -eq 'winget' -or $a.Type -eq 'msstore') {
            if (-not $haveWinget) { Write-Warn2 "$($a.Name): winget mangler"; continue }
            $wa = @('install', '--id', $a.Id, '--exact', '--source', $a.Type, '--silent',
                    '--accept-package-agreements', '--accept-source-agreements', '--disable-interactivity')
            if ($a.Args) { $wa += @($a.Args -split '\s+' | Where-Object { $_ }) }
            if ($a.NotAdmin) {
                $exe  = (Get-Command winget.exe).Source
                $code = Invoke-NonElevated $exe (($wa | ForEach-Object { if ($_ -match '\s') { "`"$_`"" } else { $_ } }) -join ' ')
            } else {
                & winget.exe @wa
                $code = $LASTEXITCODE
            }
            if ($code -in $WingetOk) { Write-Ok $a.Name }
            else { Write-Warn2 ("{0}: winget fejlkode {1} (0x{1:X8})" -f $a.Name, $code) }
            continue
        }

        if ($a.Type -eq 'file') {
            $src = if ($a.Id) { Join-Path $P.Installers $a.Id } else { $null }
            if (-not $src -or -not (Test-Path -LiteralPath $src)) { Write-Warn2 "$($a.Name): installationsfil mangler i Installers-mappen"; continue }
            if ($a.Id -match '[\\/]') {
                $run = $src                       # ligger i en undermappe: kør direkte fra serveren
            } else {
                $tmpDir = Join-Path $env:TEMP 'WinSetup'
                New-Item -ItemType Directory -Force $tmpDir | Out-Null
                $run = Join-Path $tmpDir $a.Id
                Copy-Item -LiteralPath $src $run -Force
            }
            if ($run -like '*.msi') {
                $p = Start-Process msiexec.exe -ArgumentList "/i `"$run`" $($a.Args)" -Wait -PassThru
            } else {
                $sp = @{ FilePath = $run; Wait = $true; PassThru = $true }
                if ($a.Args) { $sp.ArgumentList = $a.Args }
                $p = Start-Process @sp
            }
            if ($p.ExitCode -in 0, 3010, 1641) { Write-Ok $a.Name } else { Write-Warn2 "$($a.Name): afsluttede med kode $($p.ExitCode)" }
            continue
        }
        Write-Warn2 "$($a.Name): ukendt type '$($a.Type)'"
    }
}

# ---------------------------------------------------------------- drivere
function Export-Drivers {
    Write-Step 'Gemmer drivere fra denne pc'
    $tmp = Join-Path $env:TEMP 'WinSetupDrivers'
    if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
    New-Item -ItemType Directory $tmp | Out-Null
    pnputil.exe /export-driver * $tmp | Out-Null
    if (Test-Path $P.Drivers) { Remove-Item $P.Drivers -Recurse -Force }
    if (Invoke-Robo $tmp $P.Drivers -Mirror) {
        Write-Ok ("{0} drivere gemt" -f (Get-ChildItem $P.Drivers -Directory).Count)
    } else { Write-Warn2 'Kunne ikke kopiere drivere til serveren' }
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}

function Install-Drivers {
    Write-Step 'Installerer drivere'
    if (-not (Test-Path $P.Drivers) -or -not (Get-ChildItem $P.Drivers -Directory)) { Write-Warn2 'Der er ingen gemte drivere endnu'; return }
    $tmp = Join-Path $env:TEMP 'WinSetupDrivers'
    Invoke-Robo $P.Drivers $tmp -Mirror | Out-Null
    pnputil.exe /add-driver "$tmp\*.inf" /subdirs /install | Out-Null
    Write-Ok 'Drivere installeret (nogle kræver genstart)'
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}

# ---------------------------------------------------------------- baggrund og opsætning
function Get-AppearanceMeta {
    $m = Read-Json (Join-Path $P.Settings 'appearance.json')
    if (-not $m) { $m = [pscustomobject]@{} }
    $m
}
function Set-AppearanceMeta([hashtable]$Changes) {
    $m = Get-AppearanceMeta
    foreach ($k in $Changes.Keys) { $m | Add-Member -NotePropertyName $k -NotePropertyValue $Changes[$k] -Force }
    Save-Json $m (Join-Path $P.Settings 'appearance.json')
}

function Set-SavedWallpaper([string]$File, [string]$Style, [string]$Tile) {
    New-Item -ItemType Directory -Force $P.Settings | Out-Null
    Get-ChildItem $P.Settings -Filter 'wallpaper.*' | Remove-Item -Force
    $ext = [IO.Path]::GetExtension($File); if (-not $ext -or $File -like '*TranscodedWallpaper') { $ext = '.jpg' }
    Copy-Item -LiteralPath $File (Join-Path $P.Settings "wallpaper$ext") -Force
    $ch = @{ WallpaperFile = "wallpaper$ext" }
    if ($Style) { $ch.WallpaperStyle = $Style }
    if ($Tile)  { $ch.TileWallpaper = $Tile }
    Set-AppearanceMeta $ch
}

function Set-DesktopWallpaper([string]$Path, [string]$Style = '10', [string]$Tile = '0') {
    Set-ItemProperty 'HKCU:\Control Panel\Desktop' -Name WallpaperStyle -Value $Style
    Set-ItemProperty 'HKCU:\Control Panel\Desktop' -Name TileWallpaper -Value $Tile
    if (-not ('WinSetup.Wp' -as [type])) {
        Add-Type -Namespace WinSetup -Name Wp -MemberDefinition '[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int SystemParametersInfo(int a, int b, string c, int d);'
    }
    [WinSetup.Wp]::SystemParametersInfo(20, 0, $Path, 3) | Out-Null
}

$script:StartDir   = "$env:LOCALAPPDATA\Packages\Microsoft.Windows.StartMenuExperienceHost_cw5n1h2txyewy\LocalState"
$script:PinnedDir  = "$env:APPDATA\Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar"
$script:TaskbandKey = 'HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Taskband'

function Save-Appearance {
    Write-Step 'Gemmer baggrund og Windows-opsætning'
    $s = $P.Settings
    $c = Get-Config
    foreach ($d in 'Registry', 'Taskbar', 'Start') { New-Item -ItemType Directory -Force (Join-Path $s $d) | Out-Null }
    Get-ChildItem (Join-Path $s 'Registry') -File | Remove-Item -Force

    foreach ($k in $c.AppearanceKeys) {
        $out = Join-Path $s "Registry\$(Get-SafeName $k.Name)"
        if ($k.Values) {
            $item = Get-Item -LiteralPath ("Registry::" + $k.Key) -ErrorAction SilentlyContinue
            if (-not $item) { Write-Skip "$($k.Name): findes ikke"; continue }
            $vals = foreach ($v in $k.Values) {
                if ($null -ne $item.GetValue($v)) {
                    [pscustomobject]@{ Name = $v; Kind = $item.GetValueKind($v).ToString()
                                       Data = $item.GetValue($v, $null, 'DoNotExpandEnvironmentNames') }
                }
            }
            Save-Json ([pscustomobject]@{ Key = $k.Key; Values = @($vals) }) "$out.json"
        } else {
            reg.exe export $k.Key "$out.reg" /y 2>$null | Out-Null
            if ($LASTEXITCODE -ne 0) { Write-Skip "$($k.Name): findes ikke"; continue }
        }
        Write-Ok $k.Name
    }

    # Baggrund: brug originalfilen hvis den findes, ellers Windows' egen kopi
    $desk = Get-ItemProperty 'HKCU:\Control Panel\Desktop'
    $src = $desk.WallPaper
    if (-not $src -or -not (Test-Path -LiteralPath $src)) { $src = "$env:APPDATA\Microsoft\Windows\Themes\TranscodedWallpaper" }
    if (Test-Path -LiteralPath $src) {
        Set-SavedWallpaper $src $desk.WallpaperStyle $desk.TileWallpaper
        Write-Ok "Baggrund ($(Split-Path $src -Leaf))"
    } else { Write-Warn2 'Fandt ingen baggrund' }

    # Proceslinje-genveje og startmenu
    if (Test-Path $PinnedDir) { Invoke-Robo $PinnedDir (Join-Path $s 'Taskbar\Pinned') -Mirror | Out-Null }
    reg.exe export $TaskbandKey (Join-Path $s 'Taskbar\Taskband.reg') /y 2>$null | Out-Null
    Write-Ok 'Proceslinje-genveje'
    Get-ChildItem (Join-Path $s 'Start') -File | Remove-Item -Force
    Copy-Item "$StartDir\start*.bin" (Join-Path $s 'Start') -Force -ErrorAction SilentlyContinue
    Write-Ok 'Startmenu-fastgørelser'

    # Strømplan
    $g = [regex]::Match((powercfg.exe /getactivescheme | Out-String), '[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}').Value
    $pow = Join-Path $s 'power.pow'
    Remove-Item $pow -Force -ErrorAction SilentlyContinue
    if ($g) { powercfg.exe /export $pow $g 2>&1 | Out-Null }
    if ((Test-Path $pow) -and (Get-Item $pow).Length -gt 0) { Write-Ok 'Strømplan' } else { Write-Warn2 'Strømplanen kunne ikke gemmes (kræver administrator)' }

    Set-AppearanceMeta @{ Captured = (Get-Date).ToString('yyyy-MM-dd HH:mm'); CapturedFrom = $env:COMPUTERNAME }
}

function Restore-Appearance {
    Write-Step 'Lægger baggrund og Windows-opsætning på'
    $s = $P.Settings
    if (-not (Test-Path (Join-Path $s 'appearance.json'))) { Write-Warn2 'Der er ingen gemt opsætning endnu'; return }
    $m = Get-AppearanceMeta

    foreach ($f in Get-ChildItem (Join-Path $s 'Registry') -File -ErrorAction SilentlyContinue) {
        if ($f.Extension -eq '.reg') {
            reg.exe import $f.FullName 2>$null | Out-Null
        } else {
            $j = Read-Json $f.FullName
            $path = 'Registry::' + $j.Key
            if (-not (Test-Path -LiteralPath $path)) { New-Item -Path $path | Out-Null }
            foreach ($v in $j.Values) {
                $d = $v.Data
                if ($v.Kind -eq 'Binary') { $d = [byte[]]$d }
                New-ItemProperty -LiteralPath $path -Name $v.Name -PropertyType $v.Kind -Value $d -Force | Out-Null
            }
        }
        Write-Ok $f.BaseName
    }

    if ($m.WallpaperFile -and (Test-Path (Join-Path $s $m.WallpaperFile))) {
        $dir = Join-Path $env:USERPROFILE 'Pictures\Baggrund'
        New-Item -ItemType Directory -Force $dir | Out-Null
        $dest = Join-Path $dir $m.WallpaperFile
        Copy-Item (Join-Path $s $m.WallpaperFile) $dest -Force
        $style = if ($m.WallpaperStyle) { "$($m.WallpaperStyle)" } else { '10' }
        $tile  = if ($m.TileWallpaper)  { "$($m.TileWallpaper)" }  else { '0' }
        Set-DesktopWallpaper $dest $style $tile
        Write-Ok 'Baggrund'

        if ($m.LockScreen) {
            $lockDir = 'C:\ProgramData\WinSetup'; New-Item -ItemType Directory -Force $lockDir | Out-Null
            $lock = Join-Path $lockDir ('lockscreen' + [IO.Path]::GetExtension($dest))
            Copy-Item $dest $lock -Force
            $csp = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\PersonalizationCSP'
            if (-not (Test-Path $csp)) { New-Item -Path $csp | Out-Null }
            New-ItemProperty $csp -Name LockScreenImagePath -Value $lock -PropertyType String -Force | Out-Null
            New-ItemProperty $csp -Name LockScreenImageUrl -Value $lock -PropertyType String -Force | Out-Null
            New-ItemProperty $csp -Name LockScreenImageStatus -Value 1 -PropertyType DWord -Force | Out-Null
            Write-Ok 'Låseskærm'
        }
    }

    $pinned = Join-Path $s 'Taskbar\Pinned'
    if (Test-Path $pinned) { Invoke-Robo $pinned $PinnedDir | Out-Null }
    $tb = Join-Path $s 'Taskbar\Taskband.reg'
    if (Test-Path $tb) { reg.exe import $tb 2>$null | Out-Null }
    Write-Ok 'Proceslinje-genveje'

    if (Get-ChildItem (Join-Path $s 'Start') -Filter 'start*.bin' -ErrorAction SilentlyContinue) {
        Stop-Process -Name StartMenuExperienceHost -Force -ErrorAction SilentlyContinue
        Start-Sleep 1
        New-Item -ItemType Directory -Force $StartDir | Out-Null
        Copy-Item (Join-Path $s 'Start\start*.bin') $StartDir -Force
        Write-Ok 'Startmenu-fastgørelser'
    }

    $pow = Join-Path $s 'power.pow'
    if (Test-Path $pow) {
        $out = powercfg.exe /import $pow | Out-String
        $g = [regex]::Match($out, '[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}').Value
        if ($g) { powercfg.exe /setactive $g | Out-Null; Write-Ok 'Strømplan' }
    }

    Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue   # genstarter selv
}

# ---------------------------------------------------------------- backup af filer og indstillinger
function Backup-UserData {
    $c = Get-Config
    $dst = Get-BackupDir
    Write-Step "Tager backup til $dst"
    $running = @(Get-Process thunderbird, Code, notepad++, 'Bambu Studio' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty ProcessName -Unique)
    $snap = $null
    if ($running) {
        $snap = New-Snapshot
        if ($snap) { Write-Ok ("Disse programmer kører: " + ($running -join ', ') + " - kopierer fra et øjebliksbillede (VSS), så åbne filer kommer sikkert med") }
        else { Write-Warn2 ("Disse programmer kører og kan have låste filer: " + ($running -join ', ')) }
    }
    try {

    foreach ($f in @($c.UserFolders | Where-Object { $_.Enabled })) {
        $src = Get-UserFolder $f.Name
        if (-not (Test-Path -LiteralPath $src)) { Write-Skip "$($f.Label): findes ikke"; continue }
        $src = Get-SnapPath $snap $src
        if (Invoke-Robo $src (Join-Path $dst "Filer\$($f.Name)") -Mirror) { Write-Ok $f.Label } else { Write-Warn2 "Fejl ved $($f.Label)" }
    }
    foreach ($a in @($c.AppSettings | Where-Object { $_.Enabled })) {
        $safe = Get-SafeName $a.Name
        if ($a.Registry) {
            New-Item -ItemType Directory -Force (Join-Path $dst 'Registry') | Out-Null
            reg.exe export $a.Registry (Join-Path $dst "Registry\$safe.reg") /y 2>$null | Out-Null
            if ($LASTEXITCODE -eq 0) { Write-Ok $a.Name } else { Write-Skip "$($a.Name): ikke fundet" }
            continue
        }
        $src = Get-SnapPath $snap ([Environment]::ExpandEnvironmentVariables($a.Path))
        $target = Join-Path $dst "Programmer\$safe"
        if (Test-Path -LiteralPath $src -PathType Leaf) {
            if (Test-Path $target) { Remove-Item $target -Recurse -Force }
            New-Item -ItemType Directory -Force $target | Out-Null
            Copy-Item -LiteralPath $src $target -Force
            Write-Ok $a.Name
        } elseif (Test-Path -LiteralPath $src -PathType Container) {
            if (Invoke-Robo $src $target -Mirror) { Write-Ok $a.Name } else { Write-Warn2 "Fejl ved $($a.Name)" }
        } else { Write-Skip "$($a.Name): ikke fundet" }
    }

    } finally { Remove-Snapshot $snap }
    Save-Json ([pscustomobject]@{ Dato = (Get-Date).ToString('yyyy-MM-dd HH:mm'); Computer = $env:COMPUTERNAME }) (Join-Path $dst 'backup.json')
}

function Restore-UserData {
    $c = Get-Config
    $src = Get-BackupDir
    Write-Step "Gendanner filer og indstillinger fra $src"
    if (-not (Test-Path (Join-Path $src 'backup.json'))) { Write-Warn2 'Der er ingen backup endnu'; return }

    foreach ($f in @($c.UserFolders | Where-Object { $_.Enabled })) {
        $s = Join-Path $src "Filer\$($f.Name)"
        if (-not (Test-Path $s)) { continue }
        # /XO: overskriv aldrig en nyere fil på pc'en med en ældre fra backup
        if (Invoke-Robo $s (Get-UserFolder $f.Name) -SkipOlder) { Write-Ok $f.Label } else { Write-Warn2 "Fejl ved $($f.Label)" }
    }
    foreach ($a in @($c.AppSettings | Where-Object { $_.Enabled })) {
        $safe = Get-SafeName $a.Name
        if ($a.Registry) {
            $r = Join-Path $src "Registry\$safe.reg"
            if (Test-Path $r) { reg.exe import $r 2>$null | Out-Null; Write-Ok $a.Name }
            continue
        }
        $s = Join-Path $src "Programmer\$safe"
        if (-not (Test-Path $s)) { continue }
        $dest = [Environment]::ExpandEnvironmentVariables($a.Path)
        $leaf = Split-Path $dest -Leaf
        $items = @(Get-ChildItem -LiteralPath $s -Force)
        if ($items.Count -eq 1 -and $items[0].Name -eq $leaf -and -not $items[0].PSIsContainer) {
            New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null
            Copy-Item -LiteralPath $items[0].FullName $dest -Force
            Write-Ok $a.Name
        } elseif (Invoke-Robo $s $dest -SkipOlder) { Write-Ok $a.Name } else { Write-Warn2 "Fejl ved $($a.Name)" }
    }
}

# ---------------------------------------------------------------- USB-nøgle
function Initialize-UsbStick([string]$Drive) {
    $target = $Drive.TrimEnd('\') + '\'
    if (-not (Test-Path (Join-Path $target 'sources\boot.wim'))) {
        throw "$target ligner ikke en Windows-installations-USB (mangler sources\boot.wim)."
    }
    # Egen autounattend.xml (personlig, kun på sharet) bruges, hvis den findes. Ellers udfyldes skabelonen:
    # pc-navn fra config.json (BackupName) og administratorens navn fra den bruger, der gør USB-nøglen klar.
    $own = Join-Path $P.Usb 'autounattend.xml'
    if (Test-Path -LiteralPath $own) { Copy-Item -LiteralPath $own $target -Force }
    else {
        $pc = "$((Get-Config).BackupName)"; if (-not $pc -or $pc -eq 'MIN-PC') { $pc = $env:COMPUTERNAME }
        $user = try { (Get-LocalUser -Name $env:USERNAME -ErrorAction Stop).FullName } catch { '' }
        if (-not $user) { $user = $env:USERNAME }
        $xml = [IO.File]::ReadAllText((Join-Path $P.Usb 'autounattend.example.xml'))
        $xml = $xml.Replace('__PCNAME__', $pc.ToUpper()).Replace('__USER__', [Security.SecurityElement]::Escape($user))
        # Sprog, tastatur og tidszone: locale.json skrives af Fleetdeck (Indstillinger). Uden den: de tidligere faste danske værdier.
        $loc = [pscustomobject]@{ language = 'da-DK'; inputLocale = '0406:00000406'; timeZone = 'Romance Standard Time' }
        $lj = Join-Path $Root 'locale.json'
        if (Test-Path -LiteralPath $lj) { try { $loc = Get-Content -LiteralPath $lj -Raw -Encoding UTF8 | ConvertFrom-Json } catch { } }
        $xml = $xml.Replace('__LANG__', $loc.language).Replace('__INPUTLOCALE__', $loc.inputLocale).Replace('__TIMEZONE__', $loc.timeZone)
        [IO.File]::WriteAllText((Join-Path $target 'autounattend.xml'), $xml, (New-Object Text.UTF8Encoding $false))
    }
    $targets = @((Join-Path $target 'WinSetupBoot'), (Join-Path $target 'sources\$OEM$\$1\WinSetupBoot'))
    foreach ($d in $targets) {
        New-Item -ItemType Directory -Force $d | Out-Null
        Copy-Item (Join-Path $P.Usb 'Bootstrap.ps1') $d -Force
        [IO.File]::WriteAllText((Join-Path $d 'share.txt'), $Root, (New-Object Text.UTF8Encoding $false))
        # Fleetdeck: server-adresse og tilmeldingsnøgle (skrives af web-serveren på sharen)
        $aj = Join-Path $Root 'agent.json'
        if (Test-Path $aj) { Copy-Item $aj $d -Force } else { Remove-Item (Join-Path $d 'agent.json') -ErrorAction SilentlyContinue }
    }
    # Netkort-drivere (fx ASUS 10G) lægges i $WinpeDriver$, så Windows har netværk fra start
    $n = 0
    if (Test-Path $P.Drivers) {
        $wd = Join-Path $target '$WinpeDriver$'
        New-Item -ItemType Directory -Force $wd | Out-Null
        foreach ($dir in Get-ChildItem $P.Drivers -Directory) {
            $isNet = Get-ChildItem $dir.FullName -Filter *.inf | Where-Object {
                Select-String -LiteralPath $_.FullName -Pattern '^\s*Class\s*=\s*Net\s*$' -Quiet }
            if ($isNet) { Copy-Item $dir.FullName $wd -Recurse -Force; $n++ }
        }
    }

    # Fleetdeck: agenten installeres som Windows-tjeneste af SetupComplete.cmd (kører som SYSTEM lige efter
    # installationen, før nogen logger ind). Den tilmelder sig selv og starter nyinstallations-sekvensen.
    $agent = $false
    $aj = Read-Json (Join-Path $Root 'agent.json')
    $exe = Join-Path $Root 'Agent\Fleetdeck.exe'
    $scripts = Join-Path $target 'sources\$OEM$\$$\Setup\Scripts'
    if ($aj -and $aj.server -and $aj.key -and (Test-Path $exe)) {
        New-Item -ItemType Directory -Force $scripts | Out-Null
        Copy-Item $exe $scripts -Force
        # Fleetdeck-serveren skriver den aktuelle SetupComplete.cmd (forstår ren/profil fra netværksinstallationen)
        $sc = Join-Path $Root 'Agent\SetupComplete.cmd'
        if (Test-Path $sc) { $cmd = [IO.File]::ReadAllText($sc) } else {
        $cmd = "@echo off`r`n" +
               "rem Fleetdeck: installer agenten som Windows-tjeneste (koeres som SYSTEM efter Windows-installationen)`r`n" +
               "`"%~dp0Fleetdeck.exe`" install --server `"$($aj.server)`" --key `"$($aj.key)`" --provision >> `"%WINDIR%\Setup\Scripts\Fleetdeck-install.log`" 2>&1`r`n" +
               "exit /b 0`r`n" }
        [IO.File]::WriteAllText((Join-Path $scripts 'SetupComplete.cmd'), $cmd, [Text.Encoding]::ASCII)
        $agent = $true
    } elseif (Test-Path (Join-Path $scripts 'SetupComplete.cmd')) {
        Remove-Item (Join-Path $scripts 'SetupComplete.cmd'), (Join-Path $scripts 'Fleetdeck.exe') -Force -ErrorAction SilentlyContinue
    }
    [pscustomobject]@{ NetDrivers = $n; Agent = $agent; Server = $aj.server }
}
