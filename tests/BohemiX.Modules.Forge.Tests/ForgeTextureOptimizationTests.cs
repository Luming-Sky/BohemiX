using System.Numerics;
using BohemiX.Modules.Forge.Models;
using BohemiX.Modules.Forge.Rendering;

namespace BohemiX.Modules.Forge.Tests;

public sealed class ForgeTextureOptimizationTests
{
    [Fact]
    public async Task ProceduralRenderTexturesArePreparedOnceAndReused()
    {
        ForgeRenderWarmup.Begin();

        var first = await ForgeRenderWarmup.GetProceduralTexturesAsync();
        var second = await ForgeRenderWarmup.GetProceduralTexturesAsync();

        Assert.Same(first, second);
        Assert.Equal(ProceduralTextureAtlas.Size * ProceduralTextureAtlas.Size * 4, first.Color.Length);
        Assert.Equal(first.Color.Length, first.Normal.Length);
        Assert.True(ForgeRenderWarmup.TryGetProceduralTextures(out var completed));
        Assert.Same(first, completed);
    }

    [Fact]
    public void NormalAtlasRejectsUnexpectedPreparedColorSize()
    {
        Assert.Throws<ArgumentException>(() => ProceduralTextureAtlas.CreateNormalAtlas(new byte[4]));
    }

    [Fact]
    public void GraphicsDefaultsAndPresetsUseMediumQuality()
    {
        var defaults = new ForgeRenderSettings();
        var graphics = new ForgeGraphicsSettings();

        Assert.Equal(ForgeRenderQuality.Medium, defaults.Quality);
        Assert.Equal(1024, defaults.ShadowResolution);
        Assert.Equal(.55, defaults.ParticleDensity, 2);
        Assert.False(defaults.DepthOfFieldEnabled);
        Assert.Equal(ForgeRenderQuality.Medium, graphics.RenderQuality);
        Assert.Equal(ForgeTextureQuality.Medium, graphics.TextureQuality);
        Assert.Equal(512, ForgeGraphicsSettings.MaximumTextureDimension(ForgeTextureQuality.Medium));
        Assert.Equal(1280, ForgeGraphicsSettings.MaximumBackgroundWidth(ForgeTextureQuality.Medium));
    }

    [Theory]
    [InlineData(ForgeTextureQuality.Low, 256)]
    [InlineData(ForgeTextureQuality.Medium, 512)]
    [InlineData(ForgeTextureQuality.High, 1024)]
    public void GlbTexturesRespectSelectedMaximumDimension(ForgeTextureQuality quality, int maximum)
    {
        var loader = new ForgeGlbAssetLoader(quality);
        using var mesh = loader.LoadMesh(new Uri(Path.Combine(FindModelsDirectory(), "weapon-bearded-axe.glb")));
        var textures = DistinctTextures(mesh).ToArray();

        Assert.NotEmpty(textures);
        Assert.All(textures, texture =>
        {
            Assert.InRange(texture.Width, 1, maximum);
            Assert.InRange(texture.Height, 1, maximum);
        });
    }

    [Fact]
    public void ResizedNormalMapsRemainNormalizedAndMeshDisposalReleasesCpuPixels()
    {
        var loader = new ForgeGlbAssetLoader(ForgeTextureQuality.Low);
        var mesh = loader.LoadMesh(new Uri(Path.Combine(FindModelsDirectory(), "weapon-bearded-axe.glb")));
        var textures = DistinctTextures(mesh).ToArray();
        var normals = textures.Where(texture => texture.Role == ForgeTextureRole.Normal).ToArray();

        Assert.NotEmpty(normals);
        foreach (var texture in normals)
        {
            for (var index = 0; index + 3 < texture.Rgba.Length; index += 4)
            {
                var normal = new Vector3(
                    texture.Rgba[index] / 127.5f - 1f,
                    texture.Rgba[index + 1] / 127.5f - 1f,
                    texture.Rgba[index + 2] / 127.5f - 1f);
                Assert.InRange(normal.Length(), .98f, 1.02f);
                Assert.True(normal.Z >= 0);
            }
        }

        mesh.Dispose();
        Assert.All(textures, texture => Assert.False(texture.HasCpuData));
    }

