namespace BohemiX.Core.Models;

public sealed record ModManagementSnapshot(
    IReadOnlyList<ModManifest> Mods,
    ModMountPlan MountPlan,
    ModCatalogHealthReport HealthReport,
    IReadOnlyDictionary<string, ModConflictReview> ConflictReviews)
{
    public IReadOnlyList<ModConflict> Conflicts => MountPlan.Conflicts;

    public int TotalModCount => MountPlan.TotalModCount;

    public int EnabledModCount => MountPlan.EnabledModCount;

    public int DisabledModCount => MountPlan.DisabledModCount;

    public int EnabledFileCount => MountPlan.EnabledFileCount;

    public int UniqueVirtualPathCount => MountPlan.UniqueVirtualPathCount;

    public int ConflictCount => MountPlan.Conflicts.Count;

    public int ReviewedConflictCount => Conflicts.Count(conflict =>
        ConflictReviews.TryGetValue(conflict.Fingerprint, out var review) && review.IsReviewed);

    public int PendingConflictCount => ConflictCount - ReviewedConflictCount;

    public int ShadowedProviderCount => MountPlan.ShadowedProviderCount;
}
