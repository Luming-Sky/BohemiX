using BohemiX.Modules.Alchemy.Engine;
using BohemiX.Modules.Alchemy.Models;
using Xunit;

namespace BohemiX.Modules.Alchemy.Tests;

public sealed class GrindingInteractionTests
{
    [Fact]
    public void LoadedHerb_RemainsInMortarUntilExplicitGrindingCommit()
    {
        var engine = new AlchemyEngine();
        var load = engine.Execute(new(
            AlchemyCommandKind.LoadMortar,
            IngredientId: "belladonna",
            Count: 2));

        Assert.True(load.Accepted, load.Text);
        Assert.NotNull(engine.Snapshot.Mortar);
        Assert.DoesNotContain(engine.Snapshot.Cauldron, item => item.Form == IngredientForm.Ground);

        var missingBase = engine.Execute(new(AlchemyCommandKind.TransferGroundMortar));

        Assert.False(missingBase.Accepted);
        Assert.NotNull(engine.Snapshot.Mortar);

        var baseResult = engine.Execute(new(AlchemyCommandKind.PourBase, Base: BaseLiquid.Wine));
        Assert.True(baseResult.Accepted, baseResult.Text);
        var commit = engine.Execute(new(AlchemyCommandKind.TransferGroundMortar));

        Assert.True(commit.Accepted, commit.Text);
        Assert.Null(engine.Snapshot.Mortar);
        Assert.Contains(engine.Snapshot.Cauldron, item => item is
        {
            IngredientId: "belladonna",
            Form: IngredientForm.Ground,
            Count: 2
        });
    }
}
