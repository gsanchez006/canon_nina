# Changelog

All notable changes to the Canon Astro Image plugin are documented here.

## 1.7.0.0
- **Changed**: The "Auto-Delete Canon RAW Files" toggle is replaced by "Also save Canon RAW file (CR3/CR2)". Existing settings carry over: auto-delete off (the default) becomes Canon RAW saving on, and vice versa.
- **Changed**: With Canon RAW saving off, Canon frames are now saved directly by NINA in the selected format (FITS/XISF/TIFF). The plugin detaches the camera's RAW bytes before NINA saves, so each frame is written once instead of writing the converted file, writing the CR3/CR2 and then deleting it. The file name, headers and image history come from NINA's own save.
- **Changed**: If the RAW bytes cannot be detached (for example after a NINA update), the plugin logs a warning and saves both files for that frame. The plugin never deletes files any more.
- **Removed**: The delete path and everything that only existed for it: the `ImageSaved` handler, the reflection-based reordering of NINA's image-history handlers, the per-exposure file tracking and the path/URI safety checks. Also removed the metadata diagnostic logging, the unused `Properties/Settings` files (`UpdateSettings`, `PluginVersion`) and the manual copy of Image File Settings (NINA's `FileSaveInfo` already does it). `MyPlugin.cs` went from 483 to 186 lines.
- **Fixed**: In both-files mode the FITS/XISF/TIFF file now uses NINA's per-image-type file pattern, as NINA's own save does, so it is named consistently with the CR3/CR2.
- **Note**: In direct-save mode the selected format is the only file written, so a writer failure for that file leaves no CR3/CR2 behind. Turn on "Also save Canon RAW file" to keep the RAW.
- **Changed**: Options page wording and tooltips describe what gets saved instead of what gets deleted.
- **Changed**: Built against NINA.Plugin 3.2.0.9001 (was 3.0.0.2017-beta); minimum NINA version raised to 3.2.0.
- Canon RAW saving on (both files) works as before.

## 1.6.1.0
- **Fixed**: With a non-Canon camera connected (dedicated astro camera, ASCOM camera, NINA simulator) the plugin wrote an extra copy of every frame alongside NINA's own file, doubling save time. The plugin now checks the connected camera's driver and only converts frames from NINA's native Canon driver.

## 1.6.0.0
- **Fixed (data loss)**: Auto-delete no longer removes the Canon RAW unless the converted FITS/XISF/TIFF file verifiably exists on disk. Previously a failed conversion deleted the only copy of the frame.
- **Fixed**: Plugin settings now follow NINA profile switches instead of being cached from the first profile loaded.
- **Fixed**: The converted-file path is tracked per exposure instead of in a single shared field, so overlapping saves cannot mix up image history.
- **Fixed**: Auto-delete removes exactly the file NINA reported instead of reconstructing sibling `.cr2`/`.cr3` names.
- **Changed**: Options page uses NINA theme colours (readable in light and dark themes) and describes the verified-before-delete behaviour.
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
