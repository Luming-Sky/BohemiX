using BohemiX.Core.Models;
using BohemiX.Core.PlayerProfiles;

namespace BohemiX.Core.Services;

/// <summary>
/// Resolves platform-specific BohemiX data paths without requiring UI code to know operating system conventions.
/// Implementations must return stable paths suitable for logs, SQLite storage, local mods, and native interop files.
/// </summary>
public interface IApplicationPathService
{
    /// <summary>
    /// Returns the resolved local application paths for the current user profile.
    /// </summary>
    ApplicationPaths GetPaths();

    GlobalApplicationPaths GetGlobalPaths();

    AccountProfilePaths GetAccountPaths(Guid accountId) =>
        throw new NotSupportedException("This path service does not expose account storage.");

    GameEnvironmentPaths GetGameEnvironmentPaths(Guid environmentId) =>
        throw new NotSupportedException("This path service does not expose game-environment storage.");

    [Obsolete("Account and game-environment paths are now separate.")]
    PlayerProfilePaths GetProfilePaths(Guid playerId) =>
        throw new NotSupportedException("This path service does not expose legacy player-profile storage.");
}
