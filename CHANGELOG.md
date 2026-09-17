# Changelog

All notable changes to the Canon Astro Image plugin are documented here.

## 1.6.0.0
- **Fixed (data loss)**: Auto-delete no longer removes the Canon RAW unless the converted FITS/XISF/TIFF file verifiably exists on disk. Previously a failed conversion, or selecting RAW as the output format, deleted the only copy of the frame.
- **Fixed**: Plugin settings now follow NINA profile switches instead of being cached from the first profile loaded.
- **Fixed**: The converted-file path is tracked per exposure instead of in a single shared field, so overlapping saves cannot mix up image history.
- **Fixed**: Auto-delete removes exactly the file NINA reported instead of reconstructing sibling `.cr2`/`.cr3` names.
- **Changed**: Options page uses NINA theme colours and describes the verified-before-delete behaviour.
- **Changed**: The post-build copy into `%LOCALAPPDATA%\NINA` is now opt-in (`dotnet build -p:DeployToNina=true`).
- **Changed**: Log lines are prefixed `CanonAstroImage:` (was `CanonAstronomyFormat:`).
- Removed the unused `PluginVersion` property; documentation corrected.
- Verified: image metadata is complete at conversion time; converted-file headers match NINA's final metadata. (Tested on NINA 3.2.0.9001 with a Canon EOS R100, which reports no sensor temperature, so temperature timing specifically could not be exercised.)

## 1.5.0.0
- Added Homepage and Changelog links to the plugin's info page in NINA

## 1.4.0.0
- **Fixed**: Auto-delete CR3/CR2 now correctly deletes the Canon RAW file when filename contains temperature or other metadata tokens
- Root cause: FITS file was saved before camera temperature was available, producing a different filename than the CR3 saved by NINA; deletion was using the FITS-derived path instead of the actual CR3 path

## 1.3.0.0
- **Fixed**: Image history now correctly shows FITS/XISF/TIFF path when auto-delete is enabled
- Implementation: Event handler reordering via reflection ensures plugin runs before NINA's history handler
- Conditional redirect: Only applies when "auto-delete Canon RAW" toggle is ON
- Removed unnecessary diagnostic code and dependencies

## 1.1.0.0
- Added plugin enable/disable toggle
- Auto-delete toggle now grayed out when plugin is disabled
- Added documentation about image history interaction
- Improved UI with warning about image history trade-offs

## 1.0.0.0
- Active image writer implementation
- Multi-format support (FITS, XISF, TIFF)
- Auto-delete CR3/CR2 toggle
- Full compression settings integration
- Profile-based settings persistence
