using System.Globalization;
using BohemiX.Core.Models.Saves;
using BohemiX.Core.Services.Saves;
using Microsoft.Data.Sqlite;
using Serilog;

namespace BohemiX.Infrastructure.Services.Saves;

public sealed class SlotManager(
    string officialSavePath,
    string vaultRoot,
    string databasePath,
    IKcdSaveParser parser,
    IProcessCoordinator coordinator,
    ILogger logger,
    Guid environmentId = default)
    : ISlotManager
{
    private const int MaxDisplayNameLength = 80;

    private readonly string officialSavePath = NormalizeDirectoryPath(officialSavePath);
    private readonly string vaultRoot = NormalizeDirectoryPath(vaultRoot);
    private readonly string databasePath = Path.GetFullPath(databasePath);
    private readonly ILogger logger = logger.ForContext<SlotManager>();
    private readonly object initLock = new();
    private bool initialized;

    /// <summary>
    /// Moves the current real official save files into a generated Vault slot and records logical metadata in SQLite.
    /// </summary>
    public async Task<SaveSlot> ImportSave(
        string? displayName = null,
        IProgress<SaveImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        EnsureDatabase();

        if (!await coordinator.IsSafeToSwitch(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("游戏正在运行或正在写入存档。请先退出游戏，再备份或切换。");
        }

        if (!Directory.Exists(this.officialSavePath))
        {
            throw new DirectoryNotFoundException(this.officialSavePath);
        }

        // [防御性编程] Import 只能处理真实官方目录；如果入口已是 Junction，说明当前处于路由状态。
        if (File.GetAttributes(this.officialSavePath).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException("当前已经在使用某个备份，不能再次备份当前存档。请先取消当前切换。");
        }

        var entries = Directory.EnumerateFileSystemEntries(this.officialSavePath).ToList();
        if (entries.Count == 0)
        {
            throw new InvalidOperationException("没有找到可以备份的游戏存档。");
        }

        var manifest = BuildImportManifest(entries);

        var now = DateTimeOffset.UtcNow;
        var physicalName = CreatePhysicalName(now);
        var slotPath = GetSlotPath(physicalName);
        var moved = new List<(string Source, string Destination)>();
        var processedFiles = 0;
        var processedBytes = 0L;

        Directory.CreateDirectory(slotPath);
        ReportImportProgress(progress, "Preparing", 0, manifest.TotalFiles, 0, manifest.TotalBytes, null);

        try
        {
            foreach (var source in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = Path.Combine(slotPath, Path.GetFileName(source));
                var entry = manifest.Entries.TryGetValue(source, out var summary)
                    ? summary
                    : new ImportEntrySummary(1, GetFileLengthSafe(source));

                ReportImportProgress(
                    progress,
                    "Moving",
                    processedFiles,
                    manifest.TotalFiles,
                    processedBytes,
                    manifest.TotalBytes,
                    Path.GetFileName(source));

                // [防御性编程] 使用 Move 保留原始文件名与二进制内容；
                // 禁止重命名 metadata.xml、.pak、.whs，确保 CryEngine 兼容。
                MoveEntry(source, destination);
                moved.Add((source, destination));
                processedFiles += entry.FileCount;
                processedBytes += entry.TotalBytes;

                ReportImportProgress(
                    progress,
                    "Moving",
                    processedFiles,
                    manifest.TotalFiles,
                    processedBytes,
                    manifest.TotalBytes,
                    Path.GetFileName(source));
            }

            ReportImportProgress(progress, "Parsing metadata", processedFiles, manifest.TotalFiles, processedBytes, manifest.TotalBytes, null);
            var metadata = parser.ParseMetadata(slotPath);
            var finalDisplayName = ValidateDisplayName(displayName ?? metadata.GameSaveName ?? $"备份 {now:yyyy-MM-dd HH-mm-ss}");
            var slot = new SaveSlot(
                Guid.NewGuid(),
                physicalName,
                finalDisplayName,
                slotPath,
                now,
                now,
                metadata.GameSaveName,
                metadata.PlayTime,
                metadata.LastSavedAtUtc,
                metadata.DisplayData,
                metadata.Type,
                metadata.GameVersion,
                metadata.ThumbnailPath);

            InsertSlot(slot);
            ReportImportProgress(progress, "Completed", manifest.TotalFiles, manifest.TotalFiles, manifest.TotalBytes, manifest.TotalBytes, null);
            this.logger.Information("Imported official save directory into Vault slot {PhysicalName}", physicalName);
            return slot;
        }
        catch
        {
            foreach (var (source, destination) in moved.AsEnumerable().Reverse())
            {
                if ((File.Exists(destination) || Directory.Exists(destination)) && !File.Exists(source) && !Directory.Exists(source))
                {
                    MoveEntry(destination, source);
                }
            }

            // [防御性编程] 回滚时只删除本次创建且已确认为空的 Vault 槽位目录。
            // 绝不递归删除官方目录或未知目录，避免损坏玩家真实存档。
            if (Directory.Exists(slotPath) && !Directory.EnumerateFileSystemEntries(slotPath).Any())
            {
                Directory.Delete(slotPath, recursive: false);
            }

            throw;
        }
    }

    /// <summary>
    /// Updates only the logical DisplayName in SQLite; the generated Vault physical folder name is never changed.
    /// </summary>
    public async Task RenameSlot(Guid slotId, string displayName, CancellationToken cancellationToken = default)
    {
        EnsureDatabase();
        var safeName = ValidateDisplayName(displayName);

        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE SaveSlots
            SET DisplayName = $displayName, UpdatedAtUtc = $updatedAtUtc
            WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$displayName", safeName);
        command.Parameters.AddWithValue("$updatedAtUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$id", slotId.ToString("D"));

        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
        {
            throw new KeyNotFoundException("找不到这个备份，可能已经被删除。");
        }
    }

    /// <summary>
    /// Returns all logical save slots while resolving their physical Vault paths.
    /// </summary>
    public async Task<IReadOnlyList<SaveSlot>> GetAllSlots(CancellationToken cancellationToken = default)
    {
        EnsureDatabase();

        var result = new List<SaveSlot>();
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, PhysicalName, DisplayName, CreatedAtUtc, UpdatedAtUtc, GameSaveName, PlayTimeSeconds, LastSavedAtUtc,
                   HenryLevel, CurrentLocation, ActiveQuest, GroschenCount, PlayerStatusEffects, SaveType, GameVersion, ThumbnailPath
            FROM SaveSlots
            ORDER BY UpdatedAtUtc DESC;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var physicalName = reader.GetString(1);
            result.Add(new SaveSlot(
                Guid.Parse(reader.GetString(0)),
                physicalName,
                reader.GetString(2),
                GetSlotPath(physicalName),
                DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
                DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : TimeSpan.FromSeconds(reader.GetInt64(6)),
                reader.IsDBNull(7) ? null : DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture),
                CreateDisplayInfo(reader),
                reader.IsDBNull(13) ? null : Enum.TryParse<SaveType>(reader.GetString(13), out var saveType) ? saveType : null,
                reader.IsDBNull(14) ? null : reader.GetString(14),
                reader.IsDBNull(15) ? null : reader.GetString(15)));
        }

        for (var i = 0; i < result.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (NeedsMetadataRefresh(result[i]) && Directory.Exists(result[i].PhysicalPath))
            {
                var metadata = parser.ParseMetadata(result[i].PhysicalPath);
                if (HasAnyMetadata(metadata))
                {
                    var hydrated = result[i] with
                    {
                        GameSaveName = metadata.GameSaveName ?? result[i].GameSaveName,
                        PlayTime = metadata.PlayTime ?? result[i].PlayTime,
                        LastSavedAtUtc = metadata.LastSavedAtUtc ?? result[i].LastSavedAtUtc,
                        DisplayData = metadata.DisplayData ?? result[i].DisplayData,
                        Type = metadata.Type ?? result[i].Type,
                        GameVersion = metadata.GameVersion ?? result[i].GameVersion,
                        ThumbnailPath = metadata.ThumbnailPath ?? result[i].ThumbnailPath
                    };
                    result[i] = hydrated;
                    UpdateSlotMetadata(hydrated);
                }
            }
        }

        return result;
    }

    private void EnsureDatabase()
    {
        if (this.initialized)
        {
            return;
        }

        lock (this.initLock)
        {
            if (this.initialized)
            {
                return;
            }

            Directory.CreateDirectory(this.vaultRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(this.databasePath)!);

            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS SaveSlots (
                    Id TEXT PRIMARY KEY NOT NULL,
                    PhysicalName TEXT NOT NULL UNIQUE,
                    DisplayName TEXT NOT NULL,
                    CreatedAtUtc TEXT NOT NULL,
                    UpdatedAtUtc TEXT NOT NULL,
                GameSaveName TEXT NULL,
                PlayTimeSeconds INTEGER NULL,
                LastSavedAtUtc TEXT NULL,
                HenryLevel INTEGER NULL,
                CurrentLocation TEXT NULL,
                ActiveQuest TEXT NULL,
                GroschenCount INTEGER NULL,
                PlayerStatusEffects TEXT NULL,
                SaveType TEXT NULL,
                GameVersion TEXT NULL,
                ThumbnailPath TEXT NULL
                );
                """;
            command.ExecuteNonQuery();
            EnsureColumn(connection, "HenryLevel", "INTEGER NULL");
            EnsureColumn(connection, "CurrentLocation", "TEXT NULL");
            EnsureColumn(connection, "ActiveQuest", "TEXT NULL");
            EnsureColumn(connection, "GroschenCount", "INTEGER NULL");
            EnsureColumn(connection, "PlayerStatusEffects", "TEXT NULL");
            EnsureColumn(connection, "SaveType", "TEXT NULL");
            EnsureColumn(connection, "GameVersion", "TEXT NULL");
            EnsureColumn(connection, "ThumbnailPath", "TEXT NULL");
            EnsureColumn(connection, "EnvironmentId", $"TEXT NOT NULL DEFAULT '{environmentId:D}'");

            this.initialized = true;
        }
    }

    private void InsertSlot(SaveSlot slot)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO SaveSlots
            (Id, PhysicalName, DisplayName, CreatedAtUtc, UpdatedAtUtc, GameSaveName, PlayTimeSeconds, LastSavedAtUtc,
             HenryLevel, CurrentLocation, ActiveQuest, GroschenCount, PlayerStatusEffects, SaveType, GameVersion, ThumbnailPath)
            VALUES
            ($id, $physicalName, $displayName, $createdAtUtc, $updatedAtUtc, $gameSaveName, $playTimeSeconds, $lastSavedAtUtc,
             $henryLevel, $currentLocation, $activeQuest, $groschenCount, $playerStatusEffects, $saveType, $gameVersion, $thumbnailPath);
            """;
        command.Parameters.AddWithValue("$id", slot.Id.ToString("D"));
        command.Parameters.AddWithValue("$physicalName", slot.PhysicalName);
        command.Parameters.AddWithValue("$displayName", slot.DisplayName);
        command.Parameters.AddWithValue("$createdAtUtc", slot.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$updatedAtUtc", slot.UpdatedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$gameSaveName", (object?)slot.GameSaveName ?? DBNull.Value);
        command.Parameters.AddWithValue("$playTimeSeconds", slot.PlayTime is null ? DBNull.Value : (object)(long)slot.PlayTime.Value.TotalSeconds);
        command.Parameters.AddWithValue("$lastSavedAtUtc", (object?)slot.LastSavedAtUtc?.ToString("O", CultureInfo.InvariantCulture) ?? DBNull.Value);
        command.Parameters.AddWithValue("$henryLevel", slot.DisplayData is null || slot.DisplayData.HenryLevel <= 0 ? DBNull.Value : slot.DisplayData.HenryLevel);
        command.Parameters.AddWithValue("$currentLocation", string.IsNullOrWhiteSpace(slot.DisplayData?.CurrentLocation) ? DBNull.Value : slot.DisplayData.CurrentLocation);
        command.Parameters.AddWithValue("$activeQuest", string.IsNullOrWhiteSpace(slot.DisplayData?.ActiveQuest) ? DBNull.Value : slot.DisplayData.ActiveQuest);
        command.Parameters.AddWithValue("$groschenCount", slot.DisplayData is null || slot.DisplayData.GroschenCount <= 0 ? DBNull.Value : slot.DisplayData.GroschenCount);
        command.Parameters.AddWithValue("$playerStatusEffects", slot.DisplayData?.PlayerStatusEffects.Count > 0 ? string.Join("|", slot.DisplayData.PlayerStatusEffects) : DBNull.Value);
        command.Parameters.AddWithValue("$saveType", (object?)slot.Type?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$gameVersion", (object?)slot.GameVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("$thumbnailPath", (object?)slot.ThumbnailPath ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private void UpdateSlotMetadata(SaveSlot slot)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE SaveSlots
            SET GameSaveName = $gameSaveName,
                PlayTimeSeconds = $playTimeSeconds,
                LastSavedAtUtc = $lastSavedAtUtc,
                HenryLevel = $henryLevel,
                CurrentLocation = $currentLocation,
                ActiveQuest = $activeQuest,
                GroschenCount = $groschenCount,
                PlayerStatusEffects = $playerStatusEffects,
                SaveType = $saveType,
                GameVersion = $gameVersion,
                ThumbnailPath = $thumbnailPath
            WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$id", slot.Id.ToString("D"));
        command.Parameters.AddWithValue("$gameSaveName", (object?)slot.GameSaveName ?? DBNull.Value);
        command.Parameters.AddWithValue("$playTimeSeconds", slot.PlayTime is null ? DBNull.Value : (object)(long)slot.PlayTime.Value.TotalSeconds);
        command.Parameters.AddWithValue("$lastSavedAtUtc", (object?)slot.LastSavedAtUtc?.ToString("O", CultureInfo.InvariantCulture) ?? DBNull.Value);
        command.Parameters.AddWithValue("$henryLevel", slot.DisplayData is null || slot.DisplayData.HenryLevel <= 0 ? DBNull.Value : slot.DisplayData.HenryLevel);
        command.Parameters.AddWithValue("$currentLocation", string.IsNullOrWhiteSpace(slot.DisplayData?.CurrentLocation) ? DBNull.Value : slot.DisplayData.CurrentLocation);
        command.Parameters.AddWithValue("$activeQuest", string.IsNullOrWhiteSpace(slot.DisplayData?.ActiveQuest) ? DBNull.Value : slot.DisplayData.ActiveQuest);
        command.Parameters.AddWithValue("$groschenCount", slot.DisplayData is null || slot.DisplayData.GroschenCount <= 0 ? DBNull.Value : slot.DisplayData.GroschenCount);
        command.Parameters.AddWithValue("$playerStatusEffects", slot.DisplayData?.PlayerStatusEffects.Count > 0 ? string.Join("|", slot.DisplayData.PlayerStatusEffects) : DBNull.Value);
        command.Parameters.AddWithValue("$saveType", (object?)slot.Type?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$gameVersion", (object?)slot.GameVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("$thumbnailPath", (object?)slot.ThumbnailPath ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static bool NeedsMetadataRefresh(SaveSlot slot) =>
        string.IsNullOrWhiteSpace(slot.GameSaveName)
        || slot.PlayTime is null
        || slot.LastSavedAtUtc is null
        || slot.DisplayData is null;

    private static bool HasAnyMetadata(KcdSaveMetadata metadata) =>
        metadata.GameSaveName is not null
        || metadata.PlayTime is not null
        || metadata.LastSavedAtUtc is not null
        || metadata.DisplayData is not null
        || metadata.Type is not null
        || metadata.GameVersion is not null
        || metadata.ThumbnailPath is not null;

    private static void EnsureColumn(SqliteConnection connection, string columnName, string definition)
    {
        using var check = connection.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM pragma_table_info('SaveSlots') WHERE name = $name;";
        check.Parameters.AddWithValue("$name", columnName);

        if (Convert.ToInt32(check.ExecuteScalar(), CultureInfo.InvariantCulture) > 0)
        {
            return;
        }

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE SaveSlots ADD COLUMN {columnName} {definition};";
        alter.ExecuteNonQuery();
    }

    private static DisplayInfo? CreateDisplayInfo(SqliteDataReader reader)
    {
        var effects = reader.IsDBNull(12)
            ? []
            : reader.GetString(12)
                .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();

        var displayInfo = new DisplayInfo
        {
            HenryLevel = reader.IsDBNull(8) ? 0 : reader.GetInt32(8),
            CurrentLocation = reader.IsDBNull(9) ? string.Empty : reader.GetString(9),
            ActiveQuest = reader.IsDBNull(10) ? string.Empty : reader.GetString(10),
            GroschenCount = reader.IsDBNull(11) ? 0 : reader.GetInt32(11),
            PlayerStatusEffects = effects
        };

        return displayInfo.HenryLevel > 0
            || displayInfo.GroschenCount > 0
            || !string.IsNullOrWhiteSpace(displayInfo.CurrentLocation)
            || !string.IsNullOrWhiteSpace(displayInfo.ActiveQuest)
            || displayInfo.PlayerStatusEffects.Count > 0
                ? displayInfo
                : null;
    }

    private SqliteConnection OpenConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = this.databasePath,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        };

        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        return connection;
    }

    private string GetSlotPath(string physicalName)
    {
        var full = NormalizeDirectoryPath(Path.Combine(this.vaultRoot, physicalName));
        var rootWithSlash = this.vaultRoot + Path.DirectorySeparatorChar;

        // [防御性编程] 物理槽位路径必须锁在 Vault 根目录内，避免恶意 PhysicalName 路径穿越。
        if (!full.StartsWith(rootWithSlash, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("为避免误删，BohemiX 只会处理自己的备份文件夹。");
        }

        return full;
    }

    private static string CreatePhysicalName(DateTimeOffset now)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return $"Slot_{now:yyyyMMdd_HHmmss}_{suffix}";
    }

    private static string ValidateDisplayName(string value)
    {
        var trimmed = value.Trim();

        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new ArgumentException("备份名称不能为空。", nameof(value));
        }

        if (trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || trimmed.Any(char.IsControl))
        {
            throw new ArgumentException("备份名称里有不能用于文件名的字符。", nameof(value));
        }

        return trimmed.Length <= MaxDisplayNameLength
            ? trimmed
            : throw new ArgumentException("备份名称太长了。", nameof(value));
    }

    private static void MoveEntry(string source, string destination)
    {
        if (Directory.Exists(source))
        {
            Directory.Move(source, destination);
        }
        else
        {
            File.Move(source, destination);
        }
    }

    private static ImportManifest BuildImportManifest(IReadOnlyList<string> entries)
    {
        var summaries = new Dictionary<string, ImportEntrySummary>(StringComparer.OrdinalIgnoreCase);
        var totalFiles = 0;
        var totalBytes = 0L;

        foreach (var entry in entries)
        {
            var summary = Directory.Exists(entry)
                ? SummarizeDirectory(entry)
                : new ImportEntrySummary(1, GetFileLengthSafe(entry));

            summaries[entry] = summary;
            totalFiles += summary.FileCount;
            totalBytes += summary.TotalBytes;
        }

        return new ImportManifest(summaries, Math.Max(totalFiles, entries.Count), totalBytes);
    }

    private static ImportEntrySummary SummarizeDirectory(string path)
    {
        var fileCount = 0;
        var totalBytes = 0L;

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            fileCount++;
            totalBytes += GetFileLengthSafe(file);
        }

        return new ImportEntrySummary(Math.Max(fileCount, 1), totalBytes);
    }

    private static long GetFileLengthSafe(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0L;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0L;
        }
    }

    private static void ReportImportProgress(
        IProgress<SaveImportProgress>? progress,
        string stage,
        int processedFiles,
        int totalFiles,
        long processedBytes,
        long totalBytes,
        string? currentEntry)
    {
        progress?.Report(new SaveImportProgress(
            stage,
            Math.Clamp(processedFiles, 0, Math.Max(totalFiles, processedFiles)),
            Math.Max(totalFiles, processedFiles),
            Math.Clamp(processedBytes, 0L, Math.Max(totalBytes, processedBytes)),
            Math.Max(totalBytes, processedBytes),
            currentEntry));
    }

    private static string NormalizeDirectoryPath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private sealed record ImportManifest(
        IReadOnlyDictionary<string, ImportEntrySummary> Entries,
        int TotalFiles,
        long TotalBytes);

    private sealed record ImportEntrySummary(int FileCount, long TotalBytes);
}
