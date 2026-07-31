using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BohemiX.Core.Models;
using BohemiX.Core.Services;
using BohemiX.Infrastructure.Persistence;
using Dapper;
using Serilog;

namespace BohemiX.Infrastructure.Services;

public sealed class TrackerProjectionService : ITrackerProjectionService
{
    private const string OffsetSource = "bridge_events_jsonl";
    private const string PlayerEntityId = "player";

    private readonly IApplicationPathService applicationPathService;
    private readonly SqliteConnectionFactory connectionFactory;
    private readonly ILogger logger;

    public TrackerProjectionService(
        IApplicationPathService applicationPathService,
        SqliteConnectionFactory connectionFactory,
        ILogger logger)
    {
        this.applicationPathService = applicationPathService;
        this.connectionFactory = connectionFactory;
        this.logger = logger.ForContext<TrackerProjectionService>();
    }

    public async Task<TrackerIngestResult> IngestBridgeFileAsync(CancellationToken cancellationToken = default)
    {
        var paths = applicationPathService.GetPaths();
        Directory.CreateDirectory(paths.TrackerDirectory);

        if (!File.Exists(paths.TrackerBridgeEventsPath))
        {
            await File.WriteAllTextAsync(paths.TrackerBridgeEventsPath, string.Empty, cancellationToken);
        }

        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        var offset = await connection.QuerySingleOrDefaultAsync<long?>(
            new CommandDefinition(
                "SELECT offset_bytes FROM tracker_offsets WHERE source = @Source",
                new { Source = OffsetSource },
                cancellationToken: cancellationToken)) ?? 0;

        var fileLength = new FileInfo(paths.TrackerBridgeEventsPath).Length;
        if (offset > fileLength)
        {
            logger.Warning("Tracker bridge file was truncated. Resetting offset from {Offset} to 0.", offset);
            offset = 0;
        }

        if (offset == fileLength)
        {
            return new TrackerIngestResult(0, 0, offset, await LoadSummaryAsync(cancellationToken));
        }

        var tail = await ReadTailAsync(paths.TrackerBridgeEventsPath, offset, fileLength, cancellationToken);
        var completeText = GetCompleteText(tail);
        if (completeText.Length == 0)
        {
            return new TrackerIngestResult(0, 0, offset, await LoadSummaryAsync(cancellationToken));
        }

        var processed = 0;
        var skipped = 0;
        var lines = completeText.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var line in lines)
        {
            if (!TryParseEvent(line, out var trackerEvent))
            {
                skipped++;
                continue;
            }

            var inserted = await connection.ExecuteAsync(
                new CommandDefinition(
                    """
                    INSERT OR IGNORE INTO tracker_events (
                        event_id, session_id, event_type, occurred_utc, entity_id, entity_name, x, y, z, raw_json
                    )
                    VALUES (
                        @EventId, @SessionId, @EventType, @OccurredUtc, @EntityId, @EntityName, @X, @Y, @Z, @RawJson
                    );
                    """,
                    new
                    {
                        trackerEvent.EventId,
                        trackerEvent.SessionId,
                        EventType = (int)trackerEvent.EventType,
                        OccurredUtc = trackerEvent.OccurredAtUtc.ToString("O"),
                        trackerEvent.EntityId,
                        trackerEvent.EntityName,
                        trackerEvent.X,
                        trackerEvent.Y,
                        trackerEvent.Z,
                        trackerEvent.RawJson
                    },
                    transaction,
                    cancellationToken: cancellationToken));

            if (inserted == 0)
            {
                continue;
            }

            await UpsertSessionAsync(connection, transaction, trackerEvent, cancellationToken);
            await UpsertEntityStateAsync(connection, transaction, trackerEvent, cancellationToken);
            processed++;
        }

