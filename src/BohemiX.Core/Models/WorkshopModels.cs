namespace BohemiX.Core.Models;

public sealed record WorkshopOptions(
    uint Kcd2AppId = 1771300,
    int PageSize = 50,
    int ThumbnailCacheLimit = 256,
    int ThumbnailCacheTtlHours = 12,
    int WorkshopInstallTimeoutMinutes = 30,
    int WorkshopPollIntervalMilliseconds = 1500,
    int StableDirectoryPollCount = 3);

public enum WorkshopSortOrder
{
    Hot,
    Updated
}

public enum WorkshopDeployMode
{
    Copy,
    PreferHardLink,
    HardLink
}

public enum WorkshopInstallStage
{
    ResolvingDependencies,
    Subscribing,
    WaitingForSteamDownload,
    Deploying,
    Completed
}

public sealed record WorkshopSearchRequest(
    string? Query = null,
    int Page = 1,
    int PageSize = 50,
    WorkshopSortOrder SortOrder = WorkshopSortOrder.Hot);

public sealed record WorkshopSearchResult(
    IReadOnlyList<WorkshopModInfo> Mods,
    int TotalCount,
    int Page,
    int PageSize);

public sealed record WorkshopModInfo(
    ulong PublishedFileId,
    string Name,
    string Summary,
    string Description,
    string Author,
    long? FileSizeInBytes,
    string? ThumbnailUrl,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt,
    ulong Subscriptions,
    uint VotesUp,
    uint VotesDown,
    float Score,
    IReadOnlyList<ulong> DependencyPublishedFileIds);

public sealed record WorkshopInstallRequest(
    ulong PublishedFileId,
    string ModsDirectory,
    WorkshopDeployMode DeployMode = WorkshopDeployMode.Copy,
    bool IncludeDependencies = true);

public sealed record WorkshopInstallProgress(
    ulong PublishedFileId,
    WorkshopInstallStage Stage,
    string Message);

public sealed record WorkshopInstallResult(
    bool Success,
    ulong PublishedFileId,
    string? WorkshopContentPath,
    string? InstalledPath,
    IReadOnlyList<ulong> InstalledDependencyPublishedFileIds,
    string Message);

public sealed record WorkshopCollectionInfo(
    ulong CollectionId,
    string Name,
    string Summary,
    string Author,
    string? ThumbnailUrl,
    DateTimeOffset? UpdatedAt,
    IReadOnlyList<ulong> ChildPublishedFileIds);

public sealed record WorkshopCollectionInstallRequest(
    ulong CollectionId,
    string ModsDirectory,
    WorkshopDeployMode DeployMode = WorkshopDeployMode.Copy);

public sealed record WorkshopCollectionInstallProgress(
    ulong CollectionId,
    ulong? CurrentPublishedFileId,
    int CompletedCount,
    int TotalCount,
    string Message);

public sealed record WorkshopCollectionItemFailure(
    ulong PublishedFileId,
    string Message);

public sealed record WorkshopCollectionInstallResult(
    ulong CollectionId,
    IReadOnlyList<ulong> InstalledPublishedFileIds,
    IReadOnlyList<WorkshopCollectionItemFailure> Failures,
    string Message)
{
    public bool Success => Failures.Count == 0 && InstalledPublishedFileIds.Count > 0;

    public int InstalledCount => InstalledPublishedFileIds.Count;

    public int FailedCount => Failures.Count;
}

public sealed record WorkshopMappingEntry(
    ulong PublishedFileId,
    string Title,
    string WorkshopContentPath,
    string InstalledPath,
    DateTimeOffset InstalledAt,
    IReadOnlyList<ulong> DependencyPublishedFileIds);
