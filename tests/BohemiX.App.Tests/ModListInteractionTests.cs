using BohemiX.App.Views;
using BohemiX.App.ViewModels;
using BohemiX.Core.Models;

namespace BohemiX.App.Tests;

public sealed class ModListInteractionTests
{
    [Fact]
    public void DetailSearchQueryPrefersTheInstalledNexusModId()
    {
        var manifest = new ModManifest(
            "custom-id",
            "Display Name",
            "1.0.0",
            Path.GetTempPath(),
            0,
            true,
            [],
            new ModPackageSourceMetadata(NexusModId: 12345));
        using var mod = new ModListItemViewModel(manifest);

        var query = MainWindowViewModel.GetModDetailSearchQuery(mod);

        Assert.Equal("12345", query);
    }

    [Fact]
    public void DetailSearchQueryFallsBackToTheInstalledModName()
    {
        var manifest = new ModManifest(
            "local-mod",
            "Local Mod",
            "1.0.0",
            Path.GetTempPath(),
            0,
            true,
            []);
        using var mod = new ModListItemViewModel(manifest);

        var query = MainWindowViewModel.GetModDetailSearchQuery(mod);

        Assert.Equal("Local Mod", query);
    }

    [Fact]
    public void ConflictOutcomeVisibilityTracksWhetherThereIsTextToShow()
    {
        var manifest = new ModManifest(
            "local-mod",
            "Local Mod",
            "1.0.0",
            Path.GetTempPath(),
            0,
            true,
            []);
        using var mod = new ModListItemViewModel(manifest);

        mod.ConflictOutcomeSummary = "Active for the shared file.";
        Assert.True(mod.HasConflictOutcomeSummary);

        mod.ConflictOutcomeSummary = string.Empty;
        Assert.False(mod.HasConflictOutcomeSummary);
    }

    [Fact]
    public void MissingRequirementStateTracksAppliedRequirementsAndCanBeCleared()
    {
        var manifest = new ModManifest(
            "nexus-100",
            "Dependent Mod",
            "1.0.0",
            Path.GetTempPath(),
            0,
            true,
            []);
        using var mod = new ModListItemViewModel(manifest);
        var requirement = new NexusModRequirementViewModel(
            new NexusModRequirement(200, "Required Mod", null, false, null, null),
            "Nexus Mods",
            "Manual install required");
        var visibilityNotifications = 0;
        mod.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ModListItemViewModel.HasMissingRequirements))
            {
                visibilityNotifications++;
            }
        };

        mod.ApplyMissingRequirements([requirement], "Missing 1 prerequisite mod");

        Assert.True(mod.HasMissingRequirements);
        Assert.Equal("Missing 1 prerequisite mod", mod.MissingRequirementsSummary);
        Assert.Same(requirement, Assert.Single(mod.MissingRequirements));

        mod.ApplyMissingRequirements([], "Unused summary");

        Assert.False(mod.HasMissingRequirements);
        Assert.Empty(mod.MissingRequirements);
        Assert.Equal(string.Empty, mod.MissingRequirementsSummary);
        Assert.Equal(2, visibilityNotifications);
    }

    [Theory]
    [InlineData(100, 500, 400, 10, 73.71428571428572)]
    [InlineData(100, 500, 400, 390, 126.28571428571428)]
    [InlineData(100, 500, 400, 200, 100)]
    [InlineData(0, 500, 400, 0, 0)]
    [InlineData(500, 500, 400, 400, 500)]
    public void DragAutoScrollOffsetMovesOnlyNearTheListEdges(
        double currentOffset,
        double maxOffset,
        double viewportHeight,
        double pointerY,
        double expectedOffset)
    {
        var result = MainWindow.CalculateModListDragAutoScrollOffset(
            currentOffset,
            maxOffset,
            viewportHeight,
            pointerY);

        Assert.Equal(expectedOffset, result);
    }

    [Fact]
    public void ScrollThumbPositionTracksTheListOffset()
    {
        var thumbTop = MainWindow.CalculateScrollThumbTopForTesting(
            verticalOffset: 250,
            maxOffset: 500,
            trackHeight: 400,
            thumbHeight: 80);

        Assert.Equal(160, thumbTop);
    }

    [Fact]
    public void DraggedScrollThumbUpdatesTheListOffset()
    {
        var offset = MainWindow.CalculateScrollOffsetForTrackPositionForTesting(
            maxOffset: 500,
            trackHeight: 400,
            thumbHeight: 80,
            thumbPointerOffsetY: 40,
            pointerY: 200);

        Assert.Equal(250, offset);
    }

    [Theory]
    [InlineData(0, 2, false, 1)]
    [InlineData(0, 2, true, 2)]
    [InlineData(2, 0, false, 0)]
    [InlineData(2, 0, true, 1)]
    [InlineData(1, 2, false, 1)]
    [InlineData(1, 0, true, 1)]
    public void DropTargetIndexAccountsForRemovingTheDraggedRow(
        int sourceIndex,
        int targetIndex,
        bool placeAfter,
        int expectedIndex)
    {
        var result = MainWindow.CalculateModDropTargetIndex(sourceIndex, targetIndex, placeAfter);

        Assert.Equal(expectedIndex, result);
    }
}
