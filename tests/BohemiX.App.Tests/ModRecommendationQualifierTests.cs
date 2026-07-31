using BohemiX.App.ViewModels;
using BohemiX.Core.Models;

namespace BohemiX.App.Tests;

public sealed class ModRecommendationQualifierTests
{
    [Fact]
    public void QualifierTokensExpandPresetTermsIntoMatchingSynonyms()
    {
        var tokens = MainWindowViewModel.BuildModRecommendationQualifierTokens(["ui", "performance"]);

        Assert.Contains("ui", tokens);
        Assert.Contains("hud", tokens);
        Assert.Contains("inventory", tokens);
        Assert.Contains("performance", tokens);
        Assert.Contains("fps", tokens);
        Assert.Contains("stutter", tokens);
    }

    [Fact]
    public void UiQualifierRaisesInterfaceCandidateAboveNonMatchingCandidate()
    {
        var candidates = new[]
        {
            Mod("Balanced Combat Tuning", "Stable combat gameplay tuning for enemies and perks."),
            Mod("Readable Inventory HUD", "Clean UI interface and inventory HUD readability improvements.", modId: 2),
            Mod("Texture Pack", "Visual texture and lighting polish.", modId: 3)
        };

        var unfiltered = MainWindowViewModel.RankModRecommendationCandidatesForTesting(candidates, "Content", []);
        var filtered = MainWindowViewModel.RankModRecommendationCandidatesForTesting(candidates, "Content", ["ui"]);

        var uiBefore = unfiltered.Single(row => row.Mod.Name == "Readable Inventory HUD");
        var uiAfter = filtered.Single(row => row.Mod.Name == "Readable Inventory HUD");

        Assert.True(uiAfter.ContentScore > uiBefore.ContentScore);
        Assert.Equal("Readable Inventory HUD", filtered[0].Mod.Name);
        Assert.Contains("RecommendationSignalQualifierMatch", uiAfter.ReasonText);
    }

    [Fact]
    public void PerformanceQualifierRaisesPerformanceCandidateAboveUiCandidate()
    {
        var candidates = new[]
        {
            Mod("Inventory HUD Refresh", "Clean UI interface and inventory readability improvements."),
            Mod("FPS Stutter Fix", "Performance optimized fps stutter fix and stability patch.", modId: 2),
            Mod("Armor Collection", "Items armor and clothing equipment collection.", modId: 3)
        };

        var filtered = MainWindowViewModel.RankModRecommendationCandidatesForTesting(candidates, "Content", ["performance"]);

        Assert.Equal("FPS Stutter Fix", filtered[0].Mod.Name);
        Assert.Contains("RecommendationSignalQualifierMatch", filtered[0].ReasonText);
    }

    [Fact]
    public void QualifierQueriesTargetLibrarySearchAndCacheByRefreshPage()
    {
        var queries = MainWindowViewModel.BuildModRecommendationQueriesForTesting("Starter", ["ui", "performance"]);

        Assert.Contains("ui fix", queries);
        Assert.Contains("ui compatibility", queries);
        Assert.Contains("performance fix", queries);
        Assert.Contains("performance quality of life", queries);

        var firstPageKey = MainWindowViewModel.BuildModRecommendationCandidateCacheKeyForTesting("Starter", queries, 0);
        var secondPageKey = MainWindowViewModel.BuildModRecommendationCandidateCacheKeyForTesting("Starter", queries, 1);

        Assert.NotEqual(firstPageKey, secondPageKey);
    }

    [Fact]
    public void DailyRecommendationIsStableForTheDayAndLimitedToTopFive()
    {
        var recommendations = MainWindowViewModel.RankModRecommendationCandidatesForTesting(
            Enumerable.Range(1, 7)
                .Select(index => Mod($"Mod {index}", $"Summary {index}", index, downloads: 80_000 - index * 1_000))
                .ToArray(),
            "PopularityQuality",
            []);
        var date = new DateOnly(2026, 7, 23);

        var first = MainWindowViewModel.SelectDailyModRecommendationForDate(recommendations, date);
        var second = MainWindowViewModel.SelectDailyModRecommendationForDate(recommendations, date);

        Assert.Same(first, second);
        Assert.Contains(first, recommendations.Take(5));
        Assert.NotSame(first, MainWindowViewModel.SelectDailyModRecommendationForDate(recommendations, date.AddDays(1)));
    }

    [Fact]
    public void DailyRecommendationReturnsNullWhenThereAreNoCandidates()
    {
        Assert.Null(MainWindowViewModel.SelectDailyModRecommendationForDate([], new DateOnly(2026, 7, 23)));
    }

    [Fact]
    public void DailyRecommendationPrefersTopCandidateWithRealArtwork()
    {
        var withoutArtwork = Recommendation(Mod("No artwork", "No image URL", modId: 1));
        var withArtwork = Recommendation(Mod(
            "Real artwork",
            "Has a Nexus image URL",
            modId: 2,
            thumbnailUrl: "https://staticdelivery.nexusmods.com/mods/7286/images/2/2-cover.jpg"));

        var selected = MainWindowViewModel.SelectDailyModRecommendationForDate(
            [withoutArtwork, withArtwork],
            new DateOnly(2026, 7, 23));

        Assert.Same(withArtwork, selected);
    }

    [Fact]
    public void HeatRatingActivatesExactlyTheCalculatedNumberOfFlames()
    {
        var recommendation = Recommendation(Mod("Four flames", "Heat rating"), score: 61);

        Assert.Equal(4, recommendation.HeatLevel);
        Assert.True(recommendation.IsHeatLevel1Active);
        Assert.True(recommendation.IsHeatLevel2Active);
        Assert.True(recommendation.IsHeatLevel3Active);
        Assert.True(recommendation.IsHeatLevel4Active);
        Assert.False(recommendation.IsHeatLevel5Active);
    }

    private static ModRecommendationRowViewModel Recommendation(NexusModSearchRowViewModel mod, double score = 80)
    {
        return new ModRecommendationRowViewModel(
            mod,
            "PopularityQuality",
            "Popularity + quality",
            "Utility",
            score,
            70,
            80,
            90,
            75,
            85,
            "Good fit",
            "Stable and maintained");
    }

    private static NexusModSearchRowViewModel Mod(
        string name,
        string summary,
        int modId = 1,
        int downloads = 20_000,
        int endorsements = 1_200,
        string? thumbnailUrl = null)
    {
        return new NexusModSearchRowViewModel(new NexusModSummary(
            modId,
            name,
            summary,
            "1.0",
            "Test Author",
            downloads,
            endorsements,
            1024 * 1024,
            thumbnailUrl,
            DateTimeOffset.UtcNow.AddDays(-20),
            true,
            "published"));
    }
}
