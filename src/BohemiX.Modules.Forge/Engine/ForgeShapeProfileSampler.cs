using BohemiX.Modules.Forge.Models;

namespace BohemiX.Modules.Forge.Engine;

public static class ForgeShapeProfileSampler
{
    public static (double Lower, double Upper) BoundsAt(ShapeTemplateDefinition shape, double u)
    {
        u = Math.Clamp(u, 0, 1);
        var outline = shape.Outline;
        if (outline is { Count: >= 2 })
        {
            var first = outline[0];
            if (u <= first.U) return (first.Lower, first.Upper);
            for (var i = 1; i < outline.Count; i++)
            {
                var next = outline[i];
                if (u > next.U) continue;
                var previous = outline[i - 1];
                var amount = SmoothStep(previous.U, next.U, u);
                return (
                    Lerp(previous.Lower, next.Lower, amount),
                    Lerp(previous.Upper, next.Upper, amount));
            }

            var last = outline[^1];
            return (last.Lower, last.Upper);
        }

        return LegacyBounds(shape.Kind, u);
    }

    public static (double Occupancy, double Thickness) TargetAt(ShapeTemplateDefinition shape, double u, double v)
    {
        var signed = v - .5;
        var (lower, upper) = BoundsAt(shape, u);
        var occupied = signed >= lower && signed <= upper ? 1d : 0d;
        if (occupied > 0 && IsInsideEye(shape, u, signed)) occupied = 0;

        var edgeDistance = Math.Min(signed - lower, upper - signed);
        var edgeFactor = Math.Clamp(edgeDistance / Math.Max(.01, shape.EdgeWidth), 0, 1);
        var isAxe = IsAxe(shape);
        var isBasilard = shape.Kind.Contains("basilard", StringComparison.OrdinalIgnoreCase) ||
                         shape.Kind.Equals("shortsword", StringComparison.OrdinalIgnoreCase);
        var crossSection = isAxe
            ? .42 + .58 * edgeFactor
            : isBasilard
                ? .46 + .54 * Math.Pow(edgeFactor, .68)
                : .26 + .74 * Math.Pow(edgeFactor, .92);
        var tipFactor = u > .72
            ? .42 + .58 * (1 - SmoothStep(.72, 1, u))
            : 1;
        if (isAxe) tipFactor = u > .84 ? 1 - SmoothStep(.84, 1, u) * .16 : 1;
        var targetThickness = occupied > 0 ? shape.Thickness * crossSection * tipFactor : 0;
        return (occupied, targetThickness);
    }

    public static bool IsInsideEye(ShapeTemplateDefinition shape, double u, double signedV)
    {
        if (shape.EyeWidth <= 0 || shape.EyeHeight <= 0) return false;
        var x = (u - shape.EyeCenterX) / Math.Max(.001, shape.EyeWidth * .5);
        var y = (signedV - shape.EyeCenterY) / Math.Max(.001, shape.EyeHeight * .5);
        return x * x + y * y < 1;
    }

    private static bool IsAxe(ShapeTemplateDefinition shape) =>
        shape.Kind.Equals("axe", StringComparison.OrdinalIgnoreCase) ||
        shape.Kind.Contains("axe", StringComparison.OrdinalIgnoreCase);

    private static (double Lower, double Upper) LegacyBounds(string kind, double u)
    {
        if (kind.Equals("axe", StringComparison.OrdinalIgnoreCase) ||
            kind.Contains("axe", StringComparison.OrdinalIgnoreCase))
        {
            if (u < .18)
            {
                var t = SmoothStep(0, .18, u);
                return (-.47 + t * .07, .30 + t * .08);
            }
            if (u < .58)
            {
                var t = (u - .18) / .40;
                return (-.40 - Math.Sin(t * Math.PI) * .085, .38 - t * .02);
            }
            if (u < .82)
            {
                var t = SmoothStep(.58, .82, u);
                return (-.40 + t * .21, .36 - t * .14);
            }
            var tail = SmoothStep(.82, 1, u);
            return (-.19 + tail * .05, .22 - tail * .06);
        }

        var isShort = kind.Equals("shortsword", StringComparison.OrdinalIgnoreCase) ||
                      kind.Contains("basilard", StringComparison.OrdinalIgnoreCase);
        var tangEnd = isShort ? .22 : .18;
        var tipStart = isShort ? .70 : .76;
        var tangHalfWidth = isShort ? .065 : .045;
        var bladeProgress = Math.Clamp((u - tangEnd) / Math.Max(.01, tipStart - tangEnd), 0, 1);
        var bladeHalfWidth = isShort
            ? (.36 + Math.Sin(bladeProgress * Math.PI) * .10) * .5
            : (.40 - bladeProgress * .045) * .5;
        var halfWidth = tangHalfWidth + (bladeHalfWidth - tangHalfWidth) *
            SmoothStep(isShort ? .18 : .14, isShort ? .29 : .25, u);
        if (u >= tipStart)
        {
            var tip = 1 - SmoothStep(tipStart, 1, u);
            var tipWidth = isShort ? .009 : .005;
            halfWidth = tipWidth + (bladeHalfWidth - tipWidth) * tip;
        }
        return (-halfWidth, halfWidth);
    }

    private static double SmoothStep(double edge0, double edge1, double value)
    {
        var amount = Math.Clamp((value - edge0) / Math.Max(1e-6, edge1 - edge0), 0, 1);
        return amount * amount * (3 - 2 * amount);
    }

    private static double Lerp(double first, double second, double amount) => first + (second - first) * amount;
}
