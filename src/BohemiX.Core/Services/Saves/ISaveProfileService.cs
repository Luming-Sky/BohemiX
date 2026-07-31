using BohemiX.Core.Models.Saves;

namespace BohemiX.Core.Services.Saves;

/// <summary>
/// Canonical save-profile and immutable-snapshot API. SaveSlot/ISlotManager remain as a
/// compatibility layer for one release and use the same SaveSlots table.
/// </summary>
public interface ISaveProfileService
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SaveProfile>> GetProfilesAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SaveSnapshot>> GetSnapshotsAsync(Guid profileId, CancellationToken cancellationToken = default);

    Task<SaveSnapshotStorageStats> GetSnapshotStorageStatsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SaveBackupNode>> GetBackupNodesAsync(Guid profileId, CancellationToken cancellationToken = default);

    Task<SaveBackupLibraryStats> GetBackupLibraryStatsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SaveBackupNode>> ReconcileBackupNodesAsync(Guid profileId, CancellationToken cancellationToken = default);

    Task SetBackupNodeImportanceAsync(
        Guid nodeId,
        bool isImportant,
        string? note = null,
        CancellationToken cancellationToken = default);

    Task RestoreBackupNodeAsync(Guid nodeId, CancellationToken cancellationToken = default);

    Task DeleteBackupNodeAsync(Guid nodeId, CancellationToken cancellationToken = default);

    Task ExportBackupNodeAsync(
        Guid nodeId,
        string destinationZipPath,
        IProgress<SaveImportProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SaveBackupNode>> ImportExistingBackupNodesAsync(
        Guid profileId,
        IReadOnlyCollection<string> filePaths,
        CancellationToken cancellationToken = default);

    Task<SaveSnapshotConversionPreview> PreviewFullSnapshotConversionAsync(
        Guid snapshotId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SaveBackupNode>> ConvertFullSnapshotToNodesAsync(
        Guid snapshotId,
        IReadOnlyCollection<string> selectedRelativePaths,
        CancellationToken cancellationToken = default);

    Task<SaveProfile?> GetActiveProfileAsync(CancellationToken cancellationToken = default);

    Task<bool> HasUnmanagedSaveAsync(CancellationToken cancellationToken = default);

    Task<bool> IsSafeToChangeAsync(CancellationToken cancellationToken = default);

    Task<SaveProfile> ManageCurrentSaveAsync(
        string? displayName = null,
        IProgress<SaveImportProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<SaveProfile> CloneProfileAsync(
        Guid sourceProfileId,
        string displayName,
        IProgress<SaveImportProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task SwitchProfileAsync(Guid profileId, CancellationToken cancellationToken = default);

    Task<SaveSnapshot?> CreateSnapshotAsync(
        Guid profileId,
        SaveSnapshotTrigger trigger = SaveSnapshotTrigger.Manual,
        string? note = null,
        IProgress<SaveImportProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task RestoreSnapshotAsync(
        Guid snapshotId,
        IProgress<SaveImportProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task RenameProfileAsync(Guid profileId, string displayName, CancellationToken cancellationToken = default);

    Task SetFavoriteAsync(Guid profileId, bool isFavorite, CancellationToken cancellationToken = default);

    Task DeleteProfileAsync(Guid profileId, CancellationToken cancellationToken = default);

    Task DeleteSnapshotAsync(Guid snapshotId, CancellationToken cancellationToken = default);

    Task ExportProfileAsync(
        Guid profileId,
        string destinationZipPath,
        SavePackageExportOptions? options = null,
        IProgress<SaveImportProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task ExportSnapshotAsync(
        Guid snapshotId,
        string destinationZipPath,
        IProgress<SaveImportProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<SaveProfile> ImportPackageAsync(
        string packagePath,
        IProgress<SaveImportProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<SaveSnapshot?> CreateGameExitSnapshotAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores a PNG captured immediately before the game exits as the active
    /// profile's thumbnail. The save payload itself is never modified.
    /// </summary>
    Task<bool> SaveGameExitThumbnailAsync(byte[] pngData, CancellationToken cancellationToken = default);

    string GetOfficialSavePath();

    string GetVaultRootPath();

    string GetSnapshotsRootPath();

    IReadOnlyList<SaveMigrationWarning> MigrationWarnings { get; }
}
