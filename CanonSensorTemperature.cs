using NINA.Core.Utility;
using System.Globalization;
using System.Threading.Tasks;

namespace NINA.Plugin.CanonAstroImage {
    /// <summary>
    /// Reads the camera temperature Canon records in each frame's CR3/CR2 maker notes, using the exiftool NINA ships.
    ///
    /// The EDSDK has no numeric temperature property (only TempStatus, an overheating warning level), so NINA's Canon
    /// driver reports NaN. The per-frame EXIF value is the only source: it is what NINA itself reads for its
    /// $$SENSORTEMP$$ file-name token, and what the old ASCOM.DSLR driver reported as CCDTemperature.
    /// </summary>
    internal static class CanonSensorTemperature {
        private const string What = "sensor temperature";

        internal static string ExifToolPath => CanonExif.ExifToolPath;

        /// <summary>
        /// Returns the camera temperature in degrees C, or NaN if it could not be read. Never throws.
        /// </summary>
        internal static async Task<double> ReadAsync(byte[] rawData, string exifToolPath) {
            var output = await CanonExif.ReadTagsAsync(rawData, exifToolPath, What, "CameraTemperature");
            if (TryParse(output, out var celsius)) {
                return celsius;
            }
            if (!string.IsNullOrWhiteSpace(output)) {
                Logger.Warning($"{CanonAstroImage.LogPrefix}: unexpected camera temperature \"{output.Trim()}\" in the Canon RAW data - the {What} stays empty");
            }
            return double.NaN;
        }

        /// <summary>
        /// Parses exiftool's "-n -s3" output: a plain number of degrees C on the first line.
        /// </summary>
        internal static bool TryParse(string? exifToolOutput, out double celsius) {
            celsius = double.NaN;
            var firstLine = exifToolOutput?.Split('\n', 2)[0].Trim();
            if (string.IsNullOrEmpty(firstLine)
                || !double.TryParse(firstLine, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) {
                return false;
            }
            // Canon stores the temperature as one byte offset by 128, so a value outside -128..127 is not a reading.
            // The pattern also rejects NaN and infinity, which double.TryParse accepts.
            if (value is not (>= -128 and <= 127)) {
                return false;
            }
            celsius = value;
            return true;
        }
    }
}
