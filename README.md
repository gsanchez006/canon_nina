# Canon Astro Image Format Plugin for NINA

<p align="center"><img src="Assets/logo-256.png" width="128" alt="Canon Astro Image plugin logo"></p>

A NINA plugin that enables Canon camera users to save images directly in astronomy-friendly formats (FITS, XISF, TIFF) instead of being restricted to Canon RAW files (.cr2/.cr3).

## Overview

By default, NINA's Canon camera driver saves all images exclusively in Canon RAW format (.cr2 for older models, .cr3 for EOS R series). While RAW files preserve full sensor data, they require third-party software to convert to usable astronomy formats like FITS.

**Canon Astro Image** solves this by actively intercepting the image save pipeline and invoking NINA's native image writers, allowing you to:

- ✅ Save directly to **FITS** format with compression (RICE, GZIP, HCOMPRESS)
- ✅ Save to **XISF** format with full XML metadata
- ✅ Save to **TIFF** format with metadata preservation
- ✅ Optionally also save Canon's original CR3/CR2 next to it

## How It Works

### Architecture
NINA writes a Canon frame as CR3/CR2 only because the frame still carries the camera's original RAW bytes. Without them, NINA's normal save writes the format selected in Image File Settings. The plugin hooks NINA's `BeforeImageSaved` event (only for cameras on NINA's native Canon driver) and works in one of two modes:

1. **"Also save Canon RAW file" off (direct save)** - The plugin detaches the RAW bytes from the frame. NINA's own save then writes FITS/XISF/TIFF: one write per frame, NINA's final file name and metadata, and image history shows the file natively. No CR3/CR2 is written.
2. **"Also save Canon RAW file" on (both files)** - The plugin writes the selected format with NINA's image writer, then NINA writes the CR3/CR2 as usual.

If the RAW bytes cannot be detached (for example after a NINA update changes its internals), the plugin logs a warning and simply saves both files for that frame. Nothing is ever deleted.

### Key Technical Details
- Uses `IImageSaveMediator.BeforeImageSaved`, which NINA raises before it writes the file
- Both-files mode calls `IImageData.SaveToDisk()` with `forceFileType: true`, using NINA's Image File Settings (path, per-image-type file pattern, compression)
- Stores the plugin's settings in the NINA profile
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

### Also Saving the Canon RAW File (CR3/CR2)
1. Open NINA → Settings → Plugins → Canon Astro Image
2. Check "Also save Canon RAW file (CR3/CR2)" to get the CR3/CR2 in addition to your selected format
3. Leave it unchecked to save only the selected format - one write per frame, faster and smaller

⚠️ **Important Notes**:
- With the option off there is no RAW copy: the FITS/XISF/TIFF file is the only copy of each frame.
- With the option off, image history shows the FITS/XISF/TIFF file. With it on, image history shows the CR3/CR2 NINA saved.
- If writing the FITS/XISF/TIFF file fails in both-files mode, NINA still saves the CR3/CR2 and the error is written to the NINA log.
- Upgrading from an earlier version keeps your choice: the old "Auto-Delete" off (the default) becomes "Also save Canon RAW file" on.

## File Output

When using this plugin, you get:
- **FITS file** (or XISF/TIFF) - Your astronomy-format image ✓
- **CR3/CR2 file** (only with "Also save Canon RAW file" on) - Canon's native RAW backup ✓

The converted file contains the undebayered sensor data with NINA's standard headers (including `BAYERPAT`), identical in both modes.

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

- Direct save (Canon RAW saving off) relies on NINA internals: the RAW bytes have no public setter, so they are detached using reflection. If a NINA update breaks this, the plugin logs `could not detach the Canon RAW data - saving both files for this frame` and you get an extra CR3/CR2 per frame until the plugin is updated.
- In direct save the selected format is the only file written. If NINA's writer fails for that specific file, there is no CR3/CR2 to fall back on (a failure that affects all writes, such as a full disk, loses the frame either way).
- With Canon RAW saving on, the FITS/XISF/TIFF file is written synchronously inside NINA's save pipeline, so each exposure's save takes the extra write time. NINA does not pass a cancellation token to this hook, so an aborted sequence cannot interrupt a write already in progress.
- Plugin settings (enabled, also save Canon RAW) are stored per NINA profile.

## License

MIT License - See LICENSE for details

## Support

For issues, feature requests, or questions:
1. Check existing GitHub issues
2. Create a new issue with detailed description
3. Include NINA logs if reporting bugs

## Version History

### 1.7.0.0
- **Changed**: The "Auto-Delete Canon RAW" toggle is replaced by "Also save Canon RAW file (CR3/CR2)"; existing settings carry over
- **Changed**: With Canon RAW saving off, NINA saves Canon frames directly in the selected format - one write per frame, no CR3/CR2 written and removed; if that is unavailable it saves both files instead
- **Removed**: all file deletion, the image-history handler reordering and other code no longer needed
- Options page wording and tooltips updated
- Built against NINA.Plugin 3.2.0.9001; requires NINA 3.2.0 or later

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
