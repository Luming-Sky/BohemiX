using System.Numerics;
using BohemiX.Modules.Forge.Models;

namespace BohemiX.Modules.Forge.Rendering;

public enum ForgeStationId
{
    Hearth,
    Bellows,
    Anvil,
    Hammer,
    WaterVat,
    OilVat,
    Grinder
}

public readonly record struct ForgeStationPlacement(
    ForgeStationId Id,
    Matrix4x4 Transform,
    Vector3 BoundsMin,
    Vector3 BoundsMax);

public static class ForgeWorkbenchLayout
{
    public static readonly Matrix4x4 WorkbenchTransform = Matrix4x4.CreateTranslation(0, -1.08f, 0);
    // Authored GLB wheel centre and dressed-stone radius. Keeping these beside the
    // station transform prevents animation and workpiece contact from drifting when
    // the procedural fallback geometry changes.
    internal static readonly Vector3 GrinderWheelPivotLocal = new(0, .46f, 0);
    internal const float GrinderWheelRadiusLocal = .645f;

    public static readonly ForgeStationPlacement Hearth = new(
        ForgeStationId.Hearth,
        Matrix4x4.CreateScale(.72f) * Matrix4x4.CreateTranslation(-3.05f, -.98f, -.28f),
        new Vector3(-4.08f, -1.12f, -1.00f), new Vector3(-2.02f, .34f, .44f));

    public static readonly ForgeStationPlacement Bellows = new(
        ForgeStationId.Bellows,
        // The authored nozzle points along local +X. Aim that axis through the
        // furnace mouth and raise it to the coal-bed air channel.
        Matrix4x4.CreateScale(.72f) * Matrix4x4.CreateRotationY(.50f) * Matrix4x4.CreateTranslation(-4.15f, -.86f, .68f),
        new Vector3(-4.90f, -1.05f, -.10f), new Vector3(-2.98f, -.32f, 1.40f));

    public static readonly ForgeStationPlacement Anvil = new(
        ForgeStationId.Anvil,
        Matrix4x4.CreateScale(.72f) * Matrix4x4.CreateTranslation(-.10f, -.01f, .10f),
        new Vector3(-1.70f, -1.12f, -.58f), new Vector3(1.05f, .48f, .82f));

    public static readonly ForgeStationPlacement Hammer = new(
        ForgeStationId.Hammer,
        Matrix4x4.CreateScale(.42f) * Matrix4x4.CreateRotationZ(-.72f) * Matrix4x4.CreateRotationY(-.15f) * Matrix4x4.CreateTranslation(.90f, -.66f, 1.10f),
        new Vector3(.54f, -1.11f, .94f), new Vector3(1.43f, -.34f, 1.34f));

    public static readonly ForgeStationPlacement WaterVat = new(
        ForgeStationId.WaterVat,
        Matrix4x4.CreateScale(.96f) * Matrix4x4.CreateTranslation(2.12f, -.62f, .30f),
        new Vector3(1.50f, -1.16f, -.34f), new Vector3(2.76f, .02f, .94f));

    public static readonly ForgeStationPlacement OilVat = new(
        ForgeStationId.OilVat,
        Matrix4x4.CreateScale(.88f) * Matrix4x4.CreateTranslation(3.35f, -.66f, .18f),
        new Vector3(2.78f, -1.16f, -.40f), new Vector3(3.93f, -.02f, .78f));

    public static readonly ForgeStationPlacement Grinder = new(
        ForgeStationId.Grinder,
        Matrix4x4.CreateScale(.88f) * Matrix4x4.CreateRotationY(-.18f) * Matrix4x4.CreateTranslation(2.72f, -.55f, -1.32f),
        new Vector3(1.95f, -1.16f, -2.08f), new Vector3(3.55f, .42f, -.48f));

    public static IReadOnlyList<ForgeStationPlacement> Stations { get; } =
    [Hearth, Bellows, Anvil, Hammer, WaterVat, OilVat, Grinder];

    public static bool TryHit(ForgeRay ray, out ForgeStationId station)
    {
        return TryHitCore(ray, null, false, out station);
    }

    public static bool TryHitInteractive(
        ForgeRay ray,
        ForgeStateId state,
        bool canProceed,
        out ForgeStationId station)
    {
        return TryHitCore(ray, state, canProceed, out station);
    }

    private static bool TryHitCore(
        ForgeRay ray,
        ForgeStateId? state,
        bool canProceed,
        out ForgeStationId station)
    {
        station = default;
        var nearest = float.MaxValue;
        var found = false;
        foreach (var placement in Stations)
        {
            if (state is { } current &&
                !CanActivate(current, canProceed, placement.Id) &&
                !(current == ForgeStateId.Quenching && placement.Id is ForgeStationId.WaterVat or ForgeStationId.OilVat) &&
                !(current == ForgeStateId.Grinding && placement.Id == ForgeStationId.Grinder))
            {
                continue;
            }

            if (!ForgeRaycaster.TryHitAabb(ray, placement.BoundsMin, placement.BoundsMax, out var distance) || distance >= nearest) continue;
            nearest = distance;
            station = placement.Id;
            found = true;
        }
        return found;
    }

    public static bool CanActivate(ForgeStateId state, bool canProceed, ForgeStationId station) => (state, station) switch
    {
        (ForgeStateId.Heating or ForgeStateId.Reheat, ForgeStationId.Bellows) => true,
        (ForgeStateId.Heating or ForgeStateId.Reheat, ForgeStationId.Anvil) => canProceed,
        (ForgeStateId.Hammering, ForgeStationId.Hearth or ForgeStationId.Bellows) => true,
        (ForgeStateId.Hammering, ForgeStationId.WaterVat or ForgeStationId.OilVat) => canProceed,
        (ForgeStateId.Grinding, ForgeStationId.Grinder) => true,
        _ => false
    };

    public static bool CanStartGesture(
        ForgeStateId state,
        ForgeStationId? station,
        bool hitsWorkpiece,
        QuenchMedium? activeQuenchMedium = null) => state switch
    {
        ForgeStateId.Heating or ForgeStateId.Reheat => station == ForgeStationId.Bellows,
        ForgeStateId.Hammering => hitsWorkpiece,
        ForgeStateId.Quenching => hitsWorkpiece || IsActiveQuenchStation(station, activeQuenchMedium),
        ForgeStateId.Grinding => station == ForgeStationId.Grinder,
        _ => false
    };

    public static bool IsActiveQuenchStation(ForgeStationId? station, QuenchMedium? medium) => medium switch
    {
        QuenchMedium.Water => station == ForgeStationId.WaterVat,
        QuenchMedium.Oil => station == ForgeStationId.OilVat,
        _ => false
    };
}
