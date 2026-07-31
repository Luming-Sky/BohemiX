namespace BohemiX.Core.Models.Saves;

public sealed record SaveSnapshotStorageStats(
    long LogicalBytes,
    long PhysicalBytes,
    long LegacyBytes,
    int PendingMigrationCount,
    SaveSnapshotMaintenanceState MaintenanceState = SaveSnapshotMaintenanceState.Ready)
{
    public long SavedBytes => Math.Max(0, LogicalBytes - PhysicalBytes);
}

public enum SaveSnapshotMaintenanceState
{
    Ready,
    Migrating,
    PausedLowSpace,
    Complete,
    Faulted
}
