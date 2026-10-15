param([Parameter(Mandatory = $true)][string]$ZipPath, [Parameter(Mandatory = $true)][string]$ExecutablePath)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($ZipPath))
try {
    $ExecutableName = if ([IO.Path]::GetFileName($ZipPath).StartsWith('CPALauncher-', [StringComparison]::OrdinalIgnoreCase)) { 'CPALauncher.exe' } else { 'CPA Dock.exe' }
    $expected = @($ExecutableName, 'LICENSE.txt', 'THIRD-PARTY-LICENSES.txt', 'README.txt')
    $actual = @($archive.Entries | ForEach-Object { $_.FullName })
    if (@(Compare-Object ($expected | Sort-Object) ($actual | Sort-Object)).Count -ne 0) { throw 'Package contains missing or unexpected files.' }
    $entry = $archive.GetEntry($ExecutableName)
    $stream = $entry.Open()
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $packedHash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
    finally { $stream.Dispose(); $sha.Dispose() }
    if ($packedHash -ne (Get-FileHash -LiteralPath $ExecutablePath -Algorithm SHA256).Hash) { throw 'Packaged executable does not match build output.' }
    if ($entry.Length -eq 0) { throw 'Packaged executable is empty.' }
    Write-Host 'Package verified: file list and executable SHA-256 match.'
} finally { $archive.Dispose() }
