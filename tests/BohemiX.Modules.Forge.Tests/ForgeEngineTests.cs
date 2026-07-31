using BohemiX.Modules.Forge.Data;
using BohemiX.Modules.Forge.Engine;
using BohemiX.Modules.Forge.Input;
using Avalonia.Input;
using BohemiX.Modules.Forge.Models;
using BohemiX.Modules.Forge.ViewModels;

namespace BohemiX.Modules.Forge.Tests;

public sealed class ForgeEngineTests
{
    [Fact]
    public void ContinuousTickRequirement_TracksTimeDependentStates()
    {
        var engine = new ForgeEngine();
        Assert.False(engine.RequiresContinuousTick);

        Assert.True(engine.Execute(new SelectRecipeCommand("duelling-longsword")).Accepted);
        Assert.False(engine.RequiresContinuousTick);

        Assert.True(engine.Execute(new SelectMaterialCommand("balanced-steel")).Accepted);
        Assert.True(engine.RequiresContinuousTick);
    }

    [Fact]
    public void EnforcesRecipeMaterialHeatingAndHammeringOrder()
    {
        var engine = new ForgeEngine();

        Assert.False(engine.Execute(new SelectMaterialCommand("soft-steel")).Accepted);
        Assert.True(engine.Execute(new SelectRecipeCommand("duelling-longsword")).Accepted);
        Assert.Equal(ForgeStateId.MaterialSelect, engine.State);
        Assert.True(engine.Execute(new SelectMaterialCommand("balanced-steel")).Accepted);
        Assert.Equal(ForgeStateId.Heating, engine.State);
        Assert.False(engine.Execute(new MoveToAnvilCommand()).Accepted);
        while (engine.Snapshot.Heat < .38)
        {
            Assert.True(engine.Execute(new PumpBellowsCommand(1)).Accepted);
        }
        Assert.True(engine.Execute(new MoveToAnvilCommand()).Accepted);
        Assert.Equal(ForgeStateId.Hammering, engine.State);
        Assert.Equal(HammerFace.Flat, engine.Snapshot.RecommendedHammerFace);
    }

    [Fact]
    public void BilletWarmsInHearthAndCoolsOnAnvil()
    {
        var engine = new ForgeEngine();
        engine.Execute(new SelectRecipeCommand("duelling-longsword"));
        engine.Execute(new SelectMaterialCommand("balanced-steel"));
        var cold = engine.Snapshot.Heat;

        engine.Tick(TimeSpan.FromSeconds(2));
        Assert.True(engine.Snapshot.Heat > cold);
        while (engine.Snapshot.Heat < .55) engine.Execute(new PumpBellowsCommand(1));
        engine.Execute(new MoveToAnvilCommand());
        var hot = engine.Snapshot.Heat;
        engine.Tick(TimeSpan.FromSeconds(2));

        Assert.True(engine.Snapshot.Heat < hot);
    }

    [Fact]
    public void HeatingIsResponsiveWhileOpenAirCoolingKeepsAUsefulWorkWindow()
    {
        Assert.InRange(ForgeEngine.HeatingTimeScale, .32, .34);
        Assert.InRange(ForgeEngine.CoolingTimeScale, .10, .12);
        Assert.True(ForgeEngine.HeatingTimeScale >= ForgeEngine.CoolingTimeScale * 2.9);
        var engine = new ForgeEngine();
        engine.Execute(new SelectRecipeCommand("duelling-longsword"));
        engine.Execute(new SelectMaterialCommand("balanced-steel"));
        for (var tick = 0; tick < 40; tick++) engine.Tick(TimeSpan.FromMilliseconds(250));
        Assert.InRange(engine.Snapshot.Heat, .039, .041);

        while (engine.Snapshot.Heat < .65) engine.Execute(new PumpBellowsCommand(1));
        engine.Execute(new MoveToAnvilCommand());
        var hot = engine.Snapshot.Heat;
        for (var tick = 0; tick < 40; tick++) engine.Tick(TimeSpan.FromMilliseconds(250));
        Assert.InRange(hot - engine.Snapshot.Heat, .047, .053);
    }

