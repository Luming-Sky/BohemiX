using BohemiX.Core.Models;

namespace BohemiX.Core.Services;

/// <summary>
/// Owns the single Tracker bridge monitor and publishes complete, spoiler-safe runtime snapshots.
/// Consumers must not start independent bridge watchers.
/// </summary>
public interface ITrackerRuntimeService
{
    TrackerRuntimeSnapshot Current { get; }

    Exception? LastError { get; }

    event Action<TrackerRuntimeUpdate>? Updated;

    Task<TrackerRuntimeUpdate> RefreshAsync(CancellationToken cancellationToken = default);

    void Start();

    Task StopAsync();
}
