using BohemiX.Core.Models;
using BohemiX.Core.Services;
using Serilog;

namespace BohemiX.Infrastructure.Services;

public sealed class AchievementRuleService : IAchievementRuleService
{
    private readonly ILogger logger;

    public AchievementRuleService(ILogger logger)
    {
        this.logger = logger.ForContext<AchievementRuleService>();
    }

    public Task<IReadOnlyList<AchievementProgress>> EvaluateAsync(
        IReadOnlyList<AchievementDefinition> definitions,
        IReadOnlyDictionary<string, int> progressionValues,
        CancellationToken cancellationToken = default)
    {
        List<AchievementProgress> results = [];

        foreach (var definition in definitions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            progressionValues.TryGetValue(definition.RuleExpression, out var currentValue);
            var unlocked = currentValue >= definition.TargetValue;
            results.Add(new AchievementProgress(
                definition.Id,
                currentValue,
                definition.TargetValue,
                unlocked,
                unlocked ? DateTimeOffset.UtcNow : null));
        }

        logger.Information("Evaluated {AchievementCount} achievement definitions", definitions.Count);
        return Task.FromResult<IReadOnlyList<AchievementProgress>>(results);
    }
}

