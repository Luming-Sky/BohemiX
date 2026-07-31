namespace BohemiX.Core.Models;

public sealed record PlayerStatistic(
    string Key,
    long Value,
    long? Total,
    string? TextValue,
    DateTimeOffset UpdatedTime);

public static class PlayerStatisticKeys
{
    public const string Difficulty = "difficulty";
    public const string HoursPlayed = "hours-played";
    public const string DaysPlayed = "days-played";
    public const string MainStory = "main-story";
    public const string Achievements = "achievements";
    public const string Exploration = "exploration";
    public const string SaveSlots = "save-slots";
    public const string InstalledMods = "installed-mods";

    public const string Alchemy = "alchemy";
    public const string Forging = "forging";
    public const string Combat = "combat";
    public const string Collectibles = "collectibles";
    public const string Wealth = "wealth";
    public const string LaunchCount = "launch-count";
}
