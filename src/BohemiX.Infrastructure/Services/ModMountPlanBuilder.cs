using BohemiX.Core.Models;
using BohemiX.Core.Services;

namespace BohemiX.Infrastructure.Services;

public sealed class ModMountPlanBuilder : IModMountPlanBuilder
{
    private readonly IModConflictAnalyzer modConflictAnalyzer;

    public ModMountPlanBuilder(IModConflictAnalyzer modConflictAnalyzer)
    {
        this.modConflictAnalyzer = modConflictAnalyzer;
    }

    public ModMountPlan BuildPlan(IEnumerable<ModManifest> mods)
    {
        var allMods = mods.ToArray();
        var enabledMods = allMods.Where(mod => mod.IsEnabled).ToArray();
        var conflicts = modConflictAnalyzer.AnalyzeConflicts(enabledMods);
        var fileProviders = enabledMods
            .SelectMany(mod => mod.Files.Select(file => new FileProvider(
                mod.Id,
                mod.LoadOrder,
                NormalizeVirtualPath(file.RelativePath),
                file.SizeInBytes,
                file.ContentHash)))
            .ToArray();

        var entries = fileProviders
            .GroupBy(provider => provider.NormalizedVirtualPath, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var participants = group
                    .GroupBy(provider => provider.ModId, StringComparer.OrdinalIgnoreCase)
                    .Select(participant => new
                    {
                        ModId = participant.Key,
                        LoadOrder = participant.Max(provider => provider.LoadOrder)
                    })
                    .OrderBy(participant => participant.LoadOrder)
                    .ThenBy(participant => participant.ModId, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                var winner = participants[^1];
                var winningFile = group
                    .Where(provider => string.Equals(provider.ModId, winner.ModId, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(provider => provider.LoadOrder)
                    .ThenBy(provider => provider.NormalizedVirtualPath, StringComparer.OrdinalIgnoreCase)
                    .First();

                return new ModMountPlanEntry(
                    group.Key,
                    winner.ModId,
                    participants.Select(participant => participant.ModId).ToArray(),
                    winningFile.SizeInBytes,
                    winningFile.ContentHash);
            })
            .OrderBy(entry => entry.NormalizedVirtualPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var shadowedProviderCount = fileProviders.Length - entries.Length;
        return new ModMountPlan(
            allMods.Length,
            enabledMods.Length,
            allMods.Length - enabledMods.Length,
            fileProviders.Length,
            entries.Length,
            shadowedProviderCount,
            conflicts,
            entries);
    }

    private static string NormalizeVirtualPath(string relativePath)
    {
        return relativePath
            .Replace('\\', '/')
            .Trim()
            .TrimStart('/')
            .ToUpperInvariant();
    }

    private sealed record FileProvider(
        string ModId,
        int LoadOrder,
        string NormalizedVirtualPath,
        long SizeInBytes,
        string? ContentHash);
}
