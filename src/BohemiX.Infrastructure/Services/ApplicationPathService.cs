using BohemiX.Core.Models;
using BohemiX.Core.PlayerProfiles;
using BohemiX.Core.Services;

namespace BohemiX.Infrastructure.Services;

public sealed class ApplicationPathService : IApplicationPathService
{
    private readonly string? dataDirectoryOverride;

    public ApplicationPathService()
    {
    }

    public ApplicationPathService(IPlayerContext playerContext)
    {
        ArgumentNullException.ThrowIfNull(playerContext);
    }

    public ApplicationPathService(IPlayerContext playerContext, string dataDirectoryOverride)
    {
        ArgumentNullException.ThrowIfNull(playerContext);
        this.dataDirectoryOverride = dataDirectoryOverride;
    }

    public ApplicationPathService(string dataDirectoryOverride)
    {
        this.dataDirectoryOverride = dataDirectoryOverride;
    }

    public ApplicationPaths GetPaths()
    {
        var global = GetGlobalPaths();
        var environment = GetGameEnvironmentPaths(GameEnvironmentIds.Default);
        return new ApplicationPaths(
            environment.RootDirectory,
            environment.DatabasePath,
            global.LogsDirectory,
            environment.ModsDirectory,
            environment.NativeDirectory,
            environment.TrackerDirectory,
            environment.TrackerBridgeEventsPath,
            environment.TrackerAchievementRulesPath,
            environment.EnvironmentId,
            AccountsDatabasePath: global.AccountsDatabasePath,
            AccountsDirectory: global.AccountsDirectory,
            GameEnvironmentsDirectory: global.GameEnvironmentsDirectory,
            SettingsDirectory: environment.SettingsDirectory,
            CacheDirectory: environment.CacheDirectory,
            BackupDirectory: environment.BackupDirectory,
            SavesDirectory: environment.SavesDirectory);
    }

    public GlobalApplicationPaths GetGlobalPaths()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var rootDirectory = dataDirectoryOverride ?? Path.Combine(localAppData, "BohemiX");
        return new GlobalApplicationPaths(
            rootDirectory,
            Path.Combine(rootDirectory, "accounts.db"),
            Path.Combine(rootDirectory, "Accounts"),
            Path.Combine(rootDirectory, "DeletedAccounts"),
            Path.Combine(rootDirectory, "GameEnvironments"),
            Path.Combine(rootDirectory, "logs"),
            Path.Combine(rootDirectory, "bohemix.db"),
            Path.Combine(rootDirectory, "mods"),
            Path.Combine(rootDirectory, "native"),
            Path.Combine(rootDirectory, "tracker"));
    }

    public AccountProfilePaths GetAccountPaths(Guid accountId)
    {
        var root = Path.Combine(GetGlobalPaths().AccountsDirectory, accountId.ToString("N"));
        return new AccountProfilePaths(
            accountId,
            root,
            Path.Combine(root, "Avatar"),
            Path.Combine(root, "Settings"),
            Path.Combine(root, "Cache"));
    }

    public GameEnvironmentPaths GetGameEnvironmentPaths(Guid environmentId)
    {
        var global = GetGlobalPaths();
        var root = environmentId == GameEnvironmentIds.Default
            ? global.RootDirectory
            : Path.Combine(global.GameEnvironmentsDirectory, environmentId.ToString("N"));
        var tracker = environmentId == GameEnvironmentIds.Default
            ? global.LegacyTrackerDirectory
            : Path.Combine(root, "Tracker");
        return new GameEnvironmentPaths(
            environmentId,
            root,
            environmentId == GameEnvironmentIds.Default ? global.LegacyDatabasePath : Path.Combine(root, "bohemix.db"),
            environmentId == GameEnvironmentIds.Default ? global.LegacyModsDirectory : Path.Combine(root, "Mods"),
            environmentId == GameEnvironmentIds.Default ? global.LegacyNativeDirectory : Path.Combine(root, "Native"),
            tracker,
            Path.Combine(root, "Settings"),
            Path.Combine(root, "Cache"),
            Path.Combine(root, "Backup"),
            Path.Combine(root, "Saves"),
            Path.Combine(tracker, "bridge_events.jsonl"),
            Path.Combine(tracker, "tracker_achievement_rules.json"));
    }

    [Obsolete("Account and game-environment paths are now separate.")]
    public PlayerProfilePaths GetProfilePaths(Guid playerId)
    {
        var account = GetAccountPaths(playerId);
        var environment = GetGameEnvironmentPaths(GameEnvironmentIds.Default);
        return new PlayerProfilePaths(
            playerId,
            account.RootDirectory,
            environment.DatabasePath,
            environment.ModsDirectory,
            account.SettingsDirectory,
            account.CacheDirectory,
            environment.BackupDirectory,
            environment.SavesDirectory,
            environment.NativeDirectory,
            environment.TrackerDirectory,
            account.AvatarDirectory,
            environment.TrackerBridgeEventsPath,
            environment.TrackerAchievementRulesPath);
    }
}
