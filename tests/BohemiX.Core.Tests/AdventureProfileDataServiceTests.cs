using BohemiX.Core.Models;
using BohemiX.Infrastructure.Persistence;
using BohemiX.Infrastructure.Services;
using BohemiX.Infrastructure.Services.Saves;
using Microsoft.Data.Sqlite;

namespace BohemiX.Core.Tests;

public sealed class AdventureProfileDataServiceTests
{
    [Fact]
    public async Task LoadAsync_ProjectsExistingGameEnvironmentData()
    {
        var root = Path.Combine(Path.GetTempPath(), "BohemiX.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new ApplicationPathService(root);
            var databasePath = paths.GetPaths().DatabasePath;
            Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
            var environmentId = paths.GetPaths().GameEnvironmentId.ToString("D");

            await using (var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    CREATE TABLE SaveSlots (
                        Id TEXT PRIMARY KEY,
                        PhysicalName TEXT NOT NULL,
                        PlayTimeSeconds INTEGER,
                        LastSavedAtUtc TEXT,
                        UpdatedAtUtc TEXT NOT NULL,
                        LastActivatedAtUtc TEXT,
                        EnvironmentId TEXT NOT NULL);
                    CREATE TABLE SaveSnapshots (
                        Id TEXT PRIMARY KEY,
                        ProfileId TEXT NOT NULL,
                        LastSavedAtUtc TEXT,
                        CreatedAtUtc TEXT NOT NULL);
                    CREATE TABLE SaveBackupNodes (
                        Id TEXT PRIMARY KEY,
                        ProfileId TEXT NOT NULL,
                        LastSavedAtUtc TEXT,
                        FirstSeenAtUtc TEXT NOT NULL);
                    CREATE TABLE mod_manifests (
                        id TEXT PRIMARY KEY,
                        is_enabled INTEGER NOT NULL,
                        environment_id TEXT NOT NULL);
                    CREATE TABLE tracker_events (
                        event_id TEXT PRIMARY KEY,
                        entity_id TEXT NOT NULL,
                        entity_name TEXT NOT NULL,
                        occurred_utc TEXT NOT NULL,
                        environment_id TEXT NOT NULL);
                    CREATE TABLE game_installations (
                        install_path TEXT PRIMARY KEY,
                        executable_path TEXT NOT NULL,
                        source INTEGER NOT NULL,
                        is_verified INTEGER NOT NULL,
                        environment_id TEXT NOT NULL);

                    INSERT INTO SaveSlots VALUES (
                        '11111111-1111-1111-1111-111111111111', 'Profile_1', 178004, '2026-07-22T05:54:10Z', '2026-07-23T14:36:39Z', NULL, @EnvironmentId);
                    INSERT INTO SaveSlots VALUES (
                        '22222222-2222-2222-2222-222222222222', 'Profile_2', 7200, '2026-07-24T05:54:10Z', '2026-07-24T14:36:39Z', NULL, @EnvironmentId);
                    INSERT INTO SaveBackupNodes VALUES (
                        'node-1', '11111111-1111-1111-1111-111111111111', '2026-05-31T06:00:11Z', '2026-05-31T06:00:11Z');
                    INSERT INTO SaveBackupNodes VALUES (
                        'node-2', '11111111-1111-1111-1111-111111111111', '2026-06-04T06:00:11Z', '2026-06-04T06:00:11Z');
                    INSERT INTO mod_manifests VALUES ('mod-1', 1, @EnvironmentId);
                    INSERT INTO mod_manifests VALUES ('mod-2', 1, @EnvironmentId);
                    INSERT INTO mod_manifests VALUES ('mod-3', 0, @EnvironmentId);
                    INSERT INTO tracker_events VALUES (
                        'event-1', 'player', 'Henry', '2026-07-22T05:52:41Z', @EnvironmentId);
                    INSERT INTO game_installations VALUES (
                        'D:\KingdomCome2',
                        'D:\KingdomCome2\Bin\Win64MasterMasterSteamPGO\KingdomCome.exe',
                        0,
                        1,
                        @EnvironmentId);
                    """;
                command.Parameters.AddWithValue("@EnvironmentId", environmentId);
                await command.ExecuteNonQueryAsync();
            }

            var service = new AdventureProfileDataService(
                new SqliteConnectionFactory(paths),
                paths,
                new KcdAdventureSaveReader());
            var result = await service.LoadAsync(Guid.Parse("11111111-1111-1111-1111-111111111111"));

            Assert.Equal(string.Empty, result.PlayerName);
            Assert.Equal("Steam Edition", result.Platform);
            Assert.Equal(49, result.HoursPlayed);
            Assert.Null(result.InGameDay);
            Assert.Equal(2, result.SaveSlots);
            Assert.Equal(2, result.InstalledMods);

            var secondSave = await service.LoadAsync(Guid.Parse("22222222-2222-2222-2222-222222222222"));

            Assert.Equal(2, secondSave.HoursPlayed);
            Assert.Null(secondSave.InGameDay);
            Assert.Equal(2, secondSave.SaveSlots);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
