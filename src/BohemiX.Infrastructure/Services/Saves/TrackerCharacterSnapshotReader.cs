using System.Text;
using System.Text.Json;

namespace BohemiX.Infrastructure.Services.Saves;

internal sealed record TrackerCharacterSnapshot(
    string SessionId,
    DateTimeOffset OccurredAtUtc,
    int? HenryLevel,
    int? GroschenCount);

internal static class TrackerCharacterSnapshotReader
{
    private const int MaxTailBytes = 8 * 1024 * 1024;
    private const string GameLogEventPrefix = "[BohemiXTrackerEvent] ";
    private static readonly TimeSpan MatchWindow = TimeSpan.FromMinutes(3);

    public static async Task<TrackerCharacterSnapshot?> FindClosestAsync(
        string bridgePath,
        IReadOnlyCollection<DateTimeOffset> targetTimes,
        CancellationToken cancellationToken = default)
    {
        return await FindClosestAsync(
            bridgePath,
            gameLogPath: null,
            targetTimes,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public static async Task<TrackerCharacterSnapshot?> FindClosestAsync(
        string bridgePath,
        string? gameLogPath,
        IReadOnlyCollection<DateTimeOffset> targetTimes,
        bool matchGameLogUsingFileTime = false,
        CancellationToken cancellationToken = default)
    {
        if (targetTimes.Count == 0)
        {
            return null;
        }

        var sources = new[]
        {
            (Path: bridgePath, Prefix: (string?)null),
            (Path: gameLogPath, Prefix: (string?)GameLogEventPrefix)
        };
        TrackerCharacterSnapshot? closest = null;
        var closestDelta = TimeSpan.MaxValue;
        foreach (var source in sources)
        {
            if (string.IsNullOrWhiteSpace(source.Path) || !File.Exists(source.Path))
            {
                continue;
            }

            var text = await ReadTailAsync(source.Path, cancellationToken).ConfigureAwait(false);
            if (text is null)
            {
                continue;
            }

            foreach (var rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = rawLine;
                if (source.Prefix is not null)
                {
                    var prefixIndex = rawLine.IndexOf(source.Prefix, StringComparison.Ordinal);
                    if (prefixIndex < 0)
                    {
                        continue;
                    }

                    line = rawLine[(prefixIndex + source.Prefix.Length)..];
                }

                if (!TryParse(line, out var candidate))
                {
                    continue;
                }

                var candidateTime = candidate.OccurredAtUtc;
                if (matchGameLogUsingFileTime && source.Prefix is not null)
                {
                    try
                    {
                        candidateTime = File.GetLastWriteTimeUtc(source.Path);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Keep the event timestamp if the live log is temporarily unavailable.
                    }
                }

                var delta = targetTimes.Min(target => (target - candidateTime).Duration());
                if (delta <= MatchWindow && delta < closestDelta)
                {
                    closest = candidate;
                    closestDelta = delta;
                }
            }
        }

        return closest;
    }

    private static async Task<string?> ReadTailAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var bytesToRead = (int)Math.Min(stream.Length, MaxTailBytes);
            if (stream.Length > bytesToRead)
            {
                stream.Seek(-bytesToRead, SeekOrigin.End);
            }

            var buffer = new byte[bytesToRead];
            var offset = 0;
            while (offset < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                offset += read;
            }

            var text = Encoding.UTF8.GetString(buffer, 0, offset);
            if (stream.Length > bytesToRead)
            {
                var firstNewline = text.IndexOf('\n');
                text = firstNewline < 0 ? string.Empty : text[(firstNewline + 1)..];
            }

            return text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool TryParse(string line, out TrackerCharacterSnapshot snapshot)
    {
        snapshot = default!;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var type = GetString(root, "type", "event_type", "eventType")
                ?.Trim()
                .Replace('-', '_')
                .ToUpperInvariant();
            if (type is not ("GAME_SAVE" or "GAME_SAVED" or "CHARACTER_SNAPSHOT"))
            {
                return false;
            }

            var occurredAtUtc = ParseTimestamp(root);
            if (occurredAtUtc is null)
            {
                return false;
            }

            var level = GetInt32(root, "henry_level", "level");
            var groschen = GetInt32(root, "groschen", "groschen_count");
            if (level is null && groschen is null)
            {
                return false;
            }

            snapshot = new TrackerCharacterSnapshot(
                GetString(root, "session_id", "sessionId") ?? "unknown-session",
                occurredAtUtc.Value.ToUniversalTime(),
                level is > 0 ? level : null,
                groschen is >= 0 ? groschen : null);
            return snapshot.HenryLevel is not null || snapshot.GroschenCount is not null;
        }
        catch (JsonException)
        {
            return false;
        }
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

    private static DateTimeOffset? ParseTimestamp(JsonElement root)
    {
        var timestamp = GetString(root, "timestamp", "occurred_at", "occurred_at_utc");
        if (DateTimeOffset.TryParse(timestamp, out var parsed))
        {
            return parsed;
        }

        if (root.TryGetProperty("timestamp_unix", out var unixValue))
        {
            try
            {
                if (unixValue.TryGetInt64(out var unixSeconds))
                {
                    return DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
                }

                if (unixValue.TryGetDouble(out var unixSecondsDouble) &&
                    double.IsFinite(unixSecondsDouble) &&
                    unixSecondsDouble >= long.MinValue &&
                    unixSecondsDouble <= long.MaxValue)
                {
                    return DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(Math.Truncate(unixSecondsDouble)));
                }
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        return null;
    }
}
