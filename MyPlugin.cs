using NINA.Core.Enum;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Image.FileFormat;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using NINA.Profile;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Settings = NINA.Plugin.CanonAstroImage.Properties.Settings;

namespace NINA.Plugin.CanonAstroImage {
    /// <summary>
    /// Canon Astro Image plugin - creates FITS/XISF/TIFF files from Canon RAW captures.
    ///
    /// NINA writes a Canon frame as CR3/CR2 only because the frame still carries the camera's original
    /// RAW bytes (IImageArray.RAWData); without them NINA writes the format selected in Image File Settings.
    /// Only frames from NINA's native Canon driver are touched; other cameras are left alone.
    ///
    /// Auto-delete ON  - direct save: in BeforeImageSaved the RAW bytes are detached from the frame, so NINA's
    ///                   own save writes FITS/XISF/TIFF in a single write with its final file name, and image
    ///                   history shows it natively. No RAW is written, so nothing is deleted.
    ///                   If the bytes cannot be detached (NINA internals changed), fall back to the mode below
    ///                   plus delete.
    /// Auto-delete OFF - keep both: in BeforeImageSaved invoke NINA's writer for the selected format and remember
    ///                   the produced path, keyed by exposure start time; NINA then writes the CR3/CR2 as usual.
    ///
    /// Fallback delete (ImageSaved): only when a converted file was produced for this exposure and verifiably
    /// exists, point image history at it and delete the RAW. A RAW is never deleted without a verified
    /// replacement. For this path the ImageSaved handler is moved to the front of the invocation list (via
    /// reflection, with a plain-subscription fallback) so the history redirect is seen by NINA's ImageHistoryVM.
    /// </summary>
    [Export(typeof(IPluginManifest))]
    public class CanonAstroImage : PluginBase, INotifyPropertyChanged {
        private const string LogPrefix = "CanonAstroImage";

        // Profile-setting keys. Must stay unchanged so existing user profiles keep their values.
        private const string PluginEnabledKey = "PluginEnabled";
        private const string AutoDeleteCanonRawKey = "AutoDeleteCanonRaw";

        // Safety valve for the per-image map: entries are removed in ImageSaved; if that ever stops
        // happening the map is cleared rather than growing forever.
        private const int MaxTrackedImages = 64;

        // IDevice.Category reported by NINA's native Canon (EDSDK) camera driver. Other drivers report
        // their own vendor ("ASCOM", "ZWOptical", "QHYCCD", "Nikon", "N.I.N.A." for the simulator, ...).
        private const string CanonDriverCategory = "Canon";

        private readonly IProfileService profileService;
        private readonly IImageSaveMediator imageSaveMediator;
        private readonly ICameraMediator cameraMediator;
        private readonly IPluginOptionsAccessor pluginSettings;

        // Converted-file path per in-flight image, keyed by MetaData.Image.ExposureStart.Ticks.
        // BeforeImageSaved adds, ImageSaved removes. Replaces a single shared field so overlapping
        // saves cannot read each other's path.
        private readonly ConcurrentDictionary<long, string> producedFiles = new ConcurrentDictionary<long, string>();

        private bool imageSavedSubscribed;

        [ImportingConstructor]
        public CanonAstroImage(IProfileService profileService, IImageSaveMediator imageSaveMediator, ICameraMediator cameraMediator) {
            try {
                if (Settings.Default.UpdateSettings) {
                    Settings.Default.Upgrade();
                    Settings.Default.UpdateSettings = false;
                    CoreUtil.SaveSettings(Settings.Default);
                }

                this.profileService = profileService;
                this.imageSaveMediator = imageSaveMediator;
                this.cameraMediator = cameraMediator;

                // Reads and writes always go to the currently active profile, so profile switches are honoured.
                this.pluginSettings = new PluginOptionsAccessor(profileService, Guid.Parse(this.Identifier));

                profileService.ProfileChanged += ProfileService_ProfileChanged;
                this.imageSaveMediator.BeforeImageSaved += ImageSaveMediator_BeforeImageSaved;
                ReorderImageSavedHandlersToRunFirst();

                Logger.Info($"{LogPrefix}: initialized");
            } catch (Exception ex) {
                Logger.Error($"{LogPrefix}: constructor failed", ex);
                throw;
            }
        }

