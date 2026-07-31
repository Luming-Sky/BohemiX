using BohemiX.Core.Models;

namespace BohemiX.Core.Services;

/// <summary>
/// Exports the prepared Tracker mod package into a verified KCD2 Mods directory without touching core game files.
/// </summary>
public interface ITrackerModInstallService
{
    /// <summary>
    /// Copies the local Tracker package into `Mods/bohemix-tracker` under the verified game installation.
    /// </summary>
    Task<TrackerModInstallResult> InstallAsync(DiscoveredGame game, CancellationToken cancellationToken = default);
}
