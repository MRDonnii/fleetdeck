# WinSetup.ps1 - vinduet hvor du styrer programliste, baggrund/opsætning, backup og installation.
param([switch]$NoElevate)
. (Join-Path $PSScriptRoot 'Lib.ps1')
if (-not $NoElevate -and -not (Test-Admin)) { Start-Elevated -Script $PSCommandPath -Sta -Hidden; return }

Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, System.Windows.Forms

[xml]$xaml = @'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Windows Opsætning" Width="1180" Height="760" MinWidth="940" MinHeight="600"
        WindowStartupLocation="CenterScreen" FontFamily="Segoe UI" FontSize="13" Background="#F4F5F7">
  <Window.Resources>
    <Style TargetType="Button">
      <Setter Property="Padding" Value="12,6"/>
      <Setter Property="Margin" Value="0,0,8,0"/>
      <Setter Property="MinWidth" Value="90"/>
    </Style>
    <Style x:Key="Primary" TargetType="Button" BasedOn="{StaticResource {x:Type Button}}">
      <Setter Property="Background" Value="#0067C0"/>
      <Setter Property="Foreground" Value="White"/>
      <Setter Property="BorderBrush" Value="#005A9E"/>
      <Setter Property="FontWeight" Value="SemiBold"/>
    </Style>
    <Style TargetType="GroupBox">
      <Setter Property="Padding" Value="10"/>
    </Style>
    <Style x:Key="Hint" TargetType="TextBlock">
      <Setter Property="Foreground" Value="#4B5563"/>
      <Setter Property="TextWrapping" Value="Wrap"/>
    </Style>
  </Window.Resources>
  <DockPanel>
    <Border DockPanel.Dock="Top" Background="#1F2937" Padding="20,14">
      <StackPanel>
        <TextBlock Text="Windows Opsætning" Foreground="White" FontSize="20" FontWeight="SemiBold"/>
        <TextBlock Name="ServerText" Foreground="#9CA3AF" Margin="0,2,0,0"/>
      </StackPanel>
    </Border>
    <Border DockPanel.Dock="Bottom" Background="#E5E7EB" Padding="20,6">
      <TextBlock Name="StatusText" Text="Klar." Foreground="#374151"/>
    </Border>
    <TabControl Margin="16" Padding="12">

      <TabItem Header="  Programmer  ">
        <Grid>
          <Grid.ColumnDefinitions>
            <ColumnDefinition Width="*"/>
            <ColumnDefinition Width="16"/>
            <ColumnDefinition Width="360"/>
          </Grid.ColumnDefinitions>
          <DockPanel Grid.Column="0">
            <TextBlock DockPanel.Dock="Top" Style="{StaticResource Hint}" Margin="0,0,0,8"
                       Text="Programmer med flueben i 'Med' bliver installeret. Du kan rette navn, parametre og note direkte i listen. Husk 'Gem liste'."/>
            <StackPanel DockPanel.Dock="Bottom" Orientation="Horizontal" Margin="0,10,0,0">
              <Button Name="SaveAppsBtn" Content="Gem liste" Style="{StaticResource Primary}"/>
              <Button Name="AddFileBtn" Content="Tilføj installationsfil…"/>
              <Button Name="ImportPcBtn" Content="Tilføj det der er på denne pc"/>
              <Button Name="RemoveBtn" Content="Fjern markerede"/>
            </StackPanel>
            <DataGrid Name="AppsGrid" AutoGenerateColumns="False" CanUserAddRows="False" CanUserDeleteRows="False"
                      SelectionMode="Extended" SelectionUnit="FullRow" HeadersVisibility="Column"
                      GridLinesVisibility="Horizontal" HorizontalGridLinesBrush="#E5E7EB" RowHeight="26"
                      Background="White" BorderBrush="#D1D5DB">
              <DataGrid.Columns>
                <DataGridTemplateColumn Header="Med" Width="44" SortMemberPath="Enabled">
                  <DataGridTemplateColumn.CellTemplate>
                    <DataTemplate>
                      <CheckBox IsChecked="{Binding Enabled, UpdateSourceTrigger=PropertyChanged}" HorizontalAlignment="Center" VerticalAlignment="Center"/>
                    </DataTemplate>
                  </DataGridTemplateColumn.CellTemplate>
                </DataGridTemplateColumn>
                <DataGridTextColumn Header="Navn" Binding="{Binding Name}" Width="2*"/>
                <DataGridTextColumn Header="Kilde" Binding="{Binding Type}" Width="66" IsReadOnly="True"/>
                <DataGridTextColumn Header="Id / fil" Binding="{Binding Id}" Width="2*"/>
                <DataGridTextColumn Header="Parametre" Binding="{Binding Args}" Width="*"/>
                <DataGridTemplateColumn Header="Som bruger" Width="78" SortMemberPath="NotAdmin">
                  <DataGridTemplateColumn.CellTemplate>
                    <DataTemplate>
                      <CheckBox IsChecked="{Binding NotAdmin, UpdateSourceTrigger=PropertyChanged}" HorizontalAlignment="Center" VerticalAlignment="Center"
                                ToolTip="Installér uden administrator (kræves af fx Spotify)"/>
                    </DataTemplate>
                  </DataGridTemplateColumn.CellTemplate>
                </DataGridTemplateColumn>
                <DataGridTextColumn Header="Status" Binding="{Binding Status}" Width="86" IsReadOnly="True"/>
                <DataGridTextColumn Header="Note" Binding="{Binding Note}" Width="2*"/>
              </DataGrid.Columns>
            </DataGrid>
          </DockPanel>
          <GroupBox Grid.Column="2" Header="Find et program (winget)">
            <DockPanel>
              <DockPanel DockPanel.Dock="Top" Margin="0,0,0,8">
                <Button Name="SearchBtn" DockPanel.Dock="Right" Content="Søg" MinWidth="60" Margin="8,0,0,0"/>
                <TextBox Name="SearchBox" Padding="4" VerticalContentAlignment="Center"/>
              </DockPanel>
              <Button Name="AddSearchBtn" DockPanel.Dock="Bottom" Content="Tilføj valgte til listen" Margin="0,8,0,0" Style="{StaticResource Primary}"/>
              <ListView Name="SearchList" SelectionMode="Extended">
                <ListView.View>
                  <GridView>
                    <GridViewColumn Header="Navn" DisplayMemberBinding="{Binding Name}" Width="140"/>
                    <GridViewColumn Header="Id" DisplayMemberBinding="{Binding Id}" Width="125"/>
                    <GridViewColumn Header="Kilde" DisplayMemberBinding="{Binding Source}" Width="58"/>
                  </GridView>
                </ListView.View>
              </ListView>
            </DockPanel>
          </GroupBox>
        </Grid>
      </TabItem>

      <TabItem Header="  Baggrund og opsætning  ">
        <ScrollViewer VerticalScrollBarVisibility="Auto">
          <StackPanel MaxWidth="920" HorizontalAlignment="Left">
            <TextBlock Style="{StaticResource Hint}" Margin="0,0,0,10"
                       Text="Her gemmes din baggrund og Windows-opsætning: mørkt tema, accentfarve, proceslinje, Stifinder, mus og tastatur, startmenu, strømplan og drivere. Det lægges automatisk på ved hver nyinstallation."/>
            <Border BorderBrush="#D1D5DB" BorderThickness="1" Background="#111827" Height="300" CornerRadius="4">
              <Image Name="WallpaperImg" Stretch="Uniform"/>
            </Border>
            <TextBlock Name="WallpaperInfo" Margin="0,6,0,12" Style="{StaticResource Hint}"/>
            <WrapPanel Margin="0,0,0,10">
              <Button Name="CaptureBtn" Content="Hent baggrund og opsætning fra denne pc" Style="{StaticResource Primary}" Margin="0,0,8,6"/>
              <Button Name="ChooseWpBtn" Content="Vælg en anden baggrund…" Margin="0,0,8,6"/>
              <Button Name="ApplyBtn" Content="Læg opsætningen på denne pc nu" Margin="0,0,8,6"/>
            </WrapPanel>
            <CheckBox Name="DriversChk" IsChecked="True" Margin="0,4"
                      Content="Gem også drivere når jeg henter fra denne pc (AMD, netkort, chipset, GoXLR m.fl.)"/>
            <CheckBox Name="LockScreenChk" Margin="0,4" Content="Brug også baggrunden på låseskærmen"/>
            <TextBlock Name="DriversInfo" Margin="0,10,0,0" Style="{StaticResource Hint}"/>
          </StackPanel>
        </ScrollViewer>
      </TabItem>

      <TabItem Header="  Filer og indstillinger  ">
        <Grid>
          <Grid.ColumnDefinitions>
            <ColumnDefinition Width="*"/>
            <ColumnDefinition Width="16"/>
            <ColumnDefinition Width="*"/>
          </Grid.ColumnDefinitions>
          <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
          </Grid.RowDefinitions>
          <TextBlock Grid.ColumnSpan="3" Style="{StaticResource Hint}" Margin="0,0,0,10"
                     Text="Backup gemmes på Unraid og lægges automatisk tilbage efter en nyinstallation. Ved gendannelse bliver nyere filer på pc'en aldrig overskrevet. Luk Thunderbird, VS Code og Notepad++ før backup."/>
          <GroupBox Grid.Row="1" Grid.Column="0" Header="Mapper">
            <ScrollViewer VerticalScrollBarVisibility="Auto"><StackPanel Name="FoldersPanel"/></ScrollViewer>
          </GroupBox>
          <GroupBox Grid.Row="1" Grid.Column="2" Header="Programindstillinger">
            <ScrollViewer VerticalScrollBarVisibility="Auto"><StackPanel Name="AppSetPanel"/></ScrollViewer>
          </GroupBox>
          <StackPanel Grid.Row="2" Grid.ColumnSpan="3" Orientation="Horizontal" Margin="0,12,0,0">
            <Button Name="BackupBtn" Content="Tag backup nu" Style="{StaticResource Primary}"/>
            <Button Name="RestoreBtn" Content="Gendan backup på denne pc"/>
            <TextBlock Name="LastBackupText" VerticalAlignment="Center" Margin="8,0,0,0" Foreground="#4B5563"/>
          </StackPanel>
        </Grid>
      </TabItem>

      <TabItem Header="  Installér  ">
        <ScrollViewer VerticalScrollBarVisibility="Auto">
          <StackPanel MaxWidth="760" HorizontalAlignment="Left">
            <TextBlock FontSize="16" FontWeight="SemiBold" Text="Installér på denne pc"/>
            <TextBlock Style="{StaticResource Hint}" Margin="0,4,0,8"
                       Text="På en ny pc kører alt automatisk. Herfra kan du fx installere programmer du lige har tilføjet. Programmer der allerede er installeret bliver sprunget over."/>
            <CheckBox Name="ChkDrivers" Content="Drivere" Margin="0,4"/>
            <CheckBox Name="ChkApps" Content="Programmer fra listen" IsChecked="True" Margin="0,4"/>
            <CheckBox Name="ChkRestore" Content="Gendan filer og programindstillinger fra backup" Margin="0,4"/>
            <CheckBox Name="ChkAppearance" Content="Baggrund og Windows-opsætning" Margin="0,4"/>
            <Button Name="InstallBtn" Content="Start" Style="{StaticResource Primary}" HorizontalAlignment="Left" MinWidth="140" Margin="0,12,0,0"/>
            <TextBlock Style="{StaticResource Hint}" Margin="0,10,0,0"
                       Text="Det kører i sit eget vindue, så du kan følge med. Loggen gemmes i mappen Logs på serveren."/>

            <Separator Margin="0,24"/>
            <TextBlock FontSize="16" FontWeight="SemiBold" Text="USB-nøgle til nyinstallation"/>
            <TextBlock Style="{StaticResource Hint}" Margin="0,4,0,8"
                       Text="Lav først en dansk Windows 11-USB med Microsofts Media Creation Tool. Klik så her og vælg USB-nøglen. Der lægges en svarfil på nøglen, så installationen kører af sig selv. Den stopper kun ved valg af disk. Netkort-drivere kommer også med."/>
            <Button Name="UsbBtn" Content="Gør USB-nøgle klar…" HorizontalAlignment="Left"/>
          </StackPanel>
        </ScrollViewer>
      </TabItem>
    </TabControl>
  </DockPanel>
