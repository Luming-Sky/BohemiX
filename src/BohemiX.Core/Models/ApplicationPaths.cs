namespace BohemiX.Core.Models;

public sealed record ApplicationPaths(
    string DataDirectory,
    string DatabasePath,
    string LogsDirectory,
    string ModsDirectory,
    string NativeDirectory,
    string TrackerDirectory,
    string TrackerBridgeEventsPath,
    string TrackerAchievementRulesPath,
    Guid GameEnvironmentId = default,
    string? AccountsDatabasePath = null,
    string? AccountsDirectory = null,
    string? GameEnvironmentsDirectory = null,
    string? SettingsDirectory = null,
    string? CacheDirectory = null,
    string? BackupDirectory = null,
    string? SavesDirectory = null);
