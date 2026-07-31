using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Media.Imaging;
using BohemiX.App.Controls;
using BohemiX.App.Services;
using BohemiX.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BohemiX.App.ViewModels;

public sealed partial class ModPackRowViewModel : ViewModelBase, IDisposable, IVisualResourceOwner
{
    private const int ThumbnailDecodeWidth = 384;
    private BitmapLease? thumbnailLease;
    private bool visualResourcesActive;

    public ModPackRowViewModel(ModPackCatalogEntry entry)
    {
        Entry = entry;
        Id = entry.Id;
        Name = entry.Title;
        Summary = entry.Summary;
        Author = entry.Curator;
        Platform = entry.Platform;
        PlatformIdentifier = entry.PlatformIdentifier;
        OfficialPageUrl = entry.OfficialPageUrl;
        Category = entry.Category;
        Tags = entry.Tags;
        ItemCount = entry.ItemCount;
        GameVersionNote = entry.GameVersionNote;
        UpdatedAt = entry.UpdatedAt;
        ReviewedAt = entry.ReviewedAt;
        ContainsAdultContent = entry.ContainsAdultContent;
        RemoteThumbnailUrl = entry.ThumbnailUrl;
        ThumbnailIsRepresentative = entry.ThumbnailIsRepresentative;
        isThumbnailLoading = !string.IsNullOrWhiteSpace(RemoteThumbnailUrl);
        ThumbnailCacheId = CreateThumbnailCacheId(entry.Id);
        ApplyLocalization(key => key);
        ApplySearchKeyword(null);
    }

    public ModPackCatalogEntry Entry { get; }

    public string Id { get; }

    public string Name { get; }

    public string Summary { get; }

    public string Author { get; }

    public ModPackPlatform Platform { get; }

    public string PlatformIdentifier { get; }

    public string OfficialPageUrl { get; }

    public ModPackCategory Category { get; }

    public IReadOnlyList<string> Tags { get; }

    public int ItemCount { get; }

    public string GameVersionNote { get; }

    public DateTimeOffset UpdatedAt { get; }

    public DateTimeOffset ReviewedAt { get; }

    public bool ContainsAdultContent { get; }

    public string? RemoteThumbnailUrl { get; }

    public bool ThumbnailIsRepresentative { get; }

    public int ThumbnailCacheId { get; }

    public bool IsSteamWorkshop => Platform == ModPackPlatform.SteamWorkshopCollection;

    public bool IsNexusCollection => !IsSteamWorkshop;

    [ObservableProperty]
    private string platformText = string.Empty;

    [ObservableProperty]
    private string primaryActionText = string.Empty;

    [ObservableProperty]
    private string rightsText = string.Empty;

    [ObservableProperty]
    private string itemCountText = string.Empty;

    [ObservableProperty]
    private string updatedText = string.Empty;

    [ObservableProperty]
    private string adultContentText = string.Empty;

    [ObservableProperty]
    private string representativeThumbnailText = string.Empty;

    [ObservableProperty]
    private bool isSelected;

    [ObservableProperty]
    private bool isInstalling;

    [ObservableProperty]
    private string nameHighlightPrefix = string.Empty;

    [ObservableProperty]
    private string nameHighlightText = string.Empty;

    [ObservableProperty]
    private string nameHighlightSuffix = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasThumbnail))]
    [NotifyPropertyChangedFor(nameof(HasNoThumbnail))]
    private string? thumbnailPath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasThumbnail))]
    [NotifyPropertyChangedFor(nameof(HasNoThumbnail))]
    private Bitmap? thumbnailImage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoThumbnail))]
    private bool isThumbnailLoading;

    public bool HasThumbnail => ThumbnailImage is not null;

    public bool HasNoThumbnail => !HasThumbnail && !IsThumbnailLoading;

    public string MetaText => $"{Author} / {PlatformIdentifier}";

    public string CompatibilityText => GameVersionNote;

    public string TagsText => string.Join(" / ", Tags);

    public void ApplyLocalization(Func<string, string> translate)
    {
        PlatformText = IsSteamWorkshop
            ? translate("SteamWorkshopCollectionSource")
            : translate("NexusCollectionSource");
        PrimaryActionText = IsSteamWorkshop
            ? translate("InstallModPackViaSteam")
            : translate("InstallModPackViaNexus");
        RightsText = translate("ModPackOfficialPlatformRights");
        AdultContentText = translate("ModPackContainsAdultContent");
        RepresentativeThumbnailText = translate("ModPackRepresentativeThumbnail");
        ItemCountText = string.Format(CultureInfo.CurrentCulture, translate("ModPackItemCount"), ItemCount);
        UpdatedText = string.Format(
            CultureInfo.CurrentCulture,
            translate("ModPackUpdatedAt"),
            UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    }

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

    public void SetThumbnailPath(string? path, bool activateVisualResources = false)
    {
        ReleaseThumbnail();
        ThumbnailPath = path;
        IsThumbnailLoading = false;
        if (activateVisualResources || visualResourcesActive)
        {
            ActivateVisualResources();
        }
    }

    public void ActivateVisualResources()
    {
        visualResourcesActive = true;
        if (thumbnailLease is not null || string.IsNullOrWhiteSpace(ThumbnailPath))
        {
            return;
        }

        thumbnailLease = SharedBitmapLeaseCache.Instance.Acquire(ThumbnailPath, ThumbnailDecodeWidth);
        ThumbnailImage = thumbnailLease?.Bitmap;
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

    private (int Start, int Length)? FindNameMatch(string? keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return null;
        }

        var trimmed = keyword.Trim();
        var fullMatch = Name.IndexOf(trimmed, StringComparison.OrdinalIgnoreCase);
        if (fullMatch >= 0)
        {
            return (fullMatch, trimmed.Length);
        }

        return trimmed
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .OrderByDescending(token => token.Length)
            .Select(token => (Start: Name.IndexOf(token, StringComparison.OrdinalIgnoreCase), Length: token.Length))
            .FirstOrDefault(match => match.Start >= 0) switch
            {
                { Start: >= 0 } match => match,
                _ => null
            };
    }

    internal static int CreateThumbnailCacheId(string id)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(id));
        return unchecked((int)(0x60000000 | (BitConverter.ToInt32(hash, 0) & 0x1FFFFFFF)));
    }
}
