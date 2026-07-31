namespace BohemiX.Core.Models.Saves;

/// <summary>
/// An immutable recovery point belonging to a save profile.
/// </summary>
public sealed record SaveSnapshot(
    Guid Id,
    Guid ProfileId,
    string PhysicalName,
    string PhysicalPath,
    string? Note,
    SaveSnapshotTrigger Trigger,
    DateTimeOffset CreatedAtUtc,
    string ContentFingerprint,
    int FileCount,
    long TotalBytes,
    string? GameSaveName,
    TimeSpan? PlayTime,
    DateTimeOffset? LastSavedAtUtc,
    DisplayInfo? DisplayData = null,
    SaveType? Type = null,
    string? GameVersion = null,
    SaveSnapshotStorageKind StorageKind = SaveSnapshotStorageKind.LegacyDirectory,
    string? ManifestRelativePath = null);
