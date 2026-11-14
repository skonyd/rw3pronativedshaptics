param(
    [Parameter(Mandatory)][string]$Stock,
    [string]$Zig = 'zig',
    [string]$Output = 'TestResults/firmware-lab',
    [string]$VerifiedPcmTrace
)
$ErrorActionPreference = 'Stop'
$stockPath = (Resolve-Path -LiteralPath $Stock).Path
$out = (New-Item -ItemType Directory -Force -Path $Output).FullName
$env:ZIG_LOCAL_CACHE_DIR = Join-Path $out 'zig-cache'
$env:ZIG_GLOBAL_CACHE_DIR = Join-Path $out 'zig-global-cache'
$elf = Join-Path $out 'audio_haptics.elf'
$bin = Join-Path $out 'audio_haptics.bin'
$report = Join-Path $out 'build-replay.json'
if (Test-Path -LiteralPath $report) { Remove-Item -LiteralPath $report }

$compileArgs = @('cc', '-target', 'thumb-freestanding-eabi', '-mcpu=cortex_m4', '-Oz',
    '-ffreestanding', '-fno-builtin', '-fno-unwind-tables', '-ffunction-sections',
    '-nostdlib', '-Wl,-e,audio_haptics_step', '-T', (Join-Path $PSScriptRoot 'audio_haptics_sim.ld'),
    (Join-Path $PSScriptRoot 'audio_haptics_core.c'), '-o', $elf)
& $Zig @compileArgs
if ($LASTEXITCODE -ne 0) { throw 'Cortex-M4 build failed.' }
& $Zig objcopy -O binary --only-section=.text $elf $bin
if ($LASTEXITCODE -ne 0) { throw 'Cortex-M4 text extraction failed.' }

$replayArgs = @('-B', (Join-Path $PSScriptRoot 'simulate_audio_haptics.py'), $stockPath, $bin)
if ($VerifiedPcmTrace) {
    $replayArgs += @('--verified-pcm-trace', (Resolve-Path -LiteralPath $VerifiedPcmTrace).Path)
}
$results = & python @replayArgs
if ($LASTEXITCODE -ne 0) { throw 'Firmware replay failed.' }
$results | Set-Content -LiteralPath $report -Encoding ascii
Get-FileHash -LiteralPath $stockPath, $bin, $report
