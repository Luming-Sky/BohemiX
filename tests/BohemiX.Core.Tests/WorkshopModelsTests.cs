using BohemiX.Core.Models;

namespace BohemiX.Core.Tests;

public sealed class WorkshopModelsTests
{
    [Fact]
    public void WorkshopOptions_DefaultsTargetKcd2SteamApp()
    {
        var options = new WorkshopOptions();

        Assert.Equal<uint>(1771300, options.Kcd2AppId);
        Assert.True(options.PageSize > 0);
        Assert.True(options.ThumbnailCacheLimit > 0);
    }

    [Fact]
    public void WorkshopSearchRequest_DefaultsToHotFirstPage()
    {
        var request = new WorkshopSearchRequest();

        Assert.Equal(1, request.Page);
        Assert.Equal(50, request.PageSize);
        Assert.Equal(WorkshopSortOrder.Hot, request.SortOrder);
    }

    [Fact]
    public void WorkshopInstallRequest_DefaultsToCopyWithDependencies()
    {
        var request = new WorkshopInstallRequest(123456UL, @"D:\Games\KCD2\Mods");

        Assert.Equal(123456UL, request.PublishedFileId);
        Assert.Equal(WorkshopDeployMode.Copy, request.DeployMode);
        Assert.True(request.IncludeDependencies);
    }
}
