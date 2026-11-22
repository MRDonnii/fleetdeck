# Bootstrap.ps1 - kører automatisk første gang du logger på den nye Windows.
# Spørger om adgangskode og Unraid-login og starter så Run.ps1 -Do Full fra serveren.
param([string]$Share)

$local = 'C:\ProgramData\WinSetup'
New-Item -ItemType Directory -Force $local | Out-Null

# Standard-share: share.txt ved siden af scriptet (skrives når USB'en gøres klar)
if (-not $Share) {
    foreach ($f in (Join-Path $PSScriptRoot 'share.txt'), (Join-Path $local 'share.txt')) {
        if (Test-Path $f) { $Share = (Get-Content $f -Raw).Trim(); break }
    }
}
if (-not $Share) { $Share = Read-Host 'Stien til WinSetup-sharen (fx \\192.168.1.10\PC-Reinstall\WinSetup)' }

# Netværksinstallationen skriver deploy.cmd: "clean" = helt ren Windows, der kun skal have en adgangskode
$mode = 'managed'
$deployCmd = Join-Path $env:WINDIR 'Setup\Scripts\deploy.cmd'
if (Test-Path $deployCmd) { foreach ($l in Get-Content $deployCmd) { if ($l -match '^set WS_MODE=(\w+)') { $mode = $Matches[1] } } }
$clean = $mode -eq 'clean'

# Helt automatisk: Ren Windows skal intet. Er Fleetdeck-agenten installeret (af SetupComplete), styrer serveren
# resten – drivere, programmer, filer, baggrund – og slår til sidst automatisk login fra. Ingen spørgsmål her.
if ($clean) {
    Get-PSDrive -PSProvider FileSystem | ForEach-Object { Join-Path $_.Root 'WinSetupBoot' } | Where-Object { Test-Path $_ } |
        ForEach-Object { Remove-Item $_ -Recurse -Force -ErrorAction SilentlyContinue }
    return
}
if (Get-Service -Name 'Fleetdeck' -ErrorAction SilentlyContinue) {
    Add-Type -AssemblyName PresentationFramework
    [xml]$sx = @'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Title="Fleetdeck" Width="560" SizeToContent="Height"
        WindowStartupLocation="CenterScreen" Topmost="True" ResizeMode="NoResize" WindowStyle="None" AllowsTransparency="True"
        Background="Transparent" FontFamily="Segoe UI">
  <Border CornerRadius="18" Padding="34,30" Background="#111A30" BorderBrush="#2B3A5C" BorderThickness="1">
    <StackPanel>
      <TextBlock Text="Fleetdeck sætter din pc op" FontSize="24" FontWeight="SemiBold" Foreground="#F1F5F9"/>
      <TextBlock TextWrapping="Wrap" Margin="0,10,0,18" FontSize="14" Foreground="#94A3B8"
                 Text="Drivere, programmer og spil, dine filer, baggrund og Windows-opdateringer installeres nu automatisk. Pc'en logger selv ind og genstarter undervejs. Når alt er færdigt, beder Windows dig vælge en adgangskode."/>
      <ProgressBar IsIndeterminate="True" Height="6" Foreground="#3B82F6" Background="#1F2C4C" BorderThickness="0"/>
      <Button Name="Hide" Content="Skjul" Margin="0,22,0,0" Padding="18,7" HorizontalAlignment="Right" Foreground="White"
              Background="#1F2C4C" BorderBrush="#2B3A5C"/>
    </StackPanel>
  </Border>
</Window>
'@
    $sw = [Windows.Markup.XamlReader]::Load((New-Object System.Xml.XmlNodeReader $sx))
    $sw.FindName('Hide').Add_Click({ $sw.Close() })
    $sw.Add_MouseLeftButtonDown({ $sw.DragMove() })
    [void]$sw.ShowDialog()
    return
}

# Gem en lokal kopi + genvej, så man kan starte igen hvis vinduet lukkes
if ($PSCommandPath -ne (Join-Path $local 'Bootstrap.ps1')) { Copy-Item $PSCommandPath (Join-Path $local 'Bootstrap.ps1') -Force }
[IO.File]::WriteAllText((Join-Path $local 'share.txt'), $Share)
$desktop = [Environment]::GetFolderPath('Desktop')
$resume = Join-Path $desktop 'Fortsæt Windows-opsætning.lnk'
$wsh = New-Object -ComObject WScript.Shell
if (-not (Test-Path $resume)) {
    $l = $wsh.CreateShortcut($resume)
    $l.TargetPath = 'powershell.exe'
    $l.Arguments = "-NoProfile -ExecutionPolicy Bypass -STA -File `"$local\Bootstrap.ps1`""
    $l.IconLocation = 'shell32.dll,238'
    $l.Save()
}

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Start-Process powershell.exe -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -STA -File `"$local\Bootstrap.ps1`""
    return
}

