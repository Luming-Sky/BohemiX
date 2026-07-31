namespace BohemiX.Core.Models;

public sealed record ModHealthReport(
    string ModId,
    string DisplayName,
    bool IsEnabled,
    int FileCount,
    int ConflictPathCount,
    int WinningConflictPathCount,
    int ShadowedConflictPathCount,
    int DuplicateVirtualPathCount,
    IReadOnlyList<ModHealthIssue> Issues)
{
    public bool HasErrors => Issues.Any(issue => issue.Severity == ModHealthSeverity.Error);

    public bool HasWarnings => Issues.Any(issue => issue.Severity == ModHealthSeverity.Warning);

    public bool HasIssues => Issues.Count > 0;
}
