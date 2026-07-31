using BohemiX.Core.Models;

namespace BohemiX.Core.Services;

/// <summary>
/// Persists discovered game installations so the launcher can work from cached local state when offline.
/// Implementations should prefer the latest verified executable path for each installation root.
/// </summary>
public interface IGameInstallationStore
{
    /// <summary>
    /// Loads cached game installations from local storage.
    /// </summary>
    Task<IReadOnlyList<DiscoveredGame>> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces cached game installations with the latest discovery result.
    /// </summary>
    Task SaveAsync(IReadOnlyList<DiscoveredGame> games, CancellationToken cancellationToken = default);
}

