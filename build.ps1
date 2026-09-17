# Canon Astro Image plugin - build and package (PowerShell)
# Usage: .\build.ps1   (run from the repository root)
# Produces canon.zip containing Canon\NINA.Plugin.CanonAstroImage.dll,
# ready to extract into %LOCALAPPDATA%\NINA\Plugins\3.0.0\

$ErrorActionPreference = "Stop"

$ProjectFile = "NINA.Plugin.CanonAstroImage.csproj"
$BuildConfig = "Release"
$DllFile     = "bin\$BuildConfig\net8.0-windows\NINA.Plugin.CanonAstroImage.dll"
$OutputZip   = "canon.zip"

Write-Host ""
Write-Host "=== Canon Astro Image plugin - build and package ==="
Write-Host ""

if (-not (Test-Path $ProjectFile)) {
    Write-Host "ERROR: Project file not found: $ProjectFile (run this script from the repository root)"
    exit 1
}

Write-Host "[1/3] Building ($BuildConfig)..."
dotnet build $ProjectFile -c $BuildConfig -v minimal
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed!"
    exit 1
}
if (-not (Test-Path $DllFile)) {
    Write-Host "ERROR: DLL not found after build: $DllFile"
    exit 1
}

Write-Host "[2/3] Creating $OutputZip..."
if (Test-Path $OutputZip) { Remove-Item $OutputZip -Force }
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::Open((Join-Path (Get-Location) $OutputZip), [System.IO.Compression.ZipArchiveMode]::Create)
try {
    [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Resolve-Path $DllFile).Path, 'Canon/NINA.Plugin.CanonAstroImage.dll') | Out-Null
} finally {
    $zip.Dispose()
}

Write-Host "[3/3] Package summary"
Get-Item $OutputZip | Select-Object Name, @{n='SizeMB';e={'{0:N2}' -f ($_.Length/1MB)}} | Format-Table -AutoSize
$hash = (Get-FileHash $OutputZip -Algorithm SHA256).Hash
Write-Host "SHA256: $hash"
Write-Host ""
Write-Host "Install: extract $OutputZip into %LOCALAPPDATA%\NINA\Plugins\3.0.0\ and restart NINA"
Write-Host ""
