using BohemiX.Core.Models;

namespace BohemiX.Core.Services;

/// <summary>
/// Detects virtual file conflicts between mods using normalized relative paths.
/// Implementations should be pure and deterministic so UI previews and tests share identical results.
/// </summary>
public interface IModConflictAnalyzer
{
    /// <summary>
    /// Builds a conflict list where two or more mods provide the same normalized virtual file path.
    /// </summary>
    IReadOnlyList<ModConflict> AnalyzeConflicts(IEnumerable<ModManifest> mods);
}

