using BohemiX.Modules.Forge.Data;
using BohemiX.Modules.Forge.Engine;
using BohemiX.Modules.Forge.Models;

namespace BohemiX.Modules.Forge.Tests;

public sealed class LatticeShapeSimulationTests
{
    [Theory]
    [InlineData("duelling-longsword")]
    [InlineData("basilard")]
    [InlineData("bearded-axe")]
    public void RecipeBilletStartsUnformedWithTargetMass(string recipeId)
    {
        var recipe = new ForgeCatalog().GetRecipe(recipeId);
        var shape = new LatticeShapeSimulation();
        shape.Reset(recipe.Shape, recipe.Billet);
        var cells = shape.SnapshotCells();
        var occupied = cells.Count(cell => cell.Occupancy > .05);
        var targetOccupied = TargetCells(recipe.Shape).Length;

        Assert.NotEqual(targetOccupied, occupied);
        Assert.All(cells.Where(cell => cell.Occupancy > .05), cell => Assert.Equal(0, cell.Formation));
        Assert.InRange(shape.Metrics.MaterialMass / shape.Metrics.TargetMass, .985, 1.015);
    }

    [Fact]
    public void CorrectGuideFormsItsZoneWhileWrongStrikeDoesNotAdvanceFormation()
    {
        var recipe = new ForgeCatalog().GetRecipe("duelling-longsword");
        var zone = recipe.Zones.OrderBy(item => item.Order).First();
        var guided = new LatticeShapeSimulation();
        var wrong = new LatticeShapeSimulation();
        guided.Reset(recipe.Shape, recipe.Billet);
        wrong.Reset(recipe.Shape, recipe.Billet);
        var initialError = guided.Metrics.Error;

        guided.ApplyHammer(
            (zone.Start + zone.End) * .5,
            .5,
            .55,
            zone.RecommendedFace ?? HammerFace.Flat,
            .9,
            0,
            new ForgeFormationGuide(zone.Id, zone.Start, zone.End, .5, false));
        wrong.ApplyHammer(.5, .02, .55, HammerFace.Flat, .9, 0);

        Assert.Contains(guided.SnapshotCells(), cell => cell.Formation > .25);
        Assert.DoesNotContain(wrong.SnapshotCells(), cell => cell.Formation > 0);
        Assert.True(guided.Metrics.Error < initialError);
        Assert.InRange(guided.Metrics.MaterialMass / recipe.Billet!.MassScale / guided.Metrics.TargetMass, .985, 1.015);
    }

    [Fact]
    public void AxeHaftSeatStaysSolidWhenItsZoneFinishes()
    {
        var recipe = new ForgeCatalog().GetRecipe("bearded-axe");
        var shape = new LatticeShapeSimulation();
        shape.Reset(recipe.Shape, recipe.Billet);
        var haftSeat = recipe.Zones.Single(zone => zone.Id == "haft-seat");
        var seatX = (int)Math.Round(recipe.Shape.EyeCenterX * (shape.Columns - 1));
        var seatY = (int)Math.Round((recipe.Shape.EyeCenterY + .5) * (shape.Rows - 1));

        shape.ApplyHammer(
            (haftSeat.Start + haftSeat.End) * .5,
            .5,
            .4,
            HammerFace.Flat,
            .9,
            0,
            new ForgeFormationGuide(haftSeat.Id, haftSeat.Start, haftSeat.End, 1, false));

        Assert.Contains(shape.SnapshotCells(), cell =>
            cell.X == seatX && cell.Y == seatY && cell.Occupancy > .95);
    }

    [Fact]
    public void HammerChangesOnlyALocalAreaAndKeepsMostMaterialMass()
    {
        var recipe = new ForgeCatalog().GetRecipe("duelling-longsword");
        var shape = new LatticeShapeSimulation();
        shape.Reset(recipe.Shape);
        var before = shape.SnapshotCells().ToDictionary(cell => (cell.X, cell.Y));
        var massBefore = shape.Metrics.MaterialMass;

        shape.ApplyHammer(.5, .5, .8, HammerFace.Flat, .95, 0);

        var after = shape.SnapshotCells();
        var changed = after.Count(cell => before.TryGetValue((cell.X, cell.Y), out var old) && Math.Abs(old.Thickness - cell.Thickness) > .0001);
        Assert.InRange(changed, 1, before.Count / 3);
        Assert.InRange(shape.Metrics.MaterialMass / massBefore, .94, 1.01);
    }

    [Fact]
    public void HammerCompressionDoesNotPoolMaterialIntoTheStrikeCenterColumn()
    {
        var recipe = new ForgeCatalog().GetRecipe("duelling-longsword");
        var shape = new LatticeShapeSimulation();
        shape.Reset(recipe.Shape, recipe.Billet);
        var before = ColumnMass(shape.SnapshotCells());

        shape.ApplyHammer(.5, .5, .95, HammerFace.Flat, .95, 0);
        var after = ColumnMass(shape.SnapshotCells());

        for (var x = 0; x < shape.Columns; x++)
        {
            if (x is >= 40 and <= 55)
            {
                continue;
            }

            Assert.InRange(Math.Abs(after.GetValueOrDefault(x) - before.GetValueOrDefault(x)), 0, .000001);
        }
    }

