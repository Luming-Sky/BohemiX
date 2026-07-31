using System.Numerics;
using System.Runtime.InteropServices;
using BohemiX.Modules.Forge.Engine;
using BohemiX.Modules.Forge.Models;

namespace BohemiX.Modules.Forge.Rendering;

[StructLayout(LayoutKind.Sequential)]
public readonly record struct ForgeVertex(
    Vector3 Position,
    Vector3 Normal,
    Vector4 Tangent,
    Vector2 TexCoord,
    float MaterialId);

public sealed record MeshData(ForgeVertex[] Vertices, uint[] Indices)
{
    public bool IsFinite => Vertices.All(vertex =>
        IsFiniteVector(vertex.Position) && IsFiniteVector(vertex.Normal) &&
        IsFiniteVector(new Vector3(vertex.Tangent.X, vertex.Tangent.Y, vertex.Tangent.Z)) &&
        float.IsFinite(vertex.Tangent.W) && float.IsFinite(vertex.TexCoord.X) && float.IsFinite(vertex.TexCoord.Y));

    private static bool IsFiniteVector(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}

internal sealed class MeshBuilder
{
    private readonly List<ForgeVertex> vertices = new(8192);
    private readonly List<uint> indices = new(16384);

    public int VertexCount => vertices.Count;
    public int IndexCount => indices.Count;

    public uint AddVertex(Vector3 position, Vector3 normal, Vector3 tangent, Vector2 uv, int material)
    {
        var normalized = SafeNormalize(tangent, Vector3.UnitX);
        vertices.Add(new ForgeVertex(position, SafeNormalize(normal, Vector3.UnitY), new Vector4(normalized, 1), uv, material));
        return (uint)(vertices.Count - 1);
    }

    public uint AddVertex(Vector3 position, Vector3 normal, Vector4 tangent, Vector2 uv, int material)
    {
        var normalized = SafeNormalize(new Vector3(tangent.X, tangent.Y, tangent.Z), Vector3.UnitX);
        vertices.Add(new ForgeVertex(position, SafeNormalize(normal, Vector3.UnitY), new Vector4(normalized, tangent.W < 0 ? -1 : 1), uv, material));
        return (uint)(vertices.Count - 1);
    }

    public void AddTriangle(uint a, uint b, uint c)
    {
        indices.Add(a);
        indices.Add(b);
        indices.Add(c);
    }

    public void AddQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, int material, Vector2 uvScale = default)
    {
        if (uvScale == default)
        {
            uvScale = Vector2.One;
        }

        var normal = SafeNormalize(Vector3.Cross(b - a, c - a), Vector3.UnitY);
        var tangent = SafeNormalize(b - a, Vector3.UnitX);
        var offset = (uint)vertices.Count;
        AddVertex(a, normal, tangent, Vector2.Zero, material);
        AddVertex(b, normal, tangent, new Vector2(uvScale.X, 0), material);
        AddVertex(c, normal, tangent, uvScale, material);
        AddVertex(d, normal, tangent, new Vector2(0, uvScale.Y), material);
        AddTriangle(offset, offset + 1, offset + 2);
        AddTriangle(offset, offset + 2, offset + 3);
    }

    public void AddQuadOriented(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 outward, int material, Vector2 uvScale = default)
    {
        if (Vector3.Dot(Vector3.Cross(b - a, c - a), outward) < 0)
        {
            AddQuad(a, d, c, b, material, uvScale);
        }
        else
        {
            AddQuad(a, b, c, d, material, uvScale);
        }
    }

    public void AddTriangleFace(Vector3 a, Vector3 b, Vector3 c, Vector3 outward, int material)
    {
        if (Vector3.Dot(Vector3.Cross(b - a, c - a), outward) < 0) (b, c) = (c, b);
        var normal = SafeNormalize(Vector3.Cross(b - a, c - a), outward);
        var tangent = SafeNormalize(b - a, Vector3.UnitX);
        var offset = (uint)vertices.Count;
        AddVertex(a, normal, tangent, Vector2.Zero, material);
        AddVertex(b, normal, tangent, Vector2.UnitX, material);
        AddVertex(c, normal, tangent, Vector2.UnitY, material);
        AddTriangle(offset, offset + 1, offset + 2);
    }

    public MeshData Build() => new(vertices.ToArray(), indices.ToArray());

    public void Append(MeshData mesh, Matrix4x4 transform)
    {
        var normalMatrix = transform;
        normalMatrix.Translation = Vector3.Zero;
        var offset = (uint)vertices.Count;
        foreach (var vertex in mesh.Vertices)
        {
            vertices.Add(vertex with
            {
                Position = Vector3.Transform(vertex.Position, transform),
                Normal = SafeNormalize(Vector3.TransformNormal(vertex.Normal, normalMatrix), Vector3.UnitY),
                Tangent = new Vector4(
                    SafeNormalize(Vector3.TransformNormal(new Vector3(vertex.Tangent.X, vertex.Tangent.Y, vertex.Tangent.Z), normalMatrix), Vector3.UnitX),
                    vertex.Tangent.W)
            });
        }

        foreach (var index in mesh.Indices)
        {
            indices.Add(offset + index);
        }
    }

    private static Vector3 SafeNormalize(Vector3 value, Vector3 fallback) =>
        value.LengthSquared() < 1e-8f ? fallback : Vector3.Normalize(value);
}

public static class ProceduralMeshFactory
{
    public static MeshData CreateWorkshop()
    {
        var scene = new MeshBuilder();
        scene.Append(CreateAnvil(), Matrix4x4.CreateScale(.75f) * Matrix4x4.CreateTranslation(0, -.25f, 0));
        scene.Append(CreateHearth(), Matrix4x4.CreateScale(.62f) * Matrix4x4.CreateTranslation(-2.9f, -.58f, -.62f));
        return scene.Build();
    }

    public static MeshData CreateWorkbench()
    {
        var mesh = new MeshBuilder();
        AddTranslatedSuperellipseLoft(mesh,
        [
            new(-4.50f, .090f, 2.14f), new(-4.46f, .135f, 2.22f), new(-4.35f, .150f, 2.25f),
            new(4.35f, .150f, 2.25f), new(4.46f, .135f, 2.22f), new(4.50f, .090f, 2.14f)
        ], 7, 64, 7.2f, (int)ForgeMaterialId.WoodDark, new Vector3(0, -.15f, 0));

        AddChamferedBlock(mesh, new Vector3(0, -.69f, 2.02f), new Vector3(8.28f, .78f, .24f), .055f, (int)ForgeMaterialId.WoodDark);
        AddChamferedBlock(mesh, new Vector3(0, -.69f, -2.02f), new Vector3(8.28f, .78f, .24f), .055f, (int)ForgeMaterialId.WoodDark);
        for (var side = -1; side <= 1; side += 2)
        {
            var x = side * 4.05f;
            AddChamferedBlock(mesh, new Vector3(x, -.69f, 0), new Vector3(.28f, .78f, 3.80f), .055f, (int)ForgeMaterialId.WoodDark);
            for (var depth = -1; depth <= 1; depth += 2)
            {
                var z = depth * 1.88f;
                AddChamferedBlock(mesh, new Vector3(x, -1.73f, z), new Vector3(.50f, 2.86f, .50f), .075f, (int)ForgeMaterialId.WoodDark);
                AddChamferedBlock(mesh, new Vector3(x, -3.16f, z), new Vector3(.56f, .18f, .56f), .055f, (int)ForgeMaterialId.Brass);
            }
        }

        for (var xSign = -1; xSign <= 1; xSign += 2)
        for (var zSign = -1; zSign <= 1; zSign += 2)
        {
            AddChamferedBlock(mesh, new Vector3(xSign * 4.34f, -.155f, zSign * 2.09f), new Vector3(.30f, .31f, .30f), .045f, (int)ForgeMaterialId.Brass);
        }

        AddChamferedBlock(mesh, new Vector3(0, .64f, -2.12f), new Vector3(8.25f, 1.24f, .20f), .055f, (int)ForgeMaterialId.Wood);
        AddChamferedBlock(mesh, new Vector3(-4.08f, .65f, -2.10f), new Vector3(.28f, 1.50f, .28f), .055f, (int)ForgeMaterialId.WoodDark);
        AddChamferedBlock(mesh, new Vector3(4.08f, .65f, -2.10f), new Vector3(.28f, 1.50f, .28f), .055f, (int)ForgeMaterialId.WoodDark);
        AddChamferedBlock(mesh, new Vector3(0, 1.28f, -1.82f), new Vector3(8.45f, .18f, .72f), .050f, (int)ForgeMaterialId.WoodDark);

        var alongX = Matrix4x4.CreateRotationZ(MathF.PI / 2) * Matrix4x4.CreateTranslation(0, .78f, -1.96f);
        AddProfiledLathe(mesh,
        [
            new(.042f, -3.78f), new(.052f, -3.72f), new(.052f, 3.72f), new(.042f, 3.78f)
        ], 32, (int)ForgeMaterialId.Brass, alongX);
        foreach (var x in new[] { -3.0f, -1.5f, 0, 1.5f, 3.0f })
        {
            AddProfiledLathe(mesh,
            [
                new(.032f, -.18f), new(.043f, -.15f), new(.043f, .15f), new(.032f, .18f)
            ], 20, (int)ForgeMaterialId.Brass,
                Matrix4x4.CreateRotationX(MathF.PI / 2) * Matrix4x4.CreateTranslation(x, .60f, -1.82f));
        }
        return mesh.Build();
    }

    public static MeshData CreateAnvil()
    {
        var mesh = new MeshBuilder();
        AddLoftedAnvil(mesh);
        AddStump(mesh);
        AddLatheInto(mesh, .585f, .585f, .060f, 96, (int)ForgeMaterialId.IronDark, Matrix4x4.CreateTranslation(.04f, -.79f, 0));
        AddLatheInto(mesh, .565f, .565f, .055f, 96, (int)ForgeMaterialId.IronDark, Matrix4x4.CreateTranslation(.04f, -1.43f, 0));
        AddChamferedBlock(mesh, new Vector3(.88f, .588f, .18f), new Vector3(.18f, .014f, .18f), .014f, (int)ForgeMaterialId.IronDark);
        AddLatheInto(mesh, .076f, .076f, .014f, 40, (int)ForgeMaterialId.IronDark, Matrix4x4.CreateTranslation(.54f, .588f, -.20f));
        return mesh.Build();
    }

