using BohemiX.Core.Models;

namespace BohemiX.Core.Services;

/// <summary>
/// Checks whether the local and game-side Tracker mod files are ready without launching or inspecting the game process.
/// </summary>
public interface ITrackerModHealthService
{
    /// <summary>
    /// Checks local package, optional game installation, mod order, and bridge file readiness.
    /// </summary>
    Task<TrackerModHealth> CheckAsync(DiscoveredGame? game, CancellationToken cancellationToken = default);
}
