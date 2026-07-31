using BohemiX.Core.Models;

namespace BohemiX.Core.Services;

/// <summary>
/// Reads and writes durable local user settings from the application database.
/// Implementations must keep settings local-first and safe to use while offline.
/// </summary>
public interface IAppSettingsService
{
    /// <summary>
    /// Loads the current local settings, returning defaults when the database has no stored value.
    /// </summary>
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists the supplied local settings atomically enough that a process crash cannot leave required keys half-written.
    /// </summary>
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

