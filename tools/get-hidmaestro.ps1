# Downloads the HIDMaestro SDK (MIT, https://github.com/hifihedgehog/HIDMaestro) release v2.4.0,
# verifies its SHA-256 and extracts what RW3 Pro needs into third_party/HIDMaestro (git-ignored).
$ErrorActionPreference = 'Stop'
$version = 'v2.4.0'
$asset   = 'HIDMaestro-v2.4.0.zip'
$sha256  = 'e17cd59e939144f01a87230228c4fba1d1bc50f3286d27d2d1c867370bff10d2'
$root    = Split-Path -Parent $PSScriptRoot
$dest    = Join-Path $root 'third_party\HIDMaestro'
$tmp     = Join-Path ([IO.Path]::GetTempPath()) ("hidmaestro-" + [Guid]::NewGuid())
New-Item -ItemType Directory -Force $tmp, $dest | Out-Null
$zip = Join-Path $tmp $asset
Invoke-WebRequest "https://github.com/hifihedgehog/HIDMaestro/releases/download/$version/$asset" -OutFile $zip
$actual = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $sha256) { throw "SHA-256 mismatch: $actual" }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$z = [IO.Compression.ZipFile]::OpenRead($zip)
try {
  foreach ($e in $z.Entries) {
    $n = $e.FullName -replace '\','/'
    if ($n -in @('HIDMaestro.Core.dll','LICENSE','THIRD-PARTY-NOTICES.txt','HIDMaestroUninstall/HIDMaestroUninstall.exe')) {
      [IO.Compression.ZipFileExtensions]::ExtractToFile($e, (Join-Path $dest (Split-Path $n -Leaf)), $true)
    }
  }
} finally { $z.Dispose(); Remove-Item -Recurse -Force $tmp }
Write-Host "HIDMaestro $version ready in $dest"
