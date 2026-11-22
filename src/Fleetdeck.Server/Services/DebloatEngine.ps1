# Fleetdeck: debloat og privatliv (Windows 10/11). Kører som SYSTEM i Windows PowerShell 5.1 via et almindeligt script-job.
# Serveren sætter $plan ind foran dette script (se DebloatScript.Build). Handlinger:
#   detect – læs tilstanden (rører intet; kun indloggede brugeres profiler + standardprofilen)
#   apply  – gem før-værdier, anvend, læs tilstanden igen
#   undo   – sæt før-værdierne tilbage (kun hvor værdien stadig er Fleetdecks), læs tilstanden igen
# Sidste linje er en maskinlæsbar rapport: "##FLEETDECK-DEBLOAT## {json}".
# Registreringsnøgler, app-lister, strømplaner og Windows-funktioner: se DebloatCatalog.cs (kilder: Raphire/Win11Debloat og ChrisTitusTech/winutil, MIT).

$ErrorActionPreference = 'Continue'
# PowerShell 5.1 skriver ellers arrays som {"value":[…],"Count":n} i ConvertTo-Json
Remove-TypeData System.Array -ErrorAction SilentlyContinue
$action = [string]$plan.action
# Skriv direkte til konsollen: så kan beskeder aldrig blive en del af en funktions returværdi
function Say([string]$t) { [Console]::Out.WriteLine($t) }

# ------------------------------------------------------------------ pc'en
$cv = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -ErrorAction SilentlyContinue
$build = 0; [void][int]::TryParse([string]$cv.CurrentBuildNumber, [ref]$build)
$editionId = [string]$cv.EditionID
$edition = if ($editionId -like 'Core*') { 'home' } elseif ($editionId -like 'Professional*') { 'pro' }
           elseif ($editionId -match '^(Enterprise|Education|IoTEnterprise|ServerRdsh)') { 'enterprise' } else { 'other' }
$productType = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\ProductOptions' -ErrorAction SilentlyContinue).ProductType
$isServer = $productType -and $productType -ne 'WinNT'

$HKLM = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)
$HKU = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::Users, [Microsoft.Win32.RegistryView]::Registry64)

# ------------------------------------------------------------------ resultater pr. punkt
$R = [ordered]@{}
foreach ($it in $plan.items) {
    $R[$it.id] = @{ id = $it.id; title = [string]$it.title; label = [string]$it.label; notLabel = [string]$it.notLabel; sat = 0; total = 0; changed = 0; skipped = 0; na = $false
        missing = 0; reboot = $false; ext = (New-Object System.Collections.ArrayList)
        errors = (New-Object System.Collections.ArrayList); notes = (New-Object System.Collections.ArrayList)
        reg = (New-Object System.Collections.ArrayList); svc = (New-Object System.Collections.ArrayList)
        task = (New-Object System.Collections.ArrayList); appx = (New-Object System.Collections.ArrayList); hv = [ordered]@{} }
}
function Err($id, [string]$t) { [void]$R[$id].errors.Add($t) }
function Tally($id, [bool]$ok) { $R[$id].total++; if ($ok) { $R[$id].sat++ } }

function Applies($min, $max) { ((-not $min) -or $build -ge $min) -and ((-not $max) -or $build -le $max) }
$items = @()
foreach ($it in $plan.items) {
    $ok = (-not $isServer) -and (Applies $it.minBuild $it.maxBuild) -and ((-not $it.editions) -or (@($it.editions) -contains $edition))
    if ($ok) { $items += $it } else { $R[$it.id].na = $true }
}

# ------------------------------------------------------------------ gendannelsespunkt
$restore = ''
if ($action -ne 'detect' -and $plan.restorePoint -and $items.Count -gt 0) {
    $srKey = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore'
    $oldFreq = (Get-ItemProperty $srKey -Name SystemRestorePointCreationFrequency -ErrorAction SilentlyContinue).SystemRestorePointCreationFrequency
    try {
        # Windows laver højst ét punkt i døgnet; tillad ét nu og sæt grænsen tilbage bagefter
        Set-ItemProperty $srKey -Name SystemRestorePointCreationFrequency -Value 0 -Type DWord -ErrorAction SilentlyContinue
        Checkpoint-Computer -Description 'Fleetdeck: før debloat og privatliv' -RestorePointType MODIFY_SETTINGS -ErrorAction Stop
        $restore = 'created'
        Say 'Gendannelsespunkt oprettet.'
    } catch {
        $restore = 'failed: ' + $_.Exception.Message
        Say "Intet gendannelsespunkt (systemgendannelse er måske slået fra): $($_.Exception.Message)"
    } finally {
        if ($null -eq $oldFreq) { Remove-ItemProperty $srKey -Name SystemRestorePointCreationFrequency -ErrorAction SilentlyContinue }
        else { Set-ItemProperty $srKey -Name SystemRestorePointCreationFrequency -Value $oldFreq -Type DWord -ErrorAction SilentlyContinue }
    }
}

