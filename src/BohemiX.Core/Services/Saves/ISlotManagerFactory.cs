namespace BohemiX.Core.Services.Saves;

/// <summary>
/// Creates configured save slot managers after the UI has resolved the official KCD2 save directory.
/// </summary>
public interface ISlotManagerFactory
{
    /// <summary>
    /// Creates a slot manager for an official save path, using default BohemiX Vault and database paths when omitted.
    /// </summary>
    ISlotManager Create(string officialSavePath, string? vaultRoot = null, string? databasePath = null);
}
