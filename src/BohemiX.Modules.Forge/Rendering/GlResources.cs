using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.OpenGL;

namespace BohemiX.Modules.Forge.Rendering;

public sealed class GlResourceRegistry : IDisposable
{
    private readonly List<IDisposable> resources = [];
    private bool disposed;

    internal int Count => resources.Count;

    public T Register<T>(T resource) where T : IDisposable
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        resources.Add(resource);
        return resource;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        for (var i = resources.Count - 1; i >= 0; i--) resources[i].Dispose();
        resources.Clear();
    }

    public void Abandon()
    {
        if (disposed) return;
        disposed = true;
        resources.Clear();
    }
}

public sealed class ShaderProgram : IDisposable
{
    private readonly GL gl;
    private readonly Dictionary<string, int> locations = new(StringComparer.Ordinal);
    private bool disposed;

    public ShaderProgram(GL gl, string vertexSource, string fragmentSource, bool isGles, string label)
    {
        this.gl = gl;
        var vertex = Compile(ShaderType.VertexShader, Prefix(vertexSource, isGles));
        var fragment = Compile(ShaderType.FragmentShader, Prefix(fragmentSource, isGles, true));
        Handle = gl.CreateProgram();
        gl.AttachShader(Handle, vertex);
        gl.AttachShader(Handle, fragment);
        gl.LinkProgram(Handle);
        gl.GetProgram(Handle, ProgramPropertyARB.LinkStatus, out var linked);
        var log = gl.GetProgramInfoLog(Handle);
        gl.DeleteShader(vertex);
        gl.DeleteShader(fragment);
        if (linked == 0)
        {
            gl.DeleteProgram(Handle);
            Handle = 0;
            throw new InvalidOperationException($"{label} shader link failed: {log}");
        }
    }

    public uint Handle { get; private set; }
    public void Use() => gl.UseProgram(Handle);
    public int Uniform(string name)
    {
        if (!locations.TryGetValue(name, out var value)) locations[name] = value = gl.GetUniformLocation(Handle, name);
        return value;
    }
    public void Set(string name, float value) => gl.Uniform1(Uniform(name), value);
    public void Set(string name, int value) => gl.Uniform1(Uniform(name), value);
    public void Set(string name, Vector2 value) => gl.Uniform2(Uniform(name), value.X, value.Y);
    public void Set(string name, Vector3 value) => gl.Uniform3(Uniform(name), value.X, value.Y, value.Z);
    public void Set(string name, Vector4 value) => gl.Uniform4(Uniform(name), value.X, value.Y, value.Z, value.W);
    public unsafe void Set(string name, Matrix4x4 value)
    {
        // Matrix4x4 is row-major/row-vector. OpenGL reads this memory as column-major,
        // which is already the transpose required by column-vector GLSL expressions.
        gl.UniformMatrix4(Uniform(name), 1, false, (float*)&value);
    }

    private uint Compile(ShaderType type, string source)
    {
        var shader = gl.CreateShader(type);
        gl.ShaderSource(shader, source);
        gl.CompileShader(shader);
        gl.GetShader(shader, ShaderParameterName.CompileStatus, out var compiled);
        if (compiled != 0) return shader;
        var log = gl.GetShaderInfoLog(shader);
        gl.DeleteShader(shader);
        throw new InvalidOperationException($"Forge {type} compile failed: {log}");
    }

    private static string Prefix(string source, bool es, bool fragment = false) =>
        (es ? "#version 300 es\n" + (fragment ? "precision highp float;\nprecision highp sampler2DShadow;\n" : "precision highp float;\n") : "#version 330 core\n") + source;

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (Handle != 0) gl.DeleteProgram(Handle);
        Handle = 0;
    }
}

public sealed class MeshBuffer : IDisposable
{
    private readonly GL gl;
    private readonly bool dynamic;
    private nuint vertexCapacity;
    private nuint indexCapacity;
    private bool disposed;

