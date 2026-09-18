# Build and Package Guide

This directory includes automated build scripts to compile the Canon Astro Image plugin and create a ready-to-distribute `canon.zip` package.

## Quick Start

### Option 1: PowerShell (Recommended)
```powershell
.\build.ps1
```

### Option 2: Batch Script
```cmd
build.bat
```

Both scripts will:
1. ✓ Compile in Release configuration
2. ✓ Package the DLL into `canon.zip` under a `Canon\` folder
3. ✓ Print the package size, SHA256 and installation instructions

## Output

After running a build script, you'll have:
- **`canon.zip`** - Ready-to-install package (in plugin root directory)
- **Folder structure inside ZIP:**
  ```
  Canon/
  └── NINA.Plugin.CanonAstroImage.dll
  ```

## Installation (End Users)

Users receive `canon.zip` from GitHub releases:

1. Download `canon.zip`
2. Extract to: `%LOCALAPPDATA%\NINA\Plugins\3.0.0\`
3. Folder structure automatically creates:
   ```
   %LOCALAPPDATA%\NINA\Plugins\3.0.0\
   └── Canon/
       └── NINA.Plugin.CanonAstroImage.dll
   ```
4. Restart NINA
5. Enable in Settings → Plugins → Canon Astro Image

## Build Script Details

### `build.ps1` (PowerShell - Recommended)

**Advantages:**
- More readable output with colors
- Better error handling
- Progress indicators
- Cross-platform compatibility
- Detailed step-by-step information

**Requirements:**
- PowerShell 5.0+
- .NET 8.0 SDK

**Run:**
```powershell
# From PowerShell
.\build.ps1

# Or with explicit execution policy
powershell -ExecutionPolicy Bypass -File build.ps1
```

### `build.bat` (Batch Script)

`build.bat` is a thin wrapper that runs `build.ps1` with `-ExecutionPolicy Bypass`, so Command Prompt users get
exactly the same build without changing their execution policy. It switches to the repository root itself, so it can
be run from any directory.

**Requirements:**
- Windows Command Prompt
- .NET 8.0 SDK
- Windows PowerShell 5.1 or later (present on every supported Windows)

**Run:**
```cmd
build.bat
```

## Local development deploy

A plain `dotnet build` does not touch your NINA installation. To copy the freshly built DLL into
`%LOCALAPPDATA%\NINA\Plugins\3.0.0\Canon` after building, run:

```powershell
dotnet build NINA.Plugin.CanonAstroImage.csproj -c Release -p:DeployToNina=true
```

Restart NINA afterwards.

## Running the tests

The unit tests live in `tests/NINA.Plugin.CanonAstroImage.Tests` and need no NINA installation:

```powershell
dotnet test Canon_RAW.sln -c Release
```

They pin the reflection the plugin uses on NINA's `ImageArray` types. Run them after every `NINA.Plugin` package
bump; a failure there means the direct-save path will fall back to writing both files.

## .NET SDK Requirements

Both scripts require .NET 8.0 SDK:

**Check installation:**
```cmd
dotnet --version
```

**Download:**
- https://dotnet.microsoft.com/download

## Troubleshooting

### ".NET SDK not found"
- Install .NET 8.0 SDK from https://dotnet.microsoft.com/download
- Verify installation: `dotnet --version`
- Restart terminal/IDE if just installed

### "Project file not found"
- Ensure you're running the script from the plugin directory
- Verify `NINA.Plugin.CanonAstroImage.csproj` exists

### "Build failed"
- Check for compilation errors in output
- Verify NuGet packages are restored: `dotnet restore`
- Check NINA.Plugin NuGet package version in `.csproj`

### PowerShell execution policy error (Windows)
```powershell
# Option 1: Run with bypass
powershell -ExecutionPolicy Bypass -File build.ps1

# Option 2: Change policy (administrator)
Set-ExecutionPolicy -ExecutionPolicy RemoteSigned -Scope CurrentUser
```

## GitHub Release Workflow

1. **Test and build locally:**
   ```powershell
   dotnet test Canon_RAW.sln -c Release
   .\build.ps1
   ```

2. **Test installation** (if desired):
   - Extract `canon.zip` to `%LOCALAPPDATA%\NINA\Plugins\3.0.0\`
   - Restart NINA
   - Verify plugin appears and works

3. **Create GitHub release:**
   - Go to repository Releases
   - Create new release tag (e.g., `v1.0.0`)
   - Upload `canon.zip` as release asset
   - Include installation instructions

4. **Release notes example:**
   ```
   ## Installation
   1. Download canon.zip
   2. Extract to %LOCALAPPDATA%\NINA\Plugins\3.0.0\
   3. Restart NINA
   4. Enable in Settings → Plugins → Canon Astro Image

   ## Checksum
   SHA256: <hash from script output>
   ```

## CI/CD Integration

These scripts can be integrated into GitHub Actions:

```yaml
name: Build and Release

on: [push, pull_request]

jobs:
  build:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '8.0.x'
      - name: Test
        run: dotnet test Canon_RAW.sln -c Release
      - name: Build Plugin
        run: .\build.ps1
      - name: Upload Artifact
        uses: actions/upload-artifact@v4
        with:
          name: canon-plugin
          path: canon.zip
```

## Support

For issues with the build process:
1. Check .NET SDK version: `dotnet --version`
2. Clean and rebuild: Delete `bin/` and `obj/` directories
3. Restore packages: `dotnet restore NINA.Plugin.CanonAstroImage.csproj`
4. Review error output from build scripts

For plugin support, see main [README.md](README.md)
