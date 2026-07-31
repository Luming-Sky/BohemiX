using BohemiX.Core.Models.Saves;

namespace BohemiX.Infrastructure.Services.Saves;

public sealed partial class SaveProfileService
{
    public async Task<SaveRestoreValidationResult> ValidateSnapshotAsync(
        Guid snapshotId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await EnterForegroundGateAsync(cancellationToken).ConfigureAwait(false);
        string? staging = null;
        try
        {
            var issues = new List<SaveRestoreValidationIssue>();
            if (!await coordinator!.IsSafeToSwitch(cancellationToken).ConfigureAwait(false))
            {
                issues.Add(new SaveRestoreValidationIssue(
                    "save-write-active",
                    "The game is running or save files are still being written.",
                    true));
            }

            SaveSnapshot snapshot;
            SaveProfile profile;
            try
            {
                snapshot = await RequireSnapshotAsync(snapshotId, cancellationToken).ConfigureAwait(false);
                profile = await RequireProfileAsync(snapshot.ProfileId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is KeyNotFoundException or InvalidDataException)
            {
                return new SaveRestoreValidationResult(
                    SaveRestoreSourceKind.Snapshot,
                    snapshotId,
                    Guid.Empty,
                    false,
                    null,
                    null,
                    0,
                    0,
                    [new SaveRestoreValidationIssue("source-unavailable", ex.Message, true)]);
            }

            if (!Directory.Exists(profile.PhysicalPath))
            {
                issues.Add(new SaveRestoreValidationIssue(
                    "profile-missing",
                    "The destination save profile directory is missing.",
                    true));
            }

            staging = Path.Combine(GetStagingRootPath(), $"validate-restore-{Guid.NewGuid():N}");
            var materialized = Path.Combine(staging, "snapshot");
            Directory.CreateDirectory(staging);
            try
            {
                if (snapshot.StorageKind == SaveSnapshotStorageKind.ChunkedManifest)
                {
                    if (string.IsNullOrWhiteSpace(snapshot.ManifestRelativePath))
                    {
                        throw new InvalidDataException("The chunked snapshot has no manifest path.");
                    }

                    await snapshotStore!.MaterializeAsync(
                        snapshot.ManifestRelativePath,
                        materialized,
                        null,
                        "Validating restore",
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await CopyDirectoryAsync(
                        snapshot.PhysicalPath,
                        materialized,
                        null,
                        "Validating restore",
                        cancellationToken).ConfigureAwait(false);
                }

                var verifiedFingerprint = await ComputeDirectoryFingerprintAsync(materialized, cancellationToken).ConfigureAwait(false);
                if (!StringComparer.OrdinalIgnoreCase.Equals(verifiedFingerprint, snapshot.ContentFingerprint))
                {
                    issues.Add(new SaveRestoreValidationIssue(
                        "fingerprint-mismatch",
                        "The snapshot content does not match its recorded fingerprint.",
                        true));
                }

                var files = Directory.EnumerateFiles(materialized, "*", SearchOption.AllDirectories).ToArray();
                var totalBytes = files.Sum(path => new FileInfo(path).Length);
                if (files.Length == 0)
                {
                    issues.Add(new SaveRestoreValidationIssue(
                        "empty-snapshot",
                        "The snapshot contains no save files.",
                        true));
                }

                try
                {
                    _ = parser.ParseMetadata(materialized);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    issues.Add(new SaveRestoreValidationIssue(
                        "metadata-unreadable",
                        $"Save metadata could not be read: {ex.Message}",
                        true));
                }

                return new SaveRestoreValidationResult(
                    SaveRestoreSourceKind.Snapshot,
                    snapshot.Id,
                    profile.Id,
                    issues.All(issue => !issue.IsBlocking),
                    snapshot.ContentFingerprint,
                    verifiedFingerprint,
                    files.Length,
                    totalBytes,
                    issues);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                issues.Add(new SaveRestoreValidationIssue("materialization-failed", ex.Message, true));
                return new SaveRestoreValidationResult(
                    SaveRestoreSourceKind.Snapshot,
                    snapshot.Id,
                    profile.Id,
                    false,
                    snapshot.ContentFingerprint,
                    null,
                    0,
                    0,
                    issues);
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

    public async Task<SaveRestoreValidationResult> ValidateBackupNodeAsync(
        Guid nodeId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await EnterForegroundGateAsync(cancellationToken).ConfigureAwait(false);
        string? staging = null;
        try
        {
            var issues = new List<SaveRestoreValidationIssue>();
            if (!await coordinator!.IsSafeToSwitch(cancellationToken).ConfigureAwait(false))
            {
                issues.Add(new SaveRestoreValidationIssue(
                    "save-write-active",
                    "The game is running or save files are still being written.",
                    true));
            }

            SaveBackupNode node;
            SaveProfile profile;
            try
            {
                node = await RequireBackupNodeAsync(nodeId, cancellationToken).ConfigureAwait(false);
                profile = await RequireProfileAsync(node.ProfileId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is KeyNotFoundException or InvalidDataException)
            {
                return new SaveRestoreValidationResult(
                    SaveRestoreSourceKind.BackupNode,
                    nodeId,
                    Guid.Empty,
                    false,
                    null,
                    null,
                    0,
                    0,
                    [new SaveRestoreValidationIssue("source-unavailable", ex.Message, true)]);
            }

            staging = Path.Combine(GetStagingRootPath(), $"validate-node-{Guid.NewGuid():N}");
            Directory.CreateDirectory(staging);
            var materialized = Path.Combine(staging, "node.whs");
            try
            {
                var entry = await snapshotStore!.MaterializeFileAsync(
                    node.ManifestRelativePath,
                    materialized,
                    cancellationToken).ConfigureAwait(false);
                var verifiedHash = await ComputeFileHashAsync(materialized, cancellationToken).ConfigureAwait(false);
                if (!StringComparer.OrdinalIgnoreCase.Equals(entry.Sha256, node.ContentSha256)
                    || !StringComparer.OrdinalIgnoreCase.Equals(verifiedHash, node.ContentSha256))
                {
                    issues.Add(new SaveRestoreValidationIssue(
                        "checksum-mismatch",
                        "The backup file does not match its recorded checksum.",
                        true));
                }

                if (!StringComparer.OrdinalIgnoreCase.Equals(entry.RelativePath, node.SourceRelativePath))
                {
                    issues.Add(new SaveRestoreValidationIssue(
                        "path-mismatch",
                        "The backup manifest points to a different save path.",
                        true));
                }

                _ = GetSafeProfileFilePath(profile.PhysicalPath, node.SourceRelativePath);
                var size = new FileInfo(materialized).Length;
                return new SaveRestoreValidationResult(
                    SaveRestoreSourceKind.BackupNode,
                    node.Id,
                    profile.Id,
                    issues.All(issue => !issue.IsBlocking),
                    node.ContentSha256,
                    verifiedHash,
                    1,
                    size,
                    issues);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
            {
                issues.Add(new SaveRestoreValidationIssue("materialization-failed", ex.Message, true));
                return new SaveRestoreValidationResult(
                    SaveRestoreSourceKind.BackupNode,
                    node.Id,
                    profile.Id,
                    false,
                    node.ContentSha256,
                    null,
                    0,
                    0,
                    issues);
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
}