    public unsafe MeshBuffer(GL gl, MeshData mesh, bool dynamic = false)
    {
        this.gl = gl;
        this.dynamic = dynamic;
        VertexArray = gl.GenVertexArray();
        VertexBuffer = gl.GenBuffer();
        IndexBuffer = gl.GenBuffer();
        gl.BindVertexArray(VertexArray);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, VertexBuffer);
        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, IndexBuffer);
        var stride = (uint)Marshal.SizeOf<ForgeVertex>();
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        gl.EnableVertexAttribArray(2);
        gl.VertexAttribPointer(2, 4, VertexAttribPointerType.Float, false, stride, (void*)(6 * sizeof(float)));
        gl.EnableVertexAttribArray(3);
        gl.VertexAttribPointer(3, 2, VertexAttribPointerType.Float, false, stride, (void*)(10 * sizeof(float)));
        gl.EnableVertexAttribArray(4);
        gl.VertexAttribPointer(4, 1, VertexAttribPointerType.Float, false, stride, (void*)(12 * sizeof(float)));
        Upload(mesh);
        gl.BindVertexArray(0);
    }

    public uint VertexArray { get; }
    public uint VertexBuffer { get; }
    public uint IndexBuffer { get; }
    public uint IndexCount { get; private set; }

    public unsafe void Upload(MeshData mesh)
    {
        gl.BindVertexArray(VertexArray);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, VertexBuffer);
        var vertexBytes = (nuint)(mesh.Vertices.Length * Marshal.SizeOf<ForgeVertex>());
        fixed (ForgeVertex* pointer = mesh.Vertices)
        {
            if (!dynamic || vertexBytes > vertexCapacity)
            {
                vertexCapacity = dynamic ? NextPowerOfTwo(vertexBytes) : vertexBytes;
                gl.BufferData(BufferTargetARB.ArrayBuffer, vertexCapacity, null, dynamic ? BufferUsageARB.DynamicDraw : BufferUsageARB.StaticDraw);
            }
            gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0, vertexBytes, pointer);
        }
        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, IndexBuffer);
        var indexBytes = (nuint)(mesh.Indices.Length * sizeof(uint));
        fixed (uint* pointer = mesh.Indices)
        {
            if (!dynamic || indexBytes > indexCapacity)
            {
                indexCapacity = dynamic ? NextPowerOfTwo(indexBytes) : indexBytes;
                gl.BufferData(BufferTargetARB.ElementArrayBuffer, indexCapacity, null, dynamic ? BufferUsageARB.DynamicDraw : BufferUsageARB.StaticDraw);
            }
            gl.BufferSubData(BufferTargetARB.ElementArrayBuffer, 0, indexBytes, pointer);
        }
        IndexCount = (uint)mesh.Indices.Length;
    }

    public unsafe void Draw()
    {
        if (IndexCount == 0) return;
        gl.BindVertexArray(VertexArray);
        gl.DrawElements(PrimitiveType.Triangles, IndexCount, DrawElementsType.UnsignedInt, null);
    }

    public unsafe void DrawRange(uint firstIndex, uint indexCount)
    {
        if (indexCount == 0 || firstIndex >= IndexCount) return;
        indexCount = Math.Min(indexCount, IndexCount - firstIndex);
        gl.BindVertexArray(VertexArray);
        gl.DrawElements(
            PrimitiveType.Triangles,
            indexCount,
            DrawElementsType.UnsignedInt,
            (void*)(nuint)(firstIndex * sizeof(uint)));
    }

    private static nuint NextPowerOfTwo(nuint value)
    {
        nuint result = 4096;
        while (result < value) result <<= 1;
        return result;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        gl.DeleteBuffer(IndexBuffer);
        gl.DeleteBuffer(VertexBuffer);
        gl.DeleteVertexArray(VertexArray);
    }
}

public sealed class Texture2D : IDisposable
{
    private readonly GL gl;
    private bool disposed;

    public unsafe Texture2D(GL gl, int width, int height, byte[] data, bool srgb, bool repeat = true)
    {
        this.gl = gl;
        Width = width;
        Height = height;
        Handle = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, Handle);
        fixed (byte* pointer = data)
        {
            gl.TexImage2D(TextureTarget.Texture2D, 0, srgb ? InternalFormat.Srgb8Alpha8 : InternalFormat.Rgba8, (uint)width, (uint)height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, pointer);
        }
        ConfigureSampling(repeat);
        gl.GenerateMipmap(TextureTarget.Texture2D);
    }

    internal unsafe Texture2D(GL gl, ForgeCompressedTexture compressed, bool repeat = true)
    {
        this.gl = gl;
        var first = compressed.MipLevels[0];
        Width = first.Width;
        Height = first.Height;
        Handle = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, Handle);
        var internalFormat = compressed.Format switch
        {
            ForgeBlockCompression.Bc4 => InternalFormat.CompressedRedRgtc1,
            ForgeBlockCompression.Bc5 => InternalFormat.CompressedRGRgtc2,
            _ when compressed.Srgb => InternalFormat.CompressedSrgbAlphaS3TCDxt5Ext,
            _ => InternalFormat.CompressedRgbaS3TCDxt5Ext
        };
        for (var level = 0; level < compressed.MipLevels.Count; level++)
        {
            var mip = compressed.MipLevels[level];
            fixed (byte* pointer = mip.Data)
            {
                gl.CompressedTexImage2D(
                    TextureTarget.Texture2D,
                    level,
                    internalFormat,
                    (uint)mip.Width,
                    (uint)mip.Height,
                    0,
                    (uint)mip.Data.Length,
                    pointer);
            }
        }

        var error = gl.GetError();
        if (error != GLEnum.NoError)
        {
            gl.DeleteTexture(Handle);
            throw new InvalidOperationException($"OpenGL compressed texture upload failed: {error}");
        }

        ConfigureSampling(repeat);
    }

    private void ConfigureSampling(bool repeat)
    {
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        var wrap = repeat ? TextureWrapMode.Repeat : TextureWrapMode.ClampToEdge;
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)wrap);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)wrap);
    }
    public uint Handle { get; }
    public int Width { get; }
    public int Height { get; }
    public void Bind(TextureUnit unit)
    {
        gl.ActiveTexture(unit);
        gl.BindTexture(TextureTarget.Texture2D, Handle);
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        gl.DeleteTexture(Handle);
    }
}

