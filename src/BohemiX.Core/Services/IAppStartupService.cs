using BohemiX.Core.Models;

namespace BohemiX.Core.Services;

/// <summary>
/// Initializes the local runtime foundation before UI workflows read persistent state.
/// Implementations create required directories, database schema, and default settings.
/// </summary>
public interface IAppStartupService
{
    /// <summary>
    /// Ensures the application data directories and database schema exist, then returns the resolved paths.
    /// </summary>
    Task<ApplicationPaths> InitializeAsync(CancellationToken cancellationToken = default);
}

