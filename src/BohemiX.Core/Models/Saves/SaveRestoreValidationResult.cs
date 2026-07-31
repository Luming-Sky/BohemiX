namespace BohemiX.Core.Models.Saves;

public enum SaveRestoreSourceKind
{
    Snapshot = 0,
    BackupNode = 1
}

public sealed record SaveRestoreValidationIssue(
    string Code,
    string Message,
    bool IsBlocking);

public sealed record SaveRestoreValidationResult(
    SaveRestoreSourceKind SourceKind,
    Guid SourceId,
    Guid ProfileId,
    bool CanRestore,
    string? ExpectedFingerprint,
    string? VerifiedFingerprint,
    int FileCount,
    long TotalBytes,
    IReadOnlyList<SaveRestoreValidationIssue> Issues);
