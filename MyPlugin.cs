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
using System.Collections.Concurrent;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
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
        private readonly IImageHistoryVM imageHistoryVM;
        private readonly ConcurrentDictionary<DateTime, Task<string>> _pendingFitsPaths = new();

        [ImportingConstructor]
        public CanonAstroImage(IProfileService profileService, IImageSaveMediator imageSaveMediator, IImageHistoryVM imageHistoryVM) {
            try {
                if (Settings.Default.UpdateSettings) {
                    Settings.Default.Upgrade();
                    Settings.Default.UpdateSettings = false;
                    CoreUtil.SaveSettings(Settings.Default);
                }

                this.profileService = profileService;
                this.imageSaveMediator = imageSaveMediator;
                this.imageHistoryVM = imageHistoryVM;
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
                // Check if plugin is enabled
                if (!this.PluginEnabled) {
                    Logger.Debug("CanonAstronomyFormat: Plugin is disabled, skipping image processing");
                    await Task.CompletedTask;
                    return;
                }

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

                    // Start FITS/XISF/TIFF save in parallel with NINA's CR3 save (non-blocking)
                    // Store the task so ImageSaved handler can wait for it and redirect history
                    try {
                        var exposureStart = imageData.MetaData.Image.ExposureStart;
                        string compressionInfo = GetCompressionInfo(fileSaveInfo);
                        Logger.Debug($"CanonAstronomyFormat: Invoking {userFileType} writer with {compressionInfo}");

                        var saveTask = imageData.SaveToDisk(fileSaveInfo, CancellationToken.None, forceFileType: true);
                        _pendingFitsPaths[exposureStart] = saveTask;

                        // Log completion asynchronously when done
                        _ = saveTask.ContinueWith(t => {
                            if (t.IsCompletedSuccessfully) {
                                Logger.Info($"CanonAstronomyFormat: Successfully created {userFileType} file at {t.Result}");
                            } else {
                                Logger.Error($"CanonAstronomyFormat: Failed to save {userFileType} file: {t.Exception?.GetBaseException().Message}");
                            }
                        }, TaskContinuationOptions.ExecuteSynchronously);
                    } catch (Exception ex) {
                        Logger.Error($"CanonAstronomyFormat: Failed to start {userFileType} save: {ex.Message}\n{ex.StackTrace}");
                    }
                } else {
                    Logger.Debug("CanonAstronomyFormat: User selected RAW format (CR3) - NINA will handle natively");
                }

                await Task.CompletedTask;
            } catch (Exception ex) {
                Logger.Error($"CanonAstronomyFormat.ImageSaveMediator_BeforeImageSaved failed: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// REDIRECT IMAGE HISTORY + AUTO-DELETE CR3/CR2 FILES
        /// Called AFTER the image is saved to disk and moved to final location
        ///
        /// Strategy:
        /// 1. Look up the FITS file path from the task stored in BeforeImageSaved
        /// 2. Mutate e.PathToImage to point to FITS instead of CR3 (works if handler runs first)
        /// 3. Schedule deferred reflection-based history fix for when ImageHistoryVM runs first (safety net)
        /// 4. Auto-delete CR3/CR2 if enabled
        /// </summary>
        private void ImageSaveMediator_ImageSaved(object sender, ImageSavedEventArgs e) {
            try {
                if (e?.PathToImage == null) return;

                if (!this.PluginEnabled) {
                    Logger.Debug("CanonAstronomyFormat: Plugin is disabled, skipping history redirect");
                    return;
                }

                // Try to redirect image history to FITS path instead of CR3
                var originalCrPath = e.PathToImage.LocalPath;
                var exposureStart = e.MetaData?.Image?.ExposureStart ?? DateTime.MinValue;

                if (exposureStart != DateTime.MinValue &&
                    _pendingFitsPaths.TryRemove(exposureStart, out var fitsTask)) {
                    try {
                        // Wait for FITS save to complete (safe: we're on ThreadPool thread)
                        var fitsPath = fitsTask.GetAwaiter().GetResult();
                        if (!string.IsNullOrEmpty(fitsPath)) {
                            Logger.Debug($"CanonAstronomyFormat: Redirecting history from CR3 to {fitsPath}");

                            // STRATEGY A: Mutate e.PathToImage early (works if our handler runs before ImageHistoryVM)
                            e.PathToImage = new Uri(fitsPath);

                            // STRATEGY B: Schedule deferred reflection fix as safety net
                            // (works when ImageHistoryVM runs first and we need to fix its entry after the fact)
                            var fitsPathCopy = fitsPath;
                            var crPathCopy = originalCrPath;
                            Application.Current?.Dispatcher?.BeginInvoke(
                                DispatcherPriority.ApplicationIdle,
                                (Action)(() => TryUpdateHistoryEntry(crPathCopy, fitsPathCopy)));
                        }
                    } catch (Exception ex) {
                        Logger.Error($"CanonAstronomyFormat: Failed to resolve FITS path: {ex.Message}");
                        _pendingFitsPaths.TryRemove(exposureStart, out _);  // Cleanup on error
                    }
                }

                // AUTO-DELETE CR3/CR2 (uses updated e.PathToImage if history was redirected)
                if (!this.AutoDeleteCanonRaw) {
                    Logger.Debug("CanonAstronomyFormat: Auto-delete disabled in plugin settings");
                    return;
                }

                var savedFilePath = e.PathToImage.LocalPath;
                var fileDirectory = Path.GetDirectoryName(savedFilePath);
                var fileNameWithoutExt = Path.GetFileNameWithoutExtension(savedFilePath);

                DeleteIfExists(Path.Combine(fileDirectory, fileNameWithoutExt + ".cr3"));
                DeleteIfExists(Path.Combine(fileDirectory, fileNameWithoutExt + ".cr2"));

            } catch (Exception ex) {
                Logger.Error($"CanonAstronomyFormat.ImageSaveMediator_ImageSaved failed: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Delete a file if it exists, with logging
        /// </summary>
        private void DeleteIfExists(string path) {
            if (!File.Exists(path)) return;
            try {
                File.Delete(path);
                Logger.Info($"CanonAstronomyFormat: Auto-deleted {path}");
            } catch (Exception ex) {
                Logger.Error($"CanonAstronomyFormat: Failed to delete {path}: {ex.Message}");
            }
        }

        /// <summary>
        /// Update an already-recorded ImageHistoryPoint entry to point to FITS path instead of CR3
        /// Uses reflection to work around private setters
        /// This runs asynchronously after ImageHistoryVM has recorded the CR3 entry
        /// </summary>
        private void TryUpdateHistoryEntry(string oldCrPath, string newFitsPath) {
            try {
                var history = imageHistoryVM?.ObservableImageHistory;
                if (history == null || string.IsNullOrEmpty(oldCrPath)) return;

                // Find the entry that was just added with the CR3 path
                object entry = null;
                foreach (var item in history) {
                    var itemLocalPath = item?.LocalPath;
                    if (string.Equals(itemLocalPath, oldCrPath, StringComparison.OrdinalIgnoreCase)) {
                        entry = item;
                        break;
                    }
                }
                if (entry == null) return;

                // Use reflection to update private-set properties
                var type = entry.GetType();

                var localPathProp = type.GetProperty("LocalPath", BindingFlags.Public | BindingFlags.Instance);
                localPathProp?.SetValue(entry, newFitsPath);

                var filenameProp = type.GetProperty("Filename", BindingFlags.Public | BindingFlags.Instance);
                filenameProp?.SetValue(entry, Path.GetFileName(newFitsPath));

                // Try to raise PropertyChanged for UI update
                var raiseMethod = type.GetMethod("RaisePropertyChanged",
                    BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? type.GetMethod("OnPropertyChanged",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (raiseMethod != null) {
                    raiseMethod.Invoke(entry, new object[] { "LocalPath" });
                    raiseMethod.Invoke(entry, new object[] { "Filename" });
                }

                Logger.Info($"CanonAstronomyFormat: Updated history entry LocalPath from CR3 to {newFitsPath}");
            } catch (Exception ex) {
                Logger.Warning($"CanonAstronomyFormat: Could not update image history entry: {ex.Message}");
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

        public string PluginVersion => "1.1.0.0";

        private bool pluginEnabled;
        public bool PluginEnabled {
            get {
                // Load from profile settings if not already loaded
                if (!pluginEnabledLoaded) {
                    var pluginSettings = profileService.ActiveProfile.PluginSettings;
                    pluginEnabled = pluginSettings.TryGetValue(Guid.Parse(this.Identifier), "PluginEnabled", out bool value) ? value : true;
                    pluginEnabledLoaded = true;
                }
                return pluginEnabled;
            }
            set {
                if (pluginEnabled != value) {
                    pluginEnabled = value;
                    // Save to profile settings
                    var pluginSettings = profileService.ActiveProfile.PluginSettings;
                    pluginSettings.SetValue(Guid.Parse(this.Identifier), "PluginEnabled", value);
                    Logger.Info($"CanonAstronomyFormat: Plugin enabled set to {value}");
                    RaisePropertyChanged();
                }
            }
        }
        private bool pluginEnabledLoaded = false;

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

