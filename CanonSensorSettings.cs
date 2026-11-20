using NINA.Core.Utility;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Image.Interfaces;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.ViewModel;
using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace NINA.Plugin.CanonAstroImage {
    /// <summary>
    /// Fills in the pixel size and sensor dimensions NINA's Canon driver does not report.
    ///
    /// NINA copies a camera's pixel size into the profile, and its pixel size and dimensions into the framing
    /// assistant, only when the driver reports them. The Canon driver reports NaN and -1, so NINA keeps whatever the
    /// previous camera left there. When a Canon camera connects, this looks the model up in
    /// <see cref="CanonSensorDatabase"/> and sets:
    ///  - Options > Equipment > Camera > Pixel size (the profile; also used for plate solving and the FITS/XISF
    ///    XPIXSZ/YPIXSZ headers),
    ///  - the framing assistant's pixel size, width and height.
    /// For a model that is not in the table, or whose decoded image size is not known, the first Canon frame supplies
    /// what is missing: its size, and the pixel pitch from Canon's EXIF focal-plane resolution.
    /// </summary>
    internal sealed class CanonSensorSettings : IDisposable {
        // EDCamera.Category. NINA's camera decorator passes it through.
        private const string CanonDriverCategory = "Canon";

        private readonly IProfileService profileService;
        private readonly ICameraMediator cameraMediator;
        private readonly IFramingAssistantVM framingAssistant;
        private readonly Func<bool> isEnabled;
        private readonly object gate = new();

        // What the first Canon frame after a connect still has to supply. Guarded by gate.
        private bool needDimensionsFromFrame;
        private bool needPixelSizeFromFrame;
        private string connectedModel = string.Empty;

        public CanonSensorSettings(IProfileService profileService, ICameraMediator cameraMediator,
                                   IFramingAssistantVM framingAssistant, Func<bool> isEnabled) {
            this.profileService = profileService;
            this.cameraMediator = cameraMediator;
            this.framingAssistant = framingAssistant;
            this.isEnabled = isEnabled;
            cameraMediator.Connected += CameraMediator_Connected;
            cameraMediator.Disconnected += CameraMediator_Disconnected;
        }

        public void Dispose() {
            cameraMediator.Connected -= CameraMediator_Connected;
            cameraMediator.Disconnected -= CameraMediator_Disconnected;
        }

        /// <summary>Human-readable summary of the last values applied, for the options page.</summary>
        public string Status { get; private set; } = "No Canon camera connected yet";

        public event EventHandler? StatusChanged;

        private Task CameraMediator_Connected(object? sender, EventArgs e) {
            try {
                ApplyToConnectedCamera();
            } catch (Exception ex) {
                Logger.Error($"{CanonAstroImage.LogPrefix}: setting the Canon sensor size failed", ex);
            }
            return Task.CompletedTask;
        }

        private Task CameraMediator_Disconnected(object? sender, EventArgs e) {
            lock (gate) {
                needDimensionsFromFrame = false;
                needPixelSizeFromFrame = false;
                connectedModel = string.Empty;
            }
            return Task.CompletedTask;
        }

        /// <summary>
        /// Look up the connected camera and apply its sensor geometry. Does nothing when the feature is off or the
        /// connected camera is not on NINA's Canon driver. Also called when the feature is switched on.
        /// </summary>
        public void ApplyToConnectedCamera() {
            if (!isEnabled() || cameraMediator.GetDevice() is not ICamera camera || !camera.Connected) {
                return;
            }
            if (!IsCanonDriverWithoutGeometry(camera.Category, camera.PixelSizeX)) {
                return;
            }

            var name = camera.Name;
            if (CanonSensorDatabase.TryFind(name, out var sensor)) {
                lock (gate) {
                    connectedModel = name;
                    needPixelSizeFromFrame = false;
                    needDimensionsFromFrame = !sensor.HasDimensions;
                }
                Apply(name, sensor.PixelSizeUm, sensor.HasDimensions ? sensor.Width : null,
                      sensor.HasDimensions ? sensor.Height : null, "built-in Canon sensor table");
                if (!sensor.HasDimensions) {
                    Logger.Info($"{CanonAstroImage.LogPrefix}: the image size of the {name} is taken from its first frame");
                }
            } else {
                lock (gate) {
                    connectedModel = name;
                    needPixelSizeFromFrame = true;
                    needDimensionsFromFrame = true;
                }
                Logger.Warning($"{CanonAstroImage.LogPrefix}: \"{name}\" is not in the built-in Canon sensor table - the pixel size and image size are taken from its first frame. Please report the model so it can be added.");
                SetStatus($"{name}: not in the built-in table - waiting for the first frame");
            }
        }

        /// <summary>
        /// Called for every Canon frame before it is saved. Fills in whatever the table could not supply for the
        /// connected camera, once per connection.
        /// </summary>
        public async Task OnCanonFrameAsync(IImageData imageData) {
            bool needDimensions, needPixelSize;
            string model;
            lock (gate) {
                needDimensions = needDimensionsFromFrame;
                needPixelSize = needPixelSizeFromFrame;
                model = connectedModel;
                needDimensionsFromFrame = false;
                needPixelSizeFromFrame = false;
            }
            if ((!needDimensions && !needPixelSize) || !isEnabled()) {
                return;
            }

            try {
                int? width = needDimensions ? imageData.Properties.Width : null;
                int? height = needDimensions ? imageData.Properties.Height : null;
                double? pixelSize = null;
                if (needPixelSize) {
                    var exifPixelSize = await CanonExif.ReadPixelSizeAsync(imageData.Data.RAWData, CanonExif.ExifToolPath);
                    if (!double.IsNaN(exifPixelSize)) {
                        pixelSize = exifPixelSize;
                    } else {
                        Logger.Warning($"{CanonAstroImage.LogPrefix}: no focal-plane resolution in the {model} RAW data - enter the pixel size under Options > Equipment > Camera");
                    }
                }
                Apply(model, pixelSize, width, height, "first frame");
                if (width is > 0 && height is > 0) {
                    // Exactly what tools/sensor-table needs for --decoded, so the camera can be added to the table.
                    Logger.Info($"{CanonAstroImage.LogPrefix}: to add the {model} to the built-in table, report this line: model \"{model}\", decoded {width}x{height} (see tools/sensor-table/README.md)");
                }
            } catch (Exception ex) {
                Logger.Error($"{CanonAstroImage.LogPrefix}: reading the sensor size from the first Canon frame failed", ex);
            }
        }

        /// <summary>
        /// NINA's Canon driver (category "Canon") reports NaN as pixel size. A driver that does report a pixel size
        /// is never overridden, so the plugin cannot fight a future NINA version or another driver.
        /// </summary>
        internal static bool IsCanonDriverWithoutGeometry(string? category, double reportedPixelSize) =>
            string.Equals(category, CanonDriverCategory, StringComparison.Ordinal)
            && !(reportedPixelSize > 0);

        private void Apply(string model, double? pixelSize, int? width, int? height, string source) {
            if (pixelSize is > 0) {
                profileService.ActiveProfile.CameraSettings.PixelSize = pixelSize.Value;
                framingAssistant.CameraPixelSize = pixelSize.Value;
            }
            if (width is > 0 && height is > 0) {
                framingAssistant.CameraWidth = width.Value;
                framingAssistant.CameraHeight = height.Value;
            }

            var pixelText = pixelSize is > 0 ? $"{pixelSize.Value.ToString("0.00", CultureInfo.InvariantCulture)} µm" : null;
            var sizeText = width is > 0 && height is > 0 ? $"{width} x {height} px" : null;
            var applied = string.Join(", ", new[] { pixelText, sizeText }.Where(t => t != null));
            if (applied.Length == 0) {
                return;
            }
            Logger.Info($"{CanonAstroImage.LogPrefix}: {model}: set pixel size / sensor size to {applied} ({source})");
            SetStatus($"{model}: {applied} ({source})");
        }

        private void SetStatus(string status) {
            Status = status;
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
