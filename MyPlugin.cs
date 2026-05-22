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
    /// Solution: Hooks into NINA's image save pipeline via IImageSaveMediator.BeforeImageSaved event
    /// and actively invokes NINA's native image writers to create astronomy format files.
    ///
    /// Image History Integration (when auto-delete is enabled):
    /// - Uses event handler reordering (via reflection) to run BEFORE NINA's ImageHistoryVM
    /// - Redirects image history to point to FITS file instead of deleted CR3
    /// - Ensures NINA's UI shows the correct astronomy format file path
    /// - Only applies the redirect when "auto-delete Canon RAW" toggle is enabled
    ///
    /// Result: User gets FITS/XISF/TIFF file saved, with CR3 optionally auto-deleted.
    /// When auto-delete is enabled, image history points to FITS. When disabled, shows CR3 path.
    /// </summary>
    [Export(typeof(IPluginManifest))]
    public class CanonAstroImage : PluginBase, INotifyPropertyChanged {
        private readonly IProfileService profileService;
        private readonly IImageSaveMediator imageSaveMediator;
        private string _lastFitsPath = null;  // Store FITS path from BeforeImageSaved for use in ImageSaved

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
        /// Redirect image history to FITS path and auto-delete CR3/CR2 if enabled.
        /// Runs FIRST (via handler reordering) so e.PathToImage redirect is captured by NINA's history handler.
        /// </summary>
        private void ImageSaveMediator_ImageSaved(object sender, ImageSavedEventArgs e) {
            try {
                if (e?.PathToImage == null) return;

                if (!this.PluginEnabled) {
                    Logger.Debug("CanonAstronomyFormat: Plugin is disabled, skipping image saved handler");
                    return;
                }

                var fitsPathToUse = _lastFitsPath;
                _lastFitsPath = null;  // Clear for next image

                // Capture the original Canon RAW path BEFORE any redirect.
                // The FITS and CR3 filenames can differ (e.g. temperature token present in CR3
                // but missing in FITS when metadata isn't available at BeforeImageSaved time),
                // so we must use e.PathToImage while it still points to the CR3.
                var originalCanonPath = e.PathToImage.LocalPath;

                // Redirect image history to FITS path ONLY if auto-delete is enabled
                // (no point redirecting if we're keeping the CR3 file)
                if (this.AutoDeleteCanonRaw && !string.IsNullOrEmpty(fitsPathToUse)) {
                    Logger.Info($"CanonAstronomyFormat: Redirecting image history from CR3 to FITS");
                    e.PathToImage = new Uri(fitsPathToUse);
                    Logger.Info($"  → {fitsPathToUse}");
                }

                // AUTO-DELETE CR3/CR2 if enabled
                if (!this.AutoDeleteCanonRaw) {
                    Logger.Debug("CanonAstronomyFormat: Auto-delete disabled, keeping CR3/CR2 files");
                    return;
                }

                var fileDirectory = Path.GetDirectoryName(originalCanonPath);
                var fileNameWithoutExt = Path.GetFileNameWithoutExtension(originalCanonPath);

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

        public string PluginVersion => "1.4.0.0";

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

