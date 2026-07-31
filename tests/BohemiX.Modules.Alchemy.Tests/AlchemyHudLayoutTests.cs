using Avalonia;
using BohemiX.Modules.Alchemy.Controls;
using BohemiX.Modules.Alchemy.Models;
using Xunit;

namespace BohemiX.Modules.Alchemy.Tests;

public sealed class AlchemyHudLayoutTests
{
    [Fact]
    public void HudRegionsStayInsideTheLogicalScene()
    {
        var regions = new[]
        {
            AlchemyHudLayout.Header,
            AlchemyHudLayout.RecipeButton,
            AlchemyHudLayout.HeaderRecipe,
            AlchemyHudLayout.HeaderStep,
            AlchemyHudLayout.HeaderDeviation,
            AlchemyHudLayout.HeaderOutcome,
            AlchemyHudLayout.ResetButton,
            AlchemyHudLayout.Inventory,
            AlchemyHudLayout.Feedback,
            AlchemyHudLayout.BottomRail,
            AlchemyHudLayout.RecipeBook,
            AlchemyHudLayout.RecipeBookLeftPage,
            AlchemyHudLayout.RecipeBookRightPage,
            AlchemyHudLayout.RecipeBookPrevious,
            AlchemyHudLayout.RecipeBookNext,
            AlchemyHudLayout.RecipeBookClose,
            AlchemyHudLayout.BrewShowcase
        };

        foreach (var region in regions)
        {
            Assert.True(region.X >= 0 && region.Y >= 0, $"Region starts outside scene: {region}");
            Assert.True(region.Right <= AlchemyHudLayout.SceneWidth, $"Region exceeds scene width: {region}");
            Assert.True(region.Bottom <= AlchemyHudLayout.SceneHeight, $"Region exceeds scene height: {region}");
        }
    }

    [Fact]
    public void RecipeButtonLeavesRoomForWorkshopExitButton()
    {
        const double exitButtonRightEdge = 58;

        Assert.True(AlchemyHudLayout.RecipeButton.X > exitButtonRightEdge);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(8)]
    public void StepSegmentsFillTheHeaderWithoutOverlap(int stepCount)
    {
        Rect previous = default;
        for (var index = 0; index < stepCount; index++)
        {
            var segment = AlchemyHudLayout.StepSegment(index, stepCount);
            Assert.True(segment.Width > 0);
            Assert.True(segment.Right <= AlchemyHudLayout.HeaderStep.Right);
            if (index > 0)
            {
                Assert.True(segment.X >= previous.Right);
            }

            previous = segment;
        }
    }

    [Theory]
    [InlineData(7)]
    [InlineData(8)]
    public void RecipeStepsFitTheTwoPageLayout(int stepCount)
    {
        for (var index = 0; index < stepCount; index++)
        {
            var step = AlchemyHudLayout.RecipeStep(index);
            Assert.True(AlchemyHudLayout.RecipeBook.Contains(step.TopLeft));
            Assert.True(AlchemyHudLayout.RecipeBook.Contains(step.BottomRight));
        }
    }

    [Fact]
    public void ToolHitboxesRemainBelowHeaderAndAboveBottomRail()
    {
        Assert.All(AlchemyHudLayout.ToolHitboxes, hitbox =>
        {
            Assert.True(hitbox.Y >= AlchemyHudLayout.Header.Bottom, hitbox.ToString());
            Assert.True(hitbox.Bottom <= AlchemyHudLayout.BottomRail.Y, hitbox.ToString());
        });
    }

    [Theory]
    [InlineData("Default", 0, 3, 1)]
    [InlineData("Hover", 0, 3, 1)]
    [InlineData("Pressed", 1.5, 1, 1)]
    [InlineData("Selected", 0, 3, 1)]
    [InlineData("Disabled", 0, 1, .58)]
    [InlineData("Focused", 0, 3, 1)]
    public void InteractionStatesUseStablePhysicalFeedback(
        string stateName,
        double expectedOffset,
        double expectedShadow,
        double expectedOpacity)
    {
        var state = Enum.Parse<AlchemyInteractionState>(stateName);
        var visual = AlchemyInteractionPresentation.From(state);

        Assert.Equal(expectedOffset, visual.ContentOffset);
        Assert.Equal(expectedShadow, visual.ShadowOffset);
        Assert.Equal(expectedOpacity, visual.Opacity);
        Assert.True(visual.BorderThickness >= 1);
    }

    [Fact]
    public void InteractiveColorsMeetGameHudContrastTargets()
    {
        Assert.True(ContrastRatio(AlchemyHudTheme.AccentBright, AlchemyHudTheme.InteractiveHover) >= 3);
        Assert.True(ContrastRatio(AlchemyHudTheme.TextPrimary, AlchemyHudTheme.SurfaceInset) >= 4.5);
        Assert.True(ContrastRatio(AlchemyHudTheme.TextPrimary, AlchemyHudTheme.InteractivePressed) >= 4.5);
    }

