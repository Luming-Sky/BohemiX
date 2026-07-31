using System.Globalization;
using BohemiX.Core.PlayerProfiles;
using BohemiX.Infrastructure.Persistence;
using Dapper;
using Microsoft.Data.Sqlite;

namespace BohemiX.Infrastructure.PlayerProfiles;

public sealed class SqliteSteamAccountBindingService : IPlayerSteamAccountBindingService
{
    private readonly ProfileConnectionFactory connectionFactory;
    private readonly SemaphoreSlim initializationLock = new(1, 1);
    private bool initialized;

    public SqliteSteamAccountBindingService(ProfileConnectionFactory connectionFactory)
    {
        this.connectionFactory = connectionFactory;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (initialized)
        {
            return;
        }

        await initializationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (initialized)
            {
                return;
            }

            await using var connection = connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await connection.ExecuteAsync(new CommandDefinition(
                """
                PRAGMA foreign_keys = ON;

                CREATE TABLE IF NOT EXISTS PlayerSteamAccountBindings (
                    PlayerId TEXT NOT NULL PRIMARY KEY,
                    SteamId TEXT NOT NULL UNIQUE,
                    PersonaName TEXT NOT NULL,
                    BoundAt TEXT NOT NULL,
                    LastValidatedAt TEXT NOT NULL,
                    FOREIGN KEY (PlayerId) REFERENCES PlayerProfiles(Id) ON DELETE CASCADE
                );

                CREATE INDEX IF NOT EXISTS IX_PlayerSteamAccountBindings_SteamId
                    ON PlayerSteamAccountBindings(SteamId);
                """,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            initialized = true;
        }
        finally
        {
            initializationLock.Release();
        }
    }

    public async Task<SteamAccountBinding?> GetBindingByPlayerIdAsync(
        Guid playerId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = connectionFactory.CreateConnection();
        var row = await connection.QuerySingleOrDefaultAsync<SteamAccountBindingRow>(new CommandDefinition(
            SelectSql + " WHERE PlayerId = @PlayerId;",
            new { PlayerId = playerId.ToString("D") },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return row is null ? null : ToBinding(row);
    }

    public async Task<SteamAccountBinding?> GetBindingBySteamIdAsync(
        ulong steamId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = connectionFactory.CreateConnection();
        var row = await connection.QuerySingleOrDefaultAsync<SteamAccountBindingRow>(new CommandDefinition(
            SelectSql + " WHERE SteamId = @SteamId;",
            new { SteamId = steamId.ToString(CultureInfo.InvariantCulture) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return row is null ? null : ToBinding(row);
    }

    public async Task<SteamAccountBinding> BindAsync(
        Guid playerId,
        SteamAccountIdentity identity,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        var currentBinding = await GetBindingByPlayerIdAsync(playerId, cancellationToken).ConfigureAwait(false);
        if (currentBinding is not null && currentBinding.SteamId != identity.SteamId)
        {
            throw new SteamAccountBindingException(
                $"Player {playerId:D} is already bound to Steam account {currentBinding.SteamId}.",
                SteamAccountBindingFailureKind.PlayerAlreadyBoundToDifferentSteamAccount);
        }

        var steamBinding = await GetBindingBySteamIdAsync(identity.SteamId, cancellationToken).ConfigureAwait(false);
        if (steamBinding is not null && steamBinding.PlayerId != playerId)
        {
            throw new SteamAccountBindingException(
                $"Steam account {identity.SteamId} is already bound to player {steamBinding.PlayerId:D}.",
                SteamAccountBindingFailureKind.SteamAccountAlreadyBoundToDifferentPlayer);
        }

        var binding = new SteamAccountBinding(
            playerId,
            identity.SteamId,
            identity.PersonaName,
            currentBinding?.BoundAt ?? now,
            now);

        await using var connection = connectionFactory.CreateConnection();
        try
        {
            var affectedRows = await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO PlayerSteamAccountBindings (
                    PlayerId,
                    SteamId,
                    PersonaName,
                    BoundAt,
                    LastValidatedAt)
                VALUES (
                    @PlayerId,
                    @SteamId,
                    @PersonaName,
                    @BoundAt,
                    @LastValidatedAt)
                ON CONFLICT(PlayerId) DO UPDATE SET
                    SteamId = excluded.SteamId,
                    PersonaName = excluded.PersonaName,
                    BoundAt = excluded.BoundAt,
                    LastValidatedAt = excluded.LastValidatedAt
                WHERE PlayerSteamAccountBindings.SteamId = excluded.SteamId;
                """,
                new
                {
                    PlayerId = binding.PlayerId.ToString("D"),
                    SteamId = binding.SteamId.ToString(CultureInfo.InvariantCulture),
                    binding.PersonaName,
                    BoundAt = FormatDate(binding.BoundAt),
                    LastValidatedAt = FormatDate(binding.LastValidatedAt)
                },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            if (affectedRows == 0)
            {
                throw new SteamAccountBindingException(
                    $"Player {playerId:D} is already bound to a different Steam account.",
                    SteamAccountBindingFailureKind.PlayerAlreadyBoundToDifferentSteamAccount);
            }
        }
        catch (SqliteException ex) when (ex.SqliteExtendedErrorCode == 2067)
        {
            throw new SteamAccountBindingException(
                $"Steam account {identity.SteamId} is already bound to a different player.",
                SteamAccountBindingFailureKind.SteamAccountAlreadyBoundToDifferentPlayer);
        }

        return binding;
    }

    public async Task UnbindAsync(Guid playerId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = connectionFactory.CreateConnection();
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM PlayerSteamAccountBindings WHERE PlayerId = @PlayerId;",
            new { PlayerId = playerId.ToString("D") },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private static SteamAccountBinding ToBinding(SteamAccountBindingRow row) => new(
        Guid.Parse(row.PlayerId),
        ulong.Parse(row.SteamId, CultureInfo.InvariantCulture),
        row.PersonaName,
        ParseDate(row.BoundAt) ?? DateTimeOffset.UtcNow,
        ParseDate(row.LastValidatedAt) ?? DateTimeOffset.UtcNow);

    private static string FormatDate(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset? ParseDate(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;

    private const string SelectSql = """
        SELECT PlayerId, SteamId, PersonaName, BoundAt, LastValidatedAt
        FROM PlayerSteamAccountBindings
        """;

    private sealed class SteamAccountBindingRow
    {
        public string PlayerId { get; init; } = string.Empty;
        public string SteamId { get; init; } = string.Empty;
        public string PersonaName { get; init; } = string.Empty;
        public string BoundAt { get; init; } = string.Empty;
        public string LastValidatedAt { get; init; } = string.Empty;
    }
}
