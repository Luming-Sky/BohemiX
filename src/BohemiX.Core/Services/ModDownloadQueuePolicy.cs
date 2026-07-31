using BohemiX.Core.Models;

namespace BohemiX.Core.Services;

/// <summary>
/// Centralizes the lifecycle semantics shared by download orchestration and presentation.
/// </summary>
public static class ModDownloadQueuePolicy
{
    public static bool IsActive(ModDownloadStatus status) => status is
        ModDownloadStatus.Pending or
        ModDownloadStatus.Resolving or
        ModDownloadStatus.Downloading or
        ModDownloadStatus.Paused;

    public static bool RequiresAttention(ModDownloadStatus status) => status is
        ModDownloadStatus.Failed or
        ModDownloadStatus.ChecksumFailed;

    public static bool IsActionable(ModDownloadStatus status) =>
        IsActive(status) || RequiresAttention(status);

    public static bool IsTerminal(ModDownloadStatus status) => status is
        ModDownloadStatus.Completed or
        ModDownloadStatus.Canceled;

    public static bool CanClear(ModDownloadStatus status) =>
        IsTerminal(status) || RequiresAttention(status);

    public static bool CanCancel(ModDownloadStatus status) => !IsTerminal(status);

    public static bool CanStart(ModDownloadStatus status) => status is
        ModDownloadStatus.Pending or
        ModDownloadStatus.Failed or
        ModDownloadStatus.ChecksumFailed;

    public static bool CanPause(ModDownloadStatus status) => status is
        ModDownloadStatus.Pending or
        ModDownloadStatus.Downloading;
}