        public override Task Teardown() {
            try {
                imageSaveMediator.BeforeImageSaved -= ImageSaveMediator_BeforeImageSaved;
                imageSaveMediator.ImageSaved -= ImageSaveMediator_ImageSaved;
                profileService.ProfileChanged -= ProfileService_ProfileChanged;
            } catch (Exception ex) {
                Logger.Debug($"{LogPrefix}: teardown unsubscribe failed: {ex.Message}");
            }
            producedFiles.Clear();
            return base.Teardown();
        }

        // ------------------------------------------------------------------
        // Settings (stored per profile)
        // ------------------------------------------------------------------

        public bool PluginEnabled {
            get => pluginSettings.GetValueBoolean(PluginEnabledKey, true);
            set {
                pluginSettings.SetValueBoolean(PluginEnabledKey, value);
                Logger.Info($"{LogPrefix}: plugin enabled = {value}");
                RaisePropertyChanged();
            }
        }

        public bool AutoDeleteCanonRaw {
            get => pluginSettings.GetValueBoolean(AutoDeleteCanonRawKey, false);
            set {
                pluginSettings.SetValueBoolean(AutoDeleteCanonRawKey, value);
                Logger.Info($"{LogPrefix}: auto-delete Canon RAW = {value}");
                RaisePropertyChanged();
            }
        }

        private void ProfileService_ProfileChanged(object sender, EventArgs e) {
            Logger.Debug($"{LogPrefix}: active profile changed");
            // The values are read from the active profile on every access; only the UI needs a nudge.
            RaisePropertyChanged(nameof(PluginEnabled));
            RaisePropertyChanged(nameof(AutoDeleteCanonRaw));
        }

        // ------------------------------------------------------------------
        // Pipeline: BeforeImageSaved - direct save, or write the converted file
        // ------------------------------------------------------------------

        private Task ImageSaveMediator_BeforeImageSaved(object sender, BeforeImageSavedEventArgs e) {
            return ConvertAndTrackAsync("BeforeImageSaved", e?.Image);
        }

