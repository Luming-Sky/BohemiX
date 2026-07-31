using System.Globalization;
using System.Security.Cryptography;
using BohemiX.Core.Models.Saves;
using Dapper;

namespace BohemiX.Infrastructure.Services.Saves;

public sealed partial class SaveProfileService
{
    private const int BackupLibraryRuleVersion = 1;
    private static readonly TimeSpan BackupFileStabilityDelay = TimeSpan.FromSeconds(1);

    public async Task<IReadOnlyList<SaveBackupNode>> GetBackupNodesAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = connectionFactory.CreateConnection();
        var rows = await connection.QueryAsync<BackupNodeRow>(new CommandDefinition(
            BackupNodeSelectSql +
            " WHERE ProfileId = @ProfileId ORDER BY IsImportant DESC, COALESCE(LastSavedAtUtc, FirstSeenAtUtc) DESC;",
            new { ProfileId = profileId.ToString("D") },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.Select(ToBackupNode).ToList();
    }

    public async Task<SaveBackupLibraryStats> GetBackupLibraryStatsAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = connectionFactory.CreateConnection();
        var rows = (await connection.QueryAsync<BackupNodeStorageRow>(new CommandDefinition(
            "SELECT ManifestRelativePath, TotalBytes, IsImportant FROM SaveBackupNodes;",
            cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();
        long physicalBytes;
        try
        {
            physicalBytes = await snapshotStore!.GetPhysicalBytesAsync(
                rows.Select(row => row.ManifestRelativePath).ToList(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            physicalBytes = 0;
            logger.Warning(ex, "Backup library physical-size calculation was skipped because a manifest is unavailable");
        }
        return new SaveBackupLibraryStats(
            rows.Count,
            rows.Count(row => row.IsImportant != 0),
            rows.Sum(row => row.TotalBytes),
            rows.Where(row => row.IsImportant != 0).Sum(row => row.TotalBytes),
            physicalBytes,
            Math.Clamp(settings!.BackupNodeRetention, 1, 200));
    }

    public async Task<IReadOnlyList<SaveBackupNode>> ReconcileBackupNodesAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await EnterForegroundGateAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            settings = await settingsService.LoadAsync(cancellationToken).ConfigureAwait(false);
            var profile = await RequireProfileAsync(profileId, cancellationToken).ConfigureAwait(false);
            if (!settings.AutoBackupSaveNodes
                || !await coordinator!.IsSafeToSwitch(cancellationToken).ConfigureAwait(false))
            {
                return await GetBackupNodesCoreAsync(profileId, cancellationToken).ConfigureAwait(false);
            }

            await ReconcileBackupNodesCoreAsync(profile, cancellationToken).ConfigureAwait(false);
            return await GetBackupNodesCoreAsync(profileId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SetBackupNodeImportanceAsync(
        Guid nodeId,
        bool isImportant,
        string? note = null,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await EnterForegroundGateAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var node = await RequireBackupNodeAsync(nodeId, cancellationToken).ConfigureAwait(false);
            await using var connection = connectionFactory.CreateConnection();
            var now = DateTimeOffset.UtcNow;
            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE SaveBackupNodes
                SET IsImportant = @IsImportant,
                    ImportanceSource = @ImportanceSource,
                    ProtectedAtUtc = @ProtectedAtUtc,
                    Note = @Note
                WHERE Id = @Id;
                """,
                new
                {
                    Id = nodeId.ToString("D"),
                    IsImportant = isImportant ? 1 : 0,
                    ImportanceSource = (isImportant ? SaveBackupImportanceSource.Manual : SaveBackupImportanceSource.None).ToString(),
                    ProtectedAtUtc = isImportant ? FormatDate(now) : null,
                    Note = string.IsNullOrWhiteSpace(note) ? node.Note : note.Trim()
                },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            if (!isImportant)
            {
                await PruneBackupNodesCoreAsync(node.ProfileId, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task RestoreBackupNodeAsync(Guid nodeId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await EnterForegroundGateAsync(cancellationToken).ConfigureAwait(false);
        string? staging = null;
        string? rollback = null;
        string? target = null;
        var replaced = false;
        var created = false;
        try
        {
            if (!await coordinator!.IsSafeToSwitch(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("The game is running or save data is still being written.");
            }

            var node = await RequireBackupNodeAsync(nodeId, cancellationToken).ConfigureAwait(false);
            var profile = await RequireProfileAsync(node.ProfileId, cancellationToken).ConfigureAwait(false);
            staging = Path.Combine(GetStagingRootPath(), $"restore-node-{Guid.NewGuid():N}");
            Directory.CreateDirectory(staging);
            var materialized = Path.Combine(staging, "node.whs");
            var entry = await snapshotStore!.MaterializeFileAsync(
                node.ManifestRelativePath, materialized, cancellationToken).ConfigureAwait(false);
            if (!StringComparer.OrdinalIgnoreCase.Equals(entry.Sha256, node.ContentSha256)
                || !StringComparer.OrdinalIgnoreCase.Equals(entry.RelativePath, node.SourceRelativePath))
            {
                throw new InvalidDataException("The backup node manifest does not match its database record.");
            }

            target = GetSafeProfileFilePath(profile.PhysicalPath, node.SourceRelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target))
            {
                var currentHash = await ComputeFileHashAsync(target, cancellationToken).ConfigureAwait(false);
                if (StringComparer.OrdinalIgnoreCase.Equals(currentHash, node.ContentSha256))
                {
                    return;
                }

                await CaptureBackupNodeCoreAsync(
                    profile,
                    target,
                    SaveBackupImportanceSource.RestoreSafety,
                    "Restore safety node",
                    cancellationToken).ConfigureAwait(false);
                rollback = Path.Combine(staging, "rollback.whs");
                File.Replace(materialized, target, rollback, ignoreMetadataErrors: true);
                replaced = true;
            }
            else
            {
                File.Move(materialized, target);
                created = true;
            }

            try
            {
                var restoredHash = await ComputeFileHashAsync(target, cancellationToken).ConfigureAwait(false);
                if (!StringComparer.OrdinalIgnoreCase.Equals(restoredHash, node.ContentSha256))
                {
                    throw new InvalidDataException("The restored save file failed its post-restore checksum verification.");
                }

                var fingerprint = await ComputeDirectoryFingerprintAsync(profile.PhysicalPath, cancellationToken).ConfigureAwait(false);
                var metadata = await EnrichCharacterMetadataAsync(
                    profile.PhysicalPath,
                    parser.ParseMetadata(profile.PhysicalPath),
                    cancellationToken).ConfigureAwait(false);
                await UpdateProfileMetadataAsync(profile.Id, fingerprint, metadata, cancellationToken).ConfigureAwait(false);
                if (rollback is not null && File.Exists(rollback))
                {
                    File.Delete(rollback);
                }
            }
            catch
            {
                if (replaced && rollback is not null && target is not null && File.Exists(rollback))
                {
                    File.Replace(rollback, target, null, ignoreMetadataErrors: true);
                }
                else if (created && target is not null && File.Exists(target))
                {
                    File.Delete(target);
                }

                throw;
            }
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

    public async Task DeleteBackupNodeAsync(Guid nodeId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await EnterForegroundGateAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await DeleteBackupNodeCoreAsync(nodeId, collectGarbage: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task ExportBackupNodeAsync(
        Guid nodeId,
        string destinationZipPath,
        IProgress<SaveImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var node = await RequireBackupNodeAsync(nodeId, cancellationToken).ConfigureAwait(false);
        var profile = await RequireProfileAsync(node.ProfileId, cancellationToken).ConfigureAwait(false);
        var files = await snapshotStore!.GetPackageFilesAsync(
            node.ManifestRelativePath, "profile", cancellationToken).ConfigureAwait(false);
        var manifest = new PackageManifest(
            PackageSchemaVersion,
            new PackageProfile(
                $"{profile.DisplayName} · {node.LastSavedAtUtc?.ToLocalTime():yyyy-MM-dd HH-mm}",
                profile.IsFavorite,
                node.FirstSeenAtUtc),
            [],
            files.Select(file => new PackageFile(file.Path, file.Sha256, file.Length)).ToList());
        await WritePackageAsync(
            destinationZipPath,
            manifest,
            [],
            [new ChunkArchiveSource(node.ManifestRelativePath, "profile")],
            progress,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SaveBackupNode>> ImportExistingBackupNodesAsync(
        Guid profileId,
        IReadOnlyCollection<string> filePaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await EnterForegroundGateAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!await coordinator!.IsSafeToSwitch(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("The game is running or save data is still being written.");
            }

            var profile = await RequireProfileAsync(profileId, cancellationToken).ConfigureAwait(false);
            foreach (var path in filePaths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var fullPath = Path.GetFullPath(path);
                _ = GetSafeProfileFilePath(profile.PhysicalPath, Path.GetRelativePath(profile.PhysicalPath, fullPath));
                await CaptureBackupNodeCoreAsync(
                    profile,
                    fullPath,
                    SaveBackupImportanceSource.Manual,
                    null,
                    cancellationToken).ConfigureAwait(false);
            }

            await PruneBackupNodesCoreAsync(profileId, cancellationToken).ConfigureAwait(false);
            return await GetBackupNodesCoreAsync(profileId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<SaveSnapshotConversionPreview> PreviewFullSnapshotConversionAsync(
        Guid snapshotId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = await RequireSnapshotAsync(snapshotId, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ChunkedSnapshotStore.NodeManifestEntry> entries;
        if (snapshot.StorageKind == SaveSnapshotStorageKind.ChunkedManifest)
        {
            entries = await snapshotStore!.GetManifestEntriesAsync(
                snapshot.ManifestRelativePath ?? throw new InvalidDataException("Snapshot manifest path is missing."),
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            entries = EnumerateSafeWhsFiles(snapshot.PhysicalPath)
                .Select(file => new ChunkedSnapshotStore.NodeManifestEntry(
                    NormalizeNodeRelativePath(Path.GetRelativePath(snapshot.PhysicalPath, file.FullName)),
                    string.Empty,
                    file.Length))
                .ToList();
        }

        var whs = entries
            .Where(entry => string.Equals(Path.GetExtension(entry.RelativePath), ".whs", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(entry => GetSaveSequence(entry.RelativePath))
            .ThenByDescending(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var recommendedRecent = whs.Where(entry => !IsCriticalDecision(entry.RelativePath)).Take(10)
            .Select(entry => entry.RelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = whs.Select(entry => new SaveSnapshotConversionFile(
            entry.RelativePath,
            entry.Length,
            null,
            InferBackupNodeType(entry.RelativePath),
            IsCriticalDecision(entry.RelativePath) || recommendedRecent.Contains(entry.RelativePath),
            IsCriticalDecision(entry.RelativePath))).ToList();
        var selectedBytes = files.Where(file => file.IsRecommended).Sum(file => file.Length);
        return new SaveSnapshotConversionPreview(
            snapshot.Id,
            files,
            snapshot.TotalBytes,
            selectedBytes,
            Math.Max(0, snapshot.TotalBytes - selectedBytes));
    }

    public async Task<IReadOnlyList<SaveBackupNode>> ConvertFullSnapshotToNodesAsync(
        Guid snapshotId,
        IReadOnlyCollection<string> selectedRelativePaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selectedRelativePaths);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await EnterForegroundGateAsync(cancellationToken).ConfigureAwait(false);
        var temporaryFiles = new List<string>();
        try
        {
            var snapshot = await RequireSnapshotAsync(snapshotId, cancellationToken).ConfigureAwait(false);
            var profile = await RequireProfileAsync(snapshot.ProfileId, cancellationToken).ConfigureAwait(false);
            var available = (await GetSnapshotWhsPathsAsync(snapshot, cancellationToken).ConfigureAwait(false))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var selected = selectedRelativePaths
                .Select(NormalizeNodeRelativePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (selected.Count == 0 || selected.Any(path => !available.Contains(path)))
            {
                throw new InvalidDataException("The conversion selection contains an unknown save file.");
            }

            foreach (var relativePath in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (snapshot.StorageKind == SaveSnapshotStorageKind.ChunkedManifest)
                {
                    var nodeId = Guid.NewGuid();
                    var capture = await snapshotStore!.CreateDerivedNodeManifestAsync(
                        snapshot.ManifestRelativePath!, relativePath, profile.Id, nodeId, cancellationToken).ConfigureAwait(false);
                    var temp = Path.Combine(GetStagingRootPath(), $"convert-node-{nodeId:N}.whs");
                    temporaryFiles.Add(temp);
                    await snapshotStore.MaterializeFileAsync(capture.ManifestRelativePath, temp, cancellationToken).ConfigureAwait(false);
                    var metadata = parser.ParseMetadata(temp);
                    await InsertCapturedBackupNodeAsync(
                        profile,
                        nodeId,
                        capture,
                        snapshot.CreatedAtUtc,
                        metadata,
                        null,
                        null,
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    var source = GetSafeProfileFilePath(snapshot.PhysicalPath, relativePath);
                    var nodeId = Guid.NewGuid();
                    var capture = await snapshotStore!.CaptureFileAsync(
                        snapshot.PhysicalPath, source, profile.Id, nodeId, cancellationToken).ConfigureAwait(false);
                    await InsertCapturedBackupNodeAsync(
                        profile,
                        nodeId,
                        capture,
                        snapshot.CreatedAtUtc,
                        parser.ParseMetadata(source),
                        null,
                        null,
                        cancellationToken).ConfigureAwait(false);
                }
            }

            await DeleteSnapshotCoreAsync(snapshotId, cancellationToken).ConfigureAwait(false);
            await PruneBackupNodesCoreAsync(profile.Id, cancellationToken).ConfigureAwait(false);
            return await GetBackupNodesCoreAsync(profile.Id, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            foreach (var path in temporaryFiles)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }

            gate.Release();
        }
    }

    private async Task ReconcileBackupNodesCoreAsync(SaveProfile profile, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(profile.PhysicalPath))
        {
            return;
        }

        var before = EnumerateSafeWhsFiles(profile.PhysicalPath)
            .ToDictionary(file => NormalizeNodeRelativePath(Path.GetRelativePath(profile.PhysicalPath, file.FullName)), ToFileState, StringComparer.OrdinalIgnoreCase);
        if (before.Count == 0)
        {
            await MarkBackupLibraryScannedAsync(
                profile.Id,
                initialized: true,
                new Dictionary<string, BackupFileState>(StringComparer.OrdinalIgnoreCase),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        await Task.Delay(BackupFileStabilityDelay, cancellationToken).ConfigureAwait(false);
        var after = EnumerateSafeWhsFiles(profile.PhysicalPath)
            .ToDictionary(file => NormalizeNodeRelativePath(Path.GetRelativePath(profile.PhysicalPath, file.FullName)), ToFileState, StringComparer.OrdinalIgnoreCase);
        var stable = after
            .Where(pair => before.TryGetValue(pair.Key, out var prior) && prior.Length == pair.Value.Length && prior.LastWriteUtc == pair.Value.LastWriteUtc)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

        await using var connection = connectionFactory.CreateConnection();
        var initialized = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM SaveBackupLibraryStates WHERE ProfileId = @ProfileId;",
            new { ProfileId = profile.Id.ToString("D") },
            cancellationToken: cancellationToken)).ConfigureAwait(false) > 0;
        var observations = (await connection.QueryAsync<BackupObservationRow>(new CommandDefinition(
            "SELECT SourceRelativePath, Length, LastWriteUtc FROM SaveBackupFileObservations WHERE ProfileId = @ProfileId;",
            new { ProfileId = profile.Id.ToString("D") },
            cancellationToken: cancellationToken)).ConfigureAwait(false))
            .ToDictionary(row => row.SourceRelativePath, StringComparer.OrdinalIgnoreCase);

        IReadOnlyList<BackupFileCandidate> candidates;
        if (!initialized)
        {
            var parsed = stable.Select(pair =>
            {
                var metadata = parser.ParseMetadata(pair.Value.FullPath);
                return new BackupFileCandidate(pair.Key, pair.Value, metadata);
            }).ToList();
            var recent = parsed
                .Where(candidate => !IsCriticalDecision(candidate.RelativePath))
                .OrderByDescending(candidate => candidate.Metadata.LastSavedAtUtc ?? candidate.State.LastWriteUtc)
                .Take(Math.Clamp(settings!.BackupNodeRetention, 1, 200));
            candidates = parsed
                .Where(candidate => IsCriticalDecision(candidate.RelativePath))
                .Concat(recent)
                .DistinctBy(candidate => candidate.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        else
        {
            candidates = stable
                .Where(pair => !observations.TryGetValue(pair.Key, out var observation)
                    || observation.Length != pair.Value.Length
                    || !StringComparer.Ordinal.Equals(observation.LastWriteUtc, FormatDate(pair.Value.LastWriteUtc)))
                .Select(pair => new BackupFileCandidate(pair.Key, pair.Value, parser.ParseMetadata(pair.Value.FullPath)))
                .ToList();
        }

        foreach (var candidate in candidates)
        {
            try
            {
                await CaptureBackupNodeCoreAsync(
                    profile,
                    candidate.State.FullPath,
                    null,
                    null,
                    cancellationToken,
                    candidate.Metadata).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                logger.Information(ex, "Save file changed while being captured and will be retried: {SavePath}", candidate.State.FullPath);
            }
        }

        await MarkBackupLibraryScannedAsync(profile.Id, initialized: true, stable, cancellationToken).ConfigureAwait(false);
        await PruneBackupNodesCoreAsync(profile.Id, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SaveBackupNode> CaptureBackupNodeCoreAsync(
        SaveProfile profile,
        string sourcePath,
        SaveBackupImportanceSource? importanceOverride,
        string? note,
        CancellationToken cancellationToken,
        KcdSaveMetadata? knownMetadata = null)
    {
        var fullPath = Path.GetFullPath(sourcePath);
        var relativePath = NormalizeNodeRelativePath(Path.GetRelativePath(profile.PhysicalPath, fullPath));
        _ = GetSafeProfileFilePath(profile.PhysicalPath, relativePath);
        var before = ToFileState(new FileInfo(fullPath));
        var metadata = knownMetadata ?? parser.ParseMetadata(fullPath);
        var nodeId = Guid.NewGuid();
        ChunkedSnapshotStore.NodeCaptureResult? capture = null;
        try
        {
            capture = await snapshotStore!.CaptureFileAsync(
                profile.PhysicalPath, fullPath, profile.Id, nodeId, cancellationToken).ConfigureAwait(false);
            var after = ToFileState(new FileInfo(fullPath));
            if (before.Length != after.Length || before.LastWriteUtc != after.LastWriteUtc)
            {
                throw new IOException("The save file changed while it was being captured.");
            }

            return await InsertCapturedBackupNodeAsync(
                profile,
                nodeId,
                capture,
                after.LastWriteUtc,
                metadata,
                importanceOverride,
                note,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (capture is not null)
            {
                await snapshotStore!.DeleteManifestAsync(capture.ManifestRelativePath, CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }
    }

    private async Task<SaveBackupNode> InsertCapturedBackupNodeAsync(
        SaveProfile profile,
        Guid nodeId,
        ChunkedSnapshotStore.NodeCaptureResult capture,
        DateTimeOffset sourceLastWriteUtc,
        KcdSaveMetadata metadata,
        SaveBackupImportanceSource? importanceOverride,
        string? note,
        CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        var existingRow = await connection.QuerySingleOrDefaultAsync<BackupNodeRow>(new CommandDefinition(
            BackupNodeSelectSql + " WHERE ProfileId = @ProfileId AND ContentSha256 = @ContentSha256;",
            new { ProfileId = profile.Id.ToString("D"), ContentSha256 = capture.Sha256 },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (existingRow is not null)
        {
            await snapshotStore!.DeleteManifestAsync(capture.ManifestRelativePath, CancellationToken.None).ConfigureAwait(false);
            var protect = importanceOverride is not null;
            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE SaveBackupNodes
                SET LastSeenAtUtc = @LastSeenAtUtc,
                    SourceRelativePath = @SourceRelativePath,
                    SourceLastWriteUtc = @SourceLastWriteUtc,
                    IsImportant = CASE WHEN @Protect = 1 THEN 1 ELSE IsImportant END,
                    ImportanceSource = CASE WHEN @Protect = 1 THEN @ImportanceSource ELSE ImportanceSource END,
                    ProtectedAtUtc = CASE WHEN @Protect = 1 THEN @ProtectedAtUtc ELSE ProtectedAtUtc END,
                    Note = CASE WHEN @Note IS NOT NULL THEN @Note ELSE Note END
                WHERE Id = @Id;
                """,
                new
                {
                    Id = existingRow.Id,
                    LastSeenAtUtc = FormatDate(DateTimeOffset.UtcNow),
                    SourceRelativePath = capture.RelativePath,
                    SourceLastWriteUtc = FormatDate(sourceLastWriteUtc),
                    Protect = protect ? 1 : 0,
                    ImportanceSource = (importanceOverride ?? SaveBackupImportanceSource.None).ToString(),
                    ProtectedAtUtc = protect ? FormatDate(DateTimeOffset.UtcNow) : null,
                    Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim()
                },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            return await RequireBackupNodeAsync(Guid.Parse(existingRow.Id), cancellationToken).ConfigureAwait(false);
        }

        var importance = importanceOverride ?? DetermineAutomaticImportance(capture.RelativePath, metadata.Type);
        var isImportant = importance != SaveBackupImportanceSource.None;
        var now = DateTimeOffset.UtcNow;
        var display = metadata.DisplayData;
        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO SaveBackupNodes
                (Id, ProfileId, SourceRelativePath, ContentSha256, ManifestRelativePath, TotalBytes,
                 FirstSeenAtUtc, LastSeenAtUtc, SourceLastWriteUtc, LastSavedAtUtc, GameSaveName, PlayTimeSeconds,
                 HenryLevel, CurrentLocation, ActiveQuest, GroschenCount, PlayerStatusEffects, SaveType, GameVersion,
                 IsImportant, ImportanceSource, ProtectedAtUtc, Note, Health, HealthMessage)
                VALUES
                (@Id, @ProfileId, @SourceRelativePath, @ContentSha256, @ManifestRelativePath, @TotalBytes,
                 @FirstSeenAtUtc, @LastSeenAtUtc, @SourceLastWriteUtc, @LastSavedAtUtc, @GameSaveName, @PlayTimeSeconds,
                 @HenryLevel, @CurrentLocation, @ActiveQuest, @GroschenCount, @PlayerStatusEffects, @SaveType, @GameVersion,
                 @IsImportant, @ImportanceSource, @ProtectedAtUtc, @Note, @Health, NULL);
                """,
                new
                {
                    Id = nodeId.ToString("D"),
                    ProfileId = profile.Id.ToString("D"),
                    SourceRelativePath = capture.RelativePath,
                    ContentSha256 = capture.Sha256,
                    ManifestRelativePath = capture.ManifestRelativePath,
                    TotalBytes = capture.TotalBytes,
                    FirstSeenAtUtc = FormatDate(now),
                    LastSeenAtUtc = FormatDate(now),
                    SourceLastWriteUtc = FormatDate(sourceLastWriteUtc),
                    LastSavedAtUtc = FormatDate(metadata.LastSavedAtUtc),
                    metadata.GameSaveName,
                    PlayTimeSeconds = metadata.PlayTime is null ? null : (long?)metadata.PlayTime.Value.TotalSeconds,
                    HenryLevel = display?.HenryLevel,
                    CurrentLocation = display?.CurrentLocation,
                    ActiveQuest = display?.ActiveQuest,
                    GroschenCount = display?.GroschenCount,
                    PlayerStatusEffects = JoinEffects(display),
                    SaveType = metadata.Type?.ToString(),
                    metadata.GameVersion,
                    IsImportant = isImportant ? 1 : 0,
                    ImportanceSource = importance.ToString(),
                    ProtectedAtUtc = isImportant ? FormatDate(now) : null,
                    Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
                    Health = SaveBackupNodeHealth.Healthy.ToString()
                },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
        catch
        {
            await snapshotStore!.DeleteManifestAsync(capture.ManifestRelativePath, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        return await RequireBackupNodeAsync(nodeId, cancellationToken).ConfigureAwait(false);
    }

    private SaveBackupImportanceSource DetermineAutomaticImportance(string relativePath, SaveType? type)
    {
        if (settings!.AutoMarkCriticalSaveNodesImportant && IsCriticalDecision(relativePath))
        {
            return SaveBackupImportanceSource.AutomaticRule;
        }

        var name = Path.GetFileNameWithoutExtension(relativePath);
        if (settings.AutoMarkManualSaveNodesImportant
            && (type == SaveType.Potion
                || name.StartsWith("save", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("permanent", StringComparison.OrdinalIgnoreCase)))
        {
            return SaveBackupImportanceSource.AutomaticRule;
        }

        return SaveBackupImportanceSource.None;
    }

    private async Task PruneBackupNodesCoreAsync(Guid profileId, CancellationToken cancellationToken)
    {
        var keep = Math.Clamp(settings!.BackupNodeRetention, 1, 200);
        await using var connection = connectionFactory.CreateConnection();
        var ids = (await connection.QueryAsync<string>(new CommandDefinition(
            """
            SELECT Id FROM SaveBackupNodes
            WHERE ProfileId = @ProfileId AND IsImportant = 0
            ORDER BY COALESCE(LastSavedAtUtc, FirstSeenAtUtc) DESC, FirstSeenAtUtc DESC;
            """,
            new { ProfileId = profileId.ToString("D") },
            cancellationToken: cancellationToken)).ConfigureAwait(false)).Skip(keep).ToList();
        foreach (var id in ids)
        {
            await DeleteBackupNodeCoreAsync(Guid.Parse(id), collectGarbage: false, cancellationToken).ConfigureAwait(false);
        }

        if (ids.Count > 0)
        {
            await CollectSnapshotGarbageSafelyAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task DeleteBackupNodeCoreAsync(Guid nodeId, bool collectGarbage, CancellationToken cancellationToken)
    {
        var node = await RequireBackupNodeAsync(nodeId, cancellationToken).ConfigureAwait(false);
        var manifest = snapshotStore!.GetManifestFullPath(node.ManifestRelativePath);
        string? trash = null;
        try
        {
            if (File.Exists(manifest))
            {
                Directory.CreateDirectory(GetStagingRootPath());
                trash = Path.Combine(GetStagingRootPath(), $"delete-node-{Guid.NewGuid():N}.json");
                File.Move(manifest, trash);
            }

            await using var connection = connectionFactory.CreateConnection();
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM SaveBackupNodes WHERE Id = @Id;",
                new { Id = nodeId.ToString("D") },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
            if (trash is not null && File.Exists(trash))
            {
                File.Delete(trash);
                trash = null;
            }

            if (collectGarbage)
            {
                await CollectSnapshotGarbageSafelyAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            if (trash is not null && File.Exists(trash) && !File.Exists(manifest))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
                File.Move(trash, manifest);
            }

            throw;
        }
    }

    private async Task MarkBackupLibraryScannedAsync(
        Guid profileId,
        bool initialized,
        IReadOnlyDictionary<string, BackupFileState> files,
        CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in files)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO SaveBackupFileObservations
                    (ProfileId, SourceRelativePath, Length, LastWriteUtc, LastObservedAtUtc)
                VALUES (@ProfileId, @SourceRelativePath, @Length, @LastWriteUtc, @LastObservedAtUtc)
                ON CONFLICT(ProfileId, SourceRelativePath) DO UPDATE SET
                    Length = excluded.Length,
                    LastWriteUtc = excluded.LastWriteUtc,
                    LastObservedAtUtc = excluded.LastObservedAtUtc;
                """,
                new
                {
                    ProfileId = profileId.ToString("D"),
                    SourceRelativePath = pair.Key,
                    pair.Value.Length,
                    LastWriteUtc = FormatDate(pair.Value.LastWriteUtc),
                    LastObservedAtUtc = FormatDate(now)
                },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        if (initialized)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO SaveBackupLibraryStates (ProfileId, InitializedAtUtc, LastScanAtUtc, RuleVersion)
                VALUES (@ProfileId, @Now, @Now, @RuleVersion)
                ON CONFLICT(ProfileId) DO UPDATE SET LastScanAtUtc = excluded.LastScanAtUtc, RuleVersion = excluded.RuleVersion;
                """,
                new { ProfileId = profileId.ToString("D"), Now = FormatDate(now), RuleVersion = BackupLibraryRuleVersion },
                transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<SaveBackupNode>> GetBackupNodesCoreAsync(Guid profileId, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        var rows = await connection.QueryAsync<BackupNodeRow>(new CommandDefinition(
            BackupNodeSelectSql +
            " WHERE ProfileId = @ProfileId ORDER BY IsImportant DESC, COALESCE(LastSavedAtUtc, FirstSeenAtUtc) DESC;",
            new { ProfileId = profileId.ToString("D") },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.Select(ToBackupNode).ToList();
    }

    private async Task<SaveBackupNode> RequireBackupNodeAsync(Guid nodeId, CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory.CreateConnection();
        var row = await connection.QuerySingleOrDefaultAsync<BackupNodeRow>(new CommandDefinition(
            BackupNodeSelectSql + " WHERE Id = @Id;",
            new { Id = nodeId.ToString("D") },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return row is null ? throw new KeyNotFoundException("Backup node was not found.") : ToBackupNode(row);
    }

    private async Task<IReadOnlyList<string>> GetSnapshotWhsPathsAsync(SaveSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (snapshot.StorageKind == SaveSnapshotStorageKind.ChunkedManifest)
        {
            return (await snapshotStore!.GetManifestEntriesAsync(snapshot.ManifestRelativePath!, cancellationToken).ConfigureAwait(false))
                .Where(entry => string.Equals(Path.GetExtension(entry.RelativePath), ".whs", StringComparison.OrdinalIgnoreCase))
                .Select(entry => entry.RelativePath)
                .ToList();
        }

        return EnumerateSafeWhsFiles(snapshot.PhysicalPath)
            .Select(file => NormalizeNodeRelativePath(Path.GetRelativePath(snapshot.PhysicalPath, file.FullName)))
            .ToList();
    }

    private static IReadOnlyList<FileInfo> EnumerateSafeWhsFiles(string root)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!Directory.Exists(normalizedRoot))
        {
            return [];
        }

        var files = new List<FileInfo>();
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(normalizedRoot));
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException($"Save directory contains a reparse point: {entry.FullName}");
                }

                if (entry is DirectoryInfo child)
                {
                    pending.Push(child);
                }
                else if (entry is FileInfo file && string.Equals(file.Extension, ".whs", StringComparison.OrdinalIgnoreCase))
                {
                    files.Add(file);
                }
            }
        }

        return files;
    }

    private static string GetSafeProfileFilePath(string root, string relativePath)
    {
        var normalized = NormalizeNodeRelativePath(relativePath);
        var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullPath = Path.GetFullPath(Path.Combine(rootFull, normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("The save node path is outside the profile.");
        }

        EnsureNoReparsePoint(rootFull, fullPath);

        return fullPath;
    }

    private static void EnsureNoReparsePoint(string root, string path)
    {
        var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var current = new FileInfo(path).Directory;
        while (current is not null
            && current.FullName.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)
            && !StringComparer.OrdinalIgnoreCase.Equals(current.FullName, rootFull))
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"Save node path crosses a reparse point: {current.FullName}");
            }

            current = current.Parent;
        }
    }

    private static string NormalizeNodeRelativePath(string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        if (string.IsNullOrWhiteSpace(normalized)
            || Path.IsPathRooted(path)
            || normalized.Split('/').Any(part => part is "" or "." or "..")
            || !string.Equals(Path.GetExtension(normalized), ".whs", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Unsafe save node path: {path}");
        }

        return normalized;
    }

    private static BackupFileState ToFileState(FileInfo file)
    {
        file.Refresh();
        if (!file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new FileNotFoundException("Save file is unavailable.", file.FullName);
        }

        return new BackupFileState(
            file.FullName,
            file.Length,
            new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero));
    }

    private static bool IsCriticalDecision(string relativePath) =>
        Path.GetFileNameWithoutExtension(relativePath).StartsWith("crucialdecision", StringComparison.OrdinalIgnoreCase);

    private static int GetSaveSequence(string relativePath)
    {
        var name = Path.GetFileNameWithoutExtension(relativePath);
        var index = name.Length - 1;
        while (index >= 0 && char.IsDigit(name[index]))
        {
            index--;
        }

        return int.TryParse(name[(index + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : -1;
    }

    private static SaveType? InferBackupNodeType(string relativePath)
    {
        var name = Path.GetFileNameWithoutExtension(relativePath);
        if (name.StartsWith("auto", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("crucialdecision", StringComparison.OrdinalIgnoreCase))
        {
            return SaveType.Auto;
        }

        if (name.StartsWith("exit", StringComparison.OrdinalIgnoreCase))
        {
            return SaveType.Exit;
        }

        if (name.StartsWith("save", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("permanent", StringComparison.OrdinalIgnoreCase))
        {
            return SaveType.Potion;
        }

        return null;
    }

    private static SaveBackupNode ToBackupNode(BackupNodeRow row)
    {
        var display = CreateDisplayInfo(row.HenryLevel, row.CurrentLocation, row.ActiveQuest, row.GroschenCount, row.PlayerStatusEffects);
        return new SaveBackupNode(
            Guid.Parse(row.Id),
            Guid.Parse(row.ProfileId),
            row.SourceRelativePath,
            row.ContentSha256,
            row.ManifestRelativePath,
            row.TotalBytes,
            ParseDate(row.FirstSeenAtUtc) ?? DateTimeOffset.MinValue,
            ParseDate(row.LastSeenAtUtc) ?? DateTimeOffset.MinValue,
            ParseDate(row.LastSavedAtUtc),
            row.GameSaveName,
            row.PlayTimeSeconds is null ? null : TimeSpan.FromSeconds(row.PlayTimeSeconds.Value),
            display,
            ParseSaveType(row.SaveType),
            row.GameVersion,
            row.IsImportant != 0,
            Enum.TryParse<SaveBackupImportanceSource>(row.ImportanceSource, out var importance) ? importance : SaveBackupImportanceSource.None,
            ParseDate(row.ProtectedAtUtc),
            row.Note,
            Enum.TryParse<SaveBackupNodeHealth>(row.Health, out var health) ? health : SaveBackupNodeHealth.Corrupt,
            row.HealthMessage);
    }

    private const string BackupNodeSelectSql = """
        SELECT Id, ProfileId, SourceRelativePath, ContentSha256, ManifestRelativePath, TotalBytes,
               FirstSeenAtUtc, LastSeenAtUtc, SourceLastWriteUtc, LastSavedAtUtc, GameSaveName, PlayTimeSeconds,
               HenryLevel, CurrentLocation, ActiveQuest, GroschenCount, PlayerStatusEffects, SaveType, GameVersion,
               IsImportant, ImportanceSource, ProtectedAtUtc, Note, Health, HealthMessage
        FROM SaveBackupNodes
        """;

    private sealed class BackupNodeRow
    {
        public string Id { get; init; } = string.Empty;
        public string ProfileId { get; init; } = string.Empty;
        public string SourceRelativePath { get; init; } = string.Empty;
        public string ContentSha256 { get; init; } = string.Empty;
        public string ManifestRelativePath { get; init; } = string.Empty;
        public long TotalBytes { get; init; }
        public string FirstSeenAtUtc { get; init; } = string.Empty;
        public string LastSeenAtUtc { get; init; } = string.Empty;
        public string SourceLastWriteUtc { get; init; } = string.Empty;
        public string? LastSavedAtUtc { get; init; }
        public string? GameSaveName { get; init; }
        public long? PlayTimeSeconds { get; init; }
        public int? HenryLevel { get; init; }
        public string? CurrentLocation { get; init; }
        public string? ActiveQuest { get; init; }
        public int? GroschenCount { get; init; }
        public string? PlayerStatusEffects { get; init; }
        public string? SaveType { get; init; }
        public string? GameVersion { get; init; }
        public int IsImportant { get; init; }
        public string ImportanceSource { get; init; } = SaveBackupImportanceSource.None.ToString();
        public string? ProtectedAtUtc { get; init; }
        public string? Note { get; init; }
        public string Health { get; init; } = SaveBackupNodeHealth.Healthy.ToString();
        public string? HealthMessage { get; init; }
    }

    private sealed class BackupNodeStorageRow
    {
        public string ManifestRelativePath { get; init; } = string.Empty;
        public long TotalBytes { get; init; }
        public int IsImportant { get; init; }
    }

    private sealed class BackupObservationRow
    {
        public string SourceRelativePath { get; init; } = string.Empty;
        public long Length { get; init; }
        public string LastWriteUtc { get; init; } = string.Empty;
    }

    private sealed record BackupFileState(string FullPath, long Length, DateTimeOffset LastWriteUtc);
    private sealed record BackupFileCandidate(string RelativePath, BackupFileState State, KcdSaveMetadata Metadata);
}
