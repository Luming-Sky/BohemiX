using BohemiX.App.Services;

namespace BohemiX.App.Tests;

public sealed class MainWindowTextCatalogTests
{
    private readonly IMainWindowTextCatalog catalog = new MainWindowTextCatalog();

    [Fact]
    public void Translate_ReturnsLocalizedValuesForSupportedLanguages()
    {
        Assert.Equal("Launcher", catalog.Translate("English", "Dashboard"));
        Assert.Equal("启动器", catalog.Translate("简体中文", "Dashboard"));
    }

    [Fact]
    public void Translate_ReturnsKeyForUnknownEntries()
    {
        Assert.Equal("MissingKey", catalog.Translate("English", "MissingKey"));
        Assert.Equal("MissingKey", catalog.Translate("简体中文", "MissingKey"));
    }

    [Fact]
    public void Translate_ReturnsBatchConflictFixText()
    {
        Assert.Equal("Fix all", catalog.Translate("English", "AutoFixAllModConflicts"));
        Assert.Equal(
            "Keeps the current later-mod-wins order and confirms all pending overlaps.",
            catalog.Translate("English", "AutoFixAllModConflictsHint"));
    }

    [Fact]
    public void Translate_ReturnsModSummaryTranslationTextInBothLanguages()
    {
        Assert.Equal("Mod summary", catalog.Translate("English", "ModSummary"));
        Assert.Equal("Translate", catalog.Translate("English", "TranslateModSummary"));
        Assert.Equal("Show original", catalog.Translate("English", "ShowOriginalModSummary"));
        Assert.Equal("Mod \u7b80\u4ecb", catalog.Translate("\u7b80\u4f53\u4e2d\u6587", "ModSummary"));
        Assert.Equal("\u7ffb\u8bd1", catalog.Translate("\u7b80\u4f53\u4e2d\u6587", "TranslateModSummary"));
        Assert.Equal(
            "\u516c\u5171\u7ffb\u8bd1\u670d\u52a1\u5f53\u524d\u914d\u989d\u5df2\u7528\u5c3d\u3002",
            catalog.Translate("\u7b80\u4f53\u4e2d\u6587", "ModSummaryTranslationQuotaExceeded"));
    }

    [Fact]
    public void Translate_UsesPlainLanguageForTechnicalUiStates()
    {
        Assert.Equal("启动前让模组生效", catalog.Translate("简体中文", "PrepareVfs"));
        Assert.Equal("模组加载状态", catalog.Translate("简体中文", "VfsState"));
        Assert.Equal(
            "已禁用，不会参与模组文件检查。",
            catalog.Translate("简体中文", "DisabledExcludedFromVfsChecks"));
        Assert.Equal("正在准备", catalog.Translate("简体中文", "ModDownloadStatusResolving"));
        Assert.Equal("游戏位置同步", catalog.Translate("简体中文", "TrackerBridgeProjection"));
        Assert.Equal("允许使用 Nexus 浏览器登录", catalog.Translate("简体中文", "NexusCookieConsentTitle"));
        Assert.DoesNotContain("预检", catalog.Translate("简体中文", "ModPackPreflightReady"));
    }

    [Fact]
    public void Translate_UsesChineseFallbackForUnsupportedLanguage()
    {
        Assert.Equal("启动器", catalog.Translate("日本語", "Dashboard"));
    }

    [Fact]
    public void LanguageHelpers_NormalizeLegacyValuesAndPreserveDisplayNames()
    {
        Assert.Equal("English", catalog.NormalizeLanguage("English"));
        Assert.Equal("简体中文", catalog.NormalizeLanguage("Simplified Chinese"));
        Assert.Equal("简体中文", catalog.NormalizeLanguage(null));
        Assert.Equal("简体中文", catalog.FormatLanguageDisplayName("Simplified Chinese"));
    }
}
