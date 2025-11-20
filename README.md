# Canon Astronomy Format Plugin for NINA

A NINA plugin that enables Canon camera users to save images directly in astronomy-friendly formats (FITS, XISF, TIFF) instead of being restricted to Canon RAW files (.cr2/.cr3).

## Overview

By default, NINA's Canon camera driver saves all images exclusively in Canon RAW format (.cr2 for older models, .cr3 for EOS R series). While RAW files preserve full sensor data, they require third-party software to convert to usable astronomy formats like FITS.

**Canon Astronomy Format Plugin** solves this by actively intercepting the image save pipeline and invoking NINA's native image writers, allowing you to:

- ✅ Save directly to **FITS** format with compression (RICE, GZIP, HCOMPRESS)
- ✅ Save to **XISF** format with full XML metadata
- ✅ Save to **TIFF** format with metadata preservation
- ✅ Optionally auto-delete CR3/CR2 files after successful save

## How It Works

### Architecture
The plugin uses an **active image writer pattern** with event-driven hooks:

1. **BeforeImageSaved Event** - Intercepts the image before NINA's default CR3 save
2. **Active Invocation** - Directly calls NINA's native image writers with the selected format
3. **ImageSaved Event** - Optionally deletes CR3/CR2 files after successful save

### Key Technical Details
- Uses `IImageSaveMediator` for pipeline integration
- Calls `IImageData.SaveToDisk()` with `forceFileType: true` to override RAW default
- Copies ALL compression settings from user's Image File Settings
- Runs save operation asynchronously to avoid blocking main pipeline
- Stores auto-delete preference in profile settings

## Installation

1. Download the latest release DLL
2. Copy to: `%LOCALAPPDATA%\NINA\Plugins\3.0.0\Canon\`
3. Restart NINA
4. Enable the plugin in NINA's Plugin Options

## Usage

### Changing Image Format
1. Open NINA → Settings → Image File Settings
2. Select your desired format (FITS, XISF, or TIFF)
3. Configure compression settings as needed
4. Take exposures - plugin will automatically save in your selected format

### Auto-Delete CR3/CR2 Files
1. Open NINA → Settings → Plugins → Canon Astronomy Format
2. Check "Auto-Delete Canon RAW Files (CR3/CR2)"
3. Images will now automatically delete the RAW file after successful save

⚠️ **Warning**: Deleting RAW files is permanent. Ensure backups if you need the originals.

## File Output

When using this plugin, you get:
- **FITS file** (or XISF/TIFF) - Your astronomy-format image ✓
- **CR3 file** (optional, auto-deletable) - Canon's native RAW backup ✓

Both files contain identical image data and metadata.

## Requirements

- NINA 3.0.0 or later
- .NET 8.0 Windows Runtime
- Canon camera compatible with NINA

## Supported Formats

### FITS
- Compression: None, RICE, GZIP, HCOMPRESS, PLIO
- Legacy Writer: CFitsio or CSharpFits
- Optional .fz extension for compressed FITS

### XISF
- Compression: None, Zip, LZ4
- Checksum: None, SHA1, SHA256, SHA512
- Byte Shuffling: Enabled/Disabled

### TIFF
- Compression: None, LZW, ZIP, JPEG

## Code Quality

### Strengths
- Clear, well-documented architecture with extensive inline comments
- Proper error handling with try-catch blocks throughout
- Comprehensive logging for debugging
- Lazy-loaded settings with profile persistence
- Proper event subscription/unsubscription cleanup
- Asynchronous image processing to avoid blocking

### Implementation Notes
- `MyPlugin.cs` - Core plugin implementation (~250 lines)
- `Options.xaml/xaml.cs` - User settings UI
- `Properties/AssemblyInfo.cs` - Assembly metadata
- Clean SDK-style project file with minimal dependencies

## License

GNU GENERAL PUBLIC LICENSE 3.0 - See LICENSE for details

## Support

For issues, feature requests, or questions:
1. Check existing GitHub issues
2. Create a new issue with detailed description
3. Include NINA logs if reporting bugs

## Version History

### 1.0.0.0
- Active image writer implementation
- Multi-format support (FITS, XISF, TIFF)
- Auto-delete CR3/CR2 toggle
- Full compression settings integration
- Profile-based settings persistence
