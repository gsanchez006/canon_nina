# Canon Astro Image Format Plugin for NINA

<p align="center"><img src="Assets/logo-256.png" width="128" alt="Canon Astro Image plugin logo"></p>

A NINA plugin that enables Canon camera users to save images directly in astronomy-friendly formats (FITS, XISF, TIFF) instead of being restricted to Canon RAW files (.cr2/.cr3).

## Overview

By default, NINA's Canon camera driver saves all images exclusively in Canon RAW format (.cr2 for older models, .cr3 for EOS R series). While RAW files preserve full sensor data, they require third-party software to convert to usable astronomy formats like FITS.

**Canon Astro Image** solves this by actively intercepting the image save pipeline and invoking NINA's native image writers, allowing you to:

- ✅ Save directly to **FITS** format with compression (RICE, GZIP, HCOMPRESS)
- ✅ Save to **XISF** format with full XML metadata
- ✅ Save to **TIFF** format with metadata preservation
- ✅ Optionally auto-delete CR3/CR2 files after successful save

## How It Works

### Architecture
The plugin uses an **active image writer pattern** with event-driven hooks:

1. **BeforeImageSaved Event** - Intercepts the image before NINA's default CR3 save (only when the connected camera uses NINA's native Canon driver)
2. **Active Invocation** - Directly calls NINA's native image writers with the selected format
3. **ImageSaved Event** - Optionally deletes the CR3/CR2, but only after verifying the converted file exists on disk

### Key Technical Details
- Uses `IImageSaveMediator` for pipeline integration
- Calls `IImageData.SaveToDisk()` with `forceFileType: true` to override RAW default
- Copies ALL compression settings from user's Image File Settings
- Writes the converted file inside NINA's BeforeImageSaved hook so it exists before the RAW is finalized (see Known limitations)
- Stores auto-delete preference in profile settings
- Checks the connected camera's driver on every frame: cameras on any other driver (dedicated astro cameras, ASCOM, the NINA simulator) are ignored, so the plugin can stay enabled when you switch cameras

## Installation

1. Download the latest release DLL
2. Copy to: `%LOCALAPPDATA%\NINA\Plugins\3.0.0\Canon\`
3. Restart NINA
4. Enable the plugin in NINA's Plugin Options

## Usage

### Enabling the Plugin
1. Open NINA → Settings → Plugins → Canon Astro Image
2. Check "Enable Plugin"
3. The plugin is now active and will convert images to your selected format

### Changing Image Format
1. Open NINA → Settings → Image File Settings
2. Select your desired format (FITS, XISF, or TIFF)
3. Configure compression settings as needed
4. Take exposures - plugin will automatically save in your selected format

### Auto-Delete CR3/CR2 Files
1. Open NINA → Settings → Plugins → Canon Astro Image
2. Check "Auto-Delete Canon RAW Files (CR3/CR2)"
3. The RAW file is deleted only after the plugin has verified the converted file exists on disk. If conversion fails, the RAW is kept and a warning is written to the NINA log.

⚠️ **Important Notes**:
- Deleting RAW files is permanent. Ensure backups if you need the originals.
- When auto-delete is enabled, NINA's image history correctly shows the FITS/XISF/TIFF file path instead of the deleted CR3.

## File Output

When using this plugin, you get:
- **FITS file** (or XISF/TIFF) - Your astronomy-format image ✓
- **CR3 file** (optional, auto-deletable) - Canon's native RAW backup ✓

Both files contain the same image data. Header metadata in the converted file is taken from NINA's metadata at the time of conversion (see Known limitations).

## Requirements

- NINA 3.2.0 or later
- .NET 8.0 Windows Runtime
- Canon camera connected through NINA's native Canon driver (Canon cameras connected through ASCOM are not converted)

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

## Known limitations

- The converted file is written synchronously inside NINA's save pipeline, so each exposure's save takes the extra write time. NINA does not pass a cancellation token to this hook, so an aborted sequence cannot interrupt a conversion already in progress.
- To make image history show the converted file instead of the deleted RAW, the plugin reorders NINA's internal `ImageSaved` handlers using reflection. If a NINA update changes those internals the plugin falls back to a normal subscription, logs `ImageSaved handler ordering = fallback`, and image history will show the RAW path. Conversion and deletion still work.
- Plugin settings (enabled, auto-delete) are stored per NINA profile.

## License

MIT License - See LICENSE for details

## Support

For issues, feature requests, or questions:
1. Check existing GitHub issues
2. Create a new issue with detailed description
3. Include NINA logs if reporting bugs

## Version History

### 1.6.1.0
- Frames from non-Canon cameras (dedicated astro cameras, ASCOM, simulator) are no longer converted a second time; the plugin only acts on NINA's native Canon driver

### 1.6.0.0
- **Fixed (data loss)**: RAW is never deleted unless the converted file verifiably exists on disk
- **Fixed**: settings follow profile switches; converted-file path tracked per exposure; delete uses the exact reported path
- Options page uses NINA theme colours; post-build deploy is opt-in; docs corrected

### 1.5.0.0
- Added Homepage and Changelog links to the plugin's info page in NINA

### 1.4.0.0
- **Fixed**: Auto-delete CR3/CR2 now correctly deletes the Canon RAW file when filename contains temperature or other metadata tokens
- Root cause: FITS file was saved before camera temperature was available, producing a different filename than the CR3 saved by NINA; deletion was using the FITS-derived path instead of the actual CR3 path

### 1.3.0.0
- **Fixed**: Image history now correctly shows FITS/XISF/TIFF path when auto-delete is enabled
- Implementation: Event handler reordering via reflection ensures plugin runs before NINA's history handler
- Conditional redirect: Only applies when "auto-delete Canon RAW" toggle is ON
- Removed unnecessary diagnostic code and dependencies

### 1.1.0.0
- Added plugin enable/disable toggle
- Auto-delete toggle now grayed out when plugin is disabled
- Added documentation about image history interaction
- Improved UI with warning about image history trade-offs

### 1.0.0.0
- Active image writer implementation
- Multi-format support (FITS, XISF, TIFF)
- Auto-delete CR3/CR2 toggle
- Full compression settings integration
- Profile-based settings persistence
