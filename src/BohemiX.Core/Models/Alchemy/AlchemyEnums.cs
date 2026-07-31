namespace BohemiX.Core.Models.Alchemy;

/// <summary>
/// The four base solvents dispensed from the bench's Base Zone (left side of
/// the KCD2 alchemy UI). Every brew begins by pouring exactly one of these
/// into the cauldron.
/// </summary>
public enum AlchemyBase
{
    Water,
    Wine,
    Oil,
    Spirits
}

/// <summary>
/// Physical position of the cauldron relative to the fire. This is KCD2's
/// primary temperature-control mechanism: the cauldron is a movable vessel,
/// not a fixed pot. <see cref="Lowered"/> puts it on the heat source; the
/// <see cref="AlchemyActionKind.PullBellows"/> action only accelerates heating
/// while in this state.
/// </summary>
public enum CauldronPosition
{
    /// <summary>
    /// Cauldron lifted away from the fire (default). Heating is paused; the
    /// bellows have no effect. Players use this to interrupt boiling between
    /// hourglass turns.
    /// </summary>
    Raised,

    /// <summary>
    /// Cauldron lowered onto the fire. The contents heat up and, with the
    /// bellows, can be driven to <see cref="LiquidPhase.Boiling"/>.
    /// </summary>
    Lowered
}

/// <summary>
/// Thermal phase of whatever liquid currently sits in the cauldron. Lowering
/// the cauldron starts a plain boil; bellows intensify it when the recipe
/// explicitly calls for a bellows-driven boil.
/// </summary>
public enum LiquidPhase
{
    Empty,
    Cold,
    Heated,
    Boiling
}

/// <summary>
/// Rotation direction for the Stir verb. Each recipe specifies the direction
/// (and repetition count) its mixing step requires; matching it is one of the
/// order factors in quality evaluation.
/// </summary>
public enum StirDirection
{
    Clockwise,
    CounterClockwise
}

/// <summary>
/// Freshness of an herb pulled from the Herb Shelf. Affects final potion
/// quality: recipes call for a specific state and using the wrong one lowers
/// the freshness score.
/// </summary>
public enum HerbState
{
    Fresh,
    Dried
}

/// <summary>
/// Final potion grade, from worst to best. Determined by the three-factor
/// quality model (order / boil turns / herb freshness).
/// </summary>
public enum PotionQuality
{
    Weak,
    Regular,
    Strong,
    HenryLevel
}

/// <summary>
/// The preparation state an herb must reach before it is added to the cauldron.
/// Determines whether the player must run the Grind or Crush verb first.
/// </summary>
public enum HerbPreparation
{
    /// <summary>No pre-processing; the herb is poured straight into the cauldron.</summary>
    None,

    /// <summary>Ground in the mortar &amp; pestle into powder, then transferred via the plate.</summary>
    Ground,

    /// <summary>Squeezed/crushed by hand for juicy or soft herbs (no mortar needed).</summary>
    Crushed
}

/// <summary>
/// How a boil turn is performed. In KCD2, "boil with the bellows" means
/// lowering the cauldron, turning the hourglass, and continuously pumping the
/// bellows to sustain a rolling boil for that turn. "Plain" boiling just lets
/// the fire do the work with no bellows. A recipe can mix both modes across
/// its turns (e.g. 1 bellows turn + 2 plain turns).
/// </summary>
public enum BoilMode
{
    /// <summary>
    /// Bellows-driven boil: the player pumps the bellows to sustain a vigorous
    /// boil throughout the hourglass turn. This is the "boil with bellows"
    /// recipe instruction.
    /// </summary>
    Bellows,

    /// <summary>
    /// Plain boil: the cauldron sits on the fire with no bellows pumping; the
    /// fire alone does the work. This is the "boil" (unqualified) instruction.
    /// </summary>
    Plain
}

/// <summary>
/// For bare <see cref="AlchemyActionKind.Pour"/> actions that are not a base
/// pour or a raw-herb pour, identifies the physical transfer being attempted.
/// KCD2 treats plate-to-cauldron and cauldron-to-alembic as distinct bench
/// gestures, so the UI and recipe checker must not collapse them.
/// </summary>
public enum AlchemyTransfer
{
    /// <summary>Legacy/unspecified transfer; the bench resolves from state.</summary>
    Auto,

    /// <summary>Tip prepared material from the item plate into the cauldron.</summary>
    PlateToCauldron,

    /// <summary>Pour the finished cauldron contents into the alembic.</summary>
    CauldronToAlembic
}

/// <summary>
/// The complete set of alchemy verbs. These are <em>actions</em>, not tools —
/// per the KCD2 realistic model there is no "hands", "spoon", "knife", or
/// "filter cloth" entity. Every interaction is expressed as one of these
/// verbs operating on a physical container.
/// </summary>
public enum AlchemyActionKind
{
    /// <summary>Lower the cauldron onto the fire (begin heating).</summary>
    LowerCauldron,

    /// <summary>Raise the cauldron off the fire (pause heating).</summary>
    RaiseCauldron,

    /// <summary>Pull the bellows to intensify the fire. Only effective while lowered.</summary>
    PullBellows,

    /// <summary>Grind the current mortar contents to powder.</summary>
    Grind,

    /// <summary>Squeeze/crush a juicy herb by hand.</summary>
    Crush,

    /// <summary>Stir the cauldron contents. Direction carried by the action.</summary>
    Stir,

    /// <summary>Flip the hourglass — advances the boil by one "turn".</summary>
    TurnHourglass,

    /// <summary>Pour/transfer liquid or powder between containers.</summary>
    Pour,

    /// <summary>Extinguish the alembic candle at the correct distillation mark.</summary>
    ExtinguishDistillation,

    /// <summary>Bottle the finished brew into a phial.</summary>
    Bottle
}
