using BohemiX.Core.Models;

namespace BohemiX.Core.Services;

/// <summary>
/// Builds a deterministic VFS mount preview from the current mod catalog state.
/// UI visualizations should consume this plan instead of recalculating file winners locally.
/// </summary>
public interface IModMountPlanBuilder
{
    /// <summary>
    /// Builds a mount plan from all known mods, excluding disabled mods from virtual path resolution.
    /// </summary>
    ModMountPlan BuildPlan(IEnumerable<ModManifest> mods);
}
