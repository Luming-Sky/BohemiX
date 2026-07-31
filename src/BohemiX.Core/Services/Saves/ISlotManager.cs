using BohemiX.Core.Models.Saves;

namespace BohemiX.Core.Services.Saves;

/// <summary>
/// Manages Vault physical directories and SQLite logical slot records.
/// </summary>
public interface ISlotManager
{
    /// <summary>
    /// Moves the real official save directory contents into a generated Vault slot and creates a logical DB record.
    /// </summary>
    Task<SaveSlot> ImportSave(
        string? displayName = null,
        IProgress<SaveImportProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates only the logical display name stored in SQLite.
    /// </summary>
    Task RenameSlot(Guid slotId, string displayName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns all logical save slots with their generated Vault physical paths.
    /// </summary>
    Task<IReadOnlyList<SaveSlot>> GetAllSlots(CancellationToken cancellationToken = default);
}
