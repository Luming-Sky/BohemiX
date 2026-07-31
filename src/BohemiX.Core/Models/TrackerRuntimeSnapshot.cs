namespace BohemiX.Core.Models;

public sealed record TrackerRuntimeSnapshot(
    TrackerSummary Summary,
    IReadOnlyList<TrackerEntityState> VisibleEntities,
    IReadOnlyList<string> RecentEventLabels,
    IReadOnlyList<AchievementProgress> Achievements,
    DateTimeOffset RefreshedAtUtc)
{
    public static TrackerRuntimeSnapshot Empty { get; } = new(
        TrackerSummary.Empty,
        [],
        [],
        [],
        DateTimeOffset.MinValue);
}

public sealed record TrackerRuntimeUpdate(
    TrackerRuntimeSnapshot Snapshot,
    int ProcessedEvents,
    int SkippedLines,
    bool IsAutomatic);
