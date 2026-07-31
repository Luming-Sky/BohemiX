namespace BohemiX.Modules.Forge.Models;

public sealed class ForgeGraphicsSettings
{
    public ForgeRenderQuality RenderQuality { get; private set; } = ForgeRenderQuality.Medium;
    public ForgeTextureQuality TextureQuality { get; private set; } = ForgeTextureQuality.Medium;

    public ForgeRenderSettings RenderSettings => RenderQuality switch
    {
        ForgeRenderQuality.Low => ForgeRenderSettings.Low,
        ForgeRenderQuality.High => ForgeRenderSettings.High,
        _ => ForgeRenderSettings.Medium
    };

    public void Apply(string? renderQuality, string? textureQuality)
    {
        RenderQuality = ParseRenderQuality(renderQuality);
        TextureQuality = ParseTextureQuality(textureQuality);
    }

    public static ForgeRenderQuality ParseRenderQuality(string? value) =>
        Enum.TryParse<ForgeRenderQuality>(value, true, out var quality)
            ? quality
            : ForgeRenderQuality.Medium;

    public static ForgeTextureQuality ParseTextureQuality(string? value) =>
        Enum.TryParse<ForgeTextureQuality>(value, true, out var quality)
            ? quality
            : ForgeTextureQuality.Medium;

    public static int MaximumTextureDimension(ForgeTextureQuality quality) => quality switch
    {
        ForgeTextureQuality.Low => 256,
        ForgeTextureQuality.High => 1024,
        _ => 512
    };

    public static int MaximumBackgroundWidth(ForgeTextureQuality quality) => quality switch
    {
        ForgeTextureQuality.Low => 1024,
        ForgeTextureQuality.High => 2048,
        _ => 1280
    };
}
