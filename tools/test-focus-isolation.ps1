param([switch]$Apply)
$ErrorActionPreference = 'Stop'
$trialRoot = Split-Path -Parent $PSScriptRoot
$trialDirectory = Join-Path $trialRoot 'TestResults/focus-isolation-descendants-20261109'
New-Item -ItemType Directory -Path $trialDirectory -Force | Out-Null
$cli = 'C:/Program Files/Nefarius Software Solutions/HidHide/x64/HidHideCLI.exe'
$hostExe = 'C:\Program Files\dotnet\dotnet.exe'
$probe = Join-Path $trialRoot 'tests/RW3Pro.HardwareProbe/bin/Release/net10.0-windows10.0.26100.0/RW3Pro.HardwareProbe.dll'
$probeExe = [IO.Path]::ChangeExtension($probe, '.exe')
$target = 'USB\VID_1532&PID_0A3F\0000000000000001'
$result = [ordered]@{ Status='preparing'; Applied=$false; Restored=$false; Error=$null }
$addedApp=$false; $addedDevices=@(); $changedCloak=$false
function Invoke-HideCli([string[]]$Arguments) {
    $output = & $cli @Arguments 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0 -or $output -match '(?i)access.denied|error:|exception') { throw "HidHideCLI failed: $output" }
    return $output
}
function Save-Status { $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $trialDirectory 'status.json') -Encoding UTF8 }
function Snapshot {
    return [ordered]@{ Cloak=(Invoke-HideCli @('--cloak-state')); Inverse=(Invoke-HideCli @('--inv-state')); Devices=(Invoke-HideCli @('--dev-list')); Apps=(Invoke-HideCli @('--app-list')) }
}
function Listed([string]$Text,[string]$Flag,[string]$Value) {
    return @($Text -split '\r?\n' | Where-Object { $_.Trim() -ieq ($Flag+' "'+$Value+'"') }).Count -gt 0
}
try {
    foreach($file in @($cli,$hostExe,$probe,$probeExe)) { if(!(Test-Path -LiteralPath $file)) { throw "Missing prerequisite: $file" } }
    $before = Snapshot
    if($before.Inverse.Trim() -ne '--inv-off' -or $before.Cloak.Trim() -ne '--cloak-off') { throw 'Expected cloak OFF/inverse OFF; refusing another configuration.' }
    if(Test-Path -LiteralPath (Join-Path $env:LOCALAPPDATA 'RW3ProNativeDSHaptics/hidhide-restore.json')) { throw 'Another RW3 restore transaction exists.' }
    $settings=Get-Content -LiteralPath (Join-Path $env:LOCALAPPDATA 'RW3ProNativeDSHaptics/settings.json') -Raw | ConvertFrom-Json
    if($settings.Mode -ne 0) { throw 'RW3 must remain in Normal mode for this trial.' }
    $nodes=@(Get-PnpDevice -PresentOnly | Where-Object { $_.InstanceId -like '*VID_1532&PID_0A3F*' })
    if($nodes.Count -ne 1 -or $nodes[0].InstanceId -ine $target -or $nodes[0].Class -ne 'XboxComposite') { throw 'Exact single Xbox Wolverine node required.' }
    $container=(Get-PnpDeviceProperty -InstanceId $target -KeyName 'DEVPKEY_Device_ContainerId').Data
    $targets=@($target); $pending=@($target)
    for($depth=0;$depth -lt 4 -and $pending.Count -gt 0;$depth++) {
        $next=@()
        foreach($parent in $pending) {
            $children=(Get-PnpDeviceProperty -InstanceId $parent -KeyName 'DEVPKEY_Device_Children' -ErrorAction SilentlyContinue).Data
            foreach($child in @($children)) {
                if(!$child -or $targets -contains $child) { continue }
                $device=Get-PnpDevice -PresentOnly -InstanceId $child
                $childContainer=(Get-PnpDeviceProperty -InstanceId $child -KeyName 'DEVPKEY_Device_ContainerId').Data
                $actualParent=(Get-PnpDeviceProperty -InstanceId $child -KeyName 'DEVPKEY_Device_Parent').Data
                if($childContainer -ne $container -or $actualParent -ine $parent -or $device.Class -notin @('HIDClass','XnaComposite','XboxComposite')) { throw 'Unexpected descendant: refusing to filter outside confirmed controller chain.' }
                $targets+=$child; $next+=$child
                if($targets.Count -gt 6) { throw 'Unexpectedly large controller chain.' }
            }
        }
        $pending=$next
    }
    if($pending.Count -gt 0) { throw 'Controller chain exceeded depth bound.' }
    $result.Targets=$targets
    $baseline=& $probeExe inspect-xinput-visibility | Out-String
    [object[]]$baselineSlots=ConvertFrom-Json -InputObject $baseline
    if($LASTEXITCODE -ne 0 -or $baselineSlots.Count -ne 1) { throw 'Baseline needs one visible XInput slot.' }
    $result.Baseline=$baseline.Trim()
    if(!$Apply) { $result.Status='dry-run-only'; Save-Status; return }
    $principal=New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if(!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Administrator UAC approval required; no settings changed.' }
    $before | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $trialDirectory 'before.json') -Encoding UTF8
    # Exact undo is recorded BEFORE each possible mutation; flags are set first for partial failures.
    $undo=@('--cloak-off')
    if(!(Listed $before.Apps '--app-reg' $hostExe.Replace('/','\'))) { $undo+=('--app-unreg "'+$hostExe.Replace('/','\')+'"') }
    foreach($node in $targets) { if(!(Listed $before.Devices '--dev-hide' $node)) { $undo+=('--dev-unhide "'+$node+'"') } }
    $undo | Set-Content -LiteralPath (Join-Path $trialDirectory 'undo-arguments.txt') -Encoding UTF8
    if(!(Listed $before.Apps '--app-reg' $hostExe.Replace('/','\'))) { $addedApp=$true; Invoke-HideCli @('--app-reg',$hostExe) | Out-Null }
    foreach($node in $targets) { if(!(Listed $before.Devices '--dev-hide' $node)) { $addedDevices+=$node; Invoke-HideCli @('--dev-hide',$node) | Out-Null } }
    $changedCloak=$true; Invoke-HideCli @('--cloak-on') | Out-Null
    $result.Applied=$true
    $blocked=& $probeExe inspect-xinput-visibility | Out-String
    if($LASTEXITCODE -ne 0) { throw 'Blocked-host visibility inspection failed.' }
    $allowed=& $hostExe $probe inspect-xinput-visibility | Out-String
    if($LASTEXITCODE -ne 0) { throw 'Allowed-host visibility inspection failed.' }
    $result.BlockedHostSlots=$blocked.Trim(); $result.AllowedHostSlots=$allowed.Trim()
    [object[]]$blockedSlots=ConvertFrom-Json -InputObject $blocked
    [object[]]$allowedSlots=ConvertFrom-Json -InputObject $allowed
    if($blockedSlots.Count -ne 0 -or $allowedSlots.Count -ne 1) { throw 'Isolation not demonstrated: require zero blocked-host slots, one allowed-host slot.' }
    $result.Status='isolated-probe-running'; Save-Status
    # The user's normal browser is neither terminated nor reconfigured. A fresh profile has no old device handles.
    $profile=Join-Path $trialDirectory 'opera-profile'
    $page=Join-Path $trialDirectory 'test.html'
    @'
<!doctype html><meta charset="utf-8"><title>RW3 - gecici izolasyon testi</title><style>body{background:#151719;color:#eee;font:24px system-ui;margin:60px}h1{color:#67dc95}</style><h1>RW3 gecici izolasyon testi</h1><p>Bu yeni Opera penceresinde sabit titresimin devam edip etmedigini kontrol edin.</p><p>HidHide ayarlari kisa test sonunda eski durumuna alinacak.</p>
'@ | Set-Content -LiteralPath $page -Encoding UTF8
    $arguments=@('--user-data-dir="'+$profile+'"','--no-first-run','--no-default-browser-check',([uri]$page).AbsoluteUri)
    Start-Process -FilePath 'C:/Program Files/Opera/opera.exe' -ArgumentList $arguments -WindowStyle Normal | Out-Null
    & $hostExe $probe body-focus-probe xinput (Join-Path $trialDirectory 'motor.json') 60 > (Join-Path $trialDirectory 'motor.log') 2>&1
    if($LASTEXITCODE -ne 0) { throw 'Motor probe failed; physical result unavailable.' }
    $result.Status='probe-completed-physical-answer-required'
} catch { $result.Error=$_.Exception.Message; $result.Status='failed-or-inconclusive' }
finally {
    $restoreErrors=@()
    # Disable cloak first: restore normal access even if later cleanup fails. No pre-existing list entries are deleted.
    if($changedCloak) { try { Invoke-HideCli @('--cloak-off') | Out-Null } catch { $restoreErrors+=$_.Exception.Message } }
    foreach($node in $addedDevices) { try { Invoke-HideCli @('--dev-unhide',$node) | Out-Null } catch { $restoreErrors+=$_.Exception.Message } }
    if($addedApp) { try { Invoke-HideCli @('--app-unreg',$hostExe) | Out-Null } catch { $restoreErrors+=$_.Exception.Message } }
    if($changedCloak -or $addedDevices.Count -gt 0 -or $addedApp) {
        try {
            $after=Snapshot
            $after | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $trialDirectory 'after.json') -Encoding UTF8
            foreach($key in @('Cloak','Inverse','Devices','Apps')) {
                $a=@($before[$key] -split '\r?\n' | Where-Object {$_} | Sort-Object)
                $b=@($after[$key] -split '\r?\n' | Where-Object {$_} | Sort-Object)
                if(($a -join "`n") -cne ($b -join "`n")) { $restoreErrors+="Snapshot mismatch: $key" }
            }
            $result.Restored=$restoreErrors.Count -eq 0
        } catch { $restoreErrors+=$_.Exception.Message }
    }
    $result.RestoreErrors=$restoreErrors
    Save-Status
}
