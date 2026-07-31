using BohemiX.Core.Models.Saves;

namespace BohemiX.Core.Services.Saves;

/// <summary>
/// Manages Windows directory junctions used to route the official KCD2 save folder to a Vault slot.
/// </summary>
public interface IJunctionRouter
{
    /// <summary>
    /// Mounts linkPath as a directory junction pointing at targetPath.
    /// </summary>
    void MountJunction(string linkPath, string targetPath);

    /// <summary>
    /// Removes a junction after verifying linkPath is a mount-point reparse point.
    /// </summary>
    void UnmountJunction(string linkPath);

    /// <summary>
    /// Reads junction details, or returns null when linkPath is not a directory junction.
    /// </summary>
    JunctionInfo? TryGetJunctionInfo(string linkPath);
}
