using BohemiX.Core.Models;

namespace BohemiX.Core.Services;

/// <summary>
/// Reads local mod metadata and exposes the enabled mod set for load-order and VFS operations.
/// Implementations must treat mod archives and extracted folders as untrusted IO inputs.
/// </summary>
public interface IModCatalogService
{
    /// <summary>
    /// Loads known mod manifests from the local catalog without mutating physical game files.
    /// </summary>
    Task<IReadOnlyList<ModManifest>> LoadInstalledModsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists the enabled mod load order by mod id without writing to the physical game installation.
    /// </summary>
    Task SaveLoadOrderAsync(IReadOnlyList<string> orderedModIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists whether a mod participates in conflict analysis and VFS mount requests.
    /// </summary>
    Task SaveModEnabledStateAsync(string modId, bool isEnabled, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists enabled states for multiple mods in one atomic local catalog update.
    /// </summary>
    Task SaveModEnabledStatesAsync(
        IReadOnlyDictionary<string, bool> enabledStates,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the user-facing metadata stored in a local package manifest.
    /// </summary>
    Task UpdateModMetadataAsync(
        ModManifest mod,
        string displayName,
        string version,
        CancellationToken cancellationToken = default);
}
