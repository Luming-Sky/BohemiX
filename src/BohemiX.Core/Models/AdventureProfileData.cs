namespace BohemiX.Core.Models;

/// <summary>
/// Read-only, environment-scoped game data used by the launcher adventure profile.
/// Account identity is intentionally not part of this model.
/// </summary>
public sealed record AdventureProfileData(
    string PlayerName,
    string Platform,
    string Difficulty,
    int HoursPlayed,
    int? InGameDay,
    int? MainStoryCompleted,
    int? MainStoryTotal,
    int? AchievementsUnlocked,
    int? AchievementsTotal,
    int? LocationsDiscovered,
    int? LocationsTotal,
    int SaveSlots,
    int InstalledMods)
{
    public static AdventureProfileData Empty { get; } = new(
        string.Empty,
        string.Empty,
        "--",
        0,
        0,
        null,
        null,
        null,
        null,
        null,
        null,
        0,
        0);
}
