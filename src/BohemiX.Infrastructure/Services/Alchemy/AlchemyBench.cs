using BohemiX.Core.Models.Alchemy;

namespace BohemiX.Infrastructure.Services.Alchemy;

/// <summary>
/// The physical bench state machine. Encapsulates one mutable
/// <see cref="BenchState"/> and the rules that govern how each
/// <see cref="AlchemyActionKind"/> mutates it. This class is the single source
/// of truth for "what physically happens" on the alchemy bench.
///
/// <para><b>Design — tolerant execution:</b> a physically invalid action
/// (e.g. pulling the bellows while the cauldron is raised) is reported via
/// the returned <see cref="ApplyResult"/> and NOT applied; the caller decides
/// whether to log it, skip it, or surface it. The bench never throws for
/// gameplay-logic violations — only for genuinely unreachable states (none in
/// practice). This mirrors KCD2, where a botched procedure still yields a
/// (weak) potion rather than crashing the bench.</para>
///
/// <para><b>No tool entities:</b> there is no field or method for "hands",
/// "spoon", "knife", or "filter cloth". Heat control is modelled purely
/// through <see cref="CauldronPosition"/> (physical displacement) and
/// <see cref="LiquidPhase"/>. Grinding is the mortar/pestle container; powder
/// flows through the plate staging area.</para>
/// </summary>
public sealed class AlchemyBench
{
    private readonly BenchState state = new();
    private readonly List<AlchemyAction> performedActions = [];

    /// <summary>The current mutable bench state. Do not mutate directly; use <see cref="Apply"/>.</summary>
    public BenchState State => state;

    /// <summary>
    /// Every action passed to <see cref="Apply"/>, in order — including those
    /// that were physically rejected. Used by quality evaluation, which scores
    /// the full attempted sequence against the recipe's canonical steps.
    /// </summary>
    public IReadOnlyList<AlchemyAction> PerformedActions => performedActions;

    /// <summary>Returns the current state as an immutable snapshot.</summary>
    public BenchState Snapshot() => state.Clone();

    /// <summary>
    /// Clears all bench state and the recorded action history, returning the
    /// bench to its initial empty configuration.
    /// </summary>
    public void Reset()
    {
        performedActions.Clear();
        ResetStateOnly();
    }

    private void ResetStateOnly()
    {
        state.CauldronPosition = CauldronPosition.Raised;
        state.LiquidPhase = LiquidPhase.Empty;
        state.CauldronBase = null;
        state.CauldronHerbs.Clear();
        state.Plate.Clear();
        state.BoilTurnsElapsed = 0;
        state.BellowsPulls = 0;
        state.BoilTurnLog.Clear();
        state.Stirs.Clear();
        state.DistillationTransferred = false;
        state.AlembicHeated = false;
        state.Bottled = false;
    }

    /// <summary>
    /// Attempts to apply one action to the bench. Returns whether the action
    /// was physically valid and actually executed.
    /// </summary>
    public ApplyResult Apply(AlchemyAction action)
    {
        performedActions.Add(action);
        var (applied, error) = TryApply(action);
        return new ApplyResult(applied, error);
    }

    private (bool Applied, string? Error) TryApply(AlchemyAction action)
    {
        switch (action.Kind)
        {
            case AlchemyActionKind.LowerCauldron:
                return ApplyLower();

            case AlchemyActionKind.RaiseCauldron:
                return ApplyRaise();

            case AlchemyActionKind.PullBellows:
                return ApplyBellows();

            case AlchemyActionKind.Grind:
                return ApplyGrind(action);

            case AlchemyActionKind.Crush:
                return ApplyCrush(action);

            case AlchemyActionKind.Stir:
                return ApplyStir(action);

            case AlchemyActionKind.TurnHourglass:
                return ApplyTurnHourglass(action);

            case AlchemyActionKind.Pour:
                return ApplyPour(action);

            case AlchemyActionKind.ExtinguishDistillation:
                return ApplyExtinguishDistillation();

            case AlchemyActionKind.Bottle:
                return ApplyBottle();

            default:
                return (false, $"Unknown action kind '{action.Kind}'.");
        }
    }

