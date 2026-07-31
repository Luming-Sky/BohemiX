using BohemiX.Core.Models;
using BohemiX.Core.Services;
using BohemiX.Infrastructure.Persistence;
using Dapper;
using Serilog;

namespace BohemiX.Infrastructure.Services;

public sealed class GameLaunchSessionService : IGameLaunchSessionService
{
    private readonly SqliteConnectionFactory connectionFactory;
    private readonly IApplicationPathService applicationPathService;
    private readonly ILogger logger;

    public GameLaunchSessionService(
        SqliteConnectionFactory connectionFactory,
        IApplicationPathService applicationPathService,
        ILogger logger)
    {
        this.connectionFactory = connectionFactory;
        this.applicationPathService = applicationPathService;
        this.logger = logger.ForContext<GameLaunchSessionService>();
    }

    public async Task<GameLaunchSession> BeginAsync(
        DiscoveredGame game,
        GameLaunchOptions options,
        Guid? vfsSessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(options);
        var session = new GameLaunchSession(
            Guid.NewGuid(),
            applicationPathService.GetPaths().GameEnvironmentId,
            game.Name,
            game.ExecutablePath,
            string.IsNullOrWhiteSpace(options.Arguments) ? null : options.Arguments,
            options.UseSteamProtocol,
            vfsSessionId,
            null,
            GameLaunchSessionStatus.Requested,
            DateTimeOffset.UtcNow,
            null,
            null,
            null,
            null);

        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO game_launch_sessions (
                id, environment_id, game_name, executable_path, launch_arguments,
                used_steam_protocol, vfs_session_id, process_id, status,
                requested_utc, started_utc, exited_utc, exit_code, failure_message)
            VALUES (
                @Id, @EnvironmentId, @GameName, @ExecutablePath, @LaunchArguments,
                @UsedSteamProtocol, @VfsSessionId, NULL, @Status,
                @RequestedUtc, NULL, NULL, NULL, NULL);
            """,
            ToParameters(session),
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return session;
    }

    public Task MarkRunningAsync(Guid sessionId, int processId, CancellationToken cancellationToken = default) =>
        UpdateAsync(
            """
            UPDATE game_launch_sessions
            SET process_id = @ProcessId,
                status = @Status,
                started_utc = @Now,
                failure_message = NULL
            WHERE id = @Id;
            """,
            new
            {
                Id = sessionId.ToString("D"),
                ProcessId = processId,
                Status = (int)GameLaunchSessionStatus.Running,
                Now = DateTimeOffset.UtcNow.ToString("O")
            },
            cancellationToken);

    public Task MarkFailedAsync(Guid sessionId, string message, CancellationToken cancellationToken = default) =>
        UpdateAsync(
            """
            UPDATE game_launch_sessions
            SET status = @Status,
                exited_utc = @Now,
                failure_message = @Message
            WHERE id = @Id;
            """,
            new
            {
                Id = sessionId.ToString("D"),
                Status = (int)GameLaunchSessionStatus.Failed,
                Now = DateTimeOffset.UtcNow.ToString("O"),
                Message = message
            },
            cancellationToken);

    public Task MarkExitedByProcessIdAsync(
        int processId,
        int? exitCode,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(
            """
            UPDATE game_launch_sessions
            SET status = @Status,
                exited_utc = @Now,
                exit_code = @ExitCode
            WHERE process_id = @ProcessId AND status = @RunningStatus;
            """,
            new
            {
                ProcessId = processId,
                Status = (int)GameLaunchSessionStatus.Exited,
                RunningStatus = (int)GameLaunchSessionStatus.Running,
                Now = DateTimeOffset.UtcNow.ToString("O"),
                ExitCode = exitCode
            },
            cancellationToken);

    public Task MarkMonitoringLostAsync(
        int processId,
        string message,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(
            """
            UPDATE game_launch_sessions
            SET status = @Status,
                exited_utc = @Now,
                failure_message = @Message
            WHERE process_id = @ProcessId AND status = @RunningStatus;
            """,
            new
            {
                ProcessId = processId,
                Status = (int)GameLaunchSessionStatus.MonitoringLost,
                RunningStatus = (int)GameLaunchSessionStatus.Running,
                Now = DateTimeOffset.UtcNow.ToString("O"),
                Message = message
            },
            cancellationToken);

    public async Task<IReadOnlyList<GameLaunchSession>> GetRecentAsync(
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 500);
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<LaunchSessionRow>(new CommandDefinition(
            """
            SELECT id AS Id,
                   environment_id AS EnvironmentId,
                   game_name AS GameName,
                   executable_path AS ExecutablePath,
                   launch_arguments AS LaunchArguments,
                   used_steam_protocol AS UsedSteamProtocol,
                   vfs_session_id AS VfsSessionId,
                   process_id AS ProcessId,
                   status AS Status,
                   requested_utc AS RequestedUtc,
                   started_utc AS StartedUtc,
                   exited_utc AS ExitedUtc,
                   exit_code AS ExitCode,
                   failure_message AS FailureMessage
            FROM game_launch_sessions
            WHERE environment_id = @EnvironmentId
            ORDER BY requested_utc DESC
            LIMIT @Limit;
            """,
            new
            {
                EnvironmentId = applicationPathService.GetPaths().GameEnvironmentId.ToString("D"),
                Limit = limit
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.Select(ToModel).ToArray();
    }

    private async Task UpdateAsync(string sql, object parameters, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            sql,
            parameters,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (affected == 0)
        {
            logger.Warning("No game launch session matched a lifecycle update");
        }
    }

    internal static async Task EnsureSchemaAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            """
            CREATE TABLE IF NOT EXISTS game_launch_sessions (
                id TEXT NOT NULL PRIMARY KEY,
                environment_id TEXT NOT NULL,
                game_name TEXT NOT NULL,
                executable_path TEXT NOT NULL,
                launch_arguments TEXT NULL,
                used_steam_protocol INTEGER NOT NULL,
                vfs_session_id TEXT NULL,
                process_id INTEGER NULL,
                status INTEGER NOT NULL,
                requested_utc TEXT NOT NULL,
                started_utc TEXT NULL,
                exited_utc TEXT NULL,
                exit_code INTEGER NULL,
                failure_message TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_game_launch_sessions_environment_requested
                ON game_launch_sessions(environment_id, requested_utc DESC);

            CREATE INDEX IF NOT EXISTS ix_game_launch_sessions_process_status
                ON game_launch_sessions(process_id, status);
            """,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private static object ToParameters(GameLaunchSession session) => new
    {
        Id = session.Id.ToString("D"),
        EnvironmentId = session.GameEnvironmentId.ToString("D"),
        session.GameName,
        session.ExecutablePath,
        session.LaunchArguments,
        UsedSteamProtocol = session.UsedSteamProtocol ? 1 : 0,
        VfsSessionId = session.VfsSessionId?.ToString("D"),
        Status = (int)session.Status,
        RequestedUtc = session.RequestedAtUtc.ToString("O")
    };

    private static GameLaunchSession ToModel(LaunchSessionRow row) => new(
        Guid.Parse(row.Id),
        Guid.Parse(row.EnvironmentId),
        row.GameName,
        row.ExecutablePath,
        row.LaunchArguments,
        row.UsedSteamProtocol != 0,
        string.IsNullOrWhiteSpace(row.VfsSessionId) ? null : Guid.Parse(row.VfsSessionId),
        row.ProcessId,
        (GameLaunchSessionStatus)row.Status,
        DateTimeOffset.Parse(row.RequestedUtc),
        string.IsNullOrWhiteSpace(row.StartedUtc) ? null : DateTimeOffset.Parse(row.StartedUtc),
        string.IsNullOrWhiteSpace(row.ExitedUtc) ? null : DateTimeOffset.Parse(row.ExitedUtc),
        row.ExitCode,
        row.FailureMessage);

    private sealed class LaunchSessionRow
    {
        public string Id { get; init; } = string.Empty;
        public string EnvironmentId { get; init; } = string.Empty;
        public string GameName { get; init; } = string.Empty;
        public string ExecutablePath { get; init; } = string.Empty;
        public string? LaunchArguments { get; init; }
        public int UsedSteamProtocol { get; init; }
        public string? VfsSessionId { get; init; }
        public int? ProcessId { get; init; }
        public int Status { get; init; }
        public string RequestedUtc { get; init; } = string.Empty;
        public string? StartedUtc { get; init; }
        public string? ExitedUtc { get; init; }
        public int? ExitCode { get; init; }
        public string? FailureMessage { get; init; }
    }
}
