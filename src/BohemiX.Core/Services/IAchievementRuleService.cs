using BohemiX.Core.Models;

namespace BohemiX.Core.Services;

/// <summary>
/// Evaluates local achievement definitions against parsed player progression values.
/// Implementations must be deterministic so achievement unlocks can be replayed from local save snapshots.
/// </summary>
public interface IAchievementRuleService
{
    /// <summary>
    /// Evaluates achievement definitions with already parsed numeric progression data.
    /// Keys in <paramref name="progressionValues"/> are canonical metric names produced by the save parser.
    /// </summary>
    Task<IReadOnlyList<AchievementProgress>> EvaluateAsync(
        IReadOnlyList<AchievementDefinition> definitions,
        IReadOnlyDictionary<string, int> progressionValues,
        CancellationToken cancellationToken = default);
}

