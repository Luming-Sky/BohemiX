using System.Numerics;
using System.Security.Cryptography;
using Avalonia.Platform;
using BohemiX.Modules.Forge.Models;
using SharpGLTF.Schema2;
using SkiaSharp;

namespace BohemiX.Modules.Forge.Rendering;

public enum ForgeTextureRole
{
    BaseColor,
    Normal,
    MetallicRoughness,
    Occlusion,
    Emissive,
    Background
}

public sealed class PbrTextureAsset : IDisposable
{
    public PbrTextureAsset(
        string name,
        int width,
        int height,
        byte[] rgba,
        bool srgb,
        bool repeat,
        string contentKey,
        ForgeTextureRole role)
    {
        Name = name;
        Width = width;
        Height = height;
        Rgba = rgba;
        Srgb = srgb;
        Repeat = repeat;
        ContentKey = contentKey;
        Role = role;
    }

    public string Name { get; }
    public int Width { get; }
    public int Height { get; }
    public byte[] Rgba { get; private set; }
    public bool Srgb { get; }
    public bool Repeat { get; }
    public string ContentKey { get; }
    public ForgeTextureRole Role { get; }
    public bool HasCpuData => Rgba.Length > 0;

    public void Dispose() => Rgba = [];
}

public sealed record PbrMaterialAsset(
    string Name,
    Vector4 BaseColor,
    float Metallic,
    float Roughness,
    bool HasNormalMap,
    bool HasAmbientOcclusion,
    IReadOnlyList<string>? TextureChannels = null,
    PbrTextureAsset? BaseColorTexture = null,
    PbrTextureAsset? NormalTexture = null,
    PbrTextureAsset? MetallicRoughnessTexture = null,
    PbrTextureAsset? OcclusionTexture = null,
    PbrTextureAsset? EmissiveTexture = null,
    Vector3? EmissiveFactor = null)
{
    public bool HasBaseColorMap => TextureChannels?.Contains("BaseColor", StringComparer.OrdinalIgnoreCase) == true;
    public bool HasMetallicRoughnessMap => TextureChannels?.Contains("MetallicRoughness", StringComparer.OrdinalIgnoreCase) == true;
    public bool HasEmissionMap => TextureChannels?.Contains("Emissive", StringComparer.OrdinalIgnoreCase) == true;
}

public sealed record MeshPrimitiveAsset(int FirstIndex, int IndexCount, int MaterialIndex, string MaterialName);

public sealed record MeshAsset(
    string Name,
    MeshData Mesh,
    IReadOnlyList<PbrMaterialAsset> Materials,
    IReadOnlyList<MeshPrimitiveAsset>? Primitives = null) : IDisposable
{
    public void Dispose()
    {
        var textures = new HashSet<PbrTextureAsset>();
        foreach (var material in Materials)
        {
            if (material.BaseColorTexture is not null) textures.Add(material.BaseColorTexture);
            if (material.NormalTexture is not null) textures.Add(material.NormalTexture);
            if (material.MetallicRoughnessTexture is not null) textures.Add(material.MetallicRoughnessTexture);
            if (material.OcclusionTexture is not null) textures.Add(material.OcclusionTexture);
            if (material.EmissiveTexture is not null) textures.Add(material.EmissiveTexture);
        }

        foreach (var texture in textures) texture.Dispose();
    }
}

public interface IForgeAssetLoader
{
    MeshAsset LoadMesh(Uri assetUri);
}

public sealed class ForgeGlbAssetLoader : IForgeAssetLoader
{
    private readonly MaterialLibrary fallbackMaterials = new();
    private readonly Dictionary<string, PbrTextureAsset> decodedTextures = new(StringComparer.Ordinal);
    private readonly ForgeTextureQuality textureQuality;

    public ForgeGlbAssetLoader(ForgeTextureQuality textureQuality = ForgeTextureQuality.Medium)
    {
        this.textureQuality = textureQuality;
    }