        var nextOffset = offset + Encoding.UTF8.GetByteCount(completeText);
        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO tracker_offsets (source, offset_bytes, updated_utc)
                VALUES (@Source, @OffsetBytes, @UpdatedUtc)
                ON CONFLICT(source) DO UPDATE SET
                    offset_bytes = excluded.offset_bytes,
                    updated_utc = excluded.updated_utc;
                """,
                new
                {
                    Source = OffsetSource,
                    OffsetBytes = nextOffset,
                    UpdatedUtc = DateTimeOffset.UtcNow.ToString("O")
                },
                transaction,
                cancellationToken: cancellationToken));

        await transaction.CommitAsync(cancellationToken);
        logger.Information("Processed {ProcessedEvents} tracker events from {BridgePath}", processed, paths.TrackerBridgeEventsPath);

        return new TrackerIngestResult(processed, skipped, nextOffset, await LoadSummaryAsync(cancellationToken));
    }

    public async Task<TrackerSummary> LoadSummaryAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        var totalEvents = await connection.QuerySingleAsync<int>(
            new CommandDefinition("SELECT COUNT(*) FROM tracker_events", cancellationToken: cancellationToken));
        var visibleEntities = await connection.QuerySingleAsync<int>(
            new CommandDefinition("SELECT COUNT(*) FROM tracker_entity_states WHERE is_visible = 1", cancellationToken: cancellationToken));
        var activeQuests = await connection.QuerySingleAsync<int>(
            new CommandDefinition(
                "SELECT COUNT(*) FROM tracker_entity_states WHERE kind = @Kind AND status = @Status",
                new { Kind = (int)TrackerEntityKind.Quest, Status = (int)TrackerEntityStatus.Active },
                cancellationToken: cancellationToken));
        var completedEntities = await connection.QuerySingleAsync<int>(
            new CommandDefinition(
                "SELECT COUNT(*) FROM tracker_entity_states WHERE status IN @Statuses",
                new { Statuses = new[] { (int)TrackerEntityStatus.Completed, (int)TrackerEntityStatus.Collected } },
                cancellationToken: cancellationToken));
        var unconfirmedSessions = await connection.QuerySingleAsync<int>(
            new CommandDefinition("SELECT COUNT(*) FROM tracker_sessions WHERE is_confirmed = 0", cancellationToken: cancellationToken));

        var lastEvent = await connection.QuerySingleOrDefaultAsync<LastTrackerEventRow>(
            new CommandDefinition(
                """
                SELECT event_type AS EventType, entity_name AS EntityName, occurred_utc AS OccurredUtc
                FROM tracker_events
                ORDER BY occurred_utc DESC
                LIMIT 1
                """,
                cancellationToken: cancellationToken));

        var player = await connection.QuerySingleOrDefaultAsync<PlayerPositionRow>(
            new CommandDefinition(
                """
                SELECT x AS X, y AS Y, z AS Z
                FROM tracker_entity_states
                WHERE entity_id = @EntityId
                """,
                new { EntityId = PlayerEntityId },
                cancellationToken: cancellationToken));

        var lastLabel = lastEvent is null
            ? TrackerSummary.Empty.LastEventLabel
            : $"{(TrackerEventType)(int)lastEvent.EventType}: {lastEvent.EntityName}";
        DateTimeOffset? lastUtc = lastEvent is null
            ? null
            : DateTimeOffset.Parse(lastEvent.OccurredUtc);

        return new TrackerSummary(
            totalEvents,
            visibleEntities,
            activeQuests,
            completedEntities,
            unconfirmedSessions,
            lastLabel,
            lastUtc,
            player?.X,
            player?.Y,
            player?.Z);
    }

    public async Task<IReadOnlyList<TrackerEntityState>> LoadVisibleEntitiesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<TrackerEntityStateRow>(
            new CommandDefinition(
                """
                SELECT
                    entity_id AS EntityId,
                    kind AS Kind,
                    display_name AS DisplayName,
                    status AS Status,
                    is_visible AS IsVisible,
                    last_seen_utc AS LastSeenUtc,
                    x AS X,
                    y AS Y,
                    z AS Z
                FROM tracker_entity_states
                WHERE is_visible = 1
                ORDER BY
                    CASE kind
                        WHEN 1 THEN 0
                        WHEN 2 THEN 1
                        WHEN 3 THEN 2
                        ELSE 3
                    END,
                    last_seen_utc DESC
                LIMIT 24
                """,
                cancellationToken: cancellationToken));

        return rows
            .Select(row => new TrackerEntityState(
                row.EntityId,
                (TrackerEntityKind)(int)row.Kind,
                row.DisplayName,
                (TrackerEntityStatus)(int)row.Status,
                row.IsVisible == 1,
                DateTimeOffset.Parse(row.LastSeenUtc),
                row.X,
                row.Y,
                row.Z))
            .ToArray();
    }

    public async Task<IReadOnlyList<string>> LoadRecentEventLabelsAsync(int limit, CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<LastTrackerEventRow>(
            new CommandDefinition(
                """
                SELECT event_type AS EventType, entity_name AS EntityName, occurred_utc AS OccurredUtc
                FROM tracker_events
                ORDER BY occurred_utc DESC
                LIMIT @Limit
                """,
                new { Limit = Math.Clamp(limit, 1, 20) },
                cancellationToken: cancellationToken));

        return rows
            .Select(row =>
            {
                var eventType = (TrackerEventType)(int)row.EventType;
                var occurred = DateTimeOffset.Parse(row.OccurredUtc).ToLocalTime();
                return $"{occurred:HH:mm}  {eventType}  {row.EntityName}";
            })
            .ToArray();
    }

    private static async Task<byte[]> ReadTailAsync(
        string path,
        long offset,
        long fileLength,
        CancellationToken cancellationToken)
    {
        var count = checked((int)(fileLength - offset));
        var buffer = new byte[count];

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        stream.Seek(offset, SeekOrigin.Begin);
        var read = 0;
        while (read < count)
        {
            var current = await stream.ReadAsync(buffer.AsMemory(read, count - read), cancellationToken);
            if (current == 0)
            {
                break;
            }

            read += current;
        }

        return read == count ? buffer : buffer[..read];
    }

    private static string GetCompleteText(byte[] tail)
    {
        var text = Encoding.UTF8.GetString(tail);
        if (text.EndsWith('\n') || text.EndsWith('\r'))
        {
            return text;
        }

        var lastNewline = text.LastIndexOf('\n');
        return lastNewline < 0 ? string.Empty : text[..(lastNewline + 1)];
    }

    private static bool TryParseEvent(string line, out TrackerEvent trackerEvent)
    {
        trackerEvent = default!;

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var eventType = ParseEventType(GetString(root, "type", "event_type", "eventType"));
            var occurredUtc = GetDateTimeOffset(root, "occurred_at_utc", "occurred_at", "timestamp") ?? DateTimeOffset.UtcNow;
            var sessionId = GetString(root, "session_id", "sessionId") ?? "unknown-session";
            var entityId = GetEntityId(root, eventType);
            var entityName = GetString(root, "entity_name", "entityName", "name", "quest_name", "item_name") ?? entityId;
            var eventId = GetString(root, "event_id", "eventId", "id") ?? BuildStableEventId(sessionId, eventType, occurredUtc, entityId, line);

            trackerEvent = new TrackerEvent(
                eventId,
                sessionId,
                eventType,
                occurredUtc,
                entityId,
                entityName,
                GetDouble(root, "x"),
                GetDouble(root, "y"),
                GetDouble(root, "z"),
                line,
                GetInt32(root, "henry_level", "level"),
                GetInt32(root, "groschen", "groschen_count"));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task UpsertSessionAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        TrackerEvent trackerEvent,
        CancellationToken cancellationToken)
    {
        var isConfirmed = trackerEvent.EventType is TrackerEventType.GameSaved or TrackerEventType.SessionEnded ? 1 : 0;
        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO tracker_sessions (session_id, started_utc, last_event_utc, is_confirmed)
                VALUES (@SessionId, @StartedUtc, @LastEventUtc, @IsConfirmed)
                ON CONFLICT(session_id) DO UPDATE SET
                    last_event_utc = excluded.last_event_utc,
                    is_confirmed = CASE
                        WHEN tracker_sessions.is_confirmed = 1 OR excluded.is_confirmed = 1 THEN 1
                        ELSE 0
                    END;
                """,
                new
                {
                    trackerEvent.SessionId,
                    StartedUtc = trackerEvent.OccurredAtUtc.ToString("O"),
                    LastEventUtc = trackerEvent.OccurredAtUtc.ToString("O"),
                    IsConfirmed = isConfirmed
                },
                transaction,
                cancellationToken: cancellationToken));
    }

    private static async Task UpsertEntityStateAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        TrackerEvent trackerEvent,
        CancellationToken cancellationToken)
    {
        var state = ProjectEntityState(trackerEvent);
        if (state is null)
        {
            return;
        }

        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO tracker_entity_states (
                    entity_id, kind, display_name, status, is_visible, last_seen_utc, x, y, z
                )
                VALUES (
                    @EntityId, @Kind, @DisplayName, @Status, @IsVisible, @LastSeenUtc, @X, @Y, @Z
                )
                ON CONFLICT(entity_id) DO UPDATE SET
                    kind = excluded.kind,
                    display_name = excluded.display_name,
                    status = excluded.status,
                    is_visible = excluded.is_visible,
                    last_seen_utc = excluded.last_seen_utc,
                    x = excluded.x,
                    y = excluded.y,
                    z = excluded.z;
                """,
                new
                {
                    state.EntityId,
                    Kind = (int)state.Kind,
                    state.DisplayName,
                    Status = (int)state.Status,
                    IsVisible = state.IsVisible ? 1 : 0,
                    LastSeenUtc = state.LastSeenUtc.ToString("O"),
                    state.X,
                    state.Y,
                    state.Z
                },
                transaction,
                cancellationToken: cancellationToken));
    }

    private static TrackerEntityState? ProjectEntityState(TrackerEvent trackerEvent)
    {
        var kind = trackerEvent.EventType switch
        {
            TrackerEventType.QuestStarted or TrackerEventType.QuestCompleted => TrackerEntityKind.Quest,
            TrackerEventType.ItemAcquired or TrackerEventType.ItemRemoved => TrackerEntityKind.Item,
            TrackerEventType.PositionUpdated => TrackerEntityKind.Player,
            _ => TrackerEntityKind.Unknown
        };

        if (kind == TrackerEntityKind.Unknown)
        {
            return null;
        }

        var status = trackerEvent.EventType switch
        {
            TrackerEventType.QuestStarted => TrackerEntityStatus.Active,
            TrackerEventType.QuestCompleted => TrackerEntityStatus.Completed,
            TrackerEventType.ItemAcquired => TrackerEntityStatus.Collected,
            TrackerEventType.ItemRemoved => TrackerEntityStatus.Removed,
            TrackerEventType.PositionUpdated => TrackerEntityStatus.Visible,
            _ => TrackerEntityStatus.Hidden
        };

        var isVisible = trackerEvent.EventType is
            TrackerEventType.QuestStarted or
            TrackerEventType.QuestCompleted or
            TrackerEventType.ItemAcquired or
            TrackerEventType.ItemRemoved or
            TrackerEventType.PositionUpdated;

        return new TrackerEntityState(
            trackerEvent.EntityId,
            kind,
            trackerEvent.EntityName,
            status,
            isVisible,
            trackerEvent.OccurredAtUtc,
            trackerEvent.X,
            trackerEvent.Y,
            trackerEvent.Z);
    }

    private static TrackerEventType ParseEventType(string? raw)
    {
        var normalized = (raw ?? string.Empty).Trim().Replace("-", "_", StringComparison.Ordinal).ToUpperInvariant();
        return normalized switch
        {
            "SESSION_START" or "SESSION_STARTED" => TrackerEventType.SessionStarted,
            "QUEST_START" or "QUEST_STARTED" => TrackerEventType.QuestStarted,
            "QUEST_COMPLETE" or "QUEST_COMPLETED" => TrackerEventType.QuestCompleted,
            "ITEM_ADD" or "ITEM_ADDED" or "ITEM_ACQUIRED" => TrackerEventType.ItemAcquired,
            "ITEM_REMOVE" or "ITEM_REMOVED" => TrackerEventType.ItemRemoved,
            "POS_UPDATE" or "POSITION_UPDATE" or "POSITION_UPDATED" => TrackerEventType.PositionUpdated,
            "GAME_SAVE" or "GAME_SAVED" => TrackerEventType.GameSaved,
            "SESSION_END" or "SESSION_ENDED" => TrackerEventType.SessionEnded,
            _ => TrackerEventType.Unknown
        };
    }

    private static string GetEntityId(JsonElement root, TrackerEventType eventType)
    {
        var explicitId = GetString(root, "entity_id", "entityId", "quest_id", "item_id");
        if (!string.IsNullOrWhiteSpace(explicitId))
        {
            return explicitId;
        }

        return eventType == TrackerEventType.PositionUpdated ? PlayerEntityId : "unknown-entity";
    }

    private static string? GetString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }

        return null;
    }

    private static double? GetDouble(JsonElement root, string name)
    {
        if (root.TryGetProperty(name, out var value) && value.TryGetDouble(out var direct))
        {
            return direct;
        }

        if (root.TryGetProperty("position", out var position) &&
            position.ValueKind == JsonValueKind.Object &&
            position.TryGetProperty(name, out var nested) &&
            nested.TryGetDouble(out var fromPosition))
        {
            return fromPosition;
        }

        return null;
    }

    private static int? GetInt32(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value) && value.TryGetInt32(out var result))
            {
                return result;
            }
        }

        return null;
    }

    private static DateTimeOffset? GetDateTimeOffset(JsonElement root, params string[] names)
    {
        var raw = GetString(root, names);
        return DateTimeOffset.TryParse(raw, out var value) ? value.ToUniversalTime() : null;
    }

    private static string BuildStableEventId(
        string sessionId,
        TrackerEventType eventType,
        DateTimeOffset occurredUtc,
        string entityId,
        string line)
    {
        var input = $"{sessionId}|{eventType}|{occurredUtc:O}|{entityId}|{line}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash);
    }

    private sealed class LastTrackerEventRow
    {
        public long EventType { get; set; }

        public string EntityName { get; set; } = string.Empty;

        public string OccurredUtc { get; set; } = string.Empty;
    }

    private sealed class PlayerPositionRow
    {
        public double? X { get; set; }

        public double? Y { get; set; }

        public double? Z { get; set; }
    }

    private sealed class TrackerEntityStateRow
    {
        public string EntityId { get; set; } = string.Empty;

        public long Kind { get; set; }

        public string DisplayName { get; set; } = string.Empty;

        public long Status { get; set; }

        public long IsVisible { get; set; }

        public string LastSeenUtc { get; set; } = string.Empty;

        public double? X { get; set; }

        public double? Y { get; set; }

        public double? Z { get; set; }
    }
}
