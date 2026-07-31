using BohemiX.Core.Models;
using BohemiX.Core.Services;
using BohemiX.Infrastructure.Persistence;
using Dapper;
using Serilog;

namespace BohemiX.Infrastructure.Services;

public sealed class GameInstallationStore : IGameInstallationStore
{
    private readonly SqliteConnectionFactory connectionFactory;
    private readonly ILogger logger;

    public GameInstallationStore(SqliteConnectionFactory connectionFactory, ILogger logger)
    {
        this.connectionFactory = connectionFactory;
        this.logger = logger.ForContext<GameInstallationStore>();
    }

    public async Task<IReadOnlyList<DiscoveredGame>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.CreateConnection();
        var rows = await connection.QueryAsync<GameInstallationRow>(new CommandDefinition(
            """
            SELECT
                name AS Name,
                install_path AS InstallPath,
                executable_path AS ExecutablePath,
                source AS Source,
                is_verified AS IsVerified
            FROM game_installations
            ORDER BY source, install_path
            """,
            cancellationToken: cancellationToken));

        return rows
            .Select(row => new DiscoveredGame(
                row.Name,
                row.InstallPath,
                row.ExecutablePath,
                (GameInstallSource)row.Source,
                row.IsVerified != 0))
            .ToArray();
    }

    public async Task SaveAsync(IReadOnlyList<DiscoveredGame> games, CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM game_installations",
            transaction: transaction,
            cancellationToken: cancellationToken));

        foreach (var game in games)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO game_installations (
                    install_path,
                    name,
                    executable_path,
                    source,
                    is_verified,
                    last_seen_utc
                )
                VALUES (
                    @InstallPath,
                    @Name,
                    @ExecutablePath,
                    @Source,
                    @IsVerified,
                    @LastSeenUtc
                )
                """,
                new
                {
                    game.InstallPath,
                    game.Name,
                    game.ExecutablePath,
                    Source = (int)game.Source,
                    IsVerified = game.IsVerified ? 1 : 0,
                    LastSeenUtc = DateTimeOffset.UtcNow.ToString("O")
                },
                transaction,
                cancellationToken: cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);
        logger.Information("Saved {GameCount} discovered game installations", games.Count);
    }

    private sealed class GameInstallationRow
    {
        public string Name { get; init; } = string.Empty;

        public string InstallPath { get; init; } = string.Empty;

        public string ExecutablePath { get; init; } = string.Empty;

        public int Source { get; init; }

        public int IsVerified { get; init; }
    }
}

