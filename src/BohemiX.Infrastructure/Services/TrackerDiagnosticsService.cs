using System.Text.Json;
using BohemiX.Core.Models;
using BohemiX.Core.Services;
using BohemiX.Infrastructure.Persistence;
using Dapper;
using Serilog;

namespace BohemiX.Infrastructure.Services;

public sealed class TrackerDiagnosticsService : ITrackerDiagnosticsService
{
    private readonly IApplicationPathService applicationPathService;
    private readonly ITrackerProjectionService trackerProjectionService;
    private readonly SqliteConnectionFactory connectionFactory;
    private readonly ILogger logger;

    public TrackerDiagnosticsService(
        IApplicationPathService applicationPathService,
        ITrackerProjectionService trackerProjectionService,
        SqliteConnectionFactory connectionFactory,
        ILogger logger)
    {
        this.applicationPathService = applicationPathService;
        this.trackerProjectionService = trackerProjectionService;
        this.connectionFactory = connectionFactory;
        this.logger = logger.ForContext<TrackerDiagnosticsService>();
    }

    public async Task<TrackerDiagnosticsResult> RunBridgeSelfTestAsync(CancellationToken cancellationToken = default)
    {
        var paths = applicationPathService.GetPaths();
        Directory.CreateDirectory(paths.TrackerDirectory);

        var sessionId = $"diagnostic-{Guid.NewGuid():N}";
        var events = BuildDiagnosticEvents(sessionId);
        var lines = events.Select(item => JsonSerializer.Serialize(item)).ToArray();

        await File.AppendAllTextAsync(paths.TrackerBridgeEventsPath, string.Join(Environment.NewLine, lines) + Environment.NewLine, cancellationToken);
        var ingest = await trackerProjectionService.IngestBridgeFileAsync(cancellationToken);
        await RemoveDiagnosticProjectionAsync(sessionId, cancellationToken);

        var success = ingest.ProcessedEvents >= events.Count;
        var message = success
            ? $"Tracker bridge self-test passed: {ingest.ProcessedEvents} events processed."
            : $"Tracker bridge self-test inconclusive: {ingest.ProcessedEvents}/{events.Count} events processed.";

        logger.Information(
            "Tracker bridge self-test completed. Success={Success}, Processed={ProcessedEvents}, Expected={ExpectedEvents}",
            success,
            ingest.ProcessedEvents,
            events.Count);

        return new TrackerDiagnosticsResult(success, message, events.Count, ingest.ProcessedEvents);
    }

    private async Task RemoveDiagnosticProjectionAsync(string sessionId, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM tracker_entity_states WHERE entity_id IN @EntityIds",
            new { EntityIds = new[] { "diagnostic-player", "diagnostic-quest" } },
            transaction,
            cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM tracker_events WHERE session_id = @SessionId",
            new { SessionId = sessionId },
            transaction,
            cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM tracker_sessions WHERE session_id = @SessionId",
            new { SessionId = sessionId },
            transaction,
            cancellationToken: cancellationToken));

        await transaction.CommitAsync(cancellationToken);
    }

    private static IReadOnlyList<object> BuildDiagnosticEvents(string sessionId)
    {
        var now = DateTimeOffset.UtcNow;
        return
        [
            new
            {
                event_id = $"{sessionId}-session",
                session_id = sessionId,
                type = "SESSION_START",
                timestamp = now.ToString("O"),
                entity_id = "diagnostic-session",
                entity_name = "BohemiX diagnostic session"
            },
            new
            {
                event_id = $"{sessionId}-quest",
                session_id = sessionId,
                type = "QUEST_START",
                timestamp = now.AddSeconds(1).ToString("O"),
                entity_id = "diagnostic-quest",
                entity_name = "BohemiX diagnostic quest"
            },
            new
            {
                event_id = $"{sessionId}-position",
                session_id = sessionId,
                type = "POS_UPDATE",
                timestamp = now.AddSeconds(2).ToString("O"),
                entity_id = "diagnostic-player",
                entity_name = "Diagnostic player",
                x = 42,
                y = 84,
                z = 3
            },
            new
            {
                event_id = $"{sessionId}-save",
                session_id = sessionId,
                type = "GAME_SAVED",
                timestamp = now.AddSeconds(3).ToString("O"),
                entity_id = "diagnostic-save",
                entity_name = "Diagnostic save"
            }
        ];
    }
}
