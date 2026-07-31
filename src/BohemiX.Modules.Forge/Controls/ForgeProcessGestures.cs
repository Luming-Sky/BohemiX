using System.Numerics;
using BohemiX.Modules.Forge.Rendering;

namespace BohemiX.Modules.Forge.Controls;

public readonly record struct ForgeQuenchGestureResult(bool HasChanged, double Depth, double Speed);
public readonly record struct ForgeGrindingGestureResult(bool HasChanged, double Position, double Speed, double Delta);

public static class ForgeProcessGestures
{
    public const double WorkpieceTransferThreshold = 22;

    public static ForgeStationId? ResolveQuenchTransfer(
        bool startedOnWorkpiece,
        bool canProceed,
        double movement,
        ForgeStationId? station)
    {
        if (!startedOnWorkpiece || !canProceed || movement < WorkpieceTransferThreshold)
        {
            return null;
        }

        return station is ForgeStationId.WaterVat or ForgeStationId.OilVat ? station : null;
    }

    private const double QuenchTravel = 150;
    private const double GrindTravel = 240;

    public static ForgeQuenchGestureResult ResolveQuench(
        Vector2 start,
        Vector2 current,
        float sampleDeltaY,
        double elapsedSeconds)
    {
        var travel = current.Y - start.Y;
        if (Math.Abs(travel) < 2 && Math.Abs(sampleDeltaY) < 1)
        {
            return default;
        }

        var depth = Math.Clamp(travel / QuenchTravel, 0, 1);
        var speed = Math.Clamp(Math.Abs(sampleDeltaY) / QuenchTravel / Math.Max(.001, elapsedSeconds), 0, 1.5);
        return new ForgeQuenchGestureResult(true, depth, speed);
    }

    public static ForgeGrindingGestureResult ResolveGrinding(
        Vector2 start,
        Vector2 current,
        float sampleDeltaX,
        double elapsedSeconds)
    {
        if (Math.Abs(sampleDeltaX) < 1)
        {
            return default;
        }

        var position = Math.Clamp(.5 + (current.X - start.X) / GrindTravel, 0, 1);
        var speed = Math.Clamp(Math.Abs(sampleDeltaX) / GrindTravel / Math.Max(.001, elapsedSeconds), 0, 1.5);
        var delta = Math.Clamp(Math.Abs(sampleDeltaX) / GrindTravel, 0, .16);
        return new ForgeGrindingGestureResult(true, position, speed, delta);
    }
}