public sealed record GpuPbrMaterial(
    string Name,
    Vector4 BaseColorFactor,
    float Metallic,
    float Roughness,
    bool HasNormalMap,
    bool HasAmbientOcclusion,
    bool HasMetallicRoughnessMap,
    Vector3 EmissiveFactor,
    Texture2D? BaseColor,
    Texture2D? Normal,
    Texture2D? MetallicRoughness,
    Texture2D? Occlusion,
    Texture2D? Emissive);

public sealed class GpuMeshAsset : IDisposable
{
    private readonly IReadOnlyList<SharedGpuTextureCache.TextureLease> textureLeases;
    private bool disposed;

    internal GpuMeshAsset(
        MeshBuffer buffer,
        IReadOnlyList<MeshPrimitiveAsset> primitives,
        IReadOnlyList<GpuPbrMaterial> materials,
        IReadOnlyList<SharedGpuTextureCache.TextureLease>? textureLeases = null)
    {
        Buffer = buffer;
        Primitives = primitives;
        Materials = materials;
        this.textureLeases = textureLeases ?? [];
    }

    public MeshBuffer Buffer { get; }
    public IReadOnlyList<MeshPrimitiveAsset> Primitives { get; }
    public IReadOnlyList<GpuPbrMaterial> Materials { get; }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Buffer.Dispose();
        foreach (var lease in textureLeases) lease.Dispose();
    }
}

public sealed class RenderTarget : IDisposable
{
    private readonly GL gl;
    private readonly bool withDepth;
    private readonly bool withEmission;
    private InternalFormat colorFormat;
    private PixelType colorType;
    private bool disposed;

