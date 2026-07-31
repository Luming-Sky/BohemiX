namespace BohemiX.Core.Models;

public sealed record ModMountPlan(
    int TotalModCount,
    int EnabledModCount,
    int DisabledModCount,
    int EnabledFileCount,
    int UniqueVirtualPathCount,
    int ShadowedProviderCount,
    IReadOnlyList<ModConflict> Conflicts,
    IReadOnlyList<ModMountPlanEntry> Entries);
