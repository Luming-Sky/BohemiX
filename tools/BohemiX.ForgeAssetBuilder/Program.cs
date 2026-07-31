using System.Numerics;
using System.Text.Json;
using BohemiX.Modules.Forge.Data;
using BohemiX.Modules.Forge.Engine;
using BohemiX.Modules.Forge.Rendering;

if (args.Length > 0 && args[0].Equals("--billets", StringComparison.OrdinalIgnoreCase))
{
    var billetOutput = args.Length > 1
        ? Path.GetFullPath(args[1])
        : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "billet-preview"));
    Directory.CreateDirectory(billetOutput);
    var catalog = new ForgeCatalog();
    foreach (var recipe in catalog.Recipes)
    {
        var shape = new LatticeShapeSimulation();
        shape.Reset(recipe.Shape);
        var mesh = WorkpieceMeshBuilder.Build(shape.SnapshotCells(), flipped: false, recipe.Id, strikeCount: 0);
        var path = Path.Combine(billetOutput, $"billet-{recipe.Id}.glb");
        ForgeGlbWriter.Write(path, $"billet-{recipe.Id}", mesh);
        Console.WriteLine($"{recipe.Id}: {mesh.Vertices.Length:N0} vertices, {mesh.Indices.Length / 3:N0} triangles -> {path}");
    }
    return;
}

var output = args.Length > 0
    ? Path.GetFullPath(args[0])
    : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "BohemiX.Modules.Forge", "Assets", "Models"));
Directory.CreateDirectory(output);

var assets = new (string Name, MeshData Mesh)[]
{
    ("workbench", ProceduralMeshFactory.CreateWorkbench()),
    ("anvil", ProceduralMeshFactory.CreateAnvil()),
    ("hearth", ProceduralMeshFactory.CreateHearth()),
    ("bellows", ProceduralMeshFactory.CreateBellows()),
    ("hammer", ProceduralMeshFactory.CreateHammer()),
    ("tongs", ProceduralMeshFactory.CreateTongs()),
    ("quench-water", ProceduralMeshFactory.CreateQuenchVat(false)),
    ("quench-oil", ProceduralMeshFactory.CreateQuenchVat(true)),
    ("grinder", ProceduralMeshFactory.CreateGrinder()),
    ("grinder-stand", ProceduralMeshFactory.CreateGrinderStand()),
    ("grinder-wheel", ProceduralMeshFactory.CreateGrinderWheel())
};

foreach (var asset in assets)
{
    var path = Path.Combine(output, asset.Name + ".glb");
    ForgeGlbWriter.Write(path, asset.Name, asset.Mesh);
    Console.WriteLine($"{asset.Name}: {asset.Mesh.Vertices.Length:N0} vertices, {asset.Mesh.Indices.Length / 3:N0} triangles -> {path}");
}

internal static class ForgeGlbWriter
{
    private const uint JsonChunk = 0x4E4F534A;
    private const uint BinChunk = 0x004E4942;
    private const int VertexStride = 48;

    public static void Write(string path, string name, MeshData mesh)
    {
        var materialGroups = GroupIndices(mesh);
        using var binary = new MemoryStream();
        using (var writer = new BinaryWriter(binary, System.Text.Encoding.UTF8, true))
        {
            foreach (var vertex in mesh.Vertices)
            {
                Write(writer, vertex.Position);
                Write(writer, vertex.Normal);
                Write(writer, vertex.Tangent);
                Write(writer, vertex.TexCoord);
            }
            Align(binary, 4, 0);
            foreach (var group in materialGroups)
            {
                group.ByteOffset = checked((int)binary.Position);
                foreach (var index in group.Indices) writer.Write(index);
                Align(binary, 4, 0);
            }
        }

        var json = BuildJson(name, mesh, materialGroups, checked((int)binary.Length));
        using var jsonStream = new MemoryStream();
        jsonStream.Write(json);
        Align(jsonStream, 4, 0x20);
        using var file = File.Create(path);
        using var output = new BinaryWriter(file);
        output.Write(0x46546C67u);
        output.Write(2u);
        output.Write(checked((uint)(12 + 8 + jsonStream.Length + 8 + binary.Length)));
        output.Write(checked((uint)jsonStream.Length));
        output.Write(JsonChunk);
        jsonStream.Position = 0;
        jsonStream.CopyTo(file);
        output.Write(checked((uint)binary.Length));
        output.Write(BinChunk);
        binary.Position = 0;
        binary.CopyTo(file);
    }

    private static byte[] BuildJson(string name, MeshData mesh, IReadOnlyList<MaterialGroup> groups, int binaryLength)
    {
        using var stream = new MemoryStream();
        using var json = new Utf8JsonWriter(stream);
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var vertex in mesh.Vertices)
        {
            min = Vector3.Min(min, vertex.Position);
            max = Vector3.Max(max, vertex.Position);
        }

