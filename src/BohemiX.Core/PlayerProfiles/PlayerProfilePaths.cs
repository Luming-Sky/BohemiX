namespace BohemiX.Core.PlayerProfiles;

public sealed record GlobalApplicationPaths(
    string RootDirectory,
    string AccountsDatabasePath,
    string AccountsDirectory,
    string DeletedAccountsDirectory,
    string GameEnvironmentsDirectory,
    string LogsDirectory,
    string LegacyDatabasePath,
    string LegacyModsDirectory,
    string LegacyNativeDirectory,
    string LegacyTrackerDirectory)
{
    [Obsolete("Use AccountsDirectory.")]
    public string ProfilesDirectory => AccountsDirectory;

    [Obsolete("Use DeletedAccountsDirectory.")]
    public string DeletedProfilesDirectory => DeletedAccountsDirectory;
}

[Obsolete("Account and game-environment paths are now separate.")]
public sealed record PlayerProfilePaths(
    Guid PlayerId,
    string RootDirectory,
    string DatabasePath,
    string ModsDirectory,
    string SettingsDirectory,
    string CacheDirectory,
    string BackupDirectory,
    string SavesDirectory,
    string NativeDirectory,
    string TrackerDirectory,
    string AvatarDirectory,
    string TrackerBridgeEventsPath,
    string TrackerAchievementRulesPath);

public sealed record AccountProfilePaths(
    Guid AccountId,
    string RootDirectory,
    string AvatarDirectory,
    string SettingsDirectory,
    string CacheDirectory);

public sealed record GameEnvironmentPaths(
    Guid EnvironmentId,
    string RootDirectory,
    string DatabasePath,
    string ModsDirectory,
    string NativeDirectory,
    string TrackerDirectory,
    string SettingsDirectory,
    string CacheDirectory,
    string BackupDirectory,
    string SavesDirectory,
    string TrackerBridgeEventsPath,
    string TrackerAchievementRulesPath);

public static class GameEnvironmentIds
{
    public static readonly Guid Default = Guid.Empty;
}
