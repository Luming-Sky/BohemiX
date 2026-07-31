using BohemiX.Modules.Forge.Data;
using BohemiX.Modules.Forge.Engine;
using BohemiX.Modules.Forge.Models;

namespace BohemiX.Modules.Forge.Tests;

public sealed class ForgeQualityEvaluatorTests
{
    [Fact]
    public void ExcellentProcessProducesExcellentOrMasterworkResult()
    {
        var session = CreateSession();
        session.WorkingSeconds = 10;
        session.GoodHeatSeconds = 10;
        session.StrikeCount = 48;
        session.StrikeScore = 48;
        session.SequenceScore = 12;
        session.ReheatScore = 1;
        session.QuenchScore = 1;
        session.GrindScore = 1;
        session.GrindCoverage = 1;

        var result = new ForgeQualityEvaluator().Evaluate(session);

        Assert.True(result.Quality is ForgeQuality.Excellent or ForgeQuality.Masterwork);
        Assert.Contains(result.Reasons, reason => reason.Category == "温控" && reason.Positive);
        Assert.Contains(result.Reasons, reason => reason.Category == "打磨" && reason.Positive);
    }

    [Fact]
    public void SevereDamageAlwaysProducesBrokenResult()
    {
        var session = CreateSession();
        session.Shape.AddDamage(100);
        session.WorkingSeconds = 10;
        session.GoodHeatSeconds = 10;
        session.StrikeCount = 40;
        session.StrikeScore = 40;
        session.SequenceScore = 10;
        session.ReheatScore = 1;
        session.QuenchScore = 1;
        session.GrindScore = 1;

        var result = new ForgeQualityEvaluator().Evaluate(session);

        Assert.Equal(ForgeQuality.Broken, result.Quality);
    }

    private static ForgeSessionData CreateSession()
    {
        var catalog = new ForgeCatalog();
        var shape = new LatticeShapeSimulation();
        var recipe = catalog.GetRecipe("basilard");
        shape.Reset(recipe.Shape, recipe.Billet);
        foreach (var zone in recipe.Zones.OrderBy(zone => zone.Order))
        {
            shape.ApplyHammer(
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
        }
        return new ForgeSessionData(catalog, shape)
        {
            Recipe = recipe,
            Material = catalog.GetMaterial("balanced-steel")
        };
    }
}
