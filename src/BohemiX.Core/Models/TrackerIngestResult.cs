namespace BohemiX.Core.Models;

public sealed record TrackerIngestResult(
    int ProcessedEvents,
    int SkippedLines,
    long LastOffset,
    TrackerSummary Summary);
