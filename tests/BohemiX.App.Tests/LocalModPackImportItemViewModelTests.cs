using BohemiX.App.ViewModels;
using BohemiX.Core.Models;

namespace BohemiX.App.Tests;

public sealed class LocalModPackImportItemViewModelTests
{
    [Fact]
    public void ExistingItemDefaultsToSkipAndReplaceSelectsIt()
    {
        var item = new ModPackImportItem(
            "demo",
            "Demo",
            "1.0",
            "Author",
            "Mods/Demo",
            "C:\\staging\\Demo",
            ModPackImportItemState.AlreadyInstalled,
            "Already installed.",
            "C:\\Mods\\demo",
            "demo");
        var row = new LocalModPackImportItemViewModel(item, key => key, () => { });

        Assert.False(row.IsSelected);
        Assert.Equal(ModPackImportAction.Skip, row.Action);

        row.IsReplace = true;

        Assert.True(row.IsSelected);
        Assert.Equal(ModPackImportAction.Replace, row.Action);
    }

    [Fact]
    public void InvalidItemCannotBeSelected()
    {
        var item = new ModPackImportItem(
            "invalid",
            "Invalid",
            "-",
            string.Empty,
            "README",
            "C:\\staging\\README",
            ModPackImportItemState.Invalid,
            "No mod manifest.");
        var row = new LocalModPackImportItemViewModel(item, key => key, () => { });

        Assert.False(row.CanSelect);
        row.IsSelected = true;

        Assert.Equal(ModPackImportAction.Skip, row.Action);
    }
}
