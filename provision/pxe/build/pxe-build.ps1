# Bygger netværksboot-filer til WinSetup: tilpasset boot.wim + BCD til Microsofts signerede bootmgfw.efi
# Kræver i mappen $W: boot.wim (fra Windows 11-ISO'en), bootmgfw.efi, boot.sdi, fonts\ og WinSetupGui.exe (se ..\gui\build.sh)
#   -Server: IP-adressen på serveren med sharet (fx 192.168.1.10). -Share: sharets navn (standard PC-Reinstall).
#   -ServerName: navnet der vises i menuen (standard: -Server).
param([string]$W = $PSScriptRoot, [Parameter(Mandatory)][string]$Server, [string]$Share = 'PC-Reinstall', [string]$ServerName, [string]$Locale)
if (-not $ServerName) { $ServerName = $Server }
# Sprog i boot-menuen: -Locale, ellers sproget fra Fleetdeck (WinSetup\locale.json på sharet), ellers da-DK som hidtil
if (-not $Locale) { try { $Locale = (Get-Content "\\$Server\$Share\WinSetup\locale.json" -Raw -ErrorAction Stop | ConvertFrom-Json).language } catch { } }
if (-not $Locale) { $Locale = 'da-DK' }
$ErrorActionPreference = 'Stop'
Start-Transcript -Path (Join-Path $W 'pxe-build.log') -Force | Out-Null
try {
    $out = Join-Path $W 'out'; $mnt = Join-Path $W 'mnt'
    foreach ($d in $out, $mnt) { if (Test-Path $d) { dism /Unmount-Image /MountDir:$mnt /Discard 2>$null | Out-Null; Remove-Item $d -Recurse -Force -ErrorAction SilentlyContinue } }
    New-Item -ItemType Directory -Force "$out\Boot\Fonts", "$out\EFI\Microsoft\Boot\Fonts", $mnt | Out-Null

    # 1) Kun Windows Setup-billedet (index 2), markeret som bootbart
    Write-Host '== Eksporterer Setup-billedet'
    dism /Export-Image /SourceImageFile:"$W\boot.wim" /SourceIndex:2 /DestinationImageFile:"$out\Boot\boot.wim" /Bootable /Compress:max
    if ($LASTEXITCODE) { throw "Export-Image fejlede ($LASTEXITCODE)" }

    # 2) Tilpas: start vores script i stedet for Setup, og tilføj netkort-drivere fra denne pc
    Write-Host '== Monterer og tilpasser'
    dism /Mount-Image /ImageFile:"$out\Boot\boot.wim" /Index:1 /MountDir:$mnt
    if ($LASTEXITCODE) { throw "Mount-Image fejlede ($LASTEXITCODE)" }
    $sys = Join-Path $mnt 'Windows\System32'
    # Grafisk installer først. Lukkes den, starter tekstmenuen som nødløsning.
    if (-not (Test-Path "$W\WinSetupGui.exe")) { throw "Mangler $W\WinSetupGui.exe" }
    Copy-Item "$W\WinSetupGui.exe" (Join-Path $sys 'WinSetupGui.exe') -Force
    @"
[LaunchApps]
%SYSTEMROOT%\System32\wpeinit.exe
%SYSTEMROOT%\System32\WinSetupGui.exe, /server $Server /share $Share /name `"$ServerName`"
%SYSTEMROOT%\System32\cmd.exe, /k %SYSTEMROOT%\System32\WinSetupPXE.cmd
"@ | Set-Content (Join-Path $sys 'winpeshl.ini') -Encoding ASCII
    @'
@echo off
title WinSetup - netvaerksinstallation af Windows 11
color 1F
cls
echo.
echo   ============================================================
echo    WinSetup  -  Windows 11 installeres fra @NAME@
echo   ============================================================
echo.
echo   Venter paa netvaerk ...
set /a TRY=0
:waitnet
ping -n 1 -w 1000 @SERVER@ >nul 2>&1 && goto netok
set /a TRY+=1
if %TRY% GEQ 60 (echo   Intet netvaerk. Tjek kablet og genstart. & goto end)
timeout /t 2 >nul
goto waitnet
:netok
echo   Netvaerk OK.
echo.
echo   Log ind med din bruger til sharet (samme som til \\@SERVER@\@SHARE@).
echo.
:login
set "U="
set /p U=  Bruger:
if "%U%"=="" goto login
net use Z: \\@SERVER@\@SHARE@ /user:%U% * /persistent:no
if errorlevel 1 (echo. & echo   Forkert brugernavn eller adgangskode - proev igen. & echo. & goto login)
rem Selve menuen ligger paa serveren (PXE\menu.cmd), saa den kan rettes uden at bygge boot.wim om
if not exist Z:\WinSetup\PXE\menu.cmd (echo   Fandt ikke Z:\WinSetup\PXE\menu.cmd & goto end)
copy /y Z:\WinSetup\PXE\menu.cmd X:\menu.cmd >nul
set "WS_NAME=@NAME@"
call X:\menu.cmd
:end
echo.
echo   Skriv 'exit' for at genstarte.
'@ -replace '@SERVER@', $Server -replace '@SHARE@', $Share -replace '@NAME@', $ServerName | Set-Content (Join-Path $sys 'WinSetupPXE.cmd') -Encoding ASCII

    $net = @(Get-WindowsDriver -Online | Where-Object { $_.ClassName -eq 'Net' })
    Write-Host "== Netkort-drivere fra denne pc: $($net.Count)"
    foreach ($d in $net) {
        Write-Host "   $($d.ProviderName) $($d.Version)  $([IO.Path]::GetFileName($d.OriginalFileName))"
        dism /Image:$mnt /Add-Driver /Driver:"$($d.OriginalFileName)" | Out-Null
    }
    # VM-drivere (VirtIO: disk, netkort, skærm m.m.), så Unraid-VM'er kan installeres uden at hente drivere
    if (Test-Path "$W\drivers") {
        Write-Host "== Drivere fra $W\drivers"
        dism /Image:$mnt /Add-Driver /Driver:"$W\drivers" /Recurse | Select-String 'Installing|Installerer|successfully|fuldført|Error|Fejl' | ForEach-Object { "   $_" } | Write-Host
    }
    dism /Unmount-Image /MountDir:$mnt /Commit
    if ($LASTEXITCODE) { throw "Unmount fejlede ($LASTEXITCODE)" }

    # 3) Boot-filer: Microsofts signerede boot manager (Secure Boot kan forblive slået til)
    Copy-Item "$W\bootmgfw.efi" "$out\Boot\bootmgfw.efi" -Force
    Copy-Item "$W\boot.sdi" "$out\Boot\boot.sdi" -Force
    Copy-Item "$W\fonts\*" "$out\Boot\Fonts\" -Force
    Copy-Item "$W\fonts\*" "$out\EFI\Microsoft\Boot\Fonts\" -Force

    # 4) BCD: RAM-disk-boot af boot.wim over TFTP
    Write-Host '== Opretter BCD'
    $bcd = "$out\Boot\BCD"
    bcdedit /createstore $bcd | Out-Null
    bcdedit /store $bcd /create '{ramdiskoptions}' /d 'WinSetup ramdisk' | Out-Null
    bcdedit /store $bcd /set '{ramdiskoptions}' ramdisksdidevice boot | Out-Null
    bcdedit /store $bcd /set '{ramdiskoptions}' ramdisksdipath '\Boot\boot.sdi' | Out-Null
    bcdedit /store $bcd /set '{ramdiskoptions}' ramdisktftpblocksize 1456 | Out-Null
    $line = bcdedit /store $bcd /create /d 'WinSetup - Installer Windows 11' /application osloader
    $guid = [regex]::Match(($line | Out-String), '\{[0-9a-fA-F-]{36}\}').Value
    if (-not $guid) { throw "Kunne ikke oprette osloader: $line" }
    foreach ($a in @(
        @('device', "ramdisk=[boot]\Boot\boot.wim,{ramdiskoptions}"),
        @('osdevice', "ramdisk=[boot]\Boot\boot.wim,{ramdiskoptions}"),
        @('path', '\windows\system32\boot\winload.efi'),
        @('systemroot', '\windows'),
        @('detecthal', 'yes'),
        @('winpe', 'yes'),
        @('locale', $Locale))) { bcdedit /store $bcd /set $guid $a[0] $a[1] | Out-Null; if ($LASTEXITCODE) { throw "bcdedit $($a[0]) fejlede" } }
    bcdedit /store $bcd /create '{bootmgr}' /d 'WinSetup' | Out-Null
    bcdedit /store $bcd /set '{bootmgr}' timeout 3 | Out-Null
    bcdedit /store $bcd /set '{bootmgr}' locale $Locale | Out-Null
    bcdedit /store $bcd /displayorder $guid | Out-Null
    bcdedit /store $bcd /default $guid | Out-Null
    bcdedit /store $bcd /enum all | Out-String | Write-Host
    Get-ChildItem $out -Recurse -File | Select-Object @{n='Fil';e={$_.FullName.Replace("$out\",'')}}, @{n='MB';e={[math]::Round($_.Length/1MB,1)}} | Format-Table -AutoSize | Out-String | Write-Host
    'OK' | Set-Content (Join-Path $W 'pxe-build.ok')
} catch {
    Write-Host "FEJL: $_" -ForegroundColor Red
    dism /Unmount-Image /MountDir:$mnt /Discard 2>$null | Out-Null
} finally { Stop-Transcript | Out-Null }
