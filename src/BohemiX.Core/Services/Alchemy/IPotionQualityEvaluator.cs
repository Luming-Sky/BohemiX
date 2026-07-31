using BohemiX.Core.Models.Alchemy;

namespace BohemiX.Core.Services.Alchemy;

/// <summary>
/// Scores a brew against its recipe using the three-factor quality model from
/// the KCD2 specification. Implementations MUST be deterministic and pure —
/// given the same recipe, performed actions, and resolved herb freshness, they
/// must always return the same breakdown. This makes quality results replayable
/// from a saved action log.
///
/// The three factors are:
/// <list type="bullet">
/// <item><term>Order (weight 0.5)</term><description>fraction of recipe steps the player performed in the correct position with matching parameters.</description></item>
/// <item><term>Boil turns (weight 0.3)</term><description>how close the actual hourglass-turn count is to <c>recipe.ExpectedBoilTurns</c>.</description></item>
/// <item><term>Freshness (weight 0.2)</term><description>fraction of ingredients whose required freshness state was honoured.</description></item>
/// </list>
/// </summary>
public interface IPotionQualityEvaluator
{
    /// <summary>
    /// Evaluates the three factors and maps the weighted total onto a
    /// <see cref="PotionQuality"/> grade.
    /// </summary>
    /// <param name="recipe">The recipe the brew targeted.</param>
    /// <param name="performedActions">The actions the player actually performed, in order.</param>
    /// <returns>The scored breakdown, including the resolved grade.</returns>
    QualityBreakdown Evaluate(
        Recipe recipe,
        IReadOnlyList<AlchemyAction> performedActions);
}
