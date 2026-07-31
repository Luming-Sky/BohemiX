namespace BohemiX.Core.Models;

public enum GameLaunchSessionStatus
{
    Requested = 0,
    Running = 1,
    Exited = 2,
    Failed = 3,
    MonitoringLost = 4
}

public sealed record GameLaunchSession(
    Guid Id,
    Guid GameEnvironmentId,
    string GameName,
    string ExecutablePath,
    string? LaunchArguments,
    bool UsedSteamProtocol,
    Guid? VfsSessionId,
    int? ProcessId,
    GameLaunchSessionStatus Status,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? ExitedAtUtc,
    int? ExitCode,
    string? FailureMessage);
