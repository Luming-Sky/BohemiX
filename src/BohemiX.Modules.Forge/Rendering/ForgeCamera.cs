using System.Numerics;
using BohemiX.Modules.Forge.Models;

namespace BohemiX.Modules.Forge.Rendering;

public readonly record struct CameraPose(Vector3 Position, Vector3 Target, float FieldOfView)
{
    public static CameraPose Lerp(CameraPose from, CameraPose to, float amount) => new(
        Vector3.Lerp(from.Position, to.Position, amount),
        Vector3.Lerp(from.Target, to.Target, amount),
        from.FieldOfView + (to.FieldOfView - from.FieldOfView) * amount);
}

public sealed class ForgeCameraRig
{
    private static readonly CameraPose WaterQuenchAnchor = new(
        new Vector3(2.12f, 2.10f, 4.75f),
        new Vector3(2.12f, -.10f, .30f),
        .52f);
    private static readonly CameraPose OilQuenchAnchor = new(
        new Vector3(3.35f, 2.10f, 4.65f),
        new Vector3(3.35f, -.10f, .18f),
        .52f);
    private static readonly CameraPose TransferAnchor = new(
        new Vector3(.35f, 3.75f, 10.25f),
        new Vector3(.35f, -.22f, .05f),
        .66f);
    private static readonly CameraPose PostQuenchDisplayAnchor = new(
        new Vector3(.10f, 1.62f, 5.05f),
        new Vector3(.05f, .96f, .42f),
        .50f);
    // First-person work stance across the wheel axle. The wider field of view keeps
    // the wheel, complete weapon and the smith's grip area visible at once instead of
    // turning grinding into a close-up inspection shot.
    private static readonly CameraPose ActiveGrindingAnchor = new(
        new Vector3(6.05f, 3.38f, -.70f),
        new Vector3(2.82f, .08f, -1.30f),
        .56f);
    private static readonly IReadOnlyDictionary<ForgeStateId, CameraPose> Anchors = new Dictionary<ForgeStateId, CameraPose>
    {
        [ForgeStateId.RecipeSelect] = new(new(0, 3.75f, 10.6f), new(0, -.28f, 0), .66f),
        [ForgeStateId.MaterialSelect] = new(new(0, 3.75f, 10.6f), new(0, -.28f, 0), .66f),
        // Furnace close-up: the camera is centered on the hearth and looks straight
        // through the front arch instead of approaching from the side of the bench.
        [ForgeStateId.Heating] = new(new(-3.05f, .86f, 5.45f), new(-3.05f, -.31f, .02f), .56f),
        [ForgeStateId.Reheat] = new(new(-3.05f, .86f, 5.45f), new(-3.05f, -.31f, .02f), .56f),
        // First-person smithing view: a steep diagonal look across the anvil keeps
        // the billet, hammer and striking surface dominant, matching a real work stance.
        [ForgeStateId.Hammering] = new(new(2.30f, 5.90f, 2.80f), new(-.10f, .38f, .12f), .58f),
        [ForgeStateId.RotateWorkpiece] = new(new(2.30f, 5.90f, 2.80f), new(-.10f, .38f, .12f), .58f),
        [ForgeStateId.Quenching] = WaterQuenchAnchor,
        [ForgeStateId.Grinding] = PostQuenchDisplayAnchor,
        [ForgeStateId.Inspection] = new(new(.10f, 1.62f, 5.05f), new(.05f, .96f, .42f), .50f),
        [ForgeStateId.Result] = new(new(.10f, 1.62f, 5.05f), new(.05f, .96f, .42f), .50f)
    };

    private CameraPose from = Anchors[ForgeStateId.RecipeSelect];
    private CameraPose target = Anchors[ForgeStateId.RecipeSelect];
    private float transition = 1;
    private float yaw;
    private float pitch;
    private float zoom = 1;
    private float shake;
    private QuenchMedium quenchMedium = QuenchMedium.Water;
    private bool transferOverview;
    private bool grindingEngaged;

    public ForgeStateId State { get; private set; } = ForgeStateId.RecipeSelect;
    public CameraPose Current { get; private set; } = Anchors[ForgeStateId.RecipeSelect];

    public void SetState(ForgeStateId state, bool reducedMotion)
    {
        if (state == State) return;
        var enteringInspection = IsInspectionState(state) && !IsInspectionState(State);
        from = Current;
        transferOverview = false;
        target = AnchorFor(state, quenchMedium, grindingEngaged);
        State = state;
        transition = 0;
        if (enteringInspection)
        {
            yaw = pitch = 0;
            zoom = 1;
        }
        if (reducedMotion) transition = .68f;
    }

