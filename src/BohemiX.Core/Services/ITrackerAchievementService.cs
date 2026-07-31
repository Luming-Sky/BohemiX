using BohemiX.Core.Models;

namespace BohemiX.Core.Services;

/// <summary>
/// Evaluates local achievement rules from spoiler-safe BohemiX Tracker projections.
/// Implementations must read only local BohemiX data and must not inspect game memory or mutate save files.
/// </summary>
public interface ITrackerAchievementService
{
    /// <summary>
    /// Recomputes tracker achievement progress from persisted tracker events and entity states.
    /// </summary>
    Task<IReadOnlyList<AchievementProgress>> EvaluateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads the last persisted tracker achievement progress.
    /// </summary>
    Task<IReadOnlyList<AchievementProgress>> LoadProgressAsync(CancellationToken cancellationToken = default);
}
