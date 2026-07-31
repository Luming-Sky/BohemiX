using BohemiX.App.ViewModels;
using BohemiX.Core.Models;

namespace BohemiX.App.Tests;

public sealed class ModPackDownloadTests
{
    [Fact]
    public void SourceOptionExposesStableModPackKey()
    {
        var source = new ModDownloadSourceOptionViewModel(ModDownloadSourceOptionViewModel.ModPackKey, "模组整合包");

        Assert.True(source.IsModPack);
        Assert.False(source.IsSteamWorkshop);
        Assert.Equal("ModPack", source.Key);
    }

    [Fact]
    public void RowUsesPlatformSpecificOfficialAction()
    {
        using var nexus = new ModPackRowViewModel(CreateEntry(ModPackPlatform.NexusCollection));
        using var steam = new ModPackRowViewModel(CreateEntry(ModPackPlatform.SteamWorkshopCollection));

        nexus.ApplyLocalization(Translate);
        steam.ApplyLocalization(Translate);

        Assert.Equal("在 Nexus / Vortex 安装", nexus.PrimaryActionText);
        Assert.Equal("通过 Steam 安装", steam.PrimaryActionText);
        Assert.Equal("Nexus 合集", nexus.PlatformText);
        Assert.Equal("Steam 合集", steam.PlatformText);
        Assert.Equal("官方平台公开合集", nexus.RightsText);
    }

    [Fact]
    public void RowExposesLocalizedAdultContentMarker()
    {
        using var row = new ModPackRowViewModel(CreateEntry(ModPackPlatform.NexusCollection) with
        {
            ContainsAdultContent = true
        });

        row.ApplyLocalization(Translate);

        Assert.True(row.ContainsAdultContent);
        Assert.Equal("含成人内容", row.AdultContentText);
    }

    [Fact]
    public void RowTracksRemoteThumbnailLoadingUntilCacheCompletes()
    {
        using var row = new ModPackRowViewModel(CreateEntry(ModPackPlatform.SteamWorkshopCollection) with
        {
            ThumbnailUrl = "https://images.steamusercontent.com/ugc/example/cover/"
        });

        Assert.True(row.IsThumbnailLoading);
        Assert.False(row.HasNoThumbnail);

        row.SetThumbnailPath(null);

        Assert.False(row.IsThumbnailLoading);
        Assert.True(row.HasNoThumbnail);
    }

    [Fact]
    public void ThumbnailCacheIdIsStableForInstalledModPackFallbacks()
    {
        const string modPackId = "nexus-lculfa";

        using var first = new ModPackRowViewModel(CreateEntry(ModPackPlatform.NexusCollection) with { Id = modPackId });
        using var second = new ModPackRowViewModel(CreateEntry(ModPackPlatform.NexusCollection) with { Id = modPackId });

        Assert.Equal(first.ThumbnailCacheId, second.ThumbnailCacheId);
        Assert.Equal(first.ThumbnailCacheId, ModPackRowViewModel.CreateThumbnailCacheId(modPackId));
    }

    [Fact]
    public void RowExposesRepresentativeThumbnailMarker()
    {
        using var row = new ModPackRowViewModel(CreateEntry(ModPackPlatform.SteamWorkshopCollection) with
        {
            ThumbnailUrl = "https://images.steamusercontent.com/ugc/example/cover/",
            ThumbnailIsRepresentative = true
        });

        row.ApplyLocalization(Translate);

        Assert.True(row.ThumbnailIsRepresentative);
        Assert.Equal("内容代表图", row.RepresentativeThumbnailText);
    }

    [Fact]
    public void FilterMatchesCuratorTagsAndCategoryAndSortsByUpdate()
    {
        var older = CreateEntry(ModPackPlatform.NexusCollection) with
        {
            Id = "older-visual-pack",
            Title = "Visual Pack",
            Curator = "Alice",
            Category = ModPackCategory.Visuals,
            Tags = ["lighting"],
            UpdatedAt = DateTimeOffset.UtcNow.AddDays(-2)
        };
        var newer = older with
        {
            Id = "newer-visual-pack",
            Title = "Sharper Visuals",
            Tags = ["lighting", "textures"],
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var results = MainWindowViewModel.FilterModPackCatalog([older, newer], "Alice lighting", "Visuals");

        Assert.Equal(["newer-visual-pack", "older-visual-pack"], results.Select(entry => entry.Id));
        Assert.Empty(MainWindowViewModel.FilterModPackCatalog([older, newer], "hardcore", "Visuals"));
    }

    [Fact]
    public void BatchLimitsVisibleRowsAndStopsAfterLastBatch()
    {
        var entries = Enumerable.Range(0, 45)
            .Select(index => CreateEntry(ModPackPlatform.SteamWorkshopCollection) with
            {
                Id = $"steam-{index}",
                PlatformIdentifier = index.ToString()
            })
            .ToArray();

        var first = MainWindowViewModel.GetModPackBatch(entries, 0, 40);
        var second = MainWindowViewModel.GetModPackBatch(entries, first.NextOffset, 40);
        var completed = MainWindowViewModel.GetModPackBatch(entries, second.NextOffset, 40);

        Assert.Equal(40, first.Entries.Count);
        Assert.Equal(40, first.NextOffset);
        Assert.Equal(5, second.Entries.Count);
        Assert.Equal(45, second.NextOffset);
        Assert.Empty(completed.Entries);
        Assert.Equal(45, completed.NextOffset);
    }

    private static ModPackCatalogEntry CreateEntry(ModPackPlatform platform) => new(
        "reviewed-pack",
        "Reviewed Pack",
        "Reviewed public collection",
        platform,
        platform == ModPackPlatform.NexusCollection ? "starter" : "123456",
        platform == ModPackPlatform.NexusCollection
            ? "https://www.nexusmods.com/kingdomcomedeliverance2/collections/starter"
            : "https://steamcommunity.com/sharedfiles/filedetails/?id=123456",
        "Curator",
        ModPackCategory.EssentialsTools,
        ["starter"],
        12,
        "KCD2 1.4",
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow);

    private static string Translate(string key) => key switch
    {
        "NexusCollectionSource" => "Nexus 合集",
        "SteamWorkshopCollectionSource" => "Steam 合集",
        "InstallModPackViaNexus" => "在 Nexus / Vortex 安装",
        "InstallModPackViaSteam" => "通过 Steam 安装",
        "ModPackOfficialPlatformRights" => "官方平台公开合集",
        "ModPackContainsAdultContent" => "含成人内容",
        "ModPackRepresentativeThumbnail" => "内容代表图",
        "ModPackItemCount" => "{0} 个模组",
        "ModPackUpdatedAt" => "更新于 {0}",
        _ => key
    };
}
