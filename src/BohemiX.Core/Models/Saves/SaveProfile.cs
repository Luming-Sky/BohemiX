namespace BohemiX.Core.Models.Saves;

/// <summary>
/// A writable, Junction-backed save environment managed by BohemiX.
/// </summary>
public sealed record SaveProfile(
    Guid Id,
    string PhysicalName,
    string DisplayName,
    string PhysicalPath,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    bool IsFavorite,
    DateTimeOffset? LastActivatedAtUtc,
    string? ContentFingerprint,
    string? GameSaveName,
    TimeSpan? PlayTime,
    DateTimeOffset? LastSavedAtUtc,
    DisplayInfo? DisplayData = null,
    SaveType? Type = null,
    string? GameVersion = null,
    string? ThumbnailPath = null,
    bool IsActive = false);
