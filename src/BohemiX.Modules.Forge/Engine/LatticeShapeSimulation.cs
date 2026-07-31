using BohemiX.Modules.Forge.Models;

namespace BohemiX.Modules.Forge.Engine;

/// <summary>
/// A small material lattice, deliberately deterministic and cheap enough to update on
/// every hammer strike. The renderer can turn the same cells into a dynamic mesh.
/// </summary>
public sealed class LatticeShapeSimulation : IShapeSimulation
{
    private const int ColumnCount = 96;
    private const int RowCount = 32;
    private readonly double[] occupancy = new double[ColumnCount * RowCount];
    private readonly double[] thickness = new double[ColumnCount * RowCount];
    private readonly double[] damage = new double[ColumnCount * RowCount];
    private readonly double[] finish = new double[ColumnCount * RowCount];
    private readonly double[] formation = new double[ColumnCount * RowCount];
    private readonly double[] initialOccupancy = new double[ColumnCount * RowCount];
    private readonly double[] initialThickness = new double[ColumnCount * RowCount];
    private readonly double[] thicknessBias = new double[ColumnCount * RowCount];
    private readonly double[] targetOccupancy = new double[ColumnCount * RowCount];
    private readonly double[] targetThickness = new double[ColumnCount * RowCount];
    private ShapeTemplateDefinition template = new("sword", 1, 1, 1, .15, .5);
    private bool isFlipped;

    public int Columns => ColumnCount;
    public int Rows => RowCount;
    public bool IsFlipped => isFlipped;
    public long Revision { get; private set; }
    public ShapeMetrics Metrics { get; private set; } = new(1, 1, 1, 1, 1, 1, false, 0, 0);

    public void Reset(ShapeTemplateDefinition nextTemplate, ForgeBilletDefinition? billet = null)
    {
        template = nextTemplate;
        isFlipped = false;
        Array.Clear(occupancy);
        Array.Clear(thickness);
        Array.Clear(damage);
        Array.Clear(finish);
        Array.Clear(formation);
        Array.Clear(initialOccupancy);
        Array.Clear(initialThickness);
        Array.Clear(thicknessBias);
        Array.Clear(targetOccupancy);
        Array.Clear(targetThickness);
        BuildTarget();
        BuildBillet(billet ?? CreateDefaultBillet(nextTemplate));

        RecalculateMetrics();
        Revision++;
    }

    public void Flip()
    {
        isFlipped = !isFlipped;
        Revision++;
    }

    public void ApplyHammer(
        double x,
        double y,
        double force,
        HammerFace face,
        double plasticity,
        double addedDamage,
        ForgeFormationGuide? guide = null)
    {
        face = HammerFace.Flat;
        x = Math.Clamp(x, 0, 1);
        y = Math.Clamp(isFlipped ? 1 - y : y, 0, 1);
        force = Math.Clamp(force, .05, 1.4);
        var centerX = (int)Math.Round(x * (ColumnCount - 1));
        var centerY = (int)Math.Round(y * (RowCount - 1));
        const int radiusX = 7;
        const int radiusY = 5;
        // Keep material on its forging column. The previous implementation pooled
        // every compressed column into the strike center, which created a visible
        // bulge and made the billet snake sideways after a few blows.
        var removedByColumn = new double[ColumnCount];
        var massBefore = CurrentMass();

        for (var ix = Math.Max(0, centerX - radiusX); ix <= Math.Min(ColumnCount - 1, centerX + radiusX); ix++)
        {
            for (var iy = Math.Max(0, centerY - radiusY); iy <= Math.Min(RowCount - 1, centerY + radiusY); iy++)
            {
                var dx = (ix - centerX) / (double)Math.Max(1, radiusX);
                var dy = (iy - centerY) / (double)Math.Max(1, radiusY);
                var kernel = Math.Exp(-2.2 * (dx * dx + dy * dy));
                var i = Index(ix, iy);
                if (occupancy[i] <= .01)
                {
                    continue;
                }

                var localReduction = Math.Min(thickness[i] * .42, force * plasticity * kernel * .060);
                thickness[i] -= localReduction;
                thicknessBias[i] -= localReduction;
                removedByColumn[ix] += localReduction * occupancy[i];
                damage[i] = Math.Clamp(damage[i] + addedDamage * kernel, 0, 1);
                finish[i] = Math.Max(0, finish[i] - .025 * kernel);
            }
        }

        for (var ix = Math.Max(0, centerX - radiusX); ix <= Math.Min(ColumnCount - 1, centerX + radiusX); ix++)
        {
            if (removedByColumn[ix] > 0)
            {
                RedistributeColumn(ix, centerY, removedByColumn[ix], radiusY + 3);
            }
        }
        if (guide is not null)
        {
            ApplyFormationGuide(guide);
        }
        SmoothFormationColumns();
        RebuildFromFormation();
        ConserveMass(massBefore);
        RecalculateMetrics();
        Revision++;
    }

