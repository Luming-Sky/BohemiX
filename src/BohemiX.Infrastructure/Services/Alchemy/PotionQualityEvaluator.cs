using BohemiX.Core.Models.Alchemy;
using BohemiX.Core.Services.Alchemy;

namespace BohemiX.Infrastructure.Services.Alchemy;

/// <summary>
/// Deterministic, pure implementation of the three-factor quality model.
/// Given the same recipe and performed actions, always returns the same
/// breakdown — making quality results fully replayable from a saved log.
///
/// <para>The three factors and their weights:</para>
/// <list type="table">
/// <item><term>Order (0.5)</term><description>Fraction of recipe steps whose Kind and key parameters match the player's action at the same position.</description></item>
/// <item><term>Boil turns (0.3)</term><description>Decays linearly from 1.0 as the actual hourglass-turn count diverges from <c>recipe.ExpectedBoilTurns</c>, floored at 0.</description></item>
/// <item><term>Freshness (0.2)</term><description>Fraction of recipe ingredients whose required Fresh/Dried state was honoured by at least one matching performed add.</description></item>
/// </list>
/// <para>Grade thresholds (see <see cref="HenryLevelThreshold"/> et al.): this
/// iteration keeps them as named constants; a future iteration may lift them
/// into a configurable policy.</para>
/// </summary>
public sealed class PotionQualityEvaluator : IPotionQualityEvaluator
{
    /// <summary>Score needed (inclusive) for the top grade.</summary>
    public const double HenryLevelThreshold = 0.95;

    /// <summary>Score needed (inclusive) for Strong.</summary>
    public const double StrongThreshold = 0.80;

    /// <summary>Score needed (inclusive) for Regular.</summary>
    public const double RegularThreshold = 0.60;

    /// <summary>Penalty per boil-turn of divergence (per turn, before flooring).</summary>
    public const double BoilTurnPenalty = 0.25;

    /// <inheritdoc/>
    public QualityBreakdown Evaluate(
        Recipe recipe,
        IReadOnlyList<AlchemyAction> performedActions)
    {
        var orderScore = ScoreOrder(recipe.Steps, performedActions);
        var boilTurnsScore = ScoreBoilTurns(recipe.ExpectedBoilTurns, performedActions);
        var freshnessScore = ScoreFreshness(recipe.Ingredients, performedActions);

        var orderFactor = new QualityFactorScore(orderScore, QualityBreakdown.OrderWeight, orderScore * QualityBreakdown.OrderWeight);
        var boilFactor = new QualityFactorScore(boilTurnsScore, QualityBreakdown.BoilTurnsWeight, boilTurnsScore * QualityBreakdown.BoilTurnsWeight);
        var freshFactor = new QualityFactorScore(freshnessScore, QualityBreakdown.FreshnessWeight, freshnessScore * QualityBreakdown.FreshnessWeight);

        var total = orderFactor.Contribution + boilFactor.Contribution + freshFactor.Contribution;
        var grade = Classify(total);

        return new QualityBreakdown(grade, total, orderFactor, boilFactor, freshFactor);
    }

    /// <summary>
    /// Order factor: walks the recipe's canonical steps in lockstep with the
    /// performed actions and counts a step as matched when both Kind and the
    /// verb-relevant parameters agree. Mismatched or missing steps score 0.
    /// </summary>
    private static double ScoreOrder(IReadOnlyList<AlchemyAction> recipeSteps, IReadOnlyList<AlchemyAction> performed)
    {
        if (recipeSteps.Count == 0)
        {
            return 1.0;
        }

        var matched = 0;
        var limit = Math.Min(recipeSteps.Count, performed.Count);
        for (var i = 0; i < limit; i++)
        {
            if (ActionsEquivalent(recipeSteps[i], performed[i]))
            {
                matched++;
            }
        }

        return (double)matched / recipeSteps.Count;
    }

