using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BohemiX.Core.Models;
using BohemiX.Core.Models.Saves;
using BohemiX.Core.Services;
using BohemiX.Core.Services.Saves;
using BohemiX.Infrastructure.Persistence;
using Dapper;
using Microsoft.Data.Sqlite;
using Serilog;

namespace BohemiX.Infrastructure.Services.Saves;

/// <summary>
/// Canonical implementation for writable save profiles and immutable snapshots.
/// Existing SaveSlots rows are upgraded in place and remain compatible with ISlotManager.
/// </summary>
public sealed partial class SaveProfileService : ISaveProfileService, ISaveRestoreValidationService, IDisposable
{
    private const int PackageSchemaVersion = 1;
    private const int MaxDisplayNameLength = 80;
    private static readonly SaveSnapshotTrigger[] AutomaticTriggers =
        [SaveSnapshotTrigger.BeforeSwitch, SaveSnapshotTrigger.GameExit, SaveSnapshotTrigger.BeforeRestore];

    private readonly IApplicationPathService applicationPathService;
    private readonly IAppSettingsService settingsService;
    private readonly SqliteConnectionFactory connectionFactory;
    private readonly ISlotManagerFactory slotManagerFactory;
    private readonly IProcessCoordinatorFactory coordinatorFactory;
    private readonly IJunctionRouter junctionRouter;
    private readonly IKcdSaveParser parser;
    private readonly ILogger logger;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly List<SaveMigrationWarning> migrationWarnings = [];
    private readonly CancellationTokenSource maintenanceCancellation = new();

    private AppSettings? settings;
    private IProcessCoordinator? coordinator;
    private ChunkedSnapshotStore? snapshotStore;
    private CancellationTokenSource? maintenancePassCancellation;
    private Task? maintenanceTask;
    private SaveSnapshotMaintenanceState maintenanceState = SaveSnapshotMaintenanceState.Ready;
    private bool initialized;
    private Guid initializedEnvironmentId;
    private int disposed;

    public SaveProfileService(
        IApplicationPathService applicationPathService,
        IAppSettingsService settingsService,
        SqliteConnectionFactory connectionFactory,
        ISlotManagerFactory slotManagerFactory,
        IProcessCoordinatorFactory coordinatorFactory,
        IJunctionRouter junctionRouter,
        IKcdSaveParser parser,
        ILogger logger)
    {
        this.applicationPathService = applicationPathService;
        this.settingsService = settingsService;
        this.connectionFactory = connectionFactory;
        this.slotManagerFactory = slotManagerFactory;
        this.coordinatorFactory = coordinatorFactory;
        this.junctionRouter = junctionRouter;
        this.parser = parser;
        this.logger = logger.ForContext<SaveProfileService>();
    }

