using System.IO;
using Xunit;

namespace NINA.Plugin.CanonAstroImage.Tests;

public class CanonSensorDatabaseTests {
    [Theory]
    [InlineData("Canon EOS R100", "R100")]
    [InlineData("Canon EOS 5D Mark IV", "5DM4")]
    [InlineData("Canon EOS 5D Mark II", "5DM2")]
    [InlineData("Canon EOS R5m2", "R5M2")]
    [InlineData("EOS R5 Mark II", "R5M2")]
    [InlineData("Canon EOS-1D X Mark II", "1DXM2")]
    [InlineData("Canon EOS-1D Mark II N", "1DM2N")]
    [InlineData("Canon EOS-1Ds Mark III", "1DSM3")]
    [InlineData("Canon EOS DIGITAL REBEL XSi", "REBELXSI")]
    [InlineData("Canon EOS Kiss Digital X", "KISSX")]
    [InlineData("Canon EOS 200D II", "200DM2")]
    [InlineData("Canon EOS M50m2", "M50M2")]
    [InlineData("Canon EOS M2", "M2")]
    [InlineData("Canon EOS M", "M")]
    [InlineData("canon eos ra", "RA")]
    [InlineData("Canon EOS 5DS R", "5DSR")]
    [InlineData("  Canon   EOS  RP ", "RP")]
    [InlineData(null, "")]
    [InlineData("", "")]
    public void NormalizeModelName_IgnoresSpellingDifferences(string? name, string expected) {
        Assert.Equal(expected, CanonSensorDatabase.NormalizeModelName(name));
    }

