@echo off
setlocal

set "ProjectFile=NINA.Plugin.CanonAstroImage.csproj"
set "BuildConfig=Release"
set "DllFile=bin\%BuildConfig%\net8.0-windows\NINA.Plugin.CanonAstroImage.dll"
set "OutputZip=canon.zip"

echo.
echo === Canon Astro Image plugin - build and package ===
echo.

if not exist "%ProjectFile%" (
    echo ERROR: Project file not found: %ProjectFile% ^(run this script from the repository root^)
    exit /b 1
)

echo [1/3] Building (%BuildConfig%)...
dotnet build "%ProjectFile%" -c "%BuildConfig%" -v minimal
if errorlevel 1 (
    echo Build failed!
    exit /b 1
)
if not exist "%DllFile%" (
    echo ERROR: DLL not found after build: %DllFile%
    exit /b 1
)

echo [2/3] Creating %OutputZip%...
if exist "%OutputZip%" del "%OutputZip%"
powershell -NoProfile -Command "Add-Type -AssemblyName System.IO.Compression.FileSystem; $zip = [System.IO.Compression.ZipFile]::Open('%CD%\%OutputZip%', 'Create'); try { [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, '%CD%\%DllFile%', 'Canon/NINA.Plugin.CanonAstroImage.dll') | Out-Null } finally { $zip.Dispose() }"
if errorlevel 1 (
    echo ZIP creation failed!
    exit /b 1
)

echo [3/3] Package summary
dir "%OutputZip%"
certutil -hashfile "%OutputZip%" SHA256
echo.
echo Install: extract %OutputZip% into %%LOCALAPPDATA%%\NINA\Plugins\3.0.0\ and restart NINA
echo.
