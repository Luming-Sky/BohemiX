using BohemiX.Core.Models;

namespace BohemiX.Core.Services;

/// <summary>
/// Produces per-mod health diagnostics from the catalog and resolved mount plan for UI status and launch checks.
/// </summary>
public interface IModHealthAnalyzer
{
    /// <summary>
    /// Analyzes mod package state, duplicate virtual paths, and conflict participation without mutating files.
    /// </summary>
    ModCatalogHealthReport Analyze(IEnumerable<ModManifest> mods, ModMountPlan mountPlan);
}