    [Theory]
    [InlineData(ForgeTextureRole.BaseColor, "Bc3", 64)]
    [InlineData(ForgeTextureRole.Emissive, "Bc3", 64)]
    [InlineData(ForgeTextureRole.MetallicRoughness, "Bc3", 64)]
    [InlineData(ForgeTextureRole.Normal, "Bc5", 64)]
    [InlineData(ForgeTextureRole.Occlusion, "Bc4", 32)]
    public void RuntimeCompressionUsesExpectedFormatAndMipSize(
        ForgeTextureRole role,
        string expectedFormat,
        int expectedFirstMipBytes)
    {
        using var texture = CreateTexture(role, srgb: role is ForgeTextureRole.BaseColor or ForgeTextureRole.Emissive);
        var capabilities = new ForgeTextureCompressionCapabilities(S3tc: true, S3tcSrgb: true, Rgtc: true);

        Assert.True(ForgeTextureCompressor.TryCompress(texture, capabilities, out var compressed));
        Assert.NotNull(compressed);
        Assert.Equal(expectedFormat, compressed.Format.ToString());
        Assert.Equal(4, compressed.MipLevels.Count);
        Assert.Equal(expectedFirstMipBytes, compressed.MipLevels[0].Data.Length);
        Assert.Equal((8, 8), (compressed.MipLevels[0].Width, compressed.MipLevels[0].Height));
        Assert.Equal((1, 1), (compressed.MipLevels[^1].Width, compressed.MipLevels[^1].Height));
    }

    [Fact]
    public void CompressionReturnsFalseWhenRequiredGpuFormatIsUnavailable()
    {
        using var texture = CreateTexture(ForgeTextureRole.Normal, srgb: false);

        Assert.False(ForgeTextureCompressor.TryCompress(
            texture,
            new ForgeTextureCompressionCapabilities(S3tc: true, S3tcSrgb: true, Rgtc: false),
            out var compressed));
        Assert.Null(compressed);
        Assert.True(texture.HasCpuData);
    }

    [Fact]
    public void CpuTexturePayloadIsReleasedImmediatelyAfterSuccessfulUpload()
    {
        var texture = CreateTexture(ForgeTextureRole.BaseColor, srgb: true);

        var result = ForgeSceneRenderer.UploadAndReleaseCpuTexture(texture, source =>
        {
            Assert.True(source.HasCpuData);
            return 42;
        });

        Assert.Equal(42, result);
        Assert.False(texture.HasCpuData);
    }

    [Fact]
    public void CpuTexturePayloadIsReleasedWhenUploadFails()
    {
        var texture = CreateTexture(ForgeTextureRole.Normal, srgb: false);

        Assert.Throws<InvalidOperationException>(() =>
            ForgeSceneRenderer.UploadAndReleaseCpuTexture<int>(texture, _ =>
                throw new InvalidOperationException("upload failed")));

        Assert.False(texture.HasCpuData);
    }

    private static PbrTextureAsset CreateTexture(ForgeTextureRole role, bool srgb)
    {
        var rgba = new byte[8 * 8 * 4];
        for (var index = 0; index < rgba.Length; index += 4)
        {
            rgba[index] = 128;
            rgba[index + 1] = 128;
            rgba[index + 2] = 255;
            rgba[index + 3] = 255;
        }

        return new PbrTextureAsset("test", 8, 8, rgba, srgb, true, $"test:{role}:{srgb}", role);
    }

    private static IEnumerable<PbrTextureAsset> DistinctTextures(MeshAsset mesh) =>
        mesh.Materials
            .SelectMany(material => new[]
            {
                material.BaseColorTexture,
                material.NormalTexture,
                material.MetallicRoughnessTexture,
                material.OcclusionTexture,
                material.EmissiveTexture
            })
            .Where(texture => texture is not null)
            .Cast<PbrTextureAsset>()
            .Distinct();

    private static string FindModelsDirectory()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "src", "BohemiX.Modules.Forge", "Assets", "Models");
            if (Directory.Exists(candidate)) return candidate;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Unable to locate Forge model assets.");
    }
}