        /// <summary>
        /// Write the converted file for <paramref name="imageData"/> and remember its path for ImageSaved.
        /// Kept separate from the event handler so the conversion can be triggered from a different
        /// pipeline stage (see BeforeFinalizeImageSaved) without duplicating this logic.
        /// </summary>
        private async Task ConvertAndTrackAsync(string stage, IImageData imageData) {
            try {
                if (!PluginEnabled) {
                    Logger.Debug($"{LogPrefix}: plugin disabled, skipping conversion");
                    return;
                }

                if (imageData?.MetaData == null) {
                    return;
                }

                // Only Canon captures need converting: other cameras are already saved by NINA in the
                // selected format, so converting them would just write a second copy of the frame.
                var driverCategory = ConnectedCameraCategory();
                if (!string.Equals(driverCategory, CanonDriverCategory, StringComparison.Ordinal)) {
                    Logger.Debug($"{LogPrefix}: camera driver is '{driverCategory ?? "none"}', not Canon - skipping conversion");
                    return;
                }

                var imageSettings = profileService.ActiveProfile.ImageFileSettings;
                var userFileType = imageSettings.FileType;
                if (userFileType == FileTypeEnum.RAW) {
                    Logger.Debug($"{LogPrefix}: output format is RAW, nothing to convert");
                    return;
                }

                var cameraName = imageData.MetaData.Camera?.Name ?? "Unknown";

                if (AutoDeleteCanonRaw) {
                    if (imageData.Data?.RAWData == null) {
                        Logger.Debug($"{LogPrefix}: frame carries no RAW data, NINA saves it as {userFileType} itself");
                        return;
                    }
                    if (TryDetachRawData(imageData)) {
                        Logger.Info($"{LogPrefix}: direct save - NINA will write this frame from {cameraName} as {userFileType}, no RAW file");
                        LogMetadataSnapshot(stage, imageData.MetaData);
                        return;
                    }
                    Logger.Warning($"{LogPrefix}: direct save unavailable, falling back to convert-then-delete for this frame");
                }

                var key = CorrelationKey(imageData.MetaData);
                Logger.Info($"{LogPrefix}: converting image from {cameraName} to {userFileType} ({imageData.Properties.Width}x{imageData.Properties.Height})");
                LogMetadataSnapshot(stage, imageData.MetaData);

                var fileSaveInfo = new FileSaveInfo(profileService) {
                    FilePath = imageSettings.FilePath,
                    FilePattern = imageSettings.FilePattern,
                    FileType = userFileType,
                    FITSCompressionType = imageSettings.FITSCompressionType,
                    FITSUseLegacyWriter = imageSettings.FITSUseLegacyWriter,
                    FITSAddFzExtension = imageSettings.FITSAddFzExtension,
                    TIFFCompressionType = imageSettings.TIFFCompressionType,
                    XISFCompressionType = imageSettings.XISFCompressionType,
                    XISFChecksumType = imageSettings.XISFChecksumType,
                    XISFByteShuffling = imageSettings.XISFByteShuffling
                };

                string producedPath;
                try {
                    Logger.Debug($"{LogPrefix}: invoking {userFileType} writer ({GetCompressionInfo(fileSaveInfo)})");
                    // Awaited on purpose: the converted file must exist before NINA raises ImageSaved.
                    // BeforeImageSavedEventArgs carries no CancellationToken, so this step cannot be
                    // cancelled by a sequence abort. Known limitation.
                    producedPath = await imageData.SaveToDisk(fileSaveInfo, CancellationToken.None, forceFileType: true);
                } catch (Exception ex) {
                    Logger.Error($"{LogPrefix}: failed to write {userFileType} file - the Canon RAW will be kept", ex);
                    return;
                }

                if (string.IsNullOrEmpty(producedPath) || !File.Exists(producedPath)) {
                    Logger.Warning($"{LogPrefix}: writer returned '{producedPath}' but no file exists - the Canon RAW will be kept");
                    return;
                }

                Logger.Info($"{LogPrefix}: created {userFileType} file {producedPath}");

                if (key == 0) {
                    Logger.Warning($"{LogPrefix}: image has no ExposureStart, cannot correlate with ImageSaved - the Canon RAW will be kept");
                    return;
                }

                if (producedFiles.Count >= MaxTrackedImages) {
                    Logger.Warning($"{LogPrefix}: {producedFiles.Count} conversions were never matched by ImageSaved, clearing correlation map");
                    producedFiles.Clear();
                }
                producedFiles[key] = producedPath;
            } catch (Exception ex) {
                Logger.Error($"{LogPrefix}: conversion in {stage} failed", ex);
            }
        }

        // ------------------------------------------------------------------
        // Pipeline: ImageSaved - redirect history and delete the RAW (only with a verified replacement)
        // ------------------------------------------------------------------

