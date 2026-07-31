namespace BohemiX.Core.Models;

public sealed record ModLaunchPreflightResult(
    bool CanLaunch,
    string Message,
    ModManagementSnapshot Snapshot,
    IReadOnlyList<ModManifest> EnabledMods,
    VfsMountRequest? MountRequest);
