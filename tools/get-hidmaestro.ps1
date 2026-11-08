# Downloads the HIDMaestro SDK (MIT, https://github.com/hifihedgehog/HIDMaestro) release v2.1.0,
# verifies its SHA-256 and extracts what RW3 Pro needs into third_party/HIDMaestro (git-ignored).
$ErrorActionPreference = 'Stop'
$version = 'v2.1.0'
$asset   = 'HIDMaestro-v2.1.0.zip'
$sha256  = '7fe349f163864b8dfc36117af260d51bac94a2263b6cdfa719cb364a808397b4'
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
