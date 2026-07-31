namespace BohemiX.Core.Models.Saves;

public sealed record SaveBackupLibraryStats(
    int NodeCount,
    int ImportantCount,
    long LogicalBytes,
    long ImportantLogicalBytes,
    long PhysicalBytes,
    int RecentRetention)
{
    public long SavedBytes => Math.Max(0, LogicalBytes - PhysicalBytes);
}
