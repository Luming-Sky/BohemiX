using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using BohemiX.App.Models;
using BohemiX.Core.Models;
using BohemiX.Core.Services;
using CommunityToolkit.Mvvm.Input;

namespace BohemiX.App.ViewModels;

public partial class MainWindowViewModel
{

    [RelayCommand]
    private async Task DiscoverGamesAsync(
        GameDiscoverySearchMode searchMode = GameDiscoverySearchMode.Fast,
        CancellationToken cancellationToken = default)
    {
        try
        {
            StatusText = T("ScanningKcd2");
            var games = await gameDiscoveryService.DiscoverInstalledGamesAsync(searchMode, cancellationToken);
            ApplyDiscoveredGames(games);
            Navigate("Install");

            StatusText = selectedGame is null
                ? T("NoVerifiedInstallFound")
                : string.Format(T("Kcd2InstallVerifiedFrom"), SourceText);
            await RefreshTrackerModHealthAsync();
            LastActionText = StatusText;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            StatusText = string.Format(T("DiscoveryFailed"), ex.Message);
            LastActionText = StatusText;
        }
    }

    [RelayCommand]
    private void OpenVerificationDialog()
    {
        Navigate("Install");
        IsInstallVerificationDialogOpen = true;
        IsOwnedGameOptionsVisible = false;
        IsDeepGameSearchAvailable = false;
        VerificationDialogMessage = T("VerificationDialogQuestion");
    }

    [RelayCommand]
    private void CloseInstallVerificationDialog()
    {
        gameDiscoveryCancellation?.Cancel();
        IsInstallVerificationDialogOpen = false;
    }

    [RelayCommand]
    private async Task OpenSteamPurchaseAsync()
    {
        await externalStoreService.OpenKcd2SteamPageAsync();
        VerificationDialogMessage = T("OpenedStore");
        StatusText = T("OpenedStore");
        LastActionText = StatusText;
    }

    [RelayCommand]
    private void ShowOwnedGameOptions()
    {
        IsOwnedGameOptionsVisible = true;
        VerificationDialogMessage = T("ChooseVerificationMethod");
    }

    [RelayCommand]
    private async Task AutoSearchFromDialogAsync()
    {
        await SearchForGameFromDialogAsync(GameDiscoverySearchMode.Fast);
    }

    [RelayCommand]
    private async Task DeepSearchFromDialogAsync()
    {
        await SearchForGameFromDialogAsync(GameDiscoverySearchMode.Deep);
    }

