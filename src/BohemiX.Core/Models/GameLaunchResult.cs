namespace BohemiX.Core.Models;

public sealed record GameLaunchResult(
    bool IsStarted,
    int? ProcessId,
    string Message,
    Guid? SessionId = null);