Add-Type -AssemblyName PresentationFramework
[xml]$xaml = @'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        Title="Ny Windows - sidste trin" Width="520" SizeToContent="Height" WindowStartupLocation="CenterScreen"
        Topmost="True" ResizeMode="NoResize" FontFamily="Segoe UI" FontSize="13" Background="White">
  <StackPanel Margin="26">
    <TextBlock Text="Næsten færdig" FontSize="22" FontWeight="SemiBold"/>
    <TextBlock Name="Intro" TextWrapping="Wrap" Margin="0,6,0,18" Foreground="#4B5563"
               Text="Vælg en adgangskode til din konto og skriv hvor opsætningen ligger på Unraid. Derefter installeres drivere, programmer, dine filer, baggrund og opsætning automatisk."/>
    <TextBlock Text="Ny adgangskode til din konto"/>
    <PasswordBox Name="Pw1" Padding="5" Margin="0,4,0,10"/>
    <TextBlock Text="Gentag adgangskoden"/>
    <PasswordBox Name="Pw2" Padding="5" Margin="0,4,0,18"/>
    <StackPanel Name="ShareArea">
      <TextBlock Text="Unraid-share med opsætningen"/>
      <TextBox Name="ShareBox" Padding="5" Margin="0,4,0,10"/>
      <TextBlock Text="Unraid-brugernavn (lad stå tomt hvis sharen er offentlig)"/>
      <TextBox Name="UserBox" Padding="5" Margin="0,4,0,10"/>
      <TextBlock Text="Unraid-adgangskode"/>
      <PasswordBox Name="SharePw" Padding="5" Margin="0,4,0,16"/>
    </StackPanel>
    <TextBlock Name="Msg" TextWrapping="Wrap" Margin="0,0,0,12" Foreground="#B91C1C"/>
    <Button Name="Go" Content="Start installation" Padding="16,8" HorizontalAlignment="Right" IsDefault="True"
            Background="#0067C0" Foreground="White" FontWeight="SemiBold" BorderBrush="#005A9E"/>
  </StackPanel>
</Window>
'@
$win = [Windows.Markup.XamlReader]::Load((New-Object System.Xml.XmlNodeReader $xaml))
$ui = @{}
foreach ($n in 'Pw1', 'Pw2', 'ShareBox', 'UserBox', 'SharePw', 'Msg', 'Go', 'Intro', 'ShareArea') { $ui[$n] = $win.FindName($n) }
$ui.ShareBox.Text = $Share
$script:Started = $null
if ($clean) {
    $ui.ShareArea.Visibility = 'Collapsed'
    $ui.Intro.Text = 'Det er en helt ren Windows uden programmer og uden styring. Vælg bare en adgangskode til din konto.'
    $ui.Go.Content = 'Færdig'
}

function Show-Problem([string]$t) {
    $ui.Msg.Foreground = '#B91C1C'; $ui.Msg.Text = $t
    $ui.Go.IsEnabled = $true; $win.Cursor = $null
}

