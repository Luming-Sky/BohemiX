using System;
using System.Globalization;
using System.Linq;
using Avalonia.Media.Imaging;
using BohemiX.App.Controls;
using BohemiX.App.Services;
using BohemiX.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BohemiX.App.ViewModels;

public sealed partial class NexusModSearchRowViewModel : ViewModelBase, IDisposable, IVisualResourceOwner
{
    private const int ThumbnailDecodeWidth = 384;
    private const string NexusSourceKey = "Nexus";
    private const string SteamWorkshopSourceKey = "SteamWorkshop";

    public NexusModSearchRowViewModel(NexusModSummary mod)
    {
        SourceKey = NexusSourceKey;
        ModId = mod.ModId;
        PublishedFileId = (ulong)mod.ModId;
        DisplayId = mod.ModId.ToString(CultureInfo.InvariantCulture);
        ThumbnailCacheId = mod.ModId;
        Name = string.IsNullOrWhiteSpace(mod.Name) ? $"Mod {mod.ModId}" : mod.Name;
        Summary = mod.Summary;
        SummaryDisplayText = Summary;
        Version = string.IsNullOrWhiteSpace(mod.Version) ? "-" : mod.Version;
        Author = string.IsNullOrWhiteSpace(mod.Author) ? "-" : mod.Author;
        Downloads = Math.Max(0, mod.Downloads);
        Endorsements = Math.Max(0, mod.Endorsements);
        UpdatedAt = mod.UpdatedAt;
        FileSizeInBytes = mod.FileSizeInBytes;
        DownloadsText = mod.Downloads.ToString("N0", CultureInfo.InvariantCulture);
        EndorsementsText = mod.Endorsements.ToString("N0", CultureInfo.InvariantCulture);
        UpdatedText = mod.UpdatedAt is null ? "-" : mod.UpdatedAt.Value.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        Status = string.IsNullOrWhiteSpace(mod.Status) ? "-" : mod.Status;
        DirectDownloadEnabled = mod.DirectDownloadEnabled;
        FileSizeText = mod.FileSizeInBytes is null ? "-" : FormatSize(mod.FileSizeInBytes.Value);
        RemoteThumbnailUrl = mod.ThumbnailUrl;
        ApplyEnglishLocalization();
    }

    public NexusModSearchRowViewModel(WorkshopModInfo mod)
    {
        SourceKey = SteamWorkshopSourceKey;
        ModId = mod.PublishedFileId > int.MaxValue ? 0 : (int)mod.PublishedFileId;
        PublishedFileId = mod.PublishedFileId;
        DisplayId = mod.PublishedFileId.ToString(CultureInfo.InvariantCulture);
        ThumbnailCacheId = CreateWorkshopThumbnailCacheId(mod.PublishedFileId);
        Name = string.IsNullOrWhiteSpace(mod.Name) ? $"Workshop {mod.PublishedFileId}" : mod.Name;
        Summary = mod.Summary;
        SummaryDisplayText = Summary;
        Version = "-";
        Author = string.IsNullOrWhiteSpace(mod.Author) ? "Steam user" : mod.Author;
        Downloads = mod.Subscriptions > long.MaxValue ? long.MaxValue : (long)mod.Subscriptions;
        Endorsements = mod.VotesUp;
        UpdatedAt = mod.UpdatedAt;
        FileSizeInBytes = mod.FileSizeInBytes;
        DownloadsText = mod.Subscriptions.ToString("N0", CultureInfo.InvariantCulture);
        EndorsementsText = mod.VotesUp.ToString("N0", CultureInfo.InvariantCulture);
        UpdatedText = mod.UpdatedAt is null ? "-" : mod.UpdatedAt.Value.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        Status = "Workshop";
        DirectDownloadEnabled = true;
        FileSizeText = mod.FileSizeInBytes is null ? "-" : FormatSize(mod.FileSizeInBytes.Value);
        RemoteThumbnailUrl = mod.ThumbnailUrl;
        ApplyEnglishLocalization();
    }

    public string SourceKey { get; }

    public bool IsSteamWorkshop => string.Equals(SourceKey, SteamWorkshopSourceKey, StringComparison.Ordinal);

