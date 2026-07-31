using BohemiX.Core.Models;
using BohemiX.Core.Services;

namespace BohemiX.Infrastructure.Services;

public sealed class ModManagementSnapshotService : IModManagementSnapshotService
{
    private readonly IModCatalogService modCatalogService;
    private readonly IModConflictReviewService modConflictReviewService;
    private readonly IModHealthAnalyzer modHealthAnalyzer;
    private readonly IModMountPlanBuilder modMountPlanBuilder;

    public ModManagementSnapshotService(
        IModCatalogService modCatalogService,
        IModConflictReviewService modConflictReviewService,
        IModHealthAnalyzer modHealthAnalyzer,
        IModMountPlanBuilder modMountPlanBuilder)
    {
        this.modCatalogService = modCatalogService;
        this.modConflictReviewService = modConflictReviewService;
        this.modHealthAnalyzer = modHealthAnalyzer;
        this.modMountPlanBuilder = modMountPlanBuilder;
    }

    public async Task<ModManagementSnapshot> LoadSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var mods = await modCatalogService.LoadInstalledModsAsync(cancellationToken);
        var mountPlan = modMountPlanBuilder.BuildPlan(mods);
        var healthReport = modHealthAnalyzer.Analyze(mods, mountPlan);
        var conflictReviews = await modConflictReviewService.LoadReviewsAsync(
            mountPlan.Conflicts.Select(conflict => conflict.Fingerprint),
            cancellationToken);

        return new ModManagementSnapshot(mods, mountPlan, healthReport, conflictReviews);
    }
}