        json.WriteStartObject();
        json.WriteStartObject("asset"); json.WriteString("version", "2.0"); json.WriteString("generator", "BohemiX ForgeAssetBuilder"); json.WriteEndObject();
        json.WriteStartArray("buffers"); json.WriteStartObject(); json.WriteNumber("byteLength", binaryLength); json.WriteEndObject(); json.WriteEndArray();
        json.WriteStartArray("bufferViews");
        json.WriteStartObject(); json.WriteNumber("buffer", 0); json.WriteNumber("byteOffset", 0); json.WriteNumber("byteLength", mesh.Vertices.Length * VertexStride); json.WriteNumber("byteStride", VertexStride); json.WriteNumber("target", 34962); json.WriteEndObject();
        foreach (var group in groups)
        {
            json.WriteStartObject(); json.WriteNumber("buffer", 0); json.WriteNumber("byteOffset", group.ByteOffset); json.WriteNumber("byteLength", group.Indices.Count * sizeof(uint)); json.WriteNumber("target", 34963); json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteStartArray("accessors");
        WriteAccessor(json, 0, 0, 5126, mesh.Vertices.Length, "VEC3", min, max);
        WriteAccessor(json, 0, 12, 5126, mesh.Vertices.Length, "VEC3");
        WriteAccessor(json, 0, 24, 5126, mesh.Vertices.Length, "VEC4");
        WriteAccessor(json, 0, 40, 5126, mesh.Vertices.Length, "VEC2");
        for (var i = 0; i < groups.Count; i++) WriteAccessor(json, i + 1, 0, 5125, groups[i].Indices.Count, "SCALAR");
        json.WriteEndArray();

        var library = new MaterialLibrary();
        json.WriteStartArray("materials");
        foreach (var id in Enum.GetValues<ForgeMaterialId>())
        {
            var material = library[id];
            json.WriteStartObject();
            json.WriteString("name", id.ToString());
            json.WriteStartObject("pbrMetallicRoughness");
            json.WriteStartArray("baseColorFactor"); json.WriteNumberValue(material.Albedo.X); json.WriteNumberValue(material.Albedo.Y); json.WriteNumberValue(material.Albedo.Z); json.WriteNumberValue(1); json.WriteEndArray();
            json.WriteNumber("metallicFactor", material.Metallic); json.WriteNumber("roughnessFactor", material.Roughness);
            json.WriteEndObject();
            json.WriteEndObject();
        }
        json.WriteEndArray();

        json.WriteStartArray("meshes"); json.WriteStartObject(); json.WriteString("name", name); json.WriteStartArray("primitives");
        for (var i = 0; i < groups.Count; i++)
        {
            json.WriteStartObject();
            json.WriteStartObject("attributes"); json.WriteNumber("POSITION", 0); json.WriteNumber("NORMAL", 1); json.WriteNumber("TANGENT", 2); json.WriteNumber("TEXCOORD_0", 3); json.WriteEndObject();
            json.WriteNumber("indices", 4 + i); json.WriteNumber("material", groups[i].Material); json.WriteNumber("mode", 4);
            json.WriteEndObject();
        }
        json.WriteEndArray(); json.WriteEndObject(); json.WriteEndArray();
        json.WriteStartArray("nodes"); json.WriteStartObject(); json.WriteString("name", name); json.WriteNumber("mesh", 0); json.WriteEndObject(); json.WriteEndArray();
        json.WriteStartArray("scenes"); json.WriteStartObject(); json.WriteStartArray("nodes"); json.WriteNumberValue(0); json.WriteEndArray(); json.WriteEndObject(); json.WriteEndArray();
        json.WriteNumber("scene", 0);
        json.WriteEndObject();
        json.Flush();
        return stream.ToArray();
    }

    private static List<MaterialGroup> GroupIndices(MeshData mesh)
    {
        var groups = new SortedDictionary<int, MaterialGroup>();
        for (var i = 0; i < mesh.Indices.Length; i += 3)
        {
            var material = (int)mesh.Vertices[mesh.Indices[i]].MaterialId;
            if (!groups.TryGetValue(material, out var group)) groups.Add(material, group = new MaterialGroup(material));
            group.Indices.Add(mesh.Indices[i]); group.Indices.Add(mesh.Indices[i + 1]); group.Indices.Add(mesh.Indices[i + 2]);
        }
        return groups.Values.ToList();
    }

    private static void WriteAccessor(Utf8JsonWriter json, int view, int offset, int componentType, int count, string type, Vector3? min = null, Vector3? max = null)
    {
        json.WriteStartObject(); json.WriteNumber("bufferView", view); if (offset != 0) json.WriteNumber("byteOffset", offset); json.WriteNumber("componentType", componentType); json.WriteNumber("count", count); json.WriteString("type", type);
        if (min is { } minimum) { json.WriteStartArray("min"); json.WriteNumberValue(minimum.X); json.WriteNumberValue(minimum.Y); json.WriteNumberValue(minimum.Z); json.WriteEndArray(); }
        if (max is { } maximum) { json.WriteStartArray("max"); json.WriteNumberValue(maximum.X); json.WriteNumberValue(maximum.Y); json.WriteNumberValue(maximum.Z); json.WriteEndArray(); }
        json.WriteEndObject();
    }

    private static void Write(BinaryWriter writer, Vector2 value) { writer.Write(value.X); writer.Write(value.Y); }
    private static void Write(BinaryWriter writer, Vector3 value) { writer.Write(value.X); writer.Write(value.Y); writer.Write(value.Z); }
    private static void Write(BinaryWriter writer, Vector4 value) { writer.Write(value.X); writer.Write(value.Y); writer.Write(value.Z); writer.Write(value.W); }
    private static void Align(Stream stream, int alignment, byte value) { while (stream.Length % alignment != 0) stream.WriteByte(value); }

    private sealed class MaterialGroup(int material)
    {
        public int Material { get; } = material;
        public List<uint> Indices { get; } = [];
        public int ByteOffset { get; set; }
    }
}
