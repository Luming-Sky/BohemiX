using BohemiX.Core.Models.Alchemy;

namespace BohemiX.Core.Services.Alchemy;

/// <summary>
/// Runs a full brew: replays the player's performed actions through the
/// physical bench state machine, finalizes (bottling), and scores the result.
///
/// The bench is tolerant by design — a physically invalid action (e.g. pulling
/// the bellows while the cauldron is raised) is recorded as an error and
/// skipped rather than thrown, so a botched procedure still produces a
/// (typically weak) potion. Only structural failures (no base, never bottled)
/// yield a <c>null</c> potion.
/// </summary>
public interface IAlchemyBenchService
{
    /// <summary>
    /// Brews one potion from <paramref name="recipe"/> by replaying
    /// <paramref name="performedActions"/> against a fresh bench state.
    /// </summary>
    /// <param name="recipe">The recipe being attempted.</param>
    /// <param name="performedActions">
    /// The ordered actions the player performed. These are compared against
    /// <paramref name="recipe"/>'s canonical steps by the quality evaluator.
    /// </param>
    /// <param name="cancellationToken">Propagates cancellation.</param>
    /// <returns>
    /// A brew result containing the produced potion (or <c>null</c> on
    /// structural failure), the scored quality breakdown, the action log, and
    /// any tolerated errors.
    /// </returns>
    BrewResult Brew(
        Recipe recipe,
        IReadOnlyList<AlchemyAction> performedActions,
        CancellationToken cancellationToken = default);
}
