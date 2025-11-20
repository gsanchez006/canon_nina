@echo off
setlocal enabledelayedexpansion

set "ProjectFile=NINA.Plugin.CanonAstroImage.csproj"
set "BuildConfig=Release"
set "BuildDir=bin\%BuildConfig%\net8.0-windows"
set "DllFile=%BuildDir%\NINA.Plugin.CanonAstroImage.dll"
set "OutputZip=canon.zip"
set "TempDir=%TEMP%\canon_build_%RANDOM%"

echo.
echo ============================================================================
echo  Canon Astro Image Format Plugin - Build and Package
echo ============================================================================
echo.

if not exist "%ProjectFile%" (
    echo ERROR: Project file not found: %ProjectFile%
    exit /b 1
)

echo [STEP 1] Building project in %BuildConfig% configuration...
dotnet build "%ProjectFile%" -c "%BuildConfig%" -v minimal
if errorlevel 1 (
    echo Build failed!
    exit /b 1
)
echo Build completed successfully
echo.

if not exist "%DllFile%" (
    echo ERROR: DLL file not found: %DllFile%
    exit /b 1
)

echo [STEP 2] Preparing package...
if exist "%TempDir%" rmdir /s /q "%TempDir%"
mkdir "%TempDir%\Canon"
copy "%DllFile%" "%TempDir%\Canon\"
echo DLL copied to package
echo.

echo [STEP 3] Creating canon.zip...
if exist "%OutputZip%" del "%OutputZip%"
powershell -NoProfile -Command "Add-Type -AssemblyName System.IO.Compression.FileSystem; $zip = [System.IO.Compression.ZipFile]::Open('%CD%\%OutputZip%', 1); [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, '%DllFile%', 'Canon/NINA.Plugin.CanonAstroImage.dll'); $zip.Dispose();"
echo ZIP created successfully
echo.

echo [STEP 4] Verifying package...
dir "%OutputZip%"
echo.

echo [STEP 5] Cleaning up...
rmdir /s /q "%TempDir%"
echo Temporary files removed
echo.

echo ============================================================================
echo BUILD COMPLETE
echo ============================================================================
echo.
echo Package: %OutputZip%
echo Installation: Extract to %%LOCALAPPDATA%%\NINA\Plugins\3.0.0\
echo.

certutil -hashfile "%OutputZip%" SHA256
echo.
