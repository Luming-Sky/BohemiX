namespace BohemiX.Core.Models.Saves;

/// <summary>
/// An immutable, independently restorable game save file stored in the backup library.
/// </summary>
public sealed record SaveBackupNode(
    Guid Id,
    Guid ProfileId,
    string SourceRelativePath,
    string ContentSha256,
    string ManifestRelativePath,
    long TotalBytes,
    DateTimeOffset FirstSeenAtUtc,
    DateTimeOffset LastSeenAtUtc,
    DateTimeOffset? LastSavedAtUtc,
    string? GameSaveName,
    TimeSpan? PlayTime,
    DisplayInfo? DisplayData,
    SaveType? Type,
    string? GameVersion,
    bool IsImportant,
    SaveBackupImportanceSource ImportanceSource,
    DateTimeOffset? ProtectedAtUtc,
    string? Note,
    SaveBackupNodeHealth Health = SaveBackupNodeHealth.Healthy,
    string? HealthMessage = null)
{
    public string FileName => Path.GetFileName(SourceRelativePath);
}
