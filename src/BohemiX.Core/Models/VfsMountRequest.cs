namespace BohemiX.Core.Models;

public sealed record VfsMountRequest(
    string GameExecutablePath,
    IReadOnlyList<ModManifest> EnabledMods,
    ModMountPlan? MountPlan = null);

public sealed record VfsSessionInfo(
    Guid SessionId,
    string InstanceName,
    string GameRootPath,
    int MappedModCount,
    string NativeVersion,
    DateTimeOffset MountedAtUtc);

public sealed record VfsProcessLaunchRequest(
    string ExecutablePath,
    string? Arguments = null,
    string? WorkingDirectory = null);

public sealed record VfsProcessLaunchResult(
    bool IsStarted,
    int? ProcessId,
    string Message);
