using Avalonia;
using BohemiX.Modules.Alchemy.Data;
using BohemiX.Modules.Alchemy.Models;
using BohemiX.Modules.Alchemy.ViewModels;

namespace BohemiX.Modules.Alchemy.Controls;

internal enum AlchemyHudTone
{
    Neutral,
    Info,
    Warning,
    Danger,
    Success
}

internal enum AlchemyInteractionState
{
    Default,
    Hover,
    Pressed,
    Selected,
    Disabled,
    Focused
}

internal enum AlchemyHudTargetKind
{
    None,
    RecipeBook,
    Reset,
    RecipePrevious,
    RecipeNext,
    RecipeClose,
    Ingredient,
    BaseLiquid,
    QuantityOption,
    MortarPowder,
    Pestle,
    Bellows,
    Hourglass,
    CauldronHook,
    DistillerLever,
    ProductBottle
}

internal readonly record struct AlchemyHudTarget(AlchemyHudTargetKind Kind, int Index = -1)
{
    public static AlchemyHudTarget None => new(AlchemyHudTargetKind.None);

    public bool IsNone => Kind == AlchemyHudTargetKind.None;

    public bool IsTool => Kind is
        AlchemyHudTargetKind.MortarPowder or
        AlchemyHudTargetKind.Pestle or
        AlchemyHudTargetKind.Bellows or
        AlchemyHudTargetKind.Hourglass or
        AlchemyHudTargetKind.CauldronHook or
        AlchemyHudTargetKind.DistillerLever or
        AlchemyHudTargetKind.ProductBottle;
}

internal readonly record struct AlchemyInteractionVisual(
    uint Surface,
    uint Border,
    uint Text,
    double BorderThickness,
    double ContentOffset,
    double ShadowOffset,
    double Opacity);

internal static class AlchemyInteractionPresentation
{
    public const double FeedbackSeconds = 0.12;
    public const double PressedOffset = 1.5;

    public static AlchemyInteractionVisual From(AlchemyInteractionState state) => state switch
    {
        AlchemyInteractionState.Hover => new(
            AlchemyHudTheme.InteractiveHover,
            AlchemyHudTheme.AccentBright,
            AlchemyHudTheme.TextPrimary,
            1.6,
            0,
            3,
            1),
        AlchemyInteractionState.Pressed => new(
            AlchemyHudTheme.InteractivePressed,
            AlchemyHudTheme.AccentBright,
            AlchemyHudTheme.TextPrimary,
            1.8,
            PressedOffset,
            1,
            1),
        AlchemyInteractionState.Selected => new(
            AlchemyHudTheme.InteractiveSelected,
            AlchemyHudTheme.AccentBright,
            AlchemyHudTheme.TextPrimary,
            1.8,
            0,
            3,
            1),
        AlchemyInteractionState.Disabled => new(
            AlchemyHudTheme.Disabled,
            AlchemyHudTheme.BorderMuted,
            AlchemyHudTheme.TextMuted,
            1,
            0,
            1,
            .58),
        AlchemyInteractionState.Focused => new(
            AlchemyHudTheme.SurfaceRaised,
            AlchemyHudTheme.FocusRing,
            AlchemyHudTheme.TextPrimary,
            2,
            0,
            3,
            1),
        _ => new(
            AlchemyHudTheme.SurfaceInset,
            AlchemyHudTheme.BrassDark,
            AlchemyHudTheme.TextSecondary,
            1.1,
            0,
            3,
            1)
    };
}