    private static void AddLoftedAnvil(MeshBuilder mesh)
    {
        AddTranslatedSuperellipseLoft(mesh,
        [
            new(-2.12f, .018f, .025f), new(-1.93f, .045f, .095f), new(-1.66f, .085f, .235f),
            new(-1.30f, .125f, .395f), new(-.91f, .145f, .505f)
        ], 14, 72, 3.2f, (int)ForgeMaterialId.Iron, new Vector3(0, .435f, 0));

        AddTranslatedSuperellipseLoft(mesh,
        [
            new(-.98f, .135f, .50f), new(-.88f, .155f, .525f), new(1.13f, .155f, .525f),
            new(1.30f, .145f, .505f), new(1.42f, .120f, .46f)
        ], 16, 72, 7.5f, (int)ForgeMaterialId.Iron, new Vector3(0, .435f, 0));

        AddTranslatedSuperellipseLoft(mesh,
        [
            new(-.76f, .255f, .405f), new(-.64f, .285f, .435f), new(.60f, .285f, .435f), new(.77f, .245f, .39f)
        ], 14, 64, 4.2f, (int)ForgeMaterialId.IronDark, new Vector3(.08f, .10f, 0));

        AddTranslatedSuperellipseLoft(mesh,
        [
            new(-.49f, .285f, .285f), new(-.41f, .305f, .305f), new(.48f, .305f, .305f), new(.57f, .275f, .28f)
        ], 12, 64, 4.0f, (int)ForgeMaterialId.IronDark, new Vector3(.08f, -.205f, 0));

        AddTranslatedSuperellipseLoft(mesh,
        [
            new(-.87f, .125f, .49f), new(-.76f, .145f, .525f), new(.80f, .145f, .525f), new(.92f, .120f, .48f)
        ], 12, 64, 5.5f, (int)ForgeMaterialId.IronDark, new Vector3(.04f, -.52f, 0));
    }

    private static void AddTranslatedSuperellipseLoft(MeshBuilder target, ReadOnlySpan<Vector3> controls, int subdivisions, int ringSegments, float exponent, int material, Vector3 translation)
    {
        var dense = new Vector3[(controls.Length - 1) * subdivisions + 1];
        var index = 0;
        for (var segment = 0; segment < controls.Length - 1; segment++)
        {
            for (var step = 0; step < subdivisions; step++)
            {
                var t = Smooth01(step / (float)subdivisions);
                dense[index++] = Vector3.Lerp(controls[segment], controls[segment + 1], t);
            }
        }
        dense[index] = controls[^1];
        var component = new MeshBuilder();
        AddSuperellipseLoft(component, dense, ringSegments, exponent, material);
        target.Append(component.Build(), Matrix4x4.CreateTranslation(translation));
    }

    private static void AddRingCap(MeshBuilder mesh, Vector3[,] positions, int section, Vector3 normal, int material)
    {
        var count = positions.GetLength(1);
        var centerPosition = Vector3.Zero;
        for (var j = 0; j < count; j++) centerPosition += positions[section, j];
        centerPosition /= count;
        var center = mesh.AddVertex(centerPosition, normal, Vector3.UnitZ, new Vector2(.5f), material);
        var ring = new uint[count];
        for (var j = 0; j < count; j++)
        {
            var p = positions[section, j];
            ring[j] = mesh.AddVertex(p, normal, Vector3.UnitZ, new Vector2(p.Z + .5f, p.Y + .5f), material);
        }
        for (var j = 0; j < count; j++)
        {
            var next = (j + 1) % count;
            if (normal.X < 0) mesh.AddTriangle(center, ring[next], ring[j]);
            else mesh.AddTriangle(center, ring[j], ring[next]);
        }
    }

    private static void AddStump(MeshBuilder mesh)
    {
        const int segments = 96;
        const int rings = 9;
        var vertices = new uint[rings, segments];
        for (var r = 0; r < rings; r++)
        {
            var v = r / (float)(rings - 1);
            var y = Lerp(-1.51f, -.63f, v);
            for (var i = 0; i < segments; i++)
            {
                var angle = i / (float)segments * MathF.Tau;
                var irregularity = 1 + .025f * MathF.Sin(angle * 5 + r * .7f) + .012f * MathF.Sin(angle * 11 - r);
                var radius = Lerp(.57f, .55f, v) * irregularity;
                var radial = new Vector3(MathF.Cos(angle), 0, MathF.Sin(angle));
                var tangent = new Vector3(-radial.Z, 0, radial.X);
                var normal = Vector3.Normalize(radial + new Vector3(0, .035f * MathF.Sin(angle * 3), 0));
                vertices[r, i] = mesh.AddVertex(new Vector3(.04f, y, 0) + radial * radius, normal, tangent, new Vector2(i / 10f, v * 2.4f), (int)ForgeMaterialId.WoodDark);
            }
        }
        for (var r = 0; r < rings - 1; r++)
        for (var i = 0; i < segments; i++)
        {
            var next = (i + 1) % segments;
            mesh.AddTriangle(vertices[r, i], vertices[r, next], vertices[r + 1, next]);
            mesh.AddTriangle(vertices[r, i], vertices[r + 1, next], vertices[r + 1, i]);
        }
        var bottom = mesh.AddVertex(new Vector3(.04f, -1.51f, 0), -Vector3.UnitY, Vector3.UnitX, new Vector2(.5f), (int)ForgeMaterialId.WoodDark);
        var top = mesh.AddVertex(new Vector3(.04f, -.63f, 0), Vector3.UnitY, Vector3.UnitX, new Vector2(.5f), (int)ForgeMaterialId.WoodDark);
        for (var i = 0; i < segments; i++)
        {
            var next = (i + 1) % segments;
            mesh.AddTriangle(bottom, vertices[0, next], vertices[0, i]);
            mesh.AddTriangle(top, vertices[rings - 1, i], vertices[rings - 1, next]);
        }
    }

    private static float WidthProfile(float vertical)
    {
        if (vertical < .16f) return Lerp(1.30f, .92f, Smooth01(vertical / .16f));
        if (vertical < .52f) return Lerp(.92f, .58f, Smooth01((vertical - .16f) / .36f));
        if (vertical < .78f) return Lerp(.58f, .90f, Smooth01((vertical - .52f) / .26f));
        return Lerp(.90f, 1.05f, Smooth01((vertical - .78f) / .22f));
    }

    private static float Smooth01(float value)
    {
        value = Math.Clamp(value, 0, 1);
        return value * value * (3 - 2 * value);
    }

    private static float Lerp(float a, float b, float amount) => a + (b - a) * amount;

    private static void AddTaperedPrism(MeshBuilder mesh, float x0, float x1, float bottom, float top, float halfWidth0, float halfWidth1, float halfWidth, int material)
    {
        var a0 = new Vector3(x0, bottom, -halfWidth0); var a1 = new Vector3(x0, bottom, halfWidth0);
        var a2 = new Vector3(x0, top, halfWidth1); var a3 = new Vector3(x0, top, -halfWidth1);
        var b0 = new Vector3(x1, bottom, -halfWidth); var b1 = new Vector3(x1, bottom, halfWidth);
        var b2 = new Vector3(x1, top, halfWidth); var b3 = new Vector3(x1, top, -halfWidth);
        mesh.AddQuadOriented(a3, a2, b2, b3, Vector3.UnitY, material);
        mesh.AddQuadOriented(a0, b0, b1, a1, -Vector3.UnitY, material);
        mesh.AddQuadOriented(a1, b1, b2, a2, Vector3.UnitZ, material);
        mesh.AddQuadOriented(a0, a3, b3, b0, -Vector3.UnitZ, material);
        mesh.AddQuadOriented(a0, a1, a2, a3, -Vector3.UnitX, material);
        mesh.AddQuadOriented(b0, b3, b2, b1, Vector3.UnitX, material);
    }

    public static MeshData CreateHammer()
    {
        var mesh = new MeshBuilder();
        // The local origin is the smith's grip. Keeping the pivot at the handle butt makes
        // the render transform describe a real swing instead of rotating the tool about
        // the middle of its head.
        var alongX = Matrix4x4.CreateRotationZ(-MathF.PI / 2);
        AddProfiledLathe(mesh,
        [
            new(.090f, .00f), new(.105f, .08f), new(.098f, .28f), new(.092f, .68f),
            new(.096f, 1.08f), new(.112f, 1.34f), new(.128f, 1.48f), new(.105f, 1.57f)
        ], 48, (int)ForgeMaterialId.Wood, alongX);

        // Forged eye and collars bind the head to the handle.
        AddProfiledLathe(mesh,
        [
            new(.142f, 1.30f), new(.172f, 1.34f), new(.178f, 1.47f), new(.146f, 1.52f)
        ], 40, (int)ForgeMaterialId.IronDark, alongX);

        // Build the head across the handle. The left face stays broad and flat while the
        // opposite side narrows into the cross peen used for tips and edges.
        var head = new MeshBuilder();
        AddSuperellipseLoft(head,
        [
            new(-.72f, .285f, .300f), new(-.66f, .325f, .340f), new(-.52f, .340f, .355f),
            new(.24f, .340f, .355f), new(.44f, .300f, .310f), new(.62f, .205f, .245f),
            new(.78f, .105f, .175f), new(.88f, .045f, .125f)
        ], 48, 3.8f, (int)ForgeMaterialId.Iron);
        mesh.Append(
            head.Build(),
            Matrix4x4.CreateRotationY(-MathF.PI / 2) * Matrix4x4.CreateTranslation(1.49f, 0, 0));
        return mesh.Build();
    }

    public static MeshData CreateTongs()
    {
        var mesh = new MeshBuilder();
        for (var side = -1; side <= 1; side += 2)
        {
            var armTransform = Matrix4x4.CreateRotationZ(MathF.PI / 2) *
                               Matrix4x4.CreateRotationY(side * .035f) *
                               Matrix4x4.CreateTranslation(0, 0, side * .085f);
            AddProfiledLathe(mesh,
            [
                new(.052f, -1.18f), new(.062f, -1.10f), new(.070f, -.48f),
                new(.080f, .18f), new(.070f, .72f), new(.055f, 1.08f)
            ], 28, (int)ForgeMaterialId.IronDark, armTransform);
            AddChamferedBlockTransformed(
                mesh,
                new Vector3(.48f, .105f, .105f),
                .026f,
                (int)ForgeMaterialId.Iron,
                Matrix4x4.CreateRotationZ(side * .20f) *
                Matrix4x4.CreateTranslation(1.22f, side * .055f, side * .085f));
        }

        AddProfiledLathe(mesh,
        [
            new(.105f, -.15f), new(.125f, -.11f), new(.125f, .11f), new(.105f, .15f)
        ], 32, (int)ForgeMaterialId.Iron,
            Matrix4x4.CreateRotationX(MathF.PI / 2) * Matrix4x4.CreateTranslation(.05f, 0, 0));
        return mesh.Build();
    }

    public static MeshData CreateHearth()
    {
        var mesh = new MeshBuilder();
        AddChamferedBlock(mesh, new Vector3(0, .04f, 0), new Vector3(2.64f, .34f, 1.78f), .075f, (int)ForgeMaterialId.Stone);

        const int courses = 5;
        for (var course = 0; course < courses; course++)
        {
            var y = .285f + course * .192f;
            for (var side = -1; side <= 1; side += 2)
            for (var depth = 0; depth < 5; depth++)
            {
                var z = -.62f + depth * .31f + (course % 2 == 0 ? 0 : .012f);
                AddChamferedBlock(mesh, new Vector3(side * .995f, y, z), new Vector3(.355f, .172f, .282f), .025f, (int)ForgeMaterialId.Brick);
            }

            var columns = course % 2 == 0 ? 5 : 4;
            for (var column = 0; column < columns; column++)
            {
                var x = (column - (columns - 1) / 2f) * .34f;
                AddChamferedBlock(mesh, new Vector3(x, y, -.735f), new Vector3(.312f, .172f, .205f), .022f, (int)ForgeMaterialId.Brick);
            }
        }

        const int archBricks = 11;
        for (var i = 0; i < archBricks; i++)
        {
            var angle = (i + .5f) / archBricks * MathF.PI;
            var position = new Vector3(MathF.Cos(angle) * .685f, .935f + MathF.Sin(angle) * .685f, .665f);
            var transform = Matrix4x4.CreateRotationZ(angle + MathF.PI / 2) * Matrix4x4.CreateTranslation(position);
            AddChamferedBlockTransformed(mesh, new Vector3(.190f, .205f, .255f), .020f, (int)ForgeMaterialId.Brick, transform);
        }

        AddChamferedBlock(mesh, new Vector3(0, 1.47f, -.02f), new Vector3(2.46f, .30f, 1.72f), .075f, (int)ForgeMaterialId.Stone);
        AddProfiledLathe(mesh,
        [
            new(.70f, .198f), new(.745f, .210f), new(.77f, .244f), new(.72f, .282f), new(.58f, .306f)
        ], 56, (int)ForgeMaterialId.Coal, Matrix4x4.CreateScale(1, 1, .73f) * Matrix4x4.CreateTranslation(0, 0, .30f));

        return mesh.Build();
    }