    [Fact]
    public void Parse_ComputesThePixelSizeFromTheSpec() {
        const string json = """
            { "cameras": [
              { "model": "EOS Test", "aliases": ["EOS Test Alias"], "sensorWidthMm": 22.3, "effectiveWidth": 6000,
                "decodedWidth": 6024, "decodedHeight": 4020, "year": 2030, "specSource": "x" },
              { "model": "EOS Test 2", "sensorWidthMm": 36.0, "effectiveWidth": 6000 },
            ] }
            """;
        var sensors = CanonSensorDatabase.Parse(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)));

        Assert.Equal(2, sensors.Length);
        Assert.Equal(3.72, sensors[0].PixelSizeUm);
        Assert.Equal(new[] { "EOS Test Alias" }, sensors[0].Aliases);
        Assert.True(sensors[0].HasDimensions);
        Assert.Equal(6.0, sensors[1].PixelSizeUm);
        Assert.False(sensors[1].HasDimensions);
        Assert.Empty(sensors[1].Aliases);
    }

    [Theory]
    [InlineData("""{ "cameras": [ { "model": "", "sensorWidthMm": 22.3, "effectiveWidth": 6000 } ] }""")]
    [InlineData("""{ "cameras": [ { "model": "EOS X", "sensorWidthMm": 0, "effectiveWidth": 6000 } ] }""")]
    [InlineData("""{ "cameras": [ { "model": "EOS X", "sensorWidthMm": 22.3 } ] }""")]
    [InlineData("""{ "camera": [] }""")]
    public void Parse_RejectsBrokenEntries(string json) {
        Assert.ThrowsAny<Exception>(() => CanonSensorDatabase.Parse(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json))));
    }

    [Fact]
    public void NoTwoModelsShareALookupKey() {
        var clashes = CanonSensorDatabase.KeysByModel()
            .Where(g => g.Select(s => s.Model).Distinct().Count() > 1)
            .Select(g => $"{g.Key}: {string.Join(", ", g.Select(s => s.Model))}")
            .ToList();
        Assert.Empty(clashes);
    }

    [Fact]
    public void EveryEntryIsPlausible() {
        Assert.NotEmpty(CanonSensorDatabase.All);
        foreach (var sensor in CanonSensorDatabase.All) {
            Assert.InRange(sensor.PixelSizeUm, 2.0, 9.0);
            Assert.True(sensor.Width == 0 == (sensor.Height == 0), sensor.Model);
            if (sensor.HasDimensions) {
                Assert.InRange(sensor.Width, 2000, 9000);
                Assert.InRange(sensor.Height, 1500, 6000);
                Assert.True(sensor.Width > sensor.Height, sensor.Model);
                // Pixel size x width must give a sensor between APS-C (about 22 mm) and full frame (about 36 mm)
                // plus the few hundred decoded pixels beyond the effective area.
                var widthMm = sensor.PixelSizeUm * sensor.Width / 1000.0;
                Assert.InRange(widthMm, 21.5, 37.5);
            }
        }
    }

    [Theory]
    // Canon EOS R100: NINA decodes 6024 x 4020 (checked against the plugin author's FITS files).
    [InlineData("Canon EOS R100", "EOS R100", 3.72, 6024, 4020)]
    // Canon EOS RP: NINA decodes 6264 x 4180 (checked against a FITS file written by NINA).
    [InlineData("Canon EOS RP", "EOS RP", 5.75, 6264, 4180)]
    public void TryFind_ReturnsWhatNinaDecodes(string reportedName, string model, double pixelSize, int width, int height) {
        Assert.True(CanonSensorDatabase.TryFind(reportedName, out var sensor));
        Assert.Equal(model, sensor.Model);
        Assert.Equal(pixelSize, sensor.PixelSizeUm, 2);
        Assert.Equal(width, sensor.Width);
        Assert.Equal(height, sensor.Height);
    }

    [Theory]
    [InlineData("Canon EOS Rebel T7i", "Canon EOS 800D")]
    [InlineData("Canon EOS Kiss X9i", "Canon EOS 800D")]
    [InlineData("Canon EOS REBEL T8i", "Canon EOS 850D")]
    [InlineData("Canon EOS Kiss X10i", "Canon EOS 850D")]
    [InlineData("Canon EOS Rebel SL3", "Canon EOS 250D")]
    [InlineData("Canon EOS 200D II", "Canon EOS 250D")]
    [InlineData("Canon EOS DIGITAL REBEL XTi", "Canon EOS 400D DIGITAL")]
    [InlineData("Canon EOS Kiss M", "Canon EOS M50")]
    [InlineData("Canon EOS Kiss M2", "Canon EOS M50 Mark II")]
    [InlineData("Canon EOS R5m2", "Canon EOS R5 Mark II")]
    public void TryFind_FindsRegionalNamesOfTheSameCamera(string regionalName, string otherName) {
        Assert.True(CanonSensorDatabase.TryFind(regionalName, out var a), regionalName);
        Assert.True(CanonSensorDatabase.TryFind(otherName, out var b), otherName);
        Assert.Same(a, b);
    }

    /// <summary>Every model string found in the EXIF of the raw.pixls.us samples the table was built from.</summary>
    [Theory]
    [InlineData("Canon EOS 1000D")]
    [InlineData("Canon EOS 100D")]
    [InlineData("Canon EOS 1100D")]
    [InlineData("Canon EOS 1200D")]
    [InlineData("Canon EOS 1300D")]
    [InlineData("Canon EOS 2000D")]
    [InlineData("Canon EOS 200D")]
    [InlineData("Canon EOS 200D II")]
    [InlineData("Canon EOS 20D")]
    [InlineData("Canon EOS 250D")]
    [InlineData("Canon EOS 30D")]
    [InlineData("Canon EOS 350D DIGITAL")]
    [InlineData("Canon EOS 4000D")]
    [InlineData("Canon EOS 400D DIGITAL")]
    [InlineData("Canon EOS 40D")]
    [InlineData("Canon EOS 450D")]
    [InlineData("Canon EOS 500D")]
    [InlineData("Canon EOS 50D")]
    [InlineData("Canon EOS 550D")]
    [InlineData("Canon EOS 5D")]
    [InlineData("Canon EOS 5D Mark II")]
    [InlineData("Canon EOS 5D Mark III")]
    [InlineData("Canon EOS 5D Mark IV")]
    [InlineData("Canon EOS 5DS")]
    [InlineData("Canon EOS 5DS R")]
    [InlineData("Canon EOS 600D")]
    [InlineData("Canon EOS 60D")]
    [InlineData("Canon EOS 650D")]
    [InlineData("Canon EOS 6D")]
    [InlineData("Canon EOS 6D Mark II")]
    [InlineData("Canon EOS 700D")]
    [InlineData("Canon EOS 70D")]
    [InlineData("Canon EOS 750D")]
    [InlineData("Canon EOS 760D")]
    [InlineData("Canon EOS 77D")]
    [InlineData("Canon EOS 7D")]
    [InlineData("Canon EOS 7D Mark II")]
    [InlineData("Canon EOS 800D")]
    [InlineData("Canon EOS 80D")]
    [InlineData("Canon EOS 850D")]
    [InlineData("Canon EOS 90D")]
    [InlineData("Canon EOS DIGITAL REBEL XS")]
    [InlineData("Canon EOS DIGITAL REBEL XSi")]
    [InlineData("Canon EOS DIGITAL REBEL XT")]
    [InlineData("Canon EOS DIGITAL REBEL XTi")]
    [InlineData("Canon EOS KISS M")]
    [InlineData("Canon EOS Kiss Digital N")]
    [InlineData("Canon EOS Kiss F")]
    [InlineData("Canon EOS Kiss X3")]
    [InlineData("Canon EOS Kiss X4")]
    [InlineData("Canon EOS Kiss X80")]
    [InlineData("Canon EOS Kiss X9")]
    [InlineData("Canon EOS M")]
    [InlineData("Canon EOS M10")]
    [InlineData("Canon EOS M100")]
    [InlineData("Canon EOS M2")]
    [InlineData("Canon EOS M200")]
    [InlineData("Canon EOS M3")]
    [InlineData("Canon EOS M5")]
    [InlineData("Canon EOS M50")]
    [InlineData("Canon EOS M50m2")]
    [InlineData("Canon EOS M6")]
    [InlineData("Canon EOS M6 Mark II")]
    [InlineData("Canon EOS R")]
    [InlineData("Canon EOS R10")]
    [InlineData("Canon EOS R100")]
    [InlineData("Canon EOS R3")]
    [InlineData("Canon EOS R5")]
    [InlineData("Canon EOS R50")]
    [InlineData("Canon EOS R50 V")]
    [InlineData("Canon EOS R5m2")]
    [InlineData("Canon EOS R6")]
    [InlineData("Canon EOS R6 Mark III")]
    [InlineData("Canon EOS R6m2")]
    [InlineData("Canon EOS R7")]
    [InlineData("Canon EOS R8")]
    [InlineData("Canon EOS REBEL SL1")]
    [InlineData("Canon EOS REBEL T1i")]
    [InlineData("Canon EOS REBEL T2i")]
    [InlineData("Canon EOS REBEL T3")]
    [InlineData("Canon EOS REBEL T3i")]
    [InlineData("Canon EOS REBEL T4i")]
    [InlineData("Canon EOS REBEL T5")]
    [InlineData("Canon EOS REBEL T5i")]
    [InlineData("Canon EOS REBEL T7i")]
    [InlineData("Canon EOS RP")]
    [InlineData("Canon EOS Rebel SL2")]
    [InlineData("Canon EOS Rebel SL3")]
    [InlineData("Canon EOS Rebel T100")]
    [InlineData("Canon EOS Rebel T6")]
    [InlineData("Canon EOS Rebel T6i")]
    [InlineData("Canon EOS Rebel T6s")]
    [InlineData("Canon EOS Rebel T7")]
    [InlineData("Canon EOS Rebel T7i")]
    [InlineData("Canon EOS-1D Mark II")]
    [InlineData("Canon EOS-1D Mark II N")]
    [InlineData("Canon EOS-1D Mark III")]
    [InlineData("Canon EOS-1D Mark IV")]
    [InlineData("Canon EOS-1D X")]
    [InlineData("Canon EOS-1D X Mark II")]
    [InlineData("Canon EOS-1D X Mark III")]
    [InlineData("Canon EOS-1Ds Mark II")]
    [InlineData("Canon EOS-1Ds Mark III")]
    public void TryFind_KnowsEveryModelStringCanonCamerasReport(string reportedName) {
        Assert.True(CanonSensorDatabase.TryFind(reportedName, out _), reportedName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Canon EOS Camera")]
    [InlineData("ZWO ASI2600MC Pro")]
    [InlineData("Canon EOS R99")]
    public void TryFind_RejectsUnknownNames(string? name) {
        Assert.False(CanonSensorDatabase.TryFind(name, out _));
    }

    [Theory]
    [InlineData("Canon", double.NaN, true)]
    [InlineData("Canon", 0.0, true)]
    [InlineData("Canon", -1.0, true)]
    [InlineData("Canon", 3.76, false)]
    [InlineData("ASCOM", double.NaN, false)]
    [InlineData("Nikon", double.NaN, false)]
    [InlineData(null, double.NaN, false)]
    public void OnlyNinasCanonDriverWithoutAPixelSizeIsTouched(string? category, double reportedPixelSize, bool expected) {
        Assert.Equal(expected, CanonSensorSettings.IsCanonDriverWithoutGeometry(category, reportedPixelSize));
    }

    [Theory]
    // EOS R100 CR3: FocalPlaneXResolution 6825.938567 px/inch
    [InlineData("6825.938567\r\n2\r\n", 3.721)]
    [InlineData("6825.938567\n", 3.721)]
    [InlineData("2687.16\r\n3\r\n", 3.721)]
    [InlineData("268.716\r\n4\r\n", 3.721)]
    public void TryParsePixelSize_ConvertsTheFocalPlaneResolution(string output, double expected) {
        Assert.True(CanonExif.TryParsePixelSize(output, out var micrometres));
        Assert.Equal(expected, micrometres, 3);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0\r\n2\r\n")]
    [InlineData("-5\r\n2\r\n")]
    [InlineData("abc")]
    [InlineData("6825.9\r\n9\r\n")]
    [InlineData("100\r\n2\r\n")]
    [InlineData("Infinity")]
    public void TryParsePixelSize_RejectsAnythingElse(string? output) {
        Assert.False(CanonExif.TryParsePixelSize(output, out var micrometres));
        Assert.True(double.IsNaN(micrometres));
    }
}