internal static class AlchemyHudTheme
{
    public const uint Surface = 0xF218110C;
    public const uint SurfaceRaised = 0xF2241A12;
    public const uint SurfaceSoft = 0xD92C2016;
    public const uint SurfaceOuter = 0xFA0D0907;
    public const uint SurfaceInset = 0xF016100C;
    public const uint InteractiveHover = 0xF02E2117;
    public const uint InteractivePressed = 0xFA120D09;
    public const uint InteractiveSelected = 0xF238291A;
    public const uint SurfaceHighlight = 0x4CE8D2A2;
    public const uint HudShadow = 0x99000000;
    public const uint Border = 0xFF745236;
    public const uint BorderMuted = 0xA65D4632;
    public const uint BorderDark = 0xFF382619;
    public const uint TextPrimary = 0xFFF1DFB5;
    public const uint TextSecondary = 0xFFC3AD86;
    public const uint TextMuted = 0xFF8F7A5F;
    public const uint Accent = 0xFFB9853A;
    public const uint AccentBright = 0xFFE1B968;
    public const uint FocusRing = 0xFFF0C978;
    public const uint DangerSurface = 0xFF48201B;
    public const uint DangerBorder = 0xFFD06A58;
    public const uint BrassDark = 0xFF5B3B20;
    public const uint BrassMid = 0xFF9B6D32;
    public const uint Steel = 0xFF8993A3;
    public const uint Cold = 0xFF668C94;
    public const uint Simmering = 0xFFD2A94D;
    public const uint Boiling = 0xFFE36D32;
    public const uint Danger = 0xFFC84637;
    public const uint Success = 0xFF6F9562;
    public const uint Info = 0xFF6E9BA3;
    public const uint Disabled = 0xB314100D;
    public const uint ModalScrim = 0xC9120D09;
    public const uint BookCover = 0xFF3A2417;
    public const uint BookCoverEdge = 0xFF8A6036;
    public const uint BookPage = 0xFFE8D2A2;
    public const uint BookPageSecondary = 0xFFE0C491;
    public const uint BookInk = 0xFF302116;
    public const uint BookInkMuted = 0xFF75583A;
    public const uint BookRule = 0x8F9A774E;

    public static uint ColorFor(AlchemyHudTone tone) => tone switch
    {
        AlchemyHudTone.Info => Info,
        AlchemyHudTone.Warning => Simmering,
        AlchemyHudTone.Danger => Danger,
        AlchemyHudTone.Success => Success,
        _ => TextSecondary
    };
}

internal static class AlchemyHudLayout
{
    public const double SceneWidth = 1440;
    public const double SceneHeight = 820;
    public const double HeaderHeight = 72;
    public const double BottomRailHeight = 78;

    public static readonly Rect Header = new(0, 0, SceneWidth, HeaderHeight);
    // The first 58 logical pixels are reserved for the Avalonia exit button.
    public static readonly Rect RecipeButton = new(72, 13, 152, 46);
    public static readonly Rect HeaderRecipe = new(244, 12, 256, 48);
    public static readonly Rect HeaderStep = new(518, 10, 526, 50);
    public static readonly Rect HeaderDeviation = new(1064, 13, 142, 46);
    public static readonly Rect HeaderOutcome = new(1218, 13, 137, 46);
    public static readonly Rect ResetButton = new(1373, 14, 48, 42);

    public static readonly Rect Inventory = new(0, HeaderHeight, 274, 670);
    public static readonly Rect Feedback = new(478, 706, 536, 27);
    public static readonly Rect BottomRail = new(0, 742, SceneWidth, BottomRailHeight);
    public static readonly Rect TemperatureMetric = new(20, 752, 686, 58);
    public static readonly Rect ProcessMetric = new(724, 752, 302, 58);
    public static readonly Rect ResultMetric = new(1044, 752, 376, 58);

    public static readonly Rect RecipeBook = new(244, 86, 952, 624);
    public static readonly Rect RecipeBookPrevious = new(282, 646, 52, 44);
    public static readonly Rect RecipeBookNext = new(1106, 646, 52, 44);
    public static readonly Rect RecipeBookClose = new(1138, 104, 36, 36);
    public static readonly Rect RecipeBookLeftPage = new(262, 104, 451, 582);
    public static readonly Rect RecipeBookRightPage = new(727, 104, 451, 582);
    public static readonly Rect BrewShowcase = new(226, 70, 988, 680);
    public static readonly Point BrewShowcaseBottleStart = new(1323, 690);
    public static readonly Point BrewShowcaseBottleCenter = new(610, 360);

