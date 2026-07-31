using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;

namespace BohemiX.App.Services;

public sealed class AvatarCropService : IAvatarCropService
{
    private const int OutputSize = 512;

    public Task<string> CreateSquareAvatarAsync(
        AvatarCropRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourcePath);
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(request.SourcePath))
        {
            throw new FileNotFoundException("The selected avatar image could not be found.", request.SourcePath);
        }

        using var source = SKBitmap.Decode(request.SourcePath)
            ?? throw new InvalidDataException("The selected file is not a supported image.");
        if (source.Width <= 0 || source.Height <= 0)
        {
            throw new InvalidDataException("The selected image has invalid dimensions.");
        }

        var crop = GetCropRect(
            source.Width,
            source.Height,
            request.Zoom,
            request.HorizontalOffset,
            request.VerticalOffset);
        using var output = new SKBitmap(new SKImageInfo(OutputSize, OutputSize, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(output))
        using (var paint = new SKPaint { FilterQuality = SKFilterQuality.High })
        {
            canvas.Clear(SKColors.Transparent);
            canvas.DrawBitmap(source, crop, new SKRect(0, 0, OutputSize, OutputSize), paint);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.Combine(Path.GetTempPath(), "BohemiX", "avatar-crops");
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, $"avatar-{Guid.NewGuid():N}.png");
        using var image = SKImage.FromBitmap(output);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidDataException("The avatar image could not be encoded.");
        using var stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        data.SaveTo(stream);
        return Task.FromResult(destination);
    }

    internal static SKRect GetCropRect(
        int width,
        int height,
        double zoom,
        double horizontalOffset,
        double verticalOffset)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Image dimensions must be positive.");
        }

        var safeZoom = Math.Clamp(zoom, 1d, 3d);
        var cropSize = Math.Max(1d, Math.Min(width, height) / safeZoom);
        var maxX = Math.Max(0d, (width - cropSize) / 2d);
        var maxY = Math.Max(0d, (height - cropSize) / 2d);
        var offsetX = Math.Clamp(horizontalOffset, -1d, 1d) * maxX;
        var offsetY = Math.Clamp(verticalOffset, -1d, 1d) * maxY;
        var left = Math.Clamp(((width - cropSize) / 2d) + offsetX, 0d, width - cropSize);
        var top = Math.Clamp(((height - cropSize) / 2d) + offsetY, 0d, height - cropSize);

        return new SKRect(
            (float)left,
            (float)top,
            (float)(left + cropSize),
            (float)(top + cropSize));
    }
}
