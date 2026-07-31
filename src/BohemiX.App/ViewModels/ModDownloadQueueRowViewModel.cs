using System;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using BohemiX.App.Controls;
using BohemiX.App.Services;
using BohemiX.Core.Models;
using BohemiX.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BohemiX.App.ViewModels;

public sealed partial class ModDownloadQueueRowViewModel : ViewModelBase, IDisposable, IVisualResourceOwner
{
    private const int CoverDecodeWidth = 320;
    private const double DownloadSpeedSmoothingFactor = 0.25;
    private const string FallbackCoverUri = "avares://BohemiX.App/Assets/kcd2-hero-bg.jpg";
    private Func<string, string>? translate;
    private BitmapLease? coverLease;
    private string? coverPath;
    private double? smoothedBytesPerSecond;
    private bool visualResourcesActive;
    private bool disposed;

    public ModDownloadQueueRowViewModel(ModDownloadQueueItem item)
        : this(
            item.QueueKey,
            item.Request.ModId,
            item.Request.FileId,
            item.Request.FileName,
            item.Request.DestinationDirectory,
            item.Request.ModPackSessionId,
            item.Request.ModPackId,
            item.Request.ModPackName)
    {
        Status = item.Status;
        ErrorMessage = item.ErrorMessage;
        RefreshDisplayText();
    }

    public ModDownloadQueueRowViewModel(
        string queueKey,
        int modId,
        int fileId,
        string fileName,
        string? destinationDirectory = null,
        Guid? modPackSessionId = null,
        string? modPackId = null,
        string? modPackName = null)
    {
        QueueKey = queueKey;
        ModId = modId;
        FileId = fileId;
        FileName = fileName;
        ModName = $"Mod {modId}";
        DestinationDirectory = destinationDirectory;
        ModPackSessionId = modPackSessionId;
        ModPackId = modPackId;
        ModPackName = modPackName;
        RefreshDisplayText();
    }

    public string QueueKey { get; }

    public int ModId { get; }

    public int FileId { get; }

    public string FileName { get; }

    public string? DestinationDirectory { get; private set; }

    public string? FinalPath { get; private set; }

    public Guid? ModPackSessionId { get; private set; }

    public string? ModPackId { get; private set; }

    public string? ModPackName { get; private set; }

    public bool IsModPackDownload => ModPackSessionId is not null;

    [ObservableProperty]
    private ModDownloadStatus status = ModDownloadStatus.Pending;

    [ObservableProperty]
    private long bytesDownloaded;

    [ObservableProperty]
    private long? totalBytes;

    [ObservableProperty]
    private double progressPercent;

    [ObservableProperty]
    private double bytesPerSecond;

    [ObservableProperty]
    private string progressText = "Pending";

    [ObservableProperty]
    private string progressPercentText = "0%";

    [ObservableProperty]
    private string sizeText = "-";

    [ObservableProperty]
    private string speedText = "-";

    [ObservableProperty]
    private string statusText = "Pending";

    [ObservableProperty]
    private string statusBadgeText = "Queued";

    [ObservableProperty]
    private IBrush statusBadgeBackground = new SolidColorBrush(0xFFEFF6FF);

    [ObservableProperty]
    private IBrush statusBadgeForeground = new SolidColorBrush(0xFF1D4ED8);

    [ObservableProperty]
    private string? errorMessage;

    [ObservableProperty]
    private string installText = "Not installed";

    [ObservableProperty]
    private bool canPause;

    [ObservableProperty]
    private bool canResume;

    [ObservableProperty]
    private bool canCancel = true;

    [ObservableProperty]
    private bool canRetry;

    [ObservableProperty]
    private bool canOpenFolder;

