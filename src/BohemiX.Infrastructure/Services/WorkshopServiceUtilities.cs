using BohemiX.Core.Models;
using System.Text.Json;

namespace BohemiX.Infrastructure.Services;

internal static class WorkshopServiceUtilities
{
    internal readonly record struct DirectorySnapshotData(
        int FileCount,
        long TotalBytes,
        DateTimeOffset LastWriteTimeUtc);

    public static string CreateSummary(string description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return string.Empty;
        }

        var compact = string.Join(' ', description.Split(default(string[]), StringSplitOptions.RemoveEmptyEntries));
        return compact.Length <= 220 ? compact : compact[..220] + "...";
    }

    public static IReadOnlyList<WorkshopModInfo> SortSearchResults(
        IEnumerable<WorkshopModInfo> mods,
        WorkshopSortOrder sortOrder)
    {
        return sortOrder switch
        {
            WorkshopSortOrder.Updated => mods
                .OrderByDescending(mod => mod.UpdatedAt ?? DateTimeOffset.MinValue)
                .ThenByDescending(mod => mod.CreatedAt ?? DateTimeOffset.MinValue)
                .ThenBy(mod => mod.PublishedFileId)
                .ToArray(),
            _ => mods.ToArray()
        };
    }

    public static DirectorySnapshotData? TryCreateDirectorySnapshot(string directory)
    {
        try
        {
            var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Select(file => new FileInfo(file))
                .ToArray();
            return new DirectorySnapshotData(
                files.Length,
                files.Sum(file => file.Length),
                files.Length == 0 ? DateTimeOffset.MinValue : files.Max(file => file.LastWriteTimeUtc));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static string SanitizePathSegment(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(ch => invalidChars.Contains(ch) ? '-' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "workshop-mod" : sanitized[..Math.Min(96, sanitized.Length)];
    }

    public static string EnsureUniqueDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return path;
        }

        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"{path}-{i}";
            if (!Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return $"{path}-{Guid.NewGuid():N}";
    }

    public static bool IsPathInsideDirectory(string directory, string path)
    {
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(path);
        return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    public static async Task WriteJsonAtomicallyAsync<T>(
        string path,
        T value,
        JsonSerializerOptions options,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"Unable to determine directory for {path}.");
        Directory.CreateDirectory(directory);

        var temporaryPath = path + ".tmp";

        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             81920,
                             FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, value, options, cancellationToken).ConfigureAwait(false);
            }

            if (File.Exists(path))
            {
                if (OperatingSystem.IsWindows())
                {
                    File.Replace(temporaryPath, path, null, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(temporaryPath, path, overwrite: true);
                }
            }
            else
            {
                File.Move(temporaryPath, path);
            }
        }
        catch
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch
            {
                // Ignore cleanup failures after an unsuccessful atomic write attempt.
            }

            throw;
        }
    }

    public static WorkshopException CreateSteamInitializationException(Exception exception, uint appId)
    {
        var message = exception.Message ?? string.Empty;

        if (message.Contains("Don't own AppId", StringComparison.OrdinalIgnoreCase)
            || message.Contains("denied appID", StringComparison.OrdinalIgnoreCase))
        {
            return new WorkshopException(
                $"Steam denied access to AppID {appId}. Sign in with a Steam account that owns Kingdom Come: Deliverance II.",
                WorkshopFailureKind.GameNotOwned,
                exception);
        }

        if (message.Contains("not logged in", StringComparison.OrdinalIgnoreCase)
            || message.Contains("not logged on", StringComparison.OrdinalIgnoreCase))
        {
            return new WorkshopException(
                "Steam is installed, but the current user is not logged in.",
                WorkshopFailureKind.NotLoggedIn,
                exception);
        }

        return new WorkshopException(
            "Steamworks could not be initialized. Make sure Steam is installed, running, and that this account can access Kingdom Come: Deliverance II.",
            WorkshopFailureKind.SteamUnavailable,
            exception);
    }
}