    public static MeshData CreateBellows()
    {
        var mesh = new MeshBuilder();
        AddBellowsLoft(mesh, new Vector3(-.04f, -.085f, 0), 1.58f, .92f,
        [
            new(.92f, -.058f), new(.985f, -.038f), new(1f, .030f), new(.92f, .054f)
        ], 48, (int)ForgeMaterialId.Wood);
        AddBellowsLoft(mesh, new Vector3(-.04f, .225f, 0), 1.58f, .92f,
        [
            new(.92f, -.054f), new(1f, -.030f), new(.985f, .038f), new(.92f, .058f)
        ], 48, (int)ForgeMaterialId.Wood);
        AddBellowsLoft(mesh, new Vector3(-.04f, .055f, 0), 1.50f, .86f,
        [
            new(.90f, -.090f), new(.985f, -.064f), new(.84f, -.025f), new(.985f, .018f),
            new(.83f, .060f), new(.97f, .102f), new(.90f, .124f)
        ], 48, (int)ForgeMaterialId.Leather);

        var alongX = Matrix4x4.CreateRotationZ(MathF.PI / 2) * Matrix4x4.CreateTranslation(0, .055f, 0);
        AddProfiledLathe(mesh,
        [
            new(.028f, -1.50f), new(.035f, -1.43f), new(.052f, -1.12f), new(.073f, -.88f),
            new(.106f, -.76f), new(.116f, -.70f)
        ], 36, (int)ForgeMaterialId.Brass, alongX);
        AddProfiledLathe(mesh,
        [
            new(.055f, -.10f), new(.065f, -.075f), new(.065f, .075f), new(.055f, .10f)
        ], 28, (int)ForgeMaterialId.IronDark,
            Matrix4x4.CreateRotationX(MathF.PI / 2) * Matrix4x4.CreateTranslation(-.54f, .325f, 0));
        AddChamferedBlockTransformed(mesh, new Vector3(1.10f, .115f, .14f), .028f, (int)ForgeMaterialId.Wood,
            Matrix4x4.CreateRotationZ(-.10f) * Matrix4x4.CreateTranslation(-.12f, .405f, 0));
        return mesh.Build();
    }

    public static MeshData CreateQuenchVat(bool oil)
    {
        var mesh = new MeshBuilder();
        var vesselMaterial = oil ? (int)ForgeMaterialId.Brass : (int)ForgeMaterialId.IronDark;
        AddProfiledLathe(mesh,
        [
            new(.365f, -.46f), new(.425f, -.452f), new(.475f, -.408f), new(.500f, -.330f),
            new(.512f, -.12f), new(.522f, .15f), new(.518f, .34f), new(.502f, .425f)
        ], 64, vesselMaterial, Matrix4x4.Identity);
        AddTorusInto(mesh, .505f, .052f, 64, 12, vesselMaterial, Matrix4x4.CreateTranslation(0, .425f, 0));
        AddTorusInto(mesh, .487f, .026f, 56, 8, (int)ForgeMaterialId.IronDark, Matrix4x4.CreateTranslation(0, -.285f, 0));
        AddTorusInto(mesh, .516f, .024f, 56, 8, (int)ForgeMaterialId.IronDark, Matrix4x4.CreateTranslation(0, .115f, 0));
        AddProfiledLathe(mesh,
        [
            new(.455f, .425f), new(.455f, .448f)
        ], 64, oil ? (int)ForgeMaterialId.Oil : (int)ForgeMaterialId.Water, Matrix4x4.Identity);
        return mesh.Build();
    }

    public static MeshData CreateGrinder()
    {
        var mesh = new MeshBuilder();
        AddGrinderStand(mesh);
        AddGrinderWheel(mesh);
        return mesh.Build();
    }

    public static MeshData CreateGrinderStand()
    {
        var mesh = new MeshBuilder();
        AddGrinderStand(mesh);
        return mesh.Build();
    }

    public static MeshData CreateGrinderWheel()
    {
        var mesh = new MeshBuilder();
        AddGrinderWheel(mesh);
        return mesh.Build();
    }

    private static void AddGrinderStand(MeshBuilder mesh)
    {
        AddChamferedBlock(mesh, new Vector3(0, -.54f, 0), new Vector3(1.55f, .22f, 1.02f), .065f, (int)ForgeMaterialId.WoodDark);
        for (var side = -1; side <= 1; side += 2)
        {
            var z = side * .36f;
            AddChamferedBlockTransformed(mesh, new Vector3(.18f, 1.08f, .18f), .040f, (int)ForgeMaterialId.Wood,
                Matrix4x4.CreateRotationZ(-.34f) * Matrix4x4.CreateTranslation(-.28f, -.10f, z));
            AddChamferedBlockTransformed(mesh, new Vector3(.18f, 1.08f, .18f), .040f, (int)ForgeMaterialId.Wood,
                Matrix4x4.CreateRotationZ(.34f) * Matrix4x4.CreateTranslation(.28f, -.10f, z));
            AddChamferedBlock(mesh, new Vector3(0, .285f, z), new Vector3(.62f, .17f, .20f), .035f, (int)ForgeMaterialId.WoodDark);
        }

        var alongZ = Matrix4x4.CreateRotationX(MathF.PI / 2) * Matrix4x4.CreateTranslation(0, .38f, 0);
        AddProfiledLathe(mesh,
        [
            new(.090f, -.30f), new(.125f, -.27f), new(.145f, -.18f), new(.145f, .18f),
            new(.125f, .27f), new(.090f, .30f)
        ], 40, (int)ForgeMaterialId.IronDark, alongZ);
        AddProfiledLathe(mesh,
        [
            new(.055f, -.64f), new(.068f, -.61f), new(.068f, .61f), new(.055f, .64f)
        ], 28, (int)ForgeMaterialId.IronDark, alongZ);
        AddChamferedBlockTransformed(mesh, new Vector3(.075f, .42f, .075f), .018f, (int)ForgeMaterialId.IronDark,
            Matrix4x4.CreateRotationZ(-.64f) * Matrix4x4.CreateTranslation(.13f, .26f, .66f));
        AddProfiledLathe(mesh,
        [
            new(.055f, -.13f), new(.072f, -.10f), new(.075f, .10f), new(.055f, .13f)
        ], 28, (int)ForgeMaterialId.Wood,
            Matrix4x4.CreateRotationX(MathF.PI / 2) * Matrix4x4.CreateTranslation(.25f, .12f, .71f));
    }

    private static void AddGrinderWheel(MeshBuilder mesh)
    {
        var alongZ = Matrix4x4.CreateRotationX(MathF.PI / 2) * Matrix4x4.CreateTranslation(0, .38f, 0);
        AddProfiledLathe(mesh,
        [
            new(.505f, -.145f), new(.555f, -.134f), new(.588f, -.105f), new(.602f, -.062f),
            new(.602f, .062f), new(.588f, .105f), new(.555f, .134f), new(.505f, .145f)
        ], 72, (int)ForgeMaterialId.Stone, alongZ);
    }

    private static void AddProfiledLathe(MeshBuilder mesh, ReadOnlySpan<Vector2> profile, int segments, int material, Matrix4x4 transform, bool closedProfile = false)
    {
        if (profile.Length < 2 || segments < 3) return;
        var vertices = new uint[profile.Length, segments];
        for (var p = 0; p < profile.Length; p++)
        {
            var previous = closedProfile ? (p + profile.Length - 1) % profile.Length : Math.Max(0, p - 1);
            var next = closedProfile ? (p + 1) % profile.Length : Math.Min(profile.Length - 1, p + 1);
            var dr = profile[next].X - profile[previous].X;
            var dy = profile[next].Y - profile[previous].Y;
            for (var i = 0; i < segments; i++)
            {
                var angle = i / (float)segments * MathF.Tau;
                var c = MathF.Cos(angle);
                var s = MathF.Sin(angle);
                var localNormal = NormalizeStatic(new Vector3(c * dy, -dr, s * dy), new Vector3(c, 0, s));
                var localTangent = new Vector3(-s, 0, c);
                vertices[p, i] = mesh.AddVertex(
                    Vector3.Transform(new Vector3(c * profile[p].X, profile[p].Y, s * profile[p].X), transform),
                    Vector3.TransformNormal(localNormal, transform),
                    Vector3.TransformNormal(localTangent, transform),
                    new Vector2(i / (float)segments, p / (float)Math.Max(1, profile.Length - 1)), material);
            }
        }

        var bands = closedProfile ? profile.Length : profile.Length - 1;
        for (var p = 0; p < bands; p++)
        {
            var nextProfile = (p + 1) % profile.Length;
            for (var i = 0; i < segments; i++)
            {
                var next = (i + 1) % segments;
                mesh.AddTriangle(vertices[p, i], vertices[nextProfile, i], vertices[nextProfile, next]);
                mesh.AddTriangle(vertices[p, i], vertices[nextProfile, next], vertices[p, next]);
            }
        }

        if (closedProfile) return;
        AddLatheCap(mesh, profile[0], segments, material, transform, false);
        AddLatheCap(mesh, profile[^1], segments, material, transform, true);
    }

    private static void AddLatheCap(MeshBuilder mesh, Vector2 profile, int segments, int material, Matrix4x4 transform, bool top)
    {
        var normal = Vector3.TransformNormal(top ? Vector3.UnitY : -Vector3.UnitY, transform);
        var tangent = Vector3.TransformNormal(Vector3.UnitX, transform);
        var center = mesh.AddVertex(Vector3.Transform(new Vector3(0, profile.Y, 0), transform), normal, tangent, new Vector2(.5f), material);
        var ring = new uint[segments];
        for (var i = 0; i < segments; i++)
        {
            var angle = i / (float)segments * MathF.Tau;
            var c = MathF.Cos(angle);
            var s = MathF.Sin(angle);
            ring[i] = mesh.AddVertex(
                Vector3.Transform(new Vector3(c * profile.X, profile.Y, s * profile.X), transform), normal, tangent,
                new Vector2(c * .5f + .5f, s * .5f + .5f), material);
        }
        for (var i = 0; i < segments; i++)
        {
            var next = (i + 1) % segments;
            if (top) mesh.AddTriangle(center, ring[next], ring[i]);
            else mesh.AddTriangle(center, ring[i], ring[next]);
        }
    }

