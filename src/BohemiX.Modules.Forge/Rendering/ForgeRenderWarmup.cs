namespace BohemiX.Modules.Forge.Rendering;

public static class ForgeRenderWarmup
{
    private static readonly Lazy<Task<ForgeProceduralTextureData>> ProceduralTextures = new(
        () => Task.Run(CreateProceduralTextures),
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static void Begin() => _ = ProceduralTextures.Value;

    internal static bool TryGetProceduralTextures(out ForgeProceduralTextureData? textures)
    {
        var task = ProceduralTextures.IsValueCreated ? ProceduralTextures.Value : null;
        if (task is not { IsCompletedSuccessfully: true })
        {
            textures = null;
            return false;
        }

        textures = task.Result;
        return true;
    }

    internal static Task<ForgeProceduralTextureData> GetProceduralTexturesAsync() =>
        ProceduralTextures.Value;

    private static ForgeProceduralTextureData CreateProceduralTextures()
    {
        var color = ProceduralTextureAtlas.CreateColorAtlas();
        var normal = ProceduralTextureAtlas.CreateNormalAtlas(color);
        return new ForgeProceduralTextureData(color, normal);
    }
}

internal sealed record ForgeProceduralTextureData(byte[] Color, byte[] Normal);
