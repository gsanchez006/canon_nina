using System.IO;
using Xunit;

namespace NINA.Plugin.CanonAstroImage.Tests;

public class CanonSensorTemperatureTests {
    [Theory]
    [InlineData("48\r\n", 48.0)]
    [InlineData("48", 48.0)]
    [InlineData("-5\n", -5.0)]
    [InlineData("0", 0.0)]
    [InlineData("31\r\n40\r\n", 31.0)]
    public void TryParse_ReadsTheFirstLineInDegreesC(string output, double expected) {
        Assert.True(CanonSensorTemperature.TryParse(output, out var celsius));
        Assert.Equal(expected, celsius);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\r\n")]
    [InlineData("48 C")]
    [InlineData("Error: File format error - -")]
    [InlineData("128")]
    [InlineData("-129")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void TryParse_RejectsAnythingElse(string? output) {
        Assert.False(CanonSensorTemperature.TryParse(output, out var celsius));
        Assert.True(double.IsNaN(celsius));
    }

    [Fact]
    public async Task ReadAsync_WithoutExifTool_ReturnsNaN() {
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "exiftool.exe");

        var celsius = await CanonSensorTemperature.ReadAsync(new byte[] { 1 }, missing);

        Assert.True(double.IsNaN(celsius));
    }
}
