using System.Numerics;

namespace BohemiX.Modules.Forge.Rendering;

public enum ForgeMaterialId
{
    Iron,
    IronDark,
    Scale,
    Brass,
    Wood,
    WoodDark,
    Leather,
    Stone,
    Brick,
    Coal,
    Workpiece,
    Water,
    Oil,
    WorkpieceEdge
}

public readonly record struct ForgeMaterial(
    Vector3 Albedo,
    float Metallic,
    float Roughness,
    float AmbientOcclusion,
    Vector3 Emissive,
    float TextureScale)
{
    public bool IsValid => Metallic is >= 0 and <= 1 && Roughness is >= .04f and <= 1 &&
                           AmbientOcclusion is >= 0 and <= 1 && TextureScale > 0;
}

public sealed class MaterialLibrary
{
    private readonly ForgeMaterial[] materials =
    [
        new(new(.285f, .305f, .34f), .93f, .44f, .88f, Vector3.Zero, 4.4f),
        new(new(.085f, .095f, .112f), .88f, .62f, .75f, Vector3.Zero, 5.1f),
        new(new(.032f, .026f, .023f), .58f, .84f, .57f, Vector3.Zero, 7.2f),
        new(new(.34f, .165f, .042f), .91f, .32f, .84f, Vector3.Zero, 2.8f),
        new(new(.115f, .043f, .014f), .02f, .66f, .68f, Vector3.Zero, 1.9f),
        new(new(.052f, .018f, .007f), .01f, .80f, .60f, Vector3.Zero, 2.2f),
        new(new(.095f, .035f, .014f), .01f, .79f, .68f, Vector3.Zero, 3.0f),
        new(new(.19f, .176f, .15f), 0f, .88f, .86f, Vector3.Zero, 5.2f),
        new(new(.105f, .031f, .014f), .02f, .94f, .68f, Vector3.Zero, 3.6f),
        new(new(.018f, .014f, .012f), .08f, .93f, .60f, new(.10f, .018f, .003f), 5.5f),
        new(new(.34f, .375f, .43f), .94f, .48f, .88f, Vector3.Zero, 6.4f),
        new(new(.045f, .10f, .115f), .04f, .12f, .92f, Vector3.Zero, 1f),
        new(new(.105f, .055f, .018f), .02f, .20f, .84f, Vector3.Zero, 1f),
        new(new(.52f, .57f, .64f), 1.0f, .14f, .94f, Vector3.Zero, 7.0f)
    ];

    public int Count => materials.Length;
    public ForgeMaterial this[ForgeMaterialId id] => materials[(int)id];
    public IReadOnlyList<ForgeMaterial> All => materials;

    public static Vector3 HeatColor(double heat)
    {
        var t = Math.Clamp((heat - .12) / .95, 0, 1);
        if (t <= 0) return Vector3.Zero;
        var red = Math.Clamp(t * 2.65, 0, 1);
        var green = Math.Clamp((t - .26) * 1.72, 0, .88);
        var blue = Math.Clamp((t - .68) * 1.65, 0, .44);
        return new Vector3((float)red, (float)green, (float)blue);
    }
}

public static class ProceduralTextureAtlas
{
    public const int TileSize = 1024;
    public const int Size = TileSize * 2;

    public static byte[] CreateColorAtlas(int seed = 932_117)
    {
        var data = new byte[Size * Size * 4];
        FillTile(data, 0, 0, TextureKind.Metal, seed);
        FillTile(data, 1, 0, TextureKind.Wood, seed + 17);
        FillTile(data, 0, 1, TextureKind.Stone, seed + 31);
        FillTile(data, 1, 1, TextureKind.Scale, seed + 47);
        return data;
    }

    public static byte[] CreateNormalAtlas(int seed = 932_117)
    {
        var color = CreateColorAtlas(seed);
        return CreateNormalAtlas(color);
    }

