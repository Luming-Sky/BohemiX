using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia.Media.Imaging;
using BohemiX.App.Controls;
using BohemiX.App.Services;
using BohemiX.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BohemiX.App.ViewModels;

public sealed partial class ModListItemViewModel : ViewModelBase, IDisposable, IVisualResourceOwner
{
    private const int CoverDecodeWidth = 256;
    private const string BuiltInTrackerModId = "bohemix-tracker";
    private const string BuiltInTrackerDisplayName = "BohemiX Tracker";
    private static readonly string[] CoverFileNames =
    [
        "cover",
        "thumbnail",
        "thumb",
        "preview",
        "image",
        "icon",
        "logo"
    ];

    private static readonly string[] CoverExtensions =
    [
        ".jpg",
        ".jpeg",
        ".png",
        ".webp",
        ".bmp"
    ];

    public ModListItemViewModel(ModManifest manifest, string? dataDirectory = null)
    {
        Id = manifest.Id;
        DisplayName = manifest.DisplayName;
        Version = manifest.Version;
        RootPath = manifest.RootPath;
        FileCount = manifest.Files.Count;
        TotalSizeInBytes = manifest.Files.Sum(file => file.SizeInBytes);
        TotalSizeText = FormatSize(TotalSizeInBytes);
        LoadOrder = manifest.LoadOrder;
        IsBuiltIn = IsBuiltInTracker(manifest.Id, manifest.DisplayName, manifest.RootPath);
        IsEnabled = IsBuiltIn || manifest.IsEnabled;
        CoverImagePath = TryFindCoverImage(manifest.RootPath) ?? TryFindCachedCoverImage(manifest.Id, dataDirectory);
        ModPackId = manifest.Source?.ModPackId;
        ModPackName = manifest.Source?.ModPackName;
        ModPackRevision = manifest.Source?.ModPackRevision;
        ModPackSessionId = manifest.Source?.ModPackSessionId;
        NexusModId = manifest.Source?.NexusModId ?? TryGetNexusModId(manifest.Id);
    }

    public string Id { get; }

    public string DisplayName { get; }

    public string Version { get; }

    public string RootPath { get; }

    public int FileCount { get; }

    public long TotalSizeInBytes { get; }

    public string TotalSizeText { get; }

    public bool IsBuiltIn { get; }

    public bool CanDelete => !IsBuiltIn;

    public bool CanToggleEnabled => !IsBuiltIn;

    [ObservableProperty]
    private string? coverImagePath;

    public int? NexusModId { get; }

    public string? ModPackId { get; }

    public string? ModPackName { get; }

    public int? ModPackRevision { get; }

    public Guid? ModPackSessionId { get; }

