using BohemiX.Core.Models;

namespace BohemiX.Core.Services;

/// <summary>
/// Reads the append-only BohemiX Tracker bridge file and projects accepted events into local state.
/// Implementations must treat malformed, locked, or partially written JSONL data as recoverable input.
/// </summary>
public interface ITrackerProjectionService
{
    /// <summary>
    /// Processes newly appended lines from the local JSONL bridge file and persists the resulting projection.
    /// </summary>
    Task<TrackerIngestResult> IngestBridgeFileAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the latest spoiler-safe Tracker summary for the UI.
    /// </summary>
    Task<TrackerSummary> LoadSummaryAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns visible Tracker entities only. Hidden quests or undiscovered entities must not be returned.
    /// </summary>
    Task<IReadOnlyList<TrackerEntityState>> LoadVisibleEntitiesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns compact labels for the most recent accepted Tracker events.
    /// </summary>
    Task<IReadOnlyList<string>> LoadRecentEventLabelsAsync(int limit, CancellationToken cancellationToken = default);
}