# ------------------------------------------------------------------ brugerprofiler (HKCU for alle + standardprofilen)
$hives = New-Object System.Collections.ArrayList
$mounted = New-Object System.Collections.ArrayList
$plKey = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList'
function Mount([string]$id, [string]$dat) {
    if (-not (Test-Path -LiteralPath $dat)) { return $null }
    $name = 'FD_' + ($id -replace '[^A-Za-z0-9]', '_')
    if ($HKU.GetSubKeyNames() -contains $name) { $null = & reg.exe unload "HKU\$name" 2>&1 }   # rester fra et afbrudt job
    $out = & reg.exe load "HKU\$name" "$dat" 2>&1
    if ($LASTEXITCODE -ne 0) { Say "Kunne ikke indlæse profilen $id ($out)"; return $null }
    [void]$mounted.Add($name); return $name
}
$needUsers = @($items | Where-Object { @($_.reg | Where-Object { $_.hive -ne 'HKLM' }).Count -gt 0 }).Count -gt 0
if ($needUsers) {
    foreach ($p in @(Get-ChildItem $plKey -ErrorAction SilentlyContinue)) {
        $sid = $p.PSChildName
        if ($sid -notmatch '^S-1-(5-21|12-1)-[\d-]+$') { continue }   # lokale konti, Microsoft-konti og Entra ID
        $img = [Environment]::ExpandEnvironmentVariables([string](Get-ItemProperty $p.PSPath -ErrorAction SilentlyContinue).ProfileImagePath)
        $uname = if ($img) { Split-Path $img -Leaf } else { $sid }   # profilmappens navn, til visning pr. bruger
        if ($HKU.GetSubKeyNames() -contains $sid) { [void]$hives.Add(@{ id = $sid; sub = $sid; kind = 'user'; name = $uname }); continue }
        if (-not $plan.loadOffline) { continue }   # tjek: rør ikke profiler for brugere, der ikke er logget ind
        if (-not $img) { continue }
        $m = Mount $sid (Join-Path $img 'NTUSER.DAT')
        if ($m) { [void]$hives.Add(@{ id = $sid; sub = $m; kind = 'user'; name = $uname }) }
    }
    $defDir = [Environment]::ExpandEnvironmentVariables([string](Get-ItemProperty $plKey -ErrorAction SilentlyContinue).Default)
    if (-not $defDir) { $defDir = "$env:SystemDrive\Users\Default" }
    $m = Mount 'DEFAULT' (Join-Path $defDir 'NTUSER.DAT')
    if ($m) { [void]$hives.Add(@{ id = 'DEFAULT'; sub = $m; kind = 'default'; name = 'DEFAULT' }) }
}
# brugernes klasser (HKCU\Software\Classes) ligger i UsrClass.dat og ikke i NTUSER.DAT
$classHives = New-Object System.Collections.ArrayList
if (@($items | Where-Object { @($_.reg | Where-Object { $_.hive -eq 'CLASSES' }).Count -gt 0 }).Count -gt 0) {
    foreach ($h in @($hives | Where-Object { $_.kind -eq 'user' })) {
        $live = "$($h.id)_Classes"
        if ($HKU.GetSubKeyNames() -contains $live) { [void]$classHives.Add(@{ id = $h.id; sub = $live; kind = 'user'; name = $h.name }); continue }
        if (-not $plan.loadOffline) { continue }
        $img = [Environment]::ExpandEnvironmentVariables([string](Get-ItemProperty "$plKey\$($h.id)" -ErrorAction SilentlyContinue).ProfileImagePath)
        if (-not $img) { continue }
        $m = Mount "$($h.id)-C" (Join-Path $img 'AppData\Local\Microsoft\Windows\UsrClass.dat')
        if ($m) { [void]$classHives.Add(@{ id = $h.id; sub = $m; kind = 'user'; name = $h.name }) }
    }
}

