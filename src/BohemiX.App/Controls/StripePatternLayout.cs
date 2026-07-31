using System;
using Avalonia;

namespace BohemiX.App.Controls;

internal readonly record struct StripePatternLayout(Rect ImageRect, double Scale, double LoopLength, double Phase)
{
    private const double CoveragePadding = 1;

    public static StripePatternLayout Create(
        Size patternSize,
        Size anchorSize,
        Point anchorOffset,
        double travel,
        double unscaledLoopLength)
    {
        if (patternSize.Width <= 0
            || patternSize.Height <= 0
            || anchorSize.Width <= 0
            || anchorSize.Height <= 0
            || unscaledLoopLength <= 0)
        {
            return default;
        }

        // User-selected textures can be smaller than the built-in seamless pattern.
        // Keep enough source image outside the travel range for a single moving bitmap
        // to cover the viewport throughout the loop.
        var smallestPatternDimension = Math.Min(patternSize.Width, patternSize.Height);
        var effectiveUnscaledLoopLength = unscaledLoopLength < smallestPatternDimension
            ? unscaledLoopLength
            : smallestPatternDimension / 2d;

        // The bitmap repeats after moving one loop diagonally. Scale it just enough that
        // the same bitmap still covers the whole anchor at the end of that movement.
        var scale = Math.Max(
            1,
            Math.Max(
                (anchorSize.Width + CoveragePadding * 2) / (patternSize.Width - effectiveUnscaledLoopLength),
                (anchorSize.Height + CoveragePadding * 2) / (patternSize.Height - effectiveUnscaledLoopLength)));
        var renderedWidth = patternSize.Width * scale;
        var renderedHeight = patternSize.Height * scale;
        var loopLength = effectiveUnscaledLoopLength * scale;
        var phase = PositiveModulo(travel, effectiveUnscaledLoopLength) * scale;
        var horizontalOverscan = renderedWidth - loopLength - anchorSize.Width;
        var verticalOverscan = renderedHeight - loopLength - anchorSize.Height;
        var imageLeft = -horizontalOverscan / 2 - phase - anchorOffset.X;
        var imageTop = -verticalOverscan / 2 - phase - anchorOffset.Y;

        return new StripePatternLayout(
            new Rect(imageLeft, imageTop, renderedWidth, renderedHeight),
            scale,
            loopLength,
            phase);
    }

    private static double PositiveModulo(double value, double modulus)
    {
        var result = value % modulus;
        return result < 0 ? result + modulus : result;
    }
}
