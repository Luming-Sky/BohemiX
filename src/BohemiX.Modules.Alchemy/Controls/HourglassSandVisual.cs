namespace BohemiX.Modules.Alchemy.Controls;

internal readonly record struct HourglassSandState(
    double Progress,
    double Remaining,
    double SourceSurfaceY,
    double SourceCenterX,
    double SourceHalfWidth,
    double ReceiverSurfaceY,
    double ReceiverCenterX,
    double ReceiverHalfWidth,
    bool StreamVisible);

internal static class HourglassSandVisual
{
    private const double SourceNeckY = 3.2;
    private const double SourceFloorY = 42.2;
    private const double ReceiverFloorY = -46.4;
    private const double ReceiverNeckY = -5.6;

    private static readonly (double Y, double Left, double Right)[] UpperSamples =
    [
        (-46.4, -18.3, 16.4),
        (-44.3, -21.5, 17.7),
        (-42.2, -21.5, 18.8),
        (-38.0, -20.7, 19.0),
        (-33.7, -20.2, 18.5),
        (-29.5, -18.3, 16.6),
        (-25.2, -15.9, 14.2),
        (-21.0, -13.0, 11.6),
        (-16.7, -9.8, 8.4),
        (-12.5, -6.6, 4.9),
        (-8.2, -3.1, 1.7),
        (-5.6, -1.0, 0.1),
    ];

    private static readonly (double Y, double Left, double Right)[] LowerSamples =
    [
        (0.0, -1.8, 0.4),
        (2.1, -4.7, 2.8),
        (6.4, -8.2, 6.5),
        (10.6, -11.1, 9.7),
        (14.9, -14.3, 12.6),
        (19.1, -17.0, 15.3),
        (23.4, -21.2, 17.7),
        (27.6, -20.4, 19.0),
        (31.8, -20.7, 19.6),
        (36.1, -20.2, 18.8),
        (40.3, -18.3, 16.6),
        (42.2, -15.9, 14.0),
    ];

    private static double LowerCapacity { get; } = IntegrateArea(LowerSamples, SourceNeckY, SourceFloorY);

    internal static double TransferableArea { get; } = Math.Min(
        LowerCapacity,
        IntegrateArea(UpperSamples, ReceiverFloorY, ReceiverNeckY));

    internal static double SettledSurfaceY { get; } = FindSurfaceForArea(
        LowerSamples,
        SourceNeckY,
        SourceFloorY,
        LowerCapacity - TransferableArea);

    public static HourglassSandState Calculate(double progress)
    {
        progress = Math.Clamp(progress, 0, 1);
        var remaining = 1 - progress;
        // Convert progress to physical cross-sectional area, then invert that area back
        // into a surface height in each differently-shaped chamber. This keeps the total
        // visible sand volume constant even though the two painted bulbs are asymmetric.
        var sourceSurfaceY = FindSurfaceForArea(
            LowerSamples,
            SourceNeckY,
            SourceFloorY,
            TransferableArea * remaining);
        var receiverSurfaceY = FindSurfaceForArea(
            UpperSamples,
            ReceiverFloorY,
            ReceiverNeckY,
            TransferableArea * progress);
        var sourceBounds = InnerBounds(sourceSurfaceY);
        var receiverBounds = InnerBounds(receiverSurfaceY);
        return new HourglassSandState(
            progress,
            remaining,
            sourceSurfaceY,
            sourceBounds.Center,
            sourceBounds.HalfWidth,
            receiverSurfaceY,
            receiverBounds.Center,
            receiverBounds.HalfWidth,
            progress < 0.998);
    }

    public static double InnerHalfWidth(double chamberY) => InnerBounds(chamberY).HalfWidth;

