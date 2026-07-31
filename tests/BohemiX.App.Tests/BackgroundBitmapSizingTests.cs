using Avalonia;
using BohemiX.App.Controls;

namespace BohemiX.App.Tests;

public sealed class BackgroundBitmapSizingTests
{
    [Fact]
    public void CalculateDecodeWidth_CoversUniformFillAtDeviceResolution()
    {
        var width = BackgroundBitmapSizing.CalculateDecodeWidth(
            new PixelSize(6000, 4000),
            new Size(1600, 900),
            1.25);

        Assert.Equal(2048, width);
    }

    [Fact]
    public void CalculateDecodeWidth_AccountsForFreeCrop()
    {
        var width = BackgroundBitmapSizing.CalculateDecodeWidth(
            new PixelSize(6000, 4000),
            new Size(1600, 900),
            1,
            cropWidth: 0.5,
            cropHeight: 0.5);

        Assert.Equal(3328, width);
    }

    [Fact]
    public void CalculateDecodeWidth_NeverExceedsSourceWidth()
    {
        var width = BackgroundBitmapSizing.CalculateDecodeWidth(
            new PixelSize(1200, 800),
            new Size(2560, 1440),
            1.5,
            cropWidth: 0.1,
            cropHeight: 0.1);

        Assert.Equal(1200, width);
    }

    [Fact]
    public void CalculateDecodeWidth_RespectsMaximumDecodeWidth()
    {
        var width = BackgroundBitmapSizing.CalculateDecodeWidth(
            new PixelSize(6000, 4000),
            new Size(2560, 1440),
            1.5,
            maximumDecodeWidth: 1920);

        Assert.Equal(1920, width);
    }

    [Fact]
    public void CalculateDecodeWidth_ReturnsZeroForInvalidBounds()
    {
        Assert.Equal(0, BackgroundBitmapSizing.CalculateDecodeWidth(
            new PixelSize(6000, 4000),
            default,
            1));
    }
}
