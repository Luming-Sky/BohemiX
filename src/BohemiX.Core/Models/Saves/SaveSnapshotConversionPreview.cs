namespace BohemiX.Core.Models.Saves;

public sealed record SaveSnapshotConversionPreview(
    Guid SnapshotId,
    IReadOnlyList<SaveSnapshotConversionFile> Files,
    long SnapshotLogicalBytes,
    long SelectedLogicalBytes,
    long EstimatedReclaimableBytes);

public sealed record SaveSnapshotConversionFile(
    string RelativePath,
    long Length,
    DateTimeOffset? LastSavedAtUtc,
    SaveType? Type,
    bool IsRecommended,
    bool IsCriticalDecision);