    public MeshAsset LoadMesh(Uri assetUri)
    {
        ArgumentNullException.ThrowIfNull(assetUri);
        decodedTextures.Clear();
        try
        {
            using var stream = assetUri.IsFile
                ? File.OpenRead(assetUri.LocalPath)
                : AssetLoader.Open(assetUri);
            var model = ModelRoot.ReadGLB(stream);
            var builder = new MeshBuilder();
            var loadedMaterials = new List<PbrMaterialAsset>();
            var materialIndices = new Dictionary<Material, int>();
            var loadedPrimitives = new List<MeshPrimitiveAsset>();

            foreach (var node in model.LogicalNodes.Where(candidate => candidate.Mesh is not null))
            {
                var transform = node.WorldMatrix;
                var normalTransform = transform;
                normalTransform.Translation = Vector3.Zero;
                foreach (var primitive in node.Mesh!.Primitives)
                {
                    var positions = primitive.GetVertexAccessor("POSITION")?.AsVector3Array()
                        ?? throw new InvalidDataException($"{assetUri}: primitive has no POSITION accessor.");
                    var normals = primitive.GetVertexAccessor("NORMAL")?.AsVector3Array();
                    var tangents = primitive.GetVertexAccessor("TANGENT")?.AsVector4Array();
                    var texCoords = primitive.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array();
                    var indices = primitive.GetIndices();
                    var generatedTangents = tangents is null
                        ? GenerateTangents(positions, normals, texCoords, indices)
                        : null;
                    var materialId = ResolveMaterial(primitive.Material?.Name);
                    var materialName = ((ForgeMaterialId)materialId).ToString();
                    var materialIndex = -1;
                    if (primitive.Material is { } sourceMaterial)
                    {
                        if (!materialIndices.TryGetValue(sourceMaterial, out materialIndex))
                        {
                            materialIndex = loadedMaterials.Count;
                            materialIndices.Add(sourceMaterial, materialIndex);
                            loadedMaterials.Add(CreateMaterialAsset((ForgeMaterialId)materialId, sourceMaterial));
                        }
                    }
                    else
                    {
                        materialIndex = loadedMaterials.FindIndex(item => item.Name.Equals(materialName, StringComparison.OrdinalIgnoreCase));
                        if (materialIndex < 0)
                        {
                            materialIndex = loadedMaterials.Count;
                            loadedMaterials.Add(CreateMaterialAsset((ForgeMaterialId)materialId));
                        }
                    }
                    var offset = (uint)builder.VertexCount;
                    var firstIndex = builder.IndexCount;

                    for (var i = 0; i < positions.Count; i++)
                    {
                        var position = Vector3.Transform(positions[i], transform);
                        var normal = normals is not null && i < normals.Count
                            ? Vector3.TransformNormal(normals[i], normalTransform)
                            : Vector3.UnitY;
                        var tangentValue = tangents is not null && i < tangents.Count
                            ? tangents[i]
                            : generatedTangents![i];
                        var tangent = Vector3.TransformNormal(new Vector3(tangentValue.X, tangentValue.Y, tangentValue.Z), normalTransform);
                        var uv = texCoords is not null && i < texCoords.Count ? texCoords[i] : Vector2.Zero;
                        builder.AddVertex(position, normal, new Vector4(tangent, tangentValue.W), uv, materialId);
                    }

                    if (indices is null)
                    {
                        for (uint i = 0; i + 2 < positions.Count; i += 3) builder.AddTriangle(offset + i, offset + i + 1, offset + i + 2);
                    }
                    else
                    {
                        for (var i = 0; i + 2 < indices.Count; i += 3)
                        {
                            builder.AddTriangle(offset + indices[i], offset + indices[i + 1], offset + indices[i + 2]);
                        }
                    }
                    loadedPrimitives.Add(new MeshPrimitiveAsset(firstIndex, builder.IndexCount - firstIndex, materialIndex, materialName));
                }
            }

            var mesh = builder.Build();
            if (mesh.Vertices.Length == 0 || mesh.Indices.Length == 0) throw new InvalidDataException($"{assetUri}: model contains no triangle geometry.");
            if (!mesh.IsFinite) throw new InvalidDataException($"{assetUri}: model contains non-finite vertex data.");
            return new MeshAsset(assetUri.ToString(), mesh, loadedMaterials, loadedPrimitives);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            foreach (var texture in decodedTextures.Values.Distinct()) texture.Dispose();
            throw new InvalidOperationException($"Unable to load Forge model '{assetUri}'.", ex);
        }
        catch
        {
            foreach (var texture in decodedTextures.Values.Distinct()) texture.Dispose();
            throw;
        }
        finally
        {
            decodedTextures.Clear();
        }
    }

    private int ResolveMaterial(string? name) =>
        Enum.TryParse<ForgeMaterialId>(name, true, out var material) ? (int)material : (int)ForgeMaterialId.Iron;

