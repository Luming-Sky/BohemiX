using BohemiX.Core.Models;

namespace BohemiX.Core.Services;

/// <summary>
/// Starts the game process and tracks launch state for the daemon workflow.
/// Implementations must report the process ID when available so VFS cleanup can run after game exit.
/// </summary>
public interface IGameLauncherService
{
    /// <summary>
    /// Launches the supplied game installation and returns the initial process tracking result.
    /// The method must not throw for common launch failures such as missing executables or denied access.
    /// </summary>
    Task<GameLaunchResult> LaunchAsync(
        DiscoveredGame game,
        GameLaunchOptions? options = null,
        CancellationToken cancellationToken = default);
}
