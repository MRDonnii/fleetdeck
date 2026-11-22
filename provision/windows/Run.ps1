# Run.ps1 - udfører trinene. Eksempler:
#   Run.ps1 -Do Full                 (ny pc: drivere, programmer, filer, opsætning)
#   Run.ps1 -Do Apps                 (installér programmer fra listen)
#   Run.ps1 -Do Capture,CaptureDrivers,Backup   (gem alt fra den nuværende pc)
param(
    [string[]]$Do = @('Full'),
    [switch]$NoPause,
    [switch]$Unattended      # bruges af Fleetdeck-agenten: ingen spørgsmål, exit-kode 1 ved advarsler
)
. (Join-Path $PSScriptRoot 'Lib.ps1')

$Do = @($Do | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
if (-not (Test-Admin)) {
    if ($Unattended) { Write-Host 'Run.ps1 kræver administrator.'; exit 2 }
    Start-Elevated -Script $PSCommandPath -Arguments ("-Do " + ($Do -join ','))
    return
}
$full = $Do -contains 'Full'
if ($full) { $Do = @('Drivers', 'Apps', 'Restore', 'Appearance') }

$Host.UI.RawUI.WindowTitle = 'Windows Opsætning - ' + ($Do -join ', ')
New-Item -ItemType Directory -Force $P.Logs | Out-Null
$log = Join-Path $P.Logs ("{0}_{1:yyyy-MM-dd_HHmm}_{2}.log" -f $env:COMPUTERNAME, (Get-Date), ($Do -join '-'))
Start-Transcript -Path $log | Out-Null
$t0 = Get-Date

Write-Host ''
Write-Host '  Windows Opsætning' -ForegroundColor White
Write-Host "  Server: $Root" -ForegroundColor DarkGray
Write-Host "  Trin:   $($Do -join ', ')" -ForegroundColor DarkGray

foreach ($step in $Do) {
    try {
        switch ($step) {
            'Drivers'        { Install-Drivers }
            'Apps'           { Install-Apps }
            'Restore'        { Restore-UserData }
            'Appearance'     { Restore-Appearance }
            'Backup'         { Backup-UserData }
            'Capture'        { Save-Appearance }
            'CaptureDrivers' { Export-Drivers }
            default          { Write-Warn2 "Ukendt trin: $step" }
        }
    } catch { Write-Warn2 "Fejl i trinnet $step : $_" }
}

$min = [int]((Get-Date) - $t0).TotalMinutes
Write-Host ''
if ($Warnings.Count) {
    Write-Host "Færdig efter $min min. med $($Warnings.Count) advarsel(er):" -ForegroundColor Yellow
    $Warnings | ForEach-Object { Write-Host "  - $_" -ForegroundColor Yellow }
} else {
    Write-Host "Færdig efter $min min. Alt gik godt." -ForegroundColor Green
}
Write-Host "Log: $log" -ForegroundColor DarkGray
Stop-Transcript | Out-Null

if ($Unattended) { exit ([int]($Warnings.Count -gt 0)) }
if ($full -or $Do -contains 'Drivers') {
    $r = Read-Host "`nGenstart pc'en nu, så alt bliver gjort færdigt? (J/N)"
    if ($r -match '^[jJyY]') { Restart-Computer -Force }
} elseif (-not $NoPause) {
    Read-Host "`nTryk Enter for at lukke"
}