</Window>
'@

$win = [Windows.Markup.XamlReader]::Load((New-Object System.Xml.XmlNodeReader $xaml))
$ui = @{}
foreach ($n in $xaml.SelectNodes('//*[@Name]')) { $ui[$n.GetAttribute('Name')] = $win.FindName($n.GetAttribute('Name')) }

function Show-Msg([string]$Text, [string]$Icon = 'Information', [string]$Buttons = 'OK') {
    [System.Windows.MessageBox]::Show($win, $Text, 'Windows Opsætning', $Buttons, $Icon)
}
function Set-Status([string]$t) { $ui.StatusText.Text = $t }

$ui.ServerText.Text = "Opsætningen ligger her: $Root"
$aj = Read-Json (Join-Path $Root 'agent.json')
if ($aj -and $aj.server) { $ui.ServerText.Text += "   ·   Web-styring: $($aj.server)" }

# ---------------------------------------------------------------- programliste
$table = New-Object System.Data.DataTable
[void]$table.Columns.Add('Enabled', [bool]);  $table.Columns['Enabled'].DefaultValue = $false
[void]$table.Columns.Add('NotAdmin', [bool]); $table.Columns['NotAdmin'].DefaultValue = $false
foreach ($c in 'Name', 'Type', 'Id', 'Args', 'Note', 'Status') { [void]$table.Columns.Add($c, [string]); $table.Columns[$c].DefaultValue = '' }
$script:Dirty = $false

