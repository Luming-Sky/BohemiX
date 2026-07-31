namespace BohemiX.Core.Models.Alchemy;

/// <summary>
/// One executable step in an alchemy procedure — an immutable verb plus the
/// parameters that particular verb needs. Only the fields relevant to the
/// <see cref="Kind"/> are populated; the rest are <c>null</c>. This record is
/// used both inside recipe definitions (the canonical procedure) and to
/// describe actions a player actually performed (for replay/evaluation).
/// </summary>
/// <param name="Kind">The verb to execute (never a tool — see <see cref="AlchemyActionKind"/>).</param>
/// <param name="Base">Set only for the base-pour step (<see cref="AlchemyActionKind.Pour"/> from Base Zone).</param>
/// <param name="HerbId">Herb id for add/grind/crush steps; must resolve to a known <see cref="Herb.Id"/>.</param>
/// <param name="HerbState">Freshness of the herb being added (Fresh/Dried).</param>
/// <param name="Direction">Stir direction, set only for <see cref="AlchemyActionKind.Stir"/>.</param>
/// <param name="Turns">How many hourglass turns to advance, for <see cref="AlchemyActionKind.TurnHourglass"/>.</param>
/// <param name="StirCount">Number of stir rotations, for <see cref="AlchemyActionKind.Stir"/>.</param>
/// <param name="Count">
/// Unit count for <see cref="AlchemyActionKind.Grind"/> and
/// <see cref="AlchemyActionKind.Crush"/> (how many herbs are processed at
/// once). Defaults to 1 when null.
/// </param>
/// <param name="BoilMode">
/// For <see cref="AlchemyActionKind.TurnHourglass"/>: whether this boil turn
/// is <see cref="BoilMode.Bellows"/> (pump the bellows to sustain a rolling
/// boil) or <see cref="BoilMode.Plain"/> (fire alone).
/// </param>
/// <param name="Transfer">
/// For bare <see cref="AlchemyActionKind.Pour"/> actions: the physical transfer
/// being attempted, such as plate-to-cauldron or cauldron-to-alembic.
/// </param>
public sealed record AlchemyAction(
    AlchemyActionKind Kind,
    AlchemyBase? Base = null,
    string? HerbId = null,
    HerbState? HerbState = null,
    StirDirection? Direction = null,
    int? Turns = null,
    int? StirCount = null,
    int? Count = null,
    BoilMode? BoilMode = null,
    AlchemyTransfer? Transfer = null)
{
    /// <summary>Convenience factory for lowering the cauldron onto the fire.</summary>
    public static AlchemyAction LowerCauldron() => new(AlchemyActionKind.LowerCauldron);

    /// <summary>Convenience factory for raising the cauldron off the fire.</summary>
    public static AlchemyAction RaiseCauldron() => new(AlchemyActionKind.RaiseCauldron);

    /// <summary>Convenience factory for pulling the bellows.</summary>
    public static AlchemyAction PullBellows() => new(AlchemyActionKind.PullBellows);

    /// <summary>Convenience factory for a base-pour step.</summary>
    public static AlchemyAction PourBase(AlchemyBase alchemyBase) =>
        new(AlchemyActionKind.Pour, Base: alchemyBase);

    /// <summary>Convenience factory for adding raw herbs straight to the cauldron.</summary>
    public static AlchemyAction AddHerb(string herbId, HerbState state, int count = 1) =>
        new(AlchemyActionKind.Pour, HerbId: herbId, HerbState: state, Count: count);

    /// <summary>
    /// Convenience factory for a bare Pour with no explicit source — the bench
    /// resolves it from current state: plate→cauldron if the plate holds
    /// material, otherwise cauldron→alembic (the distillation transfer).
    /// </summary>
    public static AlchemyAction Pour(AlchemyTransfer transfer = AlchemyTransfer.Auto) =>
        new(AlchemyActionKind.Pour, Transfer: transfer);

    /// <summary>Convenience factory for finishing distillation at the correct mark.</summary>
    public static AlchemyAction ExtinguishDistillation() =>
        new(AlchemyActionKind.ExtinguishDistillation);

    /// <summary>
    /// Convenience factory for grinding an herb in the mortar &amp; pestle. The
    /// grind verb is self-contained: it carries the herb (and count) and
    /// produces powder on the plate in one step, matching KCD2's
    /// "load mortar then grind" interaction.
    /// </summary>
    public static AlchemyAction Grind(string herbId, HerbState state, int count = 1) =>
        new(AlchemyActionKind.Grind, HerbId: herbId, HerbState: state, Count: count);

    /// <summary>Convenience factory for hand-crushing a juicy herb.</summary>
    public static AlchemyAction CrushHerb(string herbId, HerbState state, int count = 1) =>
        new(AlchemyActionKind.Crush, HerbId: herbId, HerbState: state, Count: count);

    /// <summary>Convenience factory for a stir step.</summary>
    public static AlchemyAction Stir(StirDirection direction, int count = 1) =>
        new(AlchemyActionKind.Stir, Direction: direction, StirCount: count);

    /// <summary>
    /// Convenience factory for advancing the boil by <paramref name="turns"/>
    /// turns in the given <paramref name="mode"/> (bellows vs plain).
    /// </summary>
    public static AlchemyAction BoilTurns(int turns, global::BohemiX.Core.Models.Alchemy.BoilMode mode = global::BohemiX.Core.Models.Alchemy.BoilMode.Plain) =>
        new(AlchemyActionKind.TurnHourglass, Turns: turns, BoilMode: mode);

    /// <summary>Convenience factory for bottling the finished brew into a phial.</summary>
    public static AlchemyAction Bottle() => new(AlchemyActionKind.Bottle);
}
