using Avalonia.Media.Imaging;
using BohemiX.Core.Models.Saves;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BohemiX.Modules.SaveManager.Models;

public sealed class SaveProfileListItem : ObservableObject, IDisposable
{
    private static readonly HashSet<string> SupportedThumbnailExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp", ".webp" };
    private SaveManagerText presentationText = SaveManagerText.Chinese;
    private Bitmap? thumbnailImage;

    public SaveProfileListItem(SaveProfile profile, SaveManagerText? text = null, string? compactModSummary = null)
    {
        Profile = profile;
        ThumbnailPath = ResolveThumbnailPath(profile);
        UpdatePresentation(text ?? SaveManagerText.Chinese, compactModSummary ?? string.Empty);
    }

    public SaveProfile Profile { get; private set; }
    public Guid Id => Profile.Id;
    public string DisplayName => Profile.DisplayName;
    public bool IsFavorite => Profile.IsFavorite;
    public bool IsActive => Profile.IsActive;
    public string Quest => string.IsNullOrWhiteSpace(Profile.DisplayData?.ActiveQuest) ? "—" : Profile.DisplayData.ActiveQuest;
    public string Location => string.IsNullOrWhiteSpace(Profile.DisplayData?.CurrentLocation) ? "—" : Profile.DisplayData.CurrentLocation;
    public string PrimaryTitle => Quest == "—" ? DisplayName : Quest;
    public string LocationSubtitle { get; private set; } = string.Empty;
    public string QuestAndLocation => Quest == "—" ? Location : Location == "—" ? Quest : $"{Quest} · {Location}";
    public string PlayTimeText => Profile.PlayTime is null ? "—" : $"{(int)Profile.PlayTime.Value.TotalHours:D2}:{Profile.PlayTime.Value.Minutes:D2}";
    public string LastSavedText => Profile.LastSavedAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "—";
    public string UpdatedText => Profile.UpdatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string StatusText => IsActive ? "使用中" : "";
    public string SaveTypeText { get; private set; } = string.Empty;
    public string CompactModSummary { get; private set; } = string.Empty;
    public string FavoriteActionText => IsFavorite ? presentationText.Unfavorite : presentationText.Favorite;
    public string GameSaveTooltip => string.IsNullOrWhiteSpace(Profile.GameSaveName) ? DisplayName : Profile.GameSaveName;
    public string? ThumbnailPath { get; }
    public Bitmap? ThumbnailImage
    {
        get => thumbnailImage;
        private set
        {
            if (!SetProperty(ref thumbnailImage, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasThumbnail));
            OnPropertyChanged(nameof(HasNoThumbnail));
        }
    }
    public bool HasThumbnail => ThumbnailImage is not null;
    public bool HasNoThumbnail => !HasThumbnail;

    public void UpdatePresentation(SaveManagerText text, string compactModSummary)
    {
        presentationText = text;
        LocationSubtitle = Location == "—" ? text.LocationUnknown : Location;
        SaveTypeText = Profile.Type switch
        {
            SaveType.Potion => text.SaveTypePotion,
            SaveType.Bed => text.SaveTypeBed,
            SaveType.Auto => text.SaveTypeAuto,
            SaveType.Exit => text.SaveTypeExit,
            _ => text.SaveTypeUnknown
        };
        CompactModSummary = compactModSummary;
        OnPropertyChanged(nameof(LocationSubtitle));
        OnPropertyChanged(nameof(SaveTypeText));
        OnPropertyChanged(nameof(CompactModSummary));
        OnPropertyChanged(nameof(FavoriteActionText));
    }

    public void SetFavorite(bool isFavorite)
    {
        if (Profile.IsFavorite == isFavorite)
        {
            return;
        }

        Profile = Profile with
        {
            IsFavorite = isFavorite,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        OnPropertyChanged(nameof(Profile));
        OnPropertyChanged(nameof(IsFavorite));
        OnPropertyChanged(nameof(FavoriteActionText));
        OnPropertyChanged(nameof(UpdatedText));
    }

    private static string? ResolveThumbnailPath(SaveProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.ThumbnailPath) || string.IsNullOrWhiteSpace(profile.PhysicalPath))
        {
            return null;
        }

        try
        {
            var root = Path.GetFullPath(profile.PhysicalPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var candidate = Path.IsPathRooted(profile.ThumbnailPath)
                ? Path.GetFullPath(profile.ThumbnailPath)
                : Path.GetFullPath(Path.Combine(root, profile.ThumbnailPath));
            var insideRoot = string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase)
                || candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

            return insideRoot
                && SupportedThumbnailExtensions.Contains(Path.GetExtension(candidate))
                && File.Exists(candidate)
                    ? candidate
                    : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static Bitmap? LoadThumbnail(string? path)
    {
        if (path is null)
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(path);
            return Bitmap.DecodeToWidth(stream, 256, Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
        }
        catch
        {
            return null;
        }
    }

    public void ActivateVisualResources()
    {
        if (ThumbnailImage is null)
        {
            ThumbnailImage = LoadThumbnail(ThumbnailPath);
        }
    }

    public void DeactivateVisualResources()
    {
        var previous = ThumbnailImage;
        ThumbnailImage = null;
        previous?.Dispose();
    }

    public void Dispose() => DeactivateVisualResources();
}