    [Fact]
    public void RotateUsesAnIndependentTransientStateAndReturnsToHammering()
    {
        var engine = CreateAtAnvil();

        engine.Execute(new RotateWorkpieceCommand());

        Assert.Equal(ForgeStateId.RotateWorkpiece, engine.State);
        Assert.True(engine.Snapshot.IsFlipped);
        engine.Tick(TimeSpan.FromMilliseconds(260));
        Assert.Equal(ForgeStateId.Hammering, engine.State);
    }

    [Fact]
    public void ReheatReturnsToHammeringAndTracksCount()
    {
        var engine = CreateAtAnvil();

        engine.Execute(new MoveToForgeCommand());
        Assert.Equal(ForgeStateId.Reheat, engine.State);
        Assert.Equal(1, engine.Snapshot.ReheatCount);
        engine.Execute(new PumpBellowsCommand(1));
        engine.Execute(new MoveToAnvilCommand());

        Assert.Equal(ForgeStateId.Hammering, engine.State);
    }

    [Fact]
    public void FixedStepProducesDeterministicHeatAndShape()
    {
        var first = CreateAtAnvil();
        var second = CreateAtAnvil();

        for (var i = 0; i < 8; i++)
        {
            first.Execute(new HammerStrikeCommand(.35 + i * .03, .5, .65, HammerFace.Flat));
            second.Execute(new HammerStrikeCommand(.35 + i * .03, .5, .65, HammerFace.Flat));
            first.Tick(TimeSpan.FromMilliseconds(50));
            second.Tick(TimeSpan.FromMilliseconds(50));
        }

        Assert.Equal(first.Snapshot.Heat, second.Snapshot.Heat, 10);
        Assert.Equal(first.Snapshot.ShapeError, second.Snapshot.ShapeError, 10);
    }

    [Fact]
    public void AcceptedHammerStrikePublishesShapeAndVisualFeedbackTogether()
    {
        var engine = CreateAtAnvil();
        var beforeRevision = engine.Snapshot.ShapeRevision;

        var result = engine.Execute(new HammerStrikeCommand(.48, .52, .65, HammerFace.Flat));

        Assert.True(result.Accepted);
        Assert.True(result.Snapshot.ShapeRevision > beforeRevision);
        var visual = Assert.IsType<ForgeVisualEvent>(result.Snapshot.VisualEvent);
        Assert.Equal(ForgeVisualEventKind.HammerStrike, visual.Kind);
        Assert.Equal(.48, visual.X, 3);
        Assert.Equal(.52, visual.Y, 3);
        Assert.Equal(.65, visual.Intensity, 3);
    }

    [Fact]
    public void CannotQuenchBeforeEveryRecipeZoneIsWorked()
    {
        var engine = CreateAtAnvil();

        var result = engine.Execute(new BeginQuenchCommand(QuenchMedium.Water));

        Assert.False(result.Accepted);
        Assert.Equal(ForgeStateId.Hammering, engine.State);
    }

    [Fact]
    public void OneCorrectStrikeCannotCompleteARecipeZone()
    {
        var engine = CreateAtAnvil();
        var zone = Assert.IsType<ForgeZoneDefinition>(ForgeEngine.CurrentZone(engine.Session));

        StrikeZone(engine, zone);

        Assert.DoesNotContain(zone.Id, engine.Session.CompletedZones);
        Assert.Equal(1, engine.Session.ZoneStrikes[zone.Id]);
    }

    [Fact]
    public void WrongVerticalAreaDoesNotAdvanceTheCurrentZone()
    {
        var engine = CreateAtAnvil();
        var zone = Assert.IsType<ForgeZoneDefinition>(ForgeEngine.CurrentZone(engine.Session));
        var wrongY = zone.YMin > .05 ? 0 : 1;

        engine.Execute(new HammerStrikeCommand((zone.Start + zone.End) * .5, wrongY, .5, zone.RecommendedFace ?? HammerFace.Flat));

        Assert.False(engine.Session.ZoneStrikes.ContainsKey(zone.Id));
        Assert.DoesNotContain(zone.Id, engine.Session.CompletedZones);
        Assert.True(engine.Session.SequenceScore < 0);
    }