    public void AddDamage(double amount)
    {
        amount = Math.Max(0, amount);
        for (var i = 0; i < damage.Length; i++)
        {
            if (occupancy[i] > .01)
            {
                damage[i] = Math.Clamp(damage[i] + amount * .01, 0, 1);
            }
        }

        RecalculateMetrics();
        Revision++;
    }

    public IReadOnlyList<ShapeCellSnapshot> SnapshotCells()
    {
        var cells = new List<ShapeCellSnapshot>(occupancy.Length);
        for (var x = 0; x < ColumnCount; x++)
        {
            for (var y = 0; y < RowCount; y++)
            {
                var i = Index(x, y);
                if (occupancy[i] > .01 || targetOccupancy[i] > .01)
                {
                    cells.Add(new ShapeCellSnapshot(x, y, occupancy[i], thickness[i], damage[i], finish[i], formation[i]));
                }
            }
        }

        return cells;
    }

    private void RedistributeColumn(int column, int centerY, double amount, int radiusY)
    {
        if (amount <= 0)
        {
            return;
        }

        var candidates = new List<(int Index, double Weight)>();
        for (var iy = Math.Max(0, centerY - radiusY); iy <= Math.Min(RowCount - 1, centerY + radiusY); iy++)
        {
            var i = Index(column, iy);
            var dy = iy - centerY;
            var weight = Math.Exp(-.22 * dy * dy);
            if ((occupancy[i] > .02 || targetOccupancy[i] > .05) && thickness[i] < Math.Max(.20, targetThickness[i] * 1.65))
            {
                candidates.Add((i, weight));
            }
        }

        var total = candidates.Sum(item => item.Weight);
        if (total <= 0)
        {
            return;
        }

        foreach (var candidate in candidates)
        {
            var occupancyFactor = Math.Max(.12, occupancy[candidate.Index]);
            var addition = amount * candidate.Weight / total / occupancyFactor;
            thicknessBias[candidate.Index] += addition;
            thickness[candidate.Index] += addition;
        }
    }

    private void BuildBillet(ForgeBilletDefinition billet)
    {
        var targetMass = targetOccupancy.Zip(targetThickness).Sum(pair => pair.First * pair.Second);
        var occupiedCells = 0;
        for (var x = 0; x < ColumnCount; x++)
        {
            var u = x / (double)(ColumnCount - 1);
            if (u < billet.Start || u > billet.End) continue;
            var (lower, upper) = BilletBoundsAt(billet, u);
            for (var y = 0; y < RowCount; y++)
            {
                var signed = y / (double)(RowCount - 1) - .5;
                if (signed < lower || signed > upper) continue;
                initialOccupancy[Index(x, y)] = 1;
                occupiedCells++;
            }
        }

        var billetThickness = targetMass * billet.MassScale / Math.Max(1, occupiedCells);
        for (var i = 0; i < occupancy.Length; i++)
        {
            occupancy[i] = initialOccupancy[i];
            initialThickness[i] = initialOccupancy[i] > .01 ? billetThickness : 0;
            thickness[i] = initialThickness[i];
        }
    }

