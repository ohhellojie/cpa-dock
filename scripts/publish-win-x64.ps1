param(
    [string]$Runtime = "win-x64",
    [switch]$FrameworkDependent
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$ProjectRoot = Split-Path -Parent $PSScriptRoot
$ProjectFile = Join-Path $ProjectRoot "src\CPALauncher.Wpf\CPALauncher.Wpf.csproj"
$PublishProfile = "SingleFile"

$ModeName = if ($FrameworkDependent) { "framework-dependent" } else { "self-contained" }
$PublishDir = Join-Path $ProjectRoot "artifacts\publish\$Runtime\$ModeName"
$PackageDir = Join-Path $ProjectRoot "artifacts\release\CPA-Dock-$Runtime-$ModeName"
$ZipPath = Join-Path $ProjectRoot "artifacts\release\CPA-Dock-$Runtime-$ModeName.zip"

$ArtifactRoot = [IO.Path]::GetFullPath((Join-Path $ProjectRoot 'artifacts')) + [IO.Path]::DirectorySeparatorChar
foreach ($OutputPath in @($PublishDir, $PackageDir, $ZipPath)) {
    if (-not [IO.Path]::GetFullPath($OutputPath).StartsWith($ArtifactRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Output path must stay inside artifacts: $OutputPath"
    }
}

Write-Host "==> Cleaning previous outputs"
Remove-Item -LiteralPath $PublishDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $PackageDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $ZipPath -Force -ErrorAction SilentlyContinue

New-Item -ItemType Directory -Force -Path $PublishDir | Out-Null
New-Item -ItemType Directory -Force -Path $PackageDir | Out-Null

$PublishArgs = @(
    "publish",
    $ProjectFile,
    "-c", "Release",
    "-r", $Runtime,
    "-o", $PublishDir,
    "-p:PublishProfile=$PublishProfile"
)

if ($FrameworkDependent) {
    $PublishArgs += "-p:SelfContained=false"
}
else {
    $PublishArgs += "-p:SelfContained=true"
}

Write-Host "==> Running dotnet publish"
& dotnet @PublishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed."
}

$PublishedExe = Join-Path $PublishDir "CPA Dock.exe"
if (-not (Test-Path $PublishedExe)) {
    throw "Published CPA Dock.exe was not found: $PublishedExe"
}

$PublishedFiles = @(Get-ChildItem -Path $PublishDir -Recurse -File)
$UnexpectedFiles = @($PublishedFiles | Where-Object {
    $_.FullName -ne $PublishedExe
})

if ($UnexpectedFiles.Count -gt 0) {
    $UnexpectedList = $UnexpectedFiles |
        Sort-Object FullName |
        ForEach-Object { $_.FullName.Replace($PublishDir, '.').TrimStart('\\') }
    throw "Publish output is not a true single-file bundle. Unexpected files:`n - $($UnexpectedList -join "`n - ")"
}

Write-Host "==> Assembling deliverable folder"
Copy-Item $PublishedExe -Destination (Join-Path $PackageDir "CPA Dock.exe") -Force

Copy-Item -LiteralPath (Join-Path $ProjectRoot "LICENSE") -Destination (Join-Path $PackageDir "LICENSE.txt")
Copy-Item -LiteralPath (Join-Path $ProjectRoot "src\CPALauncher.Wpf\Assets\Lucide-LICENSE.txt") -Destination (Join-Path $PackageDir "THIRD-PARTY-LICENSES.txt")

$ReadmePath = Join-Path $PackageDir "README.txt"
@(
    "CPA Dock",
    "",
    "1. 解压到任意目录，运行 CPA Dock.exe。",
    "2. 在设置中选择已有 CPA 程序与 config.yaml，或点击「安装 CPA」。",
    "3. 启动 CPA，打开管理页。",
    "",
    "设置：%AppData%\CPALauncher\settings.json",
    "https://github.com/ohhellojie/cpa-dock"
) | Set-Content -Path $ReadmePath -Encoding UTF8

Write-Host "==> Creating zip package"
Compress-Archive -Path (Join-Path $PackageDir "*") -DestinationPath $ZipPath -Force
& (Join-Path $PSScriptRoot 'verify-package.ps1') -ZipPath $ZipPath -ExecutablePath $PublishedExe

$LegacyZipPath = Join-Path (Split-Path $ZipPath) "CPALauncher-$Runtime-$ModeName.zip"
Copy-Item -LiteralPath $ZipPath -Destination $LegacyZipPath -Force
# Versions through v0.3.0 require the legacy executable name inside the ZIP too.
$LegacyArchive = [IO.Compression.ZipFile]::Open($LegacyZipPath, [IO.Compression.ZipArchiveMode]::Update)
try {
    $OldEntry = $LegacyArchive.GetEntry('CPA Dock.exe')
    $NewEntry = $LegacyArchive.CreateEntry('CPALauncher.exe')
    $InputStream = $OldEntry.Open()
    $OutputStream = $NewEntry.Open()
    try { $InputStream.CopyTo($OutputStream) }
    finally { $InputStream.Dispose(); $OutputStream.Dispose() }
    $OldEntry.Delete()
    $ReadmeEntry = $LegacyArchive.GetEntry('README.txt')
    $Reader = [IO.StreamReader]::new($ReadmeEntry.Open())
    try { $LegacyReadme = $Reader.ReadToEnd().Replace('CPA Dock.exe', 'CPALauncher.exe') }
    finally { $Reader.Dispose() }
    $ReadmeEntry.Delete()
    $Writer = [IO.StreamWriter]::new($LegacyArchive.CreateEntry('README.txt').Open(), [Text.UTF8Encoding]::new($true))
    try { $Writer.Write($LegacyReadme) } finally { $Writer.Dispose() }
} finally { $LegacyArchive.Dispose() }
& (Join-Path $PSScriptRoot 'verify-package.ps1') -ZipPath $LegacyZipPath -ExecutablePath $PublishedExe
$Hashes = @($ZipPath, $LegacyZipPath) | ForEach-Object {
    $Hash = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant()
    "$Hash  $([IO.Path]::GetFileName($_))"
}
$Hashes | Set-Content -LiteralPath (Join-Path (Split-Path $ZipPath) 'SHA256SUMS.txt') -Encoding ascii

Write-Host ""
Write-Host "Publish completed:"
Write-Host "  PublishDir : $PublishDir"
Write-Host "  PackageDir : $PackageDir"
Write-Host "  ZipPath    : $ZipPath"
