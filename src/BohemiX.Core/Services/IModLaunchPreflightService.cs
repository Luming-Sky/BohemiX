using BohemiX.Core.Models;

namespace BohemiX.Core.Services;

/// <summary>
/// Evaluates whether the current mod state is ready for launch and prepares the VFS mount request metadata.
/// </summary>
public interface IModLaunchPreflightService
{
    /// <summary>
    /// Loads the current mod snapshot and returns launch readiness without starting the game or mounting VFS.
    /// </summary>
    Task<ModLaunchPreflightResult> EvaluateAsync(
        ModLaunchPreflightOptions options,
        CancellationToken cancellationToken = default);
}