    private void ApplyFormationGuide(ForgeFormationGuide guide)
    {
        var targetProgress = Math.Clamp(guide.Progress, 0, 1) * (guide.IsCorrection ? .98 : .92);
        var feather = guide.IsCorrection ? .10 : .045;
        for (var x = 0; x < ColumnCount; x++)
        {
            var u = x / (double)(ColumnCount - 1);
            var distance = u < guide.Start ? guide.Start - u : u > guide.End ? u - guide.End : 0;
            if (distance > feather) continue;
            var weight = distance <= 0 ? 1 : 1 - SmoothStep(0, feather, distance);
            for (var y = 0; y < RowCount; y++)
            {
                var i = Index(x, y);
                if (initialOccupancy[i] <= .01 && targetOccupancy[i] <= .01) continue;
                formation[i] = Math.Max(formation[i], targetProgress * weight);
                if (damage[i] < .08)
                {
                    thicknessBias[i] *= 1 - .16 * weight;
                }
            }
        }
    }

    private void RebuildFromFormation()
    {
        for (var i = 0; i < occupancy.Length; i++)
        {
            var blend = SmoothStep(0, .92, formation[i]);
            occupancy[i] = Lerp(initialOccupancy[i], targetOccupancy[i], blend);
            var baseThickness = Lerp(initialThickness[i], targetThickness[i], blend);
            thickness[i] = occupancy[i] > .005 ? Math.Max(.008, baseThickness + thicknessBias[i]) : 0;
        }
    }

    private void SmoothFormationColumns()
    {
        var columnValues = new double[ColumnCount];
        var smoothed = new double[ColumnCount];
        for (var x = 0; x < ColumnCount; x++)
        {
            var maximum = 0d;
            for (var y = 0; y < RowCount; y++)
            {
                maximum = Math.Max(maximum, formation[Index(x, y)]);
            }

            columnValues[x] = maximum;
        }

        for (var x = 0; x < ColumnCount; x++)
        {
            var left = columnValues[Math.Max(0, x - 1)];
            var center = columnValues[x];
            var right = columnValues[Math.Min(ColumnCount - 1, x + 1)];
            smoothed[x] = Math.Clamp(left * .18 + center * .64 + right * .18, 0, 1);
        }

        for (var x = 0; x < ColumnCount; x++)
        for (var y = 0; y < RowCount; y++)
        {
            formation[Index(x, y)] = smoothed[x];
        }
    }

    private void ConserveMass(double expectedMass)
    {
        var actual = CurrentMass();
        var difference = expectedMass - actual;
        if (Math.Abs(difference) <= expectedMass * .001) return;
        var totalWeight = 0d;
        for (var i = 0; i < occupancy.Length; i++)
        {
            if (occupancy[i] > .05) totalWeight += occupancy[i];
        }
        if (totalWeight <= 0) return;

        var adjustment = difference / totalWeight;
        for (var i = 0; i < occupancy.Length; i++)
        {
            if (occupancy[i] <= .05) continue;
            var next = Math.Max(.008, thickness[i] + adjustment);
            thicknessBias[i] += next - thickness[i];
            thickness[i] = next;
        }
    }

    private double CurrentMass()
    {
        var mass = 0d;
        for (var i = 0; i < occupancy.Length; i++) mass += occupancy[i] * thickness[i];
        return mass;
    }

    private static (double Lower, double Upper) BilletBoundsAt(ForgeBilletDefinition billet, double u)
    {
        var outline = billet.Outline;
        if (u <= outline[0].U) return (outline[0].Lower, outline[0].Upper);
        for (var i = 1; i < outline.Count; i++)
        {
            if (u > outline[i].U) continue;
            var previous = outline[i - 1];
            var next = outline[i];
            var amount = SmoothStep(previous.U, next.U, u);
            return (Lerp(previous.Lower, next.Lower, amount), Lerp(previous.Upper, next.Upper, amount));
        }
        return (outline[^1].Lower, outline[^1].Upper);
    }

