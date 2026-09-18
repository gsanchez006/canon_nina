using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using Xunit;

namespace NINA.Plugin.CanonAstroImage.Tests;

public class CanonFrameTests {
    [Theory]
    [InlineData("cr3", true)]
    [InlineData("CR3", true)]
    [InlineData("cr2", true)]
    [InlineData("nef", false)]
    [InlineData("", false)]
    public void IsCanonFrame_DependsOnRawType(string rawType, bool expected) {
        var array = new ImageArray(new ushort[4], new byte[] { 1 }, rawType);

        Assert.Equal(expected, CanonAstroImage.IsCanonFrame(array));
    }

    [Fact]
    public void IsCanonFrame_WithoutRawBytes_IsFalse() {
        Assert.False(CanonAstroImage.IsCanonFrame(new ImageArray(new ushort[4])));
    }

    [Fact]
    public void IsCanonFrame_AfterDetach_IsFalse() {
        var array = new ImageArray(new ushort[4], new byte[] { 1 }, "cr3");
        Assert.True(CanonAstroImage.IsCanonFrame(array));

        CanonAstroImage.TryDetachRawData(array);

        Assert.False(CanonAstroImage.IsCanonFrame(array));
    }

    [Fact]
    public void IsCanonFrame_NullArray_IsFalse() {
        Assert.False(CanonAstroImage.IsCanonFrame(null));
    }
}
