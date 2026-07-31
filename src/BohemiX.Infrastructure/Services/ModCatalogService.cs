using System.Security.Cryptography;
using System.Text.Json;
using BohemiX.Core.Models;
using BohemiX.Core.Services;
using BohemiX.Infrastructure.Persistence;
using Dapper;
using Serilog;

namespace BohemiX.Infrastructure.Services;

public sealed class ModCatalogService : IModCatalogService
{
    private const string ManifestFileName = "bohemix.mod.json";
    private const string BuiltInTrackerModId = "bohemix-tracker";
    private const string DownloadDirectoryName = "downloads";
    private const string TransactionDirectoryName = ".transactions";

    private readonly IApplicationPathService applicationPathService;
    private readonly SqliteConnectionFactory connectionFactory;
    private readonly ILogger logger;

    public ModCatalogService(
        IApplicationPathService applicationPathService,
        SqliteConnectionFactory connectionFactory,
        ILogger logger)
    {
        this.applicationPathService = applicationPathService;
        this.connectionFactory = connectionFactory;
        this.logger = logger.ForContext<ModCatalogService>();
    }

    public async Task<IReadOnlyList<ModManifest>> LoadInstalledModsAsync(CancellationToken cancellationToken = default)
    {
        var modsDirectory = applicationPathService.GetPaths().ModsDirectory;
        Directory.CreateDirectory(modsDirectory);

        string[] modDirectories;
        try
        {
            modDirectories = Directory.GetDirectories(modsDirectory, "*", SearchOption.TopDirectoryOnly)
                .Where(path => !IsReservedModDirectory(path))
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.Error(ex, "Unable to enumerate mod directory {ModsDirectory}", modsDirectory);
            return [];
        }

        var mods = new List<ModManifest>();
        for (var index = 0; index < modDirectories.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var manifest = await TryScanModAsync(modDirectories[index], index, cancellationToken);
            if (manifest is not null)
            {
                mods.Add(manifest);
            }
        }

        var savedLoadOrder = await LoadSavedLoadOrderAsync(cancellationToken);
        var savedEnabledStates = await LoadSavedEnabledStatesAsync(cancellationToken);
        var orderedMods = ApplySavedEnabledStates(ApplySavedLoadOrder(mods, savedLoadOrder), savedEnabledStates)
            .Select(mod => IsBuiltInTracker(mod.Id) ? mod with { IsEnabled = true } : mod)
            .GroupBy(mod => mod.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(mod => mod.LoadOrder).First())
            .OrderBy(mod => mod.LoadOrder)
            .ThenBy(mod => mod.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        await SaveScannedModsAsync(orderedMods, cancellationToken);
        logger.Information("Loaded {ModCount} local mod manifests from {ModsDirectory}", orderedMods.Length, modsDirectory);
        return orderedMods;
    }

    private static bool IsReservedModDirectory(string path)
    {
        var name = Path.GetFileName(path);
        return string.Equals(name, TransactionDirectoryName, StringComparison.OrdinalIgnoreCase)
               || string.Equals(name, DownloadDirectoryName, StringComparison.OrdinalIgnoreCase);
    }

    public async Task SaveLoadOrderAsync(IReadOnlyList<string> orderedModIds, CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM mod_load_order",
            transaction: transaction,
            cancellationToken: cancellationToken));

