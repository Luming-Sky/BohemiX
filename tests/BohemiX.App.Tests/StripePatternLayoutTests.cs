using Avalonia;
using BohemiX.App.Controls;

namespace BohemiX.App.Tests;

public sealed class StripePatternLayoutTests
{
    private static readonly Size PatternSize = new(1536, 1296);
    private static readonly Size ViewportSize = new(1600, 900);

    [Theory]
    [InlineData(0)]
    [InlineData(381.5)]
    [InlineData(763.999)]
    [InlineData(-0.001)]
    public void PatternCoversTheAnchorThroughoutTheLoop(double travel)
    {
        var layout = StripePatternLayout.Create(
            PatternSize,
            ViewportSize,
            default,
            travel,
            StripedCardBorder.StripeLoopLength);

        Assert.True(layout.ImageRect.Left <= 0);
        Assert.True(layout.ImageRect.Top <= 0);
        Assert.True(layout.ImageRect.Right >= ViewportSize.Width);
        Assert.True(layout.ImageRect.Bottom >= ViewportSize.Height);
    }

    [Theory]
    [InlineData(1180, 720)]
    [InlineData(1600, 900)]
    [InlineData(2560, 1440)]
    [InlineData(3840, 2160)]
    public void PatternCoverageAdaptsToWindowSize(double width, double height)
    {
        var anchorSize = new Size(width, height);
        var layout = StripePatternLayout.Create(
            PatternSize,
            anchorSize,
            default,
            StripedCardBorder.StripeLoopLength - 0.001,
            StripedCardBorder.StripeLoopLength);

        Assert.True(layout.ImageRect.Left <= 0);
        Assert.True(layout.ImageRect.Top <= 0);
        Assert.True(layout.ImageRect.Right >= anchorSize.Width);
        Assert.True(layout.ImageRect.Bottom >= anchorSize.Height);
    }

    [Fact]
    public void ViewportAnchoringProducesTheSameGlobalPatternRectForEveryControl()
    {
        var travel = 127.375;
        var rootLayout = StripePatternLayout.Create(
            PatternSize,
            ViewportSize,
            default,
            travel,
            StripedCardBorder.StripeLoopLength);
        var controlOffset = new Point(913.25, 417.75);
        var controlLayout = StripePatternLayout.Create(
            PatternSize,
            ViewportSize,
            controlOffset,
            travel,
            StripedCardBorder.StripeLoopLength);
        var controlRectInRoot = controlLayout.ImageRect.Translate(new Vector(controlOffset.X, controlOffset.Y));

        Assert.Equal(rootLayout.ImageRect.X, controlRectInRoot.X, 8);
        Assert.Equal(rootLayout.ImageRect.Y, controlRectInRoot.Y, 8);
        Assert.Equal(rootLayout.ImageRect.Width, controlRectInRoot.Width, 8);
        Assert.Equal(rootLayout.ImageRect.Height, controlRectInRoot.Height, 8);
    }

    [Fact]
    public void FractionalTravelRemainsFractional()
    {
        var first = StripePatternLayout.Create(
            PatternSize,
            ViewportSize,
            default,
            10.25,
            StripedCardBorder.StripeLoopLength);
        var second = StripePatternLayout.Create(
            PatternSize,
            ViewportSize,
            default,
            10.75,
            StripedCardBorder.StripeLoopLength);

        Assert.Equal(0.5 * first.Scale, first.ImageRect.X - second.ImageRect.X, 8);
        Assert.Equal(0.5 * first.Scale, first.ImageRect.Y - second.ImageRect.Y, 8);
    }

    [Fact]
    public void ACompleteLoopReturnsToTheSamePosition()
    {
        var start = StripePatternLayout.Create(
            PatternSize,
            ViewportSize,
            default,
            0,
            StripedCardBorder.StripeLoopLength);
        var wrapped = StripePatternLayout.Create(
            PatternSize,
            ViewportSize,
            default,
            StripedCardBorder.StripeLoopLength,
            StripedCardBorder.StripeLoopLength);

        Assert.Equal(start.ImageRect, wrapped.ImageRect);
        Assert.Equal(start.Phase, wrapped.Phase);
    }

    [Theory]
    [InlineData(320, 180)]
    [InlineData(640, 640)]
    [InlineData(1200, 500)]
    public void SmallCustomTexturesStillCoverAndMoveAcrossTheViewport(double width, double height)
    {
        var start = StripePatternLayout.Create(
            new Size(width, height),
            ViewportSize,
            default,
            0,
            StripedCardBorder.StripeLoopLength);
        var moved = StripePatternLayout.Create(
            new Size(width, height),
            ViewportSize,
            default,
            10,
            StripedCardBorder.StripeLoopLength);

        Assert.True(start.ImageRect.Width > 0);
        Assert.True(start.ImageRect.Height > 0);
        Assert.True(start.ImageRect.Right >= ViewportSize.Width);
        Assert.True(start.ImageRect.Bottom >= ViewportSize.Height);
        Assert.NotEqual(start.ImageRect.X, moved.ImageRect.X);
        Assert.NotEqual(start.ImageRect.Y, moved.ImageRect.Y);
    }

    [Fact]
    public void CardAppearanceOnlyOverridesLocalMotionWhenACustomTextureExists()
    {
        Assert.Null(StripedCardBorder.ResolveGlobalMotionOverride(null, false));
        Assert.Null(StripedCardBorder.ResolveGlobalMotionOverride("   ", true));
        Assert.False(StripedCardBorder.ResolveGlobalMotionOverride("texture.png", false));
        Assert.True(StripedCardBorder.ResolveGlobalMotionOverride("texture.png", true));
    }

    [Fact]
    public void CustomCardTextureRemainsVisibleWithoutIgnoringDisabledCards()
    {
        Assert.Equal(0, StripedCardBorder.CalculateStripeOpacity(0, hasCustomTexture: true));
        Assert.Equal(0.22, StripedCardBorder.CalculateStripeOpacity(0.06, hasCustomTexture: true), 8);
        Assert.Equal(0.058, StripedCardBorder.CalculateStripeOpacity(0.1, hasCustomTexture: false), 8);
    }
}
