using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using Xunit;

namespace NINA.Plugin.CanonAstroImage.Tests;

/// <summary>
/// Pins the reflection the plugin relies on. If a NINA.Plugin package bump renames or removes the private setters
/// on ImageArray / ImageArrayInt, these tests fail instead of the plugin silently falling back to writing both files.
/// </summary>
public class RawDetachTests {
    [Fact]
    public void Detach_ImageArray_ClearsRawDataAndRawType() {
        IImageArray array = new ImageArray(new ushort[4], new byte[] { 1, 2, 3 }, "cr3");

        var detached = CanonAstroImage.TryDetachRawData(array);

        Assert.True(detached);
        Assert.Null(array.RAWData);
        Assert.Null(array.RAWType);
        Assert.NotNull(array.FlatArray);
    }

    [Fact]
    public void Detach_ImageArrayInt_ClearsRawDataAndRawType() {
        IImageArray array = new ImageArrayInt(new int[4], new byte[] { 1, 2, 3 }, "cr2");

        var detached = CanonAstroImage.TryDetachRawData(array);

        Assert.True(detached);
        Assert.Null(array.RAWData);
        Assert.Null(array.RAWType);
        Assert.NotNull(array.FlatArrayInt);
    }

    [Fact]
    public void Detach_WithoutRawData_ReturnsTrueWithoutTouchingAnything() {
        IImageArray array = new ImageArray(new ushort[4]);

        Assert.True(CanonAstroImage.TryDetachRawData(array));
        Assert.Null(array.RAWData);
    }

    [Fact]
    public void Detach_NullArray_ReturnsTrue() {
        Assert.True(CanonAstroImage.TryDetachRawData(null));
    }

    [Fact]
    public void Detach_ArrayWithoutSetter_ReturnsFalse() {
        var array = new ReadOnlyRawArray();

        Assert.False(CanonAstroImage.TryDetachRawData(array));
        Assert.NotNull(array.RAWData);
    }

    /// <summary>Simulates a future NINA array type whose RAW properties cannot be written.</summary>
    private sealed class ReadOnlyRawArray : IImageArray {
        public ushort[] FlatArray => new ushort[4];
        public int[] FlatArrayInt => new int[0];
        public byte[] RAWData => new byte[] { 9 };
        public string RAWType => "cr3";
    }
}