    public static readonly Rect Mortar = new(402, 358, 250, 252);
    public static readonly Rect MortarDrop = new(374, 344, 324, 288);
    public static readonly Rect MortarPowder = new(438, 470, 180, 66);
    public static readonly Rect Pestle = new(450, 322, 152, 246);
    public static readonly Rect Cauldron = new(620, 248, 398, 438);
    public static readonly Rect CauldronMouth = new(650, 304, 330, 126);
    public static readonly Rect CauldronHook = new(754, 144, 102, 112);
    public static readonly Rect BellowsHandle = new(924, 492, 310, 188);
    public static readonly Rect Hourglass = new(1040, 244, 112, 168);
    public static readonly Rect DistillerLever = new(1170, 370, 152, 236);
    public static readonly Rect ProductBottle = new(304, 614, 72, 105);
    public static readonly Rect DistillerReceiver = new(1244, 554, 128, 128);

    public static IReadOnlyList<Rect> ToolHitboxes { get; } =
    [
        Mortar,
        MortarDrop,
        MortarPowder,
        Pestle,
        Cauldron,
        CauldronMouth,
        CauldronHook,
        BellowsHandle,
        Hourglass,
        DistillerLever,
        ProductBottle,
        DistillerReceiver
    ];

    public static Rect StepSegment(int index, int count)
    {
        if (count <= 0 || index < 0 || index >= count)
        {
            return default;
        }

        const double gap = 5;
        var available = HeaderStep.Width - 2;
        var width = (available - ((count - 1) * gap)) / count;
        return new Rect(HeaderStep.X + 1 + (index * (width + gap)), 55, width, 4);
    }

    public static Rect RecipeStep(int index)
    {
        var column = index < 4 ? 0 : 1;
        var row = index % 4;
        return new Rect(286 + (column * 466), 246 + (row * 88), 402, 76);
    }
}

internal readonly record struct AlchemyHudPresentation(
    string StepNumber,
    string ProcessLabel,
    string ProcessValue,
    string OutcomeLabel,
    string YieldValue,
    string DeviationValue,
    string Feedback,
    AlchemyHudTone OutcomeTone,
    AlchemyHudTone FeedbackTone)
{
    public static AlchemyHudPresentation From(AlchemyWorkshopViewModel? vm)
    {
        if (vm is null)
        {
            return new("--/--", "当前步骤", "--", "炼制中", "--", "0", string.Empty,
                AlchemyHudTone.Neutral, AlchemyHudTone.Neutral);
        }

        var snapshot = vm.Snapshot;
        return From(new AlchemyHudSource(
            snapshot.Recipe.Steps.Count,
            snapshot.CurrentStepIndex,
            vm.IsHourglassRunning,
            vm.HourglassProgress,
            vm.IsMortarLoaded,
            vm.GrindingProgress,
            snapshot.Distilled,
            snapshot.Outcome,
            snapshot.Yield,
            snapshot.Mistakes,
            vm.LastMessage), vm.UseEnglish);
    }

    internal static AlchemyHudPresentation From(AlchemyHudSource source, bool useEnglish = false)
    {
        var total = source.RecipeStepCount;
        var current = total > 0 ? Math.Min(source.CurrentStepIndex + 1, total) : 0;
        var (processLabel, processValue) = ProcessFor(source, useEnglish);
        var (outcomeLabel, outcomeTone) = OutcomeFor(source.Outcome, source.Mistakes, useEnglish);
        var feedbackTone = source.Outcome switch
        {
            BrewOutcome.Failed or BrewOutcome.Perfect or BrewOutcome.Diluted => outcomeTone,
            _ when source.Mistakes > 0 => AlchemyHudTone.Warning,
            _ => AlchemyHudTone.Neutral
        };

        return new AlchemyHudPresentation(
            total > 0 ? $"{current:00}/{total:00}" : "--/--",
            processLabel,
            processValue,
            outcomeLabel,
            source.Outcome == BrewOutcome.Brewing
                ? "--"
                : useEnglish
                    ? $"{source.Yield} bottles"
                    : $"{source.Yield} 瓶",
            source.Mistakes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            source.LastMessage,
            outcomeTone,
            feedbackTone);
    }

    private static (string Label, string Value) ProcessFor(AlchemyHudSource source, bool useEnglish)
    {
        if (source.HourglassRunning)
        {
            return (AlchemyTextCatalog.Get(AlchemyTextKey.Hourglass, useEnglish),
                source.HourglassProgress.ToString("P0", System.Globalization.CultureInfo.CurrentCulture));
        }

        if (source.MortarLoaded)
        {
            return (AlchemyTextCatalog.Get(AlchemyTextKey.Grinding, useEnglish), source.GrindingProgress >= 0.999
                ? AlchemyTextCatalog.Get(AlchemyTextKey.Complete, useEnglish)
                : source.GrindingProgress.ToString("P0", System.Globalization.CultureInfo.CurrentCulture));
        }

        if (source.Distilled)
        {
            return (AlchemyTextCatalog.Get(AlchemyTextKey.Distilling, useEnglish),
                AlchemyTextCatalog.Get(AlchemyTextKey.Ready, useEnglish));
        }

        return (AlchemyTextCatalog.Get(AlchemyTextKey.CurrentStep, useEnglish), "--");
    }

    internal static (string Label, AlchemyHudTone Tone) OutcomeFor(
        BrewOutcome outcome,
        int mistakes = 0,
        bool useEnglish = false)
    {
        if (outcome == BrewOutcome.Brewing)
        {
            return (AlchemyTextCatalog.Get(AlchemyTextKey.Brewing, useEnglish), AlchemyHudTone.Neutral);
        }

        var completion = AlchemyCompletionPresentation.From(outcome, mistakes, 0, useEnglish);
        return (completion.Grade, completion.Tone);
    }
}

