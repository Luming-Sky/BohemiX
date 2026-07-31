using BohemiX.Core.Models;
using BohemiX.Core.Services;

namespace BohemiX.Infrastructure.Services;

public sealed class ModHealthAnalyzer : IModHealthAnalyzer
{
    public ModCatalogHealthReport Analyze(IEnumerable<ModManifest> mods, ModMountPlan mountPlan)
    {
        var allMods = mods
            .OrderBy(mod => mod.LoadOrder)
            .ThenBy(mod => mod.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var conflictsByMod = mountPlan.Conflicts
            .SelectMany(conflict => conflict.ModIds.Select(modId => new
            {
                ModId = modId,
                conflict.WinningModId
            }))
            .GroupBy(entry => entry.ModId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => new ConflictParticipation(
                    group.Count(),
                    group.Count(entry => string.Equals(entry.ModId, entry.WinningModId, StringComparison.OrdinalIgnoreCase)),
                    group.Count(entry => !string.Equals(entry.ModId, entry.WinningModId, StringComparison.OrdinalIgnoreCase))),
                StringComparer.OrdinalIgnoreCase);

        var reports = allMods
            .Select(mod =>
            {
                conflictsByMod.TryGetValue(mod.Id, out var participation);
                participation ??= new ConflictParticipation(0, 0, 0);

                var duplicateVirtualPathCount = mod.Files
                    .GroupBy(file => NormalizeVirtualPath(file.RelativePath), StringComparer.OrdinalIgnoreCase)
                    .Count(group => group.Count() > 1);
                var issues = BuildIssues(mod, participation, duplicateVirtualPathCount);

                return new ModHealthReport(
                    mod.Id,
                    mod.DisplayName,
                    mod.IsEnabled,
                    mod.Files.Count,
                    participation.ConflictPathCount,
                    participation.WinningConflictPathCount,
                    participation.ShadowedConflictPathCount,
                    duplicateVirtualPathCount,
                    issues);
            })
            .ToArray();

        return new ModCatalogHealthReport(reports);
    }

    private static IReadOnlyList<ModHealthIssue> BuildIssues(
        ModManifest mod,
        ConflictParticipation participation,
        int duplicateVirtualPathCount)
    {
        var issues = new List<ModHealthIssue>();

        if (!Directory.Exists(mod.RootPath))
        {
            issues.Add(new ModHealthIssue(
                "mod.root_missing",
                ModHealthSeverity.Error,
                "The mod root directory is missing."));
        }

        if (mod.Files.Count == 0)
        {
            issues.Add(new ModHealthIssue(
                "mod.empty",
                ModHealthSeverity.Warning,
                "The mod does not expose any mountable files."));
        }

        if (duplicateVirtualPathCount > 0)
        {
            issues.Add(new ModHealthIssue(
                "mod.duplicate_virtual_paths",
                ModHealthSeverity.Warning,
                "The mod contains multiple files that normalize to the same virtual path."));
        }

        if (participation.ShadowedConflictPathCount > 0)
        {
            issues.Add(new ModHealthIssue(
                "mod.shadowed_conflicts",
                ModHealthSeverity.Warning,
                "One or more files are shadowed by later load-order mods."));
        }

        if (!mod.IsEnabled)
        {
            issues.Add(new ModHealthIssue(
                "mod.disabled",
                ModHealthSeverity.Info,
                "The mod is disabled and excluded from VFS resolution."));
        }

        return issues;
    }

    private static string NormalizeVirtualPath(string relativePath)
    {
        return relativePath
            .Replace('\\', '/')
            .Trim()
            .TrimStart('/')
            .ToUpperInvariant();
    }

    private sealed record ConflictParticipation(
        int ConflictPathCount,
        int WinningConflictPathCount,
        int ShadowedConflictPathCount);
}
