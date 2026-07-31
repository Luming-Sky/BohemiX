using BohemiX.Modules.Alchemy.Engine;
using BohemiX.Modules.Alchemy.Data;
using BohemiX.Modules.Alchemy.Models;
using Xunit;

namespace BohemiX.Modules.Alchemy.Tests;

public sealed class AlchemyEngineTests
{
    [Fact]
    public void FreshEngine_DoesNotRequireContinuousTick()
    {
        var engine = new AlchemyEngine();

        Assert.False(engine.RequiresContinuousTick);
    }

    [Fact]
    public void HeatingAndCooling_ControlContinuousTickRequirement()
    {
        var engine = new AlchemyEngine();
        Apply(engine, new(AlchemyCommandKind.PourBase, Base: BaseLiquid.Wine));
        Apply(engine, new(AlchemyCommandKind.ToggleCauldron));
        Apply(engine, new(AlchemyCommandKind.PumpBellows));

        Assert.True(engine.RequiresContinuousTick);

        engine.Tick(TimeSpan.FromSeconds(30));
        for (var index = 0; index < 200 && engine.RequiresContinuousTick; index++)
        {
            engine.Tick(TimeSpan.FromMilliseconds(250));
        }

        Assert.False(engine.RequiresContinuousTick);
    }

    [Fact]
    public void RunningHourglass_RequiresContinuousTick()
    {
        var engine = PrepareHeatedCauldron();
        Apply(engine, new(AlchemyCommandKind.PumpBellows));
        Apply(engine, new(AlchemyCommandKind.StartHourglass, Turns: 1));

        Assert.True(engine.RequiresContinuousTick);
    }

    [Fact]
    public void NightOwl_ExactProcedure_ProducesThreePerfectBottles()
    {
        var engine = new AlchemyEngine();

        Apply(engine, new(AlchemyCommandKind.PourBase, Base: BaseLiquid.Wine));
        Apply(engine, new(AlchemyCommandKind.LoadMortar, IngredientId: "belladonna", Count: 2));
        Apply(engine, new(AlchemyCommandKind.TransferGroundMortar));
        Apply(engine, new(AlchemyCommandKind.ToggleCauldron));
        PumpToBoiling(engine);
        Apply(engine, new(AlchemyCommandKind.StartHourglass, Turns: 2));
        RunStableTurn(engine, ticks: 80, pumpAt: 24);

        Apply(engine, new(AlchemyCommandKind.AddRawIngredient, IngredientId: "marigold"));
        Apply(engine, new(AlchemyCommandKind.StartHourglass, Turns: 1));
        RunStableTurn(engine, ticks: 40, pumpAt: 4);
        Apply(engine, new(AlchemyCommandKind.Distill));
        Apply(engine, new(AlchemyCommandKind.Bottle));

        Assert.Equal(BrewOutcome.Perfect, engine.Snapshot.Outcome);
        Assert.Equal(0, engine.Snapshot.Mistakes);
        Assert.Equal(3, engine.Snapshot.Yield);
    }

    [Fact]
    public void MultipleRecipeDeviations_DoNotEndTheBrew()
    {
        var engine = new AlchemyEngine();

        Apply(engine, new(AlchemyCommandKind.PourBase, Base: BaseLiquid.Oil));
        Apply(engine, new(AlchemyCommandKind.AddRawIngredient, IngredientId: "mint"));
        Apply(engine, new(AlchemyCommandKind.Distill));
        Apply(engine, new(AlchemyCommandKind.Bottle));

        Assert.Equal(BrewOutcome.Diluted, engine.Snapshot.Outcome);
        Assert.Equal(4, engine.Snapshot.Mistakes);
        Assert.Equal(1, engine.Snapshot.Yield);
    }

    [Fact]
    public void HourglassLeavingSuggestedHeatBand_AddsMistakeAndAdvancesCookStep()
    {
        var engine = new AlchemyEngine();
        Apply(engine, new(AlchemyCommandKind.PourBase, Base: BaseLiquid.Wine));
        Apply(engine, new(AlchemyCommandKind.LoadMortar, IngredientId: "belladonna", Count: 2));
        Apply(engine, new(AlchemyCommandKind.TransferGroundMortar));
        Apply(engine, new(AlchemyCommandKind.ToggleCauldron));
        PumpToBoiling(engine);
        Apply(engine, new(AlchemyCommandKind.StartHourglass, Turns: 2));

        for (var i = 0; i < 80; i++)
        {
            engine.Tick(TimeSpan.FromMilliseconds(100));
        }

        Assert.Equal(1, engine.Snapshot.Mistakes);
        Assert.Equal(4, engine.Snapshot.CurrentStepIndex);
        Assert.False(engine.Snapshot.HourglassRunning);
    }

    [Fact]
    public void DifferentCookTurnCount_AddsMistakeAndStillAdvancesCookStep()
    {
        var engine = new AlchemyEngine();
        Apply(engine, new(AlchemyCommandKind.PourBase, Base: BaseLiquid.Wine));
        Apply(engine, new(AlchemyCommandKind.LoadMortar, IngredientId: "belladonna", Count: 2));
        Apply(engine, new(AlchemyCommandKind.TransferGroundMortar));
        Apply(engine, new(AlchemyCommandKind.ToggleCauldron));
        PumpToBoiling(engine);

        Apply(engine, new(AlchemyCommandKind.StartHourglass, Turns: 1));
        RunStableTurn(engine, ticks: 40, pumpAt: 4);

        Assert.Equal(1, engine.Snapshot.Mistakes);
        Assert.Equal(4, engine.Snapshot.CurrentStepIndex);
        Assert.False(engine.Snapshot.HourglassRunning);
    }

