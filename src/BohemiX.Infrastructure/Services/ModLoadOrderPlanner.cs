using BohemiX.Core.Models;
using BohemiX.Core.Services;

namespace BohemiX.Infrastructure.Services;

public sealed class ModLoadOrderPlanner : IModLoadOrderPlanner
{
    public ModLoadOrderPlan MoveToIndex(IEnumerable<ModManifest> mods, string modId, int targetIndex)
    {
        var orderedMods = OrderMods(mods);
        var orderedModIds = orderedMods.Select(mod => mod.Id).ToArray();
        var fromIndex = Array.FindIndex(
            orderedModIds,
            id => string.Equals(id, modId, StringComparison.OrdinalIgnoreCase));

        if (fromIndex < 0)
        {
            return new ModLoadOrderPlan(orderedModIds, modId, -1, -1, false);
        }

        var clampedTargetIndex = Math.Clamp(targetIndex, 0, orderedModIds.Length - 1);
        if (fromIndex == clampedTargetIndex)
        {
            return new ModLoadOrderPlan(orderedModIds, orderedModIds[fromIndex], fromIndex, clampedTargetIndex, false);
        }

        var reordered = orderedModIds.ToList();
        var movedModId = reordered[fromIndex];
        reordered.RemoveAt(fromIndex);
        reordered.Insert(clampedTargetIndex, movedModId);

        return new ModLoadOrderPlan(reordered.ToArray(), movedModId, fromIndex, clampedTargetIndex, true);
    }

    public ModLoadOrderPlan MoveByOffset(IEnumerable<ModManifest> mods, string modId, int offset)
    {
        var orderedMods = OrderMods(mods);
        var orderedModIds = orderedMods.Select(mod => mod.Id).ToArray();
        var fromIndex = Array.FindIndex(
            orderedModIds,
            id => string.Equals(id, modId, StringComparison.OrdinalIgnoreCase));

        if (fromIndex < 0)
        {
            return new ModLoadOrderPlan(orderedModIds, modId, -1, -1, false);
        }

        return MoveToIndex(orderedMods, orderedModIds[fromIndex], fromIndex + offset);
    }

    private static ModManifest[] OrderMods(IEnumerable<ModManifest> mods)
    {
        return mods
            .OrderBy(mod => mod.LoadOrder)
            .ThenBy(mod => mod.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
