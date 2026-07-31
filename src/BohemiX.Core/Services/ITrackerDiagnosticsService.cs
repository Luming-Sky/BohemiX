using BohemiX.Core.Models;

namespace BohemiX.Core.Services;

/// <summary>
/// Runs non-game diagnostic checks for the Tracker JSONL bridge and projection pipeline.
/// Diagnostics must not persist synthetic progress as real player state.
/// </summary>
public interface ITrackerDiagnosticsService
{
    /// <summary>
    /// Appends diagnostic events, verifies ingestion, then removes synthetic projection state.
    /// </summary>
    Task<TrackerDiagnosticsResult> RunBridgeSelfTestAsync(CancellationToken cancellationToken = default);
}