    [Fact]
    public void AnyRecipeAreaCanCompleteBeforeTheSuggestedZone()
    {
        var engine = CreateAtAnvil();
        var zones = engine.Session.Recipe!.Zones.OrderBy(zone => zone.Order).ToArray();
        var suggested = zones[0];
        var later = zones[1];
        var x = (later.Start + later.End) * .5;
        var logicalY = later.YMin + (later.YMax - later.YMin) * .15;

        for (var strike = 0; strike < later.RecommendedMinStrikes; strike++)
        {
            engine.Execute(new HammerStrikeCommand(x, logicalY, .5, HammerFace.Flat));
        }

        Assert.Contains(later.Id, engine.Session.CompletedZones);
        Assert.DoesNotContain(suggested.Id, engine.Session.CompletedZones);
    }

    [Fact]
    public void LegacyHammerFaceInputIsNormalizedToFlatFace()
    {
        var engine = CreateAtAnvil();
        var zone = Assert.IsType<ForgeZoneDefinition>(ForgeEngine.CurrentZone(engine.Session));

#pragma warning disable CS0618
        engine.Execute(new HammerStrikeCommand(
            (zone.Start + zone.End) * .5,
            (zone.YMin + zone.YMax) * .5,
            .5,
            HammerFace.CrossPeen));
#pragma warning restore CS0618

        Assert.True(engine.Session.ZoneStrikes.ContainsKey(zone.Id));
        Assert.Equal(HammerFace.Flat, engine.Snapshot.HammerFace);
        Assert.Equal(HammerFace.Flat, engine.Snapshot.RecommendedHammerFace);
    }

    [Fact]
    public void CompleteWorkpieceCanFlowThroughQuenchGrindingAndInspection()
    {
        var engine = CreateAtAnvil();
        var session = engine.Session;
        Assert.NotNull(session.Recipe);
        CompleteWorkpiece(engine);

        var quench = engine.Execute(new BeginQuenchCommand(QuenchMedium.Water));
        Assert.True(quench.Accepted);
        Assert.Equal(ForgeStateId.Quenching, engine.State);
        Assert.Equal(QuenchMedium.Water, quench.Snapshot.ActiveQuenchMedium);
        engine.Execute(new UpdateQuenchCommand(.82, .55));
        engine.Tick(TimeSpan.FromMilliseconds(220));
        engine.Execute(new CompleteQuenchCommand());
        Assert.Equal(ForgeStateId.Grinding, engine.State);
        Assert.False(engine.Snapshot.GrindingEngaged);
        Assert.True(engine.Execute(new BeginGrindingCommand()).Accepted);
        Assert.True(engine.Snapshot.GrindingEngaged);
        for (var i = 0; i < 10; i++)
        {
            engine.Execute(new GrindStrokeCommand(i / 9d, session.Recipe.GrindTargetSpeed, .1));
        }

        Assert.Equal(session.GrindScore, engine.Snapshot.GrindingQuality, 6);

        engine.Execute(new SubmitInspectionCommand());
        Assert.Equal(ForgeStateId.Inspection, engine.State);
        engine.Execute(new SubmitInspectionCommand());

        Assert.Equal(ForgeStateId.Result, engine.State);
        Assert.NotNull(engine.Snapshot.Result);
    }

    [Fact]
    public void QuenchRequiresRealImmersionAndHoldingTime()
    {
        var engine = CreateAtAnvil();
        CompleteWorkpiece(engine);

        Assert.True(engine.Execute(new BeginQuenchCommand(QuenchMedium.Water)).Accepted);
        Assert.False(engine.Execute(new CompleteQuenchCommand()).Accepted);
        engine.Execute(new UpdateQuenchCommand(.82, .55));
        Assert.False(engine.Execute(new CompleteQuenchCommand()).Accepted);
        engine.Tick(TimeSpan.FromMilliseconds(220));
        Assert.True(engine.Snapshot.CanProceed);
        Assert.True(engine.Execute(new CompleteQuenchCommand()).Accepted);
    }

