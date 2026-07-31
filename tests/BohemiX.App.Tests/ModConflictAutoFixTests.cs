using BohemiX.App.ViewModels;
using BohemiX.Core.Models;
using BohemiX.Infrastructure.Services;

namespace BohemiX.App.Tests;

public sealed class ModConflictAutoFixTests
{
    [Fact]
    public void RepairOrderMovesCoveredSelectedModAfterConflictParticipants()
    {
        var analyzer = new ModConflictAnalyzer();
        var orderedIds = new[] { "armor-rebalance", "inventory-ui", "perk-fix" };
        var initialMods = BuildMods(orderedIds);
        var initialConflict = Assert.Single(analyzer.AnalyzeConflicts(initialMods));

        Assert.Equal("inventory-ui", initialConflict.WinningModId);

        var repairedOrder = MainWindowViewModel.BuildSelectedModConflictRepairOrderForTesting(
            orderedIds,
            "armor-rebalance",
            [initialConflict]);

        Assert.NotNull(repairedOrder);
        Assert.Equal(["inventory-ui", "armor-rebalance", "perk-fix"], repairedOrder);

        var repairedConflict = Assert.Single(analyzer.AnalyzeConflicts(BuildMods(repairedOrder)));
        Assert.Equal("armor-rebalance", repairedConflict.WinningModId);
    }

    [Fact]
    public void RepairOrderReturnsNullWhenSelectedModAlreadyWinsConflicts()
    {
        var analyzer = new ModConflictAnalyzer();
        var orderedIds = new[] { "inventory-ui", "armor-rebalance", "perk-fix" };
        var conflict = Assert.Single(analyzer.AnalyzeConflicts(BuildMods(orderedIds)));

        Assert.Equal("armor-rebalance", conflict.WinningModId);

        var repairedOrder = MainWindowViewModel.BuildSelectedModConflictRepairOrderForTesting(
            orderedIds,
            "armor-rebalance",
            [conflict]);

        Assert.Null(repairedOrder);
    }

    private static IReadOnlyList<ModManifest> BuildMods(IReadOnlyList<string> orderedIds)
    {
        return orderedIds
            .Select((modId, index) => new ModManifest(
                modId,
                modId,
                "1.0",
                $"D:/Mods/{modId}",
                index,
                true,
                [new ModFileEntry(GetVirtualPath(modId), 128, "hash")]))
            .ToArray();
    }

    private static string GetVirtualPath(string modId) =>
        string.Equals(modId, "perk-fix", StringComparison.OrdinalIgnoreCase)
            ? "Data/Scripts/perk-fix.xml"
            : "Data/UI/shared.xml";
}