    public bool IsInstalledFromModPack => !string.IsNullOrWhiteSpace(ModPackId);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCoverImage))]
    [NotifyPropertyChangedFor(nameof(HasNoCoverImage))]
    private Bitmap? coverImage;

    private BitmapLease? coverImageLease;
    private bool visualResourcesActive;

    public bool HasCoverImage => CoverImage is not null;

    public bool HasNoCoverImage => !HasCoverImage;

    [ObservableProperty]
    private int loadOrder;

    [ObservableProperty]
    private bool isEnabled;

    [ObservableProperty]
    private bool isConflicted;

    [ObservableProperty]
    private bool hasOverrides;

    [ObservableProperty]
    private bool isSelected;

    [ObservableProperty]
    private bool isOrderDropTargetActive;

    [ObservableProperty]
    private bool isAssignedToCustomGroup;

    [ObservableProperty]
    private string conflictSummary = "No conflicts";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConflictOutcomeSummary))]
    private string conflictOutcomeSummary = "No overrides";

    public bool HasConflictOutcomeSummary => !string.IsNullOrWhiteSpace(ConflictOutcomeSummary);

    public ObservableCollection<NexusModRequirementViewModel> MissingRequirements { get; } = [];

    [ObservableProperty]
    private string missingRequirementsSummary = string.Empty;

    public bool HasMissingRequirements => MissingRequirements.Count > 0;

    public string FileSummary => $"{FileCount.ToString(CultureInfo.InvariantCulture)} files / {TotalSizeText}";

    public void ApplyMissingRequirements(
        IEnumerable<NexusModRequirementViewModel> requirements,
        string summary)
    {
        MissingRequirements.Clear();
        foreach (var requirement in requirements)
        {
            MissingRequirements.Add(requirement);
        }

        MissingRequirementsSummary = HasMissingRequirements ? summary : string.Empty;
        OnPropertyChanged(nameof(HasMissingRequirements));
    }

    public static bool IsBuiltInTracker(string id, string displayName, string rootPath)
    {
        var normalizedRoot = rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var folderName = Path.GetFileName(normalizedRoot);

        return string.Equals(id, BuiltInTrackerModId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(folderName, BuiltInTrackerModId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(displayName, BuiltInTrackerDisplayName, StringComparison.OrdinalIgnoreCase);
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

    private static string? TryFindCoverImage(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
        {
            return null;
        }

        foreach (var fileName in CoverFileNames)
        {
            foreach (var extension in CoverExtensions)
            {
                var candidate = Path.Combine(rootPath, fileName + extension);
                if (IsLoadableCoverImage(candidate))
                {
                    return candidate;
                }
            }
        }

        try
        {
            return Directory.EnumerateFiles(rootPath, "*.*", SearchOption.TopDirectoryOnly)
                .Where(IsSupportedCoverImage)
                .Where(IsLoadableCoverImage)
                .OrderByDescending(path => CoverFileNames.Any(name => Path.GetFileNameWithoutExtension(path).Contains(name, StringComparison.OrdinalIgnoreCase)))
                .ThenBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? TryFindCachedCoverImage(string modId, string? dataDirectory)
    {
        if (string.IsNullOrWhiteSpace(modId) || string.IsNullOrWhiteSpace(dataDirectory))
        {
            return null;
        }

        var nexusId = TryGetNexusModId(modId);
        if (nexusId is null)
        {
            return null;
        }

        var cacheDirectory = Path.Combine(dataDirectory, "mod-covers");
        if (!Directory.Exists(cacheDirectory))
        {
            return null;
        }

        try
        {
            return Directory.EnumerateFiles(cacheDirectory, $"{nexusId}-*.*", SearchOption.TopDirectoryOnly)
                .Where(IsSupportedCoverImage)
                .Where(IsLoadableCoverImage)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static int? TryGetNexusModId(string modId)
    {
        const string nexusPrefix = "nexus-";
        if (!modId.StartsWith(nexusPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var suffix = modId[nexusPrefix.Length..];
        var separatorIndex = suffix.IndexOf('-');
        var idText = separatorIndex >= 0 ? suffix[..separatorIndex] : suffix;
        return int.TryParse(idText, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            ? id
            : null;
    }

    private static bool IsSupportedCoverImage(string path)
    {
        var extension = Path.GetExtension(path);
        return CoverExtensions.Any(value => string.Equals(value, extension, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsLoadableCoverImage(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length == 0)
            {
                return false;
            }

            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> header = stackalloc byte[12];
            var bytesRead = stream.Read(header);
            return bytesRead >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF
                || bytesRead >= 8
                    && header[0] == 0x89
                    && header[1] == 0x50
                    && header[2] == 0x4E
                    && header[3] == 0x47
                    && header[4] == 0x0D
                    && header[5] == 0x0A
                    && header[6] == 0x1A
                    && header[7] == 0x0A
                || bytesRead >= 12
                    && header[0] == 0x52
                    && header[1] == 0x49
                    && header[2] == 0x46
                    && header[3] == 0x46
                    && header[8] == 0x57
                    && header[9] == 0x45
                    && header[10] == 0x42
                    && header[11] == 0x50
                || bytesRead >= 2 && header[0] == 0x42 && header[1] == 0x4D;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    public void ActivateVisualResources()
    {
        visualResourcesActive = true;
        if (coverImageLease is not null || string.IsNullOrWhiteSpace(CoverImagePath))
        {
            return;
        }

        coverImageLease = SharedBitmapLeaseCache.Instance.Acquire(CoverImagePath, CoverDecodeWidth);
        CoverImage = coverImageLease?.Bitmap;
    }

    public void DeactivateVisualResources()
    {
        visualResourcesActive = false;
        ReleaseCoverImage();
    }

    public void SetCoverImagePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (string.Equals(CoverImagePath, value, StringComparison.OrdinalIgnoreCase))
        {
            if (visualResourcesActive && CoverImage is null)
            {
                ActivateVisualResources();
            }

            return;
        }

        ReleaseCoverImage();
        CoverImagePath = value;
        if (visualResourcesActive)
        {
            ActivateVisualResources();
        }
    }

    public void Dispose() => DeactivateVisualResources();

    private void ReleaseCoverImage()
    {
        CoverImage = null;
        coverImageLease?.Dispose();
        coverImageLease = null;
    }
}
