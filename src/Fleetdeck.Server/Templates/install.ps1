# Fleetdeck - installer agenten (Windows-tjeneste) på denne pc.
# Kør i PowerShell som administrator:
#   irm "{{SERVER}}/install.ps1?key={{KEY}}" | iex
# Sæt $env:FLEETDECK_PROVISION = '1' først for at starte nyinstallations-sekvensen.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$server = '{{SERVER}}'
$key = '{{KEY}}'
$fdProfile = '{{PROFILE}}'   # valgt i Fleetdeck (0 = ingen profil); tom = spørg her
# Ingen nøgle: "Anmod om adgang" – agenten beder administratoren om lov og viser en kode ($env:FLEETDECK_MESSAGE: en besked til ham)
$request = '{{REQUEST}}'
if ($env:FLEETDECK_PROFILE) { $fdProfile = $env:FLEETDECK_PROFILE }

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) { Write-Host 'Kør PowerShell som administrator, og prøv igen.' -ForegroundColor Red; return }

# Profilen skal altid vælges, så maskinen ikke får programmer, den ikke skal have
if ($fdProfile -eq '' -and -not $request) {
  $list = @(Invoke-RestMethod -Uri "$server/api/agent/profiles?key=$key" -UseBasicParsing)
  Write-Host ''
  Write-Host 'Vælg profil til denne pc (styrer hvilke programmer og indstillinger, den får):' -ForegroundColor Cyan
  Write-Host '  0) Ingen profil – kun overvågning og opdateringer'
  $i = 1; foreach ($p in $list) { Write-Host ("  {0}) {1}  ({2} programmer, {3} maskiner)" -f $i, $p.name, $p.packages, $p.machines); $i++ }
  while ($true) {
    $a = Read-Host 'Skriv nummeret'
    if ($a -match '^\d+$' -and [int]$a -ge 0 -and [int]$a -le $list.Count) { break }
    Write-Host 'Skriv et af numrene ovenfor.' -ForegroundColor Yellow
  }
  $fdProfile = if ([int]$a -eq 0) { '0' } else { [string]$list[[int]$a - 1].id }
}

Write-Host "Fleetdeck: henter agenten fra $server" -ForegroundColor Cyan
$exe = Join-Path $env:TEMP 'Fleetdeck.exe'
Invoke-WebRequest -Uri "$server/api/agent/download" -OutFile $exe -UseBasicParsing
if ($request) {
  $argList = @('install', '--server', $server, '--request')
  if ($env:FLEETDECK_MESSAGE) { $argList += @('--message', $env:FLEETDECK_MESSAGE) }
  # allerede tilmeldt: kun med $env:FLEETDECK_FORCE = '1' (ellers mister pc'en sit token, til den er godkendt igen)
  if ($env:FLEETDECK_FORCE) { $argList += '--force' }
} else {
  $argList = @('install', '--server', $server, '--key', $key)
  $argList += @('--profile', $fdProfile)
}
# https med selvsigneret certifikat: agenten fastlåser serverens nøgle (tom = almindelig certifikatkontrol)
$tlsPin = '{{TLSPIN}}'
if ($tlsPin) { $argList += @('--tls-pin', $tlsPin) }
# Smart App Control (Windows 11) blokerer programmer, Fleetdeck installerer. Slås kun fra efter bekræftelse
# (eller med $env:FLEETDECK_DISABLE_SAC = '1'). Den kan ikke slås til igen uden at nulstille Windows.
$sac = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\CI\Policy' -ErrorAction SilentlyContinue).VerifiedAndReputablePolicyState
if ($sac -eq 1 -or $sac -eq 2) {
  if ($env:FLEETDECK_DISABLE_SAC -eq '1') { $argList += '--disable-smartapp' }
  elseif ([Environment]::UserInteractive) {
    Write-Host ''
    Write-Host 'Smart App Control er slået til og blokerer programmer, som Fleetdeck installerer og opdaterer.' -ForegroundColor Yellow
    Write-Host 'Bemærk: Den kan ikke slås til igen uden at nulstille eller geninstallere Windows. Microsoft Defender beskytter fortsat pc''en.'
    if ((Read-Host 'Slå Smart App Control fra? (j/n)') -match '^[jJyY]') { $argList += '--disable-smartapp' }
  }
}
if ($env:FLEETDECK_PROVISION) { $argList += '--provision' }
& $exe @argList
# 9 = anmodning om adgang sendt (venter på godkendelse), 0 = forbindelsen er bekræftet, 8 = forbundet men en genstart gør det færdigt, 7 = installeret uden forbindelse endnu (prøver selv videre)
$code = $LASTEXITCODE
if ($code -eq 10) { Write-Host 'Pc''en er allerede tilmeldt Fleetdeck. Sæt $env:FLEETDECK_FORCE = ''1'' og kør igen, hvis du vil starte forfra.' -ForegroundColor Yellow }
elseif ($code -eq 9) { Write-Host 'Fleetdeck er installeret og venter på, at administratoren godkender pc''en. Giv ham koden ovenfor.' -ForegroundColor Yellow }
elseif ($code -eq 7) { Write-Host 'Fleetdeck er installeret, men forbindelsen er ikke bekræftet endnu. Tjenesten prøver selv videre.' -ForegroundColor Yellow }
elseif ($code -eq 8) {
  Write-Host 'Fleetdeck er forbundet. En genstart gør installationen færdig.' -ForegroundColor Yellow
  if ([Environment]::UserInteractive -and (Read-Host 'Genstart nu? (j/n)') -match '^[jJyY]') { shutdown.exe /r /t 10 /c "Fleetdeck: genstarter for at gøre installationen færdig" }
}
elseif ($code -ne 0) { Write-Host "Installationen fejlede (kode $code)" -ForegroundColor Red; return }
else { Write-Host 'Fleetdeck er installeret, og forbindelsen er bekræftet.' -ForegroundColor Green }
Remove-Item $exe -Force -ErrorAction SilentlyContinue
