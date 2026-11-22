namespace Fleetdeck.Server.Services;

/// <summary>
/// "Gør klar til Wake-on-LAN" på Windows: PowerShell-skripter, der køres som et almindeligt script-job (SYSTEM).
/// Før ændringen gemmes de gamle værdier i C:\ProgramData\Fleetdeck\wol-backup, så "Fortryd" kan sætte dem tilbage.
/// Sætter på hvert kablet netkort: Wake on Magic Packet = Enabled, Enable PME = Enabled, Wake on Link Settings = Disabled
/// (Forced vækker pc'en ved hver link-ændring), "Tillad at enheden vækker computeren", og slår Hurtig start fra.
/// </summary>
public static class WolReady
{
    public const string PrepareTitle = "Gør klar til Wake-on-LAN", RestoreTitle = "Fortryd Wake-on-LAN-opsætning", TestTitle = "Test af Wake-on-LAN";

    /// <summary>BIOS/UEFI-indstillingerne, som Fleetdeck ikke kan sætte selv (navnene varierer lidt mellem bundkort).</summary>
    public static readonly (string Name, string Value, string Why)[] Bios =
    [
        ("ErP / EuP Ready", "Off", "Ellers slukkes netkortet helt, når pc'en er slukket, og Wake-on-LAN kan ikke vække den."),
        ("Restore on AC Power Loss", "Power On", "Så starter pc'en selv efter et strømsvigt – og når Fleetdeck slukker og tænder strømmen."),
        ("Wake on LAN / Resume by PCI-E / Power On By PCI-E", "On / Enabled", "Lader netkortet tænde pc'en, når der kommer et magic packet."),
    ];

    /// <summary>Ny linje i jobbets output med BIOS-påmindelsen.</summary>
    public static string BiosText() =>
        "Husk i BIOS/UEFI (kan ikke sættes fra Windows):\n" + string.Join("\n", Bios.Select(b => $"  {b.Name} = {b.Value}"));

    public static string PrepareScript() => Common + """
        $dir = Join-Path $env:ProgramData 'Fleetdeck\wol-backup'
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        $adapters = @(Get-NetAdapter -Physical -ErrorAction SilentlyContinue | Where-Object { $_.MediaType -eq '802.3' -and $_.Status -ne 'Disabled' })
        if ($adapters.Count -eq 0) { Write-Output 'Fandt intet kablet netkort. Wake-on-LAN kræver et netkort med kabel.'; exit 2 }
        $hb = (Get-ItemProperty -Path $hbKey -Name HiberbootEnabled -ErrorAction SilentlyContinue).HiberbootEnabled
        $armed = @(powercfg /devicequery wake_armed 2>$null)
        $backup = [ordered]@{ created = (Get-Date).ToString('o'); hiberboot = $hb; adapters = @() }
        $changed = 0
        foreach ($a in $adapters) {
          Write-Output "Netkort: $($a.Name) ($($a.InterfaceDescription))"
          $props = @(Get-NetAdapterAdvancedProperty -Name $a.Name -AllProperties -ErrorAction SilentlyContinue)
          $pm = Get-NetAdapterPowerManagement -Name $a.Name -ErrorAction SilentlyContinue
          $entry = [ordered]@{ name = $a.Name; description = $a.InterfaceDescription; props = @(); wakeMagic = "$($pm.WakeOnMagicPacket)"; deviceWake = ($armed -contains $a.InterfaceDescription) }
          foreach ($w in $want) {
            $p = Find-Prop $props $w
            if (-not $p) { Write-Output "  $($w.Names[0]): findes ikke på dette netkort – springes over."; continue }
            $old = "$($p.RegistryValue)"
            $entry.props += [ordered]@{ keyword = $p.RegistryKeyword; value = $old }
            if ($old -eq $w.Reg) { Write-Output "  $($w.Names[0]) = $($w.Label) (var allerede sat)"; continue }
            try { Set-NetAdapterAdvancedProperty -Name $a.Name -RegistryKeyword $p.RegistryKeyword -RegistryValue $w.Reg -NoRestart -ErrorAction Stop; $changed++
                  Write-Output "  $($w.Names[0]) = $($w.Label) (var $old)" }
            catch { Write-Output "  $($w.Names[0]): kunne ikke sættes – $($_.Exception.Message)" }
          }
          try { Set-NetAdapterPowerManagement -Name $a.Name -WakeOnMagicPacket Enabled -NoRestart -ErrorAction Stop } catch { }
          powercfg /deviceenablewake "$($a.InterfaceDescription)" 2>$null | Out-Null
          if ($LASTEXITCODE -eq 0) { Write-Output '  Tillad at enheden vækker computeren = til' } else { Write-Output '  Tillad at enheden vækker computeren: kunne ikke sættes (sæt den i Enhedshåndtering → Strømstyring)' }
          $backup.adapters += $entry
        }
        if ($hb -ne 0) { Set-ItemProperty -Path $hbKey -Name HiberbootEnabled -Value 0 -Type DWord; Write-Output 'Hurtig start = fra (var til)' } else { Write-Output 'Hurtig start = fra (var allerede fra)' }
        $file = Join-Path $dir ("wol-" + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.json')
        $backup | ConvertTo-Json -Depth 6 | Set-Content -Path $file -Encoding UTF8
        Write-Output "Backup af de gamle værdier: $file"
        if ($changed -gt 0) { Write-Output 'Netkortets nye indstillinger gælder fra næste genstart. Genstart pc''en, før du tester.' }
        """ + "\nWrite-Output ''\nWrite-Output @'\n" + BiosText() + "\n'@\nexit 0\n";

