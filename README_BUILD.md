# Build and Package Guide

This directory includes automated build scripts to compile the Canon Astronomy Format plugin and create a ready-to-distribute `canon.zip` package.

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
1. ✓ Detect .NET SDK installation
2. ✓ Clean previous builds
3. ✓ Compile in Release configuration
4. ✓ Create `Canon\` folder structure
5. ✓ Package DLL into `canon.zip`
6. ✓ Verify package contents
7. ✓ Display installation instructions

## Output

After running a build script, you'll have:
- **`canon.zip`** - Ready-to-install package (in plugin root directory)
- **Folder structure inside ZIP:**
  ```
  Canon/
  └── NINA.Plugin.CanonAstronomyFormat.dll
  ```

## Installation (End Users)

Users receive `canon.zip` from GitHub releases:

1. Download `canon.zip`
2. Extract to: `%LOCALAPPDATA%\NINA\Plugins\3.0.0\`
3. Folder structure automatically creates:
   ```
   %LOCALAPPDATA%\NINA\Plugins\3.0.0\
   └── Canon/
       └── NINA.Plugin.CanonAstronomyFormat.dll
   ```
4. Restart NINA
5. Enable in Settings → Plugins → Canon Astronomy Format

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

**Advantages:**
- Works in traditional Command Prompt
- No execution policy concerns
- Minimal dependencies

**Requirements:**
- Windows Command Prompt
- .NET 8.0 SDK
- PowerShell (integrated into script for ZIP creation)

**Run:**
```cmd
build.bat
```

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
- Verify `NINA.Plugin.CanonAstronomyFormat.csproj` exists

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

1. **Build locally:**
   ```powershell
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
   4. Enable in Settings → Plugins → Canon Astronomy Format

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
      - uses: actions/checkout@v3
      - uses: actions/setup-dotnet@v3
        with:
          dotnet-version: '8.0.x'
      - name: Build Plugin
        run: .\NINA.Plugin.CanonAstronomyFormat\build.ps1
      - name: Upload Artifact
        uses: actions/upload-artifact@v3
        with:
          name: canon-plugin
          path: NINA.Plugin.CanonAstronomyFormat/canon.zip
```

## Support

For issues with the build process:
1. Check .NET SDK version: `dotnet --version`
2. Clean and rebuild: Delete `bin/` and `obj/` directories
3. Restore packages: `dotnet restore NINA.Plugin.CanonAstronomyFormat.csproj`
4. Review error output from build scripts

For plugin support, see main [README.md](README.md)
