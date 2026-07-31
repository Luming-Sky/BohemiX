namespace BohemiX.Core.Models;

public sealed record TrackerEvent(
    string EventId,
    string SessionId,
    TrackerEventType EventType,
    DateTimeOffset OccurredAtUtc,
    string EntityId,
    string EntityName,
    double? X,
    double? Y,
    double? Z,
    string RawJson,
    int? HenryLevel = null,
    int? GroschenCount = null);