        private void ImageSaveMediator_ImageSaved(object sender, ImageSavedEventArgs e) {
            try {
                if (e == null) {
                    return;
                }

                // Always consume this image's entry, even if nothing else happens, so the map never leaks.
                string producedPath = null;
                var key = CorrelationKey(e.MetaData);
                if (key != 0) {
                    producedFiles.TryRemove(key, out producedPath);
                }

                if (!PluginEnabled) {
                    Logger.Debug($"{LogPrefix}: plugin disabled, skipping ImageSaved handler");
                    return;
                }

                LogMetadataSnapshot("ImageSaved", e.MetaData);

                if (!AutoDeleteCanonRaw) {
                    Logger.Debug($"{LogPrefix}: auto-delete off, keeping Canon RAW");
                    return;
                }

                // Everything below runs only when the user asked for the RAW to be deleted.
                // Every failed check keeps the RAW: a RAW is never deleted without a verified replacement.

                if (e.PathToImage == null || !e.PathToImage.IsFile) {
                    Logger.Warning($"{LogPrefix}: ImageSaved has no local file path, keeping Canon RAW");
                    return;
                }
                var rawPath = e.PathToImage.LocalPath;

                if (!IsCanonRawExtension(Path.GetExtension(rawPath))) {
                    Logger.Debug($"{LogPrefix}: saved file '{rawPath}' is not a Canon RAW, nothing to delete");
                    return;
                }

                if (string.IsNullOrEmpty(producedPath)) {
                    Logger.Warning($"{LogPrefix}: auto-delete is on but no converted file was produced for '{rawPath}', keeping Canon RAW");
                    return;
                }

                if (!File.Exists(producedPath)) {
                    Logger.Warning($"{LogPrefix}: converted file '{producedPath}' is missing, keeping Canon RAW '{rawPath}'");
                    return;
                }

                if (SamePath(producedPath, rawPath)) {
                    Logger.Error($"{LogPrefix}: converted path equals the RAW path ('{rawPath}'), refusing to delete");
                    return;
                }

                Uri producedUri;
                try {
                    producedUri = new Uri(producedPath, UriKind.Absolute);
                } catch (Exception ex) {
                    Logger.Warning($"{LogPrefix}: cannot build a URI for '{producedPath}' ({ex.Message}), keeping Canon RAW");
                    return;
                }

                // Point image history at the converted file (this handler runs before ImageHistoryVM's).
                e.PathToImage = producedUri;
                Logger.Info($"{LogPrefix}: image history redirected to {producedPath}");

                DeleteIfExists(rawPath);
            } catch (Exception ex) {
                Logger.Error($"{LogPrefix}: ImageSaved handler failed", ex);
            }
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        /// <summary>
        /// Detach the camera's original RAW bytes from the frame so NINA's own save writes the selected format.
        /// IImageArray.RAWData has a private setter on NINA's ImageArray/ImageArrayInt, so this uses reflection.
        /// Returns false (frame untouched) if the setter cannot be found or the value did not change.
        /// </summary>
        private static bool TryDetachRawData(IImageData imageData) {
            try {
                var array = imageData.Data;
                var setter = array.GetType()
                    .GetProperty(nameof(IImageArray.RAWData), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?.GetSetMethod(nonPublic: true);
                if (setter == null) {
                    Logger.Warning($"{LogPrefix}: no RAWData setter on {array.GetType().FullName}");
                    return false;
                }
                setter.Invoke(array, new object[] { null });
                return array.RAWData == null;
            } catch (Exception ex) {
                Logger.Warning($"{LogPrefix}: detaching RAW data failed ({ex.Message})");
                return false;
            }
        }

        /// <summary>Category of the connected camera's driver, or null if none is connected or it cannot be read.</summary>
        private string ConnectedCameraCategory() {
            try {
                return cameraMediator.GetDevice()?.Category;
            } catch (Exception ex) {
                Logger.Warning($"{LogPrefix}: could not read the connected camera driver ({ex.Message})");
                return null;
            }
        }

        /// <summary>Key that identifies one exposure in both BeforeImageSaved and ImageSaved. 0 = unknown.</summary>
        private static long CorrelationKey(ImageMetaData metaData) {
            var start = metaData?.Image?.ExposureStart ?? default(DateTime);
            return start == default(DateTime) ? 0 : start.Ticks;
        }

        private static bool IsCanonRawExtension(string ext) {
            return string.Equals(ext, ".cr3", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ext, ".cr2", StringComparison.OrdinalIgnoreCase);
        }

        private static bool SamePath(string a, string b) {
            try {
                return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
            } catch {
                return true; // cannot normalise: treat as the same path so the caller refuses to delete
            }
        }

        private static void DeleteIfExists(string path) {
            if (!File.Exists(path)) {
                return;
            }
            try {
                File.Delete(path);
                Logger.Info($"{LogPrefix}: auto-deleted {path}");
            } catch (Exception ex) {
                Logger.Error($"{LogPrefix}: failed to delete {path}", ex);
            }
        }

        /// <summary>
        /// Debug-level snapshot of the metadata fields that end up in the FITS header / file name.
        /// Compare the "BeforeImageSaved" and "ImageSaved" lines for the same ExposureStart to see
        /// whether NINA populates anything after the converted file has already been written.
        /// </summary>
        private static void LogMetadataSnapshot(string stage, ImageMetaData m) {
            if (m == null) {
                return;
            }
            Logger.Debug($"{LogPrefix}: [{stage}] ExposureStart={m.Image?.ExposureStart:O} ExposureTime={m.Image?.ExposureTime} " +
                         $"ExposureNumber={m.Image?.ExposureNumber} Camera={m.Camera?.Name} Temperature={m.Camera?.Temperature} " +
                         $"SetPoint={m.Camera?.SetPoint} Gain={m.Camera?.Gain} Offset={m.Camera?.Offset}");
        }

        private static string GetCompressionInfo(FileSaveInfo fileSaveInfo) {
            return fileSaveInfo.FileType switch {
                FileTypeEnum.FITS => $"FITS - Compression: {fileSaveInfo.FITSCompressionType}, Legacy: {fileSaveInfo.FITSUseLegacyWriter}, AddFz: {fileSaveInfo.FITSAddFzExtension}",
                FileTypeEnum.TIFF => $"TIFF - Compression: {fileSaveInfo.TIFFCompressionType}",
                FileTypeEnum.XISF => $"XISF - Compression: {fileSaveInfo.XISFCompressionType}, Checksum: {fileSaveInfo.XISFChecksumType}, ByteShuffle: {fileSaveInfo.XISFByteShuffling}",
                _ => fileSaveInfo.FileType.ToString()
            };
        }

        // ------------------------------------------------------------------
        // Handler ordering
        // ------------------------------------------------------------------

        /// <summary>
        /// Put this plugin's ImageSaved handler at the front of the invocation list.
        ///
        /// NINA's ImageHistoryVM subscribes to ImageSaved before plugins load, so by default it reads
        /// e.PathToImage (the CR3) before this plugin can redirect it. There is no supported ordering
        /// API, so the event's backing delegate on the mediator's internal handler is rebuilt with this
        /// plugin first. Any failure falls back to a normal subscription (history then shows the CR3 path).
        /// </summary>
        private void ReorderImageSavedHandlersToRunFirst() {
            const BindingFlags bindFlags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            try {
                var handlerField = imageSaveMediator.GetType().GetField("handler", bindFlags);
                var handler = handlerField?.GetValue(imageSaveMediator);
                if (handler == null) {
                    SubscribeImageSavedFallback("could not find the mediator's 'handler' field");
                    return;
                }

                FieldInfo eventField = null;
                for (var t = handler.GetType(); t != null && eventField == null; t = t.BaseType) {
                    eventField = t.GetField("ImageSaved", bindFlags);
                }
                if (eventField == null) {
                    SubscribeImageSavedFallback($"could not find the ImageSaved backing field on {handler.GetType().FullName}");
                    return;
                }

                var ourMethod = typeof(CanonAstroImage).GetMethod(nameof(ImageSaveMediator_ImageSaved), bindFlags);
                var ourDelegate = Delegate.CreateDelegate(eventField.FieldType, this, ourMethod);

                // Not atomic: a handler subscribed by another thread between GetValue and SetValue would be
                // lost. Acceptable because this runs once, during plugin construction, before any capture.
                var existing = eventField.GetValue(handler) as Delegate;
                eventField.SetValue(handler, existing == null ? ourDelegate : Delegate.Combine(ourDelegate, existing));
                imageSavedSubscribed = true;

                var count = (eventField.GetValue(handler) as Delegate)?.GetInvocationList().Length ?? 0;
                Logger.Info($"{LogPrefix}: ImageSaved handler ordering = reordered (first of {count})");
            } catch (Exception ex) {
                Logger.Error($"{LogPrefix}: handler reordering threw", ex);
                if (!imageSavedSubscribed) {
                    SubscribeImageSavedFallback("reordering threw an exception");
                }
            }
        }

        private void SubscribeImageSavedFallback(string reason) {
            Logger.Warning($"{LogPrefix}: ImageSaved handler ordering = fallback ({reason}). " +
                           "Image history may show the deleted RAW path when auto-delete is on. This usually means the NINA version changed.");
            imageSaveMediator.ImageSaved += ImageSaveMediator_ImageSaved;
            imageSavedSubscribed = true;
        }

        // ------------------------------------------------------------------
        // INotifyPropertyChanged (PluginBase does not implement it; this matches NINA's plugin template)
        // ------------------------------------------------------------------

        public event PropertyChangedEventHandler PropertyChanged;

        protected void RaisePropertyChanged([CallerMemberName] string propertyName = null) {
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
