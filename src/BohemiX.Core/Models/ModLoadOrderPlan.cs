namespace BohemiX.Core.Models;

public sealed record ModLoadOrderPlan(
    IReadOnlyList<string> OrderedModIds,
    string MovedModId,
    int FromIndex,
    int ToIndex,
    bool Changed);
