using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace BohemiX.App.Controls;

public sealed class BackgroundImageControl : Control
{
    public static readonly StyledProperty<IImage?> SourceProperty =
        AvaloniaProperty.Register<BackgroundImageControl, IImage?>(nameof(Source));

    public static readonly StyledProperty<int> CropModeProperty =
        AvaloniaProperty.Register<BackgroundImageControl, int>(nameof(CropMode));

    public static readonly StyledProperty<double> CropXProperty =
        AvaloniaProperty.Register<BackgroundImageControl, double>(nameof(CropX));

    public static readonly StyledProperty<double> CropYProperty =
        AvaloniaProperty.Register<BackgroundImageControl, double>(nameof(CropY));

    public static readonly StyledProperty<double> CropWidthProperty =
        AvaloniaProperty.Register<BackgroundImageControl, double>(nameof(CropWidth), 1);

    public static readonly StyledProperty<double> CropHeightProperty =
        AvaloniaProperty.Register<BackgroundImageControl, double>(nameof(CropHeight), 1);

    static BackgroundImageControl()
    {
        AffectsRender<BackgroundImageControl>(SourceProperty, CropModeProperty, CropXProperty, CropYProperty, CropWidthProperty, CropHeightProperty);
    }

    public IImage? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public int CropMode
    {
        get => GetValue(CropModeProperty);
        set => SetValue(CropModeProperty, value);
    }

    public double CropX
    {
        get => GetValue(CropXProperty);
        set => SetValue(CropXProperty, value);
    }

    public double CropY
    {
        get => GetValue(CropYProperty);
        set => SetValue(CropYProperty, value);
    }

    public double CropWidth
    {
        get => GetValue(CropWidthProperty);
        set => SetValue(CropWidthProperty, value);
    }

    public double CropHeight
    {
        get => GetValue(CropHeightProperty);
        set => SetValue(CropHeightProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Source is null || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        var target = new Rect(Bounds.Size);
        var sourceSize = Source.Size;
        if (sourceSize.Width <= 0 || sourceSize.Height <= 0)
        {
            return;
        }

        if (CropWidth < 0.999 || CropHeight < 0.999 || CropX > 0 || CropY > 0)
        {
            var cropWidth = Math.Clamp(CropWidth, 0.01, 1);
            var cropHeight = Math.Clamp(CropHeight, 0.01, 1);
            var cropX = Math.Clamp(CropX, 0, 1 - cropWidth);
            var cropY = Math.Clamp(CropY, 0, 1 - cropHeight);
            var freeCrop = new Rect(
                cropX * sourceSize.Width,
                cropY * sourceSize.Height,
                cropWidth * sourceSize.Width,
                cropHeight * sourceSize.Height);
            context.DrawImage(Source, freeCrop, target);
            return;
        }

        var scale = Math.Max(target.Width / sourceSize.Width, target.Height / sourceSize.Height);
        var visibleWidth = target.Width / scale;
        var visibleHeight = target.Height / scale;
        var left = Math.Max(0, sourceSize.Width - visibleWidth) / 2;
        var top = Math.Max(0, sourceSize.Height - visibleHeight) / 2;

        switch (Math.Clamp(CropMode, 0, 4))
        {
            case 1:
                top = 0;
                break;
            case 2:
                top = Math.Max(0, sourceSize.Height - visibleHeight);
                break;
            case 3:
                left = 0;
                break;
            case 4:
                left = Math.Max(0, sourceSize.Width - visibleWidth);
                break;
        }

        var sourceRect = new Rect(left, top, visibleWidth, visibleHeight);
        context.DrawImage(Source, sourceRect, target);
    }
}

internal static class BackgroundBitmapSizing
{
    private const int DecodeWidthBucket = 256;
    internal const int MaximumBackgroundDecodeWidth = 1920;
    internal const int MaximumStartupStripeDecodeWidth = 1536;

    internal static int CalculateDecodeWidth(
        PixelSize sourcePixels,
        Size targetSize,
        double renderScaling,
        double cropWidth = 1,
        double cropHeight = 1,
        int maximumDecodeWidth = int.MaxValue)
    {
        if (sourcePixels.Width <= 0 || sourcePixels.Height <= 0 || targetSize.Width <= 0 || targetSize.Height <= 0)
        {
            return 0;
        }

        var scaling = Math.Max(0.1, renderScaling);
        var targetWidth = targetSize.Width * scaling;
        var targetHeight = targetSize.Height * scaling;
        var normalizedCropWidth = Math.Clamp(cropWidth, 0.01, 1);
        var normalizedCropHeight = Math.Clamp(cropHeight, 0.01, 1);
        var aspectRatio = (double)sourcePixels.Width / sourcePixels.Height;
        var requiredWidth = Math.Max(
            targetWidth / normalizedCropWidth,
            targetHeight * aspectRatio / normalizedCropHeight);
        var bucketedWidth = (int)Math.Ceiling(requiredWidth / DecodeWidthBucket) * DecodeWidthBucket;
        return Math.Min(sourcePixels.Width, Math.Min(Math.Max(1, maximumDecodeWidth), Math.Max(1, bucketedWidth)));
    }
}
