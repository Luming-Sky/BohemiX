namespace BohemiX.Core.Models;

public sealed record AchievementProgress(
    string AchievementId,
    int CurrentValue,
    int TargetValue,
    bool IsUnlocked,
    DateTimeOffset? UnlockedAt);

