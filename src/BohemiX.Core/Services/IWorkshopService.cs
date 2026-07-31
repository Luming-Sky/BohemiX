using BohemiX.Core.Models;
using BohemiX.Core.PlayerProfiles;

namespace BohemiX.Core.Services;

public interface IWorkshopService
{
    Task<SteamAccountIdentity> GetCurrentSteamAccountAsync(
        IProgress<SteamAccountDetectionProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<WorkshopSearchResult> SearchModsAsync(
        WorkshopSearchRequest request,
        CancellationToken cancellationToken = default);

    Task<WorkshopModInfo> GetModInfoAsync(
        ulong publishedFileId,
        CancellationToken cancellationToken = default);

    Task<WorkshopCollectionInfo> GetCollectionInfoAsync(
        ulong collectionId,
        CancellationToken cancellationToken = default);

    Task<WorkshopInstallResult> SubscribeAndInstallAsync(
        WorkshopInstallRequest request,
        IProgress<WorkshopInstallProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<WorkshopCollectionInstallResult> InstallCollectionAsync(
        WorkshopCollectionInstallRequest request,
        IProgress<WorkshopCollectionInstallProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed record SteamAccountDetectionProgress(SteamAccountDetectionStage Stage);

public enum SteamAccountDetectionStage
{
    StartingClient,
    WaitingForLogin,
    ValidatingAccount
}