    [ObservableProperty]
    private bool isCompletedGroup;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCoverImage))]
    private Bitmap? coverImage;

    [ObservableProperty]
    private string modName = string.Empty;

    public string ModText => $"Mod {ModId} / File {FileId}";

    public string SourceText => "Nexus Mods";

    public bool HasCoverImage => CoverImage is not null;

    public void SetModPresentation(
        string? name,
        string? coverPath,
        bool activateVisualResources = false)
    {
        if (disposed)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            ModName = name.Trim();
        }

        this.coverPath = coverPath;
        ReleaseCover();
        if (activateVisualResources || visualResourcesActive)
        {
            ActivateVisualResources();
        }
    }

    public void ActivateVisualResources()
    {
        visualResourcesActive = true;
        if (disposed || coverLease is not null)
        {
            return;
        }

        var source = string.IsNullOrWhiteSpace(coverPath) ? FallbackCoverUri : coverPath;
        coverLease?.Dispose();
        coverLease = SharedBitmapLeaseCache.Instance.Acquire(source, CoverDecodeWidth);
        CoverImage = coverLease?.Bitmap;

        if (CoverImage is null && !string.Equals(source, FallbackCoverUri, StringComparison.OrdinalIgnoreCase))
        {
            coverLease = SharedBitmapLeaseCache.Instance.Acquire(FallbackCoverUri, CoverDecodeWidth);
            CoverImage = coverLease?.Bitmap;
        }
    }

    public void DeactivateVisualResources()
    {
        visualResourcesActive = false;
        ReleaseCover();
    }

    private void ReleaseCover()
    {
        CoverImage = null;
        coverLease?.Dispose();
        coverLease = null;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        DeactivateVisualResources();
    }

    public void ApplyProgress(ModDownloadProgress progress)
    {
        Status = progress.Status;
        BytesDownloaded = progress.BytesDownloaded;
        TotalBytes = progress.TotalBytes;
        ProgressPercent = progress.Percent ?? ProgressPercent;
        BytesPerSecond = SmoothDownloadSpeed(progress.BytesPerSecond, progress.Status);
        RefreshDisplayText();
    }

    private double SmoothDownloadSpeed(double sample, ModDownloadStatus progressStatus)
    {
        if (progressStatus != ModDownloadStatus.Downloading)
        {
            smoothedBytesPerSecond = null;
            return 0;
        }

        sample = Math.Max(0, sample);
        if (sample <= 0 && smoothedBytesPerSecond is > 0)
        {
            return smoothedBytesPerSecond.Value;
        }

        smoothedBytesPerSecond = smoothedBytesPerSecond is > 0
            ? (DownloadSpeedSmoothingFactor * sample) + ((1 - DownloadSpeedSmoothingFactor) * smoothedBytesPerSecond.Value)
            : sample;
        return smoothedBytesPerSecond.Value;
    }

    partial void OnStatusChanged(ModDownloadStatus value)
    {
        if (value == ModDownloadStatus.Downloading)
        {
            return;
        }

        smoothedBytesPerSecond = null;
        BytesPerSecond = 0;
    }

    public void ApplyResult(ModDownloadResult result)
    {
        Status = result.Status;
        ErrorMessage = result.ErrorMessage;
        FinalPath = result.FinalPath;
        ProgressPercent = result.Success ? 100 : ProgressPercent;
        RefreshDisplayText();
    }

    public void ApplyQueueItem(ModDownloadQueueItem item)
    {
        Status = item.Status;
        ErrorMessage = item.ErrorMessage;
        DestinationDirectory = item.Request.DestinationDirectory;
        ApplyModPackMetadata(item.Request);
        if (Status == ModDownloadStatus.Completed)
        {
            ProgressPercent = 100;
        }

        RefreshDisplayText();
    }

    private void ApplyModPackMetadata(NexusModDownloadRequest request)
    {
        var wasModPackDownload = IsModPackDownload;
        var previousSessionId = ModPackSessionId;
        var previousModPackId = ModPackId;
        var previousModPackName = ModPackName;

        ModPackSessionId = request.ModPackSessionId;
        ModPackId = request.ModPackId;
        ModPackName = request.ModPackName;

        if (previousSessionId != ModPackSessionId)
        {
            OnPropertyChanged(nameof(ModPackSessionId));
        }

        if (!string.Equals(previousModPackId, ModPackId, StringComparison.Ordinal))
        {
            OnPropertyChanged(nameof(ModPackId));
        }

        if (!string.Equals(previousModPackName, ModPackName, StringComparison.Ordinal))
        {
            OnPropertyChanged(nameof(ModPackName));
        }

        if (wasModPackDownload != IsModPackDownload)
        {
            OnPropertyChanged(nameof(IsModPackDownload));
        }
    }

    public void ApplyInstallResult(ModPackageInstallResult result)
    {
        InstallText = result.Success
            ? string.Format(T("ModDownloadInstalledPath"), result.InstalledRootPath)
            : result.Message;
    }

    public void ApplyLocalization(Func<string, string> translate)
    {
        ArgumentNullException.ThrowIfNull(translate);

        this.translate = translate;
        RefreshDisplayText();
    }

    private void RefreshDisplayText()
    {
        StatusBadgeText = Status switch
        {
            ModDownloadStatus.Pending => T("ModDownloadStatusQueued"),
            ModDownloadStatus.Resolving => T("ModDownloadStatusResolving"),
            ModDownloadStatus.Downloading => T("ModDownloadStatusDownloading"),
            ModDownloadStatus.Paused => T("ModDownloadStatusPaused"),
            ModDownloadStatus.Completed => T("ModDownloadStatusDownloaded"),
            ModDownloadStatus.Canceled => T("ModDownloadStatusCanceled"),
            ModDownloadStatus.Failed => T("ModDownloadStatusFailed"),
            ModDownloadStatus.ChecksumFailed => T("ModDownloadStatusChecksum"),
            _ => Status.ToString()
        };
        StatusText = ErrorMessage is null ? StatusBadgeText : $"{StatusBadgeText}: {ErrorMessage}";
        (StatusBadgeBackground, StatusBadgeForeground) = Status switch
        {
            ModDownloadStatus.Downloading => (new SolidColorBrush(0xFFEFF6FF), new SolidColorBrush(0xFF1D4ED8)),
            ModDownloadStatus.Completed => (new SolidColorBrush(0xFFDCFCE7), new SolidColorBrush(0xFF166534)),
            ModDownloadStatus.Paused => (new SolidColorBrush(0xFFFEF3C7), new SolidColorBrush(0xFF92400E)),
            ModDownloadStatus.Canceled => (new SolidColorBrush(0xFFF1F5F9), new SolidColorBrush(0xFF475569)),
            ModDownloadStatus.Failed or ModDownloadStatus.ChecksumFailed => (new SolidColorBrush(0xFFFEE2E2), new SolidColorBrush(0xFF991B1B)),
            ModDownloadStatus.Resolving => (new SolidColorBrush(0xFFE0F2FE), new SolidColorBrush(0xFF0369A1)),
            _ => (new SolidColorBrush(0xFFF1F5F9), new SolidColorBrush(0xFF334155))
        };
        ProgressPercentText = $"{ProgressPercent.ToString("0", CultureInfo.InvariantCulture)}%";
        SizeText = TotalBytes is null or <= 0 ? "-" : FormatSize(TotalBytes.Value);
        ProgressText = TotalBytes is null
            ? $"{FormatSize(BytesDownloaded)} / -"
            : $"{FormatSize(BytesDownloaded)} / {FormatSize(TotalBytes.Value)}";
        SpeedText = Status switch
        {
            ModDownloadStatus.Downloading when BytesPerSecond > 0 => $"{FormatSize((long)BytesPerSecond)}/s",
            ModDownloadStatus.Downloading => T("ModDownloadSpeedStarting"),
            ModDownloadStatus.Completed => T("ModDownloadSpeedDone"),
            ModDownloadStatus.Paused => T("ModDownloadStatusPaused"),
            ModDownloadStatus.Canceled => T("ModDownloadStatusCanceled"),
            ModDownloadStatus.Pending => T("ModDownloadStatusQueued"),
            _ => "-"
        };
        IsCompletedGroup = Status == ModDownloadStatus.Completed;
        CanPause = Status == ModDownloadStatus.Downloading;
        CanResume = Status == ModDownloadStatus.Paused;
        CanRetry = ModDownloadQueuePolicy.RequiresAttention(Status) || Status == ModDownloadStatus.Canceled;
        CanCancel = ModDownloadQueuePolicy.CanCancel(Status);
        CanOpenFolder = !string.IsNullOrWhiteSpace(DestinationDirectory);
        if (string.Equals(InstallText, "Not installed", StringComparison.Ordinal) ||
            string.Equals(InstallText, "未安装", StringComparison.Ordinal) ||
            string.Equals(InstallText, T("ModDownloadInstallNotInstalled"), StringComparison.Ordinal))
        {
            InstallText = T("ModDownloadInstallNotInstalled");
        }
    }

    private string T(string key) => translate?.Invoke(key) ?? key switch
    {
        "ModDownloadStatusPending" => "Pending",
        "ModDownloadStatusQueued" => "Queued",
        "ModDownloadStatusResolving" => "Resolving",
        "ModDownloadStatusDownloading" => "Downloading",
        "ModDownloadStatusPaused" => "Paused",
        "ModDownloadStatusDownloaded" => "Downloaded",
        "ModDownloadStatusCanceled" => "Canceled",
        "ModDownloadStatusFailed" => "Failed",
        "ModDownloadStatusChecksum" => "Checksum",
        "ModDownloadSpeedStarting" => "Starting...",
        "ModDownloadSpeedDone" => "Done",
        "ModDownloadInstallNotInstalled" => "Not installed",
        "ModDownloadInstalledPath" => "Installed: {0}",
        _ => key
    };

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
            ? $"{size.ToString("0", CultureInfo.InvariantCulture)} {units[unitIndex]}"
            : $"{size.ToString("0.0", CultureInfo.InvariantCulture)} {units[unitIndex]}";
    }
}
