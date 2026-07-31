using BohemiX.Modules.Alchemy.Engine;
using BohemiX.Modules.Alchemy.Models;
using BohemiX.Modules.Alchemy.ViewModels;
using Xunit;

namespace BohemiX.Modules.Alchemy.Tests;

public sealed class AlchemyWorkshopExitTests
{
    [Fact]
    public void EmptyWorkshop_IsNotAnActiveBrew()
    {
        var engine = new AlchemyEngine();

        Assert.False(AlchemyWorkshopViewModel.IsActiveBrew(engine.Snapshot));
    }

    [Fact]
    public void AddedBase_IsAnActiveBrewUntilReset()
    {
        var engine = new AlchemyEngine();
        var result = engine.Execute(new AlchemyCommand(AlchemyCommandKind.PourBase, Base: BaseLiquid.Wine));

        Assert.True(result.Accepted);
        Assert.True(AlchemyWorkshopViewModel.IsActiveBrew(engine.Snapshot));

        engine.Reset();
        Assert.False(AlchemyWorkshopViewModel.IsActiveBrew(engine.Snapshot));
    }
}