    public void SetGrindingEngaged(bool engaged, bool reducedMotion)
    {
        grindingEngaged = engaged;
        if (State != ForgeStateId.Grinding)
        {
            return;
        }

        var next = AnchorFor(ForgeStateId.Grinding, quenchMedium, engaged);
        if (next == target)
        {
            return;
        }

        from = Current;
        target = next;
        transition = reducedMotion ? .68f : 0;
    }

    public void SetTransferOverview(bool active, bool reducedMotion)
    {
        if (State != ForgeStateId.Hammering || active == transferOverview)
        {
            return;
        }

        transferOverview = active;
        from = Current;
        target = active ? TransferAnchor : Anchors[ForgeStateId.Hammering];
        transition = reducedMotion ? .68f : 0;
    }

    public void SetQuenchMedium(QuenchMedium medium, bool reducedMotion)
    {
        if (medium == quenchMedium && State != ForgeStateId.Quenching)
        {
            return;
        }

        quenchMedium = medium;
        if (State != ForgeStateId.Quenching)
        {
            return;
        }

        var next = AnchorFor(ForgeStateId.Quenching, medium, grindingEngaged);
        if (next == target)
        {
            return;
        }

        from = Current;
        target = next;
        transition = reducedMotion ? .68f : 0;
    }

    public void AddOrbit(float yawDelta, float pitchDelta)
    {
        yaw = Math.Clamp(yaw + yawDelta, Degrees(-18), Degrees(18));
        pitch = Math.Clamp(pitch + pitchDelta, Degrees(-10), Degrees(10));
    }

    public void AddZoom(float delta) => zoom = Math.Clamp(zoom + delta, .85f, 1.15f);
    public void Impact(float intensity, bool reducedMotion) => shake = reducedMotion ? 0 : Math.Clamp(intensity, 0, 1) * .055f;

    public CameraPose Update(float seconds, bool reducedMotion, float time)
    {
        var duration = reducedMotion ? .10f : .32f;
        transition = Math.Min(1, transition + seconds / duration);
        var eased = transition * transition * (3 - 2 * transition);
        var pose = CameraPose.Lerp(from, target, eased);
        var offset = pose.Position - pose.Target;
        var orbit = Matrix4x4.CreateFromYawPitchRoll(yaw, pitch, 0);
        offset = Vector3.Transform(offset, orbit) * zoom;
        if (shake > .001f)
        {
            offset += new Vector3(MathF.Sin(time * 97), MathF.Sin(time * 81) * .45f, 0) * shake;
            shake = Math.Max(0, shake - seconds / .08f * .055f);
        }
        Current = pose with { Position = pose.Target + offset };
        return Current;
    }

    public static CameraPose GetAnchor(ForgeStateId state) => Anchors[state];
    public static CameraPose GetQuenchAnchor(QuenchMedium medium) =>
        medium == QuenchMedium.Oil ? OilQuenchAnchor : WaterQuenchAnchor;
    public static CameraPose GetTransferAnchor() => TransferAnchor;
    public static CameraPose GetGrindingAnchor(bool engaged) =>
        engaged ? ActiveGrindingAnchor : PostQuenchDisplayAnchor;

    private static CameraPose AnchorFor(ForgeStateId state, QuenchMedium medium, bool engaged) => state switch
    {
        ForgeStateId.Quenching => GetQuenchAnchor(medium),
        ForgeStateId.Grinding => GetGrindingAnchor(engaged),
        _ => Anchors[state]
    };
    private static bool IsInspectionState(ForgeStateId state) =>
        state is ForgeStateId.Inspection or ForgeStateId.Result;
    private static float Degrees(float value) => value * MathF.PI / 180f;
}

public readonly record struct ForgeRay(Vector3 Origin, Vector3 Direction);

public static class ForgeRaycaster
{
    public static ForgeRay CreateRay(Vector2 pixel, Vector2 viewport, Matrix4x4 view, Matrix4x4 projection)
    {
        var x = pixel.X / Math.Max(1, viewport.X) * 2 - 1;
        var y = 1 - pixel.Y / Math.Max(1, viewport.Y) * 2;
        if (!Matrix4x4.Invert(view * projection, out var inverse))
        {
            return new ForgeRay(Vector3.Zero, -Vector3.UnitZ);
        }
        var near = Vector4.Transform(new Vector4(x, y, 0, 1), inverse);
        var far = Vector4.Transform(new Vector4(x, y, 1, 1), inverse);
        near /= near.W;
        far /= far.W;
        var origin = new Vector3(near.X, near.Y, near.Z);
        var direction = Vector3.Normalize(new Vector3(far.X - near.X, far.Y - near.Y, far.Z - near.Z));
        return new ForgeRay(origin, direction);
    }

