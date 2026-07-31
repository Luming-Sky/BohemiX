using BohemiX.Core.Models.Saves;

namespace BohemiX.Core.Services.Saves;

/// <summary>
/// Parses KCD2 save metadata from Vault slots without mutating save files.
/// </summary>
public interface IKcdSaveParser
{
    /// <summary>
    /// Reads metadata.xml or a compatible KCD2 metadata file from a slot path.
    /// </summary>
    KcdSaveMetadata ParseMetadata(string slotPath);

    /// <summary>
    /// Reserved binary parsing entry point for future world-state extraction.
    /// </summary>
    KcdWorldState ExtractWorldState(string slotPath);
}
