using BohemiX.Core.Models;
using BohemiX.Core.Services;

namespace BohemiX.Core.Tests;

public sealed class ModDownloadQueuePolicyTests
{
    [Fact]
    public void StatusGroups_ExposeStableQueueSemantics()
    {
        Assert.True(ModDownloadQueuePolicy.IsActive(ModDownloadStatus.Downloading));
        Assert.True(ModDownloadQueuePolicy.IsActionable(ModDownloadStatus.ChecksumFailed));
        Assert.True(ModDownloadQueuePolicy.IsTerminal(ModDownloadStatus.Canceled));
        Assert.True(ModDownloadQueuePolicy.CanClear(ModDownloadStatus.Failed));
        Assert.False(ModDownloadQueuePolicy.CanCancel(ModDownloadStatus.Completed));
    }

    [Fact]
    public void StartAndPauseCommands_OnlyAllowSupportedStates()
    {
        Assert.True(ModDownloadQueuePolicy.CanStart(ModDownloadStatus.Pending));
        Assert.True(ModDownloadQueuePolicy.CanStart(ModDownloadStatus.Failed));
        Assert.False(ModDownloadQueuePolicy.CanStart(ModDownloadStatus.Completed));
        Assert.True(ModDownloadQueuePolicy.CanPause(ModDownloadStatus.Downloading));
        Assert.False(ModDownloadQueuePolicy.CanPause(ModDownloadStatus.Paused));
    }
}
