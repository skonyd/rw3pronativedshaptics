param(
    [ValidatePattern('^[a-zA-Z0-9_-]{1,64}$')][string]$Name='controlled-full-v2'
)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$study=Join-Path $root 'TestResults/usb-profile-study-20261108'
$admin=Join-Path $study 'admin'
$directory=Join-Path $study $Name
$deadline=(Get-Date).AddMinutes(32)
# Bounded cleanup for this exact capture, even if source playback fails or the agent turn ends.
while((Get-Date) -lt $deadline -and !(Test-Path (Join-Path $directory 'completion.json'))) {
    Start-Sleep -Seconds 2
}
$reply=Get-Content (Join-Path $admin 'reply.json') -Raw | ConvertFrom-Json
if($reply.Status -ne 'capturing' -or [IO.Path]::GetFileName($reply.File) -ne ($Name+'.pcap')) {
    throw 'Capture ownership changed; did not stop another recording.'
}
$id='automatic-stop-'+$Name
@{Id=$id;Action='stop'} | ConvertTo-Json | Set-Content (Join-Path $admin 'request.json') -Encoding utf8
$stopped=$false
for($i=0;$i -lt 40;$i++) {
    Start-Sleep -Milliseconds 250
    $reply=Get-Content (Join-Path $admin 'reply.json') -Raw | ConvertFrom-Json
    if($reply.Id -eq $id -and $reply.Status -eq 'stopped') {$stopped=$true;break}
}
if(!$stopped){throw 'USB helper did not confirm stop.'}
$completed=Test-Path (Join-Path $directory 'completion.json')
& python (Join-Path $root 'tools/analyze-sensa-usb.py') (Join-Path $admin ($Name+'.pcap')) (Join-Path $study ($Name+'-decoded.json')) --timeline $directory
$analysisExit=$LASTEXITCODE
@{CaptureStopped=$stopped;SourceCompletionPresent=$completed;AnalysisExit=$analysisExit;FinishedUtc=[DateTimeOffset]::UtcNow.ToString('o');PhysicalEquivalenceProved=$false} |
    ConvertTo-Json | Set-Content (Join-Path $directory 'finalization.json') -Encoding utf8