    private static void AddTorusInto(MeshBuilder mesh, float radius, float tubeRadius, int segments, int tubeSegments, int material, Matrix4x4 transform)
    {
        var profile = new Vector2[tubeSegments];
        for (var i = 0; i < tubeSegments; i++)
        {
            var angle = i / (float)tubeSegments * MathF.Tau;
            profile[i] = new Vector2(radius + MathF.Cos(angle) * tubeRadius, MathF.Sin(angle) * tubeRadius);
        }
        AddProfiledLathe(mesh, profile, segments, material, transform, true);
    }

    private static void AddSuperellipseLoft(MeshBuilder mesh, ReadOnlySpan<Vector3> profile, int segments, float exponent, int material)
    {
        if (profile.Length < 2 || segments < 4) return;
        var positions = new Vector3[profile.Length, segments];
        var vertices = new uint[profile.Length, segments];
        var power = 2f / Math.Max(2.01f, exponent);
        for (var p = 0; p < profile.Length; p++)
        for (var i = 0; i < segments; i++)
        {
            var angle = i / (float)segments * MathF.Tau;
            var y = SignedPower(MathF.Cos(angle), power) * profile[p].Y;
            var z = SignedPower(MathF.Sin(angle), power) * profile[p].Z;
            positions[p, i] = new Vector3(profile[p].X, y, z);
        }

        for (var p = 0; p < profile.Length; p++)
        for (var i = 0; i < segments; i++)
        {
            var previousP = Math.Max(0, p - 1);
            var nextP = Math.Min(profile.Length - 1, p + 1);
            var previous = (i + segments - 1) % segments;
            var next = (i + 1) % segments;
            var along = positions[nextP, i] - positions[previousP, i];
            var around = positions[p, next] - positions[p, previous];
            var normal = NormalizeStatic(Vector3.Cross(around, along), new Vector3(0, positions[p, i].Y, positions[p, i].Z));
            vertices[p, i] = mesh.AddVertex(positions[p, i], normal, along,
                new Vector2(p / (float)(profile.Length - 1), i / (float)segments), material);
        }

        for (var p = 0; p < profile.Length - 1; p++)
        for (var i = 0; i < segments; i++)
        {
            var next = (i + 1) % segments;
            mesh.AddTriangle(vertices[p, i], vertices[p + 1, next], vertices[p + 1, i]);
            mesh.AddTriangle(vertices[p, i], vertices[p, next], vertices[p + 1, next]);
        }
        AddSuperellipseCap(mesh, positions, 0, -Vector3.UnitX, material);
        AddSuperellipseCap(mesh, positions, profile.Length - 1, Vector3.UnitX, material);
    }

    private static void AddSuperellipseCap(MeshBuilder mesh, Vector3[,] positions, int profile, Vector3 normal, int material)
    {
        var segments = positions.GetLength(1);
        var centerPosition = Vector3.Zero;
        for (var i = 0; i < segments; i++) centerPosition += positions[profile, i];
        centerPosition /= segments;
        var center = mesh.AddVertex(centerPosition, normal, Vector3.UnitY, new Vector2(.5f), material);
        var ring = new uint[segments];
        for (var i = 0; i < segments; i++)
        {
            var position = positions[profile, i];
            ring[i] = mesh.AddVertex(position, normal, Vector3.UnitY,
                new Vector2(position.Y + .5f, position.Z + .5f), material);
        }
        for (var i = 0; i < segments; i++)
        {
            var next = (i + 1) % segments;
            if (normal.X > 0) mesh.AddTriangle(center, ring[i], ring[next]);
            else mesh.AddTriangle(center, ring[next], ring[i]);
        }
    }

    private static void AddBellowsLoft(MeshBuilder mesh, Vector3 center, float length, float width, ReadOnlySpan<Vector2> profile, int segments, int material)
    {
        if (profile.Length < 2 || segments < 4) return;
        var positions = new Vector3[profile.Length, segments];
        var vertices = new uint[profile.Length, segments];
        for (var p = 0; p < profile.Length; p++)
        for (var i = 0; i < segments; i++)
        {
            var angle = i / (float)segments * MathF.Tau;
            var c = MathF.Cos(angle);
            var s = MathF.Sin(angle);
            var fullness = .82f - .24f * c;
            positions[p, i] = center + new Vector3(
                c * length * .5f * profile[p].X,
                profile[p].Y,
                s * width * .5f * fullness * profile[p].X);
        }

        for (var p = 0; p < profile.Length; p++)
        for (var i = 0; i < segments; i++)
        {
            var previousP = Math.Max(0, p - 1);
            var nextP = Math.Min(profile.Length - 1, p + 1);
            var previous = (i + segments - 1) % segments;
            var next = (i + 1) % segments;
            var vertical = positions[nextP, i] - positions[previousP, i];
            var around = positions[p, next] - positions[p, previous];
            var fallback = positions[p, i] - new Vector3(center.X, positions[p, i].Y, center.Z);
            var normal = NormalizeStatic(Vector3.Cross(vertical, around), fallback);
            vertices[p, i] = mesh.AddVertex(positions[p, i], normal, around,
                new Vector2(i / (float)segments, p / (float)(profile.Length - 1)), material);
        }

        for (var p = 0; p < profile.Length - 1; p++)
        for (var i = 0; i < segments; i++)
        {
            var next = (i + 1) % segments;
            mesh.AddTriangle(vertices[p, i], vertices[p + 1, i], vertices[p + 1, next]);
            mesh.AddTriangle(vertices[p, i], vertices[p + 1, next], vertices[p, next]);
        }
        AddBellowsCap(mesh, positions, 0, -Vector3.UnitY, material);
        AddBellowsCap(mesh, positions, profile.Length - 1, Vector3.UnitY, material);
    }

    private static void AddBellowsCap(MeshBuilder mesh, Vector3[,] positions, int profile, Vector3 normal, int material)
    {
        var segments = positions.GetLength(1);
        var centerPosition = Vector3.Zero;
        for (var i = 0; i < segments; i++) centerPosition += positions[profile, i];
        centerPosition /= segments;
        var center = mesh.AddVertex(centerPosition, normal, Vector3.UnitX, new Vector2(.5f), material);
        var ring = new uint[segments];
        for (var i = 0; i < segments; i++)
        {
            var position = positions[profile, i];
            ring[i] = mesh.AddVertex(position, normal, Vector3.UnitX,
                new Vector2(position.X + .5f, position.Z + .5f), material);
        }
        for (var i = 0; i < segments; i++)
        {
            var next = (i + 1) % segments;
            if (normal.Y > 0) mesh.AddTriangle(center, ring[next], ring[i]);
            else mesh.AddTriangle(center, ring[i], ring[next]);
        }
    }

    private static void AddChamferedBlockTransformed(MeshBuilder mesh, Vector3 size, float bevel, int material, Matrix4x4 transform)
    {
        var component = new MeshBuilder();
        AddChamferedBlock(component, Vector3.Zero, size, bevel, material);
        mesh.Append(component.Build(), transform);
    }

    private static float SignedPower(float value, float power) => MathF.CopySign(MathF.Pow(MathF.Abs(value), power), value);

    private static Vector3 NormalizeStatic(Vector3 value, Vector3 fallback)
    {
        if (value.LengthSquared() >= 1e-8f) return Vector3.Normalize(value);
        return fallback.LengthSquared() >= 1e-8f ? Vector3.Normalize(fallback) : Vector3.UnitY;
    }

    private static void AddTimberWorkshop(MeshBuilder mesh)
    {
        for (var i = 0; i < 13; i++)
        {
            var z = -3.10f + i * .50f;
            AddChamferedBlock(mesh, new Vector3(0, -1.12f, z), new Vector3(9.4f, .16f, .46f), .025f, (int)ForgeMaterialId.WoodDark);
        }
        for (var i = 0; i < 10; i++)
        {
            var y = -1.0f + i * .51f;
            AddChamferedBlock(mesh, new Vector3(0, y, -3.05f), new Vector3(9.4f, .46f, .16f), .02f, i % 3 == 0 ? (int)ForgeMaterialId.Wood : (int)ForgeMaterialId.WoodDark);
        }
        AddChamferedBlock(mesh, new Vector3(-4.40f, 1.30f, -2.78f), new Vector3(.34f, 5.10f, .42f), .04f, (int)ForgeMaterialId.WoodDark);
        AddChamferedBlock(mesh, new Vector3(4.40f, 1.30f, -2.78f), new Vector3(.34f, 5.10f, .42f), .04f, (int)ForgeMaterialId.WoodDark);
        AddChamferedBlock(mesh, new Vector3(0, 3.58f, -2.78f), new Vector3(9.0f, .38f, .42f), .04f, (int)ForgeMaterialId.WoodDark);
    }

    private static void AddHardyHoleTop(MeshBuilder mesh, Vector2 center, float size, float halfWidth)
    {
        const float y = .515f;
        const float x0 = -.62f;
        const float x1 = .82f;
        var hx0 = center.X - size / 2;
        var hx1 = center.X + size / 2;
        var hz0 = center.Y - size / 2;
        var hz1 = center.Y + size / 2;
        mesh.AddQuad(new Vector3(x0, y, -halfWidth), new Vector3(hx0, y, -halfWidth), new Vector3(hx0, y, halfWidth), new Vector3(x0, y, halfWidth), (int)ForgeMaterialId.Iron);
        mesh.AddQuad(new Vector3(hx1, y, -halfWidth), new Vector3(x1, y, -halfWidth), new Vector3(x1, y, halfWidth), new Vector3(hx1, y, halfWidth), (int)ForgeMaterialId.Iron);
        mesh.AddQuad(new Vector3(hx0, y, -halfWidth), new Vector3(hx1, y, -halfWidth), new Vector3(hx1, y, hz0), new Vector3(hx0, y, hz0), (int)ForgeMaterialId.Iron);
        mesh.AddQuad(new Vector3(hx0, y, hz1), new Vector3(hx1, y, hz1), new Vector3(hx1, y, halfWidth), new Vector3(hx0, y, halfWidth), (int)ForgeMaterialId.Iron);
        const float bottom = .20f;
        mesh.AddQuad(new Vector3(hx0, y, hz0), new Vector3(hx1, y, hz0), new Vector3(hx1, bottom, hz0), new Vector3(hx0, bottom, hz0), (int)ForgeMaterialId.IronDark);
        mesh.AddQuad(new Vector3(hx1, y, hz1), new Vector3(hx0, y, hz1), new Vector3(hx0, bottom, hz1), new Vector3(hx1, bottom, hz1), (int)ForgeMaterialId.IronDark);
        mesh.AddQuad(new Vector3(hx0, y, hz1), new Vector3(hx0, y, hz0), new Vector3(hx0, bottom, hz0), new Vector3(hx0, bottom, hz1), (int)ForgeMaterialId.IronDark);
        mesh.AddQuad(new Vector3(hx1, y, hz0), new Vector3(hx1, y, hz1), new Vector3(hx1, bottom, hz1), new Vector3(hx1, bottom, hz0), (int)ForgeMaterialId.IronDark);
    }

    private static Vector2[] RoundedRectangle(float x, float bottom, float top, float halfWidth) =>
    [
        new(-halfWidth * .72f, bottom), new(halfWidth * .72f, bottom), new(halfWidth, bottom + .08f), new(halfWidth, top - .07f),
        new(halfWidth * .78f, top), new(-halfWidth * .78f, top), new(-halfWidth, top - .07f), new(-halfWidth, bottom + .08f)
    ];

