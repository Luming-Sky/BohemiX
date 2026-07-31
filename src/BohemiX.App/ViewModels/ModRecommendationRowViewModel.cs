using System;
using CommunityToolkit.Mvvm.ComponentModel;
using BohemiX.App.Controls;

namespace BohemiX.App.ViewModels;

public sealed partial class ModRecommendationRowViewModel : ViewModelBase, IVisualResourceOwner
{
    private Func<string, string> translate = TranslateDefault;

    public void ActivateVisualResources() => Mod.ActivateVisualResources();

    public void DeactivateVisualResources() => Mod.DeactivateVisualResources();

    public ModRecommendationRowViewModel(
        NexusModSearchRowViewModel mod,
        string strategyKey,
        string strategyText,
        string categoryText,
        double score,
        double contentScore,
        double popularityScore,
        double qualityScore,
        double freshnessScore,
        double stabilityScore,
        string reasonText,
        string reasonDetailText)
    {
        Mod = mod;
        StrategyKey = strategyKey;
        StrategyText = strategyText;
        CategoryText = categoryText;
        Score = Math.Clamp(score, 0, 100);
        ContentScore = Math.Clamp(contentScore, 0, 100);
        PopularityScore = Math.Clamp(popularityScore, 0, 100);
        QualityScore = Math.Clamp(qualityScore, 0, 100);
        FreshnessScore = Math.Clamp(freshnessScore, 0, 100);
        StabilityScore = Math.Clamp(stabilityScore, 0, 100);
        ReasonText = reasonText;
        ReasonDetailText = reasonDetailText;
        HeatLevel = CalculateHeatLevel(Score);
        RefreshScoreText();
    }

    public NexusModSearchRowViewModel Mod { get; }

    public string StrategyKey { get; }

    public string StrategyText { get; }

    public string CategoryText { get; }

    public double Score { get; }

    public int HeatLevel { get; }

    public double HeatPercent => Score;

    public bool IsHeatLevel1Active => HeatLevel >= 1;

    public bool IsHeatLevel2Active => HeatLevel >= 2;

    public bool IsHeatLevel3Active => HeatLevel >= 3;

    public bool IsHeatLevel4Active => HeatLevel >= 4;

    public bool IsHeatLevel5Active => HeatLevel >= 5;

    public double ContentScore { get; }

    public double PopularityScore { get; }

    public double QualityScore { get; }

    public double FreshnessScore { get; }

    public double StabilityScore { get; }

    public string ReasonText { get; }

    public string ReasonDetailText { get; }

    [ObservableProperty]
    private bool isSelected;

    [ObservableProperty]
    private string rankText = string.Empty;

    [ObservableProperty]
    private string scoreText = string.Empty;

    [ObservableProperty]
    private string heatLevelText = string.Empty;

    [ObservableProperty]
    private string hotnessText = string.Empty;

    [ObservableProperty]
    private string matchText = string.Empty;

    [ObservableProperty]
    private string qualityText = string.Empty;

    [ObservableProperty]
    private string heatExplanationText = string.Empty;

    [ObservableProperty]
    private string matchExplanationText = string.Empty;

    [ObservableProperty]
    private string popularityExplanationText = string.Empty;

    [ObservableProperty]
    private string qualityExplanationText = string.Empty;

    public void ApplyLocalization(Func<string, string> translate)
    {
        this.translate = translate;
        Mod.ApplyLocalization(translate);
        RefreshScoreText();
    }

    public void ApplyDownloadState(bool isDownloaded, Func<string, string> translate)
    {
        Mod.ApplyDownloadState(isDownloaded, translate);
    }

    public void UseSummaryLanguage(string targetLanguage, Func<string, string> translate)
    {
        Mod.UseSummaryLanguage(targetLanguage, translate);
    }

    private void RefreshScoreText()
    {
        HeatLevelText = HeatLevel == 0 ? "0/5" : $"{HeatLevel}/5";
        HotnessText = translate($"RecommendationHeatLevel{HeatLevel}");
        ScoreText = HeatLevelText;
        MatchText = $"{ContentScore:0}%";
        QualityText = $"{QualityScore:0}%";
        HeatExplanationText = string.Format(translate("RecommendationHeatExplanation"), HotnessText, HeatLevelText, ReasonText, ReasonDetailText);
        MatchExplanationText = string.Format(translate("RecommendationMatchExplanation"), ContentScore, CategoryText, StrategyText, ReasonText);
        PopularityExplanationText = string.Format(translate("RecommendationPopularityExplanation"), PopularityScore, Mod.DownloadsText, FreshnessScore);
        QualityExplanationText = string.Format(translate("RecommendationQualityExplanation"), QualityScore, FreshnessScore, StabilityScore);
    }

    private static int CalculateHeatLevel(double score)
    {
        if (score <= 0)
        {
            return 0;
        }

        return Math.Clamp((int)Math.Ceiling(score / 20), 1, 5);
    }

    private static string TranslateDefault(string key)
    {
        return key switch
        {
            "RecommendationHeatLevel0" => "Cold",
            "RecommendationHeatLevel1" => "Warming",
            "RecommendationHeatLevel2" => "Notable",
            "RecommendationHeatLevel3" => "Hot",
            "RecommendationHeatLevel4" => "Very hot",
            "RecommendationHeatLevel5" => "Blazing",
            "RecommendationHeatExplanation" => "{0} ({1}). The final heat combines the selected strategy, recommendation reason, and weighted signals: {2}. {3}",
            "RecommendationMatchExplanation" => "{0:0}% content match. Category: {1}; strategy: {2}. Main fit reason: {3}.",
            "RecommendationPopularityExplanation" => "{0:0}% popularity signal. This uses download volume ({1}) together with freshness ({2:0}%).",
            "RecommendationQualityExplanation" => "{0:0}% quality signal. Freshness contributes {1:0}% and stability contributes {2:0}%.",
            _ => key
        };
    }
}