    internal static Vector4[] GenerateTangents(
        IReadOnlyList<Vector3> positions,
        IReadOnlyList<Vector3>? normals,
        IReadOnlyList<Vector2>? texCoords,
        IList<uint>? indices)
    {
        var accumulated = new Vector3[positions.Count];
        var triangleIndexCount = indices?.Count ?? positions.Count;
        for (var index = 0; index + 2 < triangleIndexCount; index += 3)
        {
            var first = indices is null ? (uint)index : indices[index];
            var second = indices is null ? (uint)(index + 1) : indices[index + 1];
            var third = indices is null ? (uint)(index + 2) : indices[index + 2];
            if (first >= positions.Count || second >= positions.Count || third >= positions.Count) continue;
            var p0 = positions[(int)first];
            var p1 = positions[(int)second];
            var p2 = positions[(int)third];
            var uv0 = texCoords is not null && first < texCoords.Count ? texCoords[(int)first] : Vector2.Zero;
            var uv1 = texCoords is not null && second < texCoords.Count ? texCoords[(int)second] : Vector2.UnitX;
            var uv2 = texCoords is not null && third < texCoords.Count ? texCoords[(int)third] : Vector2.UnitY;
            var edge1 = p1 - p0;
            var edge2 = p2 - p0;
            var delta1 = uv1 - uv0;
            var delta2 = uv2 - uv0;
            var determinant = delta1.X * delta2.Y - delta1.Y * delta2.X;
            var tangent = MathF.Abs(determinant) > 1e-7f
                ? (edge1 * delta2.Y - edge2 * delta1.Y) / determinant
                : edge1;
            if (!IsFinite(tangent) || tangent.LengthSquared() < 1e-8f) continue;
            accumulated[(int)first] += tangent;
            accumulated[(int)second] += tangent;
            accumulated[(int)third] += tangent;
        }

        var result = new Vector4[positions.Count];
        for (var index = 0; index < result.Length; index++)
        {
            var normal = normals is not null && index < normals.Count && normals[index].LengthSquared() > 1e-8f
                ? Vector3.Normalize(normals[index])
                : Vector3.UnitY;
            var tangent = accumulated[index] - normal * Vector3.Dot(accumulated[index], normal);
            if (!IsFinite(tangent) || tangent.LengthSquared() < 1e-8f)
            {
                var axis = MathF.Abs(normal.X) < .8f ? Vector3.UnitX : Vector3.UnitZ;
                tangent = Vector3.Cross(axis, normal);
            }
            tangent = Vector3.Normalize(tangent);
            result[index] = new Vector4(tangent, 1);
        }
        return result;
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private PbrMaterialAsset CreateMaterialAsset(ForgeMaterialId id, Material? sourceMaterial = null)
    {
        var material = fallbackMaterials[id];
#pragma warning disable CS0618 // SharpGLTF 1.0.6 keeps the compatible aggregate accessor obsolete.
        var baseColor = sourceMaterial?.FindChannel("BaseColor")?.Parameter ?? new Vector4(material.Albedo, 1);
        var metallicRoughness = sourceMaterial?.FindChannel("MetallicRoughness")?.Parameter;
#pragma warning restore CS0618
        var channels = sourceMaterial?.Channels
            .Select(channel => channel.Key)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
        var baseColorChannel = FindChannel(sourceMaterial, "BaseColor", "BaseColorTexture", "Albedo");
        var normalChannel = FindChannel(sourceMaterial, "Normal", "NormalTexture");
        var metallicRoughnessChannel = FindChannel(sourceMaterial, "MetallicRoughness", "MetallicRoughnessTexture", "ORM");
        var occlusionChannel = FindChannel(sourceMaterial, "Occlusion", "OcclusionTexture", "AmbientOcclusion", "AO");
        var emissiveChannel = FindChannel(sourceMaterial, "Emissive", "Emission", "EmissiveTexture");
        return new PbrMaterialAsset(
            id.ToString(),
            baseColor,
            metallicRoughness?.X ?? material.Metallic,
            metallicRoughness?.Y ?? material.Roughness,
            channels.Contains("Normal", StringComparer.OrdinalIgnoreCase),
            channels.Contains("Occlusion", StringComparer.OrdinalIgnoreCase),
            channels,
            DecodeTexture(baseColorChannel, id + "_BaseColor", srgb: true, ForgeTextureRole.BaseColor),
            DecodeTexture(normalChannel, id + "_Normal", srgb: false, ForgeTextureRole.Normal),
            DecodeTexture(metallicRoughnessChannel, id + "_MetallicRoughness", srgb: false, ForgeTextureRole.MetallicRoughness),
            DecodeTexture(occlusionChannel, id + "_Occlusion", srgb: false, ForgeTextureRole.Occlusion),
            DecodeTexture(emissiveChannel, id + "_Emissive", srgb: true, ForgeTextureRole.Emissive),
#pragma warning disable CS0618 // SharpGLTF 1.0.6 exposes the aggregate channel parameter through this compatibility accessor.
            emissiveChannel?.Parameter is { } emissive ? new Vector3(emissive.X, emissive.Y, emissive.Z) : Vector3.Zero);
#pragma warning restore CS0618
    }

    private PbrTextureAsset? DecodeTexture(MaterialChannel? channel, string name, bool srgb, ForgeTextureRole role)
    {
        var image = channel?.Texture?.PrimaryImage?.Content;
        if (image is null || !image.Value.IsValid) return null;
        using var stream = image.Value.Open();
        using var encodedStream = new MemoryStream();
        stream.CopyTo(encodedStream);
        var encodedBytes = encodedStream.ToArray();
        var maximumDimension = ForgeGraphicsSettings.MaximumTextureDimension(textureQuality);
        var encodedKey = $"{Convert.ToHexString(SHA256.HashData(encodedBytes))}:{role}:{maximumDimension}:{(srgb ? "srgb" : "linear")}";

        if (decodedTextures.TryGetValue(encodedKey, out var cached))
        {
            return cached;
        }

        using var encodedData = SKData.CreateCopy(encodedBytes);
        using var codec = SKCodec.Create(encodedData);
        if (codec is null) return null;
        var scale = Math.Min(1f, maximumDimension / (float)Math.Max(codec.Info.Width, codec.Info.Height));
        var width = Math.Max(1, (int)MathF.Round(codec.Info.Width * scale));
        var height = Math.Max(1, (int)MathF.Round(codec.Info.Height * scale));
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());

        byte[] bytes;
        if (result is SKCodecResult.Success or SKCodecResult.IncompleteInput)
        {
            bytes = bitmap.Bytes.ToArray();
        }
        else
        {
            using var nativeCodec = SKCodec.Create(encodedData);
            if (nativeCodec is null) return null;
            var nativeInfo = new SKImageInfo(nativeCodec.Info.Width, nativeCodec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            using var nativeBitmap = new SKBitmap(nativeInfo);
            var nativeResult = nativeCodec.GetPixels(nativeInfo, nativeBitmap.GetPixels());
            if (nativeResult is not SKCodecResult.Success and not SKCodecResult.IncompleteInput) return null;
            using var resized = nativeBitmap.Resize(info, SKFilterQuality.Medium);
            if (resized is null) return null;
            bytes = resized.Bytes.ToArray();
        }

        if (role == ForgeTextureRole.Normal) RenormalizeNormalMap(bytes);
        var texture = new PbrTextureAsset(name, width, height, bytes, srgb, repeat: true, encodedKey, role);
        decodedTextures[encodedKey] = texture;
        return texture;
    }

    internal static void RenormalizeNormalMap(Span<byte> rgba)
    {
        for (var index = 0; index + 3 < rgba.Length; index += 4)
        {
            var normal = new Vector3(
                rgba[index] / 127.5f - 1f,
                rgba[index + 1] / 127.5f - 1f,
                rgba[index + 2] / 127.5f - 1f);
            normal = normal.LengthSquared() > 1e-8f ? Vector3.Normalize(normal) : Vector3.UnitZ;
            rgba[index] = ToNormalByte(normal.X);
            rgba[index + 1] = ToNormalByte(normal.Y);
            rgba[index + 2] = ToNormalByte(MathF.Abs(normal.Z));
        }
    }

    private static byte ToNormalByte(float value) =>
        (byte)Math.Clamp((int)MathF.Round((value * .5f + .5f) * 255f), 0, 255);

    private static MaterialChannel? FindChannel(Material? material, params string[] aliases)
    {
        if (material is null) return null;
        foreach (var channel in material.Channels)
        {
            if (aliases.Any(alias => channel.Key.Equals(alias, StringComparison.OrdinalIgnoreCase)))
            {
                return channel;
            }
        }
        return null;
    }
}