function Get-FileStatus([string]$Type, [string]$Id) {
    if ($Type -ne 'file') { return '' }
    if ($Id -and (Test-Path -LiteralPath (Join-Path $P.Installers $Id))) { 'Fil klar' } else { 'Mangler fil' }
}
function Add-AppRow($Enabled, $Name, $Type, $Id, $AppArgs, $Note, $NotAdmin) {
    $r = $table.NewRow()
    $r.Enabled = [bool]$Enabled; $r.NotAdmin = [bool]$NotAdmin
    $r.Name = "$Name"; $r.Type = "$Type"; $r.Id = "$Id"; $r.Args = "$AppArgs"; $r.Note = "$Note"
    $r.Status = Get-FileStatus $Type $Id
    $table.Rows.Add($r)
}
function Import-AppTable {
    $table.Rows.Clear()
    foreach ($a in Get-Apps) { Add-AppRow $a.Enabled $a.Name $a.Type $a.Id $a.Args $a.Note $a.NotAdmin }
    $table.AcceptChanges()
    $script:Dirty = $false
}
function Save-AppTable {
    [void]$ui.AppsGrid.CommitEdit([System.Windows.Controls.DataGridEditingUnit]::Row, $true)
    $list = foreach ($r in $table.Rows) {
        if ($r.RowState -eq 'Deleted') { continue }
        $o = [ordered]@{ Enabled = [bool]$r.Enabled; Name = "$($r.Name)"; Type = "$($r.Type)"; Id = "$($r.Id)"; Args = "$($r.Args)"; Note = "$($r.Note)" }
        if ([bool]$r.NotAdmin) { $o.NotAdmin = $true }
        [pscustomobject]$o
    }
    Save-Json @($list) $P.Apps
    $script:Dirty = $false
    Set-Status ("Programlisten er gemt ({0} programmer, {1} valgt)." -f @($list).Count, @($list | Where-Object Enabled).Count)
}
function Test-HasId([string]$Id) { [bool]($table.Select("Id = '$($Id -replace "'", "''")'").Count) }

$table.Add_RowChanged({ $script:Dirty = $true })
$table.Add_RowDeleted({ $script:Dirty = $true })
Import-AppTable
$ui.AppsGrid.ItemsSource = $table.DefaultView

$ui.SaveAppsBtn.Add_Click({ Save-AppTable })

$ui.RemoveBtn.Add_Click({
    $sel = @($ui.AppsGrid.SelectedItems)
    if (-not $sel.Count) { return }
    if ((Show-Msg "Fjern $($sel.Count) program(mer) fra listen?" 'Question' 'YesNo') -ne 'Yes') { return }
    foreach ($v in $sel) { $v.Row.Delete() }
    $table.AcceptChanges(); $script:Dirty = $true
})

$ui.AddFileBtn.Add_Click({
    $dlg = New-Object Microsoft.Win32.OpenFileDialog
    $dlg.Filter = 'Installationsfiler (*.exe;*.msi)|*.exe;*.msi|Alle filer|*.*'
    if (-not $dlg.ShowDialog($win)) { return }
    $f = $dlg.FileName; $leaf = Split-Path $f -Leaf
    Set-Status "Kopierer $leaf til serveren…"; $win.Cursor = [System.Windows.Input.Cursors]::Wait
    New-Item -ItemType Directory -Force $P.Installers | Out-Null
    Copy-Item -LiteralPath $f (Join-Path $P.Installers $leaf) -Force
    $win.Cursor = $null
    $vi = (Get-Item -LiteralPath $f).VersionInfo
    $name = if ($vi.ProductName) { $vi.ProductName.Trim() } else { [IO.Path]::GetFileNameWithoutExtension($leaf) }
    $silent = if ($leaf -like '*.msi') { '/qn /norestart' } else { '' }
    $sel = $ui.AppsGrid.SelectedItem
    if ($sel -and $sel.Row.Type -eq 'file' -and -not $sel.Row.Id) {
        $sel.Row.Id = $leaf; $sel.Row.Enabled = $true; $sel.Row.Status = 'Fil klar'
        if (-not $sel.Row.Args) { $sel.Row.Args = $silent }
        Set-Status "$leaf er sat på '$($sel.Row.Name)'. Skriv evt. tavse parametre i 'Parametre' og gem."
    } else {
        Add-AppRow $true $name 'file' $leaf $silent 'Skriv evt. tavse parametre (fx /S) i Parametre' $false
        Set-Status "$name er tilføjet. Skriv evt. tavse parametre i 'Parametre' og gem."
    }
})

$script:SkipIds = '^Microsoft\.(AppInstaller|UI\.Xaml|VCLibs|WindowsAppRuntime|DotNet\.Native|Edge|OneDrive|WindowsTerminal|WindowsInstallationAssistant)'
$ui.ImportPcBtn.Add_Click({
    Set-Status 'Læser installerede programmer (winget export)…'; $win.Cursor = [System.Windows.Input.Cursors]::Wait
    $tmp = Join-Path $env:TEMP 'winsetup-export.json'
    winget.exe export -o $tmp --accept-source-agreements --disable-interactivity | Out-Null
    $win.Cursor = $null
    $j = Read-Json $tmp
    if (-not $j) { Show-Msg 'winget export gav intet resultat.' 'Warning'; return }
    $added = 0
    foreach ($src in $j.Sources) {
        $type = if ($src.SourceDetails.Name -eq 'msstore') { 'msstore' } else { 'winget' }
        foreach ($pkg in $src.Packages) {
            $id = $pkg.PackageIdentifier
            if ($id -match $SkipIds -or (Test-HasId $id)) { continue }
            Add-AppRow $true $id $type $id '' 'Tilføjet fra pc – ret gerne navnet' $false; $added++
        }
    }
    Set-Status "$added nye programmer tilføjet. Husk at gemme."
})

function Find-Winget([string]$Query) {
    try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch {}
    $lines = @(winget.exe search $Query --accept-source-agreements --disable-interactivity 2>$null | ForEach-Object { ($_ -split "`r")[-1] })
    $sep = -1
    for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i] -match '^-{5,}') { $sep = $i; break } }
    if ($sep -lt 1) { return @() }
    $starts = @([regex]::Matches($lines[$sep - 1], '\S+') | ForEach-Object Index)
    if ($starts.Count -lt 3) { return @() }
    foreach ($l in $lines[($sep + 1)..($lines.Count - 1)]) {
        if (-not $l -or $l.Length -lt $starts[2]) { continue }
        $src = 'winget'
        if ($starts.Count -ge 4 -and $l.Length -gt $starts[-1]) { $s = $l.Substring($starts[-1]).Trim(); if ($s) { $src = $s } }
        [pscustomobject]@{
            Name   = $l.Substring(0, $starts[1]).Trim()
            Id     = $l.Substring($starts[1], $starts[2] - $starts[1]).Trim()
            Source = $src
        }
    }
}
$doSearch = {
    $q = $ui.SearchBox.Text.Trim(); if (-not $q) { return }
    Set-Status "Søger efter '$q'…"; $win.Cursor = [System.Windows.Input.Cursors]::Wait
    $win.Dispatcher.Invoke([action]{}, 'Render')
    $res = @(Find-Winget $q)
    $win.Cursor = $null
    $ui.SearchList.ItemsSource = $res
    Set-Status "$($res.Count) resultater for '$q'. Markér og klik 'Tilføj valgte til listen'."
}
$ui.SearchBtn.Add_Click($doSearch)
$ui.SearchBox.Add_KeyDown({ if ($_.Key -eq 'Return') { & $doSearch } })
$ui.AddSearchBtn.Add_Click({
    $added = 0
    foreach ($it in @($ui.SearchList.SelectedItems)) {
        if ($it.Id -like '*…') { Show-Msg "Id'et for '$($it.Name)' er forkortet af winget. Søg mere præcist." 'Warning'; continue }
        if (Test-HasId $it.Id) { continue }
        $type = if ($it.Source -match 'msstore') { 'msstore' } else { 'winget' }
        Add-AppRow $true $it.Name $type $it.Id '' '' $false; $added++
    }
    Set-Status "$added program(mer) tilføjet. Husk at gemme."
})

