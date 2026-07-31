using BohemiX.Core.Models;

namespace BohemiX.Core.Services;

/// <summary>
/// Waits for a launched game process to exit so cleanup hooks can run without blocking the UI thread.
/// </summary>
public interface IGameProcessMonitorService
{
    /// <summary>
    /// Waits until the process exits. Returns false when the process cannot be found or monitored.
    /// </summary>
    Task<bool> WaitForExitAsync(int processId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Waits until the process exits and returns the native exit code when it is available.
    /// </summary>
    Task<GameProcessExitResult> WaitForExitResultAsync(
        int processId,
        CancellationToken cancellationToken = default);
}
