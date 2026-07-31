using BohemiX.Core.Models.Alchemy;
using BohemiX.Core.Services.Alchemy;
using Serilog;

namespace BohemiX.Infrastructure.Services.Alchemy;

/// <summary>
/// Default <see cref="IAlchemyBenchService"/>. Replays the player's performed
/// actions against a fresh <see cref="AlchemyBench"/>, finalizes the brew,
/// scores it with <see cref="IPotionQualityEvaluator"/>, and assembles the
/// full <see cref="BrewResult"/> including a per-step state-transition log.
/// </summary>
public sealed class AlchemyBenchService : IAlchemyBenchService
{
    private readonly IPotionQualityEvaluator qualityEvaluator;
    private readonly ILogger logger;

    public AlchemyBenchService(IPotionQualityEvaluator qualityEvaluator, ILogger logger)
    {
        this.qualityEvaluator = qualityEvaluator;
        this.logger = logger.ForContext<AlchemyBenchService>();
    }

    /// <inheritdoc/>
    public BrewResult Brew(
        Recipe recipe,
        IReadOnlyList<AlchemyAction> performedActions,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var bench = new AlchemyBench();
        var log = new List<BrewLogEntry>(performedActions.Count);
        var errors = new List<ActionValidationError>();

        for (var i = 0; i < performedActions.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var action = performedActions[i];
            var result = bench.Apply(action);
            if (!result.Applied && result.Error is { } reason)
            {
                errors.Add(new ActionValidationError(i, action.Kind, reason));
            }

            log.Add(new BrewLogEntry(i, action, result.Applied, bench.Snapshot()));
        }

        // A brew is only valid if it ended in a bottled, non-empty cauldron.
        Potion? potion = null;
        QualityBreakdown quality;
        if (bench.CanFinalize)
        {
            var breakdown = qualityEvaluator.Evaluate(recipe, performedActions);
            potion = new Potion(
                recipe.Id,
                recipe.DisplayName,
                breakdown.Quality,
                DateTimeOffset.UtcNow);
            quality = breakdown;
            logger.Information(
                "Brew of {RecipeId} finalized as {Quality} (score {Score:F3}) with {ErrorCount} tolerated error(s)",
                recipe.Id,
                breakdown.Quality,
                breakdown.TotalScore,
                errors.Count);
        }
        else
        {
            // Structural failure: no quality to score. Produce a zeroed breakdown
            // so callers always have a non-null Quality field to inspect.
            quality = ZeroedBreakdown();
            logger.Warning(
                "Brew of {RecipeId} did not reach a valid final state (base={HasBase}, herbs={HerbCount}, bottled={Bottled})",
                recipe.Id,
                bench.State.CauldronBase is not null,
                bench.State.CauldronHerbs.Count,
                bench.State.Bottled);
        }

        return new BrewResult(potion, quality, log, errors);
    }

    private static QualityBreakdown ZeroedBreakdown()
    {
        var zero = new QualityFactorScore(0, QualityBreakdown.OrderWeight, 0);
        return new QualityBreakdown(
            PotionQuality.Weak,
            0,
            zero,
            new QualityFactorScore(0, QualityBreakdown.BoilTurnsWeight, 0),
            new QualityFactorScore(0, QualityBreakdown.FreshnessWeight, 0));
    }
}