    private static void AddArchOpening(MeshBuilder mesh, Vector3 center, float width, float height, float depth)
    {
        const int segments = 9;
        var outer = width * .62f;
        var inner = width * .43f;
        for (var i = 0; i < segments; i++)
        {
            var a0 = MathF.PI * i / segments;
            var a1 = MathF.PI * (i + 1) / segments;
            var p0 = center + new Vector3(MathF.Cos(a0) * outer, MathF.Sin(a0) * height * .55f, 0);
            var p1 = center + new Vector3(MathF.Cos(a1) * outer, MathF.Sin(a1) * height * .55f, 0);
            var p2 = center + new Vector3(MathF.Cos(a1) * inner, MathF.Sin(a1) * height * .38f, .04f);
            var p3 = center + new Vector3(MathF.Cos(a0) * inner, MathF.Sin(a0) * height * .38f, .04f);
            mesh.AddQuad(p0, p1, p2, p3, (int)ForgeMaterialId.Brick);
        }
        mesh.AddQuad(center + new Vector3(-inner, 0, .05f), center + new Vector3(inner, 0, .05f), center + new Vector3(inner, height * .38f, .05f), center + new Vector3(-inner, height * .38f, .05f), (int)ForgeMaterialId.Coal);
    }

    internal static void AddChamferedBlock(MeshBuilder mesh, Vector3 center, Vector3 size, float bevel, int material)
    {
        var h = size / 2;
        bevel = Math.Clamp(bevel, 0, MathF.Min(h.X, MathF.Min(h.Y, h.Z)) * .8f);
        var x0 = center.X - h.X; var x1 = center.X + h.X;
        var y0 = center.Y - h.Y; var y1 = center.Y + h.Y;
        var z0 = center.Z - h.Z; var z1 = center.Z + h.Z;
        var bx0 = x0 + bevel; var bx1 = x1 - bevel;
        var by0 = y0 + bevel; var by1 = y1 - bevel;
        var bz0 = z0 + bevel; var bz1 = z1 - bevel;
        mesh.AddQuadOriented(new Vector3(bx0, y1, bz0), new Vector3(bx1, y1, bz0), new Vector3(bx1, y1, bz1), new Vector3(bx0, y1, bz1), Vector3.UnitY, material, new Vector2(size.X, size.Z));
        mesh.AddQuadOriented(new Vector3(bx0, y0, bz0), new Vector3(bx0, y0, bz1), new Vector3(bx1, y0, bz1), new Vector3(bx1, y0, bz0), -Vector3.UnitY, material);
        mesh.AddQuadOriented(new Vector3(x0, by0, bz0), new Vector3(x0, by1, bz0), new Vector3(x0, by1, bz1), new Vector3(x0, by0, bz1), -Vector3.UnitX, material);
        mesh.AddQuadOriented(new Vector3(x1, by0, bz0), new Vector3(x1, by0, bz1), new Vector3(x1, by1, bz1), new Vector3(x1, by1, bz0), Vector3.UnitX, material);
        mesh.AddQuadOriented(new Vector3(bx0, by0, z0), new Vector3(bx1, by0, z0), new Vector3(bx1, by1, z0), new Vector3(bx0, by1, z0), -Vector3.UnitZ, material);
        mesh.AddQuadOriented(new Vector3(bx0, by0, z1), new Vector3(bx0, by1, z1), new Vector3(bx1, by1, z1), new Vector3(bx1, by0, z1), Vector3.UnitZ, material);

        mesh.AddQuadOriented(new Vector3(bx0, y1, bz0), new Vector3(bx0, by1, z0), new Vector3(bx1, by1, z0), new Vector3(bx1, y1, bz0), Vector3.Normalize(new Vector3(0, 1, -1)), material);
        mesh.AddQuadOriented(new Vector3(bx0, by1, z1), new Vector3(bx0, y1, bz1), new Vector3(bx1, y1, bz1), new Vector3(bx1, by1, z1), Vector3.Normalize(new Vector3(0, 1, 1)), material);
        mesh.AddQuadOriented(new Vector3(bx0, y0, bz0), new Vector3(bx1, y0, bz0), new Vector3(bx1, by0, z0), new Vector3(bx0, by0, z0), Vector3.Normalize(new Vector3(0, -1, -1)), material);
        mesh.AddQuadOriented(new Vector3(bx0, y0, bz1), new Vector3(bx0, by0, z1), new Vector3(bx1, by0, z1), new Vector3(bx1, y0, bz1), Vector3.Normalize(new Vector3(0, -1, 1)), material);
        mesh.AddQuadOriented(new Vector3(x0, by1, bz0), new Vector3(x0, by1, bz1), new Vector3(bx0, y1, bz1), new Vector3(bx0, y1, bz0), Vector3.Normalize(new Vector3(-1, 1, 0)), material);
        mesh.AddQuadOriented(new Vector3(bx1, y1, bz0), new Vector3(bx1, y1, bz1), new Vector3(x1, by1, bz1), new Vector3(x1, by1, bz0), Vector3.Normalize(new Vector3(1, 1, 0)), material);
        mesh.AddQuadOriented(new Vector3(x0, by0, bz0), new Vector3(bx0, y0, bz0), new Vector3(bx0, y0, bz1), new Vector3(x0, by0, bz1), Vector3.Normalize(new Vector3(-1, -1, 0)), material);
        mesh.AddQuadOriented(new Vector3(bx1, y0, bz0), new Vector3(x1, by0, bz0), new Vector3(x1, by0, bz1), new Vector3(bx1, y0, bz1), Vector3.Normalize(new Vector3(1, -1, 0)), material);
        mesh.AddQuadOriented(new Vector3(x0, by0, bz0), new Vector3(x0, by1, bz0), new Vector3(bx0, by1, z0), new Vector3(bx0, by0, z0), Vector3.Normalize(new Vector3(-1, 0, -1)), material);
        mesh.AddQuadOriented(new Vector3(bx1, by0, z0), new Vector3(bx1, by1, z0), new Vector3(x1, by1, bz0), new Vector3(x1, by0, bz0), Vector3.Normalize(new Vector3(1, 0, -1)), material);
        mesh.AddQuadOriented(new Vector3(x0, by0, bz1), new Vector3(bx0, by0, z1), new Vector3(bx0, by1, z1), new Vector3(x0, by1, bz1), Vector3.Normalize(new Vector3(-1, 0, 1)), material);
        mesh.AddQuadOriented(new Vector3(bx1, by0, z1), new Vector3(x1, by0, bz1), new Vector3(x1, by1, bz1), new Vector3(bx1, by1, z1), Vector3.Normalize(new Vector3(1, 0, 1)), material);

        foreach (var sx in new[] { -1, 1 })
        foreach (var sy in new[] { -1, 1 })
        foreach (var sz in new[] { -1, 1 })
        {
            var xo = sx < 0 ? x0 : x1; var xi = sx < 0 ? bx0 : bx1;
            var yo = sy < 0 ? y0 : y1; var yi = sy < 0 ? by0 : by1;
            var zo = sz < 0 ? z0 : z1; var zi = sz < 0 ? bz0 : bz1;
            mesh.AddTriangleFace(new Vector3(xo, yi, zi), new Vector3(xi, yo, zi), new Vector3(xi, yi, zo), Vector3.Normalize(new Vector3(sx, sy, sz)), material);
        }
    }

    private static MeshData CreateLathe(float bottomRadius, float topRadius, float height, int segments, int material, bool capped = true)
    {
        var mesh = new MeshBuilder();
        AddLatheInto(mesh, bottomRadius, topRadius, height, segments, material, Matrix4x4.Identity, capped);
        return mesh.Build();
    }

    private static void AddLatheInto(MeshBuilder mesh, float bottomRadius, float topRadius, float height, int segments, int material, Matrix4x4 transform, bool capped = true)
    {
        var bottom = new uint[segments];
        var top = new uint[segments];
        for (var i = 0; i < segments; i++)
        {
            var angle = i / (float)segments * MathF.Tau;
            var radial = new Vector3(MathF.Cos(angle), 0, MathF.Sin(angle));
            var tangent = new Vector3(-radial.Z, 0, radial.X);
            bottom[i] = mesh.AddVertex(Vector3.Transform(radial * bottomRadius + new Vector3(0, -height / 2, 0), transform), Vector3.TransformNormal(radial, transform), Vector3.TransformNormal(tangent, transform), new Vector2(i / (float)segments, 0), material);
            top[i] = mesh.AddVertex(Vector3.Transform(radial * topRadius + new Vector3(0, height / 2, 0), transform), Vector3.TransformNormal(radial, transform), Vector3.TransformNormal(tangent, transform), new Vector2(i / (float)segments, 1), material);
        }
        for (var i = 0; i < segments; i++)
        {
            var n = (i + 1) % segments;
            mesh.AddTriangle(bottom[i], bottom[n], top[n]);
            mesh.AddTriangle(bottom[i], top[n], top[i]);
        }
        if (!capped) return;
        var cb = mesh.AddVertex(Vector3.Transform(new Vector3(0, -height / 2, 0), transform), Vector3.TransformNormal(-Vector3.UnitY, transform), Vector3.UnitX, new Vector2(.5f), material);
        var ct = mesh.AddVertex(Vector3.Transform(new Vector3(0, height / 2, 0), transform), Vector3.TransformNormal(Vector3.UnitY, transform), Vector3.UnitX, new Vector2(.5f), material);
        for (var i = 0; i < segments; i++)
        {
            var n = (i + 1) % segments;
            mesh.AddTriangle(cb, bottom[n], bottom[i]);
            mesh.AddTriangle(ct, top[i], top[n]);
        }
    }

    private readonly record struct AnvilSection(float X, float Bottom, float Top, float HalfWidth);
}

public enum ForgeWorkpieceKind
{
    Longsword,
    Shortsword,
    Axe
}

public readonly record struct WorkpieceVisualProfile(
    ForgeWorkpieceKind Kind,
    float Length,
    float Width,
    float Bevel,
    float SideWallRatio,
    ShapeTemplateDefinition Shape,
    int DevelopmentStrikes);

public static class WorkpieceMeshBuilder
{
    public const int Columns = 96;
    public const int Rows = 32;
    public static readonly Matrix4x4 WorldTransform = Matrix4x4.CreateTranslation(0, .17f, 0);

    public static WorkpieceVisualProfile ProfileFor(string? recipeId, ShapeTemplateDefinition? shape = null)
    {
        shape ??= DefaultShapeFor(recipeId);
        var kind = shape.Kind.Equals("axe", StringComparison.OrdinalIgnoreCase) ||
                   shape.Kind.Contains("axe", StringComparison.OrdinalIgnoreCase)
            ? ForgeWorkpieceKind.Axe
            : shape.Kind.Contains("basilard", StringComparison.OrdinalIgnoreCase) ||
              shape.Kind.Equals("shortsword", StringComparison.OrdinalIgnoreCase)
                ? ForgeWorkpieceKind.Shortsword
                : ForgeWorkpieceKind.Longsword;
        var length = (float)(shape.Length * (kind == ForgeWorkpieceKind.Axe ? 3.08 : 3.45));
        var width = (float)(shape.Width * (kind == ForgeWorkpieceKind.Axe ? 1.24 : 1.10));
        var strikes = kind switch
        {
            ForgeWorkpieceKind.Longsword => 52,
            ForgeWorkpieceKind.Shortsword => 42,
            _ => 46
        };
        var bevel = kind == ForgeWorkpieceKind.Axe ? .014f : .009f;
        return new WorkpieceVisualProfile(kind, length, width, bevel, .72f, shape, strikes);
    }