# ---------------------------------------------------------------- baggrund og opsætning
function Update-AppearanceView {
    $m = Get-AppearanceMeta
    $file = if ($m.WallpaperFile) { Join-Path $P.Settings $m.WallpaperFile } else { $null }
    if ($file -and (Test-Path -LiteralPath $file)) {
        try {
            $bmp = New-Object Windows.Media.Imaging.BitmapImage
            $bmp.BeginInit(); $bmp.CacheOption = 'OnLoad'; $bmp.DecodePixelWidth = 1000
            $bmp.UriSource = New-Object Uri $file; $bmp.EndInit()
            $ui.WallpaperImg.Source = $bmp
        } catch { $ui.WallpaperImg.Source = $null }
        $style = @{ '0' = 'Centreret/fliser'; '2' = 'Stræk'; '6' = 'Tilpas'; '10' = 'Udfyld'; '22' = 'Spænd over skærme' }["$($m.WallpaperStyle)"]
        $ui.WallpaperInfo.Text = "Gemt $($m.Captured) fra $($m.CapturedFrom)   ·   Placering: $style"
    } else {
        $ui.WallpaperImg.Source = $null
        $ui.WallpaperInfo.Text = "Der er ingen opsætning gemt endnu. Klik 'Hent baggrund og opsætning fra denne pc'."
    }
    $script:LoadingLock = $true; $ui.LockScreenChk.IsChecked = [bool]$m.LockScreen; $script:LoadingLock = $false
    if (Test-Path $P.Drivers) {
        $cnt = @(Get-ChildItem $P.Drivers -Directory).Count
        $ui.DriversInfo.Text = "Gemte drivere: $cnt (senest $((Get-Item $P.Drivers).LastWriteTime.ToString('yyyy-MM-dd HH:mm')))"
    } else { $ui.DriversInfo.Text = 'Gemte drivere: ingen endnu' }
}
$ui.LockScreenChk.Add_Click({ if (-not $LoadingLock) { Set-AppearanceMeta @{ LockScreen = [bool]$ui.LockScreenChk.IsChecked } } })