internal readonly record struct AlchemyHudSource(
    int RecipeStepCount,
    int CurrentStepIndex,
    bool HourglassRunning,
    double HourglassProgress,
    bool MortarLoaded,
    double GrindingProgress,
    bool Distilled,
    BrewOutcome Outcome,
    int Yield,
    int Mistakes,
    string LastMessage);

internal readonly record struct AlchemyCompletionPresentation(
    int QualityScore,
    string Grade,
    string OutcomeLabel,
    string YieldLabel,
    AlchemyHudTone Tone)
{
    public static AlchemyCompletionPresentation From(
        BrewOutcome outcome,
        int mistakes,
        int yield,
        bool useEnglish = false)
    {
        if (outcome == BrewOutcome.Brewing)
        {
            return new(0, "--", AlchemyTextCatalog.Get(AlchemyTextKey.Brewing, useEnglish), "--", AlchemyHudTone.Neutral);
        }

        var score = outcome == BrewOutcome.Failed
            ? 0
            : outcome == BrewOutcome.Perfect
                ? 100
                : Math.Clamp(84 - (Math.Max(1, mistakes) - 1) * 7, 0, 84);
        var (grade, tone) = score switch
        {
            >= 95 => (useEnglish ? "Perfect" : "完美", AlchemyHudTone.Success),
            >= 80 => (useEnglish ? "Excellent" : "优秀", AlchemyHudTone.Info),
            >= 60 => (useEnglish ? "Fair" : "普通", AlchemyHudTone.Warning),
            _ => (useEnglish ? "Poor" : "糟糕", AlchemyHudTone.Danger)
        };

        return new(score, grade,
            outcome == BrewOutcome.Failed
                ? useEnglish ? "Brew failed" : "炼制失败"
                : AlchemyTextCatalog.Get(AlchemyTextKey.BrewComplete, useEnglish),
            outcome == BrewOutcome.Failed
                ? "--"
                : useEnglish ? $"{yield} bottles" : $"{yield} 瓶",
            tone);
    }
}