    public static (double Left, double Right, double Center, double HalfWidth) InnerBounds(double chamberY)
    {
        // Pixel samples from image2_hourglass.png after applying the exact DrawRegion
        // source/destination transform. Left and right are deliberately independent:
        // the hand-painted glass is visibly asymmetric and its lower chamber ends at
        // y=42.2, not at the mirrored upper-chamber depth.
        ReadOnlySpan<(double Y, double Left, double Right)> samples = chamberY < 0
            ? UpperSamples
            : LowerSamples;

        if (chamberY <= samples[0].Y)
        {
            return MakeBounds(samples[0].Left, samples[0].Right);
        }

        for (var index = 1; index < samples.Length; index++)
        {
            if (chamberY > samples[index].Y)
            {
                continue;
            }

            var previous = samples[index - 1];
            var next = samples[index];
            var amount = (chamberY - previous.Y) / (next.Y - previous.Y);
            return MakeBounds(
                Lerp(previous.Left, next.Left, amount),
                Lerp(previous.Right, next.Right, amount));
        }

        var last = samples[^1];
        return MakeBounds(last.Left, last.Right);
    }

    internal static double SourceAreaAt(double surfaceY) =>
        IntegrateArea(LowerSamples, SourceNeckY, Math.Clamp(surfaceY, SourceNeckY, SourceFloorY));

    internal static double ReceiverAreaAt(double surfaceY) =>
        IntegrateArea(UpperSamples, ReceiverFloorY, Math.Clamp(surfaceY, ReceiverFloorY, ReceiverNeckY));

    internal static double SettledAreaAt(double surfaceY) =>
        IntegrateArea(LowerSamples, Math.Clamp(surfaceY, SourceNeckY, SourceFloorY), SourceFloorY);

    private static double FindSurfaceForArea(
        (double Y, double Left, double Right)[] samples,
        double startY,
        double endY,
        double targetArea)
    {
        targetArea = Math.Clamp(targetArea, 0, IntegrateArea(samples, startY, endY));
        var low = startY;
        var high = endY;
        for (var iteration = 0; iteration < 36; iteration++)
        {
            var middle = (low + high) * 0.5;
            if (IntegrateArea(samples, startY, middle) < targetArea)
            {
                low = middle;
            }
            else
            {
                high = middle;
            }
        }

        return (low + high) * 0.5;
    }

    private static double IntegrateArea(
        (double Y, double Left, double Right)[] samples,
        double startY,
        double endY)
    {
        if (endY <= startY)
        {
            return 0;
        }

        var area = 0.0;
        var segmentStart = startY;
        while (segmentStart < endY - 0.000001)
        {
            var segmentEnd = endY;
            for (var index = 1; index < samples.Length; index++)
            {
                if (samples[index].Y > segmentStart + 0.000001)
                {
                    segmentEnd = Math.Min(segmentEnd, samples[index].Y);
                    break;
                }
            }

            var startWidth = WidthAt(samples, segmentStart);
            var endWidth = WidthAt(samples, segmentEnd);
            area += (startWidth + endWidth) * 0.5 * (segmentEnd - segmentStart);
            segmentStart = segmentEnd;
        }

        return area;
    }

    private static double WidthAt((double Y, double Left, double Right)[] samples, double y)
    {
        if (y <= samples[0].Y)
        {
            return samples[0].Right - samples[0].Left;
        }

        for (var index = 1; index < samples.Length; index++)
        {
            if (y > samples[index].Y)
            {
                continue;
            }

            var previous = samples[index - 1];
            var next = samples[index];
            var amount = (y - previous.Y) / (next.Y - previous.Y);
            var left = Lerp(previous.Left, next.Left, amount);
            var right = Lerp(previous.Right, next.Right, amount);
            return right - left;
        }

        var last = samples[^1];
        return last.Right - last.Left;
    }

    private static (double Left, double Right, double Center, double HalfWidth) MakeBounds(double left, double right) =>
        (left, right, (left + right) * 0.5, (right - left) * 0.5);

    private static double Lerp(double from, double to, double amount) => from + ((to - from) * Math.Clamp(amount, 0, 1));
}
