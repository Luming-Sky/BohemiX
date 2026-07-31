namespace BohemiX.Core.Models.Saves;

/// <summary>
/// Progress snapshot for importing the current game save into the local Vault.
/// </summary>
public sealed record SaveImportProgress(
    string Stage,
    int ProcessedFiles,
    int TotalFiles,
    long ProcessedBytes,
    long TotalBytes,
    string? CurrentEntry)
{
    public double Percent =>
        TotalBytes > 0
            ? Math.Clamp(ProcessedBytes * 100d / TotalBytes, 0d, 100d)
            : TotalFiles > 0
                ? Math.Clamp(ProcessedFiles * 100d / TotalFiles, 0d, 100d)
                : 0d;
}
