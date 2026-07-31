namespace BohemiX.Core.Models;

public enum ModPackImportPrepareStatus
{
    Ready,
    PasswordRequired,
    InvalidPassword,
    NoModsFound,
    InvalidArchive,
    LimitExceeded
}

public enum ModPackImportItemState
{
    New,
    AlreadyInstalled,
    DuplicateInPackage,
    Invalid
}

public enum ModPackImportAction
{
    Install,
    Skip,
    Replace
}

public enum ModPackImportItemResultState
{
    Installed,
    Replaced,
    Skipped,
    Failed
}

public sealed record ModPackImportItem(
    string Id,
    string DisplayName,
    string Version,
    string Author,
    string RelativePath,
    string PayloadDirectory,
    ModPackImportItemState State,
    string StatusMessage,
    string? ExistingRootPath = null,
    string? NativeOrderName = null);

public sealed record ModPackImportPlan(
    Guid SessionId,
    string PackageName,
    string PackagePath,
    string PackageSha256,
    IReadOnlyList<ModPackImportItem> Items,
    IReadOnlyList<string> AuthorLoadOrder,
    IReadOnlyList<string> Warnings,
    int IgnoredFileCount);

public sealed record ModPackImportPrepareResult(
    ModPackImportPrepareStatus Status,
    string Message,
    ModPackImportPlan? Plan = null,
    string? PasswordArchivePath = null);

public sealed record ModPackImportSelection(string ItemId, ModPackImportAction Action);

public sealed record ModPackImportProgress(
    string Message,
    int CompletedCount,
    int TotalCount,
    string? CurrentItemId = null,
    string? CurrentItemName = null);

public sealed record ModPackImportItemResult(
    string ItemId,
    ModPackImportItemResultState State,
    string Message);

public sealed record ModPackImportResult(
    Guid SessionId,
    int InstalledCount,
    int ReplacedCount,
    int SkippedCount,
    int FailedCount,
    IReadOnlyList<ModPackImportItemResult> Items,
    string Message);
