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
        private string _lastFitsPath = null;  // Store FITS path from BeforeImageSaved for use in ImageSaved

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

                    // SYNCHRONOUSLY save FITS/XISF/TIFF BEFORE returning from BeforeImageSaved
                    // This ensures the FITS file is ready and we can immediately redirect the path in ImageSaved
                    try {
                        string compressionInfo = GetCompressionInfo(fileSaveInfo);
                        Logger.Debug($"CanonAstronomyFormat: Invoking {userFileType} writer with {compressionInfo}");

                        // AWAIT the save task - this blocks BeforeImageSaved but ensures FITS is created before CR3 is saved
                        var fitsPath = await imageData.SaveToDisk(fileSaveInfo, CancellationToken.None, forceFileType: true);
                        Logger.Info($"CanonAstronomyFormat: Successfully created {userFileType} file at {fitsPath}");

                        // Store the path in a simple field for ImageSaved to retrieve
                        _lastFitsPath = fitsPath;
                    } catch (Exception ex) {
                        Logger.Error($"CanonAstronomyFormat: Failed to save {userFileType} file: {ex.Message}\n{ex.StackTrace}");
                        _lastFitsPath = null;
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
        /// 1. Try early redirect via e.PathToImage (works if we run before NINA's handler)
        /// 2. Schedule retry-based reflection update to find and fix the history entry
        /// 3. Auto-delete CR3/CR2 if enabled
        /// </summary>
        private void ImageSaveMediator_ImageSaved(object sender, ImageSavedEventArgs e) {
            try {
                if (e?.PathToImage == null) return;

                if (!this.PluginEnabled) {
                    Logger.Debug("CanonAstronomyFormat: Plugin is disabled, skipping history redirect");
                    return;
                }

                var originalCrPath = e.PathToImage.LocalPath;
                var fitsPathToUse = _lastFitsPath;
                _lastFitsPath = null;  // Clear for next image

                // Attempt early redirect (may not work if NINA's handler runs first)
                if (!string.IsNullOrEmpty(fitsPathToUse)) {
                    var beforeRedirect = e.PathToImage.LocalPath;
                    Logger.Info($"CanonAstronomyFormat: Attempting early redirect");
                    Logger.Info($"  BEFORE: {beforeRedirect}");
                    e.PathToImage = new Uri(fitsPathToUse);
                    var afterRedirect = e.PathToImage.LocalPath;
                    Logger.Info($"  AFTER:  {afterRedirect}");
                    Logger.Info($"  Success: {string.Equals(afterRedirect, fitsPathToUse, StringComparison.OrdinalIgnoreCase)}");
                }

                // Try synchronous history update (on current thread, same handler invocation)
                if (!string.IsNullOrEmpty(fitsPathToUse)) {
                    TrySyncUpdateHistoryEntry(originalCrPath, fitsPathToUse);
                }

                // AUTO-DELETE CR3/CR2
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
        /// Synchronous history update: find the history entry with the CR3 path
        /// and update it to point to FITS instead. Search through ALL entries to find the match.
        /// Runs on the current thread immediately (no delays, no Dispatcher callbacks).
        /// </summary>
        private bool _dumpedVmInfo = false;
        private void TrySyncUpdateHistoryEntry(string originalCrPath, string newFitsPath) {
            try {
                // ONE-TIME: Dump all properties/fields of imageHistoryVM to find UI-bound collection
                if (!_dumpedVmInfo) {
                    _dumpedVmInfo = true;
                    Logger.Info("CanonAstronomyFormat: ===== INSPECTING imageHistoryVM =====");
                    var vmType = imageHistoryVM.GetType();
                    Logger.Info($"  Type: {vmType.FullName}");

                    var bindFlagsAll = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                    foreach (var prop in vmType.GetProperties(bindFlagsAll)) {
                        try {
                            var val = prop.GetValue(imageHistoryVM);
                            var typeName = val?.GetType().Name ?? "null";
                            if (val is System.Collections.ICollection coll) {
                                Logger.Info($"  PROP {prop.Name}: {typeName} (Count: {coll.Count})");
                            } else {
                                Logger.Info($"  PROP {prop.Name}: {typeName} = {val}");
                            }
                        } catch (Exception ex) {
                            Logger.Info($"  PROP {prop.Name}: ERROR - {ex.Message}");
                        }
                    }

                    foreach (var field in vmType.GetFields(bindFlagsAll)) {
                        try {
                            var val = field.GetValue(imageHistoryVM);
                            var typeName = val?.GetType().Name ?? "null";
                            if (val is System.Collections.ICollection coll) {
                                Logger.Info($"  FIELD {field.Name}: {typeName} (Count: {coll.Count})");
                            } else {
                                Logger.Info($"  FIELD {field.Name}: {typeName} = {val}");
                            }
                        } catch (Exception ex) {
                            Logger.Info($"  FIELD {field.Name}: ERROR - {ex.Message}");
                        }
                    }
                    Logger.Info("CanonAstronomyFormat: ===== END INSPECTION =====");
                }

                var history = imageHistoryVM?.ObservableImageHistory;
                if (history == null || history.Count == 0) {
                    Logger.Info("CanonAstronomyFormat: TrySyncUpdateHistoryEntry - History collection is null or empty");
                    return;
                }

                var bindFlags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase;

                // Search through ALL entries to find one with the CR3 path we're replacing
                object foundEntry = null;
                int foundIndex = -1;
                for (int i = 0; i < history.Count; i++) {
                    var item = history[i];
                    if (item == null) continue;

                    var itemType = item.GetType();
                    var localPathProp = itemType.GetProperty("LocalPath", bindFlags);
                    var localPath = localPathProp?.GetValue(item) as string;

                    if (string.Equals(localPath, originalCrPath, StringComparison.OrdinalIgnoreCase)) {
                        foundEntry = item;
                        foundIndex = i;
                        Logger.Info($"CanonAstronomyFormat: TrySyncUpdateHistoryEntry - Found matching entry at index {i}");
                        break;
                    }
                }

                if (foundEntry == null) {
                    Logger.Info($"CanonAstronomyFormat: TrySyncUpdateHistoryEntry - No entry found with CR3 path: {originalCrPath}");
                    Logger.Info($"CanonAstronomyFormat: History has {history.Count} entries. Showing first 3:");
                    for (int i = 0; i < history.Count && i < 3; i++) {
                        var item = history[i];
                        if (item != null) {
                            var itemType = item.GetType();
                            var localPathProp = itemType.GetProperty("LocalPath", bindFlags);
                            var localPath = localPathProp?.GetValue(item) as string;
                            Logger.Info($"  Entry {i}: {localPath}");
                        }
                    }
                    return;
                }

                var entryType = foundEntry.GetType();
                var beforeLocalPath = entryType.GetProperty("LocalPath", bindFlags)?.GetValue(foundEntry) as string;
                var beforeFilename = entryType.GetProperty("Filename", bindFlags)?.GetValue(foundEntry) as string;

                Logger.Info($"CanonAstronomyFormat:   BEFORE update - LocalPath: '{beforeLocalPath}', Filename: '{beforeFilename}'");
                Logger.Info($"CanonAstronomyFormat:   Will UPDATE to - LocalPath: '{newFitsPath}', Filename: '{Path.GetFileName(newFitsPath)}'");

                // Get LocalPath and Filename properties for update
                var localPathProp2 = entryType.GetProperty("LocalPath", bindFlags);
                var filenameProp = entryType.GetProperty("Filename", bindFlags);

                // Update if writable
                if (localPathProp2?.CanWrite == true) {
                    try {
                        localPathProp2.SetValue(foundEntry, newFitsPath);
                        Logger.Info($"CanonAstronomyFormat: Successfully updated LocalPath to {newFitsPath}");
                    } catch (Exception ex) {
                        Logger.Error($"CanonAstronomyFormat: Error updating LocalPath: {ex.Message}");
                    }
                } else {
                    Logger.Info($"CanonAstronomyFormat: LocalPath property not writable (CanWrite: {localPathProp2?.CanWrite})");
                }
                if (filenameProp?.CanWrite == true) {
                    try {
                        filenameProp.SetValue(foundEntry, Path.GetFileName(newFitsPath));
                        Logger.Info($"CanonAstronomyFormat: Successfully updated Filename to {Path.GetFileName(newFitsPath)}");
                    } catch (Exception ex) {
                        Logger.Error($"CanonAstronomyFormat: Error updating Filename: {ex.Message}");
                    }
                }
            } catch (Exception ex) {
                Logger.Error($"CanonAstronomyFormat.TrySyncUpdateHistoryEntry failed: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Retry-based history update: find the ImageHistoryPoint entry with the CR3 path
        /// and update it to point to the FITS file via reflection.
        /// Retries up to 20 times with 50ms delays to handle timing of NINA's history recording.
        /// </summary>
        private void TryUpdateHistoryEntry(string originalCrPath, string newFitsPath, int retryCount) {
            const int maxRetries = 20;
            const int delayMs = 50;

            try {
                var history = imageHistoryVM?.ObservableImageHistory;
                if (history == null) {
                    Logger.Debug("CanonAstronomyFormat: ObservableImageHistory is null, cannot update entry");
                    return;
                }

                Logger.Info($"CanonAstronomyFormat: TryUpdateHistoryEntry ENTER - History count: {history.Count}, CR3 path: {originalCrPath}, retry: {retryCount}");

                // Search for entry with the CR3 path OR an entry with empty LocalPath (being populated)
                object foundCr3Entry = null;
                object foundEmptyEntry = null;
                int itemCount = 0;
                foreach (var item in history) {
                    if (item == null) continue;

                    itemCount++;
                    var itemType = item.GetType();
                    var localPathProp = itemType.GetProperty("LocalPath", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.IgnoreCase);
                    var localPath = localPathProp?.GetValue(item) as string;

                    if (retryCount == 0) {
                        Logger.Info($"CanonAstronomyFormat: History item #{itemCount} - LocalPath: '{localPath}', Type: {itemType.Name}");
                    }

                    // Check if this is our CR3 entry
                    if (string.Equals(localPath, originalCrPath, StringComparison.OrdinalIgnoreCase)) {
                        foundCr3Entry = item;
                        break;
                    }

                    // Or if it's an empty entry (still being populated by NINA)
                    if (string.IsNullOrEmpty(localPath) && foundEmptyEntry == null) {
                        foundEmptyEntry = item;
                    }
                }

                if (foundCr3Entry != null) {
                    // Found the CR3 entry - update it to FITS
                    Logger.Info($"CanonAstronomyFormat: FOUND CR3 history entry! Updating to FITS (retry #{retryCount})");

                    var entryType = foundCr3Entry.GetType();
                    var localPathProp = entryType.GetProperty("LocalPath", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.IgnoreCase);
                    var filenameProp = entryType.GetProperty("Filename", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.IgnoreCase);

                    if (localPathProp?.CanWrite == true) {
                        localPathProp.SetValue(foundCr3Entry, newFitsPath);
                    }
                    if (filenameProp?.CanWrite == true) {
                        filenameProp.SetValue(foundCr3Entry, Path.GetFileName(newFitsPath));
                    }

                    Logger.Info($"CanonAstronomyFormat: Updated history entry to {newFitsPath}");
                    return;
                }

                if (foundEmptyEntry != null) {
                    // Found an empty entry - wait for it to be populated, then check if it's ours
                    if (retryCount < maxRetries) {
                        Logger.Info($"CanonAstronomyFormat: Found empty entry in history, retrying to check if it becomes our CR3 (attempt {retryCount + 1}/{maxRetries})");
                        var delayedRetry = async () => {
                            await Task.Delay(delayMs);
                            Application.Current?.Dispatcher?.BeginInvoke(
                                DispatcherPriority.ApplicationIdle,
                                (Action)(() => TryUpdateHistoryEntry(originalCrPath, newFitsPath, retryCount + 1)));
                        };
                        _ = delayedRetry();
                    } else {
                        Logger.Warning($"CanonAstronomyFormat: Found empty entry but it never got populated with our CR3 path after {maxRetries} retries");
                    }
                } else {
                    // No empty entry and no CR3 entry found
                    if (retryCount < maxRetries) {
                        Logger.Info($"CanonAstronomyFormat: No matching entry found yet, retrying (attempt {retryCount + 1}/{maxRetries})");
                        var delayedRetry = async () => {
                            await Task.Delay(delayMs);
                            Application.Current?.Dispatcher?.BeginInvoke(
                                DispatcherPriority.ApplicationIdle,
                                (Action)(() => TryUpdateHistoryEntry(originalCrPath, newFitsPath, retryCount + 1)));
                        };
                        _ = delayedRetry();
                    } else {
                        Logger.Warning($"CanonAstronomyFormat: Could not find history entry after {maxRetries} retries ({itemCount} items searched)");
                    }
                }

            } catch (Exception ex) {
                Logger.Error($"CanonAstronomyFormat.TryUpdateHistoryEntry failed: {ex.Message}\n{ex.StackTrace}");
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

