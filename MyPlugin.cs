using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Image.FileFormat;
using NINA.Image.Interfaces;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using NINA.Profile;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using System;
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
    /// Only frames from NINA's native Canon driver are touched; other cameras are left alone.
    ///
    /// In BeforeImageSaved (which NINA raises before it writes the file):
    ///  - "Also save Canon RAW" off: detach the RAW bytes, so NINA writes only the selected format.
    ///  - "Also save Canon RAW" on:  write the selected format here; NINA then writes the CR3/CR2 as usual.
    /// If the RAW bytes cannot be detached (NINA internals changed), fall back to writing both files.
    /// </summary>
    [Export(typeof(IPluginManifest))]
    public class CanonAstroImage : PluginBase, INotifyPropertyChanged {
        private const string LogPrefix = "CanonAstroImage";

        // Profile-setting keys. Must stay unchanged so existing user profiles keep their values.
        private const string PluginEnabledKey = "PluginEnabled";
        // Stores the inverse of SaveCanonRaw. The name dates from the old "auto-delete" toggle and is kept so
        // existing profiles carry over: auto-delete off (the default) = also save Canon RAW on.
        private const string AutoDeleteCanonRawKey = "AutoDeleteCanonRaw";

        // IDevice.Category reported by NINA's native Canon (EDSDK) camera driver. Other drivers report
        // their own vendor ("ASCOM", "ZWOptical", "QHYCCD", "Nikon", "N.I.N.A." for the simulator, ...).
        private const string CanonDriverCategory = "Canon";

        private readonly IProfileService profileService;
        private readonly IImageSaveMediator imageSaveMediator;
        private readonly ICameraMediator cameraMediator;
        private readonly IPluginOptionsAccessor pluginSettings;

        [ImportingConstructor]
        public CanonAstroImage(IProfileService profileService, IImageSaveMediator imageSaveMediator, ICameraMediator cameraMediator) {
            this.profileService = profileService;
            this.imageSaveMediator = imageSaveMediator;
            this.cameraMediator = cameraMediator;

            // Reads and writes always go to the currently active profile, so profile switches are honoured.
            pluginSettings = new PluginOptionsAccessor(profileService, Guid.Parse(Identifier));

            profileService.ProfileChanged += ProfileService_ProfileChanged;
            imageSaveMediator.BeforeImageSaved += ImageSaveMediator_BeforeImageSaved;
        }

        public override Task Teardown() {
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
            get => !pluginSettings.GetValueBoolean(AutoDeleteCanonRawKey, false);
            set {
                pluginSettings.SetValueBoolean(AutoDeleteCanonRawKey, !value);
                Logger.Info($"{LogPrefix}: also save Canon RAW = {value}");
                RaisePropertyChanged();
            }
        }

        private void ProfileService_ProfileChanged(object sender, EventArgs e) {
            // The values are read from the active profile on every access; only the UI needs a nudge.
            RaisePropertyChanged(nameof(PluginEnabled));
            RaisePropertyChanged(nameof(SaveCanonRaw));
        }

        // ------------------------------------------------------------------
        // Save pipeline
        // ------------------------------------------------------------------

        private async Task ImageSaveMediator_BeforeImageSaved(object sender, BeforeImageSavedEventArgs e) {
            try {
                var imageData = e?.Image;
                if (!PluginEnabled || imageData == null) {
                    return;
                }

                // Other cameras are already saved by NINA in the selected format.
                var driverCategory = ConnectedCameraCategory();
                if (!string.Equals(driverCategory, CanonDriverCategory, StringComparison.Ordinal)) {
                    Logger.Debug($"{LogPrefix}: camera driver is '{driverCategory ?? "none"}', not Canon - skipping");
                    return;
                }

                if (!SaveCanonRaw) {
                    if (TryDetachRawData(imageData)) {
                        Logger.Info($"{LogPrefix}: NINA will save this frame as {profileService.ActiveProfile.ImageFileSettings.FileType} only");
                        return;
                    }
                    Logger.Warning($"{LogPrefix}: could not detach the Canon RAW data - saving both files for this frame");
                }

                await WriteSelectedFormatAsync(imageData);
            } catch (Exception ex) {
                Logger.Error($"{LogPrefix}: handling the Canon frame failed - NINA still saves the Canon RAW file", ex);
            }
        }

        /// <summary>
        /// Write the frame in the format selected in Image File Settings. NINA writes the CR3/CR2 afterwards.
        /// BeforeImageSavedEventArgs carries no CancellationToken, so this write cannot be cancelled by a sequence abort.
        /// </summary>
        private async Task WriteSelectedFormatAsync(IImageData imageData) {
            var fileSaveInfo = new FileSaveInfo(profileService);
            // Use the per-image-type file pattern, as NINA does for its own save, so both files are named alike.
            fileSaveInfo.FilePattern = profileService.ActiveProfile.ImageFileSettings.GetFilePattern(imageData.MetaData.Image.ImageType);

            // forceFileType: true makes NINA ignore the RAW bytes and write the selected format.
            var path = await imageData.SaveToDisk(fileSaveInfo, CancellationToken.None, forceFileType: true);
            Logger.Info($"{LogPrefix}: saved {fileSaveInfo.FileType} file {path}");
        }

        /// <summary>
        /// Detach the camera's original RAW bytes so NINA's own save writes the selected format instead of CR3/CR2.
        /// IImageArray.RAWData has a private setter on NINA's ImageArray/ImageArrayInt, so this uses reflection.
        /// Returns true if the frame no longer carries RAW bytes.
        /// </summary>
        private static bool TryDetachRawData(IImageData imageData) {
            try {
                var array = imageData.Data;
                if (array.RAWData == null) {
                    return true;
                }
                var setter = array.GetType()
                    .GetProperty(nameof(IImageArray.RAWData), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?.GetSetMethod(nonPublic: true);
                setter?.Invoke(array, new object[] { null });
                return array.RAWData == null;
            } catch (Exception ex) {
                Logger.Warning($"{LogPrefix}: detaching the Canon RAW data threw: {ex.Message}");
                return false;
            }
        }

        /// <summary>Category of the connected camera's driver, or null if none is connected or it cannot be read.</summary>
        private string ConnectedCameraCategory() {
            try {
                return cameraMediator.GetDevice()?.Category;
            } catch (Exception ex) {
                Logger.Warning($"{LogPrefix}: could not read the connected camera driver: {ex.Message}");
                return null;
            }
        }

        // ------------------------------------------------------------------
        // INotifyPropertyChanged (PluginBase does not implement it; this matches NINA's plugin template)
        // ------------------------------------------------------------------

        public event PropertyChangedEventHandler PropertyChanged;

        protected void RaisePropertyChanged([CallerMemberName] string propertyName = null) {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
