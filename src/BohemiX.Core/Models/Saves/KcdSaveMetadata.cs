namespace BohemiX.Core.Models.Saves;

/// <summary>
/// Metadata extracted from a Kingdom Come: Deliverance II save slot without changing its files.
/// </summary>
public sealed record KcdSaveMetadata(
    TimeSpan? PlayTime,
    DateTimeOffset? LastSavedAtUtc,
    string? GameSaveName,
    DisplayInfo? DisplayData = null,
    SaveType? Type = null,
    string? GameVersion = null,
    string? ThumbnailPath = null);
