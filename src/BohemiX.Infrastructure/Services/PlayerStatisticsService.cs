using BohemiX.Core.Models;
using BohemiX.Core.PlayerProfiles;
using BohemiX.Core.Services;
using BohemiX.Infrastructure.Persistence;
using Dapper;

namespace BohemiX.Infrastructure.Services;

public sealed class PlayerStatisticsService : IPlayerStatisticsService
{
    private readonly SqliteConnectionFactory connectionFactory;
    private readonly IApplicationPathService applicationPathService;
    private readonly IPlayerContext? legacyPlayerContext;

    public PlayerStatisticsService(
        SqliteConnectionFactory connectionFactory,
        IApplicationPathService applicationPathService)
    {
        this.connectionFactory = connectionFactory;
        this.applicationPathService = applicationPathService;
    }

    [Obsolete("Statistics are scoped to game environments, not player profiles.")]
    public PlayerStatisticsService(
        SqliteConnectionFactory connectionFactory,
        IPlayerContext legacyPlayerContext)
    {
        this.connectionFactory = connectionFactory;
        applicationPathService = new ApplicationPathService();
        this.legacyPlayerContext = legacyPlayerContext;
    }

    public async Task<IReadOnlyList<PlayerStatistic>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        var environmentId = GetCurrentEnvironmentId();
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);

        var rows = await connection.QueryAsync<StatisticRow>(new CommandDefinition(
            """
            SELECT statistic_key AS Key,
                   current_value AS Value,
                   target_value AS Total,
                   text_value AS TextValue,
                   updated_utc AS UpdatedTime
            FROM game_statistics
            WHERE environment_id = @EnvironmentId
            ORDER BY statistic_key;
            """,
            new { EnvironmentId = environmentId },
            cancellationToken: cancellationToken));

        return rows.Select(ToModel).ToArray();
    }

    public async Task<PlayerStatistic?> GetAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var environmentId = GetCurrentEnvironmentId();
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);

        var row = await connection.QuerySingleOrDefaultAsync<StatisticRow>(new CommandDefinition(
            """
            SELECT statistic_key AS Key,
                   current_value AS Value,
                   target_value AS Total,
                   text_value AS TextValue,
                   updated_utc AS UpdatedTime
            FROM game_statistics
            WHERE environment_id = @EnvironmentId AND statistic_key = @Key;
            """,
            new { EnvironmentId = environmentId, Key = key.Trim() },
            cancellationToken: cancellationToken));

        return row is null ? null : ToModel(row);
    }

    public async Task UpsertAsync(
        PlayerStatistic statistic,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(statistic);
        ArgumentException.ThrowIfNullOrWhiteSpace(statistic.Key);
        var environmentId = GetCurrentEnvironmentId();
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO game_statistics (
                environment_id,
                statistic_key,
                current_value,
                target_value,
                text_value,
                updated_utc)
            VALUES (
                @EnvironmentId,
                @Key,
                @Value,
                @Total,
                @TextValue,
                @UpdatedTime)
            ON CONFLICT(environment_id, statistic_key) DO UPDATE SET
                current_value = excluded.current_value,
                target_value = excluded.target_value,
                text_value = excluded.text_value,
                updated_utc = excluded.updated_utc;
            """,
            new
            {
                EnvironmentId = environmentId,
                Key = statistic.Key.Trim(),
                statistic.Value,
                statistic.Total,
                statistic.TextValue,
                UpdatedTime = statistic.UpdatedTime.ToUniversalTime().ToString("O")
            },
            cancellationToken: cancellationToken));
    }

    private string GetCurrentEnvironmentId() =>
        legacyPlayerContext?.CurrentPlayer?.Id.ToString("D")
        ?? applicationPathService.GetPaths().GameEnvironmentId.ToString("D");

    private async Task EnsureSchemaAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            """
            CREATE TABLE IF NOT EXISTS game_statistics (
                environment_id TEXT NOT NULL,
                statistic_key TEXT NOT NULL,
                current_value INTEGER NOT NULL DEFAULT 0,
                target_value INTEGER NULL,
                text_value TEXT NULL,
                updated_utc TEXT NOT NULL,
                PRIMARY KEY (environment_id, statistic_key)
            );

            CREATE TABLE IF NOT EXISTS player_statistics (
                player_id TEXT NOT NULL,
                statistic_key TEXT NOT NULL,
                current_value INTEGER NOT NULL DEFAULT 0,
                target_value INTEGER NULL,
                text_value TEXT NULL,
                updated_utc TEXT NOT NULL,
                PRIMARY KEY (player_id, statistic_key)
            );
            """,
            cancellationToken: cancellationToken));

        // Accounts no longer own game data. Preserve the newest legacy value for
        // each key in the default game environment without replacing new data.
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO game_statistics (
                environment_id,
                statistic_key,
                current_value,
                target_value,
                text_value,
                updated_utc)
            SELECT @EnvironmentId,
                   latest.statistic_key,
                   latest.current_value,
                   latest.target_value,
                   latest.text_value,
                   latest.updated_utc
            FROM (
                SELECT player_id,
                       statistic_key,
                       current_value,
                       target_value,
                       text_value,
                       updated_utc,
                       ROW_NUMBER() OVER (
                           PARTITION BY statistic_key
                           ORDER BY updated_utc DESC, player_id DESC) AS row_number
                FROM player_statistics
            ) AS latest
            WHERE latest.row_number = 1
              AND NOT EXISTS (
                  SELECT 1
                  FROM game_statistics AS current
                  WHERE current.environment_id = @EnvironmentId
                    AND current.statistic_key = latest.statistic_key);
            """,
            new { EnvironmentId = GetCurrentEnvironmentId() },
            cancellationToken: cancellationToken));
    }

    private static PlayerStatistic ToModel(StatisticRow row) =>
        new(
            row.Key,
            row.Value,
            row.Total,
            row.TextValue,
            DateTimeOffset.Parse(row.UpdatedTime));

    private sealed class StatisticRow
    {
        public string Key { get; init; } = string.Empty;

        public long Value { get; init; }

        public long? Total { get; init; }

        public string? TextValue { get; init; }

        public string UpdatedTime { get; init; } = string.Empty;
    }
}
