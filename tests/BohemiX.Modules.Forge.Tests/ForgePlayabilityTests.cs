using BohemiX.Modules.Forge.Engine;
using BohemiX.Modules.Forge.Models;

namespace BohemiX.Modules.Forge.Tests;

public sealed class ForgePlayabilityTests
{
    [Theory]
    [InlineData("duelling-longsword", "soft-steel")]
    [InlineData("duelling-longsword", "balanced-steel")]
    [InlineData("duelling-longsword", "high-carbon-steel")]
    [InlineData("basilard", "soft-steel")]
    [InlineData("basilard", "balanced-steel")]
    [InlineData("basilard", "high-carbon-steel")]
    [InlineData("bearded-axe", "soft-steel")]
    [InlineData("bearded-axe", "balanced-steel")]
    [InlineData("bearded-axe", "high-carbon-steel")]
    public void DeliberatePlayerRouteCanReachResult(string recipeId, string materialId)
    {
        var engine = new ForgeEngine();
        Assert.True(engine.Execute(new SelectRecipeCommand(recipeId)).Accepted);
        Assert.True(engine.Execute(new SelectMaterialCommand(materialId)).Accepted);
        HeatForWork(engine);
        Assert.True(engine.Execute(new MoveToAnvilCommand()).Accepted);

        var recipe = Assert.IsType<ForgeRecipeDefinition>(engine.Session.Recipe);
        foreach (var zone in recipe.Zones.OrderBy(zone => zone.Order))
        {
            for (var strike = 0; strike < zone.RecommendedMinStrikes; strike++)
            {
                if (engine.Snapshot.Heat < .48)
                {
                    Assert.True(engine.Execute(new MoveToForgeCommand()).Accepted);
                    HeatForWork(engine);
                    Assert.True(engine.Execute(new MoveToAnvilCommand()).Accepted);
                }

                var x = (zone.Start + zone.End) * .5;
                var logicalY = zone.Id is "blade" or "body"
                    ? zone.YMax - (zone.YMax - zone.YMin) * .15
                    : zone.YMin + (zone.YMax - zone.YMin) * .15;
                var y = engine.Snapshot.IsFlipped ? 1 - logicalY : logicalY;
                var face = zone.RecommendedFace ?? HammerFace.Flat;
                Assert.True(engine.Execute(new HammerStrikeCommand(x, y, .42, face)).Accepted);
                engine.Tick(TimeSpan.FromMilliseconds(80));
            }

            Assert.Contains(zone.Id, engine.Session.CompletedZones);
            Assert.True(engine.Execute(new RotateWorkpieceCommand()).Accepted);
            engine.Tick(TimeSpan.FromMilliseconds(260));
        }

        Assert.True(engine.Snapshot.CanProceed,
            $"{recipeId}/{materialId} stopped at shape error {engine.Snapshot.ShapeError:F4} after {engine.Snapshot.StrikeCount} strikes.");

        var recommended = recipe.RecommendedQuenchByMaterial[materialId];
        Assert.True(engine.Execute(new BeginQuenchCommand(recommended)).Accepted);
        Assert.True(engine.Execute(new UpdateQuenchCommand(.82, .55)).Accepted);
        engine.Tick(TimeSpan.FromMilliseconds(220));
        Assert.True(engine.Execute(new CompleteQuenchCommand()).Accepted);
        Assert.True(engine.Execute(new BeginGrindingCommand()).Accepted);
        Assert.True(engine.Execute(new SetGrinderSpeedCommand(1)).Accepted);

        for (var stroke = 0; stroke < 10; stroke++)
        {
            Assert.True(engine.Execute(new GrindStrokeCommand(
                stroke / 9d,
                recipe.GrindTargetSpeed,
                .1)).Accepted);
        }

        Assert.True(engine.Execute(new SubmitInspectionCommand()).Accepted);
        Assert.True(engine.Execute(new SubmitInspectionCommand()).Accepted);
        Assert.Equal(ForgeStateId.Result, engine.State);
        var result = Assert.IsType<ForgeResult>(engine.Snapshot.Result);
        Assert.True(result.Quality >= ForgeQuality.Fine,
            $"{recipeId}/{materialId} completed the recommended route but only scored {result.Score} ({result.Quality}).");
        if (recipeId == "basilard" && materialId == "balanced-steel")
        {
            Assert.True(result.Quality == ForgeQuality.Masterwork,
                $"Expert route scored {result.Score}/{result.Quality}; error={result.Shape.Error:F4}, " +
                $"work={engine.Session.WorkingSeconds:F2}, good={engine.Session.GoodHeatSeconds:F2}, " +
                $"strike={engine.Session.StrikeScore:F2}/{engine.Session.StrikeCount}, " +
                $"sequence={engine.Session.SequenceScore:F2}, reheats={engine.Session.ReheatCount}/{engine.Session.ReheatScore:F2}, " +
                $"quench={engine.Session.QuenchScore:F2}, grind={engine.Session.GrindScore:F2}.");
        }
    }

    private static void HeatForWork(ForgeEngine engine)
    {
        var material = Assert.IsType<ForgeMaterialDefinition>(engine.Session.Material);
        while (engine.Snapshot.Heat < material.PlasticityPeak - .025)
        {
            Assert.True(engine.Execute(new PumpBellowsCommand(.8)).Accepted);
        }
    }
}