    public static Vector3 LocalPoint(float x, float y, float height, string? recipeId, ShapeTemplateDefinition? shape = null)
    {
        var profile = ProfileFor(recipeId, shape);
        return new Vector3(
            (x - .5f) * profile.Length,
            height,
            (y - .5f) * profile.Width);
    }

    public static bool IsOccupiedAt(IReadOnlyList<ShapeCellSnapshot> cells, Vector2 lattice)
        => TrySnapToOccupied(cells, lattice, out _, searchRadius: 0);

    public static bool TrySnapToOccupied(
        IReadOnlyList<ShapeCellSnapshot> cells,
        Vector2 lattice,
        out Vector2 snapped,
        int searchRadius = 2) =>
        TrySnapToOccupied(cells, lattice, out snapped, searchRadius, searchRadius);

    public static bool TrySnapToOccupied(
        IReadOnlyList<ShapeCellSnapshot> cells,
        Vector2 lattice,
        out Vector2 snapped,
        int searchRadiusX,
        int searchRadiusY)
    {
        snapped = default;
        if (lattice.X is < 0 or > 1 || lattice.Y is < 0 or > 1) return false;
        var x = Math.Clamp((int)(lattice.X * Columns), 0, Columns - 1);
        var y = Math.Clamp((int)(lattice.Y * Rows), 0, Rows - 1);
        ShapeCellSnapshot? nearest = null;
        var nearestDistance = int.MaxValue;
        for (var i = 0; i < cells.Count; i++)
        {
            var cell = cells[i];
            if (cell.Occupancy <= .05) continue;
            var dx = cell.X - x;
            var dy = cell.Y - y;
            if (Math.Abs(dx) > searchRadiusX || Math.Abs(dy) > searchRadiusY) continue;
            var distance = dx * dx + dy * dy;
            if (distance >= nearestDistance) continue;
            nearest = cell;
            nearestDistance = distance;
        }
        if (nearest is null) return false;
        snapped = new Vector2(
            (nearest.X + .5f) / Columns,
            (nearest.Y + .5f) / Rows);
        return true;
    }

    public static MeshData Build(
        IReadOnlyList<ShapeCellSnapshot> cells,
        bool flipped,
        string? recipeId = null,
        int strikeCount = 0,
        ShapeTemplateDefinition? shape = null)
    {
        var profile = ProfileFor(recipeId, shape);
        _ = strikeCount;
        var source = CreateDenseCells();
        foreach (var cell in cells)
        {
            if ((uint)cell.X < Columns && (uint)cell.Y < Rows)
            {
                source[cell.X * Rows + cell.Y] = cell;
            }
        }

        // The dynamic workpiece deliberately uses one immutable topology for every
        // revision. Occupancy changes move a smooth loft envelope instead of adding
        // and removing lattice quads, so a billet can become a finished blade without
        // cracks, flashing cells or a model swap at the end of the process.
        const int sectionCount = Columns + 1;
        const int laneCount = Rows + 1;
        var (start, end) = LongitudinalExtent(source);
        var lower = new float[sectionCount];
        var upper = new float[sectionCount];
        var sectionFormation = new float[sectionCount];
        for (var section = 0; section < sectionCount; section++)
        {
            var u = Lerp(start, end, section / (float)Columns);
            (lower[section], upper[section], sectionFormation[section]) = SectionEnvelope(source, profile, u);
        }
        SmoothEnvelope(lower, upper);

        var topPositions = new Vector3[sectionCount * laneCount];
        var bottomPositions = new Vector3[sectionCount * laneCount];
        for (var section = 0; section < sectionCount; section++)
        {
            var u = Lerp(start, end, section / (float)Columns);
            for (var lane = 0; lane < laneCount; lane++)
            {
                var across = lane / (float)Rows;
                var signed = Lerp(lower[section], upper[section], across);
                if (flipped) signed = -signed;
                var v = Math.Clamp(signed + .5f, 0, 1);
                var localFormation = Math.Max(sectionFormation[section], SampleField(source, u, v, CellField.Formation));
                var halfHeight = CalculateHalfHeight(source, profile, u, v, across, localFormation);

                var x = (u - .5f) * profile.Length;
                var z = signed * profile.Width;
                var index = section * laneCount + lane;
                topPositions[index] = new Vector3(x, halfHeight, z);
                bottomPositions[index] = new Vector3(x, -halfHeight, z);
            }
        }

        var mesh = new MeshBuilder();
        var topVertices = new uint[sectionCount * laneCount];
        var bottomVertices = new uint[sectionCount * laneCount];
        for (var section = 0; section < sectionCount; section++)
        {
            var u = Lerp(start, end, section / (float)Columns);
            var surfaceMaterial = profile.Kind == ForgeWorkpieceKind.Axe &&
                                  u <= .30f &&
                                  sectionFormation[section] >= .38f
                ? (int)ForgeMaterialId.WorkpieceEdge
                : (int)ForgeMaterialId.Workpiece;
            for (var lane = 0; lane < laneCount; lane++)
            {
                var index = section * laneCount + lane;
                var tangent = SurfaceTangent(topPositions, section, lane, sectionCount, laneCount);
                var topNormal = SurfaceNormal(topPositions, section, lane, sectionCount, laneCount, top: true);
                var bottomNormal = SurfaceNormal(bottomPositions, section, lane, sectionCount, laneCount, top: false);
                var uv = new Vector2(section / (float)Columns, lane / (float)Rows);
                topVertices[index] = mesh.AddVertex(topPositions[index], topNormal, new Vector4(tangent, 1), uv, surfaceMaterial);
                bottomVertices[index] = mesh.AddVertex(bottomPositions[index], bottomNormal, new Vector4(tangent, -1), uv, surfaceMaterial);
            }
        }

        for (var section = 0; section < Columns; section++)
        {
            for (var lane = 0; lane < Rows; lane++)
            {
                var i00 = section * laneCount + lane;
                var i10 = (section + 1) * laneCount + lane;
                var i11 = i10 + 1;
                var i01 = i00 + 1;
                mesh.AddTriangle(topVertices[i00], topVertices[i01], topVertices[i11]);
                mesh.AddTriangle(topVertices[i00], topVertices[i11], topVertices[i10]);
                mesh.AddTriangle(bottomVertices[i00], bottomVertices[i11], bottomVertices[i01]);
                mesh.AddTriangle(bottomVertices[i00], bottomVertices[i10], bottomVertices[i11]);
            }
        }

        for (var section = 0; section < Columns; section++)
        {
            var next = section + 1;
            AddSide(section, next, 0, flipped ? Vector3.UnitZ : -Vector3.UnitZ);
            AddSide(section, next, Rows, flipped ? -Vector3.UnitZ : Vector3.UnitZ);
        }
        for (var lane = 0; lane < Rows; lane++)
        {
            AddEnd(0, lane, lane + 1, -Vector3.UnitX);
            AddEnd(Columns, lane, lane + 1, Vector3.UnitX);
        }
        return mesh.Build();

        void AddSide(int firstSection, int secondSection, int lane, Vector3 outward)
        {
            var a = firstSection * laneCount + lane;
            var b = secondSection * laneCount + lane;
            mesh.AddQuadOriented(
                bottomPositions[a], bottomPositions[b], topPositions[b], topPositions[a],
                outward, (int)ForgeMaterialId.WorkpieceEdge, new Vector2(1, .18f));
        }

        void AddEnd(int section, int firstLane, int secondLane, Vector3 outward)
        {
            var a = section * laneCount + firstLane;
            var b = section * laneCount + secondLane;
            mesh.AddQuadOriented(
                bottomPositions[a], topPositions[a], topPositions[b], bottomPositions[b],
                outward, (int)ForgeMaterialId.WorkpieceEdge, new Vector2(.18f, 1));
        }
    }

    private static ShapeCellSnapshot[] CreateDenseCells()
    {
        var result = new ShapeCellSnapshot[Columns * Rows];
        for (var x = 0; x < Columns; x++)
        for (var y = 0; y < Rows; y++)
        {
            result[x * Rows + y] = new ShapeCellSnapshot(x, y, 0, 0, 0, 0, 0);
        }
        return result;
    }

    public static float SurfaceHeightAt(
        IReadOnlyList<ShapeCellSnapshot> cells,
        Vector2 lattice,
        string? recipeId = null,
        ShapeTemplateDefinition? shape = null)
    {
        var profile = ProfileFor(recipeId, shape);
        var source = CreateDenseCells();
        foreach (var cell in cells)
        {
            if ((uint)cell.X < Columns && (uint)cell.Y < Rows)
            {
                source[cell.X * Rows + cell.Y] = cell;
            }
        }

        var u = Math.Clamp(lattice.X, 0, 1);
        var v = Math.Clamp(lattice.Y, 0, 1);
        var (lower, upper, sectionFormation) = SectionEnvelope(source, profile, u);
        var across = Math.Clamp((v - .5f - lower) / Math.Max(.001f, upper - lower), 0, 1);
        var localFormation = Math.Max(sectionFormation, SampleField(source, u, v, CellField.Formation));
        return CalculateHalfHeight(source, profile, u, v, across, localFormation);
    }

    private static float CalculateHalfHeight(
        ShapeCellSnapshot[] source,
        WorkpieceVisualProfile profile,
        float u,
        float v,
        float across,
        float localFormation)
    {
        var measuredThickness = SampleField(source, u, v, CellField.Thickness);
        var damage = SampleField(source, u, v, CellField.Damage);
        var billetHeight = InitialBilletHalfHeight(profile, u, across);
        var measuredHeight = profile.Kind == ForgeWorkpieceKind.Axe
            ? .020f + measuredThickness * .052f
            : .008f + measuredThickness * .021f;
        var development = SmoothStep(.02f, .96f, localFormation);
        var halfHeight = Lerp(billetHeight, measuredHeight, development);
        halfHeight *= ForgedCrossSectionScale(profile, u, across, development);
        halfHeight += ForgedRidge(profile, u, across, development);
        halfHeight += ForgedSurfaceOffset(profile.Kind, u, across) * development * .22f;
        return Math.Max(.0085f, halfHeight - damage * .0035f);
    }

    private static (float Start, float End) LongitudinalExtent(ShapeCellSnapshot[] source)
    {
        var total = 0f;
        var mean = 0f;
        for (var x = 0; x < Columns; x++)
        {
            var mass = 0f;
            for (var y = 0; y < Rows; y++) mass += Math.Max(0, (float)source[x * Rows + y].Occupancy);
            var weight = MathF.Pow(mass, .72f);
            var u = (x + .5f) / Columns;
            total += weight;
            mean += u * weight;
        }
        if (total < 1e-5f) return (.45f, .55f);
        mean /= total;
        var variance = 0f;
        for (var x = 0; x < Columns; x++)
        {
            var mass = 0f;
            for (var y = 0; y < Rows; y++) mass += Math.Max(0, (float)source[x * Rows + y].Occupancy);
            var weight = MathF.Pow(mass, .72f);
            var distance = (x + .5f) / Columns - mean;
            variance += distance * distance * weight;
        }
        var half = MathF.Sqrt(Math.Max(0, 3 * variance / total)) + .45f / Columns;
        return (Math.Clamp(mean - half, 0, .985f), Math.Clamp(mean + half, .015f, 1));
    }

