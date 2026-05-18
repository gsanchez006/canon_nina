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
                this.imageSaveMediator.BeforeImageSaved += ImageSaveMediator_BeforeImageSaved;

                // CRITICAL FIX: Subscribe our ImageSaved handler FIRST, before NINA's existing handlers
                // NINA's ImageHistoryVM subscribes in its constructor (before plugins load), so by default
                // it runs first and captures the CR3 path before we can redirect it.
                // Solution: Use reflection to remove all existing handlers, add ours first, then re-add others.
                ReorderImageSavedHandlersToRunFirst();

                Logger.Info("CanonAstronomyFormat plugin initialized - Will create FITS/XISF/TIFF from Canon RAW images");
                Logger.Info("Plugin's ImageSaved handler reordered to run FIRST so PathToImage redirect works");

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
        /// CRITICAL: Reorder ImageSaved event handlers so OUR handler runs FIRST.
        ///
        /// Problem: NINA's ImageHistoryVM subscribes to ImageSaved in its constructor,
        /// which runs BEFORE plugins load. By default, .NET invokes event handlers in
        /// subscription order, so NINA's handler runs first and reads e.PathToImage
        /// (the CR3 path) into the history entry via PopulateProperties() BEFORE we
        /// can modify it.
        ///
        /// Solution: Use reflection to access the event's backing delegate field,
        /// remove all existing handlers, add OUR handler first, then re-add the
        /// existing handlers. This puts our handler at the head of the invocation list.
        /// </summary>
        private void ReorderImageSavedHandlersToRunFirst() {
            try {
                var bindFlags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;

                // ImageSaveMediator delegates the event to its internal "handler" field (IImageSaveController)
                // The actual event backing field is on the ImageSaveController, not the mediator itself.
                var mediatorType = imageSaveMediator.GetType();
                var handlerField = mediatorType.GetField("handler", bindFlags);
                if (handlerField == null) {
                    Logger.Warning("CanonAstronomyFormat: Could not find 'handler' field on ImageSaveMediator, falling back to normal subscription");
                    imageSaveMediator.ImageSaved += ImageSaveMediator_ImageSaved;
                    return;
                }

                var handler = handlerField.GetValue(imageSaveMediator);
                if (handler == null) {
                    Logger.Warning("CanonAstronomyFormat: handler field is null, falling back to normal subscription");
                    imageSaveMediator.ImageSaved += ImageSaveMediator_ImageSaved;
                    return;
                }

                Logger.Info($"CanonAstronomyFormat: Found handler: {handler.GetType().FullName}");

                // Now find the ImageSaved event backing field on the handler (ImageSaveController)
                var handlerType = handler.GetType();
                System.Reflection.FieldInfo eventField = null;

                // Auto-generated event backing field is named the same as the event
                eventField = handlerType.GetField("ImageSaved", bindFlags);

                if (eventField == null) {
                    // Try walking up the inheritance chain
                    var currentType = handlerType;
                    while (currentType != null && eventField == null) {
                        eventField = currentType.GetField("ImageSaved", bindFlags);
                        if (eventField != null) break;
                        currentType = currentType.BaseType;
                    }
                }

                if (eventField == null) {
                    Logger.Warning("CanonAstronomyFormat: Could not find ImageSaved event backing field on handler, listing all fields:");
                    foreach (var f in handlerType.GetFields(bindFlags)) {
                        Logger.Info($"  Field: {f.Name} ({f.FieldType.Name})");
                    }
                    imageSaveMediator.ImageSaved += ImageSaveMediator_ImageSaved;
                    return;
                }

                Logger.Info($"CanonAstronomyFormat: Found ImageSaved event field: {eventField.Name} (Type: {eventField.FieldType.Name})");

                // Get the current delegate (combined event handlers)
                var existingDelegate = eventField.GetValue(handler) as Delegate;
                Logger.Info($"CanonAstronomyFormat: Existing handlers count: {existingDelegate?.GetInvocationList().Length ?? 0}");

                // Create our handler delegate
                var ourMethod = typeof(CanonAstroImage).GetMethod(nameof(ImageSaveMediator_ImageSaved), bindFlags);
                if (ourMethod == null) {
                    Logger.Error("CanonAstronomyFormat: Could not find our ImageSaveMediator_ImageSaved method via reflection");
                    imageSaveMediator.ImageSaved += ImageSaveMediator_ImageSaved;
                    return;
                }

                var ourDelegate = Delegate.CreateDelegate(eventField.FieldType, this, ourMethod);

                // Combine: our handler FIRST, then existing handlers
                var newDelegate = existingDelegate == null
                    ? ourDelegate
                    : Delegate.Combine(ourDelegate, existingDelegate);

                eventField.SetValue(handler, newDelegate);

                var finalDelegate = eventField.GetValue(handler) as Delegate;
                Logger.Info($"CanonAstronomyFormat: After reordering, handlers count: {finalDelegate?.GetInvocationList().Length ?? 0}, our handler is now FIRST");
            } catch (Exception ex) {
                Logger.Error($"CanonAstronomyFormat: Failed to reorder handlers: {ex.Message}\n{ex.StackTrace}");
                // Fallback to normal subscription
                imageSaveMediator.ImageSaved += ImageSaveMediator_ImageSaved;
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

                var bindFlags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase;

                // Search ALL history collections (not just ObservableImageHistory)
                // The full history is in ImageHistory (List<T>), while ObservableImageHistory is a limited stack
                var historyVmType = imageHistoryVM.GetType();
                var collectionPropNames = new[] { "ImageHistory", "ObservableImageHistory", "ObservableImageHistoryView" };
                int totalUpdates = 0;

                foreach (var propName in collectionPropNames) {
                    var collProp = historyVmType.GetProperty(propName, bindFlags);
                    if (collProp == null) continue;

                    var coll = collProp.GetValue(imageHistoryVM) as System.Collections.IList;
                    if (coll == null) continue;

                    Logger.Info($"CanonAstronomyFormat: Searching {propName} (Count: {coll.Count}) for CR3: {Path.GetFileName(originalCrPath)}");

                    for (int i = 0; i < coll.Count; i++) {
                        var item = coll[i];
                        if (item == null) continue;

                        var itemType = item.GetType();
                        var localPathProp = itemType.GetProperty("LocalPath", bindFlags);
                        var filenameProp = itemType.GetProperty("Filename", bindFlags);
                        var localPath = localPathProp?.GetValue(item) as string;

                        if (string.Equals(localPath, originalCrPath, StringComparison.OrdinalIgnoreCase)) {
                            Logger.Info($"  Found match in {propName} at index {i}, updating...");

                            if (localPathProp?.CanWrite == true) {
                                try {
                                    localPathProp.SetValue(item, newFitsPath);
                                    totalUpdates++;
                                } catch (Exception ex) {
                                    Logger.Error($"  Error updating LocalPath in {propName}: {ex.Message}");
                                }
                            }
                            if (filenameProp?.CanWrite == true) {
                                try {
                                    filenameProp.SetValue(item, Path.GetFileName(newFitsPath));
                                    totalUpdates++;
                                } catch (Exception ex) {
                                    Logger.Error($"  Error updating Filename in {propName}: {ex.Message}");
                                }
                            }
                            break;  // Each collection has at most one matching entry
                        }
                    }
                }

                Logger.Info($"CanonAstronomyFormat: TrySyncUpdateHistoryEntry completed - total properties updated: {totalUpdates}");
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

