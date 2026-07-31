#pragma warning disable CS0618 // This suite verifies the legacy profile migration boundary.

using BohemiX.Core.Models;
using BohemiX.Core.PlayerProfiles;
using BohemiX.Infrastructure.Persistence;
using BohemiX.Infrastructure.PlayerProfiles;
using BohemiX.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Serilog;

namespace BohemiX.Core.Tests;

public sealed class PlayerProfileSystemTests
{
    [Fact]
    public async Task FirstProfile_BecomesCurrentAndCreatesAccountOnlyDirectories()
    {
        var root = CreateTestRoot();
        try
        {
            var runtime = CreateRuntime(root);
            await runtime.Service.InitializeAsync();

            Assert.Null(runtime.Service.CurrentPlayer);
            Assert.Empty(runtime.Service.Profiles);

            var profile = await runtime.Service.CreateAsync(new PlayerProfileDraft(
                "Henry",
                Avatar: null,
                PlayerPlatformIds.Steam,
                PlayerProviderIds.Local,
                GameInstallPath: null));

            Assert.Equal(profile.Id, runtime.Service.CurrentPlayer?.Id);
            Assert.False(profile.IsCloudUser);
            Assert.Null(profile.CloudId);
            Assert.Null(profile.Email);
            Assert.Null(profile.AccessToken);
            Assert.Null(profile.RefreshToken);
            Assert.Null(profile.SyncTime);

            var paths = runtime.Paths.GetAccountPaths(profile.Id);
            Assert.True(Directory.Exists(paths.RootDirectory));
            Assert.True(Directory.Exists(paths.SettingsDirectory));
            Assert.True(Directory.Exists(paths.CacheDirectory));
            Assert.True(Directory.Exists(paths.AvatarDirectory));
            Assert.True(File.Exists(runtime.Paths.GetGlobalPaths().AccountsDatabasePath));
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    [Fact]
    public async Task CurrentPlayerSelection_PersistsAcrossServiceInstances()
    {
        var root = CreateTestRoot();
        try
        {
            var firstRuntime = CreateRuntime(root);
            await firstRuntime.Service.InitializeAsync();
            var henry = await CreateLocalPlayerAsync(firstRuntime.Service, "Henry", PlayerPlatformIds.Steam);
            var theresa = await CreateLocalPlayerAsync(firstRuntime.Service, "Theresa", PlayerPlatformIds.Gog);
            Assert.Equal(theresa.Id, firstRuntime.Service.CurrentPlayer?.Id);

            await firstRuntime.Service.SwitchAsync(henry.Id);

            var restartedRuntime = CreateRuntime(root);
            await restartedRuntime.Service.InitializeAsync();

            Assert.Equal(henry.Id, restartedRuntime.Service.CurrentPlayer?.Id);
            Assert.Equal(2, restartedRuntime.Service.Profiles.Count);
            Assert.Equal(PlayerPlatformIds.Steam, restartedRuntime.Service.CurrentPlayer?.Platform);
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    [Fact]
    public async Task Repository_RoundTripsReservedCloudFields()
    {
        var root = CreateTestRoot();
        try
        {
            var runtime = CreateRuntime(root);
            await runtime.Repository.InitializeAsync();
            var now = DateTimeOffset.UtcNow;
            var expected = new PlayerProfile(
                Guid.NewGuid(),
                "Cloud Ready",
                Avatar: null,
                PlayerPlatformIds.Steam,
                "future-provider",
                GameInstallPath: @"D:\Games\KCD2",
                now.AddDays(-2),
                now,
                CloudId: "cloud-42",
                Email: "player@example.test",
                AccessToken: "access-token-placeholder",
                RefreshToken: "refresh-token-placeholder",
                IsCloudUser: true,
                SyncTime: now.AddMinutes(-5),
                Version: 7);

            await runtime.Repository.UpsertAsync(expected);
            var actual = await runtime.Repository.GetByIdAsync(expected.Id);

            Assert.NotNull(actual);
            Assert.Equal(expected.CloudId, actual.CloudId);
            Assert.Equal(expected.Email, actual.Email);
            Assert.Equal(expected.AccessToken, actual.AccessToken);
            Assert.Equal(expected.RefreshToken, actual.RefreshToken);
            Assert.Equal(expected.IsCloudUser, actual.IsCloudUser);
            Assert.Equal(expected.SyncTime, actual.SyncTime);
            Assert.Equal(expected.Provider, actual.Provider);
            Assert.Equal(expected.Version, actual.Version);
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    [Fact]
    public async Task AccountSwitch_PreservesIndependentGameEnvironmentData()
    {
        var root = CreateTestRoot();
        try
        {
            var runtime = CreateRuntime(root);
            await runtime.Service.InitializeAsync();
            var henry = await CreateLocalPlayerAsync(runtime.Service, "Henry", PlayerPlatformIds.Steam);

            var connectionFactory = new SqliteConnectionFactory(runtime.Paths);
            var settingsService = new AppSettingsService(runtime.Paths, connectionFactory, runtime.Logger);
            var startupService = new AppStartupService(runtime.Paths, settingsService, connectionFactory, runtime.Logger);
            await startupService.InitializeAsync();
            var henrySettings = await settingsService.LoadAsync();
            await settingsService.SaveAsync(henrySettings with { SelectedLanguage = "English" });

            var theresa = await CreateLocalPlayerAsync(runtime.Service, "Theresa", PlayerPlatformIds.Gog);
            await startupService.InitializeAsync();
            var theresaSettings = await settingsService.LoadAsync();
            Assert.Equal("English", theresaSettings.SelectedLanguage);
            await settingsService.SaveAsync(theresaSettings with { SelectedLanguage = "Deutsch" });

            await AssertBusinessRowsOwnedByEnvironmentAsync(
                runtime.Paths.GetPaths().DatabasePath,
                runtime.Paths.GetPaths().GameEnvironmentId);

            await runtime.Service.SwitchAsync(henry.Id);
            Assert.Equal("Deutsch", (await settingsService.LoadAsync()).SelectedLanguage);
            Assert.Equal(runtime.Paths.GetPaths().DatabasePath, runtime.Paths.GetGameEnvironmentPaths(GameEnvironmentIds.Default).DatabasePath);
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    [Fact]
    public async Task CustomProvider_CanBeAddedWithoutChangingPlayerService()
    {
        var root = CreateTestRoot();
        try
        {
            var customProvider = new TestPlayerProvider();
            var runtime = CreateRuntime(root, [new LocalPlayerProvider(), customProvider]);
            await runtime.Service.InitializeAsync();

            var profile = await runtime.Service.CreateAsync(new PlayerProfileDraft(
                "Provider Test",
                Avatar: null,
                PlayerPlatformIds.Steam,
                customProvider.Id,
                GameInstallPath: null));

            Assert.Equal(customProvider.Id, profile.Provider);
            Assert.True(profile.IsCloudUser);
            Assert.Equal("test-cloud-id", profile.CloudId);
            Assert.Contains(runtime.Service.Providers, provider => provider.Id == customProvider.Id);
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    [Fact]
    public async Task StatisticsService_PreservesValuesAcrossAccountSwitches()
    {
        var root = CreateTestRoot();
        try
        {
            var runtime = CreateRuntime(root);
            await runtime.Service.InitializeAsync();
            var henry = await CreateLocalPlayerAsync(runtime.Service, "Henry", PlayerPlatformIds.Steam);
            var statistics = new PlayerStatisticsService(
                new SqliteConnectionFactory(runtime.Paths),
                runtime.Paths);
            var now = DateTimeOffset.UtcNow;

            await statistics.UpsertAsync(new PlayerStatistic(
                PlayerStatisticKeys.DaysPlayed,
                96,
                null,
                null,
                now));

            var theresa = await CreateLocalPlayerAsync(runtime.Service, "Theresa", PlayerPlatformIds.Gog);
            Assert.Equal(96, (await statistics.GetAsync(PlayerStatisticKeys.DaysPlayed))?.Value);
            await statistics.UpsertAsync(new PlayerStatistic(
                PlayerStatisticKeys.DaysPlayed,
                12,
                null,
                null,
                now.AddMinutes(1)));

            Assert.Equal(12, (await statistics.GetAsync(PlayerStatisticKeys.DaysPlayed))?.Value);
            await runtime.Service.SwitchAsync(henry.Id);
            Assert.Equal(12, (await statistics.GetAsync(PlayerStatisticKeys.DaysPlayed))?.Value);

            await AssertStatisticRowsOwnedByEnvironmentAsync(
                runtime.Paths.GetPaths().DatabasePath,
                runtime.Paths.GetPaths().GameEnvironmentId);
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    [Fact]
    public async Task StatisticsService_MigratesNewestLegacyAccountStatisticToGameEnvironment()
    {
        var root = CreateTestRoot();
        try
        {
            var runtime = CreateRuntime(root);
            await runtime.Service.InitializeAsync();
            var databasePath = runtime.Paths.GetPaths().DatabasePath;
            var older = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O");
            var newer = DateTimeOffset.UtcNow.ToString("O");

            await using (var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    CREATE TABLE player_statistics (
                        player_id TEXT NOT NULL,
                        statistic_key TEXT NOT NULL,
                        current_value INTEGER NOT NULL,
                        target_value INTEGER NULL,
                        text_value TEXT NULL,
                        updated_utc TEXT NOT NULL,
                        PRIMARY KEY (player_id, statistic_key));

                    INSERT INTO player_statistics VALUES ('first', 'days-played', 24, NULL, NULL, @Older);
                    INSERT INTO player_statistics VALUES ('second', 'days-played', 96, NULL, NULL, @Newer);
                    """;
                command.Parameters.AddWithValue("@Older", older);
                command.Parameters.AddWithValue("@Newer", newer);
                await command.ExecuteNonQueryAsync();
            }

            var statistics = new PlayerStatisticsService(
                new SqliteConnectionFactory(runtime.Paths),
                runtime.Paths);

            var migrated = await statistics.GetAsync(PlayerStatisticKeys.DaysPlayed);

            Assert.NotNull(migrated);
            Assert.Equal(96, migrated.Value);
            await AssertStatisticRowsOwnedByEnvironmentAsync(
                databasePath,
                runtime.Paths.GetPaths().GameEnvironmentId);
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    [Fact]
    public async Task DeleteCurrentPlayer_FallsBackAndArchivesProfileDirectory()
    {
        var root = CreateTestRoot();
        try
        {
            var runtime = CreateRuntime(root);
            await runtime.Service.InitializeAsync();
            var first = await CreateLocalPlayerAsync(runtime.Service, "First", PlayerPlatformIds.Steam);
            var deleting = await CreateLocalPlayerAsync(runtime.Service, "Deleting", PlayerPlatformIds.Gog);
            var deletingDirectory = runtime.Paths.GetProfilePaths(deleting.Id).RootDirectory;

            var replacement = await runtime.Service.DeleteAsync(deleting.Id);

            Assert.Equal(first.Id, replacement?.Id);
            Assert.Equal(first.Id, runtime.Service.CurrentPlayer?.Id);
            Assert.DoesNotContain(runtime.Service.Profiles, profile => profile.Id == deleting.Id);
            Assert.False(Directory.Exists(deletingDirectory));
            Assert.NotEmpty(Directory.GetDirectories(runtime.Paths.GetGlobalPaths().DeletedProfilesDirectory));
        }
        finally
        {
            DeleteTestRoot(root);
        }
    }

    private static TestRuntime CreateRuntime(string root, IReadOnlyList<IPlayerProvider>? providers = null)
    {
        var logger = new LoggerConfiguration().CreateLogger();
        var context = new PlayerContext();
        var paths = new ApplicationPathService(context, root);
        var connectionFactory = new ProfileConnectionFactory(paths);
        var repository = new SqliteProfileRepository(connectionFactory);
        var service = new PlayerService(
            repository,
            providers ?? [new LocalPlayerProvider()],
            context,
            paths,
            logger);
        return new TestRuntime(paths, repository, service, context, logger);
    }

    private static Task<PlayerProfile> CreateLocalPlayerAsync(
        IPlayerService service,
        string displayName,
        string platform) =>
        service.CreateAsync(new PlayerProfileDraft(
            displayName,
            Avatar: null,
            platform,
            PlayerProviderIds.Local,
            GameInstallPath: null));

    private static async Task AssertBusinessRowsOwnedByEnvironmentAsync(string databasePath, Guid environmentId)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync();
        await using var columns = connection.CreateCommand();
        columns.CommandText = "SELECT COUNT(*) FROM pragma_table_info('app_settings') WHERE name = 'environment_id';";
        Assert.Equal(1L, (long)(await columns.ExecuteScalarAsync())!);

        await using var owners = connection.CreateCommand();
        owners.CommandText = "SELECT DISTINCT environment_id FROM app_settings;";
        var owner = (string?)await owners.ExecuteScalarAsync();
        Assert.Equal(environmentId.ToString("D"), owner);
    }

    private static async Task AssertStatisticRowsOwnedByEnvironmentAsync(string databasePath, Guid environmentId)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync();

        await using var owners = connection.CreateCommand();
        owners.CommandText = "SELECT DISTINCT environment_id FROM game_statistics;";
        var owner = (string?)await owners.ExecuteScalarAsync();

        Assert.Equal(environmentId.ToString("D"), owner);
    }

    private static string CreateTestRoot() =>
        Path.Combine(Path.GetTempPath(), "BohemiX.PlayerProfiles.Tests", Guid.NewGuid().ToString("N"));

    private static void DeleteTestRoot(string root)
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed record TestRuntime(
        ApplicationPathService Paths,
        SqliteProfileRepository Repository,
        PlayerService Service,
        PlayerContext Context,
        ILogger Logger);

    private sealed class TestPlayerProvider : IPlayerProvider
    {
        public string Id => "test-cloud";

        public string DisplayName => "Test Cloud";

        public bool RequiresNetwork => true;

        public Task<PlayerProfile> CreateAsync(PlayerProfileDraft draft, CancellationToken cancellationToken = default)
        {
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new PlayerProfile(
                Guid.NewGuid(),
                draft.DisplayName,
                draft.Avatar,
                draft.Platform,
                Id,
                draft.GameInstallPath,
                now,
                now,
                CloudId: "test-cloud-id",
                Email: null,
                AccessToken: null,
                RefreshToken: null,
                IsCloudUser: true,
                SyncTime: null,
                Version: 1));
        }

        public Task<PlayerProfile> UpdateAsync(
            PlayerProfile current,
            PlayerProfileUpdate update,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(current with
            {
                DisplayName = update.DisplayName,
                Avatar = update.Avatar,
                Platform = update.Platform,
                GameInstallPath = update.GameInstallPath
            });
    }
}

#pragma warning restore CS0618
