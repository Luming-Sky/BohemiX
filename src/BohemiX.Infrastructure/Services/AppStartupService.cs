using BohemiX.Core.Models;
using BohemiX.Core.Services;
using BohemiX.Infrastructure.Persistence;
using Dapper;
using Serilog;

namespace BohemiX.Infrastructure.Services;

public sealed class AppStartupService : IAppStartupService
{
    private readonly IApplicationPathService applicationPathService;
    private readonly IAppSettingsService appSettingsService;
    private readonly SqliteConnectionFactory connectionFactory;
    private readonly ILogger logger;
    private readonly BundledNativeDependencyInstaller nativeDependencyInstaller;
    private readonly IApplicationErrorReporter? applicationErrorReporter;

    public AppStartupService(
        IApplicationPathService applicationPathService,
        IAppSettingsService appSettingsService,
        SqliteConnectionFactory connectionFactory,
        ILogger logger,
        IApplicationErrorReporter? applicationErrorReporter = null)
        : this(
            applicationPathService,
            appSettingsService,
            connectionFactory,
            logger,
            applicationErrorReporter,
            new BundledNativeDependencyInstaller(logger))
    {
    }

    internal AppStartupService(
        IApplicationPathService applicationPathService,
        IAppSettingsService appSettingsService,
        SqliteConnectionFactory connectionFactory,
        ILogger logger,
        IApplicationErrorReporter? applicationErrorReporter,
        BundledNativeDependencyInstaller nativeDependencyInstaller)
    {
        this.applicationPathService = applicationPathService;
        this.appSettingsService = appSettingsService;
        this.connectionFactory = connectionFactory;
        this.logger = logger.ForContext<AppStartupService>();
        this.applicationErrorReporter = applicationErrorReporter;
        this.nativeDependencyInstaller = nativeDependencyInstaller;
    }

