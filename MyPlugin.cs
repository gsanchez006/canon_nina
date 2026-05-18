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
using System.Timers;
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

                Logger.Debug($"CanonAstronomyFormat: ImageSaved - Looking for FITS path with ExposureStart: {exposureStart:O}");

                if (exposureStart != DateTime.MinValue) {
                    if (_pendingFitsPaths.TryRemove(exposureStart, out var fitsTask)) {
                        try {
                            // Wait for FITS save to complete (safe: we're on ThreadPool thread)
                            var fitsPath = fitsTask.GetAwaiter().GetResult();
                            if (!string.IsNullOrEmpty(fitsPath)) {
                                Logger.Info($"CanonAstronomyFormat: Early redirect - Setting PathToImage to FITS: {fitsPath}");

                                // STRATEGY A: Mutate e.PathToImage early (works if our handler runs before ImageHistoryVM)
                                e.PathToImage = new Uri(fitsPath);

                                // STRATEGY B: Schedule deferred reflection fix as safety net
                                var fitsPathCopy = fitsPath;
                                var crPathCopy = originalCrPath;

                                try {
                                    var dispatcher = Application.Current?.Dispatcher;
                                    if (dispatcher == null) {
                                        Logger.Warning("CanonAstronomyFormat: Cannot schedule history update - Dispatcher is null");
                                    } else {
                                        dispatcher.BeginInvoke(
                                            DispatcherPriority.SystemIdle,
                                            new Action(() => {
                                                try {
                                                    Logger.Debug($"CanonAstronomyFormat: Dispatcher callback executing for {Path.GetFileName(crPathCopy)}");
                                                    ScheduleHistoryUpdate(crPathCopy, fitsPathCopy, attemptNumber: 0);
                                                } catch (Exception ex2) {
                                                    Logger.Error($"CanonAstronomyFormat: Exception in dispatcher callback: {ex2.Message}");
                                                }
                                            }));
                                    }
                                } catch (Exception ex) {
                                    Logger.Error($"CanonAstronomyFormat: Exception scheduling dispatcher callback: {ex.Message}");
                                }
                            } else {
                                Logger.Warning("CanonAstronomyFormat: FITS save task completed but returned empty path");
                            }
                        } catch (Exception ex) {
                            Logger.Error($"CanonAstronomyFormat: Failed to resolve FITS path: {ex.Message}\n{ex.StackTrace}");
                        }
                    } else {
                        Logger.Warning($"CanonAstronomyFormat: No FITS path found in pending dictionary for ExposureStart {exposureStart:O}. Dictionary has {_pendingFitsPaths.Count} entries");
                    }
                } else {
                    Logger.Warning("CanonAstronomyFormat: Could not get ExposureStart from MetaData");
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
        /// Schedule retries to update history with increasing delays to allow NINA to record entry first
        /// </summary>
        private void ScheduleHistoryUpdate(string oldCrPath, string newFitsPath, int attemptNumber) {
            const int maxAttempts = 5;

            if (attemptNumber >= maxAttempts) {
                Logger.Debug($"CanonAstronomyFormat: History update - max attempts reached after {maxAttempts} retries");
                return;
            }

            if (TryUpdateHistoryEntry(oldCrPath, newFitsPath)) {
                Logger.Info($"CanonAstronomyFormat: Successfully updated history entry on attempt {attemptNumber + 1}");
                return; // Success!
            }

            // Calculate delay: 200ms, 400ms, 600ms, 800ms, 1000ms
            int delayMs = 200 * (attemptNumber + 1);

            Logger.Debug($"CanonAstronomyFormat: History update attempt {attemptNumber + 1} failed, scheduling retry in {delayMs}ms");

            // Use DispatcherTimer which is managed by WPF and won't be garbage collected
            var timer = new DispatcherTimer(DispatcherPriority.Normal, Application.Current.Dispatcher) {
                Interval = TimeSpan.FromMilliseconds(delayMs)
            };

            timer.Tick += (s, e) => {
                timer.Stop();
                ScheduleHistoryUpdate(oldCrPath, newFitsPath, attemptNumber + 1);
            };

            timer.Start();
        }

        /// <summary>
        /// Update an already-recorded ImageHistoryPoint entry to point to FITS path instead of CR3
        /// Uses reflection to work around private setters
        /// Returns true if successful, false if history not yet populated or entry not found
        /// </summary>
        private bool TryUpdateHistoryEntry(string oldCrPath, string newFitsPath) {
            try {
                Logger.Debug($"CanonAstronomyFormat: TryUpdateHistoryEntry - checking imageHistoryVM");

                if (imageHistoryVM == null) {
                    Logger.Warning($"CanonAstronomyFormat: imageHistoryVM is NULL - cannot access history");
                    return false;
                }

                var history = imageHistoryVM.ObservableImageHistory;
                if (history == null) {
                    Logger.Warning($"CanonAstronomyFormat: ObservableImageHistory is NULL");
                    return false;
                }

                if (history.Count == 0) {
                    Logger.Debug($"CanonAstronomyFormat: History collection is empty (count=0)");
                    return false;
                }

                Logger.Info($"CanonAstronomyFormat: History collection has {history.Count} entries, attempting to find/update...");

                // Search for entry: first try matching the CR3 path, then fall back to last entry
                object entry = null;
                string foundPath = null;

                // Strategy 1: Search for the entry with matching CR3 path
                foreach (var item in history) {
                    var pathProp = item.GetType().GetProperty("LocalPath", BindingFlags.Public | BindingFlags.Instance);
                    var itemPath = pathProp?.GetValue(item) as string;
                    if (itemPath != null && string.Equals(itemPath, oldCrPath, StringComparison.OrdinalIgnoreCase)) {
                        entry = item;
                        foundPath = itemPath;
                        Logger.Debug($"CanonAstronomyFormat: Found entry by CR3 path match");
                        break;
                    }
                }

                // Strategy 2: Use the last entry (most recently added)
                if (entry == null && history.Count > 0) {
                    entry = history[history.Count - 1];
                    var pathProp = entry.GetType().GetProperty("LocalPath", BindingFlags.Public | BindingFlags.Instance);
                    foundPath = pathProp?.GetValue(entry) as string;
                    Logger.Debug($"CanonAstronomyFormat: Using last history entry (path: {foundPath})");
                }

                if (entry == null) {
                    Logger.Debug($"CanonAstronomyFormat: Could not find history entry to update");
                    return false;
                }

                Logger.Info($"CanonAstronomyFormat: Updating history entry (currently: {foundPath}) to {newFitsPath}");

                // Use reflection to update private-set properties
                var type = entry.GetType();
                bool localPathUpdated = false;
                bool filenameUpdated = false;

                // Update LocalPath
                var localPathProp = type.GetProperty("LocalPath", BindingFlags.Public | BindingFlags.Instance);
                if (localPathProp != null) {
                    try {
                        if (localPathProp.CanWrite) {
                            localPathProp.SetValue(entry, newFitsPath);
                        } else {
                            var backingField = type.GetField("<LocalPath>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
                            if (backingField != null) {
                                backingField.SetValue(entry, newFitsPath);
                            }
                        }
                        var currentValue = localPathProp.GetValue(entry);
                        if (string.Equals(currentValue?.ToString(), newFitsPath, StringComparison.Ordinal)) {
                            localPathUpdated = true;
                            Logger.Info($"CanonAstronomyFormat: Updated LocalPath to {newFitsPath}");
                        }
                    } catch (Exception ex) {
                        Logger.Error($"CanonAstronomyFormat: Exception updating LocalPath: {ex.Message}");
                    }
                }

                // Update Filename
                var filenameProp = type.GetProperty("Filename", BindingFlags.Public | BindingFlags.Instance);
                if (filenameProp != null) {
                    try {
                        var newFilename = Path.GetFileName(newFitsPath);
                        if (filenameProp.CanWrite) {
                            filenameProp.SetValue(entry, newFilename);
                        } else {
                            var backingField = type.GetField("<Filename>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
                            if (backingField != null) {
                                backingField.SetValue(entry, newFilename);
                            }
                        }
                        var currentValue = filenameProp.GetValue(entry);
                        if (string.Equals(currentValue?.ToString(), newFilename, StringComparison.Ordinal)) {
                            filenameUpdated = true;
                            Logger.Info($"CanonAstronomyFormat: Updated Filename to {Path.GetFileName(newFitsPath)}");
                        }
                    } catch (Exception ex) {
                        Logger.Error($"CanonAstronomyFormat: Exception updating Filename: {ex.Message}");
                    }
                }

                // Raise PropertyChanged notifications
                if (localPathUpdated || filenameUpdated) {
                    try {
                        var raiseMethod = type.GetMethod("RaisePropertyChanged", BindingFlags.NonPublic | BindingFlags.Instance)
                            ?? type.GetMethod("OnPropertyChanged", BindingFlags.NonPublic | BindingFlags.Instance);
                        if (raiseMethod != null) {
                            if (localPathUpdated) raiseMethod.Invoke(entry, new object[] { "LocalPath" });
                            if (filenameUpdated) raiseMethod.Invoke(entry, new object[] { "Filename" });
                            Logger.Info("CanonAstronomyFormat: PropertyChanged notifications raised");
                        }
                    } catch (Exception ex) {
                        Logger.Warning($"CanonAstronomyFormat: Could not raise PropertyChanged: {ex.Message}");
                    }
                    Logger.Info($"CanonAstronomyFormat: Successfully updated history entry");
                    return true;
                } else {
                    Logger.Debug($"CanonAstronomyFormat: Could not update any history properties");
                    return false;
                }
            } catch (Exception ex) {
                Logger.Error($"CanonAstronomyFormat: Exception in TryUpdateHistoryEntry: {ex.Message}");
                return false;
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