    [Fact]
    public void GuidedFormationHasAContinuousColumnBoundary()
    {
        var recipe = new ForgeCatalog().GetRecipe("duelling-longsword");
        var zone = recipe.Zones.OrderBy(item => item.Order).First();
        var shape = new LatticeShapeSimulation();
        shape.Reset(recipe.Shape, recipe.Billet);
        shape.ApplyHammer(
            (zone.Start + zone.End) * .5,
            .5,
            .65,
            zone.RecommendedFace ?? HammerFace.Flat,
            .95,
            0,
            new ForgeFormationGuide(zone.Id, zone.Start, zone.End, .7, false));

        var formation = shape.SnapshotCells()
            .GroupBy(cell => cell.X)
            .ToDictionary(group => group.Key, group => group.Max(cell => cell.Formation));
        var ordered = formation.OrderBy(pair => pair.Key).ToArray();
        for (var i = 1; i < ordered.Length; i++)
        {
            Assert.InRange(Math.Abs(ordered[i].Value - ordered[i - 1].Value), 0, .24);
        }
    }

    [Fact]
    public void FlipMirrorsTheStrikeSideWithoutChangingTheExistingShape()
    {
        var recipe = new ForgeCatalog().GetRecipe("basilard");
        var shape = new LatticeShapeSimulation();
        shape.Reset(recipe.Shape);
        var mass = shape.Metrics.MaterialMass;

        shape.Flip();

        Assert.True(shape.IsFlipped);
        Assert.Equal(mass, shape.Metrics.MaterialMass, 8);
    }

    [Fact]
    public void AxeBilletAndTargetKeepTheHaftSeatSolid()
    {
        var shape = new LatticeShapeSimulation();
        var recipe = new ForgeCatalog().GetRecipe("bearded-axe");
        var definition = recipe.Shape;
        shape.Reset(definition, recipe.Billet);
        var seatX = (int)Math.Round(definition.EyeCenterX * (shape.Columns - 1));
        var seatY = (int)Math.Round((definition.EyeCenterY + .5) * (shape.Rows - 1));
        Assert.Contains(shape.SnapshotCells(), cell => cell.X == seatX && cell.Y == seatY && cell.Occupancy > .9);
        Assert.Equal(1, ForgeShapeProfileSampler.TargetAt(
            definition,
            seatX / (double)(shape.Columns - 1),
            seatY / (double)(shape.Rows - 1)).Occupancy);
        Assert.Equal(0, definition.EyeWidth);
        Assert.Equal(0, definition.EyeHeight);
    }

    [Fact]
    public void AxeTargetEndsInAShortPollInsteadOfAHandleLikeRod()
    {
        var shape = new LatticeShapeSimulation();
        var definition = new ForgeCatalog().GetRecipe("bearded-axe").Shape;
        var occupied = TargetCells(definition);

        var bodyWidth = ColumnWidth(occupied, 55);
        var pollWidth = ColumnWidth(occupied, 92);

        Assert.True(bodyWidth > pollWidth);
        Assert.InRange(pollWidth, 4, 10);
        Assert.Contains(occupied, cell => cell.X >= 94);
    }

    [Theory]
    [InlineData("duelling-longsword")]
    [InlineData("basilard")]
    public void SwordTargetPlacesTangOnLeftAndTapersTipOnRight(string recipeId)
    {
        var shape = new LatticeShapeSimulation();
        var occupied = TargetCells(new ForgeCatalog().GetRecipe(recipeId).Shape);

        var tangWidth = ColumnWidth(occupied, 10);
        var bladeWidth = ColumnWidth(occupied, 38);
        var tipShoulderWidth = ColumnWidth(occupied, 76);
        var tipWidth = ColumnWidth(occupied, 90);

        Assert.InRange(tangWidth, 2, 8);
        Assert.True(bladeWidth > tangWidth, "The blade must widen after the left-side tang.");
        Assert.True(tipWidth < tipShoulderWidth, "The right side must taper into the point.");
        Assert.Contains(occupied, cell => cell.X >= 88);
    }

    private static int ColumnWidth(IEnumerable<ShapeCellSnapshot> cells, int x) =>
        cells.Count(cell => cell.X == x && cell.Occupancy > .05);

    private static Dictionary<int, double> ColumnMass(IEnumerable<ShapeCellSnapshot> cells) =>
        cells.GroupBy(cell => cell.X)
            .ToDictionary(group => group.Key, group => group.Sum(cell => cell.Occupancy * cell.Thickness));

    private static ShapeCellSnapshot[] TargetCells(ShapeTemplateDefinition definition)
    {
        var result = new List<ShapeCellSnapshot>();
        for (var x = 0; x < 96; x++)
        for (var y = 0; y < 32; y++)
        {
            var target = ForgeShapeProfileSampler.TargetAt(definition, x / 95d, y / 31d);
            if (target.Occupancy > .05)
            {
                result.Add(new ShapeCellSnapshot(x, y, target.Occupancy, target.Thickness, 0, 0, 1));
            }
        }
        return result.ToArray();
    }
}