    // --- Heat control: the cauldron is a movable vessel -------------------

    private (bool, string?) ApplyLower()
    {
        if (state.CauldronPosition == CauldronPosition.Lowered)
        {
            return (false, "Cauldron is already lowered onto the fire.");
        }
        state.CauldronPosition = CauldronPosition.Lowered;
        // Contact with the fire heats the liquid. In KCD2 the fire alone brings
        // the cauldron to a boil (plain boil); the bellows intensify it for the
        // "boil with bellows" recipe instruction. Phase is visual only.
        if (state.CauldronBase is not null && state.LiquidPhase is LiquidPhase.Cold or LiquidPhase.Empty)
        {
            state.LiquidPhase = LiquidPhase.Boiling;
        }
        return (true, null);
    }

    private (bool, string?) ApplyRaise()
    {
        if (state.CauldronPosition == CauldronPosition.Raised)
        {
            return (false, "Cauldron is already raised off the fire.");
        }
        state.CauldronPosition = CauldronPosition.Raised;
        // Off the heat, boiling liquid cools back to merely heated.
        if (state.LiquidPhase == LiquidPhase.Boiling)
        {
            state.LiquidPhase = LiquidPhase.Heated;
        }
        return (true, null);
    }

    private (bool, string?) ApplyBellows()
    {
        // Bellows intensify the cauldron fire. Distillation uses the alembic
        // candle and is finished by ExtinguishDistillation, not by bellows.
        if (state.CauldronPosition != CauldronPosition.Lowered)
        {
            return (false, "Bellows have no effect while the cauldron is raised.");
        }
        state.BellowsPulls++;
        // Bellows push the liquid to a rolling boil (visual intensification).
        if (state.CauldronBase is not null && state.CauldronPosition == CauldronPosition.Lowered)
        {
            state.LiquidPhase = LiquidPhase.Boiling;
        }
        return (true, null);
    }

    // --- Solid preparation: mortar (grind) and hand-crush -----------------

    private (bool, string?) ApplyGrind(AlchemyAction action)
    {
        if (action.HerbId is null)
        {
            return (false, "Grind requires an herb.");
        }
        // The grind verb is self-contained per KCD2: it carries the herb and
        // count, runs the mortar & pestle, and lands the powder on the plate.
        var count = Math.Max(1, action.Count ?? 1);
        state.Plate.Add(new PlateEntry(
            action.HerbId,
            action.HerbState ?? HerbState.Fresh,
            HerbPreparation.Ground,
            count));
        return (true, null);
    }

    private (bool, string?) ApplyCrush(AlchemyAction action)
    {
        if (action.HerbId is null)
        {
            return (false, "Crush requires a target herb.");
        }
        var count = Math.Max(1, action.Count ?? 1);
        state.Plate.Add(new PlateEntry(
            action.HerbId,
            action.HerbState ?? HerbState.Fresh,
            HerbPreparation.Crushed,
            count));
        return (true, null);
    }

    // --- Mixing ------------------------------------------------------------

    private (bool, string?) ApplyStir(AlchemyAction action)
    {
        if (state.CauldronBase is null)
        {
            return (false, "Cannot stir an empty cauldron.");
        }
        var direction = action.Direction ?? StirDirection.Clockwise;
        var count = action.StirCount ?? 1;
        state.Stirs.Add(new StirRecord(direction, count));
        return (true, null);
    }

    private (bool, string?) ApplyTurnHourglass(AlchemyAction action)
    {
        // In KCD2 the hourglass advances once the cauldron is on the fire — both
        // plain boils and bellows boils count. The BoilMode (carried by the
        // action) is recorded so the evaluator can score mode fidelity.
        if (state.CauldronPosition != CauldronPosition.Lowered)
        {
            return (false, "Hourglass only advances while the cauldron is lowered onto the fire.");
        }
        if (state.CauldronBase is null)
        {
            return (false, "Nothing in the cauldron to boil.");
        }

        var turns = action.Turns ?? 1;
        var mode = action.BoilMode ?? BoilMode.Plain;
        state.BoilTurnsElapsed += turns;
        state.BoilTurnLog.Add(new BoilTurnRecord(turns, mode));
        return (true, null);
    }

