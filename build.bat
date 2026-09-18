@echo off
rem Canon Astro Image plugin - build and package.
rem Thin wrapper: all the work is in build.ps1 so the two scripts cannot drift apart.
rem Usage: build.bat   (from any directory; it switches to the repository root itself)
pushd "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1"
set "RC=%ERRORLEVEL%"
popd
exit /b %RC%
