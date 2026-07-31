using BohemiX.Modules.Forge.Data;
using BohemiX.Modules.Forge.Models;

namespace BohemiX.Modules.Forge.Engine;

public interface IForgeQualityEvaluator
{
    ForgeResult Evaluate(ForgeSessionData session);
}

/// <summary>Extension point for a new craft operation without changing the engine loop.</summary>
public interface IForgeOperationHandler
{
    string OperationId { get; }
    bool CanHandle(ForgeStateId state);
    StateCommandResult Execute(ForgeSessionData session, ForgeCommand command);
}

public interface IForgeState
{
    ForgeStateId Id { get; }
    StateCommandResult Handle(ForgeSessionData session, ForgeCommand command);
    void Enter(ForgeSessionData session);
    void Exit(ForgeSessionData session);
    void Tick(ForgeSessionData session, TimeSpan elapsed);
}

public sealed record StateCommandResult(bool Accepted, string Message);

public sealed class ForgeSessionData
{
    internal const int GrindSegmentCount = 24;
    internal ForgeSessionData(IForgeCatalog catalog, IShapeSimulation shape)
    {
        Catalog = catalog;
        Shape = shape;
    }

    public IForgeCatalog Catalog { get; }
    public IShapeSimulation Shape { get; }
    public ForgeRecipeDefinition? Recipe { get; internal set; }
    public ForgeMaterialDefinition? Material { get; internal set; }
    public ForgeStateId State { get; internal set; } = ForgeStateId.RecipeSelect;
    public ForgeStateId RotateReturnState { get; internal set; } = ForgeStateId.Hammering;
    public double Heat { get; internal set; }
    public bool IsFlipped => Shape.IsFlipped;
    public HammerFace HammerFace { get; internal set; } = HammerFace.Flat;
    public int StrikeCount { get; internal set; }
    public int ReheatCount { get; internal set; }
    public double GrindCoverage { get; internal set; }
    public double GrindScore { get; internal set; }
    public bool GrindingEngaged { get; internal set; }
    public double GrinderSpeed { get; internal set; }
    internal double GrindQualityTotal { get; set; }
    internal double GrindContactTotal { get; set; }
    internal double[] GrindSegments { get; } = new double[GrindSegmentCount];
    public double QuenchScore { get; internal set; }
    public QuenchMedium? QuenchMedium { get; internal set; }
    public double QuenchDepth { get; internal set; }
    public double QuenchSpeed { get; internal set; }
    public double QuenchPeakDepth { get; internal set; }
    public double QuenchImmersionSeconds { get; internal set; }
    public int QuenchUpdates { get; internal set; }
    public double TemperatureScore { get; internal set; }
    public double StrikeScore { get; internal set; }
    public double SequenceScore { get; internal set; }
    public double ReheatScore { get; internal set; }
    public string LastMessage { get; internal set; } = "Choose a recipe to begin.";
    public string? ActiveZone { get; internal set; }
    public ForgeResult? Result { get; internal set; }
    internal Dictionary<string, int> ZoneStrikes { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal HashSet<string> CompletedZones { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal double WorkingSeconds { get; set; }
    internal double GoodHeatSeconds { get; set; }
    internal double HotWorkSeconds { get; set; }
    internal double ColdWorkSeconds { get; set; }
    internal double RotateTimer { get; set; }
}

public sealed class ForgeStateMachine
{
    private readonly Dictionary<ForgeStateId, IForgeState> states;
    private readonly ForgeSessionData session;

    public ForgeStateMachine(ForgeSessionData session)
    {
        this.session = session;
        states = new Dictionary<ForgeStateId, IForgeState>
        {
            [ForgeStateId.RecipeSelect] = new RecipeSelectState(this),
            [ForgeStateId.MaterialSelect] = new MaterialSelectState(this),
            [ForgeStateId.Heating] = new HeatingState(this),
            [ForgeStateId.Hammering] = new HammeringState(this),
            [ForgeStateId.RotateWorkpiece] = new RotateWorkpieceState(this),
            [ForgeStateId.Reheat] = new ReheatState(this),
            [ForgeStateId.Quenching] = new QuenchingState(this),
            [ForgeStateId.Grinding] = new GrindingState(this),
            [ForgeStateId.Inspection] = new InspectionState(this),
            [ForgeStateId.Result] = new ResultState(this)
        };
        Current = states[ForgeStateId.RecipeSelect];
    }

    public IForgeState Current { get; private set; }

    public StateCommandResult Handle(ForgeCommand command)
    {
        var result = Current.Handle(session, command);
        session.LastMessage = result.Message;
        session.State = Current.Id;
        return result;
    }

    public void Tick(TimeSpan elapsed)
    {
        Current.Tick(session, elapsed);
        session.State = Current.Id;
    }

    public void TransitionTo(ForgeStateId next)
    {
        if (!states.TryGetValue(next, out var state))
        {
            throw new InvalidOperationException($"Forge state '{next}' is not registered.");
        }

        Current.Exit(session);
        Current = state;
        session.State = next;
        Current.Enter(session);
    }

    public void Reset()
    {
        TransitionTo(ForgeStateId.RecipeSelect);
    }
}

public sealed class ForgeEngine
{
    // Hearth heating and open-air cooling intentionally use different clocks.
    // The billet should reach a workable glow without excessive bellows strokes,
    // then retain that heat long enough for a deliberate anvil pass.
    internal const double HeatingTimeScale = 1d / 3d;
    internal const double CoolingTimeScale = 1d / 9d;
    private readonly IForgeCatalog catalog;
    private readonly IForgeQualityEvaluator evaluator;
    private readonly LatticeShapeSimulation shape;
    private readonly ForgeSessionData session;
    private readonly ForgeStateMachine machine;
    private IReadOnlyList<ShapeCellSnapshot> shapeCells = [];
    private double fixedAccumulator;
    private long visualEventSequence;
    private ForgeVisualEvent? visualEvent;

    public ForgeEngine(IForgeCatalog? catalog = null, IForgeQualityEvaluator? evaluator = null)
    {
        this.catalog = catalog ?? new JsonForgeCatalog();
        this.evaluator = evaluator ?? new ForgeQualityEvaluator();
        shape = new LatticeShapeSimulation();
        session = new ForgeSessionData(this.catalog, shape);
        machine = new ForgeStateMachine(session);
        Snapshot = BuildSnapshot();
    }

    public ForgeSnapshot Snapshot { get; private set; }
    public ForgeStateId State => session.State;
    public ForgeSessionData Session => session;

    public ForgeCommandResult Execute(ForgeCommand command)
    {
        if (command is AbandonForgeCommand)
        {
            ResetSession();
            return new ForgeCommandResult(true, Snapshot.LastMessage, Snapshot);
        }

        if (command is RestartForgeCommand)
        {
            ResetSession();
            return new ForgeCommandResult(true, Snapshot.LastMessage, Snapshot);
        }

        var result = machine.Handle(command);
        if (result.Accepted)
        {
            visualEvent = CreateVisualEvent(command);
        }
        shapeCells = shape.SnapshotCells();
        Snapshot = BuildSnapshot();
        return new ForgeCommandResult(result.Accepted, result.Message, Snapshot);
    }

    public ForgeSnapshot Tick(TimeSpan elapsed)
    {
        fixedAccumulator += Math.Clamp(elapsed.TotalSeconds, 0, .25);
        const double fixedStep = 1d / 60d;
        var steps = 0;
        while (fixedAccumulator >= fixedStep && steps++ < 15)
        {
            machine.Tick(TimeSpan.FromSeconds(fixedStep));
            fixedAccumulator -= fixedStep;
        }

        Snapshot = BuildSnapshot();
        return Snapshot;
    }

    public bool RequiresContinuousTick => session.State is
        ForgeStateId.Heating or
        ForgeStateId.Hammering or
        ForgeStateId.RotateWorkpiece or
        ForgeStateId.Reheat or
        ForgeStateId.Quenching;

    private void ResetSession()
    {
        session.Recipe = null;
        session.Material = null;
        session.Heat = 0;
        session.HammerFace = HammerFace.Flat;
        session.StrikeCount = 0;
        session.ReheatCount = 0;
        session.GrindCoverage = 0;
        session.GrindScore = 0;
        session.GrindingEngaged = false;
        session.GrinderSpeed = 0;
        session.GrindQualityTotal = 0;
        session.GrindContactTotal = 0;
        Array.Clear(session.GrindSegments);
        session.QuenchScore = 0;
        session.QuenchMedium = null;
        session.QuenchDepth = 0;
        session.QuenchSpeed = 0;
        session.QuenchPeakDepth = 0;
        session.QuenchImmersionSeconds = 0;
        session.QuenchUpdates = 0;
        session.TemperatureScore = 0;
        session.StrikeScore = 0;
        session.SequenceScore = 0;
        session.ReheatScore = 0;
        session.ActiveZone = null;
        session.Result = null;
        session.ZoneStrikes.Clear();
        session.CompletedZones.Clear();
        session.WorkingSeconds = 0;
        session.GoodHeatSeconds = 0;
        session.HotWorkSeconds = 0;
        session.ColdWorkSeconds = 0;
        shape.Reset(new ShapeTemplateDefinition("sword", 1, 1, 1, .15, .48));
        shapeCells = shape.SnapshotCells();
        machine.Reset();
        session.LastMessage = "Choose a recipe to begin.";
        Snapshot = BuildSnapshot();
    }

    internal ForgeCommandResult FinishInspection()
    {
        session.Result = evaluator.Evaluate(session);
        machine.TransitionTo(ForgeStateId.Result);
        Snapshot = BuildSnapshot();
        return new ForgeCommandResult(true, "Inspection complete.", Snapshot);
    }

    private ForgeSnapshot BuildSnapshot()
    {
        var recipe = session.Recipe;
        var material = session.Material;
        var band = session.Heat switch
        {
            < .18 => ForgeHeatBand.Cold,
            < .38 => ForgeHeatBand.DarkRed,
            < .58 => ForgeHeatBand.CherryRed,
            < .82 => ForgeHeatBand.OrangeRed,
            < 1.03 => ForgeHeatBand.Yellow,
            _ => ForgeHeatBand.Burnt
        };
        var shapeReady = recipe is not null &&
                         session.CompletedZones.Count >= recipe.Zones.Count &&
                         shape.Metrics.Error <= recipe.CompletionTolerance;
        var canProceed = session.State switch
        {
            ForgeStateId.Heating or ForgeStateId.Reheat => session.Heat >= .38,
            ForgeStateId.Hammering => shapeReady,
            ForgeStateId.Quenching => session.QuenchUpdates > 0 &&
                                      session.QuenchPeakDepth >= .65 &&
                                      session.QuenchImmersionSeconds >= .18,
            ForgeStateId.Grinding => session.GrindingEngaged && session.GrindCoverage >= .55,
            ForgeStateId.Inspection => true,
            _ => shapeReady
        };
        return new ForgeSnapshot(
            session.State,
            recipe?.Id,
            material?.Id,
            recipe?.Name,
            material?.Name,
            band,
            session.Heat,
            session.IsFlipped,
            HammerFace.Flat,
            HammerFace.Flat,
            session.StrikeCount,
            session.ReheatCount,
            shape.Metrics.Error,
            session.GrindCoverage,
            session.ActiveZone,
            session.LastMessage,
            canProceed,
            session.Result,
            shapeCells,
            shape.Revision,
            visualEvent,
            recipe?.Shape,
            recipe?.Visual,
            session.QuenchMedium,
            session.GrindingEngaged,
            session.GrinderSpeed,
            session.GrindScore);
    }

    private ForgeVisualEvent? CreateVisualEvent(ForgeCommand command)
    {
        var sequence = ++visualEventSequence;
        return command switch
        {
            HammerStrikeCommand strike => new ForgeVisualEvent(sequence, ForgeVisualEventKind.HammerStrike, strike.X, strike.Y, strike.Force),
            RotateWorkpieceCommand => new ForgeVisualEvent(sequence, ForgeVisualEventKind.WorkpieceRotated),
            PumpBellowsCommand pump => new ForgeVisualEvent(sequence, ForgeVisualEventKind.BellowsPumped, Intensity: pump.Strength),
            BeginQuenchCommand begin => new ForgeVisualEvent(sequence, ForgeVisualEventKind.QuenchStarted, Medium: begin.Medium),
            UpdateQuenchCommand update => new ForgeVisualEvent(sequence, ForgeVisualEventKind.QuenchUpdated, Y: update.Depth, Intensity: update.Speed),
            CompleteQuenchCommand => new ForgeVisualEvent(sequence, ForgeVisualEventKind.QuenchCompleted),
            GrindStrokeCommand grind => new ForgeVisualEvent(sequence, ForgeVisualEventKind.GrindingStroke, grind.Position, Intensity: grind.Speed),
            _ => null
        };
    }

    internal static void Cool(ForgeSessionData session, double seconds, double multiplier)
    {
        if (session.Material is null)
        {
            return;
        }

        session.Heat = Math.Max(0, session.Heat - seconds * session.Material.CoolingRate * .055 * CoolingTimeScale * multiplier);
    }

    internal static void HeatInHearth(ForgeSessionData session, double seconds)
    {
        if (session.Material is null)
        {
            return;
        }

        // A billet resting in the coal bed gains heat even without active bellows work.
        // The low passive rate preserves observation time while bellows remain the useful control.
        session.Heat = Math.Min(1.15, session.Heat + Math.Max(0, seconds) * .012 * HeatingTimeScale);
    }

    internal static ForgeZoneDefinition? CurrentZone(ForgeSessionData session)
    {
        if (session.Recipe is null)
        {
            return null;
        }

        return session.Recipe.Zones
            .OrderBy(zone => zone.Order)
            .FirstOrDefault(zone => !session.CompletedZones.Contains(zone.Id))
            ?? session.Recipe.Zones.OrderBy(zone => zone.Order).LastOrDefault();
    }

    internal static ZoneStrikeResult UpdateZone(ForgeSessionData session, double x, double y, HammerFace face)
    {
        if (session.Recipe is null)
        {
            return default;
        }

        var logicalY = session.IsFlipped ? 1 - y : y;
        var matchingZones = session.Recipe.Zones
            .Where(zone => !session.CompletedZones.Contains(zone.Id))
            .Where(zone => x >= zone.Start && x <= zone.End &&
                           logicalY >= zone.YMin && logicalY <= zone.YMax)
            .ToArray();
        var specificZones = matchingZones
            .Where(zone => !zone.Id.Equals("correction", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var zone = (specificZones.Length > 0 ? specificZones : matchingZones)
            .OrderBy(zone => (zone.End - zone.Start) * (zone.YMax - zone.YMin))
            .ThenBy(zone => Math.Abs(x - (zone.Start + zone.End) * .5) +
                            Math.Abs(logicalY - (zone.YMin + zone.YMax) * .5))
            .FirstOrDefault();
        if (zone is null)
        {
            return new ZoneStrikeResult(false, true, false, false, null, 0, 0);
        }

        session.ActiveZone = zone.Label;
        face = HammerFace.Flat;

        session.ZoneStrikes.TryGetValue(zone.Id, out var strikes);
        session.ZoneStrikes[zone.Id] = ++strikes;
        var completed = strikes >= zone.RecommendedMinStrikes;
        if (completed)
        {
            session.CompletedZones.Add(zone.Id);
            session.ActiveZone = CurrentZone(session)?.Label;
        }

        return new ZoneStrikeResult(
            true,
            true,
            true,
            completed,
            zone,
            strikes,
            Math.Clamp(strikes / (double)zone.RecommendedMinStrikes, 0, 1));
    }
}

internal readonly record struct ZoneStrikeResult(
    bool RegionMatches,
    bool FaceMatches,
    bool Advanced,
    bool Completed,
    ForgeZoneDefinition? Zone,
    int StrikeCount,
    double Progress);

internal abstract class ForgeStateBase : IForgeState
{
    protected ForgeStateBase(ForgeStateMachine machine) => Machine = machine;
    protected ForgeStateMachine Machine { get; }
    public abstract ForgeStateId Id { get; }
    public virtual void Enter(ForgeSessionData session) { }
    public virtual void Exit(ForgeSessionData session) { }
    public virtual void Tick(ForgeSessionData session, TimeSpan elapsed) { }
    public abstract StateCommandResult Handle(ForgeSessionData session, ForgeCommand command);
    protected static StateCommandResult Accept(string message) => new(true, message);
    protected static StateCommandResult Reject(string message) => new(false, message);
}

internal sealed class RecipeSelectState(ForgeStateMachine machine) : ForgeStateBase(machine)
{
    public override ForgeStateId Id => ForgeStateId.RecipeSelect;
    public override StateCommandResult Handle(ForgeSessionData session, ForgeCommand command) => command switch
    {
        SelectRecipeCommand select when TrySelect(session, select.RecipeId) => SelectAccepted(session),
        _ => Reject("Choose a recipe before selecting a material.")
    };

    private static bool TrySelect(ForgeSessionData session, string id)
    {
        var recipe = session.Catalog.Recipes.FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (recipe is null)
        {
            return false;
        }

        session.Recipe = recipe;
        session.Shape.Reset(recipe.Shape, recipe.Billet);
        return true;
    }

    private StateCommandResult SelectAccepted(ForgeSessionData session)
    {
        Machine.TransitionTo(ForgeStateId.MaterialSelect);
        return Accept($"Recipe selected: {session.Recipe!.Name}.");
    }
}

internal sealed class MaterialSelectState(ForgeStateMachine machine) : ForgeStateBase(machine)
{
    public override ForgeStateId Id => ForgeStateId.MaterialSelect;
    public override StateCommandResult Handle(ForgeSessionData session, ForgeCommand command) => command switch
    {
        SelectMaterialCommand select when TrySelect(session, select.MaterialId) => Start(session),
        SelectRecipeCommand => Reject("Finish material selection before changing the recipe."),
        _ => Reject("Choose a material for the selected billet.")
    };

    private static bool TrySelect(ForgeSessionData session, string id)
    {
        var material = session.Catalog.Materials.FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (material is null)
        {
            return false;
        }

        session.Material = material;
        session.Heat = 0;
        session.StrikeCount = 0;
        session.ReheatCount = 0;
        session.CompletedZones.Clear();
        session.ZoneStrikes.Clear();
        return true;
    }

    private StateCommandResult Start(ForgeSessionData session)
    {
        Machine.TransitionTo(ForgeStateId.Heating);
        return Accept($"{session.Material!.Name} is ready for the hearth.");
    }
}

internal sealed class HeatingState(ForgeStateMachine machine) : ForgeStateBase(machine)
{
    public override ForgeStateId Id => ForgeStateId.Heating;
    public override void Enter(ForgeSessionData session) => session.ActiveZone = ForgeEngine.CurrentZone(session)?.Label;
    public override void Tick(ForgeSessionData session, TimeSpan elapsed)
    {
        ForgeEngine.HeatInHearth(session, elapsed.TotalSeconds);
    }

    public override StateCommandResult Handle(ForgeSessionData session, ForgeCommand command) => command switch
    {
        StartHeatingCommand => Accept("The billet rests in the fire."),
        PumpBellowsCommand pump => Pump(session, pump.Strength),
        MoveToAnvilCommand => MoveToAnvil(session),
        RotateWorkpieceCommand => Rotate(session, Id),
        _ => Reject("The billet belongs in the hearth right now.")
    };

    private StateCommandResult Pump(ForgeSessionData session, double strength)
    {
        var amount = Math.Clamp(strength, 0, 1) * .065 * ForgeEngine.HeatingTimeScale;
        session.Heat = Math.Min(1.15, session.Heat + amount);
        session.TemperatureScore += IsGoodHeat(session) ? amount : 0;
        return Accept("The bellows feed the coals.");
    }

    private StateCommandResult MoveToAnvil(ForgeSessionData session)
    {
        if (session.Heat < .38)
        {
            return Reject("The billet is still too dark and rigid for useful hammering.");
        }

        Machine.TransitionTo(ForgeStateId.Hammering);
        return Accept("The glowing billet is on the anvil.");
    }

    private StateCommandResult Rotate(ForgeSessionData session, ForgeStateId returnState)
    {
        session.RotateReturnState = returnState;
        Machine.TransitionTo(ForgeStateId.RotateWorkpiece);
        return Accept("The tongs turn the workpiece.");
    }

    private static bool IsGoodHeat(ForgeSessionData session) => session.Heat is >= .58 and <= .84;
}

internal sealed class HammeringState(ForgeStateMachine machine) : ForgeStateBase(machine)
{
    public override ForgeStateId Id => ForgeStateId.Hammering;
    public override void Enter(ForgeSessionData session) => session.ActiveZone = ForgeEngine.CurrentZone(session)?.Label;
    public override void Tick(ForgeSessionData session, TimeSpan elapsed)
    {
        var seconds = elapsed.TotalSeconds;
        ForgeEngine.Cool(session, seconds, 1);
        session.WorkingSeconds += seconds;
        if (session.Heat is >= .58 and <= .84)
        {
            session.GoodHeatSeconds += seconds;
        }
        else if (session.Heat < .32)
        {
            session.ColdWorkSeconds += seconds;
        }
        else if (session.Heat > .92)
        {
            session.HotWorkSeconds += seconds;
        }
    }

    public override StateCommandResult Handle(ForgeSessionData session, ForgeCommand command) => command switch
    {
        HammerStrikeCommand strike => Strike(session, strike),
        MoveToForgeCommand => Reheat(session),
        BeginQuenchCommand quench => BeginQuench(session, quench.Medium),
        RotateWorkpieceCommand => Rotate(session),
        ToggleHammerFaceCommand => ToggleFace(session),
        _ => Reject("The anvil is ready for a deliberate action.")
    };

    private StateCommandResult Strike(ForgeSessionData session, HammerStrikeCommand strike)
    {
        if (session.Material is null || session.Recipe is null)
        {
            return Reject("Select a recipe and material first.");
        }

        var plasticity = Math.Exp(-Math.Pow((session.Heat - session.Material.PlasticityPeak) / session.Material.PlasticityWidth, 2));
        var coldDamage = session.Heat < session.Material.ColdWorkThreshold ? (session.Material.ColdWorkThreshold - session.Heat) * session.Material.CrackSensitivity : 0;
        var hotDamage = session.Heat > session.Material.OverheatThreshold ? (session.Heat - session.Material.OverheatThreshold) * 1.8 : 0;
        var zoneResult = ForgeEngine.UpdateZone(session, strike.X, strike.Y, strike.Face);
        var guide = zoneResult.Advanced && zoneResult.Zone is { } zone
            ? new ForgeFormationGuide(
                zone.Id,
                zone.Start,
                zone.End,
                zoneResult.Progress,
                zone.Id.Equals("correction", StringComparison.OrdinalIgnoreCase))
            : null;
        session.Shape.ApplyHammer(
            strike.X,
            strike.Y,
            strike.Force * session.Catalog.Hammer.ForceMultiplier,
            strike.Face,
            plasticity,
            coldDamage + hotDamage,
            guide);
        session.StrikeCount++;
        session.StrikeScore += Math.Clamp(plasticity - coldDamage - hotDamage, -1, 1);
        session.SequenceScore += zoneResult.Advanced
            ? 1
            : -.40;
        session.Heat = Math.Max(0, session.Heat - (.012 + strike.Force * .004) * ForgeEngine.CoolingTimeScale);
        if (hotDamage > 0)
        {
            session.Shape.AddDamage(hotDamage);
        }

        var message = session.Heat < .32
            ? "The strike lands cold; the steel resists."
            : !zoneResult.RegionMatches
                ? "The strike falls outside the recipe working areas."
                : !zoneResult.FaceMatches
                    ? "The hammer face does not suit this operation."
                    : zoneResult.Completed
                        ? "This area is formed; choose any remaining area."
                        : "The hammer reshapes the selected area.";
        return Accept(message);
    }

    private StateCommandResult Reheat(ForgeSessionData session)
    {
        session.ReheatCount++;
        session.ReheatScore = session.Recipe is null
            ? 0
            : session.ReheatCount >= session.Recipe.RecommendedReheatMin && session.ReheatCount <= session.Recipe.RecommendedReheatMax ? 1 : .65;
        Machine.TransitionTo(ForgeStateId.Reheat);
        return Accept("Return the workpiece to the fire before the next pass.");
    }

    private StateCommandResult BeginQuench(ForgeSessionData session, QuenchMedium medium)
    {
        if (session.Recipe is null || session.Shape.Metrics.Error > session.Recipe.CompletionTolerance || session.CompletedZones.Count < session.Recipe.Zones.Count)
        {
            return Reject("The shape still needs correction before quenching.");
        }

        session.QuenchMedium = medium;
        session.QuenchDepth = 0;
        session.QuenchSpeed = 0;
        session.QuenchPeakDepth = 0;
        session.QuenchImmersionSeconds = 0;
        session.QuenchUpdates = 0;
        Machine.TransitionTo(ForgeStateId.Quenching);
        return Accept($"The workpiece enters the {medium.ToString().ToLowerInvariant()} bath.");
    }

    private StateCommandResult Rotate(ForgeSessionData session)
    {
        session.RotateReturnState = Id;
        Machine.TransitionTo(ForgeStateId.RotateWorkpiece);
        return Accept("The tongs turn the workpiece.");
    }

    private StateCommandResult ToggleFace(ForgeSessionData session)
    {
        session.HammerFace = HammerFace.Flat;
        return Accept("The flat hammer face is ready.");
    }
}

internal sealed class RotateWorkpieceState(ForgeStateMachine machine) : ForgeStateBase(machine)
{
    public override ForgeStateId Id => ForgeStateId.RotateWorkpiece;
    public override void Enter(ForgeSessionData session)
    {
        session.Shape.Flip();
        session.RotateTimer = 0;
        session.LastMessage = "The workpiece is turned to its other face.";
    }

    public override void Tick(ForgeSessionData session, TimeSpan elapsed)
    {
        session.RotateTimer += elapsed.TotalSeconds;
        if (session.RotateTimer >= .24)
        {
            Machine.TransitionTo(session.RotateReturnState);
        }
    }

    public override StateCommandResult Handle(ForgeSessionData session, ForgeCommand command) =>
        command is RotateWorkpieceCommand ? Accept("The workpiece is already turning.") : Reject("Wait for the turn to finish.");
}

internal sealed class ReheatState(ForgeStateMachine machine) : ForgeStateBase(machine)
{
    public override ForgeStateId Id => ForgeStateId.Reheat;
    public override void Tick(ForgeSessionData session, TimeSpan elapsed) => ForgeEngine.HeatInHearth(session, elapsed.TotalSeconds);
    public override StateCommandResult Handle(ForgeSessionData session, ForgeCommand command) => command switch
    {
        PumpBellowsCommand pump => Pump(session, pump.Strength),
        MoveToAnvilCommand => ToAnvil(session),
        RotateWorkpieceCommand => Rotate(session),
        _ => Reject("The workpiece needs another heat before returning to the anvil.")
    };

    private static StateCommandResult Pump(ForgeSessionData session, double strength)
    {
        session.Heat = Math.Min(1.15, session.Heat + Math.Clamp(strength, 0, 1) * .065 * ForgeEngine.HeatingTimeScale);
        return Accept("The reheat pass brings the glow back.");
    }

    private StateCommandResult ToAnvil(ForgeSessionData session)
    {
        if (session.Heat < .38)
        {
            return Reject("The workpiece needs a stronger glow before returning to the anvil.");
        }

        Machine.TransitionTo(ForgeStateId.Hammering);
        return Accept("The reheated workpiece returns to the anvil.");
    }

    private StateCommandResult Rotate(ForgeSessionData session)
    {
        session.RotateReturnState = Id;
        Machine.TransitionTo(ForgeStateId.RotateWorkpiece);
        return Accept("The tongs turn the reheated workpiece.");
    }
}

internal sealed class QuenchingState(ForgeStateMachine machine) : ForgeStateBase(machine)
{
    public override ForgeStateId Id => ForgeStateId.Quenching;
    public override void Tick(ForgeSessionData session, TimeSpan elapsed)
    {
        if (session.Material is null || session.QuenchMedium is null)
        {
            return;
        }

        var rate = session.QuenchMedium == QuenchMedium.Water ? 1.8 : 1.05;
        session.Heat = Math.Max(0, session.Heat - elapsed.TotalSeconds * rate * Math.Max(.35, session.QuenchDepth));
        if (session.QuenchDepth >= .55)
        {
            session.QuenchImmersionSeconds += elapsed.TotalSeconds;
        }
    }

    public override StateCommandResult Handle(ForgeSessionData session, ForgeCommand command) => command switch
    {
        UpdateQuenchCommand update => Update(session, update),
        CompleteQuenchCommand => Complete(session),
        _ => Reject("Hold the workpiece steadily in the bath.")
    };

    private static StateCommandResult Update(ForgeSessionData session, UpdateQuenchCommand update)
    {
        session.QuenchDepth = Math.Clamp(update.Depth, 0, 1);
        session.QuenchSpeed = Math.Max(0, update.Speed);
        session.QuenchPeakDepth = Math.Max(session.QuenchPeakDepth, session.QuenchDepth);
        session.QuenchUpdates++;
        return Accept(session.QuenchDepth > .72 ? "The bath closes around the hot steel." : "Lower the workpiece deeper into the bath.");
    }

    private StateCommandResult Complete(ForgeSessionData session)
    {
        if (session.Material is null || session.Recipe is null || session.QuenchMedium is null)
        {
            return Reject("The quench setup is incomplete.");
        }

        if (session.QuenchUpdates == 0 || session.QuenchPeakDepth < .65)
        {
            return Reject("Immerse the working edge before completing the quench.");
        }

        if (session.QuenchImmersionSeconds < .18)
        {
            return Reject("Hold the steel in the bath until the first rush of steam settles.");
        }

        var recommended = session.Recipe.RecommendedQuenchByMaterial.GetValueOrDefault(session.Material.Id, QuenchMedium.Oil);
        var mediumScore = recommended == session.QuenchMedium ? 1 : .35;
        var depthScore = 1 - Math.Abs(session.QuenchDepth - .82) / .82;
        var speedScore = 1 - Math.Abs(session.QuenchSpeed - .55) / .55;
        session.QuenchScore = Math.Clamp(mediumScore * .55 + Math.Clamp(depthScore, 0, 1) * .25 + Math.Clamp(speedScore, 0, 1) * .20, 0, 1);
        if (session.QuenchScore < .5)
        {
            session.Shape.AddDamage((1 - session.QuenchScore) * session.Material.CrackSensitivity * .14);
        }

        Machine.TransitionTo(ForgeStateId.Grinding);
        session.GrindingEngaged = false;
        session.GrinderSpeed = 0;
        return Accept("The quench is complete. Inspect the finished form before choosing whether to grind the edge.");
    }
}

internal sealed class GrindingState(ForgeStateMachine machine) : ForgeStateBase(machine)
{
    public override ForgeStateId Id => ForgeStateId.Grinding;
    public override StateCommandResult Handle(ForgeSessionData session, ForgeCommand command) => command switch
    {
        BeginGrindingCommand => Begin(session),
        SetGrinderSpeedCommand speed => SetSpeed(session, speed.Speed),
        SkipGrindingCommand => Skip(session),
        GrindStrokeCommand stroke => Stroke(session, stroke),
        SubmitInspectionCommand => Submit(session),
        _ => Reject("Sweep the edge across the wheel before inspection.")
    };

    private static StateCommandResult Begin(ForgeSessionData session)
    {
        if (session.GrindingEngaged)
        {
            return Accept("The weapon is already positioned at the grinding wheel.");
        }

        session.GrindingEngaged = true;
        return Accept("The weapon is braced across the wheel. Set the wheel speed and work along the edge.");
    }

    private static StateCommandResult SetSpeed(ForgeSessionData session, double speed)
    {
        if (!session.GrindingEngaged)
        {
            return Reject("Move the weapon to the grinding wheel before setting its speed.");
        }

        session.GrinderSpeed = Math.Clamp(speed, 0, 1.35);
        return Accept(session.GrinderSpeed switch
        {
            <= .01 => "The grinding wheel coasts to a stop.",
            < .72 => "The wheel turns slowly and removes little material.",
            > 1.18 => "The wheel is running fast; keep the edge moving.",
            _ => "The wheel settles into a controlled working rhythm."
        });
    }

    private StateCommandResult Skip(ForgeSessionData session)
    {
        session.GrindingEngaged = false;
        session.GrindCoverage = 0;
        session.GrindScore = 0;
        Machine.TransitionTo(ForgeStateId.Inspection);
        return Accept("The edge is left as-quenched and the weapon moves to final inspection.");
    }

    private StateCommandResult Stroke(ForgeSessionData session, GrindStrokeCommand stroke)
    {
        if (session.Recipe is null || session.Material is null)
        {
            return Reject("The grinding setup is incomplete.");
        }

        if (!session.GrindingEngaged)
        {
            return Reject("Click the grinding wheel before working the edge.");
        }

        var speedError = Math.Abs(stroke.Speed - session.Recipe.GrindTargetSpeed) / Math.Max(.01, session.Recipe.GrindSpeedTolerance);
        var movementStability = Math.Clamp(1 - speedError, 0, 1);
        var wheelStability = Math.Clamp(1 - Math.Abs(session.GrinderSpeed - 1) / .55, 0, 1);
        var stability = movementStability * .62 + wheelStability * .38;
        var contact = Math.Max(0, stroke.Delta);
        var center = Math.Clamp((int)Math.Round(Math.Clamp(stroke.Position, 0, 1) * (ForgeSessionData.GrindSegmentCount - 1)), 0, ForgeSessionData.GrindSegmentCount - 1);
        for (var offset = -2; offset <= 2; offset++)
        {
            var segment = center + offset;
            if ((uint)segment >= session.GrindSegments.Length)
            {
                continue;
            }

            var weight = offset switch { 0 => 1d, -1 or 1 => .68, _ => .28 };
            session.GrindSegments[segment] = Math.Clamp(
                session.GrindSegments[segment] + contact * stability * weight,
                0,
                1);
        }

        session.GrindCoverage = session.GrindSegments.Count(value => value >= .025) /
                                (double)ForgeSessionData.GrindSegmentCount;
        session.GrindQualityTotal += stability * contact;
        session.GrindContactTotal += contact;
        var averageQuality = session.GrindContactTotal <= 0
            ? 0
            : session.GrindQualityTotal / session.GrindContactTotal;
        session.GrindScore = Math.Clamp(averageQuality * session.GrindCoverage, 0, 1);
        var excessiveSpeed = Math.Max(speedError - 1.25, (session.GrinderSpeed - 1.18) * 2.2);
        if (excessiveSpeed > 0)
        {
            session.Shape.AddDamage(excessiveSpeed * .006 * Math.Max(0, stroke.Delta));
        }

        return Accept(excessiveSpeed > 0 ? "The wheel bites too quickly." : speedError > 1 ? "The edge movement is losing its even rhythm." : "The edge moves steadily across the stone.");
    }

    private StateCommandResult Submit(ForgeSessionData session)
    {
        if (!session.GrindingEngaged || session.GrindCoverage < .55)
        {
            return Reject("The edge still shows untouched sections along the wheel.");
        }

        Machine.TransitionTo(ForgeStateId.Inspection);
        return Accept("The workpiece is ready for a final inspection.");
    }
}

internal sealed class InspectionState(ForgeStateMachine machine) : ForgeStateBase(machine)
{
    public override ForgeStateId Id => ForgeStateId.Inspection;
    public override StateCommandResult Handle(ForgeSessionData session, ForgeCommand command) => command switch
    {
        SubmitInspectionCommand => Complete(session),
        _ => Reject("Rotate the finished workpiece, then submit the inspection.")
    };

    private StateCommandResult Complete(ForgeSessionData session)
    {
        session.Result = new ForgeQualityEvaluator().Evaluate(session);
        Machine.TransitionTo(ForgeStateId.Result);
        return Accept("The inspection is complete.");
    }
}

internal sealed class ResultState(ForgeStateMachine machine) : ForgeStateBase(machine)
{
    public override ForgeStateId Id => ForgeStateId.Result;
    public override StateCommandResult Handle(ForgeSessionData session, ForgeCommand command) => command switch
    {
        RestartForgeCommand => Restart(session),
        _ => Reject("The result is recorded. Start another craft when ready.")
    };

    private StateCommandResult Restart(ForgeSessionData session)
    {
        Machine.Reset();
        return Accept("A new forge session is ready.");
    }
}

public sealed class ForgeQualityEvaluator : IForgeQualityEvaluator
{
    public ForgeResult Evaluate(ForgeSessionData session)
    {
        var recipe = session.Recipe ?? throw new InvalidOperationException("Cannot evaluate without a recipe.");
        var shape = session.Shape.Metrics;
        var shapeScore = Math.Clamp(1 - shape.Error / Math.Max(.01, recipe.CompletionTolerance * 2.5), 0, 1);
        var tempScore = session.WorkingSeconds <= .01 ? 0 : Math.Clamp(session.GoodHeatSeconds / session.WorkingSeconds * 1.2 - session.HotWorkSeconds / session.WorkingSeconds * .35 - session.ColdWorkSeconds / session.WorkingSeconds * .25, 0, 1);
        var strikeScore = Math.Clamp(.5 + session.StrikeScore / Math.Max(1, session.StrikeCount * 2), 0, 1);
        var sequenceScore = Math.Clamp(.5 + session.SequenceScore / Math.Max(1, session.StrikeCount * 2), 0, 1);
        var reheatScore = session.ReheatScore <= 0 ? .7 : session.ReheatScore;
        var score = (int)Math.Round(
            shapeScore * 30 +
            tempScore * 18 +
            strikeScore * 14 +
            sequenceScore * 10 +
            reheatScore * 8 +
            session.QuenchScore * 10 +
            session.GrindScore * 10);
        var reasons = BuildReasons(session, recipe, shape, shapeScore, tempScore, sequenceScore, reheatScore);
        var quality = !shape.StructurallySound || score < 30
            ? ForgeQuality.Broken
            : score < 45 ? ForgeQuality.Poor
            : score < 60 ? ForgeQuality.Normal
            : score < 75 ? ForgeQuality.Fine
            : score < 90 ? ForgeQuality.Excellent
            : new[] { shapeScore, tempScore, strikeScore, sequenceScore, reheatScore, session.QuenchScore, session.GrindScore }.Min() >= .7
                ? ForgeQuality.Masterwork
                : ForgeQuality.Excellent;

        return new ForgeResult(quality, Math.Clamp(score, 0, 100), reasons, shape, session.StrikeCount, session.ReheatCount, session.QuenchMedium, session.GrindCoverage);
    }

    private static IReadOnlyList<QualityReason> BuildReasons(ForgeSessionData session, ForgeRecipeDefinition recipe, ShapeMetrics shape, double shapeScore, double tempScore, double sequenceScore, double reheatScore)
    {
        var reasons = new List<QualityReason>();
        reasons.Add(tempScore >= .7
            ? new QualityReason("温控", "大部分锤击都保持在明亮的橙红工作热区。", true)
            : new QualityReason("温控", "较多加工发生在金属光泽已经变暗之后。", false));
        reasons.Add(shapeScore >= .7
            ? new QualityReason("形状", "轮廓与厚度已经贴近配方目标。", true)
            : new QualityReason("形状", "成品仍存在局部厚度或轮廓误差。", false));
        reasons.Add(sequenceScore >= .7
            ? new QualityReason("工序", "各加工区域按照配方顺序完成。", true)
            : new QualityReason("工序", "部分锤击越过了当前应加工的区域。", false));
        reasons.Add(reheatScore >= .8
            ? new QualityReason("回炉", "回炉次数保持在配方建议的加工节奏内。", true)
            : new QualityReason("回炉", "回炉次数影响了钢材的一致性。", false));
        reasons.Add(session.QuenchScore >= .7
            ? new QualityReason("淬火", "介质、浸入深度和速度与钢材匹配。", true)
            : new QualityReason("淬火", "介质、深度或浸入速度不适合这类钢材。", false));
        reasons.Add(session.GrindScore >= .7
            ? new QualityReason("打磨", "刃口以稳定、均匀的走刀完成打磨。", true)
            : new QualityReason("打磨", "砂轮速度或刃口覆盖不够均匀。", false));
        if (shape.FeatureError > .2)
        {
            reasons.Add(new QualityReason("损伤", "金属上留有冷锤、过热或急促加工造成的应变。", false));
        }

        return reasons;
    }
}