        for (var index = 0; index < orderedModIds.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO mod_load_order (mod_id, load_order, updated_utc)
                VALUES (@ModId, @LoadOrder, @UpdatedUtc);

                UPDATE mod_manifests
                SET load_order = @LoadOrder
                WHERE id = @ModId;
                """,
                new
                {
                    ModId = NormalizeModId(orderedModIds[index]),
                    LoadOrder = index,
                    UpdatedUtc = DateTimeOffset.UtcNow.ToString("O")
                },
                transaction,
                cancellationToken: cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);
        logger.Information("Saved load order for {ModCount} mods", orderedModIds.Count);
    }

    public async Task SaveModEnabledStateAsync(string modId, bool isEnabled, CancellationToken cancellationToken = default)
    {
        await SaveModEnabledStatesAsync(
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                [modId] = isEnabled
            },
            cancellationToken);
    }

    public async Task SaveModEnabledStatesAsync(
        IReadOnlyDictionary<string, bool> enabledStates,
        CancellationToken cancellationToken = default)
    {
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        foreach (var state in enabledStates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var normalizedModId = NormalizeModId(state.Key);
            var isEnabled = IsBuiltInTracker(normalizedModId) || state.Value;
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO mod_states (mod_id, is_enabled, updated_utc)
                VALUES (@ModId, @IsEnabled, @UpdatedUtc)
                ON CONFLICT(mod_id) DO UPDATE SET
                    is_enabled = excluded.is_enabled,
                    updated_utc = excluded.updated_utc;

                UPDATE mod_manifests
                SET is_enabled = @IsEnabled
                WHERE id = @ModId;
                """,
                new
                {
                    ModId = normalizedModId,
                    IsEnabled = isEnabled ? 1 : 0,
                    UpdatedUtc = DateTimeOffset.UtcNow.ToString("O")
                },
                transaction,
                cancellationToken: cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);
        logger.Information(
            "Saved enabled states for {ModCount} mods",
            enabledStates.Count);
    }

    public async Task UpdateModMetadataAsync(
        ModManifest mod,
        string displayName,
        string version,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mod);
        if (string.IsNullOrWhiteSpace(displayName) || string.IsNullOrWhiteSpace(mod.RootPath))
        {
            return;
        }

        var manifestPath = Path.Combine(mod.RootPath, ManifestFileName);
        try
        {
            var existing = await TryReadManifestFileAsync(mod.RootPath, cancellationToken);
            var manifest = new ModManifestFile
            {
                Id = string.IsNullOrWhiteSpace(existing?.Id) ? mod.Id : existing.Id,
                DisplayName = displayName.Trim(),
                Version = string.IsNullOrWhiteSpace(version) ? mod.Version : version.Trim(),
                LoadOrder = existing?.LoadOrder,
                IsEnabled = existing?.IsEnabled ?? mod.IsEnabled,
                Source = existing?.Source ?? mod.Source
            };

            var temporaryPath = manifestPath + ".tmp";
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    manifest,
                    new JsonSerializerOptions { WriteIndented = true },
                    cancellationToken);
            }

            File.Move(temporaryPath, manifestPath, overwrite: true);

            await using var connection = connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE mod_manifests
                SET display_name = @DisplayName,
                    version = @Version
                WHERE id = @ModId
                """,
                new
                {
                    DisplayName = manifest.DisplayName,
                    Version = manifest.Version,
                    ModId = mod.Id
                },
                cancellationToken: cancellationToken));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or Microsoft.Data.Sqlite.SqliteException)
        {
            logger.Warning(ex, "Unable to update metadata for mod {ModId}", mod.Id);
        }
    }

    private async Task<IReadOnlyDictionary<string, int>> LoadSavedLoadOrderAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);

            var rows = await connection.QueryAsync<LoadOrderRow>(new CommandDefinition(
                "SELECT mod_id AS ModId, load_order AS LoadOrder FROM mod_load_order",
                cancellationToken: cancellationToken));

            return rows.ToDictionary(row => NormalizeModId(row.ModId), row => row.LoadOrder, StringComparer.OrdinalIgnoreCase);
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            logger.Warning(ex, "Unable to load saved mod load order");
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static IEnumerable<ModManifest> ApplySavedLoadOrder(
        IEnumerable<ModManifest> mods,
        IReadOnlyDictionary<string, int> savedLoadOrder)
    {
        var unknownBase = savedLoadOrder.Count;
        return mods.Select(mod =>
        {
            if (savedLoadOrder.TryGetValue(NormalizeModId(mod.Id), out var savedOrder))
            {
                return mod with { LoadOrder = savedOrder };
            }

            return mod with { LoadOrder = unknownBase + mod.LoadOrder };
        });
    }

    private static IEnumerable<ModManifest> ApplySavedEnabledStates(
        IEnumerable<ModManifest> mods,
        IReadOnlyDictionary<string, bool> savedEnabledStates)
    {
        return mods.Select(mod =>
        {
            if (savedEnabledStates.TryGetValue(NormalizeModId(mod.Id), out var isEnabled))
            {
                return mod with { IsEnabled = isEnabled };
            }

            return mod;
        });
    }

    private async Task<IReadOnlyDictionary<string, bool>> LoadSavedEnabledStatesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);

            var rows = await connection.QueryAsync<EnabledStateRow>(new CommandDefinition(
                "SELECT mod_id AS ModId, is_enabled AS IsEnabled FROM mod_states",
                cancellationToken: cancellationToken));

            return rows.ToDictionary(
                row => NormalizeModId(row.ModId),
                row => row.IsEnabled != 0,
                StringComparer.OrdinalIgnoreCase);
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            logger.Warning(ex, "Unable to load saved mod enabled states");
            return new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private async Task<ModManifest?> TryScanModAsync(string modRoot, int fallbackLoadOrder, CancellationToken cancellationToken)
    {
        try
        {
            var manifestFile = await TryReadManifestFileAsync(modRoot, cancellationToken);
            var folderName = Path.GetFileName(modRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var modId = NormalizeModId(manifestFile?.Id ?? folderName);
            var files = await ScanModFilesAsync(modRoot, cancellationToken);

            return new ModManifest(
                modId,
                string.IsNullOrWhiteSpace(manifestFile?.DisplayName) ? folderName : manifestFile.DisplayName!,
                string.IsNullOrWhiteSpace(manifestFile?.Version) ? "0.0.0-local" : manifestFile.Version!,
                modRoot,
                manifestFile?.LoadOrder ?? fallbackLoadOrder,
                manifestFile?.IsEnabled ?? true,
                files,
                manifestFile?.Source);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.Warning(ex, "Unable to scan mod at {ModRoot}", modRoot);
            return null;
        }
    }

    private static async Task<ModManifestFile?> TryReadManifestFileAsync(string modRoot, CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(modRoot, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        await using var stream = File.OpenRead(manifestPath);
        return await JsonSerializer.DeserializeAsync<ModManifestFile>(
            stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
            cancellationToken);
    }

    private async Task<IReadOnlyList<ModFileEntry>> ScanModFilesAsync(string modRoot, CancellationToken cancellationToken)
    {
        var files = new List<ModFileEntry>();
        string[] physicalFiles;

        try
        {
            physicalFiles = Directory.GetFiles(modRoot, "*", SearchOption.AllDirectories);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.Warning(ex, "Unable to enumerate files under mod root {ModRoot}", modRoot);
            return files;
        }

        foreach (var physicalFile in physicalFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.Equals(Path.GetFileName(physicalFile), ManifestFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var entry = await TryCreateFileEntryAsync(modRoot, physicalFile, cancellationToken);
            if (entry is not null)
            {
                files.Add(entry);
            }
        }

        return files
            .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<ModFileEntry?> TryCreateFileEntryAsync(
        string modRoot,
        string physicalFile,
        CancellationToken cancellationToken)
    {
        try
        {
            var fileInfo = new FileInfo(physicalFile);
            await using var stream = File.Open(physicalFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var hash = await SHA256.HashDataAsync(stream, cancellationToken);

            return new ModFileEntry(
                Path.GetRelativePath(modRoot, physicalFile).Replace('\\', '/'),
                fileInfo.Length,
                Convert.ToHexString(hash));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.Warning(ex, "Unable to hash mod file {PhysicalFile}", physicalFile);
            return null;
        }
    }

    private async Task SaveScannedModsAsync(IReadOnlyList<ModManifest> mods, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM mod_files",
                transaction: transaction,
                cancellationToken: cancellationToken));
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM mod_manifests",
                transaction: transaction,
                cancellationToken: cancellationToken));

            foreach (var mod in mods)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO mod_manifests (
                        id,
                        display_name,
                        version,
                        root_path,
                        load_order,
                        is_enabled,
                        last_scanned_utc
                    )
                    VALUES (
                        @Id,
                        @DisplayName,
                        @Version,
                        @RootPath,
                        @LoadOrder,
                        @IsEnabled,
                        @LastScannedUtc
                    )
                    """,
                    new
                    {
                        mod.Id,
                        mod.DisplayName,
                        mod.Version,
                        mod.RootPath,
                        mod.LoadOrder,
                        IsEnabled = mod.IsEnabled ? 1 : 0,
                        LastScannedUtc = DateTimeOffset.UtcNow.ToString("O")
                    },
                    transaction,
                    cancellationToken: cancellationToken));

                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO mod_states (mod_id, is_enabled, updated_utc)
                    VALUES (@ModId, @IsEnabled, @UpdatedUtc)
                    ON CONFLICT(mod_id) DO UPDATE SET
                        is_enabled = excluded.is_enabled,
                        updated_utc = excluded.updated_utc
                    """,
                    new
                    {
                        ModId = mod.Id,
                        IsEnabled = mod.IsEnabled ? 1 : 0,
                        UpdatedUtc = DateTimeOffset.UtcNow.ToString("O")
                    },
                    transaction,
                    cancellationToken: cancellationToken));

                foreach (var file in mod.Files)
                {
                    await connection.ExecuteAsync(new CommandDefinition(
                        """
                        INSERT INTO mod_files (
                            mod_id,
                            relative_path,
                            size_bytes,
                            content_hash
                        )
                        VALUES (
                            @ModId,
                            @RelativePath,
                            @SizeInBytes,
                            @ContentHash
                        )
                        """,
                        new
                        {
                            ModId = mod.Id,
                            file.RelativePath,
                            file.SizeInBytes,
                            file.ContentHash
                        },
                        transaction,
                        cancellationToken: cancellationToken));
                }
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            logger.Warning(ex, "Unable to persist scanned mod catalog");
        }
    }

    private static string NormalizeModId(string value)
    {
        var id = new string(value
            .Trim()
            .Select(character => char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : '-')
            .ToArray());

        return string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id;
    }

    private static bool IsBuiltInTracker(string modId) =>
        string.Equals(NormalizeModId(modId), BuiltInTrackerModId, StringComparison.OrdinalIgnoreCase);

    private sealed class ModManifestFile
    {
        public string? Id { get; init; }

        public string? DisplayName { get; init; }

        public string? Version { get; init; }

        public int? LoadOrder { get; init; }

        public bool? IsEnabled { get; init; }

        public ModPackageSourceMetadata? Source { get; init; }
    }

    private sealed class LoadOrderRow
    {
        public string ModId { get; init; } = string.Empty;

        public int LoadOrder { get; init; }
    }

    private sealed class EnabledStateRow
    {
        public string ModId { get; init; } = string.Empty;

        public int IsEnabled { get; init; }
    }
}