# ------------------------------------------------------------------ registrering
function Base($h) { if ($h.id -eq 'HKLM') { return $HKLM, '' } else { return $HKU, ($h.sub + '\') } }
function ReadReg($h, [string]$path, [string]$name) {
    $b, $pre = Base $h
    $k = $b.OpenSubKey($pre + $path, $false)
    if ($null -eq $k) { return @{ e = $false; ke = $false } }
    try {
        if (-not ($k.GetValueNames() -contains $name)) { return @{ e = $false; ke = $true } }
        $t = $k.GetValueKind($name).ToString()
        $v = $k.GetValue($name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        if ($t -eq 'Binary') { $v = [Convert]::ToBase64String([byte[]]$v) }
        return @{ e = $true; ke = $true; t = $t; v = $v }
    } finally { $k.Close() }
}
function WriteReg($h, [string]$path, [string]$name, [string]$kind, $value) {
    $b, $pre = Base $h
    $k = $b.CreateSubKey($pre + $path, $true)
    try {
        switch ($kind.ToLowerInvariant()) {
            'dword' { $k.SetValue($name, [int]$value, [Microsoft.Win32.RegistryValueKind]::DWord) }
            'qword' { $k.SetValue($name, [long]$value, [Microsoft.Win32.RegistryValueKind]::QWord) }
            'expandstring' { $k.SetValue($name, [string]$value, [Microsoft.Win32.RegistryValueKind]::ExpandString) }
            'multistring' { $k.SetValue($name, [string[]]@($value), [Microsoft.Win32.RegistryValueKind]::MultiString) }
            'binary' { $k.SetValue($name, [Convert]::FromBase64String([string]$value), [Microsoft.Win32.RegistryValueKind]::Binary) }
            default { $k.SetValue($name, [string]$value, [Microsoft.Win32.RegistryValueKind]::String) }
        }
    } finally { $k.Close() }
}
function DeleteReg($h, [string]$path, [string]$name, [bool]$dropEmptyKey) {
    $b, $pre = Base $h
    $k = $b.OpenSubKey($pre + $path, $true)
    if ($null -eq $k) { return }
    try { $k.DeleteValue($name, $false); $empty = $k.ValueCount -eq 0 -and $k.SubKeyCount -eq 0 } finally { $k.Close() }
    if (-not ($dropEmptyKey -and $empty)) { return }
    try { $b.DeleteSubKey($pre + $path, $false) } catch { return }
    # tomme CLSID-nøgler over den fjernede nøgle må heller ikke blive liggende: de kan skygge for den rigtige registrering
    $p = $path
    while ($p.Contains('\')) {
        $p = $p.Substring(0, $p.LastIndexOf('\'))
        if ($p.Substring($p.LastIndexOf('\') + 1) -notlike '{*') { break }
        $k = $b.OpenSubKey($pre + $p, $false)
        if ($null -eq $k) { continue }
        try { $empty = $k.ValueCount -eq 0 -and $k.SubKeyCount -eq 0 } finally { $k.Close() }
        if (-not $empty) { break }
        try { $b.DeleteSubKey($pre + $p, $false) } catch { break }
    }
}
function IsTarget($cur, $op) {
    if ($null -eq $op.value) { return -not $cur.e }
    if (-not $cur.e) { return $false }
    if ($op.bits) { return $cur.t -eq 'DWord' -and (([long]$cur.v -band [long]$op.value) -eq [long]$op.value) }
    if ($op.kind -eq 'dword') { return $cur.t -eq 'DWord' -and [long]$cur.v -eq [long]$op.value }
    return $cur.t -in 'String', 'ExpandString' -and [string]$cur.v -eq [string]$op.value
}
function BeforeFor($op, [string]$hiveId) {
    if ($null -eq $op.before) { return $null }
    $p = $op.before.PSObject.Properties[$hiveId]
    if ($p) { return $p.Value } else { return $null }
}

try {
foreach ($it in $items) {
    foreach ($op in @($it.reg)) {
        if ($null -eq $op -or -not (Applies $op.min $op.max)) { continue }
        if ($op.hive -eq 'HKLM') { $targets = @(@{ id = 'HKLM' }) }
        elseif ($op.hive -eq 'DEFAULT') { $targets = @($hives | Where-Object { $_.kind -eq 'default' }) }
        elseif ($op.hive -eq 'LOGON') { $targets = @(@{ id = 'LOGON'; sub = '.DEFAULT'; kind = 'logon'; name = 'Loginskærm' }) }
        elseif ($op.hive -eq 'CLASSES') { $targets = @($classHives); if ($targets.Count -eq 0) { $R[$it.id].missing++ } }   # ingen bruger at tjekke
        else { $targets = @($hives) }
        foreach ($h in $targets) {
            try {
                $cur = ReadReg $h $op.path $op.name
                if ($action -eq 'apply') {
                    [void]$R[$it.id].reg.Add(@{ k = $op.key; h = $h.id; e = $cur.e; ke = $cur.ke; t = $cur.t; v = $cur.v })
                    if (-not (IsTarget $cur $op)) {
                        if ($null -eq $op.value) { DeleteReg $h $op.path $op.name $false }
                        elseif ($op.bits -and $cur.e -and $cur.t -eq 'DWord') { WriteReg $h $op.path $op.name $op.kind ([long]$cur.v -bor [long]$op.value) }
                        else { WriteReg $h $op.path $op.name $op.kind $op.value }
                        $R[$it.id].changed++
                    }
                } elseif ($action -eq 'undo') {
                    if (IsTarget $cur $op) {
                        # kun hvis værdien stadig er Fleetdecks – ellers har nogen ændret den siden, og så får den lov at blive
                        $b = BeforeFor $op $h.id
                        if ($b) {
                            if ($b.e) { WriteReg $h $op.path $op.name ([string]$b.t) $b.v } else { DeleteReg $h $op.path $op.name (-not $b.ke) }
                        } elseif ($op.bits) { WriteReg $h $op.path $op.name $op.kind ([long]$cur.v -band (-bnot [long]$op.value)) }
                        elseif ($null -ne $op.def) { WriteReg $h $op.path $op.name $op.kind $op.def }
                        elseif ($null -ne $op.value) { DeleteReg $h $op.path $op.name $false }
                        $R[$it.id].changed++
                    } else { $R[$it.id].skipped++ }
                }
                $okNow = IsTarget (ReadReg $h $op.path $op.name) $op
                Tally $it.id $okNow
                if ($h.id -ne 'HKLM') {   # status pr. brugerprofil: ok når alle værdier i profilen er på plads
                    if (-not $R[$it.id].hv.Contains($h.id)) { $R[$it.id].hv[$h.id] = @{ h = $h.id; n = $h.name; ok = $true } }
                    if (-not $okNow) { $R[$it.id].hv[$h.id].ok = $false }
                }
            } catch { Err $it.id "$($op.name) ($($h.id)): $($_.Exception.Message)"; Tally $it.id $false }
        }
    }
}

} finally {
# indlæste profiler lukkes igen med det samme (også ved fejl), så en bruger, der logger ind, ikke får en midlertidig profil
foreach ($m in $mounted) {
    [gc]::Collect(); [gc]::WaitForPendingFinalizers()
    for ($i = 0; $i -lt 5; $i++) {
        $null = & reg.exe unload "HKU\$m" 2>&1
        if ($LASTEXITCODE -eq 0) { break }
        Start-Sleep -Milliseconds 500
    }
    if ($LASTEXITCODE -ne 0) { Say "Advarsel: profilen $m kunne ikke lukkes igen" }
}
}

# ------------------------------------------------------------------ tjenester
function SvcStart([string]$n) {
    $k = $HKLM.OpenSubKey("SYSTEM\CurrentControlSet\Services\$n", $false)
    if ($null -eq $k) { return $null }
    try { return $k.GetValue('Start') } finally { $k.Close() }
}
function SetSvcStart([string]$n, [int]$s) {
    try {
        $k = $HKLM.OpenSubKey("SYSTEM\CurrentControlSet\Services\$n", $true)
        try { $k.SetValue('Start', $s, [Microsoft.Win32.RegistryValueKind]::DWord) } finally { $k.Close() }
    } catch {
        $t = switch ($s) { 2 { 'Automatic' } 3 { 'Manual' } default { 'Disabled' } }
        Set-Service -Name $n -StartupType $t -ErrorAction Stop
    }
}
foreach ($it in $items) {
    foreach ($s in @($it.svc)) {
        if ($null -eq $s) { continue }
        try {
            $cur = SvcStart $s.name
            if ($null -eq $cur) { continue }   # tjenesten findes ikke på denne pc
            if ($action -eq 'apply') {
                [void]$R[$it.id].svc.Add(@{ name = $s.name; start = $cur })
                if ($cur -ne $s.start) { SetSvcStart $s.name $s.start; $R[$it.id].changed++ }
                if ($s.start -eq 4) { Stop-Service -Name $s.name -Force -ErrorAction SilentlyContinue }
            } elseif ($action -eq 'undo') {
                if ($cur -eq $s.start) {
                    $to = if ($null -ne $s.before) { [int]$s.before } else { [int]$s.def }
                    SetSvcStart $s.name $to; $R[$it.id].changed++
                    if ($to -eq 2) { Start-Service -Name $s.name -ErrorAction SilentlyContinue }
                } else { $R[$it.id].skipped++ }
            }
            Tally $it.id ((SvcStart $s.name) -eq $s.start)
        } catch { Err $it.id "Tjenesten $($s.name): $($_.Exception.Message)"; Tally $it.id $false }
    }
}

# ------------------------------------------------------------------ planlagte opgaver
foreach ($it in $items) {
    foreach ($t in @($it.task)) {
        if ($null -eq $t) { continue }
        try {
            $task = Get-ScheduledTask -TaskPath $t.path -TaskName $t.name -ErrorAction SilentlyContinue
            if ($null -eq $task) { continue }   # opgaven findes ikke i denne Windows-version
            $enabled = $task.State -ne 'Disabled'
            if ($action -eq 'apply') {
                [void]$R[$it.id].task.Add(@{ path = $t.path; name = $t.name; enabled = $enabled })
                if ($enabled) { Disable-ScheduledTask -TaskPath $t.path -TaskName $t.name -ErrorAction Stop | Out-Null; $R[$it.id].changed++ }
            } elseif ($action -eq 'undo') {
                if (-not $enabled -and $t.before -ne $false) { Enable-ScheduledTask -TaskPath $t.path -TaskName $t.name -ErrorAction Stop | Out-Null; $R[$it.id].changed++ }
            }
            $now = Get-ScheduledTask -TaskPath $t.path -TaskName $t.name -ErrorAction SilentlyContinue
            Tally $it.id ($now.State -eq 'Disabled')
        } catch { Err $it.id "Opgaven $($t.name): $($_.Exception.Message)"; Tally $it.id $false }
    }
}

# ------------------------------------------------------------------ medfølgende apps (AppX)
$protected = @($plan.protected)
function IsProtected([string]$n) { foreach ($p in $protected) { if ($n -like $p) { return $true } }; return $false }
$appxItems = @($items | Where-Object { @($_.appx | Where-Object { $_ }).Count -gt 0 })
$installed = @(); $provisioned = @(); $appxOk = $true
function LoadAppx {
    $script:installed = @(); $script:provisioned = @()
    try {
        $script:installed = @(Get-AppxPackage -AllUsers -ErrorAction Stop)
        $script:provisioned = @(Get-AppxProvisionedPackage -Online -ErrorAction Stop)
    } catch { $script:appxOk = $false; Say "Kunne ikke læse apps: $($_.Exception.Message)" }
}
# En post i standardbilledet, hvis filer er væk, er ikke længere i billedet (DISM kan blive ved med at vise den et stykke tid)
function ProvLive($p) {
    $loc = [Environment]::ExpandEnvironmentVariables([string]$p.InstallLocation)
    if (-not $loc) { return $true }
    return (Test-Path -LiteralPath $loc)
}
function Found([string]$pattern) {
    $i = @($script:installed | Where-Object { $_.Name -like $pattern -and -not $_.IsFramework -and -not $_.NonRemovable -and -not (IsProtected $_.Name) })
    $p = @($script:provisioned | Where-Object { $_.DisplayName -like $pattern -and -not (IsProtected $_.DisplayName) -and (ProvLive $_) })
    return , @($i, $p)
}
# "Filen/stien blev ikke fundet" ved fjernelse betyder, at pakken allerede er væk: det er målet, ikke en fejl
function IsGone($err) {
    $hr = '0x{0:X8}' -f $err.Exception.HResult
    return ("$($err.Exception.Message) $hr" -match [string]$plan.alreadyGone)
}
function ReloadProvisioned {
    try { $script:provisioned = @(Get-AppxProvisionedPackage -Online -ErrorAction Stop) }
    catch { Say "Kunne ikke læse standardbilledet: $($_.Exception.Message)" }
}
if ($appxItems.Count -gt 0) {
    LoadAppx
    if ($action -eq 'apply' -and $appxOk) {
        foreach ($it in $appxItems) {
            foreach ($a in @($it.appx)) {
                if (IsProtected $a.name) { Err $it.id "$($a.name) er beskyttet og fjernes ikke"; continue }
                $f = Found $a.name
                foreach ($pkg in $f[0]) {
                    try { Remove-AppxPackage -Package $pkg.PackageFullName -AllUsers -ErrorAction Stop; [void]$R[$it.id].appx.Add($pkg.Name); $R[$it.id].changed++ }
                    catch { if (-not (IsGone $_)) { Err $it.id "$($pkg.Name): $($_.Exception.Message)" } }
                }
                # -AllUsers fjerner på nye Windows-versioner også pakken fra standardbilledet: afregistrér kun det, der stadig er der
                if ($f[0].Count -gt 0) { ReloadProvisioned }
                foreach ($pkg in @((Found $a.name)[1])) {
                    try {
                        try { Remove-AppxProvisionedPackage -Online -PackageName $pkg.PackageName -AllUsers -ErrorAction Stop | Out-Null }
                        catch [System.Management.Automation.ParameterBindingException] { Remove-AppxProvisionedPackage -Online -PackageName $pkg.PackageName -ErrorAction Stop | Out-Null }
                        [void]$R[$it.id].appx.Add($pkg.DisplayName + ' (standardbillede)'); $R[$it.id].changed++
                    } catch { if (-not (IsGone $_)) { Err $it.id "$($pkg.DisplayName) (standardbillede): $($_.Exception.Message)" } }
                }
            }
        }
        LoadAppx
    }
    foreach ($it in $appxItems) {
        foreach ($a in @($it.appx)) {
            if (-not $appxOk) { Tally $it.id $false; continue }
            $f = Found $a.name
            Tally $it.id (($f[0].Count + $f[1].Count) -eq 0)
        }
        if ($action -eq 'undo') { [void]$R[$it.id].notes.Add('Apps installeres igen fra Microsoft Store i et separat job.') }
    }
}

# ------------------------------------------------------------------ strømplan og strømindstillinger
$guidRx = '[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}'
$script:plans = @()
function ReadPlans {
    $script:plans = @(foreach ($line in @(& powercfg.exe /list 2>$null)) {
        if ([string]$line -match "($guidRx)\s+\((.*)\)") { [pscustomobject]@{ guid = $Matches[1].ToLowerInvariant(); name = $Matches[2] } }
    })
}
function ActivePlan {
    $o = (& powercfg.exe /getactivescheme 2>$null | Out-String)
    if ($o -match "($guidRx)") { return $Matches[1].ToLowerInvariant() }
    return ''
}
function PlanHas([string]$g) { return @($script:plans | Where-Object { $_.guid -eq $g }).Count -gt 0 }
# Fleetdecks egne kopier fjernes igen, så snart de ikke er i brug
function DropCopies {
    ReadPlans
    $a = ActivePlan
    foreach ($c in @($plan.fleetPlans)) { if ($c -ne $a -and (PlanHas $c)) { $null = & powercfg.exe /delete $c 2>&1 } }
    ReadPlans
}
function SetPlan([string]$g) {
    $out = & powercfg.exe /setactive $g 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Strømplanen kunne ikke aktiveres: $out" }
}
function RunPlans {
    foreach ($it in @($items | Where-Object { $_.powerPlan })) {
        $pp = $it.powerPlan
        try {
            $active = ActivePlan
            if ($action -eq 'apply') {
                [void]$R[$it.id].ext.Add(@{ k = 'plan'; v = @{ active = $active } })
                $target = if (PlanHas $pp.base) { [string]$pp.base } elseif ($pp.copy -and (PlanHas $pp.copy)) { [string]$pp.copy } else { '' }
                if (-not $target) {
                    if (-not $pp.copy) { throw 'Strømplanen findes ikke på denne pc.' }
                    $out = & powercfg.exe /duplicatescheme $pp.base $pp.copy 2>&1
                    if ($LASTEXITCODE -ne 0) { throw "Strømplanen kunne ikke oprettes: $out" }
                    ReadPlans
                    $target = [string]$pp.copy
                }
                if ($active -ne $target) { SetPlan $target; $R[$it.id].changed++ }
                DropCopies
            } elseif ($action -eq 'undo') {
                if ($active -eq $pp.base -or $active -eq $pp.copy) {
                    $to = [string]$pp.before.active
                    if (-not $to -or -not (PlanHas $to)) { $to = [string]$plan.balancedPlan }
                    if ($to -ne $active) { SetPlan $to; $R[$it.id].changed++ }
                } else { $R[$it.id].skipped++ }
                DropCopies
            }
            $now = ActivePlan
            Tally $it.id ($now -eq $pp.base -or $now -eq $pp.copy)
        } catch { Err $it.id "Strømplan: $($_.Exception.Message)"; Tally $it.id $false }
    }
}
# De to sidste hex-tal i svaret er værdien på lysnet og på batteri (teksterne omkring dem afhænger af Windows' sprog)
function PwRead([string]$sub, [string]$set) {
    $o = @(& powercfg.exe /query SCHEME_CURRENT $sub $set 2>$null)
    if ($LASTEXITCODE -ne 0) { return $null }
    $m = [regex]::Matches(($o -join "`n"), '0x([0-9a-fA-F]{8})')
    if ($m.Count -lt 2) { return $null }
    return @{ ac = [Convert]::ToInt64($m[$m.Count - 2].Groups[1].Value, 16); dc = [Convert]::ToInt64($m[$m.Count - 1].Groups[1].Value, 16) }
}
function PwSet([string]$side, [string]$scheme, [string]$sub, [string]$set, $v) {
    $out = & powercfg.exe "/set$($side)valueindex" $scheme $sub $set ([long]$v) 2>&1
    if ($LASTEXITCODE -ne 0) { throw "powercfg: $out" }
}
function RunPower {
    $touched = $false
    foreach ($it in $items) {
        foreach ($op in @($it.power)) {
            if ($null -eq $op) { continue }
            try {
                $cur = PwRead $op.sub $op.setting
                if ($null -eq $cur) { $R[$it.id].missing++; continue }   # indstillingen findes ikke på denne pc
                if ($action -eq 'apply') {
                    [void]$R[$it.id].ext.Add(@{ k = $op.key; v = @{ scheme = (ActivePlan); ac = $cur.ac; dc = $cur.dc } })
                    if ($null -ne $op.ac -and $cur.ac -ne $op.ac) { PwSet 'ac' 'SCHEME_CURRENT' $op.sub $op.setting $op.ac; $R[$it.id].changed++; $touched = $true }
                    if ($null -ne $op.dc -and $cur.dc -ne $op.dc) { PwSet 'dc' 'SCHEME_CURRENT' $op.sub $op.setting $op.dc; $R[$it.id].changed++; $touched = $true }
                } elseif ($action -eq 'undo') {
                    $b = $op.before
                    if ($b) {
                        $scheme = if ($b.scheme -and (PlanHas $b.scheme)) { [string]$b.scheme } else { 'SCHEME_CURRENT' }
                        if ($null -ne $op.ac -and $cur.ac -eq $op.ac) { PwSet 'ac' $scheme $op.sub $op.setting $b.ac; $R[$it.id].changed++; $touched = $true }
                        if ($null -ne $op.dc -and $cur.dc -eq $op.dc) { PwSet 'dc' $scheme $op.sub $op.setting $b.dc; $R[$it.id].changed++; $touched = $true }
                    } else { $R[$it.id].skipped++ }
                }
                $now = PwRead $op.sub $op.setting
                Tally $it.id ((($null -eq $op.ac) -or $now.ac -eq $op.ac) -and (($null -eq $op.dc) -or $now.dc -eq $op.dc))
            } catch { Err $it.id "Strømindstilling: $($_.Exception.Message)"; Tally $it.id $false }
        }
    }
    if ($touched) { $null = & powercfg.exe /setactive SCHEME_CURRENT 2>&1 }
}
ReadPlans
if ($action -eq 'undo') { RunPower; RunPlans } else { RunPlans; RunPower }

# ------------------------------------------------------------------ dvaletilstand
function HibOn {
    $k = Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Power' -ErrorAction SilentlyContinue
    if ($null -ne $k.HibernateEnabled) { return [int]$k.HibernateEnabled -ne 0 }
    return ($null -eq $k.HibernateEnabledDefault) -or ([int]$k.HibernateEnabledDefault -ne 0)
}
foreach ($it in @($items | Where-Object { $_.hibernate })) {
    try {
        $on = HibOn
        if ($action -eq 'apply') {
            [void]$R[$it.id].ext.Add(@{ k = 'hibernate'; v = @{ on = $on } })
            if ($on) { $out = & powercfg.exe /hibernate off 2>&1; if ($LASTEXITCODE -ne 0) { throw "$out" }; $R[$it.id].changed++ }
        } elseif ($action -eq 'undo') {
            if (-not $on -and $it.hibernate.before.on) { $out = & powercfg.exe /hibernate on 2>&1; if ($LASTEXITCODE -ne 0) { throw "$out" }; $R[$it.id].changed++ }
            else { $R[$it.id].skipped++ }
        }
        Tally $it.id (-not (HibOn))
    } catch { Err $it.id "Dvaletilstand: $($_.Exception.Message)"; Tally $it.id $false }
}

# ------------------------------------------------------------------ Wake-on-LAN (slås kun til, aldrig fra: Fleetdeck vækker pc'en med det)
foreach ($it in @($items | Where-Object { $_.wol })) {
    try {
        $nics = @(Get-NetAdapter -Physical -ErrorAction Stop | Where-Object { $_.NdisPhysicalMedium -eq 14 })   # 14 = kablet (802.3)
        if ($nics.Count -eq 0) { $R[$it.id].missing++ }
        foreach ($n in $nics) {
            $pm = Get-NetAdapterPowerManagement -Name $n.Name -ErrorAction SilentlyContinue
            if ($null -eq $pm -or [string]$pm.WakeOnMagicPacket -eq 'Unsupported') { $R[$it.id].missing++; continue }
            $adv = Get-NetAdapterAdvancedProperty -Name $n.Name -RegistryKeyword '*WakeOnMagicPacket' -ErrorAction SilentlyContinue
            $ok = ([string]$pm.WakeOnMagicPacket -eq 'Enabled') -and ($null -eq $adv -or [string]$adv.RegistryValue -eq '1')
            if ($action -eq 'apply' -and -not $ok) {
                # -NoRestart: netkortet må ikke falde ud, mens jobbet kører
                Set-NetAdapterPowerManagement -Name $n.Name -WakeOnMagicPacket Enabled -NoRestart -ErrorAction Stop
                if ($adv) { Set-NetAdapterAdvancedProperty -Name $n.Name -RegistryKeyword '*WakeOnMagicPacket' -RegistryValue 1 -NoRestart -ErrorAction Stop }
                $null = & powercfg.exe /deviceenablewake "$($n.InterfaceDescription)" 2>&1
                $R[$it.id].changed++; $ok = $true
            }
            Tally $it.id $ok
        }
        if ($action -eq 'undo') { [void]$R[$it.id].notes.Add('Wake-on-LAN forbliver slået til, fordi Fleetdeck bruger det til at vække pc''en.') }
    } catch { Err $it.id "Wake-on-LAN: $($_.Exception.Message)"; Tally $it.id $false }
}

# ------------------------------------------------------------------ Windows-funktioner
$featureItems = @($items | Where-Object { @($_.feature | Where-Object { $_ }).Count -gt 0 })
if ($featureItems.Count -gt 0) {
    $fs = @{}
    try { foreach ($f in @(Get-WindowsOptionalFeature -Online -ErrorAction Stop)) { $fs[[string]$f.FeatureName] = [string]$f.State } }
    catch { Say "Kunne ikke læse Windows-funktioner: $($_.Exception.Message)" }
    foreach ($it in $featureItems) {
        foreach ($f in @($it.feature)) {
            try {
                if (-not $fs.ContainsKey([string]$f.name)) { $R[$it.id].missing++; continue }   # findes ikke i denne Windows-udgave
                $cur = $fs[[string]$f.name]
                $on = $cur -in 'Enabled', 'EnablePending'
                if ($action -eq 'apply') {
                    [void]$R[$it.id].ext.Add(@{ k = $f.key; v = @{ state = $cur } })
                    if (-not $on) {
                        $res = Enable-WindowsOptionalFeature -Online -FeatureName $f.name -All -NoRestart -ErrorAction Stop
                        if ($res.RestartNeeded) { $R[$it.id].reboot = $true }
                        $R[$it.id].changed++
                    }
                } elseif ($action -eq 'undo') {
                    # kun det, Fleetdeck selv slog til: var funktionen slået til i forvejen, bliver den
                    if ($on -and $f.before -and [string]$f.before.state -notin 'Enabled', 'EnablePending') {
                        $res = Disable-WindowsOptionalFeature -Online -FeatureName $f.name -NoRestart -ErrorAction Stop
                        if ($res.RestartNeeded) { $R[$it.id].reboot = $true }
                        $R[$it.id].changed++
                    } else { $R[$it.id].skipped++ }
                }
                $now = if ($action -eq 'detect') { $cur } else { [string](Get-WindowsOptionalFeature -Online -FeatureName $f.name -ErrorAction Stop).State }
                if ($now -in 'EnablePending', 'DisablePending') { $R[$it.id].reboot = $true }
                Tally $it.id ($now -in 'Enabled', 'EnablePending')
            } catch { Err $it.id "$($f.name): $($_.Exception.Message)"; Tally $it.id $false }
        }
    }
}
foreach ($it in $items) { if ($it.needsReboot -and $R[$it.id].changed -gt 0) { $R[$it.id].reboot = $true } }

# ------------------------------------------------------------------ rapport
$out = New-Object System.Collections.ArrayList
$failed = 0
foreach ($r in $R.Values) {
    # intet at gøre, fordi pc'en ikke har det (fx en Windows-funktion, som udgaven ikke har)
    if ($r.total -eq 0 -and $r.missing -gt 0 -and $r.errors.Count -eq 0) { $r.na = $true }
    $state = if ($r.na) { 'na' } elseif ($r.errors.Count -gt 0 -and $r.sat -eq 0) { 'error' } elseif ($r.total -eq 0 -or $r.sat -eq $r.total) { 'applied' }
             elseif ($r.sat -eq 0) { 'not_applied' } else { 'partial' }
    if ($r.errors.Count -gt 0) { $failed++ }
    $label = switch ($state) { 'applied' { $r.label } 'not_applied' { $r.notLabel } 'partial' { 'delvist' } 'na' { 'gælder ikke denne pc' } default { 'fejl' } }
    $mark = if ($r.errors.Count -gt 0) { '!!' } elseif ($state -eq 'na') { '--' } else { 'OK' }
    $extra = if ($r.changed -gt 0) { " ($($r.changed) ændret)" } else { '' }
    Say "$mark  $($r.title): $label$extra"
    foreach ($e in $r.errors) { Say "      $e" }
    foreach ($n in $r.notes) { Say "      $n" }
    if ($r.reboot) { Say '      Kræver en genstart.' }
    [void]$out.Add(@{ id = $r.id; state = $state; changed = $r.changed; skipped = $r.skipped; reboot = [bool]$r.reboot; errors = @($r.errors)
        hives = @($r.hv.Values); before = @{ reg = @($r.reg); svc = @($r.svc); task = @($r.task); appx = @($r.appx); ext = @($r.ext) } })
}
$verb = switch ($action) { 'apply' { 'Anvendt' } 'undo' { 'Fortrudt' } default { 'Tjekket' } }
Say ''
Say "$verb $($items.Count) punkter på Windows build $build ($editionId). $(@($hives).Count) brugerprofiler. $failed med fejl."
ReadPlans
$activeNow = ActivePlan
$powerInfo = @{ active = $activeNow; name = [string](@($script:plans | Where-Object { $_.guid -eq $activeNow } | ForEach-Object { $_.name }) | Select-Object -First 1)
    plans = @($script:plans | ForEach-Object { @{ guid = $_.guid; name = $_.name } }) }
$report = @{ v = 1; power = $powerInfo; action = $action; build = $build; edition = $editionId; editionGroup = $edition; server = [bool]$isServer
    restorePoint = $restore; profiles = @($hives).Count; items = @($out) }
Say ('##FLEETDECK-DEBLOAT## ' + ($report | ConvertTo-Json -Depth 8 -Compress))
if ($failed -gt 0) { exit 1 } else { exit 0 }
