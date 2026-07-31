using BohemiX.Core.Models.Saves;

namespace BohemiX.Core.Services.Saves;

/// <summary>
/// Materializes and verifies save restore sources without changing the active profile.
/// </summary>
public interface ISaveRestoreValidationService
{
    Task<SaveRestoreValidationResult> ValidateSnapshotAsync(
        Guid snapshotId,
        CancellationToken cancellationToken = default);

    Task<SaveRestoreValidationResult> ValidateBackupNodeAsync(
        Guid nodeId,
        CancellationToken cancellationToken = default);
}
