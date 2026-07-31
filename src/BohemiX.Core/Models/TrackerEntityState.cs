namespace BohemiX.Core.Models;

public sealed record TrackerEntityState(
    string EntityId,
    TrackerEntityKind Kind,
    string DisplayName,
    TrackerEntityStatus Status,
    bool IsVisible,
    DateTimeOffset LastSeenUtc,
    double? X,
    double? Y,
    double? Z);
