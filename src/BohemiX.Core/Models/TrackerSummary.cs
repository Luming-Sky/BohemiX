namespace BohemiX.Core.Models;

public sealed record TrackerSummary(
    int TotalEvents,
    int VisibleEntities,
    int ActiveQuests,
    int CompletedEntities,
    int UnconfirmedSessions,
    string LastEventLabel,
    DateTimeOffset? LastEventUtc,
    double? PlayerX,
    double? PlayerY,
    double? PlayerZ)
{
    public static TrackerSummary Empty { get; } = new(
        0,
        0,
        0,
        0,
        0,
        "No tracker events processed",
        null,
        null,
        null,
        null);
}
