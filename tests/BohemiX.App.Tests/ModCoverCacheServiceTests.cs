using BohemiX.App.Services;

namespace BohemiX.App.Tests;

public sealed class ModCoverCacheServiceTests
{
    [Fact]
    public void BuildImageCandidates_PrefersFullNexusImageOverThumbnail()
    {
        var source = new Uri(
            "https://staticdelivery.nexusmods.com/mods/7286/images/thumbnails/223/223-1744047564-905253213.jpeg");

        var candidates = ModCoverCacheService.BuildImageCandidates(source);

        Assert.Equal(2, candidates.Length);
        Assert.Equal(
            "https://staticdelivery.nexusmods.com/mods/7286/images/223/223-1744047564-905253213.jpeg",
            candidates[0].AbsoluteUri);
        Assert.Equal(source, candidates[1]);
    }

    [Fact]
    public void BuildImageCandidates_LeavesUnrelatedImageUrlUnchanged()
    {
        var source = new Uri("https://example.com/images/thumbnails/cover.jpg");

        var candidates = ModCoverCacheService.BuildImageCandidates(source);

        Assert.Equal([source], candidates);
    }

    [Theory]
    [InlineData(1920, 1080, 768, 432)]
    [InlineData(1024, 1536, 768, 1152)]
    [InlineData(640, 360, 640, 360)]
    public void GetOptimizedDimensions_LimitsWidthAndPreservesAspectRatio(
        int width,
        int height,
        int expectedWidth,
        int expectedHeight)
    {
        var dimensions = ModCoverCacheService.GetOptimizedDimensions(width, height);

        Assert.Equal(expectedWidth, dimensions.Width);
        Assert.Equal(expectedHeight, dimensions.Height);
    }
}