    /// <summary>
    /// Two actions are order-equivalent when their Kind matches and the
    /// parameters that Kind cares about match. Only the fields relevant to a
    /// given verb are compared (e.g. StirCount/Direction for Stir, Turns for
    /// TurnHourglass), so unrelated nulls don't cause false mismatches.
    /// </summary>
    private static bool ActionsEquivalent(AlchemyAction expected, AlchemyAction actual)
    {
        if (expected.Kind != actual.Kind)
        {
            return false;
        }

        return expected.Kind switch
        {
            AlchemyActionKind.Pour => (expected.Base ?? 0) == (actual.Base ?? 0)
                && StringEquals(expected.HerbId, actual.HerbId)
                && (expected.HerbState ?? HerbState.Fresh) == (actual.HerbState ?? HerbState.Fresh)
                && (expected.Count ?? 1) == (actual.Count ?? 1)
                && (!IsBarePour(expected, actual)
                    || (expected.Transfer ?? AlchemyTransfer.Auto) == (actual.Transfer ?? AlchemyTransfer.Auto)),
            AlchemyActionKind.Grind => StringEquals(expected.HerbId, actual.HerbId)
                && (expected.HerbState ?? HerbState.Fresh) == (actual.HerbState ?? HerbState.Fresh)
                && (expected.Count ?? 1) == (actual.Count ?? 1),
            AlchemyActionKind.Crush => StringEquals(expected.HerbId, actual.HerbId)
                && (expected.HerbState ?? HerbState.Fresh) == (actual.HerbState ?? HerbState.Fresh)
                && (expected.Count ?? 1) == (actual.Count ?? 1),
            AlchemyActionKind.Stir => (expected.Direction ?? StirDirection.Clockwise) == (actual.Direction ?? StirDirection.Clockwise)
                && (expected.StirCount ?? 1) == (actual.StirCount ?? 1),
            AlchemyActionKind.TurnHourglass => (expected.Turns ?? 1) == (actual.Turns ?? 1)
                && (expected.BoilMode ?? BoilMode.Plain) == (actual.BoilMode ?? BoilMode.Plain),
            AlchemyActionKind.LowerCauldron or AlchemyActionKind.RaiseCauldron
                or AlchemyActionKind.PullBellows or AlchemyActionKind.ExtinguishDistillation
                or AlchemyActionKind.Bottle => true,
            _ => true,
        };
    }

    /// <summary>
    /// Boil-turns factor: sums the TurnHourglass turns actually performed and
    /// compares to the recipe's expectation. Linear decay of 0.25 per turn of
    /// divergence, floored at 0.
    /// </summary>
    private static double ScoreBoilTurns(int expected, IReadOnlyList<AlchemyAction> performed)
    {
        var actual = performed
            .Where(a => a.Kind == AlchemyActionKind.TurnHourglass)
            .Sum(a => a.Turns ?? 1);

        if (expected == 0)
        {
            return actual == 0 ? 1.0 : 0.0;
        }

        var divergence = Math.Abs(actual - expected);
        return Math.Max(0.0, 1.0 - divergence * BoilTurnPenalty);
    }

    /// <summary>
    /// Freshness factor: for each recipe ingredient, checks whether at least
    /// one performed action involving that herb (Pour-add, Grind, or Crush)
    /// used the required state. Scored as the fraction of ingredients satisfied.
    /// </summary>
    private static double ScoreFreshness(IReadOnlyList<RecipeIngredient> ingredients, IReadOnlyList<AlchemyAction> performed)
    {
        if (ingredients.Count == 0)
        {
            return 1.0;
        }

        var satisfied = 0;
        foreach (var ingredient in ingredients)
        {
            var honored = performed.Any(a =>
                IsHerbHandlingAction(a.Kind)
                && StringEquals(a.HerbId, ingredient.HerbId)
                && (a.HerbState ?? HerbState.Fresh) == ingredient.RequiredState);
            if (honored)
            {
                satisfied++;
            }
        }

        return (double)satisfied / ingredients.Count;
    }

    /// <summary>
    /// Whether an action kind carries an <see cref="AlchemyAction.HerbId"/>
    /// and <see cref="AlchemyAction.HerbState"/> worth scoring for freshness.
    /// Pour-with-base is excluded (it has no herb); the rest are herb touches.
    /// </summary>
    private static bool IsHerbHandlingAction(AlchemyActionKind kind) =>
        kind == AlchemyActionKind.Pour
        || kind == AlchemyActionKind.Grind
        || kind == AlchemyActionKind.Crush;

    private static bool IsBarePour(AlchemyAction expected, AlchemyAction actual) =>
        expected.Base is null
        && actual.Base is null
        && expected.HerbId is null
        && actual.HerbId is null;

    /// <summary>Maps a weighted total onto a quality grade via the thresholds.</summary>
    private static PotionQuality Classify(double total) => total switch
    {
        >= HenryLevelThreshold => PotionQuality.HenryLevel,
        >= StrongThreshold => PotionQuality.Strong,
        >= RegularThreshold => PotionQuality.Regular,
        _ => PotionQuality.Weak,
    };

    private static bool StringEquals(string? a, string? b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
