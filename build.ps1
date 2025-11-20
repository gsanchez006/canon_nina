# Canon Astronomy Format Plugin - Build Script (PowerShell)
# Usage: .\build.ps1

param()

$ProjectFile = "NINA.Plugin.CanonAstroImage.csproj"
$BuildConfig = "Release"
$BuildDir = "bin\$BuildConfig\net8.0-windows"
$DllFile = "$BuildDir\NINA.Plugin.CanonAstroImage.dll"
$OutputZip = "canon.zip"
$TempDir = "$env:TEMP\canon_build_$([System.Random]::new().Next())"

Write-Host ""
Write-Host "========================================================================"
Write-Host "  Canon Astronomy Format Plugin - Build and Package"
Write-Host "========================================================================"
Write-Host ""

# Check project file
if (-not (Test-Path $ProjectFile)) {
    Write-Host "ERROR: Project file not found: $ProjectFile"
    exit 1
}

# Build
Write-Host "[STEP 1] Building project in $BuildConfig configuration..."
dotnet build $ProjectFile -c $BuildConfig -v minimal
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed!"
    exit 1
}
Write-Host "Build completed successfully"
Write-Host ""

# Check DLL
if (-not (Test-Path $DllFile)) {
    Write-Host "ERROR: DLL file not found: $DllFile"
    exit 1
}

# Prepare package
Write-Host "[STEP 2] Preparing package..."
if (Test-Path $TempDir) { Remove-Item $TempDir -Recurse -Force }
New-Item -ItemType Directory -Path "$TempDir\Canon" -Force | Out-Null
Copy-Item $DllFile -Destination "$TempDir\Canon\"
Write-Host "DLL copied to package"
Write-Host ""

# Create ZIP
Write-Host "[STEP 3] Creating canon.zip..."
if (Test-Path $OutputZip) { Remove-Item $OutputZip -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::Open((Join-Path (Get-Location) $OutputZip), 1)
[System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $DllFile, 'Canon/NINA.Plugin.CanonAstroImage.dll') | Out-Null
$zip.Dispose()
Write-Host "ZIP created successfully"
Write-Host ""

# Verify
Write-Host "[STEP 4] Verifying package..."
Get-Item $OutputZip | Select-Object Name, @{n='SizeMB';e={'{0:N2}' -f ($_.Length/1MB)}}
Write-Host ""

# Cleanup
Write-Host "[STEP 5] Cleaning up..."
Remove-Item $TempDir -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "Temporary files removed"
Write-Host ""

# Summary
Write-Host "========================================================================"
Write-Host "BUILD COMPLETE"
Write-Host "========================================================================"
Write-Host ""
Write-Host "Package: $OutputZip"
Write-Host "Installation: Extract to %LOCALAPPDATA%\NINA\Plugins\3.0.0\"
Write-Host ""

$hash = (Get-FileHash $OutputZip -Algorithm SHA256).Hash
Write-Host "SHA256: $hash"
Write-Host ""
