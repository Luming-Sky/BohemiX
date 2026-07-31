namespace BohemiX.Modules.Forge.Models;

public enum ForgeStateId
{
    RecipeSelect,
    MaterialSelect,
    Heating,
    Hammering,
    RotateWorkpiece,
    Reheat,
    Quenching,
    Grinding,
    Inspection,
    Result
}

public enum ForgeHeatBand
{
    Cold,
    DarkRed,
    CherryRed,
    OrangeRed,
    Yellow,
    Burnt
}

public enum HammerFace
{
    Flat,
    [Obsolete("Forge Simulator now uses the flat hammer face only.")]
    CrossPeen
}

public enum QuenchMedium
{
    Water,
    Oil
}

public enum ForgeQuality
{
    Broken,
    Poor,
    Normal,
    Fine,
    Excellent,
    Masterwork
}

public enum ForgeVisualEventKind
{
    None,
    HammerStrike,
    WorkpieceRotated,
    BellowsPumped,
    QuenchStarted,
    QuenchUpdated,
    QuenchCompleted,
    GrindingStroke
}

public sealed record ForgeVisualEvent(
    long Sequence,
    ForgeVisualEventKind Kind,
    double X = 0.5,
    double Y = 0.5,
    double Intensity = 1,
    QuenchMedium? Medium = null);

public enum ForgeRenderQuality
{
    Low,
    Medium,
    High
}

public enum ForgeTextureQuality
{
    Low,
    Medium,
    High
}

public sealed record ForgeRenderSettings(
    ForgeRenderQuality Quality = ForgeRenderQuality.Medium,
    bool ReducedMotion = false,
    int ShadowResolution = 1024,
    double ParticleDensity = .55,
    bool BloomEnabled = true,
    bool DepthOfFieldEnabled = false)
{
    public static ForgeRenderSettings High { get; } = new(
        ForgeRenderQuality.High,
        ShadowResolution: 1536,
        ParticleDensity: 1,
        DepthOfFieldEnabled: true);
    public static ForgeRenderSettings Medium { get; } = new(
        ForgeRenderQuality.Medium,
        ShadowResolution: 1024,
        ParticleDensity: .55,
        DepthOfFieldEnabled: false);
    public static ForgeRenderSettings Low { get; } = new(
        ForgeRenderQuality.Low,
        ShadowResolution: 768,
        ParticleDensity: .35,
        DepthOfFieldEnabled: false);
}

public sealed record ForgeZoneDefinition(
    string Id,
    string Label,
    int Order,
    double IdealHeatMin,
    double IdealHeatMax,
    int RecommendedMinStrikes,
    int RecommendedMaxStrikes,
    int RecommendedFlipAfterStrikes,
    double Start = 0,
    double End = 1,
    double YMin = 0,
    double YMax = 1,
    HammerFace? RecommendedFace = null,
    string? LabelZh = null);

public static class HammerFacePolicy
{
    public static HammerFace Normalize(HammerFace _) => HammerFace.Flat;
}

public sealed record ForgeOutlinePointDefinition(
    double U,
    double Lower,
    double Upper);

public sealed record ForgeBilletDefinition(
    double Start,
    double End,
    IReadOnlyList<ForgeOutlinePointDefinition> Outline,
    double MassScale = 1.0);

public sealed record ShapeTemplateDefinition(
    string Kind,
    double Length,
    double Width,
    double Thickness,
    double EdgeWidth,
    double TipTaper,
    double EyeWidth = 0,
    double EyeHeight = 0,
    IReadOnlyList<ForgeOutlinePointDefinition>? Outline = null,
    double EyeCenterX = .47,
    double EyeCenterY = -.005);

public sealed record ForgeRecipeVisualDefinition(
    string ThumbnailAsset,
    string FinishedModelAsset,
    string FittingsModelAsset,
    double RenderLength,
    double RenderWidth,
    int RecommendedDevelopmentStrikes,
    double FittingsScale = 1,
    double FittingsOffsetX = 0,
    double FittingsOffsetY = 0,
    double FittingsOffsetZ = 0);