$ui.ChooseWpBtn.Add_Click({
    $dlg = New-Object Microsoft.Win32.OpenFileDialog
    $dlg.Filter = 'Billeder (*.jpg;*.jpeg;*.png;*.bmp)|*.jpg;*.jpeg;*.png;*.bmp'
    if (-not $dlg.ShowDialog($win)) { return }
    $m = Get-AppearanceMeta
    Set-SavedWallpaper $dlg.FileName "$($m.WallpaperStyle)" "$($m.TileWallpaper)"
    if (-not $m.Captured) { Set-AppearanceMeta @{ Captured = (Get-Date).ToString('yyyy-MM-dd HH:mm'); CapturedFrom = $env:COMPUTERNAME } }
    Update-AppearanceView
    if ((Show-Msg 'Baggrunden er gemt. Vil du også bruge den på denne pc nu?' 'Question' 'YesNo') -eq 'Yes') {
        $m = Get-AppearanceMeta
        $style = if ($m.WallpaperStyle) { "$($m.WallpaperStyle)" } else { '10' }
        Set-DesktopWallpaper $dlg.FileName $style '0'
    }
})

# ---------------------------------------------------------------- filer og indstillinger
$script:Cfg = Get-Config
foreach ($f in $Cfg.UserFolders) {
    $cb = New-Object System.Windows.Controls.CheckBox
    $cb.Content = "$($f.Label)   ($(Get-UserFolder $f.Name))"; $cb.IsChecked = [bool]$f.Enabled; $cb.Tag = $f; $cb.Margin = '0,3'
    [void]$ui.FoldersPanel.Children.Add($cb)
}
foreach ($a in $Cfg.AppSettings) {
    $cb = New-Object System.Windows.Controls.CheckBox
    $where = if ($a.Registry) { $a.Registry } else { $a.Path }
    $cb.Content = "$($a.Name)   ($where)"; $cb.IsChecked = [bool]$a.Enabled; $cb.Tag = $a; $cb.Margin = '0,3'
    [void]$ui.AppSetPanel.Children.Add($cb)
}
function Save-BackupChoices {
    foreach ($cb in @($ui.FoldersPanel.Children) + @($ui.AppSetPanel.Children)) { $cb.Tag.Enabled = [bool]$cb.IsChecked }
    Save-Json $Cfg $P.Config
}
function Update-BackupView {
    $b = Read-Json (Join-Path (Get-BackupDir) 'backup.json')
    $ui.LastBackupText.Text = if ($b) { "Seneste backup: $($b.Dato) fra $($b.Computer)" } else { 'Der er ingen backup endnu.' }
}

