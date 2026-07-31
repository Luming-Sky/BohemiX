using BohemiX.Modules.Forge.Models;

namespace BohemiX.Modules.Forge.Rendering;

/// <summary>Short-lived visual interpolation between authoritative lattice revisions.</summary>
internal sealed class ForgeShapeTransition
{
    internal const float Duration = .16f;
    private const int Count = WorkpieceMeshBuilder.Columns * WorkpieceMeshBuilder.Rows;
    private readonly ShapeCellSnapshot[] from = CreateDense();
    private readonly ShapeCellSnapshot[] current = CreateDense();
    private readonly ShapeCellSnapshot[] target = CreateDense();
    private float elapsed = Duration;
    private float impactX = .5f;
    private float impactY = .5f;
    private float impactForce;

    public IReadOnlyList<ShapeCellSnapshot> Cells => current;
    public bool HasPending { get; private set; }
    public bool IsAnimating => elapsed < Duration;

    public void SetImmediate(IReadOnlyList<ShapeCellSnapshot> cells)
    {
        FillDense(cells, current);
        Array.Copy(current, from, Count);
        Array.Copy(current, target, Count);
        elapsed = Duration;
        HasPending = false;
    }

    public void Queue(IReadOnlyList<ShapeCellSnapshot> cells, double x, double y, double force)
    {
        FillDense(cells, target);
        impactX = (float)Math.Clamp(x, 0, 1);
        impactY = (float)Math.Clamp(y, 0, 1);
        impactForce = (float)Math.Clamp(force, .05, 1.4);
        HasPending = true;
    }

    public bool Commit(bool reducedMotion)
    {
        if (!HasPending) return false;
        HasPending = false;
        if (reducedMotion)
        {
            Array.Copy(target, current, Count);
            Array.Copy(target, from, Count);
            elapsed = Duration;
            return true;
        }

        Array.Copy(current, from, Count);
        elapsed = 0;
        return true;
    }

    public bool Update(float seconds)
    {
        if (!IsAnimating) return false;
        elapsed = Math.Min(Duration, elapsed + Math.Max(0, seconds));
        var amount = elapsed / Duration;
        var eased = 1 - MathF.Pow(1 - amount, 3);
        var compression = MathF.Sin(amount * MathF.PI) * impactForce * .055f;
        for (var i = 0; i < Count; i++)
        {
            var a = from[i];
            var b = target[i];
            var u = a.X / (float)(WorkpieceMeshBuilder.Columns - 1);
            var v = a.Y / (float)(WorkpieceMeshBuilder.Rows - 1);
            var dx = (u - impactX) / .12f;
            var dy = (v - impactY) / .18f;
            var kernel = MathF.Exp(-2.2f * (dx * dx + dy * dy));
            current[i] = new ShapeCellSnapshot(
                a.X,
                a.Y,
                Lerp(a.Occupancy, b.Occupancy, eased),
                Math.Max(0, Lerp(a.Thickness, b.Thickness, eased) - compression * kernel),
                Lerp(a.Damage, b.Damage, eased),
                Lerp(a.Finish, b.Finish, eased),
                Lerp(a.Formation, b.Formation, eased));
        }
        return true;
    }

    private static ShapeCellSnapshot[] CreateDense()
    {
        var result = new ShapeCellSnapshot[Count];
        for (var x = 0; x < WorkpieceMeshBuilder.Columns; x++)
        for (var y = 0; y < WorkpieceMeshBuilder.Rows; y++)
        {
            result[x * WorkpieceMeshBuilder.Rows + y] = new ShapeCellSnapshot(x, y, 0, 0, 0, 0, 0);
        }
        return result;
    }

    private static void FillDense(IReadOnlyList<ShapeCellSnapshot> source, ShapeCellSnapshot[] destination)
    {
        for (var i = 0; i < Count; i++)
        {
            var old = destination[i];
            destination[i] = new ShapeCellSnapshot(old.X, old.Y, 0, 0, 0, 0, 0);
        }
        for (var i = 0; i < source.Count; i++)
        {
            var cell = source[i];
            if ((uint)cell.X >= WorkpieceMeshBuilder.Columns || (uint)cell.Y >= WorkpieceMeshBuilder.Rows) continue;
            destination[cell.X * WorkpieceMeshBuilder.Rows + cell.Y] = cell;
        }
    }

    private static double Lerp(double first, double second, float amount) => first + (second - first) * amount;
}