    [Fact]
    public void HudTargetsDistinguishToolsFromChrome()
    {
        Assert.True(new AlchemyHudTarget(AlchemyHudTargetKind.Bellows).IsTool);
        Assert.True(new AlchemyHudTarget(AlchemyHudTargetKind.Pestle).IsTool);
        Assert.False(new AlchemyHudTarget(AlchemyHudTargetKind.RecipeBook).IsTool);
        Assert.True(AlchemyHudTarget.None.IsNone);
    }

    [Fact]
    public void InitialPresentationUsesLayeredGuidanceDefaults()
    {
        var presentation = AlchemyHudPresentation.From(Source());

        Assert.Equal("01/08", presentation.StepNumber);
        Assert.Equal("炼制中", presentation.OutcomeLabel);
        Assert.Equal("--", presentation.YieldValue);
        Assert.Equal("0", presentation.DeviationValue);
    }

    [Fact]
    public void PresentationPrioritizesActiveDynamicProcess()
    {
        var presentation = AlchemyHudPresentation.From(Source(hourglassRunning: true, hourglassProgress: 0.42));

        Assert.Equal("沙漏", presentation.ProcessLabel);
        Assert.Equal("42%", presentation.ProcessValue);
    }

    [Fact]
    public void PresentationCanSwitchToEnglishWithoutChangingSimulationState()
    {
        var presentation = AlchemyHudPresentation.From(
            Source(hourglassRunning: true, hourglassProgress: 0.42),
            useEnglish: true);

        Assert.Equal("Hourglass", presentation.ProcessLabel);
        Assert.Equal("Brewing", presentation.OutcomeLabel);
        Assert.Equal("42%", presentation.ProcessValue);
    }

    [Theory]
    [InlineData(BrewOutcome.Brewing, 0, "炼制中", "Neutral")]
    [InlineData(BrewOutcome.Perfect, 0, "完美", "Success")]
    [InlineData(BrewOutcome.Diluted, 1, "优秀", "Info")]
    [InlineData(BrewOutcome.Diluted, 3, "普通", "Warning")]
    [InlineData(BrewOutcome.Diluted, 5, "糟糕", "Danger")]
    [InlineData(BrewOutcome.Failed, 4, "糟糕", "Danger")]
    public void OutcomeMappingUsesQualityLabelsAndSemanticTones(
        BrewOutcome outcome,
        int mistakes,
        string label,
        string tone)
    {
        var result = AlchemyHudPresentation.OutcomeFor(outcome, mistakes);

        Assert.Equal(label, result.Label);
        Assert.Equal(tone, result.Tone.ToString());
    }

    [Theory]
    [InlineData(BrewOutcome.Perfect, 0, 3, 100, "完美", "Success")]
    [InlineData(BrewOutcome.Diluted, 1, 1, 84, "优秀", "Info")]
    [InlineData(BrewOutcome.Diluted, 3, 1, 70, "普通", "Warning")]
    [InlineData(BrewOutcome.Diluted, 5, 1, 56, "糟糕", "Danger")]
    [InlineData(BrewOutcome.Failed, 4, 0, 0, "糟糕", "Danger")]
    public void CompletionPresentationMapsQualityScore(
        BrewOutcome outcome,
        int mistakes,
        int yield,
        int score,
        string grade,
        string tone)
    {
        var result = AlchemyCompletionPresentation.From(outcome, mistakes, yield);

        Assert.Equal(score, result.QualityScore);
        Assert.Equal(grade, result.Grade);
        Assert.Equal(tone, result.Tone.ToString());
    }

    private static AlchemyHudSource Source(
        bool hourglassRunning = false,
        double hourglassProgress = 0,
        BrewOutcome outcome = BrewOutcome.Brewing) =>
        new(
            RecipeStepCount: 8,
            CurrentStepIndex: 0,
            HourglassRunning: hourglassRunning,
            HourglassProgress: hourglassProgress,
            MortarLoaded: false,
            GrindingProgress: 0,
            Distilled: false,
            Outcome: outcome,
            Yield: 0,
            Mistakes: 0,
            LastMessage: string.Empty);

    private static double ContrastRatio(uint foreground, uint background)
    {
        static double Luminance(uint color)
        {
            static double Linear(byte channel)
            {
                var value = channel / 255d;
                return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
            }

            var red = Linear((byte)(color >> 16));
            var green = Linear((byte)(color >> 8));
            var blue = Linear((byte)color);
            return (.2126 * red) + (.7152 * green) + (.0722 * blue);
        }

        var lighter = Math.Max(Luminance(foreground), Luminance(background));
        var darker = Math.Min(Luminance(foreground), Luminance(background));
        return (lighter + .05) / (darker + .05);
    }
}
