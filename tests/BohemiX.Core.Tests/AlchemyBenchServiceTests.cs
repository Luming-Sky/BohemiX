using BohemiX.Core.Models.Alchemy;
using BohemiX.Core.Services.Alchemy;
using BohemiX.Infrastructure.Services;
using BohemiX.Infrastructure.Services.Alchemy;

namespace BohemiX.Core.Tests;

public sealed class AlchemyBenchServiceTests
{
    // The bench service needs an evaluator; the real one is deterministic and pure,
    // so tests can observe both the physical replay (log/errors) and the score.
    private static AlchemyBenchService CreateService() =>
        new(new PotionQualityEvaluator(), SerilogLogger.NoOp);

    [Fact]
    public void PullBellows_WhileRaised_IsToleratedAndNotApplied()
    {
        var bench = new AlchemyBench();

        var result = bench.Apply(AlchemyAction.PullBellows());

        Assert.False(result.Applied);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void PullBellows_AfterLowering_CountsAndIntensifiesBoil()
    {
        var bench = new AlchemyBench();
        bench.Apply(AlchemyAction.PourBase(AlchemyBase.Water));
        bench.Apply(AlchemyAction.LowerCauldron());

        var result = bench.Apply(AlchemyAction.PullBellows());

        Assert.True(result.Applied);
        Assert.Equal(1, bench.State.BellowsPulls);
    }

    [Fact]
    public void TurnHourglass_AdvancesAfterLowering_WithoutBellows()
    {
        // Real KCD2: a plain boil needs no bellows; lowering the cauldron is enough.
        var bench = new AlchemyBench();
        bench.Apply(AlchemyAction.PourBase(AlchemyBase.Water));
        bench.Apply(AlchemyAction.LowerCauldron());

        var result = bench.Apply(AlchemyAction.BoilTurns(1, BoilMode.Plain));

        Assert.True(result.Applied);
        Assert.Equal(1, bench.State.BoilTurnsElapsed);
    }

    [Fact]
    public void TurnHourglass_WhileRaised_IsRejected()
    {
        var bench = new AlchemyBench();
        bench.Apply(AlchemyAction.PourBase(AlchemyBase.Water));

        var result = bench.Apply(AlchemyAction.BoilTurns(1));

        Assert.False(result.Applied);
        Assert.Equal(0, bench.State.BoilTurnsElapsed);
    }

    [Fact]
    public void TurnHourglass_RecordsBoilMode()
    {
        var bench = new AlchemyBench();
        bench.Apply(AlchemyAction.PourBase(AlchemyBase.Water));
        bench.Apply(AlchemyAction.LowerCauldron());
        bench.Apply(AlchemyAction.BoilTurns(1, BoilMode.Bellows));
        bench.Apply(AlchemyAction.BoilTurns(2, BoilMode.Plain));

        Assert.Equal(3, bench.State.BoilTurnsElapsed);
        Assert.Equal(2, bench.State.BoilTurnLog.Count);
        Assert.Equal(BoilMode.Bellows, bench.State.BoilTurnLog[0].Mode);
        Assert.Equal(BoilMode.Plain, bench.State.BoilTurnLog[1].Mode);
    }

    [Fact]
    public void AddHerb_WithCount_AddsMultipleRawHerbs()
    {
        var bench = new AlchemyBench();
        bench.Apply(AlchemyAction.PourBase(AlchemyBase.Wine));

        var result = bench.Apply(AlchemyAction.AddHerb("chamomile", HerbState.Fresh, 2));

        Assert.True(result.Applied);
        Assert.Equal(2, bench.State.CauldronHerbs.Count(h => h.HerbId == "chamomile"));
    }

    [Fact]
    public void Pour_PlateToCauldron_WithEmptyPlate_IsRejected()
    {
        var bench = new AlchemyBench();
        bench.Apply(AlchemyAction.PourBase(AlchemyBase.Wine));

        var result = bench.Apply(AlchemyAction.Pour(AlchemyTransfer.PlateToCauldron));

        Assert.False(result.Applied);
        Assert.False(bench.State.DistillationTransferred);
    }

    [Fact]
    public void Pour_CauldronToAlembic_WithUnclearedPlate_IsRejected()
    {
        var bench = new AlchemyBench();
        bench.Apply(AlchemyAction.PourBase(AlchemyBase.Spirits));
        bench.Apply(AlchemyAction.Grind("mint", HerbState.Fresh, 2));
        bench.Apply(AlchemyAction.AddHerb("valerian", HerbState.Fresh));

        var result = bench.Apply(AlchemyAction.Pour(AlchemyTransfer.CauldronToAlembic));

        Assert.False(result.Applied);
        Assert.False(bench.State.DistillationTransferred);
        Assert.Single(bench.State.Plate);
    }

    [Fact]
    public void ExtinguishDistillation_CompletesAlembicBeforeBottling()
    {
        var bench = new AlchemyBench();
        bench.Apply(AlchemyAction.PourBase(AlchemyBase.Spirits));
        bench.Apply(AlchemyAction.AddHerb("valerian", HerbState.Fresh));
        bench.Apply(AlchemyAction.Pour(AlchemyTransfer.CauldronToAlembic));

        var earlyBottle = bench.Apply(AlchemyAction.Bottle());
        var extinguish = bench.Apply(AlchemyAction.ExtinguishDistillation());
        var finalBottle = bench.Apply(AlchemyAction.Bottle());

        Assert.False(earlyBottle.Applied);
        Assert.True(extinguish.Applied);
        Assert.True(finalBottle.Applied);
        Assert.True(bench.State.Bottled);
    }

    [Fact]
    public void Brew_SaviourSchnapps_PerfectProcedure_YieldsHenryLevel()
    {
        var service = CreateService();
        var recipe = SaviourSchnapps();
        var actions = SaviourSchnappsPerfectActions();

        var result = service.Brew(recipe, actions);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Potion);
        Assert.Equal(PotionQuality.HenryLevel, result.Potion!.Quality);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Brew_SaviourSchnapps_BottlesDirectly_WithoutDistillation()
    {
        var service = CreateService();
        var recipe = SaviourSchnapps();
        var actions = SaviourSchnappsPerfectActions();

        var result = service.Brew(recipe, actions);

        // Saviour Schnapps never touches the alembic; it bottles straight from the cauldron.
        Assert.False(result.Log[^1].StateSnapshot.DistillationTransferred);
        Assert.True(result.Log[^1].StateSnapshot.Bottled);
    }

    [Fact]
    public void Brew_ChamomileBrew_PerfectProcedure_YieldsHenryLevel()
    {
        var service = CreateService();
        var recipe = ChamomileBrew();
        var actions = ChamomileBrewPerfectActions();

        var result = service.Brew(recipe, actions);

        Assert.True(result.Succeeded);
        Assert.Equal(PotionQuality.HenryLevel, result.Potion!.Quality);
    }

    [Fact]
    public void Brew_CockerelPotion_DistilledProcedure_YieldsHenryLevel()
    {
        var service = CreateService();
        var recipe = CockerelPotion();
        var actions = CockerelPotionPerfectActions();

        var result = service.Brew(recipe, actions);

        Assert.True(result.Succeeded);
        Assert.Equal(PotionQuality.HenryLevel, result.Potion!.Quality);
    }

    [Fact]
    public void Brew_MissingBottle_YieldsNullPotion()
    {
        var service = CreateService();
        var recipe = ChamomileBrew();
        var actions = ChamomileBrewPerfectActions().SkipLast(1).ToList();

        var result = service.Brew(recipe, actions);

        Assert.False(result.Succeeded);
        Assert.Null(result.Potion);
    }

    [Fact]
    public void Brew_TooManyBoilTurns_LowersQualityGrade()
    {
        var service = CreateService();
        var recipe = ChamomileBrew();
        // Replace the single 1-turn boil with three turns (divergence 2).
        var actions = ChamomileBrewPerfectActions().Select(a =>
            a.Kind == AlchemyActionKind.TurnHourglass ? AlchemyAction.BoilTurns(3, BoilMode.Plain) : a).ToList();

        var result = service.Brew(recipe, actions);

        Assert.True(result.Succeeded);
        Assert.True(result.Potion!.Quality < PotionQuality.HenryLevel);
        Assert.True(result.Quality.BoilTurns.Score < 1.0);
    }

    [Fact]
    public void Brew_WrongHerbFreshness_LowersFreshnessScore()
    {
        var service = CreateService();
        var recipe = ChamomileBrew();
        var actions = ChamomileBrewPerfectActions().Select(a =>
            a is { Kind: AlchemyActionKind.Pour, HerbId: "chamomile" }
                ? AlchemyAction.AddHerb("chamomile", HerbState.Dried, 2)
                : a).ToList();

        var result = service.Brew(recipe, actions);

        Assert.True(result.Succeeded);
        Assert.True(result.Quality.Freshness.Score < 1.0);
    }

    [Fact]
    public void Brew_ExtraWrongAttempt_LowersOrderScore()
    {
        var service = CreateService();
        var recipe = ChamomileBrew();
        var actions = new[] { AlchemyAction.Stir(StirDirection.Clockwise) }
            .Concat(ChamomileBrewPerfectActions())
            .ToList();

        var result = service.Brew(recipe, actions);

        Assert.True(result.Succeeded);
        Assert.NotEmpty(result.Errors);
        Assert.True(result.Quality.Order.Score < 1.0);
        Assert.True(result.Potion!.Quality < PotionQuality.HenryLevel);
    }

    // --- Sample recipes mirroring the real KCD2 mechanics ------------------

    private static Recipe SaviourSchnapps() => new(
        "saviour-schnapps",
        "Saviour Schnapps",
        AlchemyBase.Wine,
        [
            new RecipeIngredient("nettle", 1, HerbState.Fresh, HerbPreparation.None),
            new RecipeIngredient("belladonna", 2, HerbState.Fresh, HerbPreparation.Ground)
        ],
        SaviourSchnappsPerfectActions(),
        ExpectedBoilTurns: 3,
        RequiresDistillation: false);

    private static Recipe ChamomileBrew() => new(
        "chamomile-brew",
        "Chamomile Brew",
        AlchemyBase.Wine,
        [
            new RecipeIngredient("chamomile", 2, HerbState.Fresh, HerbPreparation.None),
            new RecipeIngredient("sage", 1, HerbState.Fresh, HerbPreparation.Ground)
        ],
        ChamomileBrewPerfectActions(),
        ExpectedBoilTurns: 1,
        RequiresDistillation: false);

    private static Recipe CockerelPotion() => new(
        "cockerel-potion",
        "Cockerel Potion",
        AlchemyBase.Spirits,
        [
            new RecipeIngredient("mint", 2, HerbState.Fresh, HerbPreparation.Ground),
            new RecipeIngredient("valerian", 1, HerbState.Fresh, HerbPreparation.None)
        ],
        CockerelPotionPerfectActions(),
        ExpectedBoilTurns: 3,
        RequiresDistillation: true);

    private static IReadOnlyList<AlchemyAction> SaviourSchnappsPerfectActions() =>
    [
        AlchemyAction.PourBase(AlchemyBase.Wine),
        AlchemyAction.AddHerb("nettle", HerbState.Fresh),
        AlchemyAction.LowerCauldron(),
        AlchemyAction.BoilTurns(2, BoilMode.Plain),
        AlchemyAction.RaiseCauldron(),
        AlchemyAction.Grind("belladonna", HerbState.Fresh, 2),
        AlchemyAction.Pour(AlchemyTransfer.PlateToCauldron),
        AlchemyAction.LowerCauldron(),
        AlchemyAction.BoilTurns(1, BoilMode.Plain),
        AlchemyAction.RaiseCauldron(),
        AlchemyAction.Bottle()
    ];

    private static IReadOnlyList<AlchemyAction> ChamomileBrewPerfectActions() =>
    [
        AlchemyAction.PourBase(AlchemyBase.Wine),
        AlchemyAction.AddHerb("chamomile", HerbState.Fresh, 2),
        AlchemyAction.LowerCauldron(),
        AlchemyAction.BoilTurns(1, BoilMode.Plain),
        AlchemyAction.RaiseCauldron(),
        AlchemyAction.Grind("sage", HerbState.Fresh, 1),
        AlchemyAction.Pour(AlchemyTransfer.PlateToCauldron),
        AlchemyAction.Bottle()
    ];

    private static IReadOnlyList<AlchemyAction> CockerelPotionPerfectActions() =>
    [
        AlchemyAction.PourBase(AlchemyBase.Spirits),
        AlchemyAction.Grind("mint", HerbState.Fresh, 2),
        AlchemyAction.Pour(AlchemyTransfer.PlateToCauldron),
        AlchemyAction.LowerCauldron(),
        AlchemyAction.BoilTurns(1, BoilMode.Plain),
        AlchemyAction.AddHerb("valerian", HerbState.Fresh),
        AlchemyAction.BoilTurns(2, BoilMode.Plain),
        AlchemyAction.RaiseCauldron(),
        AlchemyAction.Pour(AlchemyTransfer.CauldronToAlembic),
        AlchemyAction.ExtinguishDistillation(),
        AlchemyAction.Bottle()
    ];
}

/// <summary>
/// A no-op Serilog logger for tests that need an <see cref="Serilog.ILogger"/>
/// but don't assert on log output.
/// </summary>
internal static class SerilogLogger
{
    public static Serilog.ILogger NoOp { get; } = new Serilog.LoggerConfiguration()
        .CreateLogger();
}
