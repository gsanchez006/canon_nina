using NINA.Core.Utility;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugin.CanonAstroImage {
    /// <summary>
    /// Reads tags from a frame's CR3/CR2 bytes with the exiftool NINA ships. The RAW bytes are piped on stdin, so they
    /// never go to disk.
    /// </summary>
    internal static class CanonExif {
        // exiftool.exe is a packed Perl application; its first run on a machine unpacks itself, which can take seconds.
        private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);

        internal static string ExifToolPath =>
            Path.Combine(CoreUtil.APPLICATIONDIRECTORY, "Utility", "ExifTool", "exiftool.exe");

        /// <summary>
        /// Runs exiftool on the RAW bytes and returns its output, one value per requested tag and line, or null if
        /// exiftool is missing, failed or timed out (a warning naming <paramref name="what"/> is logged). Never throws.
        /// </summary>
        /// <param name="tags">Tag names without the leading dash, for example "CameraTemperature".</param>
        internal static async Task<string?> ReadTagsAsync(byte[] rawData, string exifToolPath, string what, params string[] tags) {
            try {
                if (!File.Exists(exifToolPath)) {
                    Logger.Warning($"{CanonAstroImage.LogPrefix}: exiftool not found at {exifToolPath} - the {what} stays empty");
                    return null;
                }

                // ArgumentList quotes each argument, so the empty -config value reaches exiftool intact.
                // -config "" (must come first) skips loading a user .ExifTool_config, which exiftool would run as Perl
                // code. -fast stops reading at the image data, -n prints plain numbers (no units), -s3 prints values
                // only, and "-" reads the file from stdin.
                var startInfo = new ProcessStartInfo(exifToolPath) {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                foreach (var argument in new[] { "-config", "", "-fast", "-n", "-s3" }) {
                    startInfo.ArgumentList.Add(argument);
                }
                foreach (var tag in tags) {
                    startInfo.ArgumentList.Add("-" + tag);
                }
                startInfo.ArgumentList.Add("-");

                using var process = new Process { StartInfo = startInfo };
                process.Start();

                // Killing the process is the only reliable way to unblock a pipe write, so the timeout kills it.
                using var timeout = new CancellationTokenSource(ReadTimeout);
                using var killOnTimeout = timeout.Token.Register(() => TryKill(process));

                var output = process.StandardOutput.ReadToEndAsync();
                var errors = process.StandardError.ReadToEndAsync();
                await WriteToStdinAsync(process, rawData);
                await process.WaitForExitAsync();

                if (timeout.IsCancellationRequested) {
                    Logger.Warning($"{CanonAstroImage.LogPrefix}: exiftool did not answer within {ReadTimeout.TotalSeconds:0} s - the {what} stays empty");
                    return null;
                }
                var text = await output;
                if (string.IsNullOrWhiteSpace(text)) {
                    Logger.Warning($"{CanonAstroImage.LogPrefix}: no {what} in the Canon RAW data - it stays empty. exiftool: {(await errors).Trim()}");
                }
                return text;
            } catch (Exception ex) {
                Logger.Warning($"{CanonAstroImage.LogPrefix}: reading the {what} failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Pixel pitch in micrometres from the EXIF focal-plane resolution Canon records, or NaN. Canon derives it from
        /// the published sensor size, so it is usually within about 1-2% of the true pitch: good enough when the camera
        /// is not in the built-in table, and better than the previous camera's value.
        /// </summary>
        internal static async Task<double> ReadPixelSizeAsync(byte[] rawData, string exifToolPath) {
            var output = await ReadTagsAsync(rawData, exifToolPath, "focal-plane resolution",
                                             "FocalPlaneXResolution", "FocalPlaneResolutionUnit");
            return TryParsePixelSize(output, out var micrometres) ? micrometres : double.NaN;
        }

        /// <summary>
        /// Parses "-n -s3 -FocalPlaneXResolution -FocalPlaneResolutionUnit" output: pixels per unit on the first line,
        /// the EXIF unit code on the second (2 = inch, 3 = cm, 4 = mm; 1/missing means inch per the EXIF spec).
        /// </summary>
        internal static bool TryParsePixelSize(string? exifToolOutput, out double micrometres) {
            micrometres = double.NaN;
            var lines = (exifToolOutput ?? string.Empty).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0
                || !double.TryParse(lines[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var pixelsPerUnit)
                || !(pixelsPerUnit > 0) || double.IsInfinity(pixelsPerUnit)) {
                return false;
            }
            var unit = lines.Length > 1 && int.TryParse(lines[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var u) ? u : 2;
            var micrometresPerUnit = unit switch {
                1 or 2 => 25400.0,
                3 => 10000.0,
                4 => 1000.0,
                _ => double.NaN,
            };
            var value = micrometresPerUnit / pixelsPerUnit;
            // Every camera sensor ever made has a pitch between roughly 1 and 15 µm; anything else is a bogus tag.
            if (!(value is >= 1.0 and <= 15.0)) {
                return false;
            }
            micrometres = value;
            return true;
        }

        private static async Task WriteToStdinAsync(Process process, byte[] rawData) {
            try {
                await process.StandardInput.BaseStream.WriteAsync(rawData);
            } catch (IOException) {
                // With -fast, exiftool exits as soon as it has the maker notes near the start of the file, which
                // closes the pipe before all bytes are written. A timeout kill ends up here as well.
            }
            try {
                process.StandardInput.Close();
            } catch (IOException) {
                // Same broken pipe, surfacing on the final flush.
            }
        }

        private static void TryKill(Process process) {
            try {
                process.Kill(entireProcessTree: true);
            } catch (Exception ex) {
                // Runs on the timeout's timer thread, where an escaping exception would take NINA down. Kill throws
                // when the process already exited, and AggregateException when part of the tree could not be killed.
                Logger.Debug($"{CanonAstroImage.LogPrefix}: stopping exiftool: {ex.Message}");
            }
        }
    }
}
