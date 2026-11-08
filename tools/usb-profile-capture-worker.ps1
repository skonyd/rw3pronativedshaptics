param([ValidateRange(1,4)][int]$LifetimeHours=2)
$ErrorActionPreference = 'Stop'
# Explicitly bounded elevated helper: USB inventory/capture only. No general command execution.
$work = Join-Path (Split-Path $PSScriptRoot -Parent) 'TestResults/usb-profile-study-20261108/admin'
New-Item -ItemType Directory -Force -Path $work | Out-Null
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Windows administrator token required.' }
$tool = 'C:\Program Files\USBPcap\USBPcapCMD.exe'
$capture = $null
$deadline = (Get-Date).AddHours($LifetimeHours)
$seen = ''
function Save-Reply($reply) { $reply | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $work 'reply.json') -Encoding utf8 }
Save-Reply @{ Status='ready'; Administrator=$true; AllowedActions=@('inventory','capture','stop','exit'); Expires=$deadline.ToString('o') }
try {
    while ((Get-Date) -lt $deadline) {
        $requestFile = Join-Path $work 'request.json'
        if (!(Test-Path -LiteralPath $requestFile)) { Start-Sleep -Milliseconds 250; continue }
        try { $request = Get-Content -LiteralPath $requestFile -Raw | ConvertFrom-Json } catch { Start-Sleep -Milliseconds 250; continue }
        if (!$request.Id -or $request.Id -eq $seen) { Start-Sleep -Milliseconds 250; continue }
        $seen = [string]$request.Id
        try {
            switch ($request.Action) {
                'inventory' {
                    if ($capture -and !$capture.HasExited) { throw 'Stop capture before inventory.' }
                    $info = [Diagnostics.ProcessStartInfo]::new($tool)
                    if ($request.Hub) {
                        if ($request.Hub -notmatch '^\\\\\.\\USBPcap[1-9][0-9]?$') { throw 'Invalid inventory hub.' }
                        $info.Arguments = '--extcap-interface "' + $request.Hub + '" --extcap-config'
                    } else { $info.Arguments = '--extcap-interfaces' }
                    $info.UseShellExecute=$false; $info.CreateNoWindow=$true
                    $info.RedirectStandardInput=$true; $info.RedirectStandardOutput=$true; $info.RedirectStandardError=$true
                    $process=[Diagnostics.Process]::Start($info)
                    $stdout=$process.StandardOutput.ReadToEndAsync(); $stderr=$process.StandardError.ReadToEndAsync()
                    $process.StandardInput.WriteLine('0'); $process.StandardInput.Close()
                    if (!$process.WaitForExit(10000)) { $process.Kill(); $process.WaitForExit() }
                    $stdout.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $work 'inventory.txt')
                    $stderr.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $work 'inventory-errors.txt')
                    Save-Reply @{Id=$seen;Status='inventory-complete';Exit=$process.ExitCode}
                    $process.Dispose()
                }
                'capture' {
                    if ($capture -and !$capture.HasExited) { throw 'A capture is already active.' }
                    # Root-hub/address must be established from the current inventory before this request.
                    if ($request.Hub -notmatch '^\\\\\.\\USBPcap[1-9][0-9]?$') { throw 'Invalid USBPcap root-hub path.' }
                    $address=[int]$request.Address
                    if ($address -lt 1 -or $address -gt 127) { throw 'Exactly one valid device address required.' }
                    if ($request.Name -notmatch '^[a-zA-Z0-9_-]{1,64}$') { throw 'Invalid capture name.' }
                    $out=Join-Path $work ($request.Name+'.pcap')
                    if (Test-Path -LiteralPath $out) { throw 'Capture file already exists.' }
                    $capture=Start-Process -FilePath $tool -ArgumentList @('-d',$request.Hub,'--devices',"$address",'--inject-descriptors','-o','-') -WindowStyle Hidden -PassThru -RedirectStandardOutput $out -RedirectStandardError (Join-Path $work ($request.Name+'-errors.txt'))
                    Save-Reply @{Id=$seen;Status='capturing';PID=$capture.Id;Address=$address;Hub=$request.Hub;File=$out}
                }
                'stop' {
                    if ($capture -and !$capture.HasExited) { $capture.Kill(); $capture.WaitForExit() }
                    if ($capture) { $capture.Dispose(); $capture=$null }
                    Save-Reply @{Id=$seen;Status='stopped'}
                }
                'exit' { Save-Reply @{Id=$seen;Status='exited'}; return }
                default { throw 'Unsupported action. Only USB inventory/capture/stop/exit are allowed.' }
            }
        } catch { Save-Reply @{Id=$seen;Status='error';Error=$_.Exception.Message} }
    }
} finally {
    if ($capture -and !$capture.HasExited) { $capture.Kill(); $capture.WaitForExit() }
    if ($capture) { $capture.Dispose() }
}
