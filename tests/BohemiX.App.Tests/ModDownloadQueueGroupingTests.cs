using Avalonia.Headless.XUnit;
using BohemiX.App.ViewModels;
using BohemiX.Core.Models;

namespace BohemiX.App.Tests;

public sealed class ModDownloadQueueGroupingTests
{
    [AvaloniaFact]
    public void RowsFromTheSameModPackSessionUseTheSameGroupKey()
    {
        var sessionId = Guid.NewGuid();
        using var first = CreateRow(1, 10, ModDownloadStatus.Downloading, sessionId, "pack", "Test Pack");
        using var second = CreateRow(2, 20, ModDownloadStatus.Completed, sessionId, "pack", "Test Pack");
        using var standalone = CreateRow(3, 30, ModDownloadStatus.Pending);

        Assert.Equal(
            ModDownloadQueueGroupViewModel.GetGroupKey(first),
            ModDownloadQueueGroupViewModel.GetGroupKey(second));
        Assert.NotEqual(
            ModDownloadQueueGroupViewModel.GetGroupKey(first),
            ModDownloadQueueGroupViewModel.GetGroupKey(standalone));
        Assert.Equal(
            ModDownloadQueueGroupViewModel.StandaloneGroupKey,
            ModDownloadQueueGroupViewModel.GetGroupKey(standalone));
    }

    [AvaloniaFact]
    public void ModPackGroupShowsPackNameAndAggregatedStatus()
    {
        var sessionId = Guid.NewGuid();
        using var active = CreateRow(1, 10, ModDownloadStatus.Downloading, sessionId, "pack", "Test Pack");
        using var completed = CreateRow(2, 20, ModDownloadStatus.Completed, sessionId, "pack", "Test Pack");
        using var failed = CreateRow(3, 30, ModDownloadStatus.Failed, sessionId, "pack", "Test Pack");
        ModDownloadQueueRowViewModel? selected = null;
        var group = new ModDownloadQueueGroupViewModel("modpack:test", value => selected = value);

        group.Update([active, completed, failed], Translate);
        group.SelectedItem = active;

        Assert.True(group.IsModPack);
        Assert.Equal("Test Pack", group.TitleText);
        Assert.Equal("Mod pack", group.KindText);
        Assert.Equal("1 active / 1 downloaded / 1 attention", group.SummaryText);
        Assert.Equal("1/3", group.CountText);
        Assert.Same(active, selected);
    }

    [AvaloniaFact]
    public void DownloadRowSmoothsSpeedAndResetsItAfterLeavingDownloading()
    {
        using var row = CreateRow(1, 10, ModDownloadStatus.Pending);

        row.ApplyProgress(CreateProgress(1_000, ModDownloadStatus.Downloading));
        Assert.Equal(1_000, row.BytesPerSecond);

        row.ApplyProgress(CreateProgress(5_000, ModDownloadStatus.Downloading));
        Assert.Equal(2_000, row.BytesPerSecond);

        row.ApplyProgress(CreateProgress(0, ModDownloadStatus.Paused));
        Assert.Equal(0, row.BytesPerSecond);

        row.ApplyProgress(CreateProgress(4_000, ModDownloadStatus.Downloading));
        Assert.Equal(4_000, row.BytesPerSecond);
    }

    private static ModDownloadProgress CreateProgress(double bytesPerSecond, ModDownloadStatus status) =>
        new("1:10", 1, 10, "mod-1.zip", 1024, 4096, 25, bytesPerSecond, status);

    private static ModDownloadQueueRowViewModel CreateRow(
        int modId,
        int fileId,
        ModDownloadStatus status,
        Guid? sessionId = null,
        string? modPackId = null,
        string? modPackName = null)
    {
        var request = new NexusModDownloadRequest(
            "kingdomcomedeliverance2",
            modId,
            fileId,
            new Uri($"https://downloads.example.test/{modId}/{fileId}"),
            $"mod-{modId}.zip",
            Path.GetTempPath(),
            null,
            ModPackSessionId: sessionId,
            ModPackId: modPackId,
            ModPackName: modPackName);
        return new ModDownloadQueueRowViewModel(new ModDownloadQueueItem(
            $"{modId}:{fileId}",
            request,
            status,
            status == ModDownloadStatus.Failed ? "failed" : null));
    }

    private static string Translate(string key) => key switch
    {
        "ModPackSource" => "Mod packs",
        "StandaloneDownloads" => "Individual downloads",
        "ModPackDownloadSection" => "Mod pack",
        "StandaloneDownloadSection" => "Individual mods",
        "DownloadQueueGroupSummary" => "{0} active / {1} downloaded / {2} attention",
        _ => key
    };
}