public sealed record ForgeRecipeDefinition(
    string Id,
    string Name,
    string Description,
    ShapeTemplateDefinition Shape,
    IReadOnlyList<ForgeZoneDefinition> Zones,
    double CompletionTolerance,
    IReadOnlyDictionary<string, QuenchMedium> RecommendedQuenchByMaterial,
    int RecommendedReheatMin,
    int RecommendedReheatMax,
    double GrindTargetSpeed,
    double GrindSpeedTolerance,
    string? NameZh = null,
    string? DescriptionZh = null,
    ForgeRecipeVisualDefinition? Visual = null,
    ForgeBilletDefinition? Billet = null);

public sealed record ForgeMaterialDefinition(
    string Id,
    string Name,
    string Description,
    double PlasticityPeak,
    double PlasticityWidth,
    double CoolingRate,
    double OverheatThreshold,
    double ColdWorkThreshold,
    double CrackSensitivity,
    double WaterHardening,
    double OilHardening,
    double GrindHardness);

public sealed record ForgeHammerDefinition(
    string Id,
    string Name,
    double FlatRadius,
    double PeenRadius,
    double ForceMultiplier);

public sealed record HammerStrikeCommand(double X, double Y, double Force, HammerFace Face) : ForgeCommand;
public sealed record SelectRecipeCommand(string RecipeId) : ForgeCommand;
public sealed record SelectMaterialCommand(string MaterialId) : ForgeCommand;
public sealed record StartHeatingCommand : ForgeCommand;
public sealed record PumpBellowsCommand(double Strength) : ForgeCommand;
public sealed record MoveToAnvilCommand : ForgeCommand;
public sealed record MoveToForgeCommand : ForgeCommand;
public sealed record RotateWorkpieceCommand : ForgeCommand;
public sealed record BeginQuenchCommand(QuenchMedium Medium) : ForgeCommand;
public sealed record UpdateQuenchCommand(double Depth, double Speed) : ForgeCommand;
public sealed record CompleteQuenchCommand : ForgeCommand;
public sealed record BeginGrindingCommand : ForgeCommand;
public sealed record SetGrinderSpeedCommand(double Speed) : ForgeCommand;
public sealed record SkipGrindingCommand : ForgeCommand;
public sealed record GrindStrokeCommand(double Position, double Speed, double Delta) : ForgeCommand;
public sealed record SubmitInspectionCommand : ForgeCommand;
public sealed record ToggleHammerFaceCommand : ForgeCommand;
public sealed record RestartForgeCommand : ForgeCommand;
public sealed record AbandonForgeCommand : ForgeCommand;

public abstract record ForgeCommand;

public sealed record ShapeCellSnapshot(
    int X,
    int Y,
    double Occupancy,
    double Thickness,
    double Damage,
    double Finish,
    double Formation = 0);

public sealed record ForgeFormationGuide(
    string ZoneId,
    double Start,
    double End,
    double Progress,
    bool IsCorrection);

public sealed record ShapeMetrics(
    double Error,
    double OutlineError,
    double ThicknessError,
    double EdgeError,
    double FeatureError,
    double StraightnessError,
    bool StructurallySound,
    double MaterialMass,
    double TargetMass);

public sealed record QualityReason(string Category, string Message, bool Positive);

public sealed record ForgeResult(
    ForgeQuality Quality,
    int Score,
    IReadOnlyList<QualityReason> Reasons,
    ShapeMetrics Shape,
    int Strikes,
    int Reheats,
    QuenchMedium? QuenchMedium,
    double GrindCoverage);

public sealed record ForgeSnapshot(
    ForgeStateId State,
    string? RecipeId,
    string? MaterialId,
    string? RecipeName,
    string? MaterialName,
    ForgeHeatBand HeatBand,
    double Heat,
    bool IsFlipped,
    HammerFace HammerFace,
    HammerFace? RecommendedHammerFace,
    int StrikeCount,
    int ReheatCount,
    double ShapeError,
    double GrindCoverage,
    string? ActiveZone,
    string LastMessage,
    bool CanProceed,
    ForgeResult? Result,
    IReadOnlyList<ShapeCellSnapshot> ShapeCells,
    long ShapeRevision,
    ForgeVisualEvent? VisualEvent,
    ShapeTemplateDefinition? RecipeShape = null,
    ForgeRecipeVisualDefinition? RecipeVisual = null,
    QuenchMedium? ActiveQuenchMedium = null,
    bool GrindingEngaged = false,
    double GrinderSpeed = 0,
    double GrindingQuality = 0);

public sealed record ForgeCommandResult(bool Accepted, string Message, ForgeSnapshot Snapshot);
