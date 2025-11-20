using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Image.FileFormat;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using NINA.Profile;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.ViewModel;
using System;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Settings = NINA.Plugin.CanonAstroImage.Properties.Settings;

namespace NINA.Plugin.CanonAstroImage {
    /// <summary>
    /// Canon Astro Image Format Plugin - Converts Canon RAW images to FITS/XISF/TIFF formats
    /// 
    /// SOLUTION: Hooks into NINA's image save pipeline via IImageSaveMediator.BeforeImageSaved event
    /// and ACTIVELY INVOKES NINA's native image writers to create astronomy format files.
    /// 
    /// Key Mechanism:
    /// 1. When image is captured and ready for save, BeforeImageSaved event fires
    /// 2. Plugin gets the IImageData object (contains pixel array and metadata)
    /// 3. Plugin reads user's selected FileType from NINA Image Settings
    /// 4. Plugin calls imageData.SaveToDisk() to invoke NINA's native writers
    /// 5. FITS/XISF/TIFF file is created with all Canon metadata
    /// 6. CR3 file created separately by Canon EDSDK (unavoidable, acceptable)
    /// 
    /// Result: User gets BOTH CR3 (from Canon) AND FITS/XISF/TIFF (from plugin)
    /// All files contain identical image data and metadata
    /// </summary>
    [Export(typeof(IPluginManifest))]
    public class CanonAstroImage : PluginBase, INotifyPropertyChanged {
        private readonly IProfileService profileService;
        private readonly IImageSaveMediator imageSaveMediator;

        [ImportingConstructor]
        public CanonAstroImage(IProfileService profileService, IImageSaveMediator imageSaveMediator) {
            try {
                if (Settings.Default.UpdateSettings) {
                    Settings.Default.Upgrade();
                    Settings.Default.UpdateSettings = false;
                    CoreUtil.SaveSettings(Settings.Default);
                }

                this.profileService = profileService;
                this.imageSaveMediator = imageSaveMediator;
                profileService.ProfileChanged += ProfileService_ProfileChanged;

                // CRITICAL: Subscribe to image save pipeline BEFORE image is written to disk
                // Plugin will actively invoke NINA's native image writers
                this.imageSaveMediator.BeforeImageSaved += ImageSaveMediator_BeforeImageSaved;
                this.imageSaveMediator.ImageSaved += ImageSaveMediator_ImageSaved;

                Logger.Info("CanonAstronomyFormat plugin initialized - Will create FITS/XISF/TIFF from Canon RAW images");
                Logger.Debug("Plugin actively invokes NINA native image writers for astronomy formats");
                Logger.Debug("Plugin can auto-delete CR3/CR2 files if enabled in plugin settings");

            } catch (Exception ex) {
                Logger.Error($"CanonAstronomyFormat: Constructor failed: {ex.Message}\n{ex.StackTrace}");
                throw;
            }
        }

