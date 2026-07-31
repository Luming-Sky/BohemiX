using Avalonia.Headless.XUnit;
using BohemiX.App.Services;
using BohemiX.App.ViewModels;
using BohemiX.Core.Models;

namespace BohemiX.App.Tests;

public sealed class VisualResourceLifecycleTests
{
    private const string TestImage = "avares://BohemiX.App/Assets/kcd2-hero-bg.jpg";

    [AvaloniaFact]
    public void InstalledModCover_LoadsWhenPathArrivesAfterActivation()
    {
        using var row = new ModListItemViewModel(new ModManifest(
            "nexus-42",
            "Test mod",
            "1.0",
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
            0,
            true,
            []));

        row.ActivateVisualResources();
        Assert.Null(row.CoverImage);

        row.SetCoverImagePath(TestImage);

        Assert.NotNull(row.CoverImage);
    }

    [AvaloniaFact]
    public void ModPackThumbnail_CanStayDormantAndReactivate()
    {
        using var row = new ModPackRowViewModel(CreateModPackEntry());

        row.SetThumbnailPath(TestImage, activateVisualResources: false);

        Assert.Equal(TestImage, row.ThumbnailPath);
        Assert.Null(row.ThumbnailImage);

        row.ActivateVisualResources();
        Assert.NotNull(row.ThumbnailImage);

        row.DeactivateVisualResources();
        row.DeactivateVisualResources();
        Assert.Equal(TestImage, row.ThumbnailPath);
        Assert.Null(row.ThumbnailImage);

        row.ActivateVisualResources();
        Assert.NotNull(row.ThumbnailImage);
    }

    [AvaloniaFact]
    public void NexusThumbnail_CanStayDormantAndReactivate()
    {
        using var row = new NexusModSearchRowViewModel(new NexusModSummary(
            42,
            "Test mod",
            "Summary",
            "1.0",
            "Author",
            1,
            1,
            null,
            null,
            null,
            true,
            "published"));

        row.SetThumbnailPath(TestImage, activateVisualResources: false);

        Assert.Equal(TestImage, row.ThumbnailPath);
        Assert.Null(row.ThumbnailImage);

        row.ActivateVisualResources();
        Assert.NotNull(row.ThumbnailImage);

        row.DeactivateVisualResources();
        row.DeactivateVisualResources();
        Assert.Equal(TestImage, row.ThumbnailPath);
        Assert.Null(row.ThumbnailImage);

        row.ActivateVisualResources();
        Assert.NotNull(row.ThumbnailImage);
    }

    [AvaloniaFact]
    public void NexusThumbnail_LoadsWhenPathArrivesAfterActivation()
    {
        using var row = new NexusModSearchRowViewModel(new NexusModSummary(
            42,
            "Test mod",
            "Summary",
            "1.0",
            "Author",
            1,
            1,
            null,
            null,
            null,
            true,
            "published"));

        row.ActivateVisualResources();
        Assert.Null(row.ThumbnailImage);

        row.SetThumbnailPath(TestImage, activateVisualResources: false);

        Assert.NotNull(row.ThumbnailImage);
    }

    [AvaloniaFact]
    public void ModPackThumbnail_LoadsWhenPathArrivesAfterActivation()
    {
        using var row = new ModPackRowViewModel(CreateModPackEntry());

        row.ActivateVisualResources();
        Assert.Null(row.ThumbnailImage);

        row.SetThumbnailPath(TestImage, activateVisualResources: false);

        Assert.NotNull(row.ThumbnailImage);
    }

    [AvaloniaFact]
    public void DownloadQueueCover_PreservesPresentationWhileDormant()
    {
        using var row = new ModDownloadQueueRowViewModel("42:7", 42, 7, "test.zip");

        row.SetModPresentation("Test mod", TestImage, activateVisualResources: false);

        Assert.Equal("Test mod", row.ModName);
        Assert.Null(row.CoverImage);

        row.ActivateVisualResources();
        Assert.NotNull(row.CoverImage);

        row.DeactivateVisualResources();
        row.DeactivateVisualResources();
        Assert.Equal("Test mod", row.ModName);
        Assert.Null(row.CoverImage);

        row.ActivateVisualResources();
        Assert.NotNull(row.CoverImage);
    }

    [AvaloniaFact]
    public void DownloadQueueCover_RefreshesWhenPresentationArrivesWhileActive()
    {
        using var row = new ModDownloadQueueRowViewModel("42:7", 42, 7, "test.zip");

        row.ActivateVisualResources();
        Assert.NotNull(row.CoverImage);

        row.SetModPresentation("Test mod", TestImage, activateVisualResources: false);

        Assert.Equal("Test mod", row.ModName);
        Assert.NotNull(row.CoverImage);
    }

    [AvaloniaFact]
    public void GameNewsImage_IsLazyAndReversible()
    {
        using var row = new GameNewsItemViewModel(new GameNewsItem(
            "news-1",
            "Title",
            "Summary",
            "https://example.test/news-1",
            "Author",
            "Source",
            DateTimeOffset.UtcNow,
            ImagePath: TestImage));

        Assert.False(row.AreVisualResourcesActive);
        Assert.Null(row.Image);

        row.ActivateVisualResources();
        Assert.True(row.AreVisualResourcesActive);
        Assert.NotNull(row.Image);

        row.DeactivateVisualResources();
        row.DeactivateVisualResources();
        Assert.False(row.AreVisualResourcesActive);
        Assert.Equal(TestImage, row.Item.ImagePath);
        Assert.Null(row.Image);

        row.ActivateVisualResources();
        Assert.NotNull(row.Image);
    }

    private static ModPackCatalogEntry CreateModPackEntry() => new(
        "test-pack",
        "Test pack",
        "Summary",
        ModPackPlatform.NexusCollection,
        "test",
        "https://example.test/pack",
        "Curator",
        ModPackCategory.EssentialsTools,
        [],
        1,
        "1.0",
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow);
}
