using System.Text.Json;
using BohemiX.Core.Models;
using BohemiX.Core.Services;
using BohemiX.Infrastructure.Persistence;
using Dapper;
using Serilog;

namespace BohemiX.Infrastructure.Services;

public sealed class TrackerAchievementService : ITrackerAchievementService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly IApplicationPathService applicationPathService;
    private readonly IAchievementRuleService achievementRuleService;
    private readonly SqliteConnectionFactory connectionFactory;
    private readonly ILogger logger;

    public TrackerAchievementService(
        IApplicationPathService applicationPathService,
        IAchievementRuleService achievementRuleService,
        SqliteConnectionFactory connectionFactory,
        ILogger logger)
    {
        this.applicationPathService = applicationPathService;
        this.achievementRuleService = achievementRuleService;
        this.connectionFactory = connectionFactory;
        this.logger = logger.ForContext<TrackerAchievementService>();
    }

    public async Task<IReadOnlyList<AchievementProgress>> EvaluateAsync(CancellationToken cancellationToken = default)
    {
        var definitions = await LoadDefinitionsAsync(cancellationToken);
        var metrics = await LoadTrackerMetricsAsync(cancellationToken);
        var progress = await achievementRuleService.EvaluateAsync(definitions, metrics, cancellationToken);

        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        foreach (var item in progress)
        {
            await connection.ExecuteAsync(
                new CommandDefinition(
                    """
                    INSERT INTO achievement_progress (
                        achievement_id, current_value, target_value, is_unlocked, unlocked_utc, updated_utc
                    )
                    VALUES (
                        @AchievementId, @CurrentValue, @TargetValue, @IsUnlocked, @UnlockedUtc, @UpdatedUtc
                    )
                    ON CONFLICT(achievement_id) DO UPDATE SET
                        current_value = excluded.current_value,
                        target_value = excluded.target_value,
                        is_unlocked = CASE
                            WHEN achievement_progress.is_unlocked = 1 OR excluded.is_unlocked = 1 THEN 1
                            ELSE 0
                        END,
                        unlocked_utc = CASE
                            WHEN achievement_progress.unlocked_utc IS NOT NULL THEN achievement_progress.unlocked_utc
                            ELSE excluded.unlocked_utc
                        END,
                        updated_utc = excluded.updated_utc;
                    """,
                    new
                    {
                        item.AchievementId,
                        item.CurrentValue,
                        item.TargetValue,
                        IsUnlocked = item.IsUnlocked ? 1 : 0,
                        UnlockedUtc = item.UnlockedAt?.ToString("O"),
                        UpdatedUtc = DateTimeOffset.UtcNow.ToString("O")
                    },
                    transaction,
                    cancellationToken: cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);
        logger.Information("Evaluated {AchievementCount} tracker achievements", progress.Count);
        return progress;
    }

    public async Task<IReadOnlyList<AchievementProgress>> LoadProgressAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<AchievementProgressRow>(
            new CommandDefinition(
                """
                SELECT
                    achievement_id AS AchievementId,
                    current_value AS CurrentValue,
                    target_value AS TargetValue,
                    is_unlocked AS IsUnlocked,
                    unlocked_utc AS UnlockedUtc
                FROM achievement_progress
                ORDER BY is_unlocked DESC, achievement_id ASC
                """,
                cancellationToken: cancellationToken));

        return rows
            .Select(row => new AchievementProgress(
                row.AchievementId,
                row.CurrentValue,
                row.TargetValue,
                row.IsUnlocked == 1,
                string.IsNullOrWhiteSpace(row.UnlockedUtc) ? null : DateTimeOffset.Parse(row.UnlockedUtc)))
            .ToArray();
    }

    private async Task<IReadOnlyList<AchievementDefinition>> LoadDefinitionsAsync(CancellationToken cancellationToken)
    {
        var paths = applicationPathService.GetPaths();
        Directory.CreateDirectory(paths.TrackerDirectory);

        if (!File.Exists(paths.TrackerAchievementRulesPath))
        {
            var defaults = CreateDefaultDefinitions();
            var json = JsonSerializer.Serialize(defaults, JsonOptions);
            await File.WriteAllTextAsync(paths.TrackerAchievementRulesPath, json, cancellationToken);
            return defaults;
        }

        try
        {
            var json = await File.ReadAllTextAsync(paths.TrackerAchievementRulesPath, cancellationToken);
            return JsonSerializer.Deserialize<IReadOnlyList<AchievementDefinition>>(json, JsonOptions) ?? CreateDefaultDefinitions();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            logger.Warning(ex, "Failed to load tracker achievement rules from {RulesPath}", paths.TrackerAchievementRulesPath);
            return CreateDefaultDefinitions();
        }
    }

    private async Task<IReadOnlyDictionary<string, int>> LoadTrackerMetricsAsync(CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        var metrics = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["tracker.events.total"] = await QueryCountAsync(connection, "SELECT COUNT(*) FROM tracker_events", cancellationToken),
            ["tracker.quests.active"] = await QueryCountAsync(connection, "SELECT COUNT(*) FROM tracker_entity_states WHERE kind = 1 AND status = 2", cancellationToken),
            ["tracker.quests.completed"] = await QueryCountAsync(connection, "SELECT COUNT(*) FROM tracker_entity_states WHERE kind = 1 AND status = 3", cancellationToken),
            ["tracker.items.collected"] = await QueryCountAsync(connection, "SELECT COUNT(*) FROM tracker_entity_states WHERE kind = 2 AND status = 4", cancellationToken),
            ["tracker.position.updates"] = await QueryCountAsync(connection, "SELECT COUNT(*) FROM tracker_events WHERE event_type = 6", cancellationToken),
            ["tracker.sessions.confirmed"] = await QueryCountAsync(connection, "SELECT COUNT(*) FROM tracker_sessions WHERE is_confirmed = 1", cancellationToken)
        };

        return metrics;
    }

    private static async Task<int> QueryCountAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        return await connection.QuerySingleAsync<int>(new CommandDefinition(sql, cancellationToken: cancellationToken));
    }

    private static IReadOnlyList<AchievementDefinition> CreateDefaultDefinitions()
    {
        return
        [
            new AchievementDefinition("tracker.first_trace", "First Trace", "Process the first BohemiX Tracker event.", "tracker.events.total", 1),
            new AchievementDefinition("tracker.quest_started", "On The Road", "Reveal the first tracked quest.", "tracker.quests.active", 1),
            new AchievementDefinition("tracker.quest_completed", "Written In The Chronicle", "Complete one tracked quest.", "tracker.quests.completed", 1),
            new AchievementDefinition("tracker.first_position", "Cartographer's Mark", "Receive the first player position update.", "tracker.position.updates", 1),
            new AchievementDefinition("tracker.confirmed_session", "Safe Return", "Confirm one tracker session with a save or clean session end.", "tracker.sessions.confirmed", 1)
        ];
    }

    private sealed class AchievementProgressRow
    {
        public string AchievementId { get; set; } = string.Empty;

        public int CurrentValue { get; set; }

        public int TargetValue { get; set; }

        public int IsUnlocked { get; set; }

        public string? UnlockedUtc { get; set; }
    }
}