    private async Task SearchForGameFromDialogAsync(GameDiscoverySearchMode searchMode)
    {
        if (!IsAutoSearchButtonEnabled)
        {
            return;
        }

        gameDiscoveryCancellation?.Cancel();
        gameDiscoveryCancellation?.Dispose();
        gameDiscoveryCancellation = new CancellationTokenSource();
        var cancellation = gameDiscoveryCancellation;

        IsAutoSearchButtonEnabled = false;
        IsDeepGameSearchAvailable = false;
        AutoSearchButtonText = T("Searching");
        VerificationDialogMessage = searchMode == GameDiscoverySearchMode.Deep
            ? T("SearchingKcd2DeepInstallDirectory")
            : T("SearchingKcd2InstallDirectory");

        try
        {
            await DiscoverGamesAsync(searchMode, cancellation.Token);
            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            var isVerified = selectedGame is { IsVerified: true };
            VerificationDialogMessage = isVerified
                ? string.Format(T("VerifiedInstallPath"), selectedGame!.ExecutablePath)
                : searchMode == GameDiscoverySearchMode.Fast
                    ? T("FastSearchNoVerifiedInstallFound")
                    : T("NoVerifiedInstallFound");
            IsDeepGameSearchAvailable = !isVerified && searchMode == GameDiscoverySearchMode.Fast;
            IsInstallVerificationDialogOpen = !isVerified;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(gameDiscoveryCancellation, cancellation))
            {
                gameDiscoveryCancellation = null;
                cancellation.Dispose();
                AutoSearchButtonText = T("AutoSearch");
                IsAutoSearchButtonEnabled = true;
            }
        }
    }

    [RelayCommand]
    private async Task BrowseGameExecutableAsync()
    {
        var selectedPath = await gamePathPickerService.PickGameExecutableAsync();
        if (!string.IsNullOrWhiteSpace(selectedPath))
        {
            ManualGamePath = selectedPath;
            VerificationDialogMessage = string.Format(T("SelectedManualPath"), selectedPath);
        }
    }

    [RelayCommand]
    private async Task BrowseGameDirectoryAsync()
    {
        var selectedPath = await gamePathPickerService.PickGameDirectoryAsync();
        if (!string.IsNullOrWhiteSpace(selectedPath))
        {
            ManualGamePath = selectedPath;
            VerificationDialogMessage = string.Format(T("SelectedManualPath"), selectedPath);
        }
    }

    private bool CanVerifyManualGamePath() => !string.IsNullOrWhiteSpace(ManualGamePath);

    [RelayCommand(CanExecute = nameof(CanVerifyManualGamePath))]
    private async Task VerifyManualGamePathAsync()
    {
        try
        {
            var game = await gameDiscoveryService.VerifyManualPathAsync(ManualGamePath);
            if (game is null)
            {
                VerificationDialogMessage = T("ManualVerificationNoGame");
                return;
            }

            ApplyDiscoveredGames(await gameInstallationStore.LoadAsync());
            await RefreshTrackerModHealthAsync();
            VerificationDialogMessage = string.Format(T("VerifiedInstallPath"), game.ExecutablePath);
            StatusText = T("Kcd2InstallVerifiedFrom").Contains("{0}", StringComparison.Ordinal)
                ? string.Format(T("Kcd2InstallVerifiedFrom"), game.Source)
                : VerificationDialogMessage;
            LastActionText = StatusText;
            IsInstallVerificationDialogOpen = false;
        }
        catch (Exception ex)
        {
            VerificationDialogMessage = string.Format(T("ManualVerificationFailed"), ex.Message);
            LastActionText = VerificationDialogMessage;
        }
    }

    [RelayCommand]
    private Task LaunchGameAsync() => LaunchGameCoreAsync();

    private async Task<bool> LaunchGameCoreAsync()
    {
        try
        {
            if (selectedGame is null || !selectedGame.IsVerified)
            {
                StatusText = T("NoExecutableCached");
                var games = await gameDiscoveryService.DiscoverInstalledGamesAsync();
                ApplyDiscoveredGames(games);
            }

            if (selectedGame is null || !selectedGame.IsVerified)
            {
                Navigate("Install");
                StatusText = T("LaunchUnavailable");
                LastActionText = StatusText;
                return false;
            }

            if (!await EnsureTrackerInstalledForLaunchAsync())
            {
                return false;
            }

            if (!await PrepareModsForLaunchAsync())
            {
                return false;
            }

            StatusText = T("LaunchingGame");
            var result = await gameLauncherService.LaunchAsync(
                selectedGame,
                new GameLaunchOptions(LaunchArguments, UseSteamProtocol));
            LastPlayedText = result.IsStarted && result.ProcessId is not null
                ? string.Format(T("ProcessLabel"), result.ProcessId)
                : T("LaunchFailedShort");
            StatusText = result.IsStarted
                ? string.Format(T("LaunchRequested"), LastPlayedText)
                : string.Format(T("LaunchFailedWithMessage"), result.Message);
            LastActionText = StatusText;
            if (result is { IsStarted: true, ProcessId: not null })
            {
                gameRuntimeMonitorService.Start(new GameRuntimeMonitorRequest(
                    result.ProcessId.Value,
                    result.SessionId,
                    selectedGame.ExecutablePath));
            }
            else if (!result.IsStarted)
            {
                await applicationErrorReporter.ReportAsync(ApplicationErrorReport.GameLaunchFailure(
                    result.Message,
                    selectedGame.ExecutablePath,
                    "创建游戏进程",
                    $"Launch result: {result.Message}\nSession: {result.SessionId?.ToString("D") ?? "not created"}",
                    applicationPathService.GetPaths().LogsDirectory,
                    result.SessionId));
            }
            if (result.IsStarted)
            {
                RequestLaunchWindowAction();
            }
            await RefreshTrackerModHealthAsync();
            return result.IsStarted;
        }
        catch (Exception ex)
        {
            StatusText = string.Format(T("LaunchFailedWithMessage"), ex.Message);
            LastActionText = StatusText;
            var executablePath = selectedGame?.ExecutablePath ?? "尚未找到游戏启动文件";
            await applicationErrorReporter.ReportAsync(ApplicationErrorReport.FromException(
                ex,
                "游戏启动失败",
                ex.Message,
                "Kingdom Come: Deliverance II 启动",
                ApplicationErrorCategory.GameLaunch,
                $"启动游戏时：正在准备并打开游戏\n游戏启动文件：{executablePath}",
                applicationPathService.GetPaths().LogsDirectory));
            return false;
        }
    }

    private async Task<bool> EnsureTrackerInstalledForLaunchAsync()
    {
        if (selectedGame is null)
        {
            return false;
        }

        var result = await trackerModInstallService.InstallAsync(selectedGame);
        TrackerModInstallStatusText = result.Message;
        if (result.IsInstalled)
        {
            await RefreshTrackerModHealthAsync();
            return true;
        }

        Navigate("Mods");
        StatusText = string.Format(T("TrackerInstallRequiredForLaunchFailed"), result.Message);
        LastActionText = StatusText;
        return false;
    }

    private async Task<bool> PrepareModsForLaunchAsync()
    {
        if (selectedGame is null)
        {
            return false;
        }

        var preflight = await modLaunchPreflightService.EvaluateAsync(new ModLaunchPreflightOptions(
            selectedGame.ExecutablePath,
            CheckModConflictsBeforeLaunch,
            EnableVfsBeforeLaunch));
        ApplyModSnapshot(preflight.Snapshot);

        if (!preflight.CanLaunch)
        {
            Navigate("Mods");
            Sections[2].Detail = string.Format(T("LaunchBlockedWithMessage"), preflight.Message);
            StatusText = Sections[2].Detail;
            LastActionText = StatusText;
            return false;
        }

        if (preflight.MountRequest is null)
        {
            if (vfsSessionService.CurrentState != VfsSessionState.Idle)
            {
                await vfsSessionService.UnmountAsync();
                VfsStateText = LocalizeVfsState(vfsSessionService.CurrentState);
            }
        }
        else
        {
            await vfsSessionService.MountAsync(preflight.MountRequest);
            VfsStateText = LocalizeVfsState(vfsSessionService.CurrentState);

            if (vfsSessionService.CurrentState != VfsSessionState.Mounted)
            {
                StatusText = T("VfsUnavailableLaunchBlocked");
                LastActionText = StatusText;
                var vfsError = vfsSessionService.LastError;
                var report = vfsError is not null
                    ? ApplicationErrorReport.FromException(
                        vfsError,
                        "模组加载失败",
                        vfsError.Message,
                        "BohemiX 模组加载",
                        ApplicationErrorCategory.VirtualFileSystem,
                        $"准备模组时\n游戏启动文件：{selectedGame.ExecutablePath}",
                        applicationPathService.GetPaths().LogsDirectory)
                    : new ApplicationErrorReport(
                        ApplicationErrorCategory.VirtualFileSystem,
                        "模组加载失败",
                        "无法让已启用的模组在游戏中生效，因此已停止启动游戏。",
                        $"准备模组时\n游戏启动文件：{selectedGame.ExecutablePath}",
                        "BohemiX 模组加载",
                        "The mod-loading step failed without a detailed error. See the application log for the underlying system failure.",
                        DateTimeOffset.UtcNow,
                        applicationPathService.GetPaths().LogsDirectory);
                await applicationErrorReporter.ReportAsync(report);
                return false;
            }
        }

        await RefreshTrackerModHealthAsync();
        return true;
    }

    private void OnGameRuntimeExited(GameRuntimeExitUpdate update)
    {
        _ = Dispatcher.UIThread.InvokeAsync(async () =>
        {
            VfsStateText = LocalizeVfsState(update.VfsState);
            LastPlayedText = T(update.ExitResult.IsAbnormalExit ? "GameExitedWithError" : "GameExited");
            StatusText = update.ExitResult.IsAbnormalExit
                ? string.Format(T("GameExitedWithErrorStatus"), update.ExitResult.ExitCode)
                : T("GameExitedStatus");
            LastActionText = StatusText;
            await RefreshTrackerModHealthAsync();
            await SaveManager.HandleGameExitAsync(update.ExitThumbnail);
        });
    }


}
