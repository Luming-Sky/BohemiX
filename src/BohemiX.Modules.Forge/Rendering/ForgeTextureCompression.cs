using BCnEncoder.Encoder;
using BCnEncoder.Shared;

namespace BohemiX.Modules.Forge.Rendering;

internal enum ForgeBlockCompression
{
    Bc3,
    Bc4,
    Bc5
}

internal sealed record ForgeCompressedMip(int Width, int Height, byte[] Data);

internal sealed record ForgeCompressedTexture(
    ForgeBlockCompression Format,
    bool Srgb,
    IReadOnlyList<ForgeCompressedMip> MipLevels);

internal readonly record struct ForgeTextureCompressionCapabilities(
    bool S3tc,
    bool S3tcSrgb,
    bool Rgtc)
{
    public static ForgeTextureCompressionCapabilities Detect(Silk.NET.OpenGL.GL gl, bool isGles)
    {
        var s3tc = gl.IsExtensionPresent("GL_EXT_texture_compression_s3tc")
            || gl.IsExtensionPresent("GL_EXT_texture_compression_dxt5")
            || gl.IsExtensionPresent("GL_ANGLE_texture_compression_dxt5");
        var s3tcSrgb = s3tc && (!isGles
            || gl.IsExtensionPresent("GL_EXT_texture_compression_s3tc_srgb")
            || gl.IsExtensionPresent("GL_EXT_sRGB"));
        var rgtc = !isGles
            || gl.IsExtensionPresent("GL_ARB_texture_compression_rgtc")
            || gl.IsExtensionPresent("GL_EXT_texture_compression_rgtc");
        return new ForgeTextureCompressionCapabilities(s3tc, s3tcSrgb, rgtc);
    }

    public bool Supports(ForgeTextureRole role, bool srgb) => role switch
    {
        ForgeTextureRole.Normal or ForgeTextureRole.Occlusion => Rgtc,
        _ when srgb => S3tcSrgb,
        _ => S3tc
    };
}

internal static class ForgeTextureCompressor
{
    public static bool TryCompress(
        PbrTextureAsset texture,
        ForgeTextureCompressionCapabilities capabilities,
        out ForgeCompressedTexture? compressed)
    {
        compressed = null;
        if (!texture.HasCpuData || !capabilities.Supports(texture.Role, texture.Srgb)) return false;

        var (format, blockFormat) = texture.Role switch
        {
            ForgeTextureRole.Normal => (CompressionFormat.Bc5, ForgeBlockCompression.Bc5),
            ForgeTextureRole.Occlusion => (CompressionFormat.Bc4, ForgeBlockCompression.Bc4),
            _ => (CompressionFormat.Bc3, ForgeBlockCompression.Bc3)
        };

        try
        {
            var encoder = new BcEncoder(format);
            encoder.OutputOptions.GenerateMipMaps = true;
            encoder.OutputOptions.Quality = CompressionQuality.Fast;
            encoder.Options.IsParallel = false;
            var encoded = encoder.EncodeToRawBytes(
                texture.Rgba,
                texture.Width,
                texture.Height,
                PixelFormat.Rgba32);
            var levels = new ForgeCompressedMip[encoded.Length];
            var width = texture.Width;
            var height = texture.Height;
            for (var level = 0; level < encoded.Length; level++)
            {
                levels[level] = new ForgeCompressedMip(width, height, encoded[level]);
                width = Math.Max(1, width / 2);
                height = Math.Max(1, height / 2);
            }

            compressed = new ForgeCompressedTexture(blockFormat, texture.Srgb, levels);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

internal sealed class SharedGpuTextureCache : IDisposable
{
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private bool disposed;

    public TextureLease Acquire(string key, Func<Texture2D> factory)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!entries.TryGetValue(key, out var entry))
        {
            entry = new Entry(factory());
            entries.Add(key, entry);
        }

        entry.ReferenceCount++;
        return new TextureLease(this, key, entry.Texture);
    }

    private void Release(string key)
    {
        if (disposed || !entries.TryGetValue(key, out var entry)) return;
        entry.ReferenceCount--;
        if (entry.ReferenceCount > 0) return;
        entries.Remove(key);
        entry.Texture.Dispose();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var entry in entries.Values) entry.Texture.Dispose();
        entries.Clear();
    }

    private sealed class Entry(Texture2D texture)
    {
        public Texture2D Texture { get; } = texture;
        public int ReferenceCount { get; set; }
    }

    internal sealed class TextureLease : IDisposable
    {
        private SharedGpuTextureCache? owner;
        private readonly string key;

        public TextureLease(SharedGpuTextureCache owner, string key, Texture2D texture)
        {
            this.owner = owner;
            this.key = key;
            Texture = texture;
        }

        public Texture2D Texture { get; }

        public void Dispose()
        {
            Interlocked.Exchange(ref owner, null)?.Release(key);
        }
    }
}
