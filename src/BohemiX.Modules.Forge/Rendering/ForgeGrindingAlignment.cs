using System.Numerics;

namespace BohemiX.Modules.Forge.Rendering;

internal enum ForgeGrindingOrientation
{
    BladeForward,
    EdgeAcrossWheel
}

internal readonly record struct ForgeGrindingAlignment(
    Vector3 LocalContactPoint,
    Vector3 LocalLengthAxis,
    Vector3 LocalInteriorAxis,
    Vector3 LocalFaceNormal,
    float Scale,
    ForgeGrindingOrientation Orientation,
    Vector3 WheelLocalContactPoint,
    bool UsesAuthoredWheelPose,
    Vector3 WheelLocalRootPosition,
    Quaternion WheelLocalRootRotation)
{
    private const float DefaultScale = .62f;
    private static readonly Vector3 WheelCrownContact = new(0, 1.105f, 0);
    private static readonly Vector3 LongswordWheelContact = new(.04935527f, 1.0996903f, .15f);
    private static readonly Vector3 BasilardWheelContact = new(.02449076f, 1.0944052f, .15f);
    private static readonly Vector3 AuthoredSwordRootPosition = new(
        .1215230003f,
        1.1150300503f,
        .1500000060f);
    private static readonly Quaternion AuthoredSwordRootRotation = Quaternion.Normalize(new Quaternion(
        .0739127845f,
        -.7032331824f,
        .0739127919f,
        .7032331824f));

    public static ForgeGrindingAlignment FromMesh(string? recipeId, MeshAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var normalizedRecipeId = recipeId?.ToLowerInvariant();
        var fallback = Fallback(normalizedRecipeId);
        var edgeVertices = EdgeVertices(asset);
        if (edgeVertices.Count < 3)
        {
            return fallback;
        }

        var contact = normalizedRecipeId == "bearded-axe"
            ? FindAxeContact(edgeVertices, fallback.LocalContactPoint)
            : FindSwordContact(edgeVertices, fallback.LocalContactPoint);
        return IsFinite(contact) ? fallback with { LocalContactPoint = contact } : fallback;
    }

    public static ForgeGrindingAlignment Fallback(string? recipeId) => recipeId?.ToLowerInvariant() switch
    {
        "basilard" => new(
            new Vector3(0, -.16f, 0),
            Vector3.UnitX,
            Vector3.UnitY,
            Vector3.UnitZ,
            DefaultScale,
            ForgeGrindingOrientation.BladeForward,
            BasilardWheelContact,
            true,
            AuthoredSwordRootPosition,
            AuthoredSwordRootRotation),
        // Keep the calibrated axe pose: its handle and long cutting-edge direction
        // run along local Z, its cheek normal is local Y, and the head grows inward
        // along +X. Its accepted top-crown target remains unchanged.
        "bearded-axe" => new(
            new Vector3(-1.275f, .08f, 0),
            Vector3.UnitZ,
            Vector3.UnitX,
            Vector3.UnitY,
            DefaultScale,
            ForgeGrindingOrientation.EdgeAcrossWheel,
            WheelCrownContact,
            false,
            Vector3.Zero,
            Quaternion.Identity),
        _ => new(
            new Vector3(0, -.119f, 0),
            Vector3.UnitX,
            Vector3.UnitY,
            Vector3.UnitZ,
            DefaultScale,
            ForgeGrindingOrientation.BladeForward,
            LongswordWheelContact,
            true,
            AuthoredSwordRootPosition,
            AuthoredSwordRootRotation)
    };

    private static List<Vector3> EdgeVertices(MeshAsset asset)
    {
        var result = new List<Vector3>();
        if (asset.Primitives is null) return result;

        foreach (var primitive in asset.Primitives)
        {
            if (!primitive.MaterialName.Equals(nameof(ForgeMaterialId.WorkpieceEdge), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var end = Math.Min(asset.Mesh.Indices.Length, primitive.FirstIndex + primitive.IndexCount);
            for (var index = Math.Max(0, primitive.FirstIndex); index < end; index++)
            {
                var vertexIndex = asset.Mesh.Indices[index];
                if (vertexIndex < asset.Mesh.Vertices.Length)
                {
                    result.Add(asset.Mesh.Vertices[vertexIndex].Position);
                }
            }
        }

        return result;
    }

    private static Vector3 FindSwordContact(IReadOnlyList<Vector3> vertices, Vector3 fallback)
    {
        Bounds(vertices, out var minimum, out var maximum);
        var span = maximum - minimum;
        var centerX = (minimum.X + maximum.X) * .5f;
        var centerBand = Math.Max(.02f, span.X * .12f);
        var edgeBand = Math.Max(.002f, span.Y * .035f);
        var candidates = vertices.Where(vertex =>
            MathF.Abs(vertex.X - centerX) <= centerBand &&
            vertex.Y <= minimum.Y + edgeBand).ToArray();
        return candidates.Length == 0 ? fallback : Average(candidates);
    }

    private static Vector3 FindAxeContact(IReadOnlyList<Vector3> vertices, Vector3 fallback)
    {
        Bounds(vertices, out var minimum, out var maximum);
        var span = maximum - minimum;
        var centerY = (minimum.Y + maximum.Y) * .5f;
        var centerBand = Math.Max(.03f, span.Y * .18f);
        var edgeBand = Math.Max(.003f, span.X * .045f);
        var candidates = vertices.Where(vertex =>
            vertex.X <= minimum.X + edgeBand &&
            MathF.Abs(vertex.Y - centerY) <= centerBand).ToArray();
        return candidates.Length == 0 ? fallback : Average(candidates);
    }

    private static void Bounds(IReadOnlyList<Vector3> vertices, out Vector3 minimum, out Vector3 maximum)
    {
        minimum = new Vector3(float.MaxValue);
        maximum = new Vector3(float.MinValue);
        foreach (var vertex in vertices)
        {
            minimum = Vector3.Min(minimum, vertex);
            maximum = Vector3.Max(maximum, vertex);
        }
    }

    private static Vector3 Average(IReadOnlyList<Vector3> vertices)
    {
        var sum = Vector3.Zero;
        foreach (var vertex in vertices) sum += vertex;
        return sum / vertices.Count;
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
