using BohemiX.Core.Models;

namespace BohemiX.Core.Services;

/// <summary>
/// Calculates deterministic load-order updates for drag, move, top, and bottom commands without mutating the catalog.
/// </summary>
public interface IModLoadOrderPlanner
{
    /// <summary>
    /// Moves a mod to an absolute index in the current load-order list and returns the ordered mod ids to persist.
    /// </summary>
    ModLoadOrderPlan MoveToIndex(IEnumerable<ModManifest> mods, string modId, int targetIndex);

    /// <summary>
    /// Moves a mod by a relative offset in the current load-order list and returns the ordered mod ids to persist.
    /// </summary>
    ModLoadOrderPlan MoveByOffset(IEnumerable<ModManifest> mods, string modId, int offset);
}
