namespace BohemiX.Core.Models;

public sealed record AchievementDefinition(
    string Id,
    string Title,
    string Description,
    string RuleExpression,
    int TargetValue);