    public async Task<ApplicationPaths> InitializeAsync(CancellationToken cancellationToken = default)
    {
        var paths = applicationPathService.GetPaths();
        Directory.CreateDirectory(paths.DataDirectory);
        Directory.CreateDirectory(paths.LogsDirectory);
        Directory.CreateDirectory(paths.ModsDirectory);
        Directory.CreateDirectory(paths.NativeDirectory);
        Directory.CreateDirectory(paths.TrackerDirectory);
        CreateOptionalDirectory(paths.SettingsDirectory);
        CreateOptionalDirectory(paths.CacheDirectory);
        CreateOptionalDirectory(paths.BackupDirectory);
        CreateOptionalDirectory(paths.SavesDirectory);

        try
        {
            await nativeDependencyInstaller.InstallIfPresentAsync(paths, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Error(ex, "Bundled native dependency installation failed");
            await ReportNativeDependencyFailureAsync(ex, paths, cancellationToken);
            throw;
        }

        await InitializeDatabaseAsync(cancellationToken);

        var settings = await appSettingsService.LoadAsync(cancellationToken);
        if (!Directory.Exists(settings.ModsDirectory))
        {
            Directory.CreateDirectory(settings.ModsDirectory);
            await appSettingsService.SaveAsync(settings, cancellationToken);
        }

        logger.Information("BohemiX runtime initialized at {DataDirectory}", paths.DataDirectory);
        return paths;
    }

    private async Task ReportNativeDependencyFailureAsync(
        Exception exception,
        ApplicationPaths paths,
        CancellationToken cancellationToken)
    {
        if (applicationErrorReporter is null)
        {
            return;
        }

        try
        {
            await applicationErrorReporter.ReportAsync(
                ApplicationErrorReport.FromException(
                    exception,
                    "Native dependency verification failed",
                    "BohemiX could not verify or install its native VFS dependencies. Startup was stopped.",
                    "BohemiX native dependency installer",
                    ApplicationErrorCategory.VirtualFileSystem,
                    $"Bundled native directory: {Path.Combine(AppContext.BaseDirectory, "native")}",
                    paths.LogsDirectory),
                cancellationToken);
        }
        catch (Exception reportError) when (reportError is not OperationCanceledException)
        {
            logger.Error(reportError, "Unable to display the native dependency failure dialog");
        }
    }

    private async Task InitializeDatabaseAsync(CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        var command = new CommandDefinition(
            """
            PRAGMA journal_mode = WAL;
            PRAGMA foreign_keys = ON;

            CREATE TABLE IF NOT EXISTS app_settings (
                key TEXT NOT NULL PRIMARY KEY,
                value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS game_installations (
                install_path TEXT NOT NULL PRIMARY KEY,
                name TEXT NOT NULL,
                executable_path TEXT NOT NULL,
                source INTEGER NOT NULL,
                is_verified INTEGER NOT NULL,
                last_seen_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS mod_manifests (
                id TEXT NOT NULL PRIMARY KEY,
                display_name TEXT NOT NULL,
                version TEXT NOT NULL,
                root_path TEXT NOT NULL,
                load_order INTEGER NOT NULL,
                is_enabled INTEGER NOT NULL DEFAULT 1,
                last_scanned_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS mod_files (
                mod_id TEXT NOT NULL,
                relative_path TEXT NOT NULL,
                size_bytes INTEGER NOT NULL,
                content_hash TEXT NULL,
                PRIMARY KEY (mod_id, relative_path),
                FOREIGN KEY (mod_id) REFERENCES mod_manifests(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS mod_load_order (
                mod_id TEXT NOT NULL PRIMARY KEY,
                load_order INTEGER NOT NULL,
                updated_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS mod_states (
                mod_id TEXT NOT NULL PRIMARY KEY,
                is_enabled INTEGER NOT NULL,
                updated_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS mod_conflict_reviews (
                fingerprint TEXT NOT NULL PRIMARY KEY,
                is_reviewed INTEGER NOT NULL,
                updated_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS tracker_offsets (
                source TEXT NOT NULL PRIMARY KEY,
                offset_bytes INTEGER NOT NULL,
                updated_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS tracker_sessions (
                session_id TEXT NOT NULL PRIMARY KEY,
                started_utc TEXT NOT NULL,
                last_event_utc TEXT NOT NULL,
                is_confirmed INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS tracker_events (
                event_id TEXT NOT NULL PRIMARY KEY,
                session_id TEXT NOT NULL,
                event_type INTEGER NOT NULL,
                occurred_utc TEXT NOT NULL,
                entity_id TEXT NOT NULL,
                entity_name TEXT NOT NULL,
                x REAL NULL,
                y REAL NULL,
                z REAL NULL,
                raw_json TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS tracker_entity_states (
                entity_id TEXT NOT NULL PRIMARY KEY,
                kind INTEGER NOT NULL,
                display_name TEXT NOT NULL,
                status INTEGER NOT NULL,
                is_visible INTEGER NOT NULL,
                last_seen_utc TEXT NOT NULL,
                x REAL NULL,
                y REAL NULL,
                z REAL NULL
            );

            CREATE TABLE IF NOT EXISTS achievement_progress (
                achievement_id TEXT NOT NULL PRIMARY KEY,
                current_value INTEGER NOT NULL,
                target_value INTEGER NOT NULL,
                is_unlocked INTEGER NOT NULL,
                unlocked_utc TEXT NULL,
                updated_utc TEXT NOT NULL
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
            cancellationToken: cancellationToken);

        await connection.ExecuteAsync(command);
        await GameLaunchSessionService.EnsureSchemaAsync(connection, cancellationToken);
        await EnsureModManifestEnabledColumnAsync(connection, cancellationToken);
        await EnsureGameEnvironmentColumnsAsync(connection, cancellationToken);
    }

    private async Task EnsureGameEnvironmentColumnsAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var environmentId = applicationPathService.GetPaths().GameEnvironmentId.ToString("D");
        var definition = $"TEXT NOT NULL DEFAULT '{environmentId}'";
        string[] tables =
        [
            "app_settings",
            "game_installations",
            "mod_manifests",
            "mod_files",
            "mod_load_order",
            "mod_states",
            "mod_conflict_reviews",
            "tracker_offsets",
            "tracker_sessions",
            "tracker_events",
            "tracker_entity_states",
            "achievement_progress",
            "player_statistics"
        ];

        foreach (var table in tables)
        {
            await EnsureColumnAsync(connection, table, "environment_id", definition, cancellationToken);
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            CREATE TABLE IF NOT EXISTS game_environment_runtime_metadata (
                environment_id TEXT NOT NULL PRIMARY KEY,
                schema_version INTEGER NOT NULL,
                initialized_utc TEXT NOT NULL
            );

            INSERT INTO game_environment_runtime_metadata (environment_id, schema_version, initialized_utc)
            VALUES (@EnvironmentId, 1, @InitializedUtc)
            ON CONFLICT(environment_id) DO UPDATE SET
                schema_version = excluded.schema_version;
            """,
            new { EnvironmentId = environmentId, InitializedUtc = DateTimeOffset.UtcNow.ToString("O") },
            cancellationToken: cancellationToken));
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

    private static void CreateOptionalDirectory(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            Directory.CreateDirectory(path);
        }
    }

    private static async Task EnsureModManifestEnabledColumnAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var columns = await connection.QueryAsync<TableColumn>(new CommandDefinition(
            "SELECT name AS Name FROM pragma_table_info('mod_manifests')",
            cancellationToken: cancellationToken));

        if (columns.Any(column => string.Equals(column.Name, "is_enabled", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "ALTER TABLE mod_manifests ADD COLUMN is_enabled INTEGER NOT NULL DEFAULT 1",
            cancellationToken: cancellationToken));
    }

    private sealed class TableColumn
    {
        public string Name { get; init; } = string.Empty;
    }
}
