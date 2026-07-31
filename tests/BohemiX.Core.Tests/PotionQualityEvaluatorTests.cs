using BohemiX.Core.Models.Alchemy;
using BohemiX.Infrastructure.Services.Alchemy;

namespace BohemiX.Core.Tests;

public sealed class PotionQualityEvaluatorTests
{
    private readonly PotionQualityEvaluator evaluator = new();

    [Fact]
    public void Evaluate_PerfectProcedure_ReachesHenryLevel()
    {
        var recipe = SampleRecipe();
        var actions = PerfectActions();

        var result = evaluator.Evaluate(recipe, actions);

        Assert.Equal(PotionQuality.HenryLevel, result.Quality);
        Assert.Equal(1.0, result.Order.Score);
        Assert.Equal(1.0, result.BoilTurns.Score);
        Assert.Equal(1.0, result.Freshness.Score);
        Assert.True(result.TotalScore >= PotionQualityEvaluator.HenryLevelThreshold);
    }

    [Fact]
    public void Evaluate_TooFewBoilTurns_DegradesBoilTurnsScore()
    {
        var recipe = SampleRecipe();
        // Keep the action in place but use 0 turns where 1 is expected.
        var actions = PerfectActions().Select(a =>
            a.Kind == AlchemyActionKind.TurnHourglass ? AlchemyAction.BoilTurns(0) : a).ToList();

        var result = evaluator.Evaluate(recipe, actions);

        // 1 turn of divergence * 0.25 penalty = 0.75 boil-turn score.
        Assert.Equal(0.75, result.BoilTurns.Score, precision: 2);
        // Freshness is independent of the boil-turn deviation.
        Assert.Equal(1.0, result.Freshness.Score);
    }

    [Fact]
    public void Evaluate_WrongStirDirection_DropsOrderScoreAndTotal()
    {
        var recipe = SampleRecipe();
        var actions = PerfectActions().Select(a =>
            a.Kind == AlchemyActionKind.Stir
                ? AlchemyAction.Stir(StirDirection.CounterClockwise)
                : a).ToList();

        var result = evaluator.Evaluate(recipe, actions);

        // One of ten steps mismatched -> 0.9 order score.
        Assert.True(result.Order.Score < 1.0);
        Assert.Equal(0.9, result.Order.Score, precision: 2);
        // 0.9*0.5 + 1*0.3 + 1*0.2 = 0.95 sits right on the HenryLevel threshold,
        // so a single order slip on a 10-step recipe still lands at Strong/Henry.
        Assert.True(result.Quality >= PotionQuality.Strong);
    }

    [Fact]
    public void Evaluate_DriedHerbWhereFreshRequired_LowersFreshness()
    {
        var recipe = SampleRecipe();
        var actions = PerfectActions().Select(a =>
            a is { Kind: AlchemyActionKind.Pour, HerbId: "herb-a" }
                ? AlchemyAction.AddHerb("herb-a", HerbState.Dried)
                : a).ToList();

        var result = evaluator.Evaluate(recipe, actions);

        // One of two ingredients honoured -> 0.5.
        Assert.Equal(0.5, result.Freshness.Score, precision: 2);
    }

    [Fact]
    public void Evaluate_CatastrophicProcedure_YieldsWeak()
    {
        var recipe = SampleRecipe();
        // A single unrelated action: nothing matches.
        var actions = new List<AlchemyAction>
        {
            AlchemyAction.CrushHerb("unknown", HerbState.Dried)
        };

        var result = evaluator.Evaluate(recipe, actions);

        Assert.Equal(PotionQuality.Weak, result.Quality);
        Assert.Equal(0.0, result.Order.Score);
    }

    [Fact]
    public void Evaluate_WrongBarePourTransfer_DropsOrderScore()
    {
        var recipe = SampleRecipe();
        var actions = PerfectActions().Select(a =>
            a is { Kind: AlchemyActionKind.Pour, Base: null, HerbId: null }
                ? AlchemyAction.Pour(AlchemyTransfer.CauldronToAlembic)
                : a).ToList();

        var result = evaluator.Evaluate(recipe, actions);

        Assert.True(result.Order.Score < 1.0);
    }

    [Fact]
    public void Evaluate_Thresholds_MapScoreToExpectedGrade()
    {
        // HenryLevel boundary is 0.95. Construct a recipe where only freshness
        // contributes and push the score just over Strong (0.80) but below Henry.
        var recipe = new Recipe(
            "threshold-probe",
            "Threshold Probe",
            AlchemyBase.Water,
            [new RecipeIngredient("herb-a", 1, HerbState.Fresh, HerbPreparation.None)],
            // A single step that the action below will match.
            [AlchemyAction.PourBase(AlchemyBase.Water)],
            ExpectedBoilTurns: 0,
            RequiresDistillation: false);

        // Perfect order + perfect boil (0 turns expected, 0 performed) but wrong freshness.
        var actions = new List<AlchemyAction>
        {
            AlchemyAction.PourBase(AlchemyBase.Water)
        };

        var result = evaluator.Evaluate(recipe, actions);

        // order 1.0*0.5 + boil 1.0*0.3 + fresh 0.0*0.2 = 0.80 -> Strong boundary.
        Assert.Equal(0.80, result.TotalScore, precision: 2);
        Assert.Equal(PotionQuality.Strong, result.Quality);
    }

    private static Recipe SampleRecipe() => new(
        "sample",
        "Sample",
        AlchemyBase.Water,
        [
            new RecipeIngredient("herb-a", 1, HerbState.Fresh, HerbPreparation.None),
            new RecipeIngredient("herb-b", 1, HerbState.Fresh, HerbPreparation.Ground)
        ],
        PerfectActions(),
        ExpectedBoilTurns: 1,
        RequiresDistillation: false);

    private static IReadOnlyList<AlchemyAction> PerfectActions() =>
    [
        AlchemyAction.PourBase(AlchemyBase.Water),
        AlchemyAction.AddHerb("herb-a", HerbState.Fresh),
        AlchemyAction.LowerCauldron(),
        AlchemyAction.PullBellows(),
        AlchemyAction.BoilTurns(1),
        AlchemyAction.RaiseCauldron(),
        AlchemyAction.Grind("herb-b", HerbState.Fresh, 1),
        AlchemyAction.Pour(AlchemyTransfer.PlateToCauldron),
        AlchemyAction.Stir(StirDirection.Clockwise),
        AlchemyAction.Bottle()
    ];
}