    private static ForgeBilletDefinition CreateDefaultBillet(ShapeTemplateDefinition shape)
    {
        var isAxe = shape.Kind.Equals("axe", StringComparison.OrdinalIgnoreCase) ||
                    shape.Kind.Contains("axe", StringComparison.OrdinalIgnoreCase);
        var isShort = shape.Kind.Contains("basilard", StringComparison.OrdinalIgnoreCase) ||
                      shape.Kind.Contains("shortsword", StringComparison.OrdinalIgnoreCase);
        var start = isAxe ? .18 : isShort ? .06 : .04;
        var end = isAxe ? .96 : isShort ? .86 : .90;
        var halfWidth = isAxe ? .16 : isShort ? .090 : .065;
        return new ForgeBilletDefinition(start, end,
        [
            new(start, -halfWidth, halfWidth),
            new(end, -halfWidth, halfWidth)
        ]);
    }

    private void BuildTarget()
    {
        for (var x = 0; x < ColumnCount; x++)
        {
            var u = x / (double)(ColumnCount - 1);
            for (var y = 0; y < RowCount; y++)
            {
                var v = y / (double)(RowCount - 1);
                var i = Index(x, y);
                var shape = ForgeShapeProfileSampler.TargetAt(template, u, v);
                targetOccupancy[i] = shape.Occupancy;
                targetThickness[i] = shape.Thickness;
            }
        }
    }

    private void RecalculateMetrics()
    {
        var outline = 0d;
        var thicknessError = 0d;
        var edge = 0d;
        var feature = 0d;
        var currentMass = 0d;
        var targetMass = 0d;
        var straightness = 0d;
        for (var x = 0; x < ColumnCount; x++)
        {
            var columnCenter = 0d;
            var columnMass = 0d;
            for (var y = 0; y < RowCount; y++)
            {
                var i = Index(x, y);
                var target = targetOccupancy[i];
                var actual = occupancy[i];
                outline += Math.Abs(actual - target);
                thicknessError += Math.Abs(thickness[i] - targetThickness[i]) / Math.Max(.05, targetThickness[i] + .05);
                var edgeWeight = y < 4 || y >= RowCount - 4 ? 1.4 : .45;
                edge += Math.Abs((actual > .05 ? thickness[i] : 0) - targetThickness[i]) * edgeWeight;
                feature += damage[i] * (target > .05 ? 1.25 : .5);
                currentMass += actual * thickness[i];
                targetMass += target * targetThickness[i];
                columnCenter += y * actual;
                columnMass += actual;
            }

            if (columnMass > .01)
            {
                var expected = (RowCount - 1) / 2d;
                straightness += Math.Abs(columnCenter / columnMass - expected) / RowCount;
            }
        }

        var count = ColumnCount * RowCount;
        outline /= count;
        thicknessError /= count;
        edge /= Math.Max(1, count * .75);
        feature /= count;
        straightness /= ColumnCount;
        var normalized = Math.Clamp(
            outline * .30 + Math.Clamp(thicknessError, 0, 1) * .25 + Math.Clamp(edge, 0, 1) * .15 + Math.Clamp(feature, 0, 1) * .20 + Math.Clamp(straightness, 0, 1) * .10,
            0,
            1);
        var structurallySound = damage.Max() < .92 && occupancy.Zip(targetOccupancy).Where(pair => pair.Second > .1).All(pair => pair.First > .03);
        Metrics = new ShapeMetrics(normalized, outline, thicknessError, edge, feature, straightness, structurallySound, currentMass, targetMass);
    }

    private static int Index(int x, int y) => x * RowCount + y;

    private static double SmoothStep(double edge0, double edge1, double value)
    {
        var amount = Math.Clamp((value - edge0) / Math.Max(1e-6, edge1 - edge0), 0, 1);
        return amount * amount * (3 - 2 * amount);
    }

    private static double Lerp(double first, double second, double amount) => first + (second - first) * amount;
}
