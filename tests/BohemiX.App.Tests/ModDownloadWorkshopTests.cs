using BohemiX.App.ViewModels;
using BohemiX.Core.Models;

namespace BohemiX.App.Tests;

public sealed class ModDownloadWorkshopTests
{
    [Fact]
    public void WorkshopSearchRowPreservesPublishedFileIdAndLocalizesSource()
    {
        const ulong publishedFileId = 4_000_000_000;
        using var row = new NexusModSearchRowViewModel(CreateWorkshopMod(publishedFileId));

        row.ApplyLocalization(TranslateChinese);

        Assert.True(row.IsSteamWorkshop);
        Assert.Equal(publishedFileId, row.PublishedFileId);
        Assert.Equal("4000000000", row.DisplayId);
        Assert.Equal(0, row.ModId);
        Assert.Equal("Steam 创意工坊", row.SourceText);
        Assert.Equal("通过 Steam 订阅", row.DownloadStateText);
        Assert.Equal("下载", row.DownloadActionText);
        Assert.True(row.CanDownload);
    }

    [Fact]
    public void DownloadedWorkshopSearchRowCannotBeDownloadedAgain()
    {
        using var row = new NexusModSearchRowViewModel(CreateWorkshopMod(123));

        row.ApplyDownloadState(true, TranslateChinese);

        Assert.True(row.IsDownloaded);
        Assert.False(row.CanDownload);
        Assert.Equal("已下载", row.DownloadStateText);
        Assert.Equal("已下载", row.DownloadActionText);
    }

    [Fact]
    public void SourceOptionKeepsStableKeyWhenDisplayNameChanges()
    {
        var source = new ModDownloadSourceOptionViewModel(
            ModDownloadSourceOptionViewModel.SteamWorkshopKey,
            "Steam Workshop");

        source.DisplayName = "Steam 创意工坊";

        Assert.Equal(ModDownloadSourceOptionViewModel.SteamWorkshopKey, source.Key);
        Assert.True(source.IsSteamWorkshop);
        Assert.Equal("Steam 创意工坊", source.DisplayName);
    }

    [Fact]
    public void SearchOperationGateInvalidatesPreviousSourceRequest()
    {
        using var gate = new ModDownloadSearchOperationGate();
        var first = gate.Begin();

        var second = gate.Begin();

        Assert.True(first.CancellationToken.IsCancellationRequested);
        Assert.False(gate.IsCurrent(first));
        Assert.False(second.CancellationToken.IsCancellationRequested);
        Assert.True(gate.IsCurrent(second));
    }

    private static WorkshopModInfo CreateWorkshopMod(ulong publishedFileId)
    {
        return new WorkshopModInfo(
            publishedFileId,
            "Combat Rebalance",
            "Workshop summary",
            "Workshop description",
            "Steam user",
            1024,
            null,
            DateTimeOffset.UtcNow.AddDays(-2),
            DateTimeOffset.UtcNow,
            1250,
            42,
            2,
            0.95f,
            []);
    }

    private static string TranslateChinese(string key)
    {
        return key switch
        {
            "SteamWorkshopSource" => "Steam 创意工坊",
            "WorkshopMetricText" => "{0} 次订阅 / {1} 个赞 / {2}",
            "WorkshopSubscribeViaSteam" => "通过 Steam 订阅",
            "ModDownloadStatusDownloaded" => "已下载",
            "ManagerDownload" => "管理器下载",
            "BrowserSignInOrApiKeyRequired" => "需要浏览器登录或 API 密钥",
            "Download" => "下载",
            _ => key
        };
    }
}
