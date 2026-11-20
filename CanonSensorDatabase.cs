using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NINA.Plugin.CanonAstroImage {
    /// <summary>
    /// Sensor geometry of one Canon EOS model.
    /// </summary>
    /// <param name="Model">Display name, as the Canon Camera Museum lists it (North American name where there is one).</param>
    /// <param name="PixelSizeUm">Pixel pitch in micrometres: Canon's published sensor width / effective image width.</param>
    /// <param name="Width">Width in pixels of the image NINA decodes from this camera's RAW file (LibRaw's visible area:
    /// larger than the effective/JPEG size, smaller than the full readout). 0 = not known yet.</param>
    /// <param name="Height">Height in pixels of the decoded image, see <paramref name="Width"/>. 0 = not known yet.</param>
    /// <param name="Aliases">Every other name the camera reports in some region (Rebel / Kiss / xxxD, EXIF spellings).</param>
    internal sealed record CanonSensor(string Model, double PixelSizeUm, int Width, int Height, params string[] Aliases) {
        public bool HasDimensions => Width > 0 && Height > 0;
    }

    /// <summary>
    /// Built-in table of Canon EOS cameras that write CR2 or CR3 files. NINA's Canon driver reports no pixel size and
    /// no sensor dimensions, so the plugin looks the connected camera up here by the model name it reports.
    /// The table is CanonSensors.json, embedded in the DLL; tools/sensor-table/README.md explains how to add cameras.
    /// </summary>
    internal static class CanonSensorDatabase {
        internal const string ResourceName = "NINA.Plugin.CanonAstroImage.CanonSensors.json";

        private static readonly Lazy<CanonSensor[]> Sensors = new(LoadEmbedded);
        private static readonly Lazy<Dictionary<string, CanonSensor>> ByKey = new(BuildIndex);

        /// <summary>Every model in the table.</summary>
        public static IReadOnlyList<CanonSensor> All => Sensors.Value;

        /// <summary>
        /// Look up the camera by the name NINA's Canon driver reports (for example "Canon EOS R100" or
        /// "Canon EOS REBEL T7i"). Case, spacing, hyphens, the "Canon"/"EOS"/"Digital" words and the spelling of
        /// "Mark II" / "Mk II" / "m2" / "II" do not matter.
        /// </summary>
        public static bool TryFind(string? cameraName, out CanonSensor sensor) {
            sensor = null!;
            var key = NormalizeModelName(cameraName);
            return key.Length > 0 && ByKey.Value.TryGetValue(key, out sensor!);
        }

        private static readonly Regex MarkPattern = new(
            @"\b(?:MARK|MK\.?)\s*(IV|III|II|I|V|\d)\b|(?<=[0-9A-Z])\s+(III|II|IV)\b|(?<=[0-9])M(\d)\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// Reduce a camera name to a lookup key: upper case, no "CANON"/"EOS"/"DIGITAL", "Mark II"-style suffixes
        /// as "M2", and only letters and digits. "Canon EOS 5D Mark IV" -> "5DM4", "Canon EOS R5m2" -> "R5M2",
        /// "Canon EOS DIGITAL REBEL XSi" -> "REBELXSI", "Canon EOS-1D X Mark II" -> "1DXM2".
        /// </summary>
        internal static string NormalizeModelName(string? name) {
            if (string.IsNullOrWhiteSpace(name)) {
                return string.Empty;
            }
            var s = name.ToUpperInvariant().Replace('-', ' ').Replace('_', ' ');
            s = Regex.Replace(s, @"\b(CANON|EOS|DIGITAL)\b", " ", RegexOptions.CultureInvariant);
            s = Regex.Replace(s, @"\s+", " ").Trim();
            s = MarkPattern.Replace(s, m => {
                var token = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
                return "M" + RomanToDigit(token);
            });
            var sb = new StringBuilder(s.Length);
            foreach (var c in s) {
                if (char.IsLetterOrDigit(c)) {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        private static string RomanToDigit(string token) => token switch {
            "I" => "1",
            "II" => "2",
            "III" => "3",
            "IV" => "4",
            "V" => "5",
            _ => token,
        };

        private static CanonSensor[] LoadEmbedded() {
            using var stream = typeof(CanonSensorDatabase).Assembly.GetManifestResourceStream(ResourceName)
                ?? throw new InvalidOperationException($"Embedded resource {ResourceName} is missing");
            return Parse(stream);
        }

        /// <summary>
        /// Reads the CanonSensors.json format: { "cameras": [ { "model", "aliases", "sensorWidthMm", "effectiveWidth",
        /// "decodedWidth", "decodedHeight", ... } ] }. Pixel size = sensorWidthMm * 1000 / effectiveWidth, rounded to
        /// 0.01 µm like NINA's own pixel-size field. Other properties (year, sources) are documentation only.
        /// </summary>
        internal static CanonSensor[] Parse(Stream json) {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var result = new List<CanonSensor>();
            foreach (var camera in document.RootElement.GetProperty("cameras").EnumerateArray()) {
                var model = camera.GetProperty("model").GetString();
                if (string.IsNullOrWhiteSpace(model)) {
                    throw new InvalidDataException("A camera without a model name");
                }
                var sensorWidthMm = camera.GetProperty("sensorWidthMm").GetDouble();
                var effectiveWidth = camera.GetProperty("effectiveWidth").GetInt32();
                if (!(sensorWidthMm > 0) || effectiveWidth <= 0) {
                    throw new InvalidDataException($"{model}: sensorWidthMm and effectiveWidth must be positive");
                }
                var aliases = camera.TryGetProperty("aliases", out var a)
                    ? a.EnumerateArray().Select(x => x.GetString() ?? string.Empty).Where(x => x.Length > 0).ToArray()
                    : Array.Empty<string>();
                result.Add(new CanonSensor(
                    model,
                    Math.Round(sensorWidthMm * 1000.0 / effectiveWidth, 2),
                    camera.TryGetProperty("decodedWidth", out var w) ? w.GetInt32() : 0,
                    camera.TryGetProperty("decodedHeight", out var h) ? h.GetInt32() : 0,
                    aliases));
            }
            return result.ToArray();
        }

        private static Dictionary<string, CanonSensor> BuildIndex() {
            var index = new Dictionary<string, CanonSensor>(StringComparer.Ordinal);
            foreach (var sensor in Sensors.Value) {
                foreach (var name in sensor.Aliases.Prepend(sensor.Model)) {
                    var key = NormalizeModelName(name);
                    if (key.Length > 0) {
                        // First entry wins; the unit tests make sure no two models share a key.
                        index.TryAdd(key, sensor);
                    }
                }
            }
            return index;
        }

        /// <summary>Every lookup key with the models that claim it. Used by the tests to catch ambiguous aliases.</summary>
        internal static IEnumerable<IGrouping<string, CanonSensor>> KeysByModel() =>
            Sensors.Value
                .SelectMany(s => s.Aliases.Prepend(s.Model).Select(NormalizeModelName).Distinct().Select(k => (Key: k, Sensor: s)))
                .GroupBy(x => x.Key, x => x.Sensor);
    }
}