# ---------------------------------------------------------------- kør trin i eget vindue
$script:RunProc = $null; $script:RunDone = $null
$script:Timer = New-Object System.Windows.Threading.DispatcherTimer
$Timer.Interval = [TimeSpan]::FromSeconds(1)
$Timer.Add_Tick({
    if ($RunProc -and $RunProc.HasExited) {
        $Timer.Stop(); $script:RunProc = $null
        Set-Status 'Færdig. Se loggen i mappen Logs hvis noget gik galt.'
        if ($RunDone) { & $RunDone }
    }
})
function Start-RunWindow([string[]]$Steps, [scriptblock]$OnDone) {
    if ($RunProc) { Show-Msg 'Der kører allerede et job. Vent til det er færdigt.' 'Warning'; return }
    $run = Join-Path $Root 'Run.ps1'
    $script:RunProc = Start-Process powershell.exe -PassThru -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$run`" -Do $($Steps -join ',')"
    $script:RunDone = $OnDone
    Set-Status "Kører: $($Steps -join ', ') (følg med i det andet vindue)…"
    $Timer.Start()
}

$ui.CaptureBtn.Add_Click({
    $steps = @('Capture'); if ($ui.DriversChk.IsChecked) { $steps += 'CaptureDrivers' }
    Start-RunWindow $steps { Update-AppearanceView }
})
$ui.ApplyBtn.Add_Click({
    if ((Show-Msg 'Læg den gemte baggrund og opsætning på denne pc? Stifinder genstarter undervejs.' 'Question' 'YesNo') -eq 'Yes') {
        Start-RunWindow @('Appearance') $null
    }
})
$ui.BackupBtn.Add_Click({ Save-BackupChoices; Start-RunWindow @('Backup') { Update-BackupView } })
$ui.RestoreBtn.Add_Click({
    if ((Show-Msg "Kopiér filer og indstillinger fra backup tilbage til denne pc?`n`nNyere filer på pc'en bliver ikke overskrevet." 'Question' 'YesNo') -eq 'Yes') {
        Save-BackupChoices; Start-RunWindow @('Restore') $null
    }
})
$ui.InstallBtn.Add_Click({
    $steps = @()
    if ($ui.ChkDrivers.IsChecked)    { $steps += 'Drivers' }
    if ($ui.ChkApps.IsChecked)       { $steps += 'Apps' }
    if ($ui.ChkRestore.IsChecked)    { $steps += 'Restore' }
    if ($ui.ChkAppearance.IsChecked) { $steps += 'Appearance' }
    if (-not $steps) { Show-Msg 'Vælg mindst ét trin.' 'Warning'; return }
    if ($Dirty) { Save-AppTable }
    Save-BackupChoices
    Start-RunWindow $steps $null
})
$ui.UsbBtn.Add_Click({
    $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
    $dlg.Description = 'Vælg Windows-USB-nøglen (drevets rod, fx E:\)'
    if ($dlg.ShowDialog() -ne 'OK') { return }
    try {
        $win.Cursor = [System.Windows.Input.Cursors]::Wait
        $res = Initialize-UsbStick $dlg.SelectedPath
        $win.Cursor = $null
        $agentTxt = if ($res.Agent) { "Ja – pc'en tilmelder sig $($res.Server) under installationen" } else { 'Nej (start Fleetdeck-serveren først, så kommer agenten med)' }
        Show-Msg "USB-nøglen er klar.`n`nNetkort-drivere lagt med: $($res.NetDrivers)`nFleetdeck-agent: $agentTxt`n`nBoot pc'en fra nøglen (F11 på MSI-bundkort). Installationen stopper kun ved valg af disk."
    } catch { $win.Cursor = $null; Show-Msg "$_" 'Error' }
})

$win.Add_Closing({
    if ($Dirty) {
        $r = Show-Msg 'Gem ændringerne i programlisten?' 'Question' 'YesNoCancel'
        if ($r -eq 'Cancel') { $_.Cancel = $true; return }
        if ($r -eq 'Yes') { Save-AppTable }
    }
})

Update-AppearanceView
Update-BackupView
[void]$win.ShowDialog()
