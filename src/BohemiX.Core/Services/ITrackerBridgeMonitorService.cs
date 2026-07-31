namespace BohemiX.Core.Services;

/// <summary>
/// Watches the local BohemiX Tracker JSONL bridge and reports settled file changes.
/// Implementations must not ingest or project tracker data; the runtime coordinator owns that work.
/// </summary>
public interface ITrackerBridgeMonitorService
{
    /// <summary>
    /// Watches the configured bridge file until cancellation and yields the detection time for each settled change.
    /// </summary>
    IAsyncEnumerable<DateTimeOffset> WatchAsync(CancellationToken cancellationToken = default);
}