    [Fact]
    public void GrindingConsumesInventoryOnceAndMovesPowderDirectlyIntoCauldron()
    {
        var engine = new AlchemyEngine();
        Apply(engine, new(AlchemyCommandKind.PourBase, Base: BaseLiquid.Wine));

        Apply(engine, new(AlchemyCommandKind.LoadMortar, IngredientId: "belladonna", Count: 2));
        Assert.Equal(6, engine.Snapshot.Inventory["belladonna"]);
        Assert.NotNull(engine.Snapshot.Mortar);

        Apply(engine, new(AlchemyCommandKind.TransferGroundMortar));
        Assert.Null(engine.Snapshot.Mortar);
        Assert.Contains(engine.Snapshot.Cauldron, item => item is { IngredientId: "belladonna", Form: IngredientForm.Ground, Count: 2 });
    }

    [Fact]
    public void SameHerbCanBeAddedOneAtATimeUntilTheRecipeQuantityIsReached()
    {
        var engine = new AlchemyEngine();
        engine.SelectRecipe(AlchemyCatalog.Recipes.Single(recipe => recipe.Id == "marigold-decoction"));
        Apply(engine, new(AlchemyCommandKind.PourBase, Base: BaseLiquid.Water));

        Apply(engine, new(AlchemyCommandKind.AddRawIngredient, IngredientId: "marigold", Count: 1));

        Assert.Equal(1, engine.Snapshot.CurrentStepIndex);
        Assert.Equal(1, engine.Snapshot.CurrentStepProgress);
        Assert.Equal(0, engine.Snapshot.Mistakes);
        Assert.Equal(7, engine.Snapshot.Inventory["marigold"]);

        Apply(engine, new(AlchemyCommandKind.AddRawIngredient, IngredientId: "marigold", Count: 1));

        Assert.Equal(2, engine.Snapshot.CurrentStepIndex);
        Assert.Equal(0, engine.Snapshot.CurrentStepProgress);
        Assert.Equal(0, engine.Snapshot.Mistakes);
        Assert.Equal(6, engine.Snapshot.Inventory["marigold"]);
        Assert.Contains(engine.Snapshot.Cauldron, item => item is { IngredientId: "marigold", Form: IngredientForm.Raw, Count: 2 });
    }

    [Fact]
    public void MortarLoadsAccumulateOneAtATime()
    {
        var engine = new AlchemyEngine();
        Apply(engine, new(AlchemyCommandKind.PourBase, Base: BaseLiquid.Wine));

        Apply(engine, new(AlchemyCommandKind.LoadMortar, IngredientId: "belladonna", Count: 1));
        Apply(engine, new(AlchemyCommandKind.LoadMortar, IngredientId: "belladonna", Count: 1));
        Assert.Equal(2, engine.Snapshot.Mortar?.Count);

        Apply(engine, new(AlchemyCommandKind.TransferGroundMortar));
        Assert.Equal(3, engine.Snapshot.CurrentStepIndex);
        Assert.Equal(0, engine.Snapshot.Mistakes);
    }

    [Fact]
    public void BellowsHeat_IsProportionalToPullDistance()
    {
        var weak = PrepareHeatedCauldron();
        var strong = PrepareHeatedCauldron();

        Apply(weak, new(AlchemyCommandKind.PumpBellows, Strength: 0.2));
        Apply(strong, new(AlchemyCommandKind.PumpBellows, Strength: 1));

        Assert.True(strong.Snapshot.Temperature > weak.Snapshot.Temperature + 8);
    }

    [Fact]
    public void OffGuideAction_IsAcceptedAndOnlyLowersQuality()
    {
        var engine = new AlchemyEngine();
        Apply(engine, new(AlchemyCommandKind.PourBase, Base: BaseLiquid.Wine));

        var result = engine.Execute(new(AlchemyCommandKind.AddRawIngredient, IngredientId: "mint"));

        Assert.True(result.Accepted);
        Assert.Equal(1, engine.Snapshot.Mistakes);
        Assert.Equal(7, engine.Snapshot.Inventory["mint"]);
        Assert.Contains(engine.Snapshot.Cauldron, item => item is { IngredientId: "mint", Form: IngredientForm.Raw, Count: 1 });
        Assert.Equal(1, engine.Snapshot.CurrentStepIndex);
    }

    private static void PumpToBoiling(AlchemyEngine engine)
    {
        while (engine.Snapshot.HeatBand != HeatBand.Boiling)
        {
            Apply(engine, new(AlchemyCommandKind.PumpBellows));
        }
    }

    private static AlchemyEngine PrepareHeatedCauldron()
    {
        var engine = new AlchemyEngine();
        Apply(engine, new(AlchemyCommandKind.PourBase, Base: BaseLiquid.Wine));
        Apply(engine, new(AlchemyCommandKind.LoadMortar, IngredientId: "belladonna", Count: 2));
        Apply(engine, new(AlchemyCommandKind.TransferGroundMortar));
        Apply(engine, new(AlchemyCommandKind.ToggleCauldron));
        return engine;
    }

    private static void RunStableTurn(AlchemyEngine engine, int ticks, int pumpAt)
    {
        for (var i = 0; i < ticks; i++)
        {
            if (i == pumpAt)
            {
                Apply(engine, new(AlchemyCommandKind.PumpBellows));
            }

            engine.Tick(TimeSpan.FromMilliseconds(100));
        }
    }

    private static void Apply(AlchemyEngine engine, AlchemyCommand command)
    {
        var result = engine.Execute(command);
        Assert.True(result.Accepted, result.Text);
    }
}
