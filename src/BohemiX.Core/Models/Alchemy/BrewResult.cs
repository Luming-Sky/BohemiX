namespace BohemiX.Core.Models.Alchemy;

/// <summary>
/// The complete outcome of a brew attempt: the produced potion (if any), the
/// scored quality breakdown, a step-by-step log of bench state transitions,
/// and any non-fatal errors the bench tolerated (recorded rather than thrown
/// so a botched procedure still yields a — likely weak — potion).
/// </summary>
public sealed record BrewResult(
    Potion? Potion,
    QualityBreakdown Quality,
    IReadOnlyList<BrewLogEntry> Log,
    IReadOnlyList<ActionValidationError> Errors)
{
    /// <summary>True when a phial was successfully bottled, regardless of grade.</summary>
    public bool Succeeded => Potion is not null;
}

/// <summary>
/// The scored three-factor breakdown behind a <see cref="PotionQuality"/>
/// grade. Each factor is in [0,1]; the weighted total maps onto the grade via
/// the evaluator's thresholds.
/// </summary>
public sealed record QualityBreakdown(
    PotionQuality Quality,
    double TotalScore,
    QualityFactorScore Order,
    QualityFactorScore BoilTurns,
    QualityFactorScore Freshness)
{
    /// <summary>The weights are: order 0.5, boil-turns 0.3, freshness 0.2.</summary>
    public const double OrderWeight = 0.5;

    /// <summary>The weights are: order 0.5, boil-turns 0.3, freshness 0.2.</summary>
    public const double BoilTurnsWeight = 0.3;

    /// <summary>The weights are: order 0.5, boil-turns 0.3, freshness 0.2.</summary>
    public const double FreshnessWeight = 0.2;
}

/// <summary>
/// One factor's contribution: its raw score in [0,1] and the weighted amount
/// it added to the total.
/// </summary>
public sealed record QualityFactorScore(
    double Score,
    double Weight,
    double Contribution);

/// <summary>
/// A single state transition in the brew log: the action that was applied,
/// whether it was actually executed (vs. tolerated as invalid), and a snapshot
/// of the bench state immediately afterwards.
/// </summary>
public sealed record BrewLogEntry(
    int StepIndex,
    AlchemyAction Action,
    bool Applied,
    BenchState StateSnapshot);

/// <summary>
/// A non-fatal problem the bench tolerated instead of throwing — e.g. pulling
/// the bellows while the cauldron is raised, or bottling an empty cauldron.
/// The offending action is skipped (not applied) and the brew continues; the
/// accumulated score naturally degrades.
/// </summary>
public sealed record ActionValidationError(
    int StepIndex,
    AlchemyActionKind Kind,
    string Reason);
