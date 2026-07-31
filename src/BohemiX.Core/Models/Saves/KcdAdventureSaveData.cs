namespace BohemiX.Core.Models.Saves;

/// <summary>
/// Spoiler-free aggregate data read from one KCD2 save playline.
/// Nullable progress values indicate that the source does not expose that value reliably.
/// </summary>
public sealed record KcdAdventureSaveData(
    string Difficulty,
    int? InGameDay,
    int? MainStoryCompleted,
    int? MainStoryTotal,
    int? AchievementsUnlocked,
    int? AchievementsTotal,
    int? LocationsDiscovered,
    int? LocationsTotal,
    int SaveFileCount)
{
    public static KcdAdventureSaveData Empty { get; } = new(
        "--",
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        0);
}