    public static string RestoreScript() => Common + """
        $dir = Join-Path $env:ProgramData 'Fleetdeck\wol-backup'
        $file = Get-ChildItem -Path $dir -Filter 'wol-*.json' -ErrorAction SilentlyContinue | Sort-Object Name -Descending | Select-Object -First 1
        if (-not $file) { Write-Output 'Der er ingen backup at fortryde fra.'; exit 2 }
        $b = Get-Content -Raw -Path $file.FullName | ConvertFrom-Json
        foreach ($e in @($b.adapters)) {
          Write-Output "Netkort: $($e.name)"
          foreach ($p in @($e.props)) {
            try { Set-NetAdapterAdvancedProperty -Name $e.name -RegistryKeyword $p.keyword -RegistryValue $p.value -NoRestart -ErrorAction Stop; Write-Output "  $($p.keyword) = $($p.value)" }
            catch { Write-Output "  $($p.keyword): kunne ikke sættes tilbage – $($_.Exception.Message)" }
          }
          if ($e.wakeMagic -in @('Enabled','Disabled')) { try { Set-NetAdapterPowerManagement -Name $e.name -WakeOnMagicPacket $e.wakeMagic -NoRestart -ErrorAction Stop } catch { } }
          if (-not $e.deviceWake) { powercfg /devicedisablewake "$($e.description)" 2>$null | Out-Null; Write-Output '  Tillad at enheden vækker computeren = fra' }
        }
        if ($null -ne $b.hiberboot) { Set-ItemProperty -Path $hbKey -Name HiberbootEnabled -Value ([int]$b.hiberboot) -Type DWord; Write-Output "Hurtig start = $(if ([int]$b.hiberboot -eq 1) { 'til' } else { 'fra' })" }
        Rename-Item -Path $file.FullName -NewName ($file.Name + '.fortrudt')
        Write-Output "Sat tilbage fra $($file.Name). Gælder fra næste genstart."
        exit 0
        """;

    private const string Common = """
        $ErrorActionPreference = 'Continue'
        [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
        $hbKey = 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Power'
        # driverens registreringsnøgle først (ens på alle sprog), ellers det viste navn (Intel bruger engelske navne)
        $want = @(
          @{ Keys = @('*WakeOnMagicPacket'); Names = @('Wake on Magic Packet'); Reg = '1'; Label = 'Enabled' },
          @{ Keys = @('EnablePME', '*EnablePME'); Names = @('Enable PME'); Reg = '1'; Label = 'Enabled' },
          @{ Keys = @('WakeOnLink', '*WakeOnLink'); Names = @('Wake on Link Settings', 'Wake on Link'); Reg = '0'; Label = 'Disabled' }
        )
        function Find-Prop($props, $w) {
          $p = $props | Where-Object { $w.Keys -contains $_.RegistryKeyword } | Select-Object -First 1
          if (-not $p) { $p = $props | Where-Object { $w.Names -contains $_.DisplayName } | Select-Object -First 1 }
          return $p
        }

        """;
}