    [Fact]
    public void GrindingRequiresEngagementBeforeEdgeWork()
    {
        var engine = CreateAtAnvil();
        CompleteWorkpiece(engine);
        engine.Execute(new BeginQuenchCommand(QuenchMedium.Water));
        engine.Execute(new UpdateQuenchCommand(.82, .55));
        engine.Tick(TimeSpan.FromMilliseconds(220));
        engine.Execute(new CompleteQuenchCommand());

        Assert.False(engine.Execute(new SubmitInspectionCommand()).Accepted);
        Assert.False(engine.Execute(new GrindStrokeCommand(.5, engine.Session.Recipe!.GrindTargetSpeed, .1)).Accepted);
        Assert.True(engine.Execute(new BeginGrindingCommand()).Accepted);
        for (var i = 0; i < 6; i++)
        {
            engine.Execute(new GrindStrokeCommand(i / 5d, engine.Session.Recipe!.GrindTargetSpeed, .1));
        }
        Assert.True(engine.Snapshot.CanProceed);
        Assert.True(engine.Execute(new SubmitInspectionCommand()).Accepted);
    }

    [Fact]
    public void GrindingOneSpotCannotFakeFullEdgeCoverage()
    {
        var engine = CreateAtAnvil();
        CompleteWorkpiece(engine);
        engine.Execute(new BeginQuenchCommand(QuenchMedium.Water));
        engine.Execute(new UpdateQuenchCommand(.82, .55));
        engine.Tick(TimeSpan.FromMilliseconds(220));
        engine.Execute(new CompleteQuenchCommand());
        engine.Execute(new BeginGrindingCommand());

        for (var stroke = 0; stroke < 30; stroke++)
        {
            engine.Execute(new GrindStrokeCommand(.5, engine.Session.Recipe!.GrindTargetSpeed, .1));
        }

        Assert.True(engine.Snapshot.GrindCoverage < .3);
        Assert.False(engine.Snapshot.CanProceed);
        Assert.False(engine.Execute(new SubmitInspectionCommand()).Accepted);
    }

    [Fact]
    public void AsQuenchedEdgeCanSkipGrindingAndContinueToInspection()
    {
        var engine = CreateAtAnvil();
        CompleteWorkpiece(engine);
        engine.Execute(new BeginQuenchCommand(QuenchMedium.Water));
        engine.Execute(new UpdateQuenchCommand(.82, .55));
        engine.Tick(TimeSpan.FromMilliseconds(220));
        engine.Execute(new CompleteQuenchCommand());

        Assert.False(engine.Snapshot.GrindingEngaged);
        Assert.True(engine.Execute(new SkipGrindingCommand()).Accepted);
        Assert.Equal(ForgeStateId.Inspection, engine.State);
        Assert.Equal(0, engine.Snapshot.GrindCoverage);
    }

    [Fact]
    public void GrinderSpeedCanBeAdjustedOnlyAfterTheWheelIsEngaged()
    {
        var engine = CreateAtAnvil();
        CompleteWorkpiece(engine);
        engine.Execute(new BeginQuenchCommand(QuenchMedium.Water));
        engine.Execute(new UpdateQuenchCommand(.82, .55));
        engine.Tick(TimeSpan.FromMilliseconds(220));
        engine.Execute(new CompleteQuenchCommand());

        Assert.Equal(0, engine.Snapshot.GrinderSpeed, 3);
        Assert.False(engine.Execute(new SetGrinderSpeedCommand(.7)).Accepted);
        Assert.True(engine.Execute(new BeginGrindingCommand()).Accepted);
        Assert.True(engine.Execute(new SetGrinderSpeedCommand(.7)).Accepted);
        Assert.Equal(.7, engine.Snapshot.GrinderSpeed, 3);
        Assert.True(engine.Execute(new SetGrinderSpeedCommand(2)).Accepted);
        Assert.Equal(1.35, engine.Snapshot.GrinderSpeed, 3);
    }

    [Theory]
    [InlineData(0.00, 1, .10)]
    [InlineData(0.00, -1, 0.00)]
    [InlineData(1.00, 1, 1.10)]
    [InlineData(1.00, -1, .90)]
    [InlineData(1.35, 1, 1.35)]
    [InlineData(.05, -1, 0.00)]
    public void GrinderKeyboardStepIsBounded(double current, int direction, double expected)
    {
        Assert.Equal(expected, ForgeWorkshopViewModel.AdjustedGrinderSpeed(current, direction), 3);
    }

