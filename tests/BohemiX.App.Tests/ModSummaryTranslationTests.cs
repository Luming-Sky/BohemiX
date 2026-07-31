using BohemiX.App.ViewModels;
using BohemiX.Core.Models;

namespace BohemiX.App.Tests;

public sealed class ModSummaryTranslationTests
{
    [Fact]
    public void RowTracksBusyTranslatedAndOriginalStates()
    {
        using var row = CreateRow("An English summary.");
        row.UseSummaryLanguage("zh-CN", TranslateEnglish);

        row.BeginSummaryTranslation(TranslateEnglish);

        Assert.True(row.IsSummaryTranslationBusy);
        Assert.False(row.CanTranslateSummary);
        Assert.Equal("Translating...", row.SummaryTranslationActionText);
        Assert.Equal("An English summary.", row.SummaryDisplayText);

        row.ApplySummaryTranslation("\u4e00\u6bb5\u4e2d\u6587\u7b80\u4ecb\u3002", "zh-CN", TranslateEnglish);

        Assert.False(row.IsSummaryTranslationBusy);
        Assert.True(row.IsShowingTranslatedSummary);
        Assert.True(row.HasTranslationForCurrentLanguage);
        Assert.Equal("\u4e00\u6bb5\u4e2d\u6587\u7b80\u4ecb\u3002", row.SummaryDisplayText);
        Assert.Equal("Show original", row.SummaryTranslationActionText);

        row.ToggleSummaryTranslation(TranslateEnglish);

        Assert.False(row.IsShowingTranslatedSummary);
        Assert.Equal("An English summary.", row.SummaryDisplayText);
        Assert.Equal("Show translation", row.SummaryTranslationActionText);
    }

    [Fact]
    public void RowHidesOldLanguageTranslationAndReusesItWhenLanguageReturns()
    {
        using var row = CreateRow("An English summary.");
        row.UseSummaryLanguage("zh-CN", TranslateEnglish);
        row.ApplySummaryTranslation("\u4e00\u6bb5\u4e2d\u6587\u7b80\u4ecb\u3002", "zh-CN", TranslateEnglish);

        row.UseSummaryLanguage("en", TranslateEnglish);

        Assert.False(row.HasTranslationForCurrentLanguage);
        Assert.False(row.IsShowingTranslatedSummary);
        Assert.Equal("An English summary.", row.SummaryDisplayText);
        Assert.Equal("Translate", row.SummaryTranslationActionText);

        row.UseSummaryLanguage("zh-CN", TranslateEnglish);

        Assert.True(row.HasTranslationForCurrentLanguage);
        Assert.Equal("An English summary.", row.SummaryDisplayText);
        Assert.Equal("Show translation", row.SummaryTranslationActionText);

        row.ToggleSummaryTranslation(TranslateEnglish);

        Assert.Equal("\u4e00\u6bb5\u4e2d\u6587\u7b80\u4ecb\u3002", row.SummaryDisplayText);
    }

    [Fact]
    public void RowFailureKeepsOriginalAndExposesLocalizedError()
    {
        using var row = CreateRow("An English summary.");
        row.UseSummaryLanguage("zh-CN", TranslateEnglish);
        row.BeginSummaryTranslation(TranslateEnglish);

        row.FailSummaryTranslation("Translation failed.", TranslateEnglish);

        Assert.False(row.IsSummaryTranslationBusy);
        Assert.False(row.IsShowingTranslatedSummary);
        Assert.Equal("An English summary.", row.SummaryDisplayText);
        Assert.True(row.HasSummaryTranslationError);
        Assert.Equal("Translation failed.", row.SummaryTranslationErrorText);
    }

    [Theory]
    [InlineData("A combat overhaul.", "en")]
    [InlineData("\u6218\u6597\u5e73\u8861\u8c03\u6574", "zh-CN")]
    [InlineData("Version 2 - \u4e2d\u6587\u8bf4\u660e", "zh-CN")]
    [InlineData(null, "en")]
    public void DetectModSummaryLanguage_RecognizesSupportedLanguages(string? summary, string expected)
    {
        Assert.Equal(expected, MainWindowViewModel.DetectModSummaryLanguage(summary));
    }

    private static NexusModSearchRowViewModel CreateRow(string summary) =>
        new(new NexusModSummary(
            42,
            "Combat Rebalance",
            summary,
            "1.0",
            "Test Author",
            100,
            25,
            1024,
            null,
            DateTimeOffset.UtcNow,
            true,
            "published"));

    private static string TranslateEnglish(string key) => key switch
    {
        "TranslateModSummary" => "Translate",
        "TranslatingModSummary" => "Translating...",
        "ShowOriginalModSummary" => "Show original",
        "ShowTranslatedModSummary" => "Show translation",
        _ => key
    };
}
