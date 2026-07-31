namespace BohemiX.Core.Models;

public enum ModPackInstallItemSource
{
    Nexus,
    Bundle,
    Direct,
    Browse,
    Manual,
    Unknown
}

public enum ModPackInstallItemSupport
{
    Supported,
    ManualActionRequired,
    RequiresVortex,
    Invalid
}

public enum ModPackInstallItemStatus
{
    Pending,
    WaitingForAuthorization,
    Downloading,
    Installing,
    Installed,
    Reused,
    Skipped,
    Failed,
    Paused,
    Canceled
}

public enum ModPackInstallSessionStatus
{
    Prepared,
    Running,
    Paused,
    Completed,
    PartiallyCompleted,
    Canceled,
    Failed
}

public enum ModPackInstallStage
{
    Preparing,
    WaitingForAuthorization,
    Downloading,
    Installing,
    ApplyingLoadOrder,
    Completed,
    Paused,
    Failed
}

public sealed record NexusCollectionRevisionInfo(
    long CollectionId,
    long RevisionId,
    string Slug,
    string Name,
    string Summary,
    string GameDomainName,
    int RevisionNumber,
    string RevisionStatus,
    bool AdultContent,
    string DownloadLink,
    long? FileSizeInBytes,
    long? TotalSizeInBytes,
    int? ModCount);

public sealed record NexusCollectionManifestInfo(
    string Author,
    string Name,
    string Description,
    string Summary,
    string DomainName,
    IReadOnlyList<string> GameVersions,
    string InstallInstructions);

public sealed record NexusCollectionManifestItem(
    string Id,
    string Name,
    string Version,
    bool Optional,
    string DomainName,
    ModPackInstallItemSource Source,
    int? ModId,
    int? FileId,
    string? Md5,
    long? FileSizeInBytes,
    string? LogicalFileName,
    string? FileExpression,
    string? Url,
    string? Instructions,
    string? Author,
    int Phase,
    bool HasInstallerChoices,
    bool HasPatches,
    bool HasFileOverrides,
    bool ContainsAdultContent);

public sealed record NexusCollectionManifest(
    NexusCollectionManifestInfo Info,
    IReadOnlyList<NexusCollectionManifestItem> Items,
    IReadOnlyList<string> LoadOrder);

public sealed record NexusCollectionPackage(
    NexusCollectionRevisionInfo Revision,
    NexusCollectionManifest Manifest,
    string SessionDirectory,
    string ArchivePath,
    string BundledDirectory);

public sealed record ModPackInstallPlanItem(
    string Id,
    string Name,
    string Version,
    bool IsOptional,
    ModPackInstallItemSource Source,
    ModPackInstallItemSupport Support,
    string SupportMessage,
    int Phase,
    int? ModId,
    int? FileId,
    string? Md5,
    long? FileSizeInBytes,
    string? BundlePath,
    string? ManualUrl,
    string? Instructions,
    bool ContainsAdultContent);

public sealed record ModPackInstallPlan(
    Guid SessionId,
    string ModPackId,
    string ModPackName,
    string OfficialPageUrl,
    string PlatformIdentifier,
    int RevisionNumber,
    long RevisionId,
    bool ContainsAdultContent,
    string CompatibilityText,
    long? TotalSizeInBytes,
    IReadOnlyList<ModPackInstallPlanItem> Items,
    IReadOnlyList<string> LoadOrder)
{
    public int RequiredCount => Items.Count(item => !item.IsOptional);

    public int OptionalCount => Items.Count(item => item.IsOptional);

    public int UnsupportedCount => Items.Count(item => item.Support != ModPackInstallItemSupport.Supported);
}

public sealed record ModPackInstallItemState(
    string ItemId,
    ModPackInstallItemStatus Status,
    string? InstalledModId = null,
    string? ErrorMessage = null);

public sealed record ModPackInstallSession(
    Guid SessionId,
    ModPackInstallPlan Plan,
    ModPackInstallSessionStatus Status,
    IReadOnlyList<string> SelectedOptionalItemIds,
    IReadOnlyList<ModPackInstallItemState> Items,
    DateTimeOffset UpdatedAt,
    string? Message = null);

public sealed record ModPackInstallProgress(
    Guid SessionId,
    ModPackInstallStage Stage,
    string Message,
    int CompletedCount,
    int TotalCount,
    string? CurrentItemId = null,
    string? CurrentItemName = null,
    ModDownloadProgress? DownloadProgress = null);

public sealed record ModPackInstallResult(
    Guid SessionId,
    ModPackInstallSessionStatus Status,
    int InstalledCount,
    int ReusedCount,
    int SkippedCount,
    int FailedCount,
    IReadOnlyList<ModPackInstallItemState> Items,
    string Message);

public sealed record NexusDownloadAuthorizationRequest(
    string GameDomainName,
    int ModId,
    int FileId,
    string ModName,
    string FileName,
    Uri OfficialFilePageUri);

public sealed record NexusDownloadAuthorization(
    string Key,
    long Expires,
    long? UserId = null);

public sealed record NexusDownloadAuthorizationResult(
    bool Authorized,
    NexusDownloadAuthorization? Authorization,
    string? Message = null);

public sealed record ModPackageSourceMetadata(
    string? Platform = null,
    int? NexusModId = null,
    int? NexusFileId = null,
    string? Md5 = null,
    string? ModPackId = null,
    int? ModPackRevision = null,
    Guid? ModPackSessionId = null,
    string? ModPackName = null);