    internal static byte[] CreateNormalAtlas(byte[] color)
    {
        ArgumentNullException.ThrowIfNull(color);
        if (color.Length != Size * Size * 4)
        {
            throw new ArgumentException("The Forge color atlas has an unexpected size.", nameof(color));
        }

        var data = new byte[color.Length];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var l = Luma(color, Math.Max(0, x - 1), y);
                var r = Luma(color, Math.Min(Size - 1, x + 1), y);
                var d = Luma(color, x, Math.Max(0, y - 1));
                var u = Luma(color, x, Math.Min(Size - 1, y + 1));
                var normal = Vector3.Normalize(new Vector3((l - r) * .72f, (d - u) * .72f, 1));
                var i = (y * Size + x) * 4;
                data[i] = (byte)((normal.X * .5f + .5f) * 255);
                data[i + 1] = (byte)((normal.Y * .5f + .5f) * 255);
                data[i + 2] = (byte)((normal.Z * .5f + .5f) * 255);
                data[i + 3] = 255;
            }
        }
        return data;
    }

    private static void FillTile(byte[] data, int tileX, int tileY, TextureKind kind, int seed)
    {
        for (var y = 0; y < TileSize; y++)
        for (var x = 0; x < TileSize; x++)
        {
            var n = FractalNoise(x, y, seed);
            var value = kind switch
            {
                TextureKind.Wood => .38f + .34f * MathF.Sin(y * .09f + n * 5.4f) + n * .18f,
                TextureKind.Metal => .46f + n * .13f + MathF.Sin((x + y) * .105f) * .024f + MathF.Sin(y * .31f) * .010f,
                TextureKind.Stone => StoneValue(x, y, n, seed),
                _ => .20f + n * .48f + (Hash01(x, y, seed + 173) > .94f ? .35f : 0)
            };
            value = Math.Clamp(value, 0, 1);
            var i = (((tileY * TileSize + y) * Size) + tileX * TileSize + x) * 4;
            var roughness = kind switch
            {
                TextureKind.Metal => .34f + (1 - value) * .34f + MathF.Abs(MathF.Sin(y * .23f)) * .045f,
                TextureKind.Wood => .62f + (1 - value) * .25f,
                TextureKind.Stone => .82f + (1 - value) * .15f,
                _ => .68f + (1 - value) * .28f
            };
            var ao = Math.Clamp(.70f + value * .28f, 0, 1);
            data[i] = (byte)(value * 255);
            data[i + 1] = (byte)(Math.Clamp(roughness, 0, 1) * 255);
            data[i + 2] = (byte)(ao * 255);
            data[i + 3] = 255;
        }
    }

    private static float FractalNoise(int x, int y, int seed) =>
        ValueNoise(x, y, seed, 64) * .54f + ValueNoise(x, y, seed + 101, 23) * .31f + ValueNoise(x, y, seed + 251, 7) * .15f;

    private static float StoneValue(int x, int y, float noise, int seed)
    {
        var coarse = ValueNoise(x, y, seed + 401, 91);
        var aggregateNoise = ValueNoise(x, y, seed + 463, 19);
        var grit = Hash01(x, y, seed + 509);
        var aggregate = grit > .984f ? .19f : grit < .014f ? -.17f : 0;
        return .34f + coarse * .27f + aggregateNoise * .15f + noise * .09f + aggregate;
    }

    private static float ValueNoise(int x, int y, int seed, int period)
    {
        var gx = x / period; var gy = y / period;
        var tx = SmoothStep((x % period) / (float)period); var ty = SmoothStep((y % period) / (float)period);
        var a = Lerp(Hash01(gx, gy, seed), Hash01(gx + 1, gy, seed), tx);
        var b = Lerp(Hash01(gx, gy + 1, seed), Hash01(gx + 1, gy + 1, seed), tx);
        return Lerp(a, b, ty);
    }

    private static float Hash01(int x, int y, int seed)
    {
        var value = unchecked((uint)(x * 374761393 + y * 668265263 + seed * 1442695041));
        value = (value ^ (value >> 13)) * 1274126177u;
        return (value ^ (value >> 16)) / (float)uint.MaxValue;
    }

    private static float SmoothStep(float value) => value * value * (3 - 2 * value);
    private static float Lerp(float a, float b, float amount) => a + (b - a) * amount;

    private static float Luma(byte[] data, int x, int y) => data[(y * Size + x) * 4] / 255f;
    private enum TextureKind { Metal, Wood, Stone, Scale }
}

public static class ForgeEnvironmentTextures
{
    public const int EnvironmentWidth = 512;
    public const int EnvironmentHeight = 256;
    public const int BrdfSize = 256;

    public static byte[] CreateEnvironment()
    {
        var data = new byte[EnvironmentWidth * EnvironmentHeight * 4];
        var forgeDirection = Vector3.Normalize(new Vector3(-.62f, .18f, .76f));
        var coolWindowDirection = Vector3.Normalize(new Vector3(.28f, .72f, -.63f));
        var softboxDirection = Vector3.Normalize(new Vector3(.62f, .38f, .69f));
        for (var y = 0; y < EnvironmentHeight; y++)
        for (var x = 0; x < EnvironmentWidth; x++)
        {
            var u = (x + .5f) / EnvironmentWidth;
            var v = (y + .5f) / EnvironmentHeight;
            var phi = (u - .5f) * MathF.Tau;
            var elevation = (v - .5f) * MathF.PI;
            var direction = new Vector3(MathF.Cos(elevation) * MathF.Cos(phi), MathF.Sin(elevation), MathF.Cos(elevation) * MathF.Sin(phi));
            var sky = Math.Clamp(direction.Y * .5f + .5f, 0, 1);
            var color = Vector3.Lerp(new Vector3(.018f, .013f, .011f), new Vector3(.185f, .225f, .29f), MathF.Pow(sky, .68f));
            var forge = MathF.Pow(Math.Max(0, Vector3.Dot(direction, forgeDirection)), 30) * 1.55f;
            color += new Vector3(1f, .17f, .025f) * forge;
            var window = MathF.Pow(Math.Max(0, Vector3.Dot(direction, coolWindowDirection)), 96) * 1.35f;
            color += new Vector3(.72f, .86f, 1f) * window;
            var softbox = MathF.Pow(Math.Max(0, Vector3.Dot(direction, softboxDirection)), 18) * .28f;
            color += new Vector3(.48f, .56f, .68f) * softbox;
            var i = (y * EnvironmentWidth + x) * 4;
            data[i] = ToByte(color.X); data[i + 1] = ToByte(color.Y); data[i + 2] = ToByte(color.Z); data[i + 3] = 255;
        }
        return data;
    }

    public static byte[] CreateBrdfLut()
    {
        var data = new byte[BrdfSize * BrdfSize * 4];
        for (var y = 0; y < BrdfSize; y++)
        for (var x = 0; x < BrdfSize; x++)
        {
            var ndv = (x + .5f) / BrdfSize;
            var roughness = (y + .5f) / BrdfSize;
            var grazing = MathF.Pow(1 - ndv, 5);
            var scale = Math.Clamp(1 - roughness * .32f - grazing * .18f, 0, 1);
            var bias = Math.Clamp(grazing * (.42f - roughness * .24f), 0, 1);
            var i = (y * BrdfSize + x) * 4;
            data[i] = ToByte(scale); data[i + 1] = ToByte(bias); data[i + 2] = 0; data[i + 3] = 255;
        }
        return data;
    }

    private static byte ToByte(float value) => (byte)(Math.Clamp(value, 0, 1) * 255);
}
