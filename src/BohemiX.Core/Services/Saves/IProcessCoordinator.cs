namespace BohemiX.Core.Services.Saves;

/// <summary>
/// Coordinates game-process and file-write safety before any save-slot switch.
/// </summary>
public interface IProcessCoordinator
{
    /// <summary>
    /// Returns false while KingdomCome2.exe is running or the official save directory is being written.
    /// </summary>
    Task<bool> IsSafeToSwitch(CancellationToken cancellationToken = default);
}
