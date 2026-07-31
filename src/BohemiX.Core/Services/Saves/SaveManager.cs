using System.Text.Json;
using System.Text.Json.Serialization;
using BohemiX.Core.Models.Saves;

namespace BohemiX.Core.Services.Saves;

/// <summary>
/// Pure business logic for KCD2 save metadata discovery and durable payload writes.
/// </summary>
public static class SaveManager
{
    private const string MetaFileName = "meta.json";
    private const string CoreDataFileName = "core_data.whs";
    private const string TemporaryCoreDataFileName = "core_data.whs.tmp";
    private const string BackupCoreDataFileName = "core_data.whs.bak";
    private const string TemporaryMetaFileName = "meta.json.tmp";
    private const string SaveDirectoryPrefix = "save_";
    private const string ExitDirectoryPrefix = "exit_";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Root used by the signature required by the game integration layer.
    /// Hosts may override this during startup.
    /// </summary>
    public static string DefaultRootPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "saves");

    public static async Task<List<SaveMeta>> DiscoverSavesAsync(string rootPath)
    {
        var result = new List<SaveMeta>();

        try
        {
            var normalizedRoot = NormalizeRootPath(rootPath);
            if (!Directory.Exists(normalizedRoot))
            {
                Console.WriteLine($"[SaveManager] Save root does not exist: {normalizedRoot}");
                return result;
            }

            foreach (var playlineDirectory in Directory.EnumerateDirectories(normalizedRoot, "playline*", SearchOption.TopDirectoryOnly))
            {
                foreach (var saveDirectory in Directory.EnumerateDirectories(playlineDirectory, $"{SaveDirectoryPrefix}*", SearchOption.TopDirectoryOnly))
                {
                    var metaPath = Path.Combine(saveDirectory, MetaFileName);
                    if (!File.Exists(metaPath))
                    {
                        continue;
                    }

                    try
                    {
                        var json = await File.ReadAllTextAsync(metaPath).ConfigureAwait(false);
                        var meta = JsonSerializer.Deserialize<SaveMeta>(json, JsonOptions);
                        if (meta is null)
                        {
                            Console.WriteLine($"[SaveManager] Empty or invalid metadata skipped: {metaPath}");
                            continue;
                        }

                        result.Add(meta);
                    }
                    catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                    {
                        Console.WriteLine($"[SaveManager] Failed to read metadata. Path={metaPath}; Error={ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Console.WriteLine($"[SaveManager] Save discovery failed. Root={rootPath}; Error={ex.Message}");
        }

        return result
            .OrderByDescending(meta => meta.Timestamp)
            .ToList();
    }

    public static Task<bool> WriteSaveSafeAsync(string playlineId, SaveMeta meta, byte[] coreGameData) =>
        WriteSaveSafeAsync(DefaultRootPath, playlineId, meta, coreGameData);

    public static async Task<bool> WriteSaveSafeAsync(string rootPath, string playlineId, SaveMeta meta, byte[] coreGameData)
    {
        ArgumentNullException.ThrowIfNull(meta);
        ArgumentNullException.ThrowIfNull(coreGameData);

        var saveDirectory = GetSaveDirectoryPath(rootPath, playlineId, meta.SaveID);
        var temporaryPath = Path.Combine(saveDirectory, TemporaryCoreDataFileName);
        var finalPath = Path.Combine(saveDirectory, CoreDataFileName);
        var backupPath = Path.Combine(saveDirectory, BackupCoreDataFileName);

        try
        {
            Directory.CreateDirectory(saveDirectory);
            meta.PlaylineID = playlineId;

            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            // Shadow copy step 1: write the new large payload to a side file first.
            // The live core_data.whs stays untouched while the risky write is in progress,
            // so a crash or power loss cannot turn the current save into a 0-byte file.
            await File.WriteAllBytesAsync(temporaryPath, coreGameData).ConfigureAwait(false);

            var temporaryLength = new FileInfo(temporaryPath).Length;
            if (temporaryLength != coreGameData.LongLength)
            {
                throw new IOException($"Temporary payload size mismatch. Expected={coreGameData.LongLength}; Actual={temporaryLength}");
            }

            if (File.Exists(backupPath))
            {
                File.Delete(backupPath);
            }

            // Shadow copy step 2: move the old official payload to a backup name.
            // Rename within the same directory is effectively atomic on supported file systems,
            // giving us a rollback target until metadata is committed.
            if (File.Exists(finalPath))
            {
                File.Move(finalPath, backupPath, overwrite: true);
            }

            // Shadow copy step 3: promote the fully written temp file to the official name.
            File.Move(temporaryPath, finalPath, overwrite: true);

            await WriteMetaJsonSafeAsync(saveDirectory, meta).ConfigureAwait(false);

            if (File.Exists(backupPath))
            {
                File.Delete(backupPath);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException)
        {
            Console.WriteLine($"[SaveManager] Safe write failed. Directory={saveDirectory}; Error={ex.Message}");
            TryRollbackCorePayload(finalPath, temporaryPath, backupPath);
            return false;
        }
    }

    public static async Task<byte[]> LoadCoreDataAsync(string rootPath, string playlineId, string saveId)
    {
        var saveDirectory = ResolveExistingSaveDirectory(rootPath, playlineId, saveId);
        var corePath = Path.Combine(saveDirectory, CoreDataFileName);

        try
        {
            if (!File.Exists(corePath))
            {
                throw new SaveFileNotFoundException($"KCD2 save payload was not found: {corePath}", corePath);
            }

            return await File.ReadAllBytesAsync(corePath).ConfigureAwait(false);
        }
        catch (SaveFileNotFoundException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"[SaveManager] Failed to load core payload. Path={corePath}; Error={ex.Message}");
            throw;
        }
    }

    public static async Task ConvertExitSaveToPermanentAsync(string rootPath, string playlineId, string saveId)
    {
        var sourceDirectory = ResolveExistingSaveDirectory(rootPath, playlineId, saveId);
        var metaPath = Path.Combine(sourceDirectory, MetaFileName);

        try
        {
            if (!File.Exists(metaPath))
            {
                throw new FileNotFoundException($"Save metadata was not found: {metaPath}", metaPath);
            }

            var json = await File.ReadAllTextAsync(metaPath).ConfigureAwait(false);
            var meta = JsonSerializer.Deserialize<SaveMeta>(json, JsonOptions)
                ?? throw new JsonException($"Metadata could not be deserialized: {metaPath}");

            if (meta.Type != SaveType.Exit)
            {
                throw new InvalidOperationException($"Save {saveId} is {meta.Type}, not an Exit save.");
            }

            var sourceName = Path.GetFileName(sourceDirectory);
            var suffix = StripKnownSavePrefix(sourceName);
            var targetDirectory = Path.Combine(Path.GetDirectoryName(sourceDirectory)!, $"{SaveDirectoryPrefix}{suffix}");

            meta.Type = SaveType.Potion;

            if (string.Equals(sourceDirectory, targetDirectory, StringComparison.OrdinalIgnoreCase))
            {
                await WriteMetaJsonSafeAsync(sourceDirectory, meta).ConfigureAwait(false);
                return;
            }

            if (Directory.Exists(targetDirectory))
            {
                throw new IOException($"Permanent save directory already exists: {targetDirectory}");
            }

            Directory.Move(sourceDirectory, targetDirectory);

            try
            {
                await WriteMetaJsonSafeAsync(targetDirectory, meta).ConfigureAwait(false);
            }
            catch
            {
                if (!Directory.Exists(sourceDirectory) && Directory.Exists(targetDirectory))
                {
                    Directory.Move(targetDirectory, sourceDirectory);
                }

                throw;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException or InvalidOperationException)
        {
            Console.WriteLine($"[SaveManager] Failed to convert Exit save. Playline={playlineId}; Save={saveId}; Error={ex.Message}");
            throw;
        }
    }

    private static async Task WriteMetaJsonSafeAsync(string saveDirectory, SaveMeta meta)
    {
        var temporaryPath = Path.Combine(saveDirectory, TemporaryMetaFileName);
        var finalPath = Path.Combine(saveDirectory, MetaFileName);
        var json = JsonSerializer.Serialize(meta, JsonOptions);

        await File.WriteAllTextAsync(temporaryPath, json).ConfigureAwait(false);
        File.Move(temporaryPath, finalPath, overwrite: true);
    }

    private static void TryRollbackCorePayload(string finalPath, string temporaryPath, string backupPath)
    {
        try
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            if (!File.Exists(finalPath) && File.Exists(backupPath))
            {
                File.Move(backupPath, finalPath, overwrite: true);
            }
        }
        catch (Exception rollbackEx) when (rollbackEx is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"[SaveManager] Rollback failed. Path={finalPath}; Error={rollbackEx.Message}");
        }
    }

    private static string ResolveExistingSaveDirectory(string rootPath, string playlineId, string saveId)
    {
        var playlineDirectory = GetPlaylineDirectoryPath(rootPath, playlineId);
        var safeSaveId = ValidatePathSegment(saveId, nameof(saveId));
        var candidates = new[]
        {
            Path.Combine(playlineDirectory, safeSaveId),
            Path.Combine(playlineDirectory, $"{SaveDirectoryPrefix}{StripKnownSavePrefix(safeSaveId)}"),
            Path.Combine(playlineDirectory, $"{ExitDirectoryPrefix}{StripKnownSavePrefix(safeSaveId)}")
        };

        return candidates.FirstOrDefault(Directory.Exists)
            ?? Path.Combine(playlineDirectory, $"{SaveDirectoryPrefix}{StripKnownSavePrefix(safeSaveId)}");
    }

    private static string GetSaveDirectoryPath(string rootPath, string playlineId, string saveId)
    {
        var playlineDirectory = GetPlaylineDirectoryPath(rootPath, playlineId);
        var safeSaveId = ValidatePathSegment(saveId, nameof(saveId));
        return Path.Combine(playlineDirectory, $"{SaveDirectoryPrefix}{StripKnownSavePrefix(safeSaveId)}");
    }

    private static string GetPlaylineDirectoryPath(string rootPath, string playlineId)
    {
        var normalizedRoot = NormalizeRootPath(rootPath);
        var safePlaylineId = ValidatePathSegment(playlineId, nameof(playlineId));
        return Path.Combine(normalizedRoot, safePlaylineId);
    }

    private static string NormalizeRootPath(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new ArgumentException("Root path cannot be empty.", nameof(rootPath));
        }

        return Path.GetFullPath(rootPath);
    }

    private static string ValidatePathSegment(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Path segment cannot be empty.", parameterName);
        }

        if (value is "." or ".." ||
            value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            value.Contains(Path.DirectorySeparatorChar) ||
            value.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new ArgumentException($"Invalid path segment: {value}", parameterName);
        }

        return value;
    }

    private static string StripKnownSavePrefix(string value)
    {
        if (value.StartsWith(SaveDirectoryPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return value[SaveDirectoryPrefix.Length..];
        }

        return value.StartsWith(ExitDirectoryPrefix, StringComparison.OrdinalIgnoreCase)
            ? value[ExitDirectoryPrefix.Length..]
            : value;
    }
}