    public static bool TryHitWorkpiece(ForgeRay ray, Matrix4x4 world, out Vector2 lattice, out Vector3 point, string? recipeId = null)
    {
        if (!TryProjectWorkpiecePlane(ray, world, 0, recipeId, out lattice, out point)) return false;
        return lattice.X is >= 0 and <= 1 && lattice.Y is >= 0 and <= 1;
    }

    public static bool TryProjectWorkpiecePlane(ForgeRay ray, Matrix4x4 world, out Vector2 lattice, out Vector3 point)
        => TryProjectWorkpiecePlane(ray, world, 0, null, out lattice, out point);

    private static bool TryProjectWorkpiecePlane(
        ForgeRay ray,
        Matrix4x4 world,
        float planeHeight,
        string? recipeId,
        out Vector2 lattice,
        out Vector3 point)
    {
        lattice = default;
        point = default;
        if (!Matrix4x4.Invert(world, out var inverse)) return false;
        var localOrigin = Vector3.Transform(ray.Origin, inverse);
        var localDirection = Vector3.Normalize(Vector3.TransformNormal(ray.Direction, inverse));
        if (Math.Abs(localDirection.Y) < 1e-5f) return false;
        var t = (planeHeight - localOrigin.Y) / localDirection.Y;
        if (t < 0) return false;
        var local = localOrigin + localDirection * t;
        var profile = WorkpieceMeshBuilder.ProfileFor(recipeId);
        var u = local.X / profile.Length + .5f;
        var v = local.Z / profile.Width + .5f;
        lattice = new Vector2(u, v);
        point = Vector3.Transform(local, world);
        return true;
    }

    public static bool TryHitOccupiedWorkpiece(
        ForgeRay ray,
        Matrix4x4 world,
        IReadOnlyList<ShapeCellSnapshot> cells,
        bool flipped,
        out Vector2 lattice,
        out Vector3 point,
        string? recipeId = null,
        ShapeTemplateDefinition? shape = null)
    {
        lattice = default;
        point = default;
        if (!TryProjectWorkpiecePlane(ray, world, 0, recipeId, out var localLattice, out point))
        {
            return false;
        }

        if (localLattice.X is < -.14f or > 1.14f || localLattice.Y is < -.38f or > 1.38f)
        {
            return false;
        }

        var clamped = Vector2.Clamp(localLattice, Vector2.Zero, Vector2.One);
        if (!WorkpieceMeshBuilder.TrySnapToOccupied(
                cells,
                clamped,
                out var firstOccupied,
                searchRadiusX: 6,
                searchRadiusY: 6))
        {
            return false;
        }

        var surfaceHeight = WorkpieceMeshBuilder.SurfaceHeightAt(cells, firstOccupied, recipeId, shape);
        if (!TryProjectWorkpiecePlane(ray, world, surfaceHeight, recipeId, out localLattice, out point))
        {
            return false;
        }

        var inside = localLattice.X is >= 0 and <= 1 && localLattice.Y is >= 0 and <= 1;
        clamped = Vector2.Clamp(localLattice, Vector2.Zero, Vector2.One);
        var snapped = inside
            ? WorkpieceMeshBuilder.TrySnapToOccupied(cells, clamped, out var occupiedLattice, searchRadius: 3)
            : WorkpieceMeshBuilder.TrySnapToOccupied(cells, clamped, out occupiedLattice, searchRadiusX: 6, searchRadiusY: 8);
        if (!snapped)
        {
            return false;
        }

        lattice = flipped
            ? new Vector2(occupiedLattice.X, 1 - occupiedLattice.Y)
            : occupiedLattice;
        return true;
    }

    public static bool TryHitAabb(ForgeRay ray, Vector3 min, Vector3 max, out float distance)
    {
        distance = 0;
        var far = float.MaxValue;
        for (var axis = 0; axis < 3; axis++)
        {
            var origin = axis == 0 ? ray.Origin.X : axis == 1 ? ray.Origin.Y : ray.Origin.Z;
            var direction = axis == 0 ? ray.Direction.X : axis == 1 ? ray.Direction.Y : ray.Direction.Z;
            var lo = axis == 0 ? min.X : axis == 1 ? min.Y : min.Z;
            var hi = axis == 0 ? max.X : axis == 1 ? max.Y : max.Z;
            if (Math.Abs(direction) < 1e-6f)
            {
                if (origin < lo || origin > hi) return false;
                continue;
            }
            var t0 = (lo - origin) / direction;
            var t1 = (hi - origin) / direction;
            if (t0 > t1) (t0, t1) = (t1, t0);
            distance = Math.Max(distance, t0);
            far = Math.Min(far, t1);
            if (far < distance) return false;
        }
        return far >= Math.Max(0, distance);
    }
}
