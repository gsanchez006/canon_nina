using NINA.Core.Utility;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
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
        // -config "" (must come first) skips loading a user .ExifTool_config, which exiftool would run as Perl code.
        // -fast stops reading at the image data, -n prints the plain number in degrees C (no " C" suffix), -s3 prints
        // the value only, and "-" reads the file from stdin, so the RAW bytes never go to disk.
        private static readonly string[] ExifToolArguments = { "-config", "", "-fast", "-n", "-s3", "-CameraTemperature", "-" };

        // exiftool.exe is a packed Perl application; its first run on a machine unpacks itself, which can take seconds.
        private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);

        internal static string ExifToolPath =>
            Path.Combine(CoreUtil.APPLICATIONDIRECTORY, "Utility", "ExifTool", "exiftool.exe");

        /// <summary>
        /// Returns the camera temperature in degrees C, or NaN if it could not be read. Never throws.
        /// </summary>
        internal static async Task<double> ReadAsync(byte[] rawData, string exifToolPath) {
            try {
                if (!File.Exists(exifToolPath)) {
                    Logger.Warning($"{CanonAstroImage.LogPrefix}: exiftool not found at {exifToolPath} - the sensor temperature stays empty");
                    return double.NaN;
                }

                // ArgumentList quotes each argument, so the empty -config value reaches exiftool intact.
                var startInfo = new ProcessStartInfo(exifToolPath) {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                foreach (var argument in ExifToolArguments) {
                    startInfo.ArgumentList.Add(argument);
                }
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
                    Logger.Warning($"{CanonAstroImage.LogPrefix}: exiftool did not answer within {ReadTimeout.TotalSeconds:0} s - the sensor temperature stays empty");
                    return double.NaN;
                }
                if (TryParse(await output, out var celsius)) {
                    return celsius;
                }
                Logger.Warning($"{CanonAstroImage.LogPrefix}: no camera temperature in the Canon RAW data - the sensor temperature stays empty. exiftool: {(await errors).Trim()}");
                return double.NaN;
            } catch (Exception ex) {
                Logger.Warning($"{CanonAstroImage.LogPrefix}: reading the camera temperature failed: {ex.Message}");
                return double.NaN;
            }
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
