namespace BohemiX.Core.Models;

public sealed record GameProcessExitResult(
    bool WasMonitored,
    int ProcessId,
    int? ExitCode,
    string? ErrorMessage = null)
{
    public bool IsAbnormalExit => WasMonitored && ExitCode is not null && ExitCode != 0;
}