    public IReadOnlyList<SaveMigrationWarning> MigrationWarnings
    {
        get
        {
            lock (migrationWarnings)
            {
                return migrationWarnings.ToList();
            }
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var requestedEnvironmentId = applicationPathService.GetPaths().GameEnvironmentId;
        if (initialized && initializedEnvironmentId == requestedEnvironmentId)
        {
            return;
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (initialized && initializedEnvironmentId == requestedEnvironmentId)
            {
                return;
            }

            if (initialized)
            {
                Volatile.Read(ref maintenancePassCancellation)?.Cancel();
                if (coordinator is IDisposable disposableCoordinator)
                {
                    disposableCoordinator.Dispose();
                }

                settings = null;
                coordinator = null;
                snapshotStore = null;
                lock (migrationWarnings)
                {
                    migrationWarnings.Clear();
                }
                initialized = false;
            }

            settings = await settingsService.LoadAsync(cancellationToken).ConfigureAwait(false);
            coordinator = coordinatorFactory.Create(GetOfficialSavePath());
            Directory.CreateDirectory(GetVaultRootPath());
            Directory.CreateDirectory(GetSnapshotsRootPath());
            Directory.CreateDirectory(GetStagingRootPath());
            snapshotStore = new ChunkedSnapshotStore(GetSnapshotsRootPath(), GetStagingRootPath());
            snapshotStore.EnsureDirectories();

            await EnsureDatabaseAsync(cancellationToken).ConfigureAwait(false);
            await MigrateHiddenSaveManagerAsync(cancellationToken).ConfigureAwait(false);
            await ReconcileSnapshotStorageAsync(cancellationToken).ConfigureAwait(false);
            initialized = true;
            initializedEnvironmentId = requestedEnvironmentId;
            maintenanceTask ??= Task.Run(() => RunMaintenanceLoopAsync(maintenanceCancellation.Token), CancellationToken.None);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<SaveProfile>> GetProfilesAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var activeTarget = GetActiveTarget();

        await using var connection = connectionFactory.CreateConnection();
        var rows = (await connection.QueryAsync<ProfileRow>(new CommandDefinition(
            ProfileSelectSql + " ORDER BY COALESCE(LastActivatedAtUtc, UpdatedAtUtc) DESC, DisplayName COLLATE NOCASE;",
            cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

        if (await BackfillMissingCharacterDataAsync(rows, activeTarget, cancellationToken).ConfigureAwait(false))
        {
            rows = (await connection.QueryAsync<ProfileRow>(new CommandDefinition(
                ProfileSelectSql + " ORDER BY COALESCE(LastActivatedAtUtc, UpdatedAtUtc) DESC, DisplayName COLLATE NOCASE;",
                cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();
        }

        return rows.Select(row => ToProfile(row, activeTarget)).ToList();
    }

    public async Task<IReadOnlyList<SaveSnapshot>> GetSnapshotsAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = connectionFactory.CreateConnection();
        var rows = await connection.QueryAsync<SnapshotRow>(new CommandDefinition(
            SnapshotSelectSql + " WHERE ProfileId = @ProfileId ORDER BY CreatedAtUtc DESC;",
            new { ProfileId = profileId.ToString("D") },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.Select(ToSnapshot).ToList();
    }

    public async Task<SaveSnapshotStorageStats> GetSnapshotStorageStatsAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = connectionFactory.CreateConnection();
        var rows = (await connection.QueryAsync<SnapshotStorageRow>(new CommandDefinition(
            "SELECT TotalBytes, StorageFormat FROM SaveSnapshots;",
            cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();
        var logicalBytes = rows.Sum(row => row.TotalBytes);
        var legacyRows = rows.Where(row => ParseStorageKind(row.StorageFormat) == SaveSnapshotStorageKind.LegacyDirectory).ToList();
        var physicalBytes = Directory.Exists(GetSnapshotsRootPath())
            ? Directory.EnumerateFiles(GetSnapshotsRootPath(), "*", SearchOption.AllDirectories).Sum(GetFileLength)
            : 0;
        return new SaveSnapshotStorageStats(
            logicalBytes,
            physicalBytes,
            legacyRows.Sum(row => row.TotalBytes),
            legacyRows.Count,
            legacyRows.Count == 0 ? SaveSnapshotMaintenanceState.Complete : maintenanceState);
    }

    public async Task<SaveProfile?> GetActiveProfileAsync(CancellationToken cancellationToken = default)
    {
        var profiles = await GetProfilesAsync(cancellationToken).ConfigureAwait(false);
        return profiles.FirstOrDefault(profile => profile.IsActive);
    }

    public async Task<bool> HasUnmanagedSaveAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var path = GetOfficialSavePath();
        cancellationToken.ThrowIfCancellationRequested();
        return junctionRouter.TryGetJunctionInfo(path) is null
            && Directory.Exists(path)
            && Directory.EnumerateFileSystemEntries(path).Any();
    }

    public async Task<bool> IsSafeToChangeAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await coordinator!.IsSafeToSwitch(cancellationToken).ConfigureAwait(false);
    }

    public async Task<SaveProfile> ManageCurrentSaveAsync(
        string? displayName = null,
        IProgress<SaveImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await EnterForegroundGateAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (junctionRouter.TryGetJunctionInfo(GetOfficialSavePath()) is not null)
            {
                throw new InvalidOperationException("当前存档已经由 BohemiX 管理。");
            }

            var manager = slotManagerFactory.Create(GetOfficialSavePath(), GetVaultRootPath());
            var slot = await manager.ImportSave(displayName, progress, cancellationToken).ConfigureAwait(false);
            var fingerprint = await ComputeDirectoryFingerprintAsync(slot.PhysicalPath, cancellationToken).ConfigureAwait(false);
            var metadata = await EnrichCharacterMetadataAsync(
                slot.PhysicalPath,
                new KcdSaveMetadata(slot.PlayTime, slot.LastSavedAtUtc, slot.GameSaveName, slot.DisplayData, slot.Type, slot.GameVersion, slot.ThumbnailPath),
                cancellationToken).ConfigureAwait(false);
            await UpdateProfileMetadataAsync(slot.Id, fingerprint, metadata, cancellationToken).ConfigureAwait(false);

            junctionRouter.MountJunction(GetOfficialSavePath(), slot.PhysicalPath);
            await MarkActivatedAsync(slot.Id, cancellationToken).ConfigureAwait(false);
            return (await GetProfileByIdAsync(slot.Id, cancellationToken).ConfigureAwait(false))! with { IsActive = true };
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<SaveProfile> CloneProfileAsync(
        Guid sourceProfileId,
        string displayName,
        IProgress<SaveImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await EnterForegroundGateAsync(cancellationToken).ConfigureAwait(false);
        string? destination = null;
        try
        {
            var source = await RequireProfileAsync(sourceProfileId, cancellationToken).ConfigureAwait(false);
            var name = ValidateDisplayName(displayName);
            var id = Guid.NewGuid();
            var physicalName = CreatePhysicalName("Profile", id);
            destination = Path.Combine(GetVaultRootPath(), physicalName);
            await CopyDirectoryAsync(source.PhysicalPath, destination, progress, "Cloning", cancellationToken).ConfigureAwait(false);
            var fingerprint = await ComputeDirectoryFingerprintAsync(destination, cancellationToken).ConfigureAwait(false);
            var metadata = await EnrichCharacterMetadataAsync(destination, parser.ParseMetadata(destination), cancellationToken).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var profile = new SaveProfile(
                id, physicalName, name, destination, now, now, false, null, fingerprint,
                metadata.GameSaveName, metadata.PlayTime, metadata.LastSavedAtUtc, metadata.DisplayData,
                metadata.Type, metadata.GameVersion, metadata.ThumbnailPath);
            await InsertProfileAsync(profile, cancellationToken).ConfigureAwait(false);
            return profile;
        }
        catch
        {
            if (destination is not null && Directory.Exists(destination))
            {
                DeleteManagedDirectory(destination, GetVaultRootPath());
            }

            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SwitchProfileAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await EnterForegroundGateAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!await coordinator!.IsSafeToSwitch(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("游戏正在运行或存档仍在写入，请稍后再切换。");
            }

            var selected = await RequireProfileAsync(profileId, cancellationToken).ConfigureAwait(false);
            var active = await GetActiveProfileCoreAsync(cancellationToken).ConfigureAwait(false);
            if (active?.Id == selected.Id)
            {
                return;
            }

            if (active is not null && settings!.AutoBackupSaveNodes && settings.AutoSnapshotBeforeSwitch)
            {
                // A completed backup-library reconciliation is required before switching away.
                await ReconcileBackupNodesCoreAsync(active, cancellationToken).ConfigureAwait(false);
            }

            if (active is null && await HasUnmanagedSaveCoreAsync().ConfigureAwait(false))
            {
                throw new InvalidOperationException("官方存档目录中仍有未纳管文件，请先将当前存档纳入管理。");
            }

            junctionRouter.MountJunction(GetOfficialSavePath(), selected.PhysicalPath);
            await MarkActivatedAsync(selected.Id, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<SaveSnapshot?> CreateSnapshotAsync(
        Guid profileId,
        SaveSnapshotTrigger trigger = SaveSnapshotTrigger.Manual,
        string? note = null,
        IProgress<SaveImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await EnterForegroundGateAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var profile = await RequireProfileAsync(profileId, cancellationToken).ConfigureAwait(false);
            return await CreateSnapshotCoreAsync(profile, trigger, note, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task RestoreSnapshotAsync(
        Guid snapshotId,
        IProgress<SaveImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await EnterForegroundGateAsync(cancellationToken).ConfigureAwait(false);
        string? staging = null;
        string? rollback = null;
        var activeWasUnmounted = false;
        var originalMoved = false;
        var swapped = false;
        SaveProfile? profile = null;

        try
        {
            if (!await coordinator!.IsSafeToSwitch(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("游戏正在运行或存档仍在写入，无法恢复快照。");
            }

            var snapshot = await RequireSnapshotAsync(snapshotId, cancellationToken).ConfigureAwait(false);
            profile = await RequireProfileAsync(snapshot.ProfileId, cancellationToken).ConfigureAwait(false);
            var active = await GetActiveProfileCoreAsync(cancellationToken).ConfigureAwait(false);

            staging = Path.Combine(GetStagingRootPath(), $"restore-{Guid.NewGuid():N}");
            var replacement = Path.Combine(staging, "replacement");
            rollback = Path.Combine(staging, "rollback");
            Directory.CreateDirectory(staging);
            if (snapshot.StorageKind == SaveSnapshotStorageKind.ChunkedManifest)
            {
                if (snapshot.ManifestRelativePath is null)
                {
                    throw new InvalidDataException("增量快照缺少清单路径。");
                }

                await snapshotStore!.MaterializeAsync(
                    snapshot.ManifestRelativePath, replacement, progress, "Restoring", cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await CopyDirectoryAsync(snapshot.PhysicalPath, replacement, progress, "Restoring", cancellationToken).ConfigureAwait(false);
            }
            var replacementFingerprint = await ComputeDirectoryFingerprintAsync(replacement, cancellationToken).ConfigureAwait(false);
            if (!StringComparer.OrdinalIgnoreCase.Equals(replacementFingerprint, snapshot.ContentFingerprint))
            {
                throw new InvalidDataException("快照校验失败，档案未被修改。");
            }

            // Only create a safety point after the restore source itself has passed full verification.
            await CreateSnapshotCoreAsync(profile, SaveSnapshotTrigger.BeforeRestore, "恢复前安全快照", progress, cancellationToken).ConfigureAwait(false);

            if (active?.Id == profile.Id)
            {
                junctionRouter.UnmountJunction(GetOfficialSavePath());
                activeWasUnmounted = true;
            }

            Directory.Move(profile.PhysicalPath, rollback);
            originalMoved = true;
            Directory.Move(replacement, profile.PhysicalPath);
            swapped = true;

            var committedFingerprint = await ComputeDirectoryFingerprintAsync(profile.PhysicalPath, cancellationToken).ConfigureAwait(false);
            if (!StringComparer.OrdinalIgnoreCase.Equals(committedFingerprint, replacementFingerprint))
            {
                throw new InvalidDataException("恢复后的存档与已验证快照不一致，已开始自动回滚。");
            }

            if (activeWasUnmounted)
            {
                junctionRouter.MountJunction(GetOfficialSavePath(), profile.PhysicalPath);
                var mounted = junctionRouter.TryGetJunctionInfo(GetOfficialSavePath());
                if (mounted is null
                    || !StringComparer.OrdinalIgnoreCase.Equals(
                        Path.TrimEndingDirectorySeparator(Path.GetFullPath(mounted.TargetPath)),
                        Path.TrimEndingDirectorySeparator(Path.GetFullPath(profile.PhysicalPath))))
                {
                    throw new IOException("恢复后的官方存档入口未正确指向目标档案，已开始自动回滚。");
                }

                activeWasUnmounted = false;
            }

            var metadata = await EnrichCharacterMetadataAsync(
                profile.PhysicalPath,
                parser.ParseMetadata(profile.PhysicalPath),
                cancellationToken).ConfigureAwait(false);
            await UpdateProfileMetadataAsync(profile.Id, replacementFingerprint, metadata, cancellationToken).ConfigureAwait(false);
            DeleteManagedDirectory(rollback, GetStagingRootPath());
            rollback = null;
        }
        catch
        {
            if (swapped && profile is not null && rollback is not null && Directory.Exists(rollback))
            {
                if (Directory.Exists(profile.PhysicalPath))
                {
                    var failed = Path.Combine(GetStagingRootPath(), $"failed-restore-{Guid.NewGuid():N}");
                    Directory.Move(profile.PhysicalPath, failed);
                    Directory.Move(rollback, profile.PhysicalPath);
                    DeleteManagedDirectory(failed, GetStagingRootPath());
                    rollback = null;
                }
            }
            else if (originalMoved && profile is not null && rollback is not null && Directory.Exists(rollback) && !Directory.Exists(profile.PhysicalPath))
            {
                Directory.Move(rollback, profile.PhysicalPath);
                rollback = null;
            }

            if (activeWasUnmounted && profile is not null)
            {
                junctionRouter.MountJunction(GetOfficialSavePath(), profile.PhysicalPath);
            }

            throw;
        }
        finally
        {
            if (staging is not null && Directory.Exists(staging))
            {
                DeleteManagedDirectory(staging, GetStagingRootPath());
            }

            gate.Release();
        }
    }

    public async Task RenameProfileAsync(Guid profileId, string displayName, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = connectionFactory.CreateConnection();
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE SaveSlots SET DisplayName = @Name, UpdatedAtUtc = @Now WHERE Id = @Id;",
            new { Id = profileId.ToString("D"), Name = ValidateDisplayName(displayName), Now = FormatDate(DateTimeOffset.UtcNow) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (affected == 0)
        {
            throw new KeyNotFoundException("找不到这个档案。");
        }
    }

    public async Task SetFavoriteAsync(Guid profileId, bool isFavorite, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = connectionFactory.CreateConnection();
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE SaveSlots SET IsFavorite = @Value, UpdatedAtUtc = @Now WHERE Id = @Id;",
            new { Id = profileId.ToString("D"), Value = isFavorite ? 1 : 0, Now = FormatDate(DateTimeOffset.UtcNow) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (affected == 0)
        {
            throw new KeyNotFoundException("找不到这个档案。");
        }
    }

    public async Task DeleteProfileAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await EnterForegroundGateAsync(cancellationToken).ConfigureAwait(false);
        string? profileTrash = null;
        string? snapshotsTrash = null;
        string? manifestsTrash = null;
        string? nodeManifestsTrash = null;
        SaveProfile? profile = null;
        try
        {
            profile = await RequireProfileAsync(profileId, cancellationToken).ConfigureAwait(false);
            if ((await GetActiveProfileCoreAsync(cancellationToken).ConfigureAwait(false))?.Id == profileId)
            {
                throw new InvalidOperationException("活动档案不可删除，请先切换到其他档案。");
            }

            var trashRoot = Path.Combine(GetStagingRootPath(), $"delete-{Guid.NewGuid():N}");
            Directory.CreateDirectory(trashRoot);
            profileTrash = Path.Combine(trashRoot, "profile");
            if (Directory.Exists(profile.PhysicalPath))
            {
                EnsureInsideRoot(profile.PhysicalPath, GetVaultRootPath());
                Directory.Move(profile.PhysicalPath, profileTrash);
            }

            var snapshotRoot = Path.Combine(GetSnapshotsRootPath(), profileId.ToString("D"));
            snapshotsTrash = Path.Combine(trashRoot, "snapshots");
            if (Directory.Exists(snapshotRoot))
            {
                EnsureInsideRoot(snapshotRoot, GetSnapshotsRootPath());
                Directory.Move(snapshotRoot, snapshotsTrash);
            }

            var manifestRoot = Path.Combine(GetSnapshotsRootPath(), "manifests", profileId.ToString("D"));
            manifestsTrash = Path.Combine(trashRoot, "manifests");
            if (Directory.Exists(manifestRoot))
            {
                EnsureInsideRoot(manifestRoot, Path.Combine(GetSnapshotsRootPath(), "manifests"));
                Directory.Move(manifestRoot, manifestsTrash);
            }

            var nodeManifestRoot = Path.Combine(GetSnapshotsRootPath(), "manifests", "nodes", profileId.ToString("D"));
            nodeManifestsTrash = Path.Combine(trashRoot, "node-manifests");
            if (Directory.Exists(nodeManifestRoot))
            {
                EnsureInsideRoot(nodeManifestRoot, Path.Combine(GetSnapshotsRootPath(), "manifests", "nodes"));
                Directory.Move(nodeManifestRoot, nodeManifestsTrash);
            }

            await using var connection = connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await connection.ExecuteAsync(new CommandDefinition(
                """
                DELETE FROM SaveBackupFileObservations WHERE ProfileId = @Id;
                DELETE FROM SaveBackupLibraryStates WHERE ProfileId = @Id;
                DELETE FROM SaveBackupNodes WHERE ProfileId = @Id;
                DELETE FROM SaveSnapshots WHERE ProfileId = @Id;
                DELETE FROM SaveSlots WHERE Id = @Id;
                """,
                new { Id = profileId.ToString("D") }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            profileTrash = snapshotsTrash = manifestsTrash = nodeManifestsTrash = null;
            try
            {
                DeleteManagedDirectory(trashRoot, GetStagingRootPath());
            }
            catch (Exception cleanupError)
            {
                logger.Warning(cleanupError, "Profile {ProfileId} was deleted but staging cleanup was deferred", profileId);
            }
            await CollectSnapshotGarbageSafelyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (profileTrash is not null && Directory.Exists(profileTrash))
            {
                if (profile is not null && !Directory.Exists(profile.PhysicalPath))
                {
                    Directory.Move(profileTrash, profile.PhysicalPath);
                }
            }

            if (snapshotsTrash is not null && Directory.Exists(snapshotsTrash))
            {
                var original = Path.Combine(GetSnapshotsRootPath(), profileId.ToString("D"));
                if (!Directory.Exists(original))
                {
                    Directory.Move(snapshotsTrash, original);
                }
            }

            if (manifestsTrash is not null && Directory.Exists(manifestsTrash))
            {
                var original = Path.Combine(GetSnapshotsRootPath(), "manifests", profileId.ToString("D"));
                if (!Directory.Exists(original))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(original)!);
                    Directory.Move(manifestsTrash, original);
                }
            }

            if (nodeManifestsTrash is not null && Directory.Exists(nodeManifestsTrash))
            {
                var original = Path.Combine(GetSnapshotsRootPath(), "manifests", "nodes", profileId.ToString("D"));
                if (!Directory.Exists(original))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(original)!);
                    Directory.Move(nodeManifestsTrash, original);
                }
            }

            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task DeleteSnapshotAsync(Guid snapshotId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await EnterForegroundGateAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await DeleteSnapshotCoreAsync(snapshotId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task DeleteSnapshotCoreAsync(Guid snapshotId, CancellationToken cancellationToken)
    {
        string? trash = null;
        SaveSnapshot? snapshot = null;
        var movedFile = false;
        try
        {
            snapshot = await RequireSnapshotAsync(snapshotId, cancellationToken).ConfigureAwait(false);
            if (snapshot.StorageKind == SaveSnapshotStorageKind.ChunkedManifest && File.Exists(snapshot.PhysicalPath))
            {
                trash = Path.Combine(GetStagingRootPath(), $"delete-snapshot-{Guid.NewGuid():N}.json");
                File.Move(snapshot.PhysicalPath, trash);
                movedFile = true;
            }
            else if (snapshot.StorageKind == SaveSnapshotStorageKind.LegacyDirectory && Directory.Exists(snapshot.PhysicalPath))
            {
                trash = Path.Combine(GetStagingRootPath(), $"delete-snapshot-{Guid.NewGuid():N}");
                EnsureInsideRoot(snapshot.PhysicalPath, GetSnapshotsRootPath());
                Directory.Move(snapshot.PhysicalPath, trash);
            }

            await using var connection = connectionFactory.CreateConnection();
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM SaveSnapshots WHERE Id = @Id;",
                new { Id = snapshotId.ToString("D") }, cancellationToken: cancellationToken)).ConfigureAwait(false);
            if (trash is not null)
            {
                var committedTrash = trash;
                trash = null;
                try
                {
                    if (movedFile)
                    {
                        File.Delete(committedTrash);
                    }
                    else
                    {
                        DeleteManagedDirectory(committedTrash, GetStagingRootPath());
                    }
                }
                catch (Exception cleanupError)
                {
                    logger.Warning(cleanupError, "Snapshot {SnapshotId} was deleted but staging cleanup was deferred", snapshotId);
                }
            }

            await CollectSnapshotGarbageSafelyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (trash is not null && snapshot is not null && movedFile && File.Exists(trash) && !File.Exists(snapshot.PhysicalPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(snapshot.PhysicalPath)!);
                File.Move(trash, snapshot.PhysicalPath);
            }
            else if (trash is not null && snapshot is not null && Directory.Exists(trash) && !Directory.Exists(snapshot.PhysicalPath))
            {
                Directory.Move(trash, snapshot.PhysicalPath);
            }

            throw;
        }
    }

    public async Task ExportProfileAsync(
        Guid profileId,
        string destinationZipPath,
        SavePackageExportOptions? options = null,
        IProgress<SaveImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await EnterForegroundGateAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ExportProfileCoreAsync(
                profileId, destinationZipPath, options, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task ExportProfileCoreAsync(
        Guid profileId,
        string destinationZipPath,
        SavePackageExportOptions? options,
        IProgress<SaveImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        options ??= new SavePackageExportOptions();
        var profile = await RequireProfileAsync(profileId, cancellationToken).ConfigureAwait(false);
        var snapshots = options.IncludeSnapshots
            ? await GetSnapshotsAsync(profileId, cancellationToken).ConfigureAwait(false)
            : [];

        var directFiles = new List<PackageFile>();
        var chunkedSources = new List<ChunkArchiveSource>();
        var packageFiles = new List<PackageFile>();
        AddPackageFiles(directFiles, profile.PhysicalPath, "profile");
        foreach (var snapshot in snapshots)
        {
            var prefix = $"snapshots/{snapshot.Id:D}";
            if (snapshot.StorageKind == SaveSnapshotStorageKind.ChunkedManifest)
            {
                var manifestPath = snapshot.ManifestRelativePath
                    ?? throw new InvalidDataException("增量快照缺少清单路径。");
                chunkedSources.Add(new ChunkArchiveSource(manifestPath, prefix));
                var chunkFiles = await snapshotStore!.GetPackageFilesAsync(manifestPath, prefix, cancellationToken).ConfigureAwait(false);
                packageFiles.AddRange(chunkFiles.Select(file => new PackageFile(file.Path, file.Sha256, file.Length)));
            }
            else
            {
                AddPackageFiles(directFiles, snapshot.PhysicalPath, prefix);
            }
        }

        for (var i = 0; i < directFiles.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            directFiles[i] = directFiles[i] with { Sha256 = await ComputeFileHashAsync(directFiles[i].SourcePath!, cancellationToken).ConfigureAwait(false) };
        }
        packageFiles.InsertRange(0, directFiles.Select(file => file with { SourcePath = null }));

        var manifest = new PackageManifest(
            PackageSchemaVersion,
            new PackageProfile(profile.DisplayName, profile.IsFavorite, profile.CreatedAtUtc),
            snapshots.Select(s => new PackageSnapshot(s.Id, s.Note, s.Trigger, s.CreatedAtUtc)).ToList(),
            packageFiles);
        await WritePackageAsync(
            destinationZipPath, manifest, directFiles, chunkedSources, progress, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SaveProfile> ImportPackageAsync(
        string packagePath,
        IProgress<SaveImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await EnterForegroundGateAsync(cancellationToken).ConfigureAwait(false);
        var staging = Path.Combine(GetStagingRootPath(), $"import-{Guid.NewGuid():N}");
        string? finalProfilePath = null;
        var finalSnapshotManifests = new List<string>();
        try
        {
            Directory.CreateDirectory(staging);
            await using var stream = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            var manifestEntry = archive.GetEntry("manifest.json") ?? throw new InvalidDataException("存档包缺少 manifest.json。");
            PackageManifest manifest;
            await using (var manifestStream = manifestEntry.Open())
            {
                manifest = await JsonSerializer.DeserializeAsync<PackageManifest>(manifestStream, cancellationToken: cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("存档包清单无效。");
            }

            if (manifest.SchemaVersion != PackageSchemaVersion)
            {
                throw new InvalidDataException($"不支持的存档包版本：{manifest.SchemaVersion}。");
            }

            var declared = manifest.Files.ToDictionary(file => NormalizePackagePath(file.Path), StringComparer.OrdinalIgnoreCase);
            var payloadEntries = archive.Entries.Where(entry => !string.Equals(entry.FullName, "manifest.json", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(entry.Name)).ToList();
            if (payloadEntries.Count != declared.Count)
            {
                throw new InvalidDataException("存档包文件清单与实际内容不一致。");
            }

            long processedBytes = 0;
            var totalBytes = manifest.Files.Sum(file => file.Length);
            for (var i = 0; i < payloadEntries.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = payloadEntries[i];
                var packagePathKey = NormalizePackagePath(entry.FullName);
                if (!declared.TryGetValue(packagePathKey, out var file))
                {
                    throw new InvalidDataException($"存档包包含未声明文件：{entry.FullName}");
                }

                var destination = GetSafeExtractionPath(staging, packagePathKey);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await using (var source = entry.Open())
                await using (var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                {
                    await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
                }

                var actualHash = await ComputeFileHashAsync(destination, cancellationToken).ConfigureAwait(false);
                if (!StringComparer.OrdinalIgnoreCase.Equals(actualHash, file.Sha256))
                {
                    throw new InvalidDataException($"文件哈希校验失败：{entry.FullName}");
                }

                processedBytes += file.Length;
                progress?.Report(new SaveImportProgress("Importing", i + 1, payloadEntries.Count, processedBytes, totalBytes, entry.FullName));
            }

            var stagedProfile = Path.Combine(staging, "profile");
            if (!Directory.Exists(stagedProfile) || !Directory.EnumerateFileSystemEntries(stagedProfile).Any())
            {
                throw new InvalidDataException("存档包不包含档案当前状态。");
            }

            var id = Guid.NewGuid();
            var physicalName = CreatePhysicalName("Profile", id);
            finalProfilePath = Path.Combine(GetVaultRootPath(), physicalName);
            var displayName = await CreateUniqueDisplayNameAsync(manifest.Profile.DisplayName, cancellationToken).ConfigureAwait(false);
            var metadata = parser.ParseMetadata(stagedProfile);
            var fingerprint = await ComputeDirectoryFingerprintAsync(stagedProfile, cancellationToken).ConfigureAwait(false);
            Directory.Move(stagedProfile, finalProfilePath);

            var now = DateTimeOffset.UtcNow;
            var profile = new SaveProfile(
                id, physicalName, displayName, finalProfilePath, now, now, manifest.Profile.IsFavorite, null,
                fingerprint, metadata.GameSaveName, metadata.PlayTime, metadata.LastSavedAtUtc,
                metadata.DisplayData, metadata.Type, metadata.GameVersion, metadata.ThumbnailPath);

            await using var connection = connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await InsertProfileAsync(connection, transaction, profile, cancellationToken).ConfigureAwait(false);

            foreach (var packageSnapshot in manifest.Snapshots)
            {
                var source = Path.Combine(staging, "snapshots", packageSnapshot.Id.ToString("D"));
                if (!Directory.Exists(source))
                {
                    throw new InvalidDataException($"存档包缺少快照 {packageSnapshot.Id:D}。");
                }

                var snapshotId = Guid.NewGuid();
                var physicalSnapshotName = snapshotId.ToString("D");
                var snapshotMetadata = parser.ParseMetadata(source);
                var sourceFingerprint = await ComputeDirectoryFingerprintAsync(source, cancellationToken).ConfigureAwait(false);
                var capture = await snapshotStore!.CaptureAsync(
                    source, id, snapshotId, progress, cancellationToken).ConfigureAwait(false);
                finalSnapshotManifests.Add(capture.ManifestRelativePath);
                var finalFingerprint = await ComputeDirectoryFingerprintAsync(source, cancellationToken).ConfigureAwait(false);
                if (!StringComparer.OrdinalIgnoreCase.Equals(sourceFingerprint, capture.ContentFingerprint)
                    || !StringComparer.OrdinalIgnoreCase.Equals(sourceFingerprint, finalFingerprint))
                {
                    throw new InvalidDataException($"导入快照 {packageSnapshot.Id:D} 时文件发生变化。");
                }

                var snapshotPath = snapshotStore.GetManifestFullPath(capture.ManifestRelativePath);
                var snapshot = new SaveSnapshot(
                    snapshotId, id, physicalSnapshotName, snapshotPath, packageSnapshot.Note, packageSnapshot.Trigger,
                    packageSnapshot.CreatedAtUtc, capture.ContentFingerprint, capture.FileCount, capture.TotalBytes,
                    snapshotMetadata.GameSaveName, snapshotMetadata.PlayTime, snapshotMetadata.LastSavedAtUtc,
                    snapshotMetadata.DisplayData, snapshotMetadata.Type, snapshotMetadata.GameVersion,
                    SaveSnapshotStorageKind.ChunkedManifest, capture.ManifestRelativePath);
                await InsertSnapshotAsync(connection, transaction, snapshot, cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            finalProfilePath = null;
            finalSnapshotManifests.Clear();
            return profile;
        }
        catch
        {
            if (finalProfilePath is not null && Directory.Exists(finalProfilePath))
            {
                DeleteManagedDirectory(finalProfilePath, GetVaultRootPath());
            }

            foreach (var manifestPath in finalSnapshotManifests)
            {
                try
                {
                    await snapshotStore!.DeleteManifestAsync(manifestPath, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception cleanupError)
                {
                    logger.Warning(cleanupError, "Could not clean imported snapshot manifest {ManifestPath}", manifestPath);
                }
            }

            await CollectSnapshotGarbageSafelyAsync(CancellationToken.None).ConfigureAwait(false);

            throw;
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                DeleteManagedDirectory(staging, GetStagingRootPath());
            }

            gate.Release();
        }
    }

    public async Task ExportSnapshotAsync(
        Guid snapshotId,
        string destinationZipPath,
        IProgress<SaveImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await EnterForegroundGateAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ExportSnapshotCoreAsync(snapshotId, destinationZipPath, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task ExportSnapshotCoreAsync(
        Guid snapshotId,
        string destinationZipPath,
        IProgress<SaveImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var snapshot = await RequireSnapshotAsync(snapshotId, cancellationToken).ConfigureAwait(false);
        var profile = await RequireProfileAsync(snapshot.ProfileId, cancellationToken).ConfigureAwait(false);
        var directFiles = new List<PackageFile>();
        var chunkedSources = new List<ChunkArchiveSource>();
        var packageFiles = new List<PackageFile>();
        if (snapshot.StorageKind == SaveSnapshotStorageKind.ChunkedManifest)
        {
            var manifestPath = snapshot.ManifestRelativePath
                ?? throw new InvalidDataException("增量快照缺少清单路径。");
            chunkedSources.Add(new ChunkArchiveSource(manifestPath, "profile"));
            var chunkFiles = await snapshotStore!.GetPackageFilesAsync(manifestPath, "profile", cancellationToken).ConfigureAwait(false);
            packageFiles.AddRange(chunkFiles.Select(file => new PackageFile(file.Path, file.Sha256, file.Length)));
        }
        else
        {
            AddPackageFiles(directFiles, snapshot.PhysicalPath, "profile");
        }

        for (var i = 0; i < directFiles.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            directFiles[i] = directFiles[i] with
            {
                Sha256 = await ComputeFileHashAsync(directFiles[i].SourcePath!, cancellationToken).ConfigureAwait(false)
            };
        }
        packageFiles.InsertRange(0, directFiles.Select(file => file with { SourcePath = null }));

        var manifest = new PackageManifest(
            PackageSchemaVersion,
            new PackageProfile($"{profile.DisplayName} · {snapshot.CreatedAtUtc.ToLocalTime():yyyy-MM-dd HH-mm}", profile.IsFavorite, snapshot.CreatedAtUtc),
            [],
            packageFiles);
        await WritePackageAsync(
            destinationZipPath, manifest, directFiles, chunkedSources, progress, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SaveSnapshot?> CreateGameExitSnapshotAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (!settings!.AutoBackupSaveNodes || !settings.AutoSnapshotOnGameExit)
        {
            return null;
        }

        await EnterForegroundGateAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!await coordinator!.IsSafeToSwitch(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            var active = await GetActiveProfileCoreAsync(cancellationToken).ConfigureAwait(false);
            if (active is not null)
            {
                await ReconcileBackupNodesCoreAsync(active, cancellationToken).ConfigureAwait(false);
            }

            // The legacy return value remains for one release; game-exit protection is now node based.
            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> SaveGameExitThumbnailAsync(byte[] pngData, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pngData);
        if (pngData.Length is < 8 or > 8 * 1024 * 1024 || !IsPng(pngData))
        {
            return false;
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await EnterForegroundGateAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var active = await GetActiveProfileCoreAsync(cancellationToken).ConfigureAwait(false);
            if (active is null || !Directory.Exists(active.PhysicalPath))
            {
                return false;
            }

            const string relativePath = ".bohemix\\exit-thumbnail.png";
            var thumbnailPath = Path.Combine(active.PhysicalPath, ".bohemix", "exit-thumbnail.png");
            Directory.CreateDirectory(Path.GetDirectoryName(thumbnailPath)!);
            var temporaryPath = thumbnailPath + $".tmp-{Guid.NewGuid():N}";
            try
            {
                await File.WriteAllBytesAsync(temporaryPath, pngData, cancellationToken).ConfigureAwait(false);
                File.Move(temporaryPath, thumbnailPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }

            await using var connection = connectionFactory.CreateConnection();
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE SaveSlots SET ThumbnailPath = @ThumbnailPath, UpdatedAtUtc = @Now WHERE Id = @Id;",
                new
                {
                    Id = active.Id.ToString("D"),
                    ThumbnailPath = relativePath,
                    Now = FormatDate(DateTimeOffset.UtcNow)
                },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    public string GetOfficialSavePath()
    {
        var configured = settings?.OfficialSavePath;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return NormalizeDirectoryPath(configured);
        }

        return NormalizeDirectoryPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Saved Games", "kingdomcome2", "saves"));
    }

    public string GetVaultRootPath() =>
        NormalizeDirectoryPath(Path.Combine(applicationPathService.GetPaths().DataDirectory, "saves", "vault"));

    public string GetSnapshotsRootPath() =>
        NormalizeDirectoryPath(Path.Combine(applicationPathService.GetPaths().DataDirectory, "saves", "snapshots"));

    private string GetStagingRootPath() =>
        NormalizeDirectoryPath(Path.Combine(applicationPathService.GetPaths().DataDirectory, "saves", "staging"));

    private async Task EnterForegroundGateAsync(CancellationToken cancellationToken)
    {
        Volatile.Read(ref maintenancePassCancellation)?.Cancel();
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RunMaintenanceLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            while (!cancellationToken.IsCancellationRequested)
            {
                var migrated = false;
                var activeCoordinator = coordinator;
                if (initialized
                    && activeCoordinator is not null
                    && await activeCoordinator.IsSafeToSwitch(cancellationToken).ConfigureAwait(false)
                    && gate.Wait(0))
                {
                    using var passCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    Interlocked.Exchange(ref maintenancePassCancellation, passCancellation);
                    try
                    {
                        maintenanceState = SaveSnapshotMaintenanceState.Migrating;
                        if (settings!.AutoBackupSaveNodes)
                        {
                            await using var profileConnection = connectionFactory.CreateConnection();
                            var profileRows = await profileConnection.QueryAsync<ProfileRow>(new CommandDefinition(
                                ProfileSelectSql,
                                cancellationToken: passCancellation.Token)).ConfigureAwait(false);
                            var activeTarget = GetActiveTarget();
                            foreach (var profile in profileRows.Select(row => ToProfile(row, activeTarget)))
                            {
                                await ReconcileBackupNodesCoreAsync(profile, passCancellation.Token).ConfigureAwait(false);
                            }
                        }

                        migrated = await MigrateOneLegacySnapshotAsync(passCancellation.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (passCancellation.IsCancellationRequested)
                    {
                        maintenanceState = SaveSnapshotMaintenanceState.Ready;
                    }
                    catch (Exception ex)
                    {
                        maintenanceState = SaveSnapshotMaintenanceState.Faulted;
                        AddMigrationWarning("snapshot-maintenance", ex.Message);
                        logger.Warning(ex, "Snapshot maintenance pass failed");
                    }
                    finally
                    {
                        Interlocked.CompareExchange(ref maintenancePassCancellation, null, passCancellation);
                        gate.Release();
                    }
                }

                await Task.Delay(migrated ? TimeSpan.FromSeconds(2) : TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    internal async Task<bool> MigrateOneLegacySnapshotAsync(CancellationToken cancellationToken, bool enforceFreeSpace = true)
    {
        await using var connection = connectionFactory.CreateConnection();
        var row = await connection.QuerySingleOrDefaultAsync<SnapshotRow>(new CommandDefinition(
            SnapshotSelectSql + " WHERE StorageFormat = @StorageFormat ORDER BY CreatedAtUtc DESC LIMIT 1;",
            new { StorageFormat = SaveSnapshotStorageKind.LegacyDirectory.ToString() },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (row is null)
        {
            maintenanceState = SaveSnapshotMaintenanceState.Complete;
            return false;
        }

        var snapshot = ToSnapshot(row);
        if (!Directory.Exists(snapshot.PhysicalPath))
        {
            maintenanceState = SaveSnapshotMaintenanceState.Faulted;
            AddMigrationWarning(snapshot.Id.ToString("D"), "旧快照目录不存在，已跳过自动迁移。");
            return false;
        }

        var additionalBytes = await snapshotStore!.EstimateAdditionalBytesAsync(snapshot.PhysicalPath, cancellationToken).ConfigureAwait(false);
        var drive = new DriveInfo(Path.GetPathRoot(GetSnapshotsRootPath())!);
        var reserve = Math.Max(1024L * 1024 * 1024, drive.TotalSize / 10);
        if (enforceFreeSpace && drive.AvailableFreeSpace - additionalBytes < reserve)
        {
            maintenanceState = SaveSnapshotMaintenanceState.PausedLowSpace;
            return false;
        }

        string? manifestRelativePath = null;
        try
        {
            var sourceFingerprint = await ComputeDirectoryFingerprintAsync(snapshot.PhysicalPath, cancellationToken).ConfigureAwait(false);
            if (!StringComparer.OrdinalIgnoreCase.Equals(sourceFingerprint, snapshot.ContentFingerprint))
            {
                throw new InvalidDataException("旧快照内容指纹与数据库记录不一致。");
            }

            var capture = await snapshotStore.CaptureAsync(
                snapshot.PhysicalPath, snapshot.ProfileId, snapshot.Id, null, cancellationToken).ConfigureAwait(false);
            manifestRelativePath = capture.ManifestRelativePath;
            var finalFingerprint = await ComputeDirectoryFingerprintAsync(snapshot.PhysicalPath, cancellationToken).ConfigureAwait(false);
            if (!StringComparer.OrdinalIgnoreCase.Equals(sourceFingerprint, capture.ContentFingerprint)
                || !StringComparer.OrdinalIgnoreCase.Equals(sourceFingerprint, finalFingerprint))
            {
                throw new InvalidDataException("迁移期间旧快照内容发生变化。");
            }

            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE SaveSnapshots
                SET StorageFormat = @StorageFormat, ManifestRelativePath = @ManifestRelativePath
                WHERE Id = @Id AND StorageFormat = @LegacyFormat;
                """,
                new
                {
                    Id = snapshot.Id.ToString("D"),
                    StorageFormat = SaveSnapshotStorageKind.ChunkedManifest.ToString(),
                    LegacyFormat = SaveSnapshotStorageKind.LegacyDirectory.ToString(),
                    capture.ManifestRelativePath
                },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            try
            {
                DeleteManagedDirectory(snapshot.PhysicalPath, GetSnapshotsRootPath());
            }
            catch (Exception cleanupError)
            {
                logger.Warning(cleanupError, "Legacy snapshot {SnapshotId} was migrated but its old directory remains", snapshot.Id);
            }

            maintenanceState = SaveSnapshotMaintenanceState.Ready;
            await CollectSnapshotGarbageSafelyAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            if (manifestRelativePath is not null)
            {
                try
                {
                    await snapshotStore.DeleteManifestAsync(manifestRelativePath, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception cleanupError)
                {
                    logger.Warning(cleanupError, "Could not clean failed migration manifest {ManifestPath}", manifestRelativePath);
                }
            }

            await CollectSnapshotGarbageSafelyAsync(CancellationToken.None).ConfigureAwait(false);

            throw;
        }
    }

    private async Task ReconcileSnapshotStorageAsync(CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        var rows = (await connection.QueryAsync<SnapshotRow>(new CommandDefinition(
            SnapshotSelectSql,
            cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();
        var liveManifests = new List<string>();
        var hasInvalidManifest = false;
        foreach (var row in rows.Where(row => ParseStorageKind(row.StorageFormat) == SaveSnapshotStorageKind.ChunkedManifest))
        {
            if (string.IsNullOrWhiteSpace(row.ManifestRelativePath))
            {
                hasInvalidManifest = true;
                AddMigrationWarning(row.Id, "增量快照缺少清单路径。");
                continue;
            }

            try
            {
                await snapshotStore!.ValidateManifestAsync(row.ManifestRelativePath, cancellationToken).ConfigureAwait(false);
                liveManifests.Add(row.ManifestRelativePath);
                var legacyPath = Path.Combine(GetSnapshotsRootPath(), row.ProfileId, row.PhysicalName);
                if (Directory.Exists(legacyPath))
                {
                    DeleteManagedDirectory(legacyPath, GetSnapshotsRootPath());
                }
            }
            catch (Exception ex)
            {
                hasInvalidManifest = true;
                AddMigrationWarning(row.Id, $"增量快照清单损坏或丢失：{ex.Message}");
                logger.Warning(ex, "Snapshot manifest reconciliation failed for {SnapshotId}", row.Id);
            }
        }

        var nodeRows = (await connection.QueryAsync<BackupManifestRow>(new CommandDefinition(
            "SELECT Id, ManifestRelativePath FROM SaveBackupNodes;",
            cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();
        foreach (var node in nodeRows)
        {
            try
            {
                await snapshotStore!.ValidateManifestAsync(node.ManifestRelativePath, cancellationToken).ConfigureAwait(false);
                liveManifests.Add(node.ManifestRelativePath);
                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE SaveBackupNodes SET Health = @Health, HealthMessage = NULL WHERE Id = @Id;",
                    new { Id = node.Id, Health = SaveBackupNodeHealth.Healthy.ToString() },
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                hasInvalidManifest = true;
                var manifestExists = false;
                try
                {
                    manifestExists = File.Exists(snapshotStore!.GetManifestFullPath(node.ManifestRelativePath));
                }
                catch
                {
                }

                var health = manifestExists ? SaveBackupNodeHealth.Corrupt : SaveBackupNodeHealth.MissingManifest;
                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE SaveBackupNodes SET Health = @Health, HealthMessage = @Message WHERE Id = @Id;",
                    new { Id = node.Id, Health = health.ToString(), Message = ex.Message },
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
                AddMigrationWarning(node.Id, $"Backup node manifest is unavailable: {ex.Message}");
                logger.Warning(ex, "Backup node manifest reconciliation failed for {NodeId}", node.Id);
            }
        }

        if (!hasInvalidManifest)
        {
            await snapshotStore!.ReconcileOrphanManifestsAsync(liveManifests, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task CollectSnapshotGarbageSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = connectionFactory.CreateConnection();
            var paths = (await connection.QueryAsync<string>(new CommandDefinition(
                """
                SELECT ManifestRelativePath FROM SaveSnapshots
                WHERE StorageFormat = @StorageFormat AND ManifestRelativePath IS NOT NULL
                UNION ALL
                SELECT ManifestRelativePath FROM SaveBackupNodes;
                """,
                new { StorageFormat = SaveSnapshotStorageKind.ChunkedManifest.ToString() },
                cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();
            await snapshotStore!.CollectGarbageAsync(paths, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AddMigrationWarning("snapshot-gc", $"快照对象清理已推迟：{ex.Message}");
            logger.Warning(ex, "Snapshot object garbage collection was deferred");
        }
    }

    private void AddMigrationWarning(string sourceId, string message)
    {
        lock (migrationWarnings)
        {
            if (!migrationWarnings.Any(warning => warning.SourceId == sourceId && warning.Message == message))
            {
                migrationWarnings.Add(new SaveMigrationWarning(sourceId, message));
            }
        }
    }

    private async Task<SaveSnapshot?> CreateSnapshotCoreAsync(
        SaveProfile profile,
        SaveSnapshotTrigger trigger,
        string? note,
        IProgress<SaveImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(profile.PhysicalPath))
        {
            throw new DirectoryNotFoundException(profile.PhysicalPath);
        }

        var fingerprint = await ComputeDirectoryFingerprintAsync(profile.PhysicalPath, cancellationToken).ConfigureAwait(false);
        var metadata = await EnrichCharacterMetadataAsync(
            profile.PhysicalPath,
            parser.ParseMetadata(profile.PhysicalPath),
            cancellationToken).ConfigureAwait(false);
        if (trigger is SaveSnapshotTrigger.BeforeSwitch or SaveSnapshotTrigger.GameExit)
        {
            await using var duplicateConnection = connectionFactory.CreateConnection();
            var latestFingerprint = await duplicateConnection.ExecuteScalarAsync<string?>(new CommandDefinition(
                "SELECT ContentFingerprint FROM SaveSnapshots WHERE ProfileId = @Id ORDER BY CreatedAtUtc DESC LIMIT 1;",
                new { Id = profile.Id.ToString("D") }, cancellationToken: cancellationToken)).ConfigureAwait(false);
            if (StringComparer.OrdinalIgnoreCase.Equals(fingerprint, latestFingerprint))
            {
                await UpdateProfileMetadataAsync(profile.Id, fingerprint, metadata, cancellationToken).ConfigureAwait(false);
                return null;
            }
        }

        var id = Guid.NewGuid();
        var physicalName = id.ToString("D");
        string? manifestRelativePath = null;
        try
        {
            var capture = await snapshotStore!.CaptureAsync(
                profile.PhysicalPath, profile.Id, id, progress, cancellationToken).ConfigureAwait(false);
            manifestRelativePath = capture.ManifestRelativePath;
            var finalSourceFingerprint = await ComputeDirectoryFingerprintAsync(profile.PhysicalPath, cancellationToken).ConfigureAwait(false);
            if (!StringComparer.OrdinalIgnoreCase.Equals(fingerprint, capture.ContentFingerprint)
                || !StringComparer.OrdinalIgnoreCase.Equals(fingerprint, finalSourceFingerprint))
            {
                throw new InvalidDataException("创建快照时文件发生变化，操作已取消。");
            }

            var path = snapshotStore.GetManifestFullPath(capture.ManifestRelativePath);
            var snapshot = new SaveSnapshot(
                id, profile.Id, physicalName, path, string.IsNullOrWhiteSpace(note) ? null : note.Trim(), trigger,
                DateTimeOffset.UtcNow, capture.ContentFingerprint, capture.FileCount, capture.TotalBytes,
                metadata.GameSaveName, metadata.PlayTime, metadata.LastSavedAtUtc,
                metadata.DisplayData, metadata.Type, metadata.GameVersion,
                SaveSnapshotStorageKind.ChunkedManifest, capture.ManifestRelativePath);

            await using var connection = connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await InsertSnapshotAsync(connection, transaction, snapshot, cancellationToken).ConfigureAwait(false);
            await UpdateProfileMetadataAsync(
                connection,
                transaction,
                profile.Id,
                capture.ContentFingerprint,
                metadata,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            await PruneAutomaticSnapshotsAsync(profile.Id, cancellationToken).ConfigureAwait(false);
            return snapshot;
        }
        catch
        {
            if (manifestRelativePath is not null)
            {
                try
                {
                    await snapshotStore!.DeleteManifestAsync(manifestRelativePath, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception cleanupError)
                {
                    logger.Warning(cleanupError, "Could not clean failed snapshot manifest {ManifestPath}", manifestRelativePath);
                }
            }

            await CollectSnapshotGarbageSafelyAsync(CancellationToken.None).ConfigureAwait(false);

            throw;
        }
    }

    private async Task WritePackageAsync(
        string destinationZipPath,
        PackageManifest manifest,
        IReadOnlyList<PackageFile> directFiles,
        IReadOnlyList<ChunkArchiveSource> chunkedSources,
        IProgress<SaveImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var destination = Path.GetFullPath(destinationZipPath);
        var destinationDirectory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrWhiteSpace(destinationDirectory))
        {
            Directory.CreateDirectory(destinationDirectory);
        }

        var temp = destination + $".tmp-{Guid.NewGuid():N}";
        try
        {
            await using var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, true);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
            var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
            await using (var manifestStream = manifestEntry.Open())
            {
                await JsonSerializer.SerializeAsync(manifestStream, manifest, cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            var totalBytes = manifest.Files.Sum(file => file.Length);
            long processed = 0;
            for (var i = 0; i < directFiles.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = directFiles[i];
                var entry = archive.CreateEntry(file.Path, CompressionLevel.Optimal);
                await using var source = new FileStream(file.SourcePath!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, true);
                await using var target = entry.Open();
                await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
                processed += file.Length;
                progress?.Report(new SaveImportProgress("Exporting", i + 1, manifest.Files.Count, processed, totalBytes, file.Path));
            }

            foreach (var source in chunkedSources)
            {
                processed = await snapshotStore!.WriteFilesToArchiveAsync(
                    source.ManifestRelativePath,
                    archive,
                    source.Prefix,
                    progress,
                    processed,
                    totalBytes,
                    cancellationToken).ConfigureAwait(false);
            }

            archive.Dispose();
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            await stream.DisposeAsync().ConfigureAwait(false);
            File.Move(temp, destination, overwrite: true);
        }
        catch
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }

            throw;
        }
    }

    private async Task PruneAutomaticSnapshotsAsync(Guid profileId, CancellationToken cancellationToken)
    {
        var keep = Math.Clamp(settings!.AutoSnapshotRetention, 1, 200);
        await using var connection = connectionFactory.CreateConnection();
        var rows = (await connection.QueryAsync<SnapshotRow>(new CommandDefinition(
            SnapshotSelectSql + " WHERE ProfileId = @ProfileId AND Trigger <> @Manual ORDER BY CreatedAtUtc DESC;",
            new { ProfileId = profileId.ToString("D"), Manual = SaveSnapshotTrigger.Manual.ToString() },
            cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

        foreach (var row in rows.Skip(keep))
        {
            var snapshot = ToSnapshot(row);
            await DeleteSnapshotCoreAsync(snapshot.Id, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task EnsureDatabaseAsync(CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            """
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
                ThumbnailPath TEXT NULL,
                IsFavorite INTEGER NOT NULL DEFAULT 0,
                LastActivatedAtUtc TEXT NULL,
                ContentFingerprint TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS SaveSnapshots (
                Id TEXT PRIMARY KEY NOT NULL,
                ProfileId TEXT NOT NULL,
                PhysicalName TEXT NOT NULL,
                Note TEXT NULL,
                Trigger TEXT NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                ContentFingerprint TEXT NOT NULL,
                FileCount INTEGER NOT NULL,
                TotalBytes INTEGER NOT NULL,
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
                StorageFormat TEXT NOT NULL DEFAULT 'LegacyDirectory',
                ManifestRelativePath TEXT NULL,
                FOREIGN KEY(ProfileId) REFERENCES SaveSlots(Id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS IX_SaveSnapshots_ProfileId_CreatedAtUtc
                ON SaveSnapshots(ProfileId, CreatedAtUtc DESC);

            CREATE TABLE IF NOT EXISTS SaveBackupNodes (
                Id TEXT PRIMARY KEY NOT NULL,
                ProfileId TEXT NOT NULL,
                SourceRelativePath TEXT NOT NULL,
                ContentSha256 TEXT NOT NULL,
                ManifestRelativePath TEXT NOT NULL,
                TotalBytes INTEGER NOT NULL,
                FirstSeenAtUtc TEXT NOT NULL,
                LastSeenAtUtc TEXT NOT NULL,
                SourceLastWriteUtc TEXT NOT NULL,
                LastSavedAtUtc TEXT NULL,
                GameSaveName TEXT NULL,
                PlayTimeSeconds INTEGER NULL,
                HenryLevel INTEGER NULL,
                CurrentLocation TEXT NULL,
                ActiveQuest TEXT NULL,
                GroschenCount INTEGER NULL,
                PlayerStatusEffects TEXT NULL,
                SaveType TEXT NULL,
                GameVersion TEXT NULL,
                IsImportant INTEGER NOT NULL DEFAULT 0,
                ImportanceSource TEXT NOT NULL DEFAULT 'None',
                ProtectedAtUtc TEXT NULL,
                Note TEXT NULL,
                Health TEXT NOT NULL DEFAULT 'Healthy',
                HealthMessage TEXT NULL,
                FOREIGN KEY(ProfileId) REFERENCES SaveSlots(Id) ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_SaveBackupNodes_ProfileId_ContentSha256
                ON SaveBackupNodes(ProfileId, ContentSha256);
            CREATE INDEX IF NOT EXISTS IX_SaveBackupNodes_ProfileId_SavedAt
                ON SaveBackupNodes(ProfileId, IsImportant, LastSavedAtUtc DESC, FirstSeenAtUtc DESC);

            CREATE TABLE IF NOT EXISTS SaveBackupLibraryStates (
                ProfileId TEXT PRIMARY KEY NOT NULL,
                InitializedAtUtc TEXT NOT NULL,
                LastScanAtUtc TEXT NOT NULL,
                RuleVersion INTEGER NOT NULL,
                FOREIGN KEY(ProfileId) REFERENCES SaveSlots(Id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS SaveBackupFileObservations (
                ProfileId TEXT NOT NULL,
                SourceRelativePath TEXT NOT NULL,
                Length INTEGER NOT NULL,
                LastWriteUtc TEXT NOT NULL,
                LastObservedAtUtc TEXT NOT NULL,
                PRIMARY KEY(ProfileId, SourceRelativePath),
                FOREIGN KEY(ProfileId) REFERENCES SaveSlots(Id) ON DELETE CASCADE
            );
            """,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        await EnsureColumnAsync(connection, "SaveSlots", "IsFavorite", "INTEGER NOT NULL DEFAULT 0", cancellationToken).ConfigureAwait(false);
        await EnsureColumnAsync(connection, "SaveSlots", "LastActivatedAtUtc", "TEXT NULL", cancellationToken).ConfigureAwait(false);
        await EnsureColumnAsync(connection, "SaveSlots", "ContentFingerprint", "TEXT NULL", cancellationToken).ConfigureAwait(false);
        await EnsureColumnAsync(connection, "SaveSnapshots", "StorageFormat", "TEXT NOT NULL DEFAULT 'LegacyDirectory'", cancellationToken).ConfigureAwait(false);
        await EnsureColumnAsync(connection, "SaveSnapshots", "ManifestRelativePath", "TEXT NULL", cancellationToken).ConfigureAwait(false);
        var environmentId = applicationPathService.GetPaths().GameEnvironmentId.ToString("D");
        var environmentDefinition = $"TEXT NOT NULL DEFAULT '{environmentId}'";
        await EnsureColumnAsync(connection, "SaveSlots", "EnvironmentId", environmentDefinition, cancellationToken).ConfigureAwait(false);
        await EnsureColumnAsync(connection, "SaveSnapshots", "EnvironmentId", environmentDefinition, cancellationToken).ConfigureAwait(false);
        await EnsureColumnAsync(connection, "SaveBackupNodes", "EnvironmentId", environmentDefinition, cancellationToken).ConfigureAwait(false);
        await EnsureColumnAsync(connection, "SaveBackupLibraryStates", "EnvironmentId", environmentDefinition, cancellationToken).ConfigureAwait(false);
        await EnsureColumnAsync(connection, "SaveBackupFileObservations", "EnvironmentId", environmentDefinition, cancellationToken).ConfigureAwait(false);
    }

    private async Task MigrateHiddenSaveManagerAsync(CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        var tableExists = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'KCD2_Saves';",
            cancellationToken: cancellationToken)).ConfigureAwait(false) > 0;
        if (!tableExists)
        {
            return;
        }

        var rows = await connection.QueryAsync<LegacyRow>(new CommandDefinition(
            "SELECT Id, DisplayName, PhysicalPath, CreatedTime, IsFavorite FROM KCD2_Saves WHERE IsDeleted = 0;",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceId = row.Id ?? string.Empty;
            string? destination = null;
            try
            {
                if (string.IsNullOrWhiteSpace(row.PhysicalPath) || !Directory.Exists(row.PhysicalPath))
                {
                    throw new DirectoryNotFoundException(row.PhysicalPath);
                }

                var id = Guid.TryParse(sourceId, out var parsed) ? parsed : Guid.NewGuid();
                var alreadyMigrated = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                    "SELECT COUNT(*) FROM SaveSlots WHERE Id = @Id;",
                    new { Id = id.ToString("D") }, cancellationToken: cancellationToken)).ConfigureAwait(false) > 0;
                if (alreadyMigrated)
                {
                    continue;
                }

                var physicalName = CreatePhysicalName("Migrated", id);
                destination = Path.Combine(GetVaultRootPath(), physicalName);
                await CopyDirectoryAsync(row.PhysicalPath, destination, null, "Migrating", cancellationToken).ConfigureAwait(false);
                var sourceFingerprint = await ComputeDirectoryFingerprintAsync(row.PhysicalPath, cancellationToken).ConfigureAwait(false);
                var destinationFingerprint = await ComputeDirectoryFingerprintAsync(destination, cancellationToken).ConfigureAwait(false);
                if (!StringComparer.OrdinalIgnoreCase.Equals(sourceFingerprint, destinationFingerprint))
                {
                    throw new InvalidDataException("迁移后的文件校验失败。");
                }

                var metadata = await EnrichCharacterMetadataAsync(destination, parser.ParseMetadata(destination), cancellationToken).ConfigureAwait(false);
                var created = row.CreatedTime > 0 ? DateTimeOffset.FromUnixTimeSeconds(row.CreatedTime) : DateTimeOffset.UtcNow;
                var profile = new SaveProfile(
                    id, physicalName, ValidateDisplayName(row.DisplayName ?? "迁移的档案"), destination,
                    created, DateTimeOffset.UtcNow, row.IsFavorite != 0, null, destinationFingerprint,
                    metadata.GameSaveName, metadata.PlayTime, metadata.LastSavedAtUtc,
                    metadata.DisplayData, metadata.Type, metadata.GameVersion, metadata.ThumbnailPath);
                await InsertProfileAsync(profile, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (destination is not null && Directory.Exists(destination))
                {
                    DeleteManagedDirectory(destination, GetVaultRootPath());
                }
                migrationWarnings.Add(new SaveMigrationWarning(sourceId, ex.Message));
                logger.Warning(ex, "Could not migrate legacy KCD2 save {SaveId}; source was preserved", sourceId);
            }
        }
    }

    private async Task<SaveProfile?> GetProfileByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        var row = await connection.QuerySingleOrDefaultAsync<ProfileRow>(new CommandDefinition(
            ProfileSelectSql + " WHERE Id = @Id;", new { Id = id.ToString("D") }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return row is null ? null : ToProfile(row, GetActiveTarget());
    }

    private async Task<SaveProfile> RequireProfileAsync(Guid id, CancellationToken cancellationToken) =>
        await GetProfileByIdAsync(id, cancellationToken).ConfigureAwait(false)
        ?? throw new KeyNotFoundException("找不到这个档案。");

    private async Task<SaveSnapshot> RequireSnapshotAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        var row = await connection.QuerySingleOrDefaultAsync<SnapshotRow>(new CommandDefinition(
            SnapshotSelectSql + " WHERE Id = @Id;", new { Id = id.ToString("D") }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return row is null ? throw new KeyNotFoundException("找不到这个快照。") : ToSnapshot(row);
    }

    private async Task<SaveProfile?> GetActiveProfileCoreAsync(CancellationToken cancellationToken)
    {
        var activeTarget = GetActiveTarget();
        if (activeTarget is null)
        {
            return null;
        }

        await using var connection = connectionFactory.CreateConnection();
        var rows = await connection.QueryAsync<ProfileRow>(new CommandDefinition(ProfileSelectSql, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.Select(row => ToProfile(row, activeTarget)).FirstOrDefault(profile => profile.IsActive);
    }

    private Task<bool> HasUnmanagedSaveCoreAsync()
    {
        var official = GetOfficialSavePath();
        return Task.FromResult(
            junctionRouter.TryGetJunctionInfo(official) is null
            && Directory.Exists(official)
            && Directory.EnumerateFileSystemEntries(official).Any());
    }

    private string? GetActiveTarget() => junctionRouter.TryGetJunctionInfo(GetOfficialSavePath())?.TargetPath;

    private async Task InsertProfileAsync(SaveProfile profile, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        await InsertProfileAsync(connection, null, profile, cancellationToken).ConfigureAwait(false);
    }

    private static Task InsertProfileAsync(SqliteConnection connection, System.Data.Common.DbTransaction? transaction, SaveProfile profile, CancellationToken cancellationToken)
    {
        return connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO SaveSlots
            (Id, PhysicalName, DisplayName, CreatedAtUtc, UpdatedAtUtc, GameSaveName, PlayTimeSeconds, LastSavedAtUtc,
             HenryLevel, CurrentLocation, ActiveQuest, GroschenCount, PlayerStatusEffects, SaveType, GameVersion, ThumbnailPath,
             IsFavorite, LastActivatedAtUtc, ContentFingerprint)
            VALUES
            (@Id, @PhysicalName, @DisplayName, @CreatedAtUtc, @UpdatedAtUtc, @GameSaveName, @PlayTimeSeconds, @LastSavedAtUtc,
             @HenryLevel, @CurrentLocation, @ActiveQuest, @GroschenCount, @PlayerStatusEffects, @SaveType, @GameVersion, @ThumbnailPath,
             @IsFavorite, @LastActivatedAtUtc, @ContentFingerprint);
            """,
            ToProfileParameters(profile), transaction, cancellationToken: cancellationToken));
    }

    private static Task InsertSnapshotAsync(SqliteConnection connection, System.Data.Common.DbTransaction transaction, SaveSnapshot snapshot, CancellationToken cancellationToken)
    {
        return connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO SaveSnapshots
            (Id, ProfileId, PhysicalName, Note, Trigger, CreatedAtUtc, ContentFingerprint, FileCount, TotalBytes,
             GameSaveName, PlayTimeSeconds, LastSavedAtUtc, HenryLevel, CurrentLocation, ActiveQuest, GroschenCount,
             PlayerStatusEffects, SaveType, GameVersion, StorageFormat, ManifestRelativePath)
            VALUES
            (@Id, @ProfileId, @PhysicalName, @Note, @Trigger, @CreatedAtUtc, @ContentFingerprint, @FileCount, @TotalBytes,
             @GameSaveName, @PlayTimeSeconds, @LastSavedAtUtc, @HenryLevel, @CurrentLocation, @ActiveQuest, @GroschenCount,
             @PlayerStatusEffects, @SaveType, @GameVersion, @StorageFormat, @ManifestRelativePath);
            """,
            ToSnapshotParameters(snapshot), transaction, cancellationToken: cancellationToken));
    }

    private async Task MarkActivatedAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE SaveSlots SET LastActivatedAtUtc = @Now, UpdatedAtUtc = @Now WHERE Id = @Id;",
            new { Id = id.ToString("D"), Now = FormatDate(DateTimeOffset.UtcNow) }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private async Task UpdateProfileFingerprintAsync(Guid id, string fingerprint, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE SaveSlots SET ContentFingerprint = @Fingerprint WHERE Id = @Id;",
            new { Id = id.ToString("D"), Fingerprint = fingerprint }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private async Task UpdateProfileMetadataAsync(Guid id, string fingerprint, KcdSaveMetadata metadata, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        await UpdateProfileMetadataAsync(connection, null, id, fingerprint, metadata, cancellationToken).ConfigureAwait(false);
    }

    private static Task UpdateProfileMetadataAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction? transaction,
        Guid id,
        string fingerprint,
        KcdSaveMetadata metadata,
        CancellationToken cancellationToken)
    {
        return connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE SaveSlots SET ContentFingerprint = @Fingerprint, UpdatedAtUtc = @Now,
                GameSaveName = @GameSaveName, PlayTimeSeconds = @PlayTimeSeconds, LastSavedAtUtc = @LastSavedAtUtc,
                HenryLevel = COALESCE(@HenryLevel, HenryLevel), CurrentLocation = @CurrentLocation, ActiveQuest = @ActiveQuest,
                GroschenCount = COALESCE(@GroschenCount, GroschenCount),
                PlayerStatusEffects = COALESCE(@PlayerStatusEffects, PlayerStatusEffects),
                SaveType = @SaveType, GameVersion = @GameVersion, ThumbnailPath = @ThumbnailPath
            WHERE Id = @Id;
            """,
            new
            {
                Id = id.ToString("D"), Fingerprint = fingerprint, Now = FormatDate(DateTimeOffset.UtcNow),
                metadata.GameSaveName, PlayTimeSeconds = (long?)metadata.PlayTime?.TotalSeconds,
                LastSavedAtUtc = FormatDate(metadata.LastSavedAtUtc),
                HenryLevel = PersistedHenryLevel(metadata.DisplayData),
                CurrentLocation = metadata.DisplayData?.CurrentLocation,
                ActiveQuest = metadata.DisplayData?.ActiveQuest,
                GroschenCount = PersistedGroschenCount(metadata.DisplayData),
                PlayerStatusEffects = JoinEffects(metadata.DisplayData), SaveType = metadata.Type?.ToString(),
                metadata.GameVersion, metadata.ThumbnailPath
            }, transaction, cancellationToken: cancellationToken));
    }

    private async Task<bool> BackfillMissingCharacterDataAsync(
        IReadOnlyCollection<ProfileRow> rows,
        string? activeTarget,
        CancellationToken cancellationToken)
    {
        var bridgePath = applicationPathService.GetPaths().TrackerBridgeEventsPath;
        var updates = new List<(string Id, int? Level, int? Groschen)>();
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (row.HenryLevel is not null && row.GroschenCount is not null)
            {
                continue;
            }

            var profilePath = Path.Combine(GetVaultRootPath(), row.PhysicalName);
            var targets = GetCharacterSnapshotTargetTimes(profilePath, ParseDate(row.LastSavedAtUtc));
            var isActiveProfile = activeTarget is not null && SamePath(profilePath, activeTarget);
            if (isActiveProfile)
            {
                targets = targets.Append(DateTimeOffset.UtcNow).ToArray();
            }
            var snapshot = await TrackerCharacterSnapshotReader.FindClosestAsync(
                bridgePath,
                GetGameLogPath(),
                targets,
                matchGameLogUsingFileTime: isActiveProfile,
                cancellationToken).ConfigureAwait(false);
            if (snapshot is null)
            {
                continue;
            }

            var level = row.HenryLevel ?? snapshot.HenryLevel;
            var groschen = row.GroschenCount ?? snapshot.GroschenCount;
            if (level != row.HenryLevel || groschen != row.GroschenCount)
            {
                updates.Add((row.Id, level, groschen));
            }
        }

        if (updates.Count == 0)
        {
            return false;
        }

        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var update in updates)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE SaveSlots SET
                    HenryLevel = COALESCE(HenryLevel, @Level),
                    GroschenCount = COALESCE(GroschenCount, @Groschen)
                WHERE Id = @Id;
                """,
                new { update.Id, update.Level, update.Groschen },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<KcdSaveMetadata> EnrichCharacterMetadataAsync(
        string savePath,
        KcdSaveMetadata metadata,
        CancellationToken cancellationToken)
    {
        var targets = GetCharacterSnapshotTargetTimes(savePath, metadata.LastSavedAtUtc);
        var snapshot = await TrackerCharacterSnapshotReader.FindClosestAsync(
            applicationPathService.GetPaths().TrackerBridgeEventsPath,
            GetGameLogPath(),
            targets,
            matchGameLogUsingFileTime: false,
            cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            return metadata;
        }

        if (snapshot.HenryLevel is null || snapshot.GroschenCount is null)
        {
            return metadata;
        }

        var existing = metadata.DisplayData;
        var level = existing?.HenryLevel is > 0 ? existing.HenryLevel : snapshot.HenryLevel ?? 0;
        var groschen = existing?.GroschenCount is > 0 ? existing.GroschenCount : snapshot.GroschenCount ?? 0;

        return metadata with
        {
            DisplayData = new DisplayInfo
            {
                HenryLevel = level,
                GroschenCount = groschen,
                CurrentLocation = existing?.CurrentLocation ?? string.Empty,
                ActiveQuest = existing?.ActiveQuest ?? string.Empty,
                PlayerStatusEffects = existing?.PlayerStatusEffects.ToList() ?? []
            }
        };
    }

    private static IReadOnlyCollection<DateTimeOffset> GetCharacterSnapshotTargetTimes(
        string savePath,
        DateTimeOffset? metadataSavedAtUtc)
    {
        var targets = new List<DateTimeOffset>();
        if (metadataSavedAtUtc is not null)
        {
            targets.Add(metadataSavedAtUtc.Value.ToUniversalTime());
        }

        try
        {
            if (Directory.Exists(savePath))
            {
                var latestWhs = Directory.EnumerateFiles(savePath, "*.whs", SearchOption.AllDirectories)
                    .Select(path => new FileInfo(path))
                    .OrderByDescending(file => file.LastWriteTimeUtc)
                    .FirstOrDefault();
                if (latestWhs is not null)
                {
                    targets.Add(new DateTimeOffset(latestWhs.LastWriteTimeUtc, TimeSpan.Zero));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Metadata time can still be used when a save directory is temporarily busy.
        }

        return targets;
    }

    private string? GetGameLogPath()
    {
        var installPath = settings?.PreferredGameInstallPath;
        if (string.IsNullOrWhiteSpace(installPath))
        {
            return null;
        }

        return Path.Combine(installPath, "kcd.log");
    }

    private async Task<string> CreateUniqueDisplayNameAsync(string desired, CancellationToken cancellationToken)
    {
        var baseName = ValidateDisplayName(desired);
        await using var connection = connectionFactory.CreateConnection();
        var names = (await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT DisplayName FROM SaveSlots;", cancellationToken: cancellationToken)).ConfigureAwait(false))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(baseName))
        {
            return baseName;
        }

        for (var suffix = 2; ; suffix++)
        {
            var suffixText = $" ({suffix})";
            var prefixLength = Math.Min(baseName.Length, MaxDisplayNameLength - suffixText.Length);
            var candidate = baseName[..prefixLength] + suffixText;
            if (!names.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private static async Task EnsureColumnAsync(SqliteConnection connection, string table, string column, string definition, CancellationToken cancellationToken)
    {
        var columns = await connection.QueryAsync<ColumnRow>(new CommandDefinition($"PRAGMA table_info({table});", cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (columns.Any(item => string.Equals(item.Name, column, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        await connection.ExecuteAsync(new CommandDefinition($"ALTER TABLE {table} ADD COLUMN {column} {definition};", cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private static async Task CopyDirectoryAsync(
        string source,
        string destination,
        IProgress<SaveImportProgress>? progress,
        string stage,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException(source);
        }

        var files = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
        var totalBytes = files.Sum(path => GetFileLength(path));
        Directory.CreateDirectory(destination);
        long processedBytes = 0;
        for (var i = 0; i < files.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, files[i]);
            var targetPath = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            await using var input = new FileStream(files[i], FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, true);
            await using var output = new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            processedBytes += input.Length;
            progress?.Report(new SaveImportProgress(stage, i + 1, files.Count, processedBytes, totalBytes, relative));
        }
    }

    private static async Task<string> ComputeDirectoryFingerprintAsync(string root, CancellationToken cancellationToken)
    {
        using var composite = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .OrderBy(path => Path.GetRelativePath(root, path), StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            composite.AppendData(Encoding.UTF8.GetBytes(relative.ToUpperInvariant()));
            composite.AppendData([0]);
            var hash = Convert.FromHexString(await ComputeFileHashAsync(file, cancellationToken).ConfigureAwait(false));
            composite.AppendData(hash);
        }

        return Convert.ToHexString(composite.GetHashAndReset());
    }

    private static async Task<string> ComputeFileHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static DirectoryStats GetDirectoryStats(string root)
    {
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToList();
        return new DirectoryStats(files.Count, files.Sum(GetFileLength));
    }

    private static long GetFileLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }

    private static void AddPackageFiles(List<PackageFile> destination, string root, string prefix)
    {
        foreach (var source in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var relative = Path.GetRelativePath(root, source).Replace('\\', '/');
            destination.Add(new PackageFile($"{prefix}/{relative}", string.Empty, GetFileLength(source), source));
        }
    }

    private static string NormalizePackagePath(string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        if (string.IsNullOrWhiteSpace(normalized)
            || Path.IsPathRooted(path)
            || normalized.Split('/').Any(part => part is "" or "." or ".."))
        {
            throw new InvalidDataException($"存档包包含不安全路径：{path}");
        }

        return normalized;
    }

    private static string GetSafeExtractionPath(string root, string packagePath)
    {
        var relative = NormalizePackagePath(packagePath).Replace('/', Path.DirectorySeparatorChar);
        var destination = Path.GetFullPath(Path.Combine(root, relative));
        EnsureInsideRoot(destination, root);
        return destination;
    }

    private static void DeleteManagedDirectory(string path, string root)
    {
        EnsureInsideRoot(path, root);
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static void EnsureInsideRoot(string path, string root)
    {
        var full = NormalizeDirectoryPath(path);
        var rootFull = NormalizeDirectoryPath(root);
        var rootPrefix = rootFull + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("操作路径超出 BohemiX 管理目录。");
        }
    }

    private SaveProfile ToProfile(ProfileRow row, string? activeTarget)
    {
        var physicalPath = Path.Combine(GetVaultRootPath(), row.PhysicalName);
        return new SaveProfile(
            Guid.Parse(row.Id), row.PhysicalName, row.DisplayName, physicalPath,
            ParseDate(row.CreatedAtUtc)!.Value, ParseDate(row.UpdatedAtUtc)!.Value,
            row.IsFavorite != 0, ParseDate(row.LastActivatedAtUtc), row.ContentFingerprint,
            row.GameSaveName, row.PlayTimeSeconds is null ? null : TimeSpan.FromSeconds(row.PlayTimeSeconds.Value),
            ParseDate(row.LastSavedAtUtc), CreateDisplayInfo(row.HenryLevel, row.CurrentLocation, row.ActiveQuest, row.GroschenCount, row.PlayerStatusEffects),
            ParseSaveType(row.SaveType), row.GameVersion, row.ThumbnailPath,
            activeTarget is not null && SamePath(activeTarget, physicalPath));
    }

    private SaveSnapshot ToSnapshot(SnapshotRow row)
    {
        var storageKind = ParseStorageKind(row.StorageFormat);
        var path = storageKind == SaveSnapshotStorageKind.ChunkedManifest && row.ManifestRelativePath is not null
            ? snapshotStore!.GetManifestFullPath(row.ManifestRelativePath)
            : Path.Combine(GetSnapshotsRootPath(), row.ProfileId, row.PhysicalName);
        return new SaveSnapshot(
            Guid.Parse(row.Id), Guid.Parse(row.ProfileId), row.PhysicalName, path, row.Note,
            Enum.TryParse<SaveSnapshotTrigger>(row.Trigger, out var trigger) ? trigger : SaveSnapshotTrigger.Manual,
            ParseDate(row.CreatedAtUtc)!.Value, row.ContentFingerprint, row.FileCount, row.TotalBytes,
            row.GameSaveName, row.PlayTimeSeconds is null ? null : TimeSpan.FromSeconds(row.PlayTimeSeconds.Value),
            ParseDate(row.LastSavedAtUtc), CreateDisplayInfo(row.HenryLevel, row.CurrentLocation, row.ActiveQuest, row.GroschenCount, row.PlayerStatusEffects),
            ParseSaveType(row.SaveType), row.GameVersion, storageKind, row.ManifestRelativePath);
    }

    private static SaveSnapshotStorageKind ParseStorageKind(string? value) =>
        Enum.TryParse<SaveSnapshotStorageKind>(value, out var kind)
            ? kind
            : SaveSnapshotStorageKind.LegacyDirectory;

    private static object ToProfileParameters(SaveProfile profile) => new
    {
        Id = profile.Id.ToString("D"), profile.PhysicalName, profile.DisplayName,
        CreatedAtUtc = FormatDate(profile.CreatedAtUtc), UpdatedAtUtc = FormatDate(profile.UpdatedAtUtc),
        profile.GameSaveName, PlayTimeSeconds = (long?)profile.PlayTime?.TotalSeconds,
        LastSavedAtUtc = FormatDate(profile.LastSavedAtUtc), HenryLevel = PersistedHenryLevel(profile.DisplayData),
        CurrentLocation = profile.DisplayData?.CurrentLocation,
        ActiveQuest = profile.DisplayData?.ActiveQuest,
        GroschenCount = PersistedGroschenCount(profile.DisplayData),
        PlayerStatusEffects = JoinEffects(profile.DisplayData), SaveType = profile.Type?.ToString(),
        profile.GameVersion, profile.ThumbnailPath, IsFavorite = profile.IsFavorite ? 1 : 0,
        LastActivatedAtUtc = FormatDate(profile.LastActivatedAtUtc), profile.ContentFingerprint
    };

    private static object ToSnapshotParameters(SaveSnapshot snapshot) => new
    {
        Id = snapshot.Id.ToString("D"), ProfileId = snapshot.ProfileId.ToString("D"), snapshot.PhysicalName,
        snapshot.Note, Trigger = snapshot.Trigger.ToString(), CreatedAtUtc = FormatDate(snapshot.CreatedAtUtc),
        snapshot.ContentFingerprint, snapshot.FileCount, snapshot.TotalBytes, snapshot.GameSaveName,
        PlayTimeSeconds = (long?)snapshot.PlayTime?.TotalSeconds, LastSavedAtUtc = FormatDate(snapshot.LastSavedAtUtc),
        HenryLevel = PersistedHenryLevel(snapshot.DisplayData),
        CurrentLocation = snapshot.DisplayData?.CurrentLocation,
        ActiveQuest = snapshot.DisplayData?.ActiveQuest,
        GroschenCount = PersistedGroschenCount(snapshot.DisplayData),
        PlayerStatusEffects = JoinEffects(snapshot.DisplayData), SaveType = snapshot.Type?.ToString(), snapshot.GameVersion,
        StorageFormat = snapshot.StorageKind.ToString(), snapshot.ManifestRelativePath
    };

    private static DisplayInfo? CreateDisplayInfo(int? level, string? location, string? quest, int? groschen, string? effects)
    {
        if (level is null && string.IsNullOrWhiteSpace(location) && string.IsNullOrWhiteSpace(quest) && groschen is null && string.IsNullOrWhiteSpace(effects))
        {
            return null;
        }

        return new DisplayInfo
        {
            HenryLevel = level ?? 0,
            CurrentLocation = location ?? string.Empty,
            ActiveQuest = quest ?? string.Empty,
            GroschenCount = groschen ?? 0,
            PlayerStatusEffects = string.IsNullOrWhiteSpace(effects) ? [] : effects.Split('|', StringSplitOptions.RemoveEmptyEntries).ToList()
        };
    }

    private static SaveType? ParseSaveType(string? value) =>
        Enum.TryParse<SaveType>(value, out var type) ? type : null;

    private static string? JoinEffects(DisplayInfo? info) =>
        info?.PlayerStatusEffects.Count > 0 ? string.Join('|', info.PlayerStatusEffects) : null;

    private static int? PersistedHenryLevel(DisplayInfo? info) =>
        info?.HenryLevel is > 0 ? info.HenryLevel : null;

    private static int? PersistedGroschenCount(DisplayInfo? info) =>
        info is not null && (info.GroschenCount > 0 || info.HenryLevel > 0)
            ? Math.Max(0, info.GroschenCount)
            : null;

    private static string ValidateDisplayName(string? value)
    {
        var name = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("档案名称不能为空。", nameof(value));
        }

        if (name.Length > MaxDisplayNameLength || name.Any(char.IsControl))
        {
            throw new ArgumentException("档案名称无效或超过 80 个字符。", nameof(value));
        }

        return name;
    }

    private static string CreatePhysicalName(string prefix, Guid id) =>
        $"{prefix}_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}_{id:N}";

    private static bool IsPng(byte[] data) =>
        data.Length >= 8 &&
        data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47 &&
        data[4] == 0x0D && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A;

    private static string NormalizeDirectoryPath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool SamePath(string left, string right) =>
        StringComparer.OrdinalIgnoreCase.Equals(NormalizeDirectoryPath(left), NormalizeDirectoryPath(right));

    private static string? FormatDate(DateTimeOffset? value) => value?.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset? ParseDate(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) ? parsed : null;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        maintenanceCancellation.Cancel();
        Volatile.Read(ref maintenancePassCancellation)?.Cancel();
        try
        {
            maintenanceTask?.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }

        maintenanceCancellation.Dispose();
        gate.Dispose();
        if (coordinator is IDisposable disposableCoordinator)
        {
            disposableCoordinator.Dispose();
        }
    }

    private const string ProfileSelectSql = """
        SELECT Id, PhysicalName, DisplayName,
               CreatedAtUtc, UpdatedAtUtc, IsFavorite, LastActivatedAtUtc, ContentFingerprint,
               GameSaveName, PlayTimeSeconds, LastSavedAtUtc, HenryLevel, CurrentLocation, ActiveQuest,
               GroschenCount, PlayerStatusEffects, SaveType, GameVersion, ThumbnailPath
        FROM SaveSlots
        """;

    private const string SnapshotSelectSql = """
        SELECT Id, ProfileId, PhysicalName, Note, Trigger, CreatedAtUtc, ContentFingerprint, FileCount, TotalBytes,
               GameSaveName, PlayTimeSeconds, LastSavedAtUtc, HenryLevel, CurrentLocation, ActiveQuest,
               GroschenCount, PlayerStatusEffects, SaveType, GameVersion, StorageFormat, ManifestRelativePath
        FROM SaveSnapshots
        """;

    private sealed class ProfileRow
    {
        public string Id { get; init; } = string.Empty;
        public string PhysicalName { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public string? PhysicalPath { get; init; }
        public string CreatedAtUtc { get; init; } = string.Empty;
        public string UpdatedAtUtc { get; init; } = string.Empty;
        public int IsFavorite { get; init; }
        public string? LastActivatedAtUtc { get; init; }
        public string? ContentFingerprint { get; init; }
        public string? GameSaveName { get; init; }
        public long? PlayTimeSeconds { get; init; }
        public string? LastSavedAtUtc { get; init; }
        public int? HenryLevel { get; init; }
        public string? CurrentLocation { get; init; }
        public string? ActiveQuest { get; init; }
        public int? GroschenCount { get; init; }
        public string? PlayerStatusEffects { get; init; }
        public string? SaveType { get; init; }
        public string? GameVersion { get; init; }
        public string? ThumbnailPath { get; init; }
    }

    private sealed class SnapshotRow
    {
        public string Id { get; init; } = string.Empty;
        public string ProfileId { get; init; } = string.Empty;
        public string PhysicalName { get; init; } = string.Empty;
        public string? Note { get; init; }
        public string Trigger { get; init; } = string.Empty;
        public string CreatedAtUtc { get; init; } = string.Empty;
        public string ContentFingerprint { get; init; } = string.Empty;
        public int FileCount { get; init; }
        public long TotalBytes { get; init; }
        public string? GameSaveName { get; init; }
        public long? PlayTimeSeconds { get; init; }
        public string? LastSavedAtUtc { get; init; }
        public int? HenryLevel { get; init; }
        public string? CurrentLocation { get; init; }
        public string? ActiveQuest { get; init; }
        public int? GroschenCount { get; init; }
        public string? PlayerStatusEffects { get; init; }
        public string? SaveType { get; init; }
        public string? GameVersion { get; init; }
        public string StorageFormat { get; init; } = SaveSnapshotStorageKind.LegacyDirectory.ToString();
        public string? ManifestRelativePath { get; init; }
    }

    private sealed class LegacyRow
    {
        public string? Id { get; init; }
        public string? DisplayName { get; init; }
        public string? PhysicalPath { get; init; }
        public long CreatedTime { get; init; }
        public int IsFavorite { get; init; }
    }

    private sealed class ColumnRow { public string Name { get; init; } = string.Empty; }
    private sealed class SnapshotStorageRow
    {
        public long TotalBytes { get; init; }
        public string StorageFormat { get; init; } = SaveSnapshotStorageKind.LegacyDirectory.ToString();
    }
    private sealed class BackupManifestRow
    {
        public string Id { get; init; } = string.Empty;
        public string ManifestRelativePath { get; init; } = string.Empty;
    }
    private sealed record DirectoryStats(int FileCount, long TotalBytes);
    private sealed record PackageProfile(string DisplayName, bool IsFavorite, DateTimeOffset CreatedAtUtc);
    private sealed record PackageSnapshot(Guid Id, string? Note, SaveSnapshotTrigger Trigger, DateTimeOffset CreatedAtUtc);
    private sealed record PackageFile(string Path, string Sha256, long Length, [property: System.Text.Json.Serialization.JsonIgnore] string? SourcePath = null);
    private sealed record ChunkArchiveSource(string ManifestRelativePath, string Prefix);
    private sealed record PackageManifest(int SchemaVersion, PackageProfile Profile, IReadOnlyList<PackageSnapshot> Snapshots, IReadOnlyList<PackageFile> Files);

}
