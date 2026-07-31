using BohemiX.Core.Models;

namespace BohemiX.Core.Services;

/// <summary>
/// Owns the lifecycle of the virtual file system session used to mount mods without overwriting game files.
/// Implementations hide all native `usvfs.dll` interop behind this managed boundary.
/// </summary>
public interface IVfsSessionService
{
    /// <summary>
    /// Gets the current lifecycle state of the virtual file system session.
    /// </summary>
    VfsSessionState CurrentState { get; }

    /// <summary>
    /// Gets details about the active native VFS session, or <see langword="null"/> when no session is mounted.
    /// </summary>
    VfsSessionInfo? SessionInfo { get; }

    /// <summary>
    /// Gets the most recent VFS lifecycle error so callers can report the exact failure location.
    /// </summary>
    Exception? LastError { get; }

    /// <summary>
    /// Mounts enabled mods into the virtual file system before the game process is started.
    /// </summary>
    Task MountAsync(VfsMountRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts a process through the native VFS hook. A normally started process cannot see virtual mappings.
    /// </summary>
    Task<VfsProcessLaunchResult> LaunchProcessAsync(
        VfsProcessLaunchRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Unmounts the virtual file system and releases native resources after the game process exits.
    /// </summary>
    Task UnmountAsync(CancellationToken cancellationToken = default);
}
