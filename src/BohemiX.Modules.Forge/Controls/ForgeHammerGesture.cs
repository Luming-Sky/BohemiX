using System.Numerics;

namespace BohemiX.Modules.Forge.Controls;

public readonly record struct ForgeHammerGestureResult(
    bool ShouldStrike,
    Vector2 Lattice,
    double Force);

/// <summary>
/// Pure gesture policy shared by the Avalonia adapter and tests. A click produces a light
/// corrective blow; pulling upward keeps the original variable-force physical gesture.
/// </summary>
public static class ForgeHammerGesture
{
    public const float PullThreshold = 14f;
    public const float ClickTolerance = 10f;
    public const double LightStrikeForce = .28;
    public const double ChargeDurationSeconds = 1.15;

    public static double ForceForHold(double heldSeconds) =>
        Math.Clamp(.18 + Math.Max(0, heldSeconds) / ChargeDurationSeconds * .82, .18, 1);

    public static ForgeHammerGestureResult ResolveHold(
        bool targetAvailable,
        Vector2 lattice,
        double heldSeconds) => targetAvailable
            ? new ForgeHammerGestureResult(true, lattice, ForceForHold(heldSeconds))
            : default;

    public static ForgeHammerGestureResult Resolve(
        bool startedOnWorkpiece,
        Vector2 lattice,
        Vector2 start,
        Vector2 end)
    {
        if (!startedOnWorkpiece)
        {
            return default;
        }

        var movement = Vector2.Distance(start, end);
        var pull = start.Y - end.Y;
        if (movement <= ClickTolerance)
        {
            return new ForgeHammerGestureResult(true, lattice, LightStrikeForce);
        }

        if (pull < PullThreshold)
        {
            return default;
        }

        return new ForgeHammerGestureResult(
            true,
            lattice,
            Math.Clamp(pull / 128d, .15, 1));
    }
}
