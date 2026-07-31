namespace BohemiX.Core.Models.Alchemy;

/// <summary>
/// Mutable runtime snapshot of the alchemy bench. The <c>AlchemyBench</c>
/// mutates one of these as each action is applied; a deep copy is taken for
/// every <see cref="BrewLogEntry"/> so the brew history is replayable.
///
/// The state models the nine physical entities only — there is no field for
/// "hands", "spoon", or "filter cloth". Heat is modelled through
/// <see cref="CauldronPosition"/> (physical displacement) and
/// <see cref="LiquidPhase"/>.
/// </summary>
public sealed class BenchState
{
    /// <summary>Where the cauldron sits relative to the fire.</summary>
    public CauldronPosition CauldronPosition { get; set; } = CauldronPosition.Raised;

    /// <summary>Thermal phase of the cauldron's current liquid contents.</summary>
    public LiquidPhase LiquidPhase { get; set; } = LiquidPhase.Empty;

    /// <summary>The base solvent currently in the cauldron, if any has been poured.</summary>
    public AlchemyBase? CauldronBase { get; set; }

    /// <summary>
    /// Herbs that have been poured into the cauldron, in add order. Each entry
    /// records the herb id, its freshness when added, and how it was prepared.
    /// </summary>
    public List<CauldronHerbEntry> CauldronHerbs { get; } = [];

    /// <summary>
    /// The plate/dish staging area. Holds ground powder or prepared herbs
    /// between processing and cauldron addition. The Grind and Crush verbs
    /// produce entries here; a subsequent Pour transfers them to the cauldron.
    /// </summary>
    public List<PlateEntry> Plate { get; } = [];

    /// <summary>How many hourglass turns have elapsed since the cauldron reached boiling.</summary>
    public int BoilTurnsElapsed { get; set; }

    /// <summary>
    /// Total number of bellows pulls performed. In KCD2 each bellows-boil turn
    /// takes roughly 3 pulls to sustain; this counter feeds the visual and the
    /// quality evaluator without forcing an exact 3-per-turn rule.
    /// </summary>
    public int BellowsPulls { get; set; }

    /// <summary>
    /// Per-turn boil records: each hourglass turn logs its mode (Bellows/Plain)
    /// so the quality evaluator can compare modes against the recipe's intent.
    /// </summary>
    public List<BoilTurnRecord> BoilTurnLog { get; } = [];

    /// <summary>
    /// Recorded stir events: direction + count, in the order performed. Used
    /// by the order-quality factor.
    /// </summary>
    public List<StirRecord> Stirs { get; set; } = [];

    /// <summary>True once the cauldron's contents have been poured into the alembic.</summary>
    public bool DistillationTransferred { get; set; }

    /// <summary>
    /// True once the alembic candle has been extinguished at the correct mark.
    /// Distillation recipes require this before bottling.
    /// </summary>
    public bool AlembicHeated { get; set; }

    /// <summary>True once the brew has been bottled into a phial.</summary>
    public bool Bottled { get; set; }

    /// <summary>Produces an independent deep copy so log snapshots are immutable.</summary>
    public BenchState Clone()
    {
        var copy = new BenchState
        {
            CauldronPosition = CauldronPosition,
            LiquidPhase = LiquidPhase,
            CauldronBase = CauldronBase,
            BoilTurnsElapsed = BoilTurnsElapsed,
            BellowsPulls = BellowsPulls,
            DistillationTransferred = DistillationTransferred,
            AlembicHeated = AlembicHeated,
            Bottled = Bottled
        };
        copy.CauldronHerbs.AddRange(CauldronHerbs);
        copy.Plate.AddRange(Plate);
        copy.Stirs.AddRange(Stirs);
        copy.BoilTurnLog.AddRange(BoilTurnLog);
        return copy;
    }
}

/// <summary>An herb as it sits in the cauldron, post any preparation.</summary>
public sealed record CauldronHerbEntry(
    string HerbId,
    HerbState State,
    HerbPreparation Preparation);

/// <summary>Prepared material staged on the plate, awaiting transfer to the cauldron.</summary>
public sealed record PlateEntry(
    string HerbId,
    HerbState State,
    HerbPreparation Preparation,
    int Count);

/// <summary>A performed stir event, captured for order-factor evaluation.</summary>
public sealed record StirRecord(
    StirDirection Direction,
    int Count);

/// <summary>One completed hourglass turn, with the mode it was boiled in.</summary>
public sealed record BoilTurnRecord(int Turns, BoilMode Mode);