    private static (float Lower, float Upper, float Formation) SectionEnvelope(
        ShapeCellSnapshot[] source,
        WorkpieceVisualProfile profile,
        float u)
    {
        var total = 0f;
        var center = 0f;
        var formation = 0f;
        for (var y = 0; y < Rows; y++)
        {
            var v = (y + .5f) / Rows;
            var occupancy = Math.Max(0, SampleField(source, u, v, CellField.Occupancy));
            var weight = MathF.Pow(occupancy, .62f);
            total += weight;
            center += (v - .5f) * weight;
            formation = Math.Max(formation, SampleField(source, u, v, CellField.Formation));
        }

        var target = ForgeShapeProfileSampler.BoundsAt(profile.Shape, u);
        if (total < 1e-5f)
        {
            var targetCenter = (float)((target.Lower + target.Upper) * .5);
            return (targetCenter - .008f, targetCenter + .008f, formation);
        }

        center /= total;
        var variance = 0f;
        for (var y = 0; y < Rows; y++)
        {
            var v = (y + .5f) / Rows;
            var occupancy = Math.Max(0, SampleField(source, u, v, CellField.Occupancy));
            var weight = MathF.Pow(occupancy, .62f);
            var distance = v - .5f - center;
            variance += distance * distance * weight;
        }
        var half = MathF.Sqrt(Math.Max(0, 3 * variance / total)) + .46f / Rows;
        var measuredLower = center - half;
        var measuredUpper = center + half;
        var targetBlend = SmoothStep(.12f, .96f, formation) * .92f;
        return (
            Lerp(measuredLower, (float)target.Lower, targetBlend),
            Lerp(measuredUpper, (float)target.Upper, targetBlend),
            formation);
    }

    private static void SmoothEnvelope(float[] lower, float[] upper)
    {
        var lowerCopy = (float[])lower.Clone();
        var upperCopy = (float[])upper.Clone();
        for (var i = 1; i < lower.Length - 1; i++)
        {
            lower[i] = lowerCopy[i - 1] * .18f + lowerCopy[i] * .64f + lowerCopy[i + 1] * .18f;
            upper[i] = upperCopy[i - 1] * .18f + upperCopy[i] * .64f + upperCopy[i + 1] * .18f;
            if (upper[i] - lower[i] < .012f)
            {
                var center = (upper[i] + lower[i]) * .5f;
                lower[i] = center - .006f;
                upper[i] = center + .006f;
            }
        }
    }

    private static float SampleField(ShapeCellSnapshot[] source, float u, float v, CellField field)
    {
        var x = Math.Clamp(u, 0, 1) * (Columns - 1);
        var y = Math.Clamp(v, 0, 1) * (Rows - 1);
        var x0 = Math.Clamp((int)MathF.Floor(x), 0, Columns - 1);
        var y0 = Math.Clamp((int)MathF.Floor(y), 0, Rows - 1);
        var x1 = Math.Min(Columns - 1, x0 + 1);
        var y1 = Math.Min(Rows - 1, y0 + 1);
        var tx = x - x0;
        var ty = y - y0;
        var a = CellValue(source[x0 * Rows + y0], field);
        var b = CellValue(source[x1 * Rows + y0], field);
        var c = CellValue(source[x0 * Rows + y1], field);
        var d = CellValue(source[x1 * Rows + y1], field);
        return Lerp(Lerp(a, b, tx), Lerp(c, d, tx), ty);
    }

    private static float CellValue(ShapeCellSnapshot cell, CellField field) => field switch
    {
        CellField.Occupancy => (float)cell.Occupancy,
        CellField.Thickness => (float)cell.Thickness,
        CellField.Damage => (float)cell.Damage,
        _ => (float)cell.Formation
    };

    private static float ForgedCrossSectionScale(
        WorkpieceVisualProfile profile,
        float u,
        float across,
        float development)
    {
        var edge = Math.Abs(across * 2 - 1);
        if (profile.Kind == ForgeWorkpieceKind.Axe)
        {
            var cuttingZone = 1 - SmoothStep(.10f, .36f, u);
            var cheekCrown = 1 + (1 - edge * edge) * .10f * development;
            var cuttingWedge = 1 - cuttingZone * development * SmoothStep(.32f, 1, edge) * .78f;
            return Math.Max(.15f, cheekCrown * cuttingWedge);
        }

        var bladeStart = profile.Kind == ForgeWorkpieceKind.Shortsword ? .20f : .16f;
        var blade = SmoothStep(bladeStart, bladeStart + .10f, u);
        var wedge = 1 - SmoothStep(.42f, 1, edge) * blade * development * .87f;
        var point = 1 - SmoothStep(.78f, 1, u) * blade * development * .34f;
        return Math.Max(.10f, wedge * point);
    }

    private static float ForgedRidge(WorkpieceVisualProfile profile, float u, float across, float development)
    {
        var center = 1 - Math.Abs(across * 2 - 1);
        if (profile.Kind == ForgeWorkpieceKind.Axe)
        {
            var eyeCheek = SmoothStep(.54f, .70f, u) * (1 - SmoothStep(.82f, .94f, u));
            return MathF.Pow(center, 2.2f) * eyeCheek * development * .008f;
        }
        var bladeStart = profile.Kind == ForgeWorkpieceKind.Shortsword ? .20f : .16f;
        var blade = SmoothStep(bladeStart, bladeStart + .10f, u);
        return MathF.Pow(center, 2.8f) * blade * development * (profile.Kind == ForgeWorkpieceKind.Shortsword ? .0045f : .0055f);
    }

    private static Vector3 SurfaceTangent(
        Vector3[] positions,
        int section,
        int lane,
        int sectionCount,
        int laneCount)
    {
        var previous = Math.Max(0, section - 1) * laneCount + lane;
        var next = Math.Min(sectionCount - 1, section + 1) * laneCount + lane;
        var tangent = positions[next] - positions[previous];
        return tangent.LengthSquared() < 1e-8f ? Vector3.UnitX : Vector3.Normalize(tangent);
    }

    private static Vector3 SurfaceNormal(
        Vector3[] positions,
        int section,
        int lane,
        int sectionCount,
        int laneCount,
        bool top)
    {
        var left = Math.Max(0, section - 1) * laneCount + lane;
        var right = Math.Min(sectionCount - 1, section + 1) * laneCount + lane;
        var down = section * laneCount + Math.Max(0, lane - 1);
        var up = section * laneCount + Math.Min(laneCount - 1, lane + 1);
        var longitudinal = positions[right] - positions[left];
        var transverse = positions[up] - positions[down];
        var normal = Vector3.Cross(transverse, longitudinal);
        if (!top) normal = -normal;
        if (normal.LengthSquared() < 1e-8f) return top ? Vector3.UnitY : -Vector3.UnitY;
        normal = Vector3.Normalize(normal);
        if (top && normal.Y < 0 || !top && normal.Y > 0) normal = -normal;
        return normal;
    }

    private enum CellField
    {
        Occupancy,
        Thickness,
        Damage,
        Formation
    }

    private static Vector3 WithHeight(Vector3 horizontal, float height) => new(horizontal.X, height, horizontal.Z);

    private static Vector3 CornerPosition(
        ShapeCellSnapshot?[] source,
        int cornerX,
        int cornerY,
        bool flipped,
        WorkpieceVisualProfile profile)
    {
        var x = -profile.Length / 2 + cornerX / (float)Columns * profile.Length;
        var smoothedBoundary = TrySmoothedOuterBoundary(source, cornerX, cornerY, out var boundaryRow);
        var row = flipped
            ? Rows - (smoothedBoundary ? boundaryRow : cornerY)
            : smoothedBoundary ? boundaryRow : cornerY;
        var z = -profile.Width / 2 + row / (float)Rows * profile.Width;
        if (smoothedBoundary)
        {
            var upper = boundaryRow >= Rows * .5f;
            if (flipped) upper = !upper;
            var targetZ = AnalyticOuterBoundary(profile, cornerX / (float)Columns, upper);
            z = Lerp(z, targetZ, CornerFormation(source, cornerX, cornerY));
        }
        var count = 0;
        for (var cx = cornerX - 1; cx <= cornerX; cx++)
        for (var cy = cornerY - 1; cy <= cornerY; cy++)
        {
            if (!Occupied(source, cx, cy)) continue;
            count++;
        }
        var eyePoint = Vector3.Zero;
        var eyeBoundary = profile.Kind == ForgeWorkpieceKind.Axe &&
                          !smoothedBoundary &&
                          count is > 0 and < 4 &&
                          TryAnalyticEyeBoundary(profile, new Vector3(x, 0, z), out eyePoint, out _);
        if (eyeBoundary)
        {
            var eyeFormation = CornerFormation(source, cornerX, cornerY);
            x = Lerp(x, eyePoint.X, eyeFormation);
            z = Lerp(z, eyePoint.Z, eyeFormation);
        }
        // Do not pull an isolated lattice corner toward its single occupied cell.
        // That shortcut made partially formed edges zig-zag as each hammer strike
        // crossed a new cell. Boundary smoothing above already supplies the correct
        // continuous contour, while beveling handles the remaining exposed corner.
        return new Vector3(x, 0, z);
    }

    private static bool TryAnalyticEyeBoundary(
        WorkpieceVisualProfile profile,
        Vector3 point,
        out Vector3 boundary,
        out Vector3 awayFromEye)
    {
        boundary = point;
        awayFromEye = Vector3.Zero;
        if (profile.Kind != ForgeWorkpieceKind.Axe ||
            profile.Shape.EyeWidth <= 0 ||
            profile.Shape.EyeHeight <= 0) return false;
        var shape = profile.Shape;
        var center = new Vector3(((float)shape.EyeCenterX - .5f) * profile.Length, 0, (float)shape.EyeCenterY * profile.Width);
        var radiusX = Math.Max(.001f, (float)shape.EyeWidth * .5f * profile.Length);
        var radiusZ = Math.Max(.001f, (float)shape.EyeHeight * .5f * profile.Width);
        var normalizedX = (point.X - center.X) / radiusX;
        var normalizedZ = (point.Z - center.Z) / radiusZ;
        var radial = MathF.Sqrt(normalizedX * normalizedX + normalizedZ * normalizedZ);
        if (radial is < .55f or > 1.50f) return false;
        var angle = MathF.Atan2(normalizedZ, normalizedX);
        boundary = new Vector3(
            center.X + MathF.Cos(angle) * radiusX,
            0,
            center.Z + MathF.Sin(angle) * radiusZ);
        awayFromEye = Vector3.Normalize(new Vector3(
            (boundary.X - center.X) / (radiusX * radiusX),
            0,
            (boundary.Z - center.Z) / (radiusZ * radiusZ)));
        return true;
    }

    private static float AnalyticOuterBoundary(WorkpieceVisualProfile profile, float u, bool upper)
    {
        var bounds = ForgeShapeProfileSampler.BoundsAt(profile.Shape, u);
        return (float)(upper ? bounds.Upper : bounds.Lower) * profile.Width;
    }