    public RenderTarget(GL gl, int width, int height, bool withDepth, bool preferHdr = false, bool withEmission = false)
    {
        this.gl = gl;
        this.withDepth = withDepth;
        this.withEmission = withEmission;
        colorFormat = preferHdr ? InternalFormat.Rgba16f : InternalFormat.Rgba8;
        colorType = preferHdr ? PixelType.HalfFloat : PixelType.UnsignedByte;
        Framebuffer = gl.GenFramebuffer();
        ColorTexture = gl.GenTexture();
        if (withEmission) EmissionTexture = gl.GenTexture();
        if (withDepth) DepthTexture = gl.GenTexture();
        Resize(width, height);
    }
    public uint Framebuffer { get; }
    public uint ColorTexture { get; }
    public uint EmissionTexture { get; }
    public uint DepthTexture { get; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public bool IsHdr => colorFormat == InternalFormat.Rgba16f;
    public unsafe void Resize(int width, int height)
    {
        width = Math.Max(1, width); height = Math.Max(1, height);
        if (Width == width && Height == height) return;
        Width = width; Height = height;
        gl.BindTexture(TextureTarget.Texture2D, ColorTexture);
        gl.TexImage2D(TextureTarget.Texture2D, 0, colorFormat, (uint)width, (uint)height, 0, PixelFormat.Rgba, colorType, null);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, Framebuffer);
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, ColorTexture, 0);
        if (withEmission)
        {
            AllocateColorTexture(EmissionTexture, width, height);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment1, TextureTarget.Texture2D, EmissionTexture, 0);
            Span<GLEnum> attachments = stackalloc[] { GLEnum.ColorAttachment0, GLEnum.ColorAttachment1 };
            fixed (GLEnum* pointer = attachments) gl.DrawBuffers(2, pointer);
        }
        if (withDepth)
        {
            gl.BindTexture(TextureTarget.Texture2D, DepthTexture);
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.DepthComponent24, (uint)width, (uint)height, 0, PixelFormat.DepthComponent, PixelType.UnsignedInt, null);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, TextureTarget.Texture2D, DepthTexture, 0);
        }
        if (gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != GLEnum.FramebufferComplete && colorFormat == InternalFormat.Rgba16f)
        {
            for (var i = 0; i < 8 && gl.GetError() != GLEnum.NoError; i++) { }
            colorFormat = InternalFormat.Rgba8;
            colorType = PixelType.UnsignedByte;
            gl.BindTexture(TextureTarget.Texture2D, ColorTexture);
            gl.TexImage2D(TextureTarget.Texture2D, 0, colorFormat, (uint)width, (uint)height, 0, PixelFormat.Rgba, colorType, null);
            if (withEmission) AllocateColorTexture(EmissionTexture, width, height);
        }
        EnsureComplete(gl, IsHdr ? "Forge HDR render target" : "Forge color render target");
    }
    public void BindTexture(TextureUnit unit)
    {
        gl.ActiveTexture(unit);
        gl.BindTexture(TextureTarget.Texture2D, ColorTexture);
    }
    public void BindEmissionTexture(TextureUnit unit)
    {
        if (!withEmission) throw new InvalidOperationException("This render target has no emission attachment.");
        gl.ActiveTexture(unit);
        gl.BindTexture(TextureTarget.Texture2D, EmissionTexture);
    }
    public void BindDepthTexture(TextureUnit unit)
    {
        if (!withDepth) throw new InvalidOperationException("This render target has no depth attachment.");
        gl.ActiveTexture(unit);
        gl.BindTexture(TextureTarget.Texture2D, DepthTexture);
    }
    private unsafe void AllocateColorTexture(uint texture, int width, int height)
    {
        gl.BindTexture(TextureTarget.Texture2D, texture);
        gl.TexImage2D(TextureTarget.Texture2D, 0, colorFormat, (uint)width, (uint)height, 0, PixelFormat.Rgba, colorType, null);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
    }
    public static void EnsureComplete(GL gl, string label)
    {
        var status = gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != GLEnum.FramebufferComplete) throw new InvalidOperationException($"{label} is incomplete: {status}");
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (withDepth) gl.DeleteTexture(DepthTexture);
        if (withEmission) gl.DeleteTexture(EmissionTexture);
        gl.DeleteTexture(ColorTexture);
        gl.DeleteFramebuffer(Framebuffer);
    }
}

public sealed class ShadowMap : IDisposable
{
    private readonly GL gl;
    private bool disposed;

    public unsafe ShadowMap(GL gl, int resolution, bool isGles)
    {
        this.gl = gl;
        Resolution = resolution;
        Framebuffer = gl.GenFramebuffer();
        Texture = gl.GenTexture();
        try
        {
            gl.BindTexture(TextureTarget.Texture2D, Texture);
            var internalFormat = isGles ? InternalFormat.DepthComponent16 : InternalFormat.DepthComponent24;
            var pixelType = isGles ? PixelType.UnsignedShort : PixelType.Float;
            gl.TexImage2D(TextureTarget.Texture2D, 0, internalFormat, (uint)resolution, (uint)resolution, 0, PixelFormat.DepthComponent, pixelType, null);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareMode, (int)TextureCompareMode.None);
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, Framebuffer);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, TextureTarget.Texture2D, Texture, 0);
            var none = GLEnum.None;
            gl.DrawBuffers(1, &none);
            gl.ReadBuffer(ReadBufferMode.None);
            RenderTarget.EnsureComplete(gl, "Forge shadow map");
            var shadowError = gl.GetError();
            if (shadowError != GLEnum.NoError)
            {
                throw new InvalidOperationException($"Forge shadow map OpenGL error: {shadowError}");
            }
        }
        catch
        {
            gl.DeleteTexture(Texture);
            gl.DeleteFramebuffer(Framebuffer);
            throw;
        }
    }
    public int Resolution { get; }
    public uint Framebuffer { get; }
    public uint Texture { get; }
    public void BindTexture(TextureUnit unit)
    {
        gl.ActiveTexture(unit);
        gl.BindTexture(TextureTarget.Texture2D, Texture);
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        gl.DeleteTexture(Texture);
        gl.DeleteFramebuffer(Framebuffer);
    }
}

public sealed class FullscreenQuad : IDisposable
{
    private readonly GL gl;
    private bool disposed;
    public FullscreenQuad(GL gl)
    {
        this.gl = gl;
        VertexArray = gl.GenVertexArray();
    }
    public uint VertexArray { get; }
    public void Draw()
    {
        gl.BindVertexArray(VertexArray);
        gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        gl.DeleteVertexArray(VertexArray);
    }
}
