using BohemiX.Core.Models;

namespace BohemiX.Core.Services;

/// <summary>
/// Persists one durable record for every game launch attempt and closes it when process monitoring completes.
/// </summary>
public interface IGameLaunchSessionService
{
    Task<GameLaunchSession> BeginAsync(
        DiscoveredGame game,
        GameLaunchOptions options,
        Guid? vfsSessionId,
        CancellationToken cancellationToken = default);

    Task MarkRunningAsync(Guid sessionId, int processId, CancellationToken cancellationToken = default);

    Task MarkFailedAsync(Guid sessionId, string message, CancellationToken cancellationToken = default);

    Task MarkExitedByProcessIdAsync(
        int processId,
        int? exitCode,
        CancellationToken cancellationToken = default);

    Task MarkMonitoringLostAsync(int processId, string message, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GameLaunchSession>> GetRecentAsync(
        int limit = 50,
        CancellationToken cancellationToken = default);
}
