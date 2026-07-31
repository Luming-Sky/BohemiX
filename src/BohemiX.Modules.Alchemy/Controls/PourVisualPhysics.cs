using Avalonia;
using BohemiX.Modules.Alchemy.Models;

namespace BohemiX.Modules.Alchemy.Controls;

internal enum BottleCapPhase
{
    Closed,
    Opening,
    Open
}

internal readonly record struct PourFlowProfile(
    double ProgressPerSecond,
    double ExitSpeed,
    double Gravity,
    double StreamWidth);

internal readonly record struct PourTrajectory(
    Point Mouth,
    Point Impact,
    Vector InitialVelocity,
    double Gravity,
    double FlightTime,
    double Flow,
    double Width,
    bool HitsCauldron)
{
    public Point PositionAt(double normalizedTime)
    {
        var time = Math.Clamp(normalizedTime, 0, 1) * FlightTime;
        return Mouth
            + (InitialVelocity * time)
            + new Vector(0, 0.5 * Gravity * time * time);
    }
}

internal static class PourVisualPhysics
{
    internal const double BottleScaleWhileDragging = 1.08;
    internal const double MinimumPourAngle = 48;
    internal const double FullPourAngle = 94;
    internal const double CauldronInteriorLeft = 684;
    internal const double CauldronInteriorRight = 956;

    public static Point GetBottleMouth(Point center, double rotationDegrees, double scale)
    {
        var radians = rotationDegrees * Math.PI / 180;
        var localMouth = new Vector(0, -49 * scale);
        return center + Rotate(localMouth, radians);
    }

    public static Vector GetBottleOutward(double rotationDegrees)
    {
        var radians = rotationDegrees * Math.PI / 180;
        return Normalize(Rotate(new Vector(0, -1), radians));
    }

    public static PourFlowProfile GetProfile(BaseLiquid liquid) => liquid switch
    {
        BaseLiquid.Water => new PourFlowProfile(0.84, 118, 560, 7.1),
        BaseLiquid.Wine => new PourFlowProfile(0.76, 108, 535, 7.8),
        BaseLiquid.Spirits => new PourFlowProfile(0.96, 132, 585, 5.7),
        BaseLiquid.Oil => new PourFlowProfile(0.54, 82, 430, 9.2),
        _ => new PourFlowProfile(0.78, 110, 540, 7)
    };

    public static double CalculateFlow(
        BaseLiquid liquid,
        double tiltDegrees,
        BottleCapPhase capPhase,
        double capProgress,
        double remainingVolume)
    {
        if (capPhase != BottleCapPhase.Open || capProgress < 0.999 || remainingVolume <= 0.0001)
        {
            return 0;
        }

        var tilt = SmoothStep(MinimumPourAngle, FullPourAngle, tiltDegrees);
        var headPressure = 0.34 + (0.66 * Math.Sqrt(Math.Clamp(remainingVolume, 0, 1)));
        var viscosityResponse = liquid == BaseLiquid.Oil ? tilt * tilt : Math.Sqrt(tilt);
        return Math.Clamp(viscosityResponse * headPressure, 0, 1);
    }

    public static PourTrajectory? CreateTrajectory(
        Point mouth,
        double rotationDegrees,
        BaseLiquid liquid,
        double flow,
        double targetSurfaceY)
    {
        if (flow <= 0.0001)
        {
            return null;
        }

        var profile = GetProfile(liquid);
        var outward = GetBottleOutward(rotationDegrees);
        var initialSpeed = profile.ExitSpeed * (0.62 + (0.38 * flow));
        var velocity = outward * initialSpeed;
        var flightTime = SolvePositiveFlightTime(mouth.Y, velocity.Y, profile.Gravity, targetSurfaceY);
        if (flightTime <= 0 || double.IsNaN(flightTime) || double.IsInfinity(flightTime))
        {
            return null;
        }

        flightTime = Math.Clamp(flightTime, 0.035, 1.2);
        var impact = mouth
            + (velocity * flightTime)
            + new Vector(0, 0.5 * profile.Gravity * flightTime * flightTime);
        var hitsCauldron = mouth.Y < targetSurfaceY - 2
            && impact.X >= CauldronInteriorLeft
            && impact.X <= CauldronInteriorRight;
        var width = profile.StreamWidth * (0.24 + (0.76 * Math.Sqrt(flow)));

        return new PourTrajectory(
            mouth,
            impact,
            velocity,
            profile.Gravity,
            flightTime,
            flow,
            width,
            hitsCauldron);
    }

    public static double ProgressDelta(BaseLiquid liquid, double flow, double elapsedSeconds, bool hitsCauldron)
    {
        if (!hitsCauldron || flow <= 0 || elapsedSeconds <= 0)
        {
            return 0;
        }

        return GetProfile(liquid).ProgressPerSecond * Math.Clamp(flow, 0, 1) * elapsedSeconds;
    }

    private static double SolvePositiveFlightTime(double startY, double velocityY, double gravity, double targetY)
    {
        var discriminant = (velocityY * velocityY) + (2 * gravity * (targetY - startY));
        if (discriminant < 0 || gravity <= 0)
        {
            return -1;
        }

        var root = (-velocityY + Math.Sqrt(discriminant)) / gravity;
        return root > 0 ? root : -1;
    }

    private static Vector Rotate(Vector value, double radians) => new(
        (value.X * Math.Cos(radians)) - (value.Y * Math.Sin(radians)),
        (value.X * Math.Sin(radians)) + (value.Y * Math.Cos(radians)));

    private static Vector Normalize(Vector value)
    {
        var length = Math.Sqrt((value.X * value.X) + (value.Y * value.Y));
        return length <= 0.0001 ? default : value / length;
    }

    private static double SmoothStep(double from, double to, double value)
    {
        var t = Math.Clamp((value - from) / (to - from), 0, 1);
        return t * t * (3 - (2 * t));
    }
}
