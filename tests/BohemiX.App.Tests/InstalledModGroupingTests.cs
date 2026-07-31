using BohemiX.App.ViewModels;
using BohemiX.Core.Models;

namespace BohemiX.App.Tests;

public sealed class InstalledModGroupingTests
{
    [Fact]
    public void ModsFromTheSameModPackUseTheSameGroupKeyAcrossInstallSessions()
    {
        using var first = CreateRow("first", "pack", "Test Pack", Guid.NewGuid());
        using var second = CreateRow("second", "pack", "Test Pack", Guid.NewGuid());
        using var standalone = CreateRow("standalone");

        Assert.Equal(
            InstalledModGroupViewModel.GetGroupKey(first),
            InstalledModGroupViewModel.GetGroupKey(second));
        Assert.NotEqual(
            InstalledModGroupViewModel.GetGroupKey(first),
            InstalledModGroupViewModel.GetGroupKey(standalone));
        Assert.Equal(
            InstalledModGroupViewModel.StandaloneGroupKey,
            InstalledModGroupViewModel.GetGroupKey(standalone));
    }

    [Fact]
    public void ModPackGroupUsesPersistedPackName()
    {
        var sessionId = Guid.NewGuid();
        using var first = CreateRow("first", "pack", "Test Pack", sessionId);
        using var second = CreateRow("second", "pack", "Test Pack", sessionId);

        var group = new InstalledModGroupViewModel("modpack:test", [first, second], Translate);

        Assert.True(group.IsModPack);
        Assert.Equal("Test Pack", group.TitleText);
        Assert.Equal("Installed from mod pack", group.KindText);
        Assert.Equal("2 mods", group.CountText);
    }

    [Fact]
    public void CustomNameOverridesDisplayTitleAndPreservesDefaultTitle()
    {
        using var row = CreateRow("first", "pack", "Test Pack", Guid.NewGuid());

        var group = new InstalledModGroupViewModel(
            "modpack:test",
            [row],
            Translate,
            "  Gameplay essentials  ");

        Assert.True(group.HasCustomName);
        Assert.Equal("Gameplay essentials", group.TitleText);
        Assert.Equal("Test Pack", group.DefaultTitleText);
    }

    [Fact]
    public void BlankCustomNameFallsBackToGeneratedTitle()
    {
        using var row = CreateRow("standalone");

        var group = new InstalledModGroupViewModel(
            InstalledModGroupViewModel.StandaloneGroupKey,
            [row],
            Translate,
            "   ");

        Assert.False(group.HasCustomName);
        Assert.Equal("Individual mods", group.TitleText);
        Assert.Equal(group.DefaultTitleText, group.TitleText);
    }

    [Fact]
    public void CustomGroupSupportsEmptyGroupsAndCustomKind()
    {
        var group = new InstalledModGroupViewModel(
            "custom:essentials",
            [],
            Translate,
            "Essentials");

        Assert.True(group.IsCustom);
        Assert.True(group.CanDelete);
        Assert.False(group.IsModPack);
        Assert.Equal("Essentials", group.TitleText);
        Assert.Equal("Custom group", group.KindText);
        Assert.Equal("0 mods", group.CountText);
    }

    [Fact]
    public void GroupEnabledStateUpdatesEveryRegularMod()
    {
        using var first = CreateRow("first");
        using var second = CreateRow("second");
        first.IsEnabled = true;
        second.IsEnabled = false;
        var group = new InstalledModGroupViewModel("custom:essentials", [first, second], Translate);

        group.SetEnabledState(isEnabled: false);

        Assert.False(first.IsEnabled);
        Assert.False(second.IsEnabled);

        group.SetEnabledState(isEnabled: true);

        Assert.True(first.IsEnabled);
        Assert.True(second.IsEnabled);
    }