    public int ModId { get; }

    public ulong PublishedFileId { get; }

    public string DisplayId { get; }

    public int ThumbnailCacheId { get; }

    public string Name { get; }

    public string Summary { get; }

    public string Version { get; }

    public string Author { get; }

    public long Downloads { get; }

    public long Endorsements { get; }

    public DateTimeOffset? UpdatedAt { get; }

    public long? FileSizeInBytes { get; }

    public string DownloadsText { get; }

    public string EndorsementsText { get; }

    public string UpdatedText { get; }

    public string Status { get; }

    public bool DirectDownloadEnabled { get; }

    public string FileSizeText { get; }

    public string? RemoteThumbnailUrl { get; }

    [ObservableProperty]
    private string sourceText = string.Empty;

    [ObservableProperty]
    private string metricText = string.Empty;

    [ObservableProperty]
    private string downloadStateText = string.Empty;

    [ObservableProperty]
    private string downloadActionText = string.Empty;

    [ObservableProperty]
    private string summaryDisplayText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanTranslateSummary))]
    private bool isSummaryTranslationBusy;

    [ObservableProperty]
    private bool isShowingTranslatedSummary;

    [ObservableProperty]
    private string summaryTranslationActionText = "Translate";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSummaryTranslationError))]
    private string summaryTranslationErrorText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSummaryTranslationNotice))]
    private string summaryTranslationNoticeText = string.Empty;

    private string translatedSummary = string.Empty;
    private string translatedSummaryLanguage = string.Empty;
    private string currentSummaryLanguage = "en";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    private bool isDownloaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasThumbnail))]
    [NotifyPropertyChangedFor(nameof(HasNoThumbnail))]
    private string? thumbnailPath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasThumbnail))]
    [NotifyPropertyChangedFor(nameof(HasNoThumbnail))]
    private Bitmap? thumbnailImage;

    private BitmapLease? thumbnailLease;
    private bool visualResourcesActive;

    [ObservableProperty]
    private bool isSelected;

    [ObservableProperty]
    private string nameHighlightPrefix = string.Empty;

    [ObservableProperty]
    private string nameHighlightText = string.Empty;

    [ObservableProperty]
    private string nameHighlightSuffix = string.Empty;

    public string MetaText => $"#{DisplayId} / {Author} / v{Version}";

    public string CategoryText => "KCD2";

    public bool HasThumbnail => ThumbnailImage is not null;

    public bool HasNoThumbnail => !HasThumbnail;

    public bool CanDownload => !IsDownloaded;

    public bool HasSummary => !string.IsNullOrWhiteSpace(Summary);

    public bool CanTranslateSummary => !IsSummaryTranslationBusy && HasSummary;

    public bool HasSummaryTranslationError => !string.IsNullOrWhiteSpace(SummaryTranslationErrorText);

    public bool HasSummaryTranslationNotice => !string.IsNullOrWhiteSpace(SummaryTranslationNoticeText);

    public bool HasTranslationForCurrentLanguage =>
        !string.IsNullOrWhiteSpace(translatedSummary)
        && string.Equals(translatedSummaryLanguage, currentSummaryLanguage, StringComparison.OrdinalIgnoreCase);

    public void ApplySearchKeyword(string? keyword)
    {
        var match = FindNameMatch(keyword);
        if (match is null)
        {
            NameHighlightPrefix = Name;
            NameHighlightText = string.Empty;
            NameHighlightSuffix = string.Empty;
            return;
        }

        NameHighlightPrefix = Name[..match.Value.Start];
        NameHighlightText = Name.Substring(match.Value.Start, match.Value.Length);
        NameHighlightSuffix = Name[(match.Value.Start + match.Value.Length)..];
    }

    public void ApplyLocalization(Func<string, string> translate)
    {
        ArgumentNullException.ThrowIfNull(translate);

        SourceText = IsSteamWorkshop
            ? translate("SteamWorkshopSource")
            : translate("NexusModsSource");

        MetricText = IsSteamWorkshop
            ? string.Format(
                CultureInfo.CurrentCulture,
                translate("WorkshopMetricText"),
                DownloadsText,
                EndorsementsText,
                FileSizeText)
            : string.Format(
                CultureInfo.CurrentCulture,
                translate("NexusMetricText"),
                DownloadsText,
                EndorsementsText,
                FileSizeText);

        RefreshDownloadTexts(translate);
        RefreshSummaryTranslationText(translate);
    }

    public void UseSummaryLanguage(string targetLanguage, Func<string, string> translate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetLanguage);
        ArgumentNullException.ThrowIfNull(translate);

        currentSummaryLanguage = targetLanguage;
        if (!HasTranslationForCurrentLanguage)
        {
            IsShowingTranslatedSummary = false;
            SummaryDisplayText = Summary;
        }

        SummaryTranslationErrorText = string.Empty;
        SummaryTranslationNoticeText = string.Empty;
        RefreshSummaryTranslationText(translate);
    }

    public void BeginSummaryTranslation(Func<string, string> translate)
    {
        ArgumentNullException.ThrowIfNull(translate);

        SummaryTranslationErrorText = string.Empty;
        SummaryTranslationNoticeText = string.Empty;
        IsSummaryTranslationBusy = true;
        RefreshSummaryTranslationText(translate);
    }

    public void ApplySummaryTranslation(
        string translatedText,
        string targetLanguage,
        Func<string, string> translate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(translatedText);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetLanguage);
        ArgumentNullException.ThrowIfNull(translate);

        translatedSummary = translatedText.Trim();
        translatedSummaryLanguage = targetLanguage;
        IsSummaryTranslationBusy = false;
        SummaryTranslationErrorText = string.Empty;
        SummaryTranslationNoticeText = string.Empty;

        if (string.Equals(currentSummaryLanguage, targetLanguage, StringComparison.OrdinalIgnoreCase))
        {
            IsShowingTranslatedSummary = true;
            SummaryDisplayText = translatedSummary;
        }

        RefreshSummaryTranslationText(translate);
    }

    public void FailSummaryTranslation(string errorText, Func<string, string> translate)
    {
        ArgumentNullException.ThrowIfNull(errorText);
        ArgumentNullException.ThrowIfNull(translate);

        IsSummaryTranslationBusy = false;
        IsShowingTranslatedSummary = false;
        SummaryDisplayText = Summary;
        SummaryTranslationErrorText = errorText;
        SummaryTranslationNoticeText = string.Empty;
        RefreshSummaryTranslationText(translate);
    }

    public void ShowSummaryTranslationNotice(string noticeText, Func<string, string> translate)
    {
        ArgumentNullException.ThrowIfNull(noticeText);
        ArgumentNullException.ThrowIfNull(translate);

        IsSummaryTranslationBusy = false;
        IsShowingTranslatedSummary = false;
        SummaryDisplayText = Summary;
        SummaryTranslationErrorText = string.Empty;
        SummaryTranslationNoticeText = noticeText;
        RefreshSummaryTranslationText(translate);
    }

    public void ToggleSummaryTranslation(Func<string, string> translate)
    {
        ArgumentNullException.ThrowIfNull(translate);
        if (!HasTranslationForCurrentLanguage)
        {
            return;
        }

        IsShowingTranslatedSummary = !IsShowingTranslatedSummary;
        SummaryDisplayText = IsShowingTranslatedSummary ? translatedSummary : Summary;
        SummaryTranslationErrorText = string.Empty;
        SummaryTranslationNoticeText = string.Empty;
        RefreshSummaryTranslationText(translate);
    }

    public void ApplyDownloadState(bool isDownloaded, Func<string, string> translate)
    {
        ArgumentNullException.ThrowIfNull(translate);

        IsDownloaded = isDownloaded;
        RefreshDownloadTexts(translate);
    }

    public void SetThumbnailPath(string? thumbnailPath, bool activateVisualResources = false)
    {
        ReleaseThumbnail();
        ThumbnailPath = thumbnailPath;

        if (activateVisualResources || visualResourcesActive)
        {
            ActivateVisualResources();
        }
    }

    public void ActivateVisualResources()
    {
        visualResourcesActive = true;
        if (thumbnailLease is null && ThumbnailImage is null && !string.IsNullOrWhiteSpace(ThumbnailPath))
        {
            thumbnailLease = SharedBitmapLeaseCache.Instance.Acquire(ThumbnailPath, ThumbnailDecodeWidth);
            ThumbnailImage = thumbnailLease?.Bitmap;
        }
    }

    public void SetSharedThumbnailImage(Bitmap? bitmap)
    {
        ReleaseThumbnail();
        ThumbnailImage = bitmap;
    }

    public void DeactivateVisualResources()
    {
        visualResourcesActive = false;
        ReleaseThumbnail();
    }

    public void Dispose() => DeactivateVisualResources();

    private void ReleaseThumbnail()
    {
        ThumbnailImage = null;
        thumbnailLease?.Dispose();
        thumbnailLease = null;
    }

    private static string FormatSize(long sizeInBytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var size = (double)sizeInBytes;
        var unitIndex = 0;

        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{size:0} {units[unitIndex]}"
            : $"{size:0.0} {units[unitIndex]}";
    }

    private static int CreateWorkshopThumbnailCacheId(ulong publishedFileId)
    {
        return unchecked((int)(0x40000000 | (publishedFileId.GetHashCode() & 0x3FFFFFFF)));
    }

    private void ApplyEnglishLocalization()
    {
        SourceText = IsSteamWorkshop ? "Steam Workshop" : "Nexus Mods";
        MetricText = IsSteamWorkshop
            ? $"{DownloadsText} subscriptions / {EndorsementsText} votes / {FileSizeText}"
            : $"{DownloadsText} downloads / {EndorsementsText} endorsements / {FileSizeText}";
        RefreshDownloadTexts(key => key switch
        {
            "Download" => "Download",
            "ModDownloadStatusDownloaded" => "Downloaded",
            "WorkshopSubscribeViaSteam" => "Subscribe via Steam",
            "ManagerDownload" => "Manager download",
            "BrowserSignInOrApiKeyRequired" => "Browser sign-in or API key",
            _ => key
        });
        RefreshSummaryTranslationText(key => key switch
        {
            "TranslateModSummary" => "Translate",
            "TranslatingModSummary" => "Translating...",
            "ShowOriginalModSummary" => "Show original",
            "ShowTranslatedModSummary" => "Show translation",
            _ => key
        });
        ApplySearchKeyword(null);
    }

    private void RefreshSummaryTranslationText(Func<string, string> translate)
    {
        SummaryTranslationActionText = IsSummaryTranslationBusy
            ? translate("TranslatingModSummary")
            : HasTranslationForCurrentLanguage
                ? IsShowingTranslatedSummary
                    ? translate("ShowOriginalModSummary")
                    : translate("ShowTranslatedModSummary")
                : translate("TranslateModSummary");
    }

    private void RefreshDownloadTexts(Func<string, string> translate)
    {
        if (IsDownloaded)
        {
            DownloadStateText = translate("ModDownloadStatusDownloaded");
            DownloadActionText = translate("ModDownloadStatusDownloaded");
            return;
        }

        DownloadStateText = DirectDownloadEnabled
            ? IsSteamWorkshop
                ? translate("WorkshopSubscribeViaSteam")
                : translate("ManagerDownload")
            : translate("BrowserSignInOrApiKeyRequired");
        DownloadActionText = translate("Download");
    }

    private (int Start, int Length)? FindNameMatch(string? keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return null;
        }

        var trimmedKeyword = keyword.Trim();
        var fullPhraseIndex = Name.IndexOf(trimmedKeyword, StringComparison.OrdinalIgnoreCase);
        if (fullPhraseIndex >= 0)
        {
            return (fullPhraseIndex, trimmedKeyword.Length);
        }

        return trimmedKeyword
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .OrderByDescending(token => token.Length)
            .Select(token => (Start: Name.IndexOf(token, StringComparison.OrdinalIgnoreCase), Length: token.Length))
            .FirstOrDefault(match => match.Start >= 0) switch
            {
                { Start: >= 0 } match => match,
                _ => null
            };
    }
}
