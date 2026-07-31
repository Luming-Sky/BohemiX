using BohemiX.App.Services;
using SkiaSharp;

namespace BohemiX.App.Tests;

public sealed class AvatarCropServiceTests
{
    [Fact]
    public async Task CreateSquareAvatarAsync_UsesTheSelectedHorizontalFraming()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"bohemix-avatar-source-{Guid.NewGuid():N}.png");
        string? outputPath = null;
        try
        {
            using (var source = new SKBitmap(400, 200))
            using (var canvas = new SKCanvas(source))
            using (var red = new SKPaint { Color = SKColors.Red })
            using (var blue = new SKPaint { Color = SKColors.Blue })
            {
                canvas.DrawRect(new SKRect(0, 0, 200, 200), red);
                canvas.DrawRect(new SKRect(200, 0, 400, 200), blue);
                using var image = SKImage.FromBitmap(source);
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                using var stream = File.Open(sourcePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                data.SaveTo(stream);
            }

            var service = new AvatarCropService();
            outputPath = await service.CreateSquareAvatarAsync(new AvatarCropRequest(
                sourcePath,
                Zoom: 1,
                HorizontalOffset: 1,
                VerticalOffset: 0));

            using var cropped = SKBitmap.Decode(outputPath);
            Assert.NotNull(cropped);
            Assert.Equal(512, cropped.Width);
            Assert.Equal(512, cropped.Height);
            var center = cropped.GetPixel(256, 256);
            Assert.True(center.Blue > 220);
            Assert.True(center.Red < 30);
        }
        finally
        {
            if (File.Exists(sourcePath))
            {
                File.Delete(sourcePath);
            }

            if (outputPath is not null && File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }
}
