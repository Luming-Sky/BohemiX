using BohemiX.Core.Models;

namespace BohemiX.Core.Messages;

public sealed record AchievementUnlockedMessage(AchievementProgress Progress);