    private static float SmoothStep(float edge0, float edge1, float value)
    {
        var amount = Math.Clamp((value - edge0) / Math.Max(1e-6f, edge1 - edge0), 0, 1);
        return amount * amount * (3 - 2 * amount);
    }

    private static float Lerp(float first, float second, float amount) => first + (second - first) * amount;

    private static ShapeTemplateDefinition DefaultShapeFor(string? recipeId) => recipeId?.ToLowerInvariant() switch
    {
        "basilard" or "shortsword" => new("basilard", .74, .92, .94, .18, .34),
        "bearded-axe" or "axe" => new("bearded-axe", .72, 1, 1.12, .13, .30, 0, 0, EyeCenterX: .72, EyeCenterY: .015),
        _ => new("duelling-longsword", 1, .82, .96, .13, .56)
    };

    private static bool TrySmoothedOuterBoundary(
        ShapeCellSnapshot?[] source,
        int cornerX,
        int cornerY,
        out float boundaryRow)
    {
        var lowerTotal = 0f;
        var upperTotal = 0f;
        var weightTotal = 0f;
        for (var x = cornerX - 2; x <= cornerX + 1; x++)
        {
            if (x < 0 || x >= Columns) continue;
            var lower = Rows;
            var upper = -1;
            for (var y = 0; y < Rows; y++)
            {
                if (!Occupied(source, x, y)) continue;
                lower = Math.Min(lower, y);
                upper = Math.Max(upper, y);
            }

            if (upper < lower) continue;
            var distance = Math.Abs((x + .5f) - cornerX);
            var weight = distance < .75f ? 2f : 1f;
            lowerTotal += lower * weight;
            upperTotal += (upper + 1) * weight;
            weightTotal += weight;
        }

        if (weightTotal <= 0)
        {
            boundaryRow = cornerY;
            return false;
        }

        var lowerBoundary = lowerTotal / weightTotal;
        var upperBoundary = upperTotal / weightTotal;
        var lowerDistance = Math.Abs(cornerY - lowerBoundary);
        var upperDistance = Math.Abs(cornerY - upperBoundary);
        if (Math.Min(lowerDistance, upperDistance) > 1.35f)
        {
            boundaryRow = cornerY;
            return false;
        }

        boundaryRow = lowerDistance <= upperDistance ? lowerBoundary : upperBoundary;
        return true;
    }

    private static Vector3 SurfaceCornerPosition(
        ShapeCellSnapshot?[] source,
        int cornerX,
        int cornerY,
        bool flipped,
        WorkpieceVisualProfile profile)
    {
        var outer = CornerPosition(source, cornerX, cornerY, flipped, profile);
        if (profile.Bevel <= 1e-5f) return outer;
        var materialOutward = Vector3.Zero;
        if (TrySmoothedOuterBoundary(source, cornerX, cornerY, out var boundaryRow))
        {
            var upper = boundaryRow >= Rows * .5f;
            if (flipped) upper = !upper;
            materialOutward += AnalyticOuterNormal(profile, cornerX / (float)Columns, upper);
        }

        if (TryAnalyticEyeBoundary(profile, outer, out _, out var awayFromEye))
        {
            materialOutward -= awayFromEye;
        }

        var hasLeft = HasOccupiedCornerSide(source, cornerX - 1, cornerY);
        var hasRight = HasOccupiedCornerSide(source, cornerX, cornerY);
        if (!hasLeft && hasRight) materialOutward -= Vector3.UnitX;
        if (hasLeft && !hasRight) materialOutward += Vector3.UnitX;
        if (materialOutward.LengthSquared() > 1e-8f)
        {
            return outer - Vector3.Normalize(materialOutward) * profile.Bevel;
        }

        var center = Vector3.Zero;
        var count = 0;
        for (var x = cornerX - 1; x <= cornerX; x++)
        for (var y = cornerY - 1; y <= cornerY; y++)
        {
            if (!Occupied(source, x, y)) continue;
            var visualRow = flipped ? Rows - (y + .5f) : y + .5f;
            center += new Vector3(
                -profile.Length * .5f + (x + .5f) / Columns * profile.Length,
                0,
                -profile.Width * .5f + visualRow / Rows * profile.Width);
            count++;
        }

        if (count is 0 or 4) return outer;
        var inward = center / count - outer;
        inward.Y = 0;
        return inward.LengthSquared() < 1e-8f
            ? outer
            : outer + Vector3.Normalize(inward) * profile.Bevel;
    }

    private static Vector3 AnalyticOuterNormal(WorkpieceVisualProfile profile, float u, bool upper)
    {
        const float sample = 1f / Columns;
        var u0 = Math.Max(0, u - sample);
        var u1 = Math.Min(1, u + sample);
        var tangent = new Vector3(
            (u1 - u0) * profile.Length,
            0,
            AnalyticOuterBoundary(profile, u1, upper) - AnalyticOuterBoundary(profile, u0, upper));
        var outward = upper
            ? new Vector3(-tangent.Z, 0, tangent.X)
            : new Vector3(tangent.Z, 0, -tangent.X);
        return outward.LengthSquared() < 1e-8f
            ? (upper ? Vector3.UnitZ : -Vector3.UnitZ)
            : Vector3.Normalize(outward);
    }

    private static bool HasOccupiedCornerSide(ShapeCellSnapshot?[] source, int cellX, int cornerY) =>
        Occupied(source, cellX, cornerY - 1) || Occupied(source, cellX, cornerY);

    private static bool Occupied(ShapeCellSnapshot?[] source, int x, int y) =>
        x >= 0 && x < Columns && y >= 0 && y < Rows && source[x * Rows + y] is not null;

    private static float Height(ShapeCellSnapshot?[] source, int x, int y) =>
        Occupied(source, x, y) ? (float)source[x * Rows + y]!.Thickness : 0;

    private static float CornerHalfHeight(
        ShapeCellSnapshot?[] source,
        int cornerX,
        int cornerY,
        WorkpieceVisualProfile profile)
    {
        var total = 0f;
        var count = 0;
        for (var x = cornerX - 1; x <= cornerX; x++)
        for (var y = cornerY - 1; y <= cornerY; y++)
        {
            if (!Occupied(source, x, y)) continue;
            total += .034f + (float)source[x * Rows + y]!.Thickness * .095f;
            count++;
        }
        if (count == 0) return .034f;

        var forgedHeight = total / count;
        var u = Math.Clamp(cornerX / (float)Columns, 0, 1);
        var v = Math.Clamp(cornerY / (float)Rows, 0, 1);
        var localFormation = CornerFormation(source, cornerX, cornerY);
        // A billet starts as a continuous rolled bar. Blend into measured cell
        // thickness only as that region is actually forged; using the target
        // profile from frame zero was the source of the old finished-looking bar.
        var billetHeight = InitialBilletHalfHeight(profile, u, v);
        var formedHeight = Lerp(billetHeight, forgedHeight, localFormation);
        formedHeight += ForgedSurfaceOffset(profile.Kind, u, v) * (localFormation * .35f);
        var edgeScale = ForgedEdgeScale(profile, u, v, localFormation);
        return Math.Max(.014f, formedHeight * edgeScale);
    }

    private static float CornerFormation(ShapeCellSnapshot?[] source, int cornerX, int cornerY)
    {
        var total = 0f;
        var count = 0;
        for (var x = cornerX - 1; x <= cornerX; x++)
        for (var y = cornerY - 1; y <= cornerY; y++)
        {
            if (!Occupied(source, x, y)) continue;
            total += (float)source[x * Rows + y]!.Formation;
            count++;
        }
        return count == 0 ? 0 : Math.Clamp(total / count, 0, 1);
    }

    private static float ForgedEdgeScale(WorkpieceVisualProfile profile, float u, float v, float development)
    {
        var formed = SmoothStep(.16f, .92f, development);
        if (profile.Kind == ForgeWorkpieceKind.Axe)
        {
            // A bearded axe is a single-bevel wedge at the cutting edge. The poll,
            // beard perimeter and eye stay full thickness instead of gaining a rim.
            var cuttingEdge = 1 - SmoothStep(0, .28f, u);
            return 1 - cuttingEdge * formed * .84f;
        }

        var bounds = ForgeShapeProfileSampler.BoundsAt(profile.Shape, u);
        var center = (float)((bounds.Lower + bounds.Upper) * .5);
        var halfWidth = Math.Max(.004f, (float)((bounds.Upper - bounds.Lower) * .5));
        var signed = v - .5f;
        var across = Math.Clamp(Math.Abs(signed - center) / halfWidth, 0, 1);
        var bladeStart = profile.Kind == ForgeWorkpieceKind.Shortsword ? .20f : .17f;
        var blade = SmoothStep(bladeStart, bladeStart + .10f, u);
        var cuttingEdges = SmoothStep(.56f, 1, across) * blade;
        var tip = SmoothStep(.82f, 1, u) * blade;
        var scale = 1 - cuttingEdges * formed * .86f;
        scale *= 1 - tip * formed * .42f;
        return Math.Max(.10f, scale);
    }

    private static float InitialBilletHalfHeight(WorkpieceVisualProfile profile, float u, float v)
    {
        var across = Math.Abs(v - .5f) * 2;
        return profile.Kind switch
        {
            ForgeWorkpieceKind.Longsword =>
                (.082f - SmoothStep(.72f, 1, u) * .014f) *
                (u < .18f ? .82f : 1) *
                (1 - SmoothStep(.62f, 1, across) * .26f),
            ForgeWorkpieceKind.Shortsword =>
                (.090f - SmoothStep(.68f, 1, u) * .016f) *
                (u < .22f ? .86f : 1) *
                (1 - SmoothStep(.58f, 1, across) * .24f),
            _ =>
                (.178f - SmoothStep(0, .20f, u) * .026f - SmoothStep(.76f, 1, u) * .020f) *
                (1 - SmoothStep(.58f, 1, across) * .20f)
        };
    }

    private static float ForgedSurfaceOffset(ForgeWorkpieceKind kind, float u, float v)
    {
        var phase = kind switch
        {
            ForgeWorkpieceKind.Longsword => .7f,
            ForgeWorkpieceKind.Shortsword => 2.1f,
            _ => 3.6f
        };
        var broad = MathF.Sin(u * 7.4f + phase) * MathF.Sin(v * 3.1f - phase * .4f);
        var secondary = MathF.Sin(u * 17.1f - v * 5.9f + phase) * .18f;
        return (broad + secondary) * (kind == ForgeWorkpieceKind.Axe ? .0018f : .00115f);
    }

    private static Vector3 CornerNormal(
        ShapeCellSnapshot?[] source,
        int cornerX,
        int cornerY,
        bool flipped,
        WorkpieceVisualProfile profile)
    {
        var dx = profile.Length / Columns;
        var dz = profile.Width / Rows;
        var left = CornerHalfHeight(source, cornerX - 1, cornerY, profile);
        var right = CornerHalfHeight(source, cornerX + 1, cornerY, profile);
        var down = CornerHalfHeight(source, cornerX, cornerY - 1, profile);
        var up = CornerHalfHeight(source, cornerX, cornerY + 1, profile);
        var dhdx = (right - left) / (2 * dx);
        var dhdz = (up - down) / (2 * dz) * (flipped ? -1 : 1);
        return Vector3.Normalize(new Vector3(-dhdx, 1, -dhdz));
    }
}
