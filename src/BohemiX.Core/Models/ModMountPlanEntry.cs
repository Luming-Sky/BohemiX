namespace BohemiX.Core.Models;

public sealed record ModMountPlanEntry(
    string NormalizedVirtualPath,
    string WinningModId,
    IReadOnlyList<string> LoadOrderModIds,
    long WinningSizeInBytes,
    string? WinningContentHash);