        /// <summary>
        /// ACTIVE IMAGE WRITER: Invokes NINA's native image writers to create FITS/XISF/TIFF files
        /// 
        /// Strategy: 
        /// 1. BeforeImageSaved hook gives us the IImageData with pixel array and metadata
        /// 2. We extract the user's selected FileType from Image Settings
        /// 3. We call IImageData.SaveToDisk() to invoke NINA's native writers
        /// 4. This creates the astronomy format file independently of CR3 creation
        /// </summary>
        private async Task ImageSaveMediator_BeforeImageSaved(object sender, BeforeImageSavedEventArgs e) {
            try {
                var imageData = e.Image;
                
                if (imageData?.MetaData == null) {
                    return;
                }

                // Get user's selected file format from NINA Image Settings
                var userFileType = profileService.ActiveProfile.ImageFileSettings.FileType;
                var cameraName = imageData.MetaData.Camera?.Name ?? "Unknown";
                
                Logger.Info($"CanonAstronomyFormat: Processing image - Camera: {cameraName}, Output format: {userFileType}");
                Logger.Debug($"Image dimensions: {imageData.Properties.Width}x{imageData.Properties.Height}");

                // Only save in astronomy formats (not RAW which is CR3)
                if (userFileType != FileTypeEnum.RAW) {
                    // Create FileSaveInfo with user's selected format AND compression settings
                    var imageSettings = profileService.ActiveProfile.ImageFileSettings;
                    var fileSaveInfo = new FileSaveInfo {
                        FilePath = imageSettings.FilePath,
                        FilePattern = imageSettings.FilePattern,
                        FileType = userFileType,
                        // Copy all compression settings from user's Image File Settings
                        FITSCompressionType = imageSettings.FITSCompressionType,
                        FITSUseLegacyWriter = imageSettings.FITSUseLegacyWriter,
                        FITSAddFzExtension = imageSettings.FITSAddFzExtension,
                        TIFFCompressionType = imageSettings.TIFFCompressionType,
                        XISFCompressionType = imageSettings.XISFCompressionType,
                        XISFChecksumType = imageSettings.XISFChecksumType,
                        XISFByteShuffling = imageSettings.XISFByteShuffling
                    };

                    // Invoke NINA's native image writer in background thread
                    // This won't block the main save operation
                    _ = Task.Run(async () => {
                        try {
                            string compressionInfo = GetCompressionInfo(fileSaveInfo);
                            Logger.Debug($"CanonAstronomyFormat: Invoking {userFileType} writer with {compressionInfo}");
                            var outputPath = await imageData.SaveToDisk(fileSaveInfo, CancellationToken.None, forceFileType: true);
                            Logger.Info($"CanonAstronomyFormat: Successfully created {userFileType} file at {outputPath}");
                        } catch (Exception ex) {
                            Logger.Error($"CanonAstronomyFormat: Failed to save {userFileType} file: {ex.Message}\n{ex.StackTrace}");
                        }
                    });
                } else {
                    Logger.Debug("CanonAstronomyFormat: User selected RAW format (CR3) - NINA will handle natively");
                }

                await Task.CompletedTask;
            } catch (Exception ex) {
                Logger.Error($"CanonAstronomyFormat.ImageSaveMediator_BeforeImageSaved failed: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// AUTO-DELETE CR3/CR2 FILES: Deletes Canon RAW files after successful save if user enabled the setting
        /// Called AFTER the image is saved to disk and moved to final location
        /// </summary>
        private void ImageSaveMediator_ImageSaved(object sender, ImageSavedEventArgs e) {
            try {
                if (e?.PathToImage == null) return;

                // Check if auto-delete is enabled
                if (!this.AutoDeleteCanonRaw) {
                    Logger.Debug("CanonAstronomyFormat: Auto-delete disabled in plugin settings");
                    return;
                }

                // Get the path to the saved file
                var savedFilePath = e.PathToImage.LocalPath;
                var fileDirectory = Path.GetDirectoryName(savedFilePath);
                var fileNameWithoutExt = Path.GetFileNameWithoutExtension(savedFilePath);

                // Look for CR3 and CR2 files with same name in the same directory
                var cr3File = Path.Combine(fileDirectory, fileNameWithoutExt + ".cr3");
                var cr2File = Path.Combine(fileDirectory, fileNameWithoutExt + ".cr2");

                // Delete CR3 file if it exists
                if (File.Exists(cr3File)) {
                    try {
                        File.Delete(cr3File);
                        Logger.Info($"CanonAstronomyFormat: Auto-deleted CR3 file: {cr3File}");
                    } catch (Exception ex) {
                        Logger.Error($"CanonAstronomyFormat: Failed to delete CR3 file {cr3File}: {ex.Message}");
                    }
                }

                // Delete CR2 file if it exists (for older Canon models)
                if (File.Exists(cr2File)) {
                    try {
                        File.Delete(cr2File);
                        Logger.Info($"CanonAstronomyFormat: Auto-deleted CR2 file: {cr2File}");
                    } catch (Exception ex) {
                        Logger.Error($"CanonAstronomyFormat: Failed to delete CR2 file {cr2File}: {ex.Message}");
                    }
                }

            } catch (Exception ex) {
                Logger.Error($"CanonAstronomyFormat.ImageSaveMediator_ImageSaved failed: {ex.Message}\n{ex.StackTrace}");
            }
        }

        public override Task Teardown() {
            try {
                if (imageSaveMediator != null) {
                    imageSaveMediator.BeforeImageSaved -= ImageSaveMediator_BeforeImageSaved;
                    imageSaveMediator.ImageSaved -= ImageSaveMediator_ImageSaved;
                }
            } catch { }
            
            profileService.ProfileChanged -= ProfileService_ProfileChanged;
            return base.Teardown();
        }

        private void ProfileService_ProfileChanged(object sender, EventArgs e) {
            Logger.Debug("CanonAstronomyFormat: Active profile changed");
            RaisePropertyChanged(nameof(PluginVersion));
        }

        public string PluginVersion => "1.0.0.0";

        private bool autoDeleteCanonRaw;
        public bool AutoDeleteCanonRaw {
            get {
                // Load from profile settings if not already loaded
                if (!autoDeleteCanonRawLoaded) {
                    var pluginSettings = profileService.ActiveProfile.PluginSettings;
                    autoDeleteCanonRaw = pluginSettings.TryGetValue(Guid.Parse(this.Identifier), "AutoDeleteCanonRaw", out bool value) ? value : false;
                    autoDeleteCanonRawLoaded = true;
                }
                return autoDeleteCanonRaw;
            }
            set {
                if (autoDeleteCanonRaw != value) {
                    autoDeleteCanonRaw = value;
                    // Save to profile settings
                    var pluginSettings = profileService.ActiveProfile.PluginSettings;
                    pluginSettings.SetValue(Guid.Parse(this.Identifier), "AutoDeleteCanonRaw", value);
                    Logger.Info($"CanonAstronomyFormat: Auto-delete CR3/CR2 files set to {value}");
                    RaisePropertyChanged();
                }
            }
        }
        private bool autoDeleteCanonRawLoaded = false;

        private string GetCompressionInfo(FileSaveInfo fileSaveInfo) {
            return fileSaveInfo.FileType switch {
                FileTypeEnum.FITS => $"FITS - Compression: {fileSaveInfo.FITSCompressionType}, Legacy: {fileSaveInfo.FITSUseLegacyWriter}, AddFz: {fileSaveInfo.FITSAddFzExtension}",
                FileTypeEnum.TIFF => $"TIFF - Compression: {fileSaveInfo.TIFFCompressionType}",
                FileTypeEnum.XISF => $"XISF - Compression: {fileSaveInfo.XISFCompressionType}, Checksum: {fileSaveInfo.XISFChecksumType}, ByteShuffle: {fileSaveInfo.XISFByteShuffling}",
                _ => fileSaveInfo.FileType.ToString()
            };
        }

        public event PropertyChangedEventHandler PropertyChanged;
        
        protected void RaisePropertyChanged([CallerMemberName] string propertyName = null) {
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