    [Theory]
    [InlineData(Key.Tab, ForgeInputAction.ToggleRecipe)]
    [InlineData(Key.Escape, ForgeInputAction.TogglePause)]
    [InlineData(Key.Space, ForgeInputAction.PumpBellows)]
    [InlineData(Key.Enter, ForgeInputAction.ContextAction)]
    [InlineData(Key.Q, ForgeInputAction.RotateWorkpiece)]
    [InlineData(Key.R, ForgeInputAction.Reheat)]
    [InlineData(Key.D1, ForgeInputAction.QuenchWater)]
    [InlineData(Key.NumPad2, ForgeInputAction.QuenchOil)]
    [InlineData(Key.G, ForgeInputAction.BeginGrinding)]
    [InlineData(Key.K, ForgeInputAction.SkipGrinding)]
    [InlineData(Key.Up, ForgeInputAction.IncreaseGrinderSpeed)]
    [InlineData(Key.S, ForgeInputAction.DecreaseGrinderSpeed)]
    public void KeyboardMapResolvesForgeActions(Key key, ForgeInputAction expected)
    {
        Assert.True(ForgeKeyboardMap.TryResolve(key, KeyModifiers.None, out var action));
        Assert.Equal(expected, action);
    }

    [Fact]
    public void KeyboardMapLeavesModifiedAndUnmappedKeysToTheApplicationShell()
    {
        Assert.False(ForgeKeyboardMap.TryResolve(Key.Q, KeyModifiers.Control, out _));
        Assert.False(ForgeKeyboardMap.TryResolve(Key.F5, KeyModifiers.None, out _));
    }

    [Theory]
    [InlineData(PhysicalKey.Q, ForgeInputAction.RotateWorkpiece)]
    [InlineData(PhysicalKey.R, ForgeInputAction.Reheat)]
    [InlineData(PhysicalKey.Digit1, ForgeInputAction.QuenchWater)]
    [InlineData(PhysicalKey.NumPad2, ForgeInputAction.QuenchOil)]
    [InlineData(PhysicalKey.W, ForgeInputAction.IncreaseGrinderSpeed)]
    [InlineData(PhysicalKey.S, ForgeInputAction.DecreaseGrinderSpeed)]
    public void KeyboardMapUsesPhysicalKeysWhenTextInputOrImeChangesLogicalKey(
        PhysicalKey key,
        ForgeInputAction expected)
    {
        Assert.True(ForgeKeyboardMap.TryResolve(key, KeyModifiers.None, out var action));
        Assert.Equal(expected, action);
    }

    private static ForgeEngine CreateAtAnvil()
    {
        var engine = new ForgeEngine();
        engine.Execute(new SelectRecipeCommand("duelling-longsword"));
        engine.Execute(new SelectMaterialCommand("balanced-steel"));
        while (engine.Snapshot.Heat < .70)
        {
            engine.Execute(new PumpBellowsCommand(1));
        }
        engine.Execute(new MoveToAnvilCommand());
        return engine;
    }

    private static void StrikeZone(ForgeEngine engine, ForgeZoneDefinition zone)
    {
        var logicalY = (zone.YMin + zone.YMax) * .5;
        var commandY = engine.Snapshot.IsFlipped ? 1 - logicalY : logicalY;
        engine.Execute(new HammerStrikeCommand(
            (zone.Start + zone.End) * .5,
            commandY,
            .5,
            zone.RecommendedFace ?? HammerFace.Flat));
    }

    private static void CompleteWorkpiece(ForgeEngine engine)
    {
        var session = engine.Session;
        foreach (var zone in session.Recipe!.Zones.OrderBy(zone => zone.Order))
        {
            session.Shape.ApplyHammer(
                (zone.Start + zone.End) * .5,
                (zone.YMin + zone.YMax) * .5,
                .05,
                zone.RecommendedFace ?? HammerFace.Flat,
                0,
                0,
                new ForgeFormationGuide(
                    zone.Id,
                    zone.Start,
                    zone.End,
                    1,
                    zone.Id.Equals("correction", StringComparison.OrdinalIgnoreCase)));
            session.CompletedZones.Add(zone.Id);
        }
    }
}
