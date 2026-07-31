using BohemiX.Core.Models;
using BohemiX.Core.Services;

namespace BohemiX.Infrastructure.Services;

public sealed class ModLaunchPreflightService : IModLaunchPreflightService
{
    private readonly IModManagementSnapshotService modManagementSnapshotService;

    public ModLaunchPreflightService(IModManagementSnapshotService modManagementSnapshotService)
    {
        this.modManagementSnapshotService = modManagementSnapshotService;
    }

    public async Task<ModLaunchPreflightResult> EvaluateAsync(
        ModLaunchPreflightOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var snapshot = await modManagementSnapshotService.LoadSnapshotAsync(cancellationToken);
        var enabledMods = snapshot.Mods.Where(mod => mod.IsEnabled).ToArray();

        if (options.RequireReviewedConflicts && snapshot.PendingConflictCount > 0)
        {
            return new ModLaunchPreflightResult(
                false,
                $"{snapshot.PendingConflictCount} mod conflicts need review before launch.",
                snapshot,
                enabledMods,
                null);
        }

        var mountRequest = enabledMods.Length > 0
            ? new VfsMountRequest(options.GameExecutablePath, enabledMods, snapshot.MountPlan)
            : null;
        var message = mountRequest is null
            ? "Mod preflight passed; VFS mount is not required."
            : $"Mod preflight passed; {enabledMods.Length} enabled mods are ready for VFS mount.";

        return new ModLaunchPreflightResult(
            true,
            message,
            snapshot,
            enabledMods,
            mountRequest);
    }
}
