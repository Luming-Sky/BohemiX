namespace BohemiX.Core.Models;

public sealed record ModLaunchPreflightOptions(
    string GameExecutablePath,
    bool RequireReviewedConflicts,
    bool EnableVfs);
