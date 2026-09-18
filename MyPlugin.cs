using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Image.FileFormat;
using NINA.Image.Interfaces;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using NINA.Profile;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugin.CanonAstroImage {
    /// <summary>
    /// Canon Astro Image plugin - saves Canon captures as FITS/XISF/TIFF, optionally alongside the Canon RAW file.
    ///
    /// NINA writes a Canon frame as CR3/CR2 only because the frame still carries the camera's original RAW bytes
    /// (IImageArray.RAWData); without them NINA's normal save writes the format selected in Image File Settings.
    /// A frame counts as a Canon frame when it carries RAW bytes of type cr2/cr3. Frames from other cameras carry no
    /// RAW bytes (or a different RAW type) and are left alone.
    ///
    /// NINA raises two events before it writes a file, in this order:
    ///  1. BeforeImageSaved: "Also save Canon RAW" off -> detach the RAW bytes (and RAW type), so NINA's own save
    ///     writes only the selected format, with NINA's retry, timeout and file naming.
    ///  2. BeforeFinalizeImageSaved: if the frame still carries RAW bytes ("Also save Canon RAW" on, or the detach
    ///     failed because NINA internals changed), write the selected format here. NINA then writes the CR3/CR2.
    ///     This event carries the custom file-name patterns other plugins add, so both files are named alike.
    /// </summary>
    [Export(typeof(IPluginManifest))]
    public sealed class CanonAstroImage : PluginBase, INotifyPropertyChanged {
        private const string LogPrefix = "CanonAstroImage";

        // Profile-setting keys. Must stay unchanged so existing user profiles keep their values.
        private const string PluginEnabledKey = "PluginEnabled";
        // Written since 1.7.1. Read first.
        private const string SaveCanonRawKey = "SaveCanonRaw";
        // Legacy key from the "auto-delete" toggle (1.7.0 and earlier). Stores the inverse of SaveCanonRaw:
        // auto-delete off (the old default) = also save Canon RAW on. Read as a fallback when the new key is absent,
        // and still written so a downgrade to 1.7.0 keeps the user's choice.
        private const string AutoDeleteCanonRawKey = "AutoDeleteCanonRaw";

        // Mirrors NINA's own policy for its file write in ImageSaveController (5 minute timeout, 3 attempts, 1 s apart).
        private static readonly TimeSpan WriteTimeout = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(1);
        private const int WriteAttempts = 3;

        // Reflection lookups are cached per concrete array type (NINA has ImageArray and ImageArrayInt).
        private static readonly ConcurrentDictionary<Type, (MethodInfo? RawData, MethodInfo? RawType)> RawSetters = new();

        private readonly IProfileService profileService;
        private readonly IImageSaveMediator imageSaveMediator;
        private readonly IPluginOptionsAccessor pluginSettings;
        private readonly Guid pluginGuid;

        [ImportingConstructor]
        public CanonAstroImage(IProfileService profileService, IImageSaveMediator imageSaveMediator) {
            ArgumentNullException.ThrowIfNull(profileService);
            ArgumentNullException.ThrowIfNull(imageSaveMediator);

            this.profileService = profileService;
            this.imageSaveMediator = imageSaveMediator;

            // Reads and writes always go to the currently active profile, so profile switches are honoured.
            pluginGuid = Guid.Parse(Identifier);
            pluginSettings = new PluginOptionsAccessor(profileService, pluginGuid);

            profileService.ProfileChanged += ProfileService_ProfileChanged;
            imageSaveMediator.BeforeImageSaved += ImageSaveMediator_BeforeImageSaved;
            imageSaveMediator.BeforeFinalizeImageSaved += ImageSaveMediator_BeforeFinalizeImageSaved;
        }

        public override Task Teardown() {
            imageSaveMediator.BeforeFinalizeImageSaved -= ImageSaveMediator_BeforeFinalizeImageSaved;
            imageSaveMediator.BeforeImageSaved -= ImageSaveMediator_BeforeImageSaved;
            profileService.ProfileChanged -= ProfileService_ProfileChanged;
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

        /// <summary>Also write Canon's original CR3/CR2 next to the FITS/XISF/TIFF file.</summary>
        public bool SaveCanonRaw {
            get {
                var settings = profileService.ActiveProfile.PluginSettings;
                if (settings.TryGetValue(pluginGuid, SaveCanonRawKey, out bool saveCanonRaw)) {
                    return saveCanonRaw;
                }
                if (settings.TryGetValue(pluginGuid, AutoDeleteCanonRawKey, out bool autoDelete)) {
                    return !autoDelete;
                }
                return true;
            }
            set {
                pluginSettings.SetValueBoolean(SaveCanonRawKey, value);
                pluginSettings.SetValueBoolean(AutoDeleteCanonRawKey, !value);
                Logger.Info($"{LogPrefix}: also save Canon RAW = {value}");
                RaisePropertyChanged();
            }
        }

        private void ProfileService_ProfileChanged(object? sender, EventArgs e) {
            // The values are read from the active profile on every access; only the UI needs a nudge.
            RaisePropertyChanged(nameof(PluginEnabled));
            RaisePropertyChanged(nameof(SaveCanonRaw));
        }

        // ------------------------------------------------------------------
        // Save pipeline
        // ------------------------------------------------------------------

        /// <summary>
        /// Runs before NINA prepares and writes the file. In direct-save mode the RAW bytes are detached here so NINA's
        /// own save writes the selected format. Nothing is written by the plugin in this handler.
        /// </summary>
        private Task ImageSaveMediator_BeforeImageSaved(object sender, BeforeImageSavedEventArgs e) {
            try {
                var imageData = e?.Image;
                if (imageData == null || !PluginEnabled || SaveCanonRaw || !IsCanonFrame(imageData.Data)) {
                    return Task.CompletedTask;
                }

                if (TryDetachRawData(imageData.Data)) {
                    Logger.Info($"{LogPrefix}: NINA will save this frame as {profileService.ActiveProfile.ImageFileSettings.FileType} only");
                } else {
                    Logger.Warning($"{LogPrefix}: could not detach the Canon RAW data - saving both files for this frame");
                }
            } catch (Exception ex) {
                Logger.Error($"{LogPrefix}: handling the Canon frame failed - NINA still saves the Canon RAW file", ex);
            }
            return Task.CompletedTask;
        }

        /// <summary>
        /// Runs after NINA has prepared the image and other plugins have added their custom file-name patterns, right
        /// before NINA writes the file. If the frame still carries RAW bytes, NINA is about to write a CR3/CR2, so the
        /// selected format is written here with the same pattern, custom tokens, timeout and retry NINA uses.
        /// </summary>
        private async Task ImageSaveMediator_BeforeFinalizeImageSaved(object sender, BeforeFinalizeImageSavedEventArgs e) {
            try {
                var imageData = e?.Image?.RawImageData;
                if (imageData == null || !PluginEnabled || !IsCanonFrame(imageData.Data)) {
                    return;
                }

                await WriteSelectedFormatAsync(imageData, e!.Patterns);
            } catch (Exception ex) {
                Logger.Error($"{LogPrefix}: writing the converted file failed - NINA still saves the Canon RAW file", ex);
            }
        }

        /// <summary>
        /// Write the frame in the format selected in Image File Settings. NINA writes the CR3/CR2 afterwards.
        /// NINA mutates FileSaveInfo.FilePath during a save, so a fresh FileSaveInfo is built for every attempt.
        /// </summary>
        private async Task WriteSelectedFormatAsync(IImageData imageData, IList<ImagePattern> customPatterns) {
            var filePattern = profileService.ActiveProfile.ImageFileSettings.GetFilePattern(imageData.MetaData.Image.ImageType);
            var fileType = profileService.ActiveProfile.ImageFileSettings.FileType;

            using var timeout = new CancellationTokenSource(WriteTimeout);
            var path = await Retry.Do(
                () => imageData.SaveToDisk(
                    new FileSaveInfo(profileService) { FilePattern = filePattern },
                    timeout.Token,
                    forceFileType: true,
                    customPatterns: customPatterns),
                RetryInterval,
                WriteAttempts);

            Logger.Info($"{LogPrefix}: saved {fileType} file {path}");
        }

        /// <summary>
        /// A Canon frame still carries the camera's RAW bytes with a cr2/cr3 RAW type. Frames from other cameras carry
        /// no RAW bytes (dedicated astro cameras, ASCOM, simulator) or a different type (Nikon: nef). Frames whose RAW
        /// bytes were already detached are no longer Canon frames for the purpose of this plugin.
        /// </summary>
        internal static bool IsCanonFrame(IImageArray? array) {
            if (array?.RAWData == null) {
                return false;
            }
            return string.Equals(array.RAWType, "cr2", StringComparison.OrdinalIgnoreCase)
                || string.Equals(array.RAWType, "cr3", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Detach the camera's original RAW bytes so NINA's own save writes the selected format instead of CR3/CR2.
        /// The RAW type is cleared as well: NINA keys its DSLR sensor-temperature file naming (exiftool on the written
        /// file, then a rename) on RAWType, which makes no sense for a FITS/XISF/TIFF file.
        /// IImageArray.RAWData/RAWType have private setters on NINA's ImageArray/ImageArrayInt, so this uses reflection.
        /// Returns true if the frame no longer carries RAW bytes.
        /// </summary>
        internal static bool TryDetachRawData(IImageArray? array) {
            try {
                if (array?.RAWData == null) {
                    return true;
                }
                var setters = RawSetters.GetOrAdd(array.GetType(), FindRawSetters);
                setters.RawData?.Invoke(array, new object?[] { null });
                setters.RawType?.Invoke(array, new object?[] { null });
                return array.RAWData == null;
            } catch (Exception ex) {
                Logger.Warning($"{LogPrefix}: detaching the Canon RAW data threw: {ex.Message}");
                return false;
            }
        }

        private static (MethodInfo? RawData, MethodInfo? RawType) FindRawSetters(Type arrayType) {
            MethodInfo? Setter(string propertyName) => arrayType
                .GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetSetMethod(nonPublic: true);
            return (Setter(nameof(IImageArray.RAWData)), Setter(nameof(IImageArray.RAWType)));
        }

        // ------------------------------------------------------------------
        // INotifyPropertyChanged (PluginBase does not implement it; this matches NINA's plugin template)
        // ------------------------------------------------------------------

        public event PropertyChangedEventHandler? PropertyChanged;

        private void RaisePropertyChanged([CallerMemberName] string? propertyName = null) {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
