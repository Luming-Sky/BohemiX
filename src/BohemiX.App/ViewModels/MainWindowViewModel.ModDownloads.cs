using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia.Threading;
using BohemiX.App.Models;
using BohemiX.Core.Models;
using BohemiX.Core.Services;
using CommunityToolkit.Mvvm.Input;

namespace BohemiX.App.ViewModels;

public partial class MainWindowViewModel
{
    private Task? backgroundModDownloadQueueTask;

    private async Task InstallSteamWorkshopModAsync(NexusModSearchRowViewModel mod)
    {
        if (IsDownloadingMods || IsShuttingDown)
        {
            return;
        }

        try
        {
            IsDownloadingMods = true;
            var modsDirectory = applicationPathService.GetPaths().ModsDirectory;
            ModsDirectory = modsDirectory;

            var progress = new Progress<WorkshopInstallProgress>(progress =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    NexusStatusText = FormatWorkshopInstallProgress(mod, progress);
                    StatusText = NexusStatusText;
                    LastActionText = StatusText;
                });
            });

            NexusStatusText = string.Format(T("SubscribingSteamWorkshopItem"), mod.Name);
            StatusText = NexusStatusText;
            LastActionText = StatusText;

            var result = await workshopService.SubscribeAndInstallAsync(
                new WorkshopInstallRequest(mod.PublishedFileId, modsDirectory, WorkshopDeployMode.Copy),
                progress,
                shutdownCancellation.Token);

            NexusStatusText = result.Success
                ? string.Format(T("SteamWorkshopInstallComplete"), mod.Name)
                : result.Message;
            StatusText = NexusStatusText;
            LastActionText = StatusText;

            if (result.Success)
            {
                await RefreshModsAsync();
                RefreshModSearchDownloadStates();
            }
        }
        catch (WorkshopException ex)
        {
            NexusStatusText = FormatWorkshopError(ex);
            StatusText = NexusStatusText;
            LastActionText = StatusText;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            NexusStatusText = string.Format(T("SteamWorkshopInstallFailed"), ex.Message);
            StatusText = NexusStatusText;
            LastActionText = StatusText;
        }
        finally
        {
            IsDownloadingMods = false;
            if (!IsShuttingDown && modDownloader.Queue.Any(item => item.Status == ModDownloadStatus.Pending))
            {
                StartModDownloadQueueInBackground();
            }
        }
    }

    [RelayCommand]
    private async Task StartModDownloadQueueAsync()
    {
        if (IsDownloadingMods || IsShuttingDown)
        {
            return;
        }

        try
        {
            IsDownloadingMods = true;
            if (!await EnsureNexusAccountBoundAsync())
            {
                return;
            }

            SyncDownloadQueueRows(modDownloader.Queue);

            if (ModDownloadQueueRows.Count == 0)
            {
                NexusStatusText = T("NoTasks");
                StatusText = NexusStatusText;
                LastActionText = StatusText;
                return;
            }

            var progress = new Progress<ModDownloadProgress>(progress =>
            {
                Dispatcher.UIThread.Post(() => ApplyDownloadProgress(progress));
            });

            var results = await modDownloader.StartQueuedDownloadsAsync(progress, shutdownCancellation.Token);
            modDownloader.ClearCanceledQueueItems();
            SyncDownloadQueueRows(modDownloader.Queue);
            RefreshModSearchDownloadStates();
            var completed = results.Count(result => result.Success);
            var installed = await InstallCompletedDownloadsAsync(results);
            modDownloader.ClearCompletedQueueItems();
            SyncDownloadQueueRows(modDownloader.Queue);
            RefreshModSearchDownloadStates();
            NexusStatusText = string.Format(T("ModDownloadQueueFinished"), completed, results.Count, installed);
            StatusText = NexusStatusText;
            LastActionText = StatusText;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            NexusStatusText = string.Format(T("ModDownloadQueueFailed"), ex.Message);
            StatusText = NexusStatusText;
            LastActionText = StatusText;
        }
        catch (Exception ex)
        {
            NexusStatusText = string.Format(T("ModDownloadQueueFailed"), ex.Message);
            StatusText = NexusStatusText;
            LastActionText = StatusText;
        }
        finally
        {
            IsDownloadingMods = false;
            if (!IsShuttingDown && modDownloader.Queue.Any(item => item.Status == ModDownloadStatus.Pending))
            {
                StartModDownloadQueueInBackground();
            }
        }
    }

    private void StartModDownloadQueueInBackground()
    {
        if (IsShuttingDown)
        {
            return;
        }

        var task = StartModDownloadQueueAsync();
        backgroundModDownloadQueueTask = task;
        _ = ObserveBackgroundModDownloadQueueAsync(task);
    }

    private async Task ObserveBackgroundModDownloadQueueAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception ex)
        {
            Serilog.Log.Logger.Warning(ex, "Background mod download queue failed");
        }
        finally
        {
            if (ReferenceEquals(backgroundModDownloadQueueTask, task))
            {
                backgroundModDownloadQueueTask = null;
            }
        }
    }

    [RelayCommand]
    private void PauseModDownload(ModDownloadQueueRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        if (!modDownloader.PauseQueueItem(row.QueueKey))
        {
            return;
        }

        SyncDownloadQueueRows(modDownloader.Queue);
        NexusStatusText = $"{row.FileName}: {PauseText}";
        StatusText = NexusStatusText;
        LastActionText = StatusText;
    }

    [RelayCommand]
    private void ResumeModDownload(ModDownloadQueueRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        if (!modDownloader.ResumeQueueItem(row.QueueKey))
        {
            return;
        }

        SyncDownloadQueueRows(modDownloader.Queue);
        NexusStatusText = $"{row.FileName}: {ResumeText}";
        StatusText = NexusStatusText;
        LastActionText = StatusText;

        StartModDownloadQueueInBackground();
    }

    [RelayCommand]
    private void CancelModDownload(ModDownloadQueueRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        if (!modDownloader.CancelQueueItem(row.QueueKey))
        {
            return;
        }

        dismissedCanceledDownloadKeys.Add(row.QueueKey);
        if (ReferenceEquals(SelectedDownloadQueueItem, row))
        {
            SelectedDownloadQueueItem = null;
        }

        if (!IsDownloadingMods)
        {
            modDownloader.ClearCanceledQueueItems();
        }

        SyncDownloadQueueRows(modDownloader.Queue);
        NexusStatusText = $"{row.FileName}: {CancelText}";
        StatusText = NexusStatusText;
        LastActionText = StatusText;
    }

    [RelayCommand]
    private void RetryModDownload(ModDownloadQueueRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        if (!modDownloader.RetryQueueItem(row.QueueKey))
        {
            return;
        }

        SyncDownloadQueueRows(modDownloader.Queue);
        NexusStatusText = $"{row.FileName}: {RetryText}";
        StatusText = NexusStatusText;
        LastActionText = StatusText;

        StartModDownloadQueueInBackground();
    }

    [RelayCommand]
    private void OpenModDownloadFolder(ModDownloadQueueRowViewModel? row)
    {
        if (row is null || string.IsNullOrWhiteSpace(row.DestinationDirectory))
        {
            return;
        }

        NexusStatusText = TryOpenShellTarget(row.DestinationDirectory)
            ? string.Format(T("OpenedDownloadFolder"), row.FileName)
            : string.Format(T("OpenDownloadFolderFailed"), row.FileName);
        StatusText = NexusStatusText;
        LastActionText = StatusText;
    }

    [RelayCommand]
    private void OpenModDownloadDirectory()
    {
        var downloadDirectory = GetModDownloadDirectory();
        Directory.CreateDirectory(downloadDirectory);
        NexusStatusText = TryOpenShellTarget(downloadDirectory)
            ? T("OpenedDownloadDirectory")
            : string.Format(T("OpenDownloadDirectoryFailed"), downloadDirectory);
        StatusText = NexusStatusText;
        LastActionText = StatusText;
    }

    [RelayCommand]
    private void PauseModDownloadQueue()
    {
        var changed = modDownloader.PauseQueue();
        SyncDownloadQueueRows(modDownloader.Queue);
        NexusStatusText = string.Format(T("ModDownloadQueuePaused"), changed);
        StatusText = NexusStatusText;
        LastActionText = StatusText;
    }

    [RelayCommand]
    private async Task ResumeModDownloadQueueAsync()
    {
        var changed = modDownloader.ResumeQueue();
        SyncDownloadQueueRows(modDownloader.Queue);
        NexusStatusText = string.Format(T("ModDownloadQueueResumed"), changed);
        StatusText = NexusStatusText;
        LastActionText = StatusText;

        if (changed > 0)
        {
            await StartModDownloadQueueAsync();
        }
    }

    [RelayCommand]
    private void CancelModDownloadQueue()
    {
        var canceledKeys = ModDownloadQueueRows
            .Where(row => row.Status is not ModDownloadStatus.Completed and not ModDownloadStatus.Canceled)
            .Select(row => row.QueueKey)
            .ToArray();
        var changed = modDownloader.CancelQueue();
        foreach (var queueKey in canceledKeys)
        {
            dismissedCanceledDownloadKeys.Add(queueKey);
        }

        if (!IsDownloadingMods)
        {
            modDownloader.ClearCanceledQueueItems();
        }

        SyncDownloadQueueRows(modDownloader.Queue);
        NexusStatusText = string.Format(T("ModDownloadQueueCanceled"), changed);
        StatusText = NexusStatusText;
        LastActionText = StatusText;
    }

    [RelayCommand]
    private void ClearModDownloadQueue()
    {
        var removed = modDownloader.ClearInactiveQueueItems();
        SyncDownloadQueueRows(modDownloader.Queue);
        NexusStatusText = string.Format(T("ModDownloadQueueCleared"), removed);
        StatusText = NexusStatusText;
        LastActionText = StatusText;
    }

    private async Task<bool> EnsureNexusAccountBoundAsync()
    {
        var isBound = await PlayerProfiles.EnsureNexusAccountBoundAsync();
        if (isBound)
        {
            _ = TryLoadDailyModRecommendationAsync();
        }

        return isBound;
    }

    private async Task<bool> EnsureNexusAccountBoundForDownloadAsync(NexusModSearchRowViewModel mod)
    {
        await PlayerProfiles.RefreshNexusAccountAsync();
        if (PlayerProfiles.HasNexusBinding)
        {
            return true;
        }

        pendingNexusModDownload = mod;
        if (!IsModDownloadPageVisible)
        {
            NavigateCore("Mods", silent: false);
            SelectModManagerPage("Download");
        }

        OpenNexusAccountBindingDialog();
        return false;
    }

    [RelayCommand]
    private async Task InstallLocalModPackageAsync()
    {
        if (IsShuttingDown)
        {
            return;
        }

        var packagePath = await gamePathPickerService.PickModPackageAsync();
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            return;
        }

        var modsDirectory = applicationPathService.GetPaths().ModsDirectory;
        var displayName = Path.GetFileNameWithoutExtension(packagePath);
        var result = await modPackageInstaller.InstallAsync(new ModPackageInstallRequest(
            packagePath,
            modsDirectory,
            displayName,
            displayName,
            "local"), shutdownCancellation.Token);

        StatusText = result.Message;
        NexusStatusText = result.Message;
        LastActionText = StatusText;

        if (result.Success)
        {
            await RefreshModsAsync();
        }
    }

    private async Task<int> InstallCompletedDownloadsAsync(IReadOnlyList<ModDownloadResult> results)
    {
        var installed = 0;
        var modsDirectory = applicationPathService.GetPaths().ModsDirectory;

        foreach (var result in results.Where(result => result.Success && !string.IsNullOrWhiteSpace(result.FinalPath)))
        {
            var request = result.Request;
            var metadata = await ResolveNexusModInstallMetadataAsync(request);
            var installResult = await modPackageInstaller.InstallAsync(new ModPackageInstallRequest(
                result.FinalPath!,
                modsDirectory,
                $"nexus-{request.ModId}",
                metadata.DisplayName,
                metadata.Version), shutdownCancellation.Token);

            ApplyInstallResultToQueueRow(request, installResult);
            if (installResult.Success)
            {
                installed++;
            }
        }

        if (installed > 0)
        {
            await RefreshModsAsync();
        }

        RefreshModSearchDownloadStates();
        return installed;
    }

    private async Task<(string DisplayName, string Version)> ResolveNexusModInstallMetadataAsync(
        NexusModDownloadRequest request)
    {
        var fallbackName = Path.GetFileNameWithoutExtension(request.FileName);
        var source = FindModSearchRow(request.ModId);
        if (source is not null)
        {
            return (source.Name, source.Version);
        }

        var queueKey = $"{request.GameDomainName}:{request.ModId}:{request.FileId}";
        var queueRow = ModDownloadQueueRows.FirstOrDefault(row =>
            string.Equals(row.QueueKey, queueKey, StringComparison.OrdinalIgnoreCase));
        if (queueRow is not null
            && !string.IsNullOrWhiteSpace(queueRow.ModName)
            && !string.Equals(queueRow.ModName, $"Mod {request.ModId}", StringComparison.OrdinalIgnoreCase))
        {
            return (queueRow.ModName, "nexus");
        }

        try
        {
            var details = await nexusModService.GetModDetailsAsync(request.GameDomainName, request.ModId);
            return (
                string.IsNullOrWhiteSpace(details.Name) ? fallbackName : details.Name,
                string.IsNullOrWhiteSpace(details.Version) ? "nexus" : details.Version);
        }
        catch (Exception ex) when (ex is NexusModsException or HttpRequestException or IOException or InvalidOperationException)
        {
            return (fallbackName, "nexus");
        }
    }

    private void ApplyInstallResultToQueueRow(
        NexusModDownloadRequest request,
        ModPackageInstallResult installResult)
    {
        var queueKey = $"{request.GameDomainName}:{request.ModId}:{request.FileId}";
        var row = ModDownloadQueueRows.FirstOrDefault(row => string.Equals(row.QueueKey, queueKey, StringComparison.OrdinalIgnoreCase));
        if (row is null)
        {
            return;
        }

        row.ApplyInstallResult(installResult);
    }
}
