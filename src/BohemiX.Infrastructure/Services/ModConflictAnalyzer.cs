using BohemiX.Core.Models;
using BohemiX.Core.Services;

namespace BohemiX.Infrastructure.Services;

public sealed class ModConflictAnalyzer : IModConflictAnalyzer
{
    public IReadOnlyList<ModConflict> AnalyzeConflicts(IEnumerable<ModManifest> mods)
    {
        return mods
            .SelectMany(mod => mod.Files.Select(file => new
            {
                ModId = mod.Id,
                mod.LoadOrder,
                NormalizedPath = NormalizeVirtualPath(file.RelativePath)
            }))
            .GroupBy(entry => entry.NormalizedPath, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Select(entry => entry.ModId).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            .Select(group =>
            {
                var participants = group
                    .GroupBy(entry => entry.ModId, StringComparer.OrdinalIgnoreCase)
                    .Select(participant => new
                    {
                        ModId = participant.Key,
                        LoadOrder = participant.Max(entry => entry.LoadOrder)
                    })
                    .ToArray();

                var winner = participants
                    .OrderByDescending(participant => participant.LoadOrder)
                    .ThenBy(participant => participant.ModId, StringComparer.OrdinalIgnoreCase)
                    .First();

                return new ModConflict(
                    group.Key,
                    participants.Select(participant => participant.ModId).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                    participants
                        .OrderBy(participant => participant.LoadOrder)
                        .ThenBy(participant => participant.ModId, StringComparer.OrdinalIgnoreCase)
                        .Select(participant => participant.ModId)
                        .ToArray(),
                    winner.ModId);
            })
            .OrderBy(conflict => conflict.NormalizedVirtualPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string NormalizeVirtualPath(string relativePath)
    {
        return relativePath
            .Replace('\\', '/')
            .Trim()
            .TrimStart('/')
            .ToUpperInvariant();
    }
}