    [Fact]
    public void DisablingGroupKeepsBuiltInModEnabled()
    {
        using var builtIn = CreateRow("bohemix-tracker");
        using var regular = CreateRow("regular");
        var group = new InstalledModGroupViewModel("custom:essentials", [builtIn, regular], Translate);

        group.SetEnabledState(isEnabled: false);

        Assert.True(builtIn.IsBuiltIn);
        Assert.True(builtIn.IsEnabled);
        Assert.False(regular.IsEnabled);
    }

    [Fact]
    public void EmptyGroupDoesNotOfferBulkStateActions()
    {
        var group = new InstalledModGroupViewModel("custom:empty", [], Translate);

        Assert.True(group.IsEmpty);
        Assert.False(group.HasItems);
        group.SetEnabledState(isEnabled: false);
    }

    [Fact]
    public void EmptyAutomaticGroupPreservesItsSourceMetadata()
    {
        using var row = CreateRow("first", "pack", "Test Pack", Guid.NewGuid());

        var group = new InstalledModGroupViewModel(
            InstalledModGroupViewModel.GetGroupKey(row),
            [],
            Translate,
            sourceRow: row);

        Assert.True(group.IsEmpty);
        Assert.True(group.IsModPack);
        Assert.Equal("Test Pack", group.TitleText);
        Assert.Equal("0 mods", group.CountText);
    }

    [Fact]
    public void AutomaticGroupAcceptsOnlyCustomAssignedModsFromItsOwnSource()
    {
        using var matching = CreateRow("matching", "pack", "Test Pack", Guid.NewGuid());
        using var other = CreateRow("other", "other-pack", "Other Pack", Guid.NewGuid());
        matching.IsAssignedToCustomGroup = true;
        other.IsAssignedToCustomGroup = true;
        var group = new InstalledModGroupViewModel(
            InstalledModGroupViewModel.GetGroupKey(matching),
            [],
            Translate,
            sourceRow: matching);

        Assert.True(group.CanAcceptDrop(matching));
        Assert.False(group.CanAcceptDrop(other));

        matching.IsAssignedToCustomGroup = false;
        Assert.False(group.CanAcceptDrop(matching));
    }

    [Fact]
    public void ValidCustomAssignmentOverridesAutomaticGrouping()
    {
        using var row = CreateRow("first", "pack", "Test Pack", Guid.NewGuid());
        var assignments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [row.Id] = "custom:essentials"
        };
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["custom:essentials"] = "Essentials"
        };

        Assert.Equal(
            "custom:essentials",
            InstalledModGroupViewModel.GetEffectiveGroupKey(row, assignments, names));
    }

    [Fact]
    public void MissingCustomAssignmentFallsBackToAutomaticGrouping()
    {
        using var row = CreateRow("first", "pack", "Test Pack", Guid.NewGuid());
        var assignments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [row.Id] = "custom:missing"
        };

        Assert.Equal(
            InstalledModGroupViewModel.GetGroupKey(row),
            InstalledModGroupViewModel.GetEffectiveGroupKey(
                row,
                assignments,
                new Dictionary<string, string>()));
    }

    private static ModListItemViewModel CreateRow(
        string id,
        string? modPackId = null,
        string? modPackName = null,
        Guid? sessionId = null)
    {
        var manifest = new ModManifest(
            id,
            id,
            "1.0.0",
            Path.Combine(Path.GetTempPath(), id),
            0,
            true,
            [],
            modPackId is null
                ? null
                : new ModPackageSourceMetadata(
                    ModPackId: modPackId,
                    ModPackSessionId: sessionId,
                    ModPackName: modPackName));
        return new ModListItemViewModel(manifest);
    }

    private static string Translate(string key) => key switch
    {
        "ModPackSource" => "Mod packs",
        "StandaloneInstalledMods" => "Individual mods",
        "ModPackInstalledSection" => "Installed from mod pack",
        "StandaloneInstalledSection" => "Installed individually",
        "CustomInstalledMods" => "Custom group",
        "CustomInstalledSection" => "Custom group",
        "InstalledModGroupCount" => "{0} mods",
        _ => key
    };
}
