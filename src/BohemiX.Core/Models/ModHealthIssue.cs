namespace BohemiX.Core.Models;

public sealed record ModHealthIssue(
    string Code,
    ModHealthSeverity Severity,
    string Message);
