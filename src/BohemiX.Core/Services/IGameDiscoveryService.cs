using BohemiX.Core.Models;

namespace BohemiX.Core.Services;

/// <summary>
/// Locates supported game installations from local launcher metadata and user-provided paths.
/// Implementations should check Steam libraries, Epic manifests, and a manual fallback without blocking the UI thread.
/// </summary>
public interface IGameDiscoveryService
{
    /// <summary>
    /// Discovers installed Kingdom Come: Deliverance II locations available on the current machine.
    /// Returned paths must be verified before they are marked as trusted launch targets.
    /// </summary>
    Task<IReadOnlyList<DiscoveredGame>> DiscoverInstalledGamesAsync(
        GameDiscoverySearchMode searchMode = GameDiscoverySearchMode.Fast,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies a user-selected executable or installation directory and stores it as a manual installation when valid.
    /// </summary>
    Task<DiscoveredGame?> VerifyManualPathAsync(string path, CancellationToken cancellationToken = default);
}