    // --- Pour: base / herb / plate->cauldron / cauldron->alembic ----------

    private (bool, string?) ApplyPour(AlchemyAction action)
    {
        // 1) Pouring a base solvent from the Base Zone into the cauldron.
        if (action.Base is { } baseKind)
        {
            if (state.CauldronBase is not null)
            {
                return (false, $"Cauldron already holds {state.CauldronBase}; cannot add {baseKind}.");
            }
            state.CauldronBase = baseKind;
            state.LiquidPhase = state.CauldronPosition == CauldronPosition.Lowered
                ? LiquidPhase.Heated
                : LiquidPhase.Cold;
            return (true, null);
        }

        // 2) Pouring a raw herb straight from the shelf into the cauldron.
        if (action.HerbId is { } herbId)
        {
            if (state.CauldronBase is null)
            {
                return (false, "Cannot add herbs before a base solvent is poured.");
            }
            var count = Math.Max(1, action.Count ?? 1);
            for (var i = 0; i < count; i++)
            {
                state.CauldronHerbs.Add(new CauldronHerbEntry(
                    herbId,
                    action.HerbState ?? HerbState.Fresh,
                    HerbPreparation.None));
            }
            return (true, null);
        }

        var transfer = action.Transfer ?? AlchemyTransfer.Auto;

        // 3) Transferring prepared material from the plate to the cauldron.
        if (transfer != AlchemyTransfer.CauldronToAlembic && state.Plate.Count > 0)
        {
            if (state.CauldronBase is null)
            {
                return (false, "Cannot transfer plate contents into an empty cauldron.");
            }
            foreach (var entry in state.Plate)
            {
                state.CauldronHerbs.Add(new CauldronHerbEntry(
                    entry.HerbId,
                    entry.State,
                    entry.Preparation));
            }
            state.Plate.Clear();
            return (true, null);
        }

        if (transfer == AlchemyTransfer.PlateToCauldron)
        {
            return (false, "The item plate is empty.");
        }

        // 4) No explicit source resolved — interpret as cauldron->alembic
        //    transfer (the distillation path). Requires cauldron contents.
        if (transfer == AlchemyTransfer.CauldronToAlembic && state.Plate.Count > 0)
        {
            return (false, "Clear the item plate before transferring the cauldron to the alembic.");
        }

        if (state.CauldronBase is null || state.CauldronHerbs.Count == 0)
        {
            return (false, "Nothing in the cauldron to transfer to the alembic.");
        }
        state.DistillationTransferred = true;
        return (true, null);
    }

    private (bool, string?) ApplyExtinguishDistillation()
    {
        if (!state.DistillationTransferred)
        {
            return (false, "No active distillation to extinguish.");
        }

        if (state.AlembicHeated)
        {
            return (false, "Distillation is already complete.");
        }

        state.AlembicHeated = true;
        return (true, null);
    }

    // --- Finishing: bottle from cauldron or alembic -----------------------

    private (bool, string?) ApplyBottle()
    {
        if (state.Bottled)
        {
            return (false, "Already bottled.");
        }
        if (state.DistillationTransferred)
        {
            // Distillation recipes bottle from the alembic — but only after
            // the candle has been extinguished at the correct mark.
            if (!state.AlembicHeated)
            {
                return (false, "The alembic candle must be extinguished before bottling the distillate.");
            }
            state.Bottled = true;
            return (true, null);
        }
        if (state.CauldronBase is null)
        {
            return (false, "Nothing to bottle — the cauldron is empty.");
        }
        state.Bottled = true;
        return (true, null);
    }

    /// <summary>
    /// Whether the bench has reached a structurally valid end state for
    /// producing a potion: the cauldron received a base, at least one herb
    /// was added, and the brew was bottled.
    /// </summary>
    public bool CanFinalize =>
        state.CauldronBase is not null
        && state.CauldronHerbs.Count > 0
        && state.Bottled;

    /// <summary>The outcome of an <see cref="Apply"/> call.</summary>
    public sealed record ApplyResult(bool Applied, string? Error);
}