$ui.Go.Add_Click({
    if ($ui.Pw1.Password.Length -eq 0)              { Show-Problem 'Skriv en adgangskode til din konto.'; return }
    if ($ui.Pw1.Password -ne $ui.Pw2.Password)       { Show-Problem 'De to adgangskoder er ikke ens.'; return }
    if ($clean) {
        try { Set-LocalUser -Name $env:USERNAME -Password $ui.Pw1.SecurePassword -ErrorAction Stop }
        catch { Show-Problem "Kunne ikke sætte adgangskoden: $_"; return }
        $wl = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'
        Set-ItemProperty $wl -Name AutoAdminLogon -Value '0'
        Remove-ItemProperty $wl -Name DefaultPassword -ErrorAction SilentlyContinue
        $script:Started = 'clean'
        $win.Close()
        return
    }
    $sh = $ui.ShareBox.Text.Trim().TrimEnd('\')
    if ($sh -notmatch '^\\\\([^\\]+)\\.+') { Show-Problem 'Sharen skal skrives som \\SERVER\Share'; return }
    $srv = $Matches[1]

    $ui.Go.IsEnabled = $false; $win.Cursor = [System.Windows.Input.Cursors]::Wait
    $ui.Msg.Foreground = '#374151'; $ui.Msg.Text = "Forbinder til $srv … (venter op til 2 minutter på netværket)"
    $win.Dispatcher.Invoke([action]{}, 'Render')

    $user = $ui.UserBox.Text.Trim()
    if ($user) { cmdkey.exe /add:$srv /user:$user "/pass:$($ui.SharePw.Password)" | Out-Null }
    $deadline = (Get-Date).AddMinutes(2); $found = $false
    while ((Get-Date) -lt $deadline) {
        if (Test-Path (Join-Path $sh 'Run.ps1')) { $found = $true; break }
        Start-Sleep 3
    }
    if (-not $found) { Show-Problem "Kan ikke finde $sh\Run.ps1. Tjek netværkskablet, navnet på sharen og Unraid-brugeren."; return }

    try { Set-LocalUser -Name $env:USERNAME -Password $ui.Pw1.SecurePassword -ErrorAction Stop }
    catch { Show-Problem "Kunne ikke sætte adgangskoden: $_"; return }
    $wl = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'
    Set-ItemProperty $wl -Name AutoAdminLogon -Value '0'
    Remove-ItemProperty $wl -Name DefaultPassword -ErrorAction SilentlyContinue

    $script:Started = $sh
    $win.Close()
})

[void]$win.ShowDialog()
if (-not $Started) { return }
if ($Started -eq 'clean') {
    # Ren Windows: fjern alle spor af opsætningen
    Remove-Item $resume -Force -ErrorAction SilentlyContinue
    Remove-Item $deployCmd -Force -ErrorAction SilentlyContinue
    Get-PSDrive -PSProvider FileSystem | ForEach-Object { Join-Path $_.Root 'WinSetupBoot' } | Where-Object { Test-Path $_ } |
        ForEach-Object { Remove-Item $_ -Recurse -Force -ErrorAction SilentlyContinue }
    Start-Process cmd.exe -ArgumentList "/c timeout /t 3 >nul & rmdir /s /q `"$local`"" -WindowStyle Hidden
    return
}

[IO.File]::WriteAllText((Join-Path $local 'share.txt'), $Started)
Remove-Item $resume -Force -ErrorAction SilentlyContinue
$l = $wsh.CreateShortcut((Join-Path $desktop 'Windows Opsætning.lnk'))
$l.TargetPath = Join-Path $Started 'Start-Opsaetning.cmd'
$l.IconLocation = 'imageres.dll,109'
$l.Save()

# Fleetdeck: findes der en web-server, så tilmeld pc'en og lad serveren styre resten
$agent = $null
foreach ($f in (Join-Path $PSScriptRoot 'agent.json'), (Join-Path $Started 'agent.json')) {
    if (Test-Path $f) { try { $agent = Get-Content $f -Raw | ConvertFrom-Json; break } catch {} }
}
# Normalt har SetupComplete.cmd allerede installeret agenten som tjeneste under Windows-installationen
$enrolled = [bool](Get-Service -Name 'Fleetdeck' -ErrorAction SilentlyContinue)
if (-not $enrolled -and $agent -and $agent.server -and $agent.key) {
    try {
        $env:FLEETDECK_PROVISION = '1'
        $script = (Invoke-WebRequest -Uri "$($agent.server)/install.ps1?key=$($agent.key)" -UseBasicParsing -TimeoutSec 30).Content
        & ([scriptblock]::Create($script))
        $enrolled = Test-Path 'C:\ProgramData\Fleetdeck\config.json'
    } catch { Write-Host "Fleetdeck-tilmelding fejlede: $_" -ForegroundColor Yellow }
}

if ($enrolled) {
    $l = $wsh.CreateShortcut((Join-Path $desktop 'Fleetdeck.url'))
    $l.TargetPath = $agent.server
    $l.Save()
    # Vis agentens log, så man kan følge nyinstallationen lokalt (den styres fra web-siden)
    $follow = Join-Path $local 'Follow.ps1'
    $txt = @"
`$Host.UI.RawUI.WindowTitle = 'Fleetdeck - nyinstallation'
Write-Host "Installationen styres nu af Fleetdeck: $($agent.server)" -ForegroundColor Cyan
Write-Host "Du kan lukke dette vindue. Pc'en genstarter selv, når alt er færdigt."
Write-Host ''
`$log = 'C:\ProgramData\Fleetdeck\agent.log'
while (-not (Test-Path `$log)) { Start-Sleep 2 }
Get-Content `$log -Wait -Tail 40
"@
    [IO.File]::WriteAllText($follow, $txt, (New-Object Text.UTF8Encoding $true))
    Start-Process powershell.exe -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$follow`""
    Start-Process $agent.server
} else {
    Start-Process powershell.exe -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $Started 'Run.ps1')`" -Do Full"
}
