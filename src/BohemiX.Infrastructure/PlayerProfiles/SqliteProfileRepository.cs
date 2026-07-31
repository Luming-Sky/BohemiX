using System.Globalization;
using BohemiX.Core.PlayerProfiles;
using BohemiX.Infrastructure.Persistence;
using Dapper;

namespace BohemiX.Infrastructure.PlayerProfiles;

public sealed class SqliteProfileRepository : IProfileRepository
{
    private readonly ProfileConnectionFactory connectionFactory;

    public SqliteProfileRepository(ProfileConnectionFactory connectionFactory)
    {
        this.connectionFactory = connectionFactory;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            PRAGMA journal_mode = WAL;
            PRAGMA foreign_keys = ON;

            CREATE TABLE IF NOT EXISTS PlayerProfiles (
                Id TEXT NOT NULL PRIMARY KEY,
                DisplayName TEXT NOT NULL,
                Avatar TEXT NULL,
                Platform TEXT NOT NULL,
                Provider TEXT NOT NULL,
                GameInstallPath TEXT NULL,
                Bio TEXT NULL,
                CreatedTime TEXT NOT NULL,
                LastLoginTime TEXT NOT NULL,
                CloudId TEXT NULL,
                Email TEXT NULL,
                AccessToken TEXT NULL,
                RefreshToken TEXT NULL,
                IsCloudUser INTEGER NOT NULL DEFAULT 0,
                SyncTime TEXT NULL,
                Version INTEGER NOT NULL DEFAULT 1
            );

            CREATE INDEX IF NOT EXISTS IX_PlayerProfiles_Provider
                ON PlayerProfiles(Provider);

            CREATE INDEX IF NOT EXISTS IX_PlayerProfiles_LastLoginTime
                ON PlayerProfiles(LastLoginTime DESC);

            CREATE TABLE IF NOT EXISTS PlayerProfileState (
                StateKey TEXT NOT NULL PRIMARY KEY,
                CurrentPlayerId TEXT NULL,
                UpdatedTime TEXT NOT NULL,
                FOREIGN KEY (CurrentPlayerId) REFERENCES PlayerProfiles(Id) ON DELETE SET NULL
            );
            """,
            cancellationToken: cancellationToken));
        await EnsureColumnAsync(connection, "PlayerProfiles", "Bio", "TEXT NULL", cancellationToken);
    }

    public async Task<IReadOnlyList<PlayerProfile>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.CreateConnection();
        var rows = await connection.QueryAsync<PlayerProfileRow>(new CommandDefinition(
            SelectSql + " ORDER BY LastLoginTime DESC, DisplayName COLLATE NOCASE;",
            cancellationToken: cancellationToken));
        return rows.Select(ToProfile).ToArray();
    }

    public async Task<PlayerProfile?> GetByIdAsync(Guid playerId, CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.CreateConnection();
        var row = await connection.QuerySingleOrDefaultAsync<PlayerProfileRow>(new CommandDefinition(
            SelectSql + " WHERE Id = @Id;",
            new { Id = playerId.ToString("D") },
            cancellationToken: cancellationToken));
        return row is null ? null : ToProfile(row);
    }

    public async Task<Guid?> GetCurrentPlayerIdAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.CreateConnection();
        var value = await connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            "SELECT CurrentPlayerId FROM PlayerProfileState WHERE StateKey = 'current';",
            cancellationToken: cancellationToken));
        return Guid.TryParse(value, out var playerId) ? playerId : null;
    }

    public async Task UpsertAsync(PlayerProfile profile, CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.CreateConnection();
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO PlayerProfiles (
                Id, DisplayName, Avatar, Platform, Provider, GameInstallPath, Bio,
                CreatedTime, LastLoginTime, CloudId, Email, AccessToken, RefreshToken,
                IsCloudUser, SyncTime, Version)
            VALUES (
                @Id, @DisplayName, @Avatar, @LegacyPlatform, @Provider, @LegacyGameInstallPath, @Bio,
                @CreatedTime, @LastLoginTime, @CloudId, @Email, @AccessToken, @RefreshToken,
                @IsCloudUser, @SyncTime, @Version)
            ON CONFLICT(Id) DO UPDATE SET
                DisplayName = excluded.DisplayName,
                Avatar = excluded.Avatar,
                Platform = excluded.Platform,
                Provider = excluded.Provider,
                GameInstallPath = excluded.GameInstallPath,
                Bio = excluded.Bio,
                CreatedTime = excluded.CreatedTime,
                LastLoginTime = excluded.LastLoginTime,
                CloudId = excluded.CloudId,
                Email = excluded.Email,
                AccessToken = excluded.AccessToken,
                RefreshToken = excluded.RefreshToken,
                IsCloudUser = excluded.IsCloudUser,
                SyncTime = excluded.SyncTime,
                Version = excluded.Version;
            """,
            ToParameters(profile),
            cancellationToken: cancellationToken));
    }

    public async Task SetCurrentPlayerIdAsync(Guid? playerId, CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.CreateConnection();
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO PlayerProfileState (StateKey, CurrentPlayerId, UpdatedTime)
            VALUES ('current', @CurrentPlayerId, @UpdatedTime)
            ON CONFLICT(StateKey) DO UPDATE SET
                CurrentPlayerId = excluded.CurrentPlayerId,
                UpdatedTime = excluded.UpdatedTime;
            """,
            new
            {
                CurrentPlayerId = playerId?.ToString("D"),
                UpdatedTime = FormatDate(DateTimeOffset.UtcNow)
            },
            cancellationToken: cancellationToken));
    }

    public async Task DeleteAsync(Guid playerId, CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE PlayerProfileState SET CurrentPlayerId = NULL, UpdatedTime = @UpdatedTime WHERE CurrentPlayerId = @Id;",
            new { Id = playerId.ToString("D"), UpdatedTime = FormatDate(DateTimeOffset.UtcNow) },
            transaction,
            cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM PlayerProfiles WHERE Id = @Id;",
            new { Id = playerId.ToString("D") },
            transaction,
            cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
    }

    private static object ToParameters(PlayerProfile profile)
    {
        var environment = LegacyPlayerProfileCompatibility.From(profile);
        return new
        {
            Id = profile.Id.ToString("D"),
            profile.DisplayName,
            profile.Avatar,
            LegacyPlatform = environment.Platform,
            profile.Provider,
            LegacyGameInstallPath = environment.GameInstallPath,
            profile.Bio,
            CreatedTime = FormatDate(profile.CreatedTime),
            LastLoginTime = FormatDate(profile.LastLoginTime),
            profile.CloudId,
            profile.Email,
            profile.AccessToken,
            profile.RefreshToken,
            IsCloudUser = profile.IsCloudUser ? 1 : 0,
            SyncTime = FormatDate(profile.SyncTime),
            profile.Version
        };
    }

    private static PlayerProfile ToProfile(PlayerProfileRow row) => LegacyPlayerProfileCompatibility.Apply(new PlayerProfile(
        Guid.Parse(row.Id),
        row.DisplayName,
        row.Avatar,
        row.Provider,
        row.Bio,
        ParseDate(row.CreatedTime) ?? DateTimeOffset.UtcNow,
        ParseDate(row.LastLoginTime) ?? DateTimeOffset.UtcNow,
        row.CloudId,
        row.Email,
        row.AccessToken,
        row.RefreshToken,
        row.IsCloudUser != 0,
        ParseDate(row.SyncTime),
        Math.Max(1, row.Version)), new LegacyPlayerProfileCompatibility.EnvironmentFields(
            row.Platform,
            row.GameInstallPath));

    private static string FormatDate(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    private static string? FormatDate(DateTimeOffset? value) =>
        value?.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset? ParseDate(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;

    private const string SelectSql = """
        SELECT Id, DisplayName, Avatar, Platform, Provider, GameInstallPath, Bio,
               CreatedTime, LastLoginTime, CloudId, Email, AccessToken, RefreshToken,
               IsCloudUser, SyncTime, Version
        FROM PlayerProfiles
        """;

    private sealed class PlayerProfileRow
    {
        public string Id { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public string? Avatar { get; init; }
        public string Platform { get; init; } = "unbound";
        public string Provider { get; init; } = string.Empty;
        public string? GameInstallPath { get; init; }
        public string? Bio { get; init; }
        public string CreatedTime { get; init; } = string.Empty;
        public string LastLoginTime { get; init; } = string.Empty;
        public string? CloudId { get; init; }
        public string? Email { get; init; }
        public string? AccessToken { get; init; }
        public string? RefreshToken { get; init; }
        public int IsCloudUser { get; init; }
        public string? SyncTime { get; init; }
        public int Version { get; init; }
    }

    private static async Task EnsureColumnAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        string table,
        string column,
        string definition,
        CancellationToken cancellationToken)
    {
        var columns = await connection.QueryAsync<TableColumn>(new CommandDefinition(
            $"SELECT name AS Name FROM pragma_table_info('{table}')",
            cancellationToken: cancellationToken));
        if (columns.Any(item => string.Equals(item.Name, column, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            $"ALTER TABLE {table} ADD COLUMN {column} {definition}",
            cancellationToken: cancellationToken));
    }

    private sealed class TableColumn
    {
        public string Name { get; init; } = string.Empty;
    }
}
