using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BohemiX.App.PlayerProfiles;
using BohemiX.Core.Models;
using BohemiX.Core.PlayerProfiles;
using BohemiX.Core.Services;
using CommunityToolkit.Mvvm.Input;

namespace BohemiX.App.ViewModels;

public partial class MainWindowViewModel
{
    private async Task HandleSelectedModDownloadSourceChangedAsync(ModDownloadSourceOptionViewModel? value)
    {
        if (value is null)
        {
            return;
        }

        if (value.IsNexus)
        {
            await ShowNexusAccountBindingDialogAsync();
            return;
        }

        if (!value.IsSteamWorkshop)
        {
            pendingNexusModDownload = null;
            approvedModDownloadSourceKey = value.Key;
            CloseNexusAccountBindingDialog(restoreApprovedSource: false);
            CloseSteamAccountBindingDialog(restoreApprovedSource: false);
            SelectedModDownloadCategory = GetModDownloadCategoryDisplay("All");
            RefreshModDownloadCategoryFilters();
            ApplySelectedModDownloadSourceChange();
            return;
        }

        await ShowSteamAccountBindingDialogAsync();
    }

    private async Task ShowNexusAccountBindingDialogAsync()
    {
        if (!IsModDownloadPageVisible)
        {
            return;
        }

        if (IsNexusAccountBindingDialogOpen)
        {
            return;
        }

        await PlayerProfiles.RefreshNexusAccountAsync();
        if (PlayerProfiles.HasNexusBinding)
        {
            ApproveNexusModDownloadSource();
            return;
        }

        OpenNexusAccountBindingDialog();
    }

    private void OpenNexusAccountBindingDialog(string? statusOverride = null)
    {
        if (!IsModDownloadPageVisible)
        {
            return;
        }

        NexusAccountBindingDialogStatusText = statusOverride
            ?? (pendingNexusModDownload is null
                ? "当前尚未绑定 Nexus Mods 账号。登录并绑定后才能使用 Nexus 下载源。"
                : $"下载“{pendingNexusModDownload.Name}”前，需要先登录并绑定 Nexus Mods 账号。绑定成功后将自动继续。");

        if (IsNexusAccountBindingDialogOpen)
        {
            return;
        }

        CloseSteamAccountBindingDialog(restoreApprovedSource: false);
        nexusAccountBindingDialogRequestId++;
        NexusAccountBindingDialogApiKey = string.Empty;
        IsNexusAccountBindingDialogOpen = true;
        IsNexusAccountBindingDialogBusy = false;
        RefreshCanSearchModDownloads();
        ConfirmNexusAccountBindingCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanConfirmNexusAccountBinding))]
    private async Task ConfirmNexusAccountBindingAsync()
    {
        var requestId = nexusAccountBindingDialogRequestId;
        IsNexusAccountBindingDialogBusy = true;
        NexusAccountBindingDialogStatusText = "正在打开 Nexus Mods 授权页面，完成授权后会自动继续。";
        RefreshCanSearchModDownloads();
        ConfirmNexusAccountBindingCommand.NotifyCanExecuteChanged();
        try
        {
            var isBound = await PlayerProfiles.EnsureNexusAccountBoundAsync();
            if (requestId != nexusAccountBindingDialogRequestId || !IsNexusAccountBindingDialogOpen)
            {
                return;
            }

            if (!isBound)
            {
                NexusAccountBindingDialogStatusText = PlayerProfiles.NexusBindingMessage;
                return;
            }

            await CompleteNexusAccountBindingAsync();
        }
        catch (Exception ex)
        {
            NexusAccountBindingDialogStatusText = $"Nexus 账号绑定失败：{ex.Message}";
        }
        finally
        {
            IsNexusAccountBindingDialogBusy = false;
            RefreshCanSearchModDownloads();
            ConfirmNexusAccountBindingCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanBindNexusAccountWithApiKey))]
    private async Task BindNexusAccountWithApiKeyAsync()
    {
        var requestId = nexusAccountBindingDialogRequestId;
        var apiKey = NexusAccountBindingDialogApiKey.Trim();
        IsNexusAccountBindingDialogBusy = true;
        NexusAccountBindingDialogStatusText = "正在检查 Nexus 个人密钥...";
        RefreshCanSearchModDownloads();
        ConfirmNexusAccountBindingCommand.NotifyCanExecuteChanged();
        BindNexusAccountWithApiKeyCommand.NotifyCanExecuteChanged();
        try
        {
            var isBound = await PlayerProfiles.BindNexusAccountWithApiKeyAsync(apiKey);
            if (requestId != nexusAccountBindingDialogRequestId || !IsNexusAccountBindingDialogOpen)
            {
                return;
            }

            if (!isBound)
            {
                NexusAccountBindingDialogStatusText = PlayerProfiles.NexusBindingMessage;
                return;
            }

            await CompleteNexusAccountBindingAsync();
        }
        catch (Exception ex)
        {
            NexusAccountBindingDialogStatusText = $"Nexus 个人密钥无法使用：{ex.Message}";
        }
        finally
        {
            apiKey = string.Empty;
            IsNexusAccountBindingDialogBusy = false;
            RefreshCanSearchModDownloads();
            ConfirmNexusAccountBindingCommand.NotifyCanExecuteChanged();
            BindNexusAccountWithApiKeyCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private void CancelNexusAccountBinding()
    {
        PlayerProfiles.CancelNexusAccountBindingRequest();
        pendingNexusModDownload = null;
        if (IsNexusApiKeyRequiredForPendingAction)
        {
            pendingNexusModPackInstall = null;
        }

        CloseNexusAccountBindingDialog(restoreApprovedSource: true);
        StatusText = "已取消 Nexus 账号绑定。";
        LastActionText = StatusText;
    }

    private void ApproveNexusModDownloadSource()
    {
        approvedModDownloadSourceKey = ModDownloadSourceOptionViewModel.NexusKey;
        CloseNexusAccountBindingDialog(restoreApprovedSource: false);
        SelectedModDownloadCategory = GetModDownloadCategoryDisplay("All");
        RefreshModDownloadCategoryFilters();
        ApplySelectedModDownloadSourceChange();
    }

    private async Task CompleteNexusAccountBindingAsync()
    {
        var modPackToPrepare = IsNexusApiKeyRequiredForPendingAction
            ? pendingNexusModPackInstall
            : null;
        if (modPackToPrepare is not null)
        {
            pendingNexusModDownload = null;
            CloseNexusAccountBindingDialog(restoreApprovedSource: false);
            await PrepareNexusModPackInstallAsync(modPackToPrepare);
            return;
        }

        var modToDownload = pendingNexusModDownload;
        pendingNexusModDownload = null;
        ApproveNexusModDownloadSource();

        if (modToDownload is not null)
        {
            await DownloadNexusModAsync(modToDownload);
        }
    }

    private void CloseNexusAccountBindingDialog(bool restoreApprovedSource)
    {
        nexusAccountBindingDialogRequestId++;
        IsNexusAccountBindingDialogOpen = false;
        IsNexusAccountBindingDialogBusy = false;
        IsNexusApiKeyRequiredForPendingAction = false;
        NexusAccountBindingDialogApiKey = string.Empty;
        RefreshCanSearchModDownloads();
        ConfirmNexusAccountBindingCommand.NotifyCanExecuteChanged();
        BindNexusAccountWithApiKeyCommand.NotifyCanExecuteChanged();

        if (!restoreApprovedSource)
        {
            return;
        }

        var approvedSource = ModDownloadSources.FirstOrDefault(source =>
            string.Equals(source.Key, approvedModDownloadSourceKey, StringComparison.Ordinal))
            ?? ModDownloadSources.First();
        if (ReferenceEquals(SelectedModDownloadSource, approvedSource))
        {
            return;
        }

        suppressSelectedModDownloadSourceChanged = true;
        try
        {
            SelectedModDownloadSource = approvedSource;
        }
        finally
        {
            suppressSelectedModDownloadSourceChanged = false;
        }

        ApplySelectedModDownloadSourceChange();
    }

    private void ApplySelectedModDownloadSourceChange()
    {
        RefreshModDownloadSourceUiText();
        InvalidateModDownloadSearch(clearResults: true);
        ResetModSearchPagination();
        ScheduleModSearchAutoSearch();
    }

    private async Task ShowSteamAccountBindingDialogAsync()
    {
        steamAccountBindingDetectionCancellation?.Cancel();
        steamAccountBindingDetectionCancellation?.Dispose();
        var detectionCancellation = new CancellationTokenSource();
        steamAccountBindingDetectionCancellation = detectionCancellation;

        steamAccountBindingDialogRequestId++;
        var requestId = steamAccountBindingDialogRequestId;
        pendingSteamAccountIdentity = null;
        pendingPlayerSteamAccountBinding = null;
        pendingSteamAccountBindingConflict = null;
        pendingSteamBindingConflictPlayerName = null;
        pendingSteamBindingPlayerProfile = PlayerProfiles.CurrentPlayer?.Profile;

        IsSteamAccountBindingDialogOpen = true;
        IsSteamAccountBindingBusy = true;
        steamAccountBindingDialogState = SteamAccountBindingDialogState.Detecting;
        RefreshSteamAccountBindingDialogText();
        RefreshCanSearchModDownloads();

        if (pendingSteamBindingPlayerProfile is null)
        {
            steamAccountBindingDetectionCancellation = null;
            detectionCancellation.Dispose();
            IsSteamAccountBindingBusy = false;
            steamAccountBindingDialogState = SteamAccountBindingDialogState.NoCurrentPlayer;
            RefreshSteamAccountBindingDialogText();
            return;
        }

        var bindingPlayerProfile = pendingSteamBindingPlayerProfile;
        var progress = new SteamAccountDetectionProgressGate(value =>
        {
            if (requestId != steamAccountBindingDialogRequestId
                || detectionCancellation.IsCancellationRequested
                || !IsSteamAccountBindingDialogOpen)
            {
                return;
            }

            steamAccountBindingDialogState = value.Stage switch
            {
                SteamAccountDetectionStage.StartingClient => SteamAccountBindingDialogState.StartingSteam,
                SteamAccountDetectionStage.WaitingForLogin => SteamAccountBindingDialogState.WaitingForSteamLogin,
                SteamAccountDetectionStage.ValidatingAccount => SteamAccountBindingDialogState.ValidatingSteamAccount,
                _ => SteamAccountBindingDialogState.Detecting
            };
            RefreshSteamAccountBindingDialogText();
        });

        try
        {
            SteamAccountIdentity identity;
            try
            {
                identity = await workshopService
                    .GetCurrentSteamAccountAsync(progress, detectionCancellation.Token)
                    .ConfigureAwait(true);
            }
            finally
            {
                progress.Complete();
            }

            var playerBinding = await playerSteamAccountBindingService
                .GetBindingByPlayerIdAsync(bindingPlayerProfile.Id, detectionCancellation.Token)
                .ConfigureAwait(true);
            var steamBinding = await playerSteamAccountBindingService
                .GetBindingBySteamIdAsync(identity.SteamId, detectionCancellation.Token)
                .ConfigureAwait(true);

            if (requestId != steamAccountBindingDialogRequestId || !IsSteamAccountBindingDialogOpen)
            {
                return;
            }

            pendingSteamAccountIdentity = identity;
            pendingPlayerSteamAccountBinding = playerBinding;
            pendingSteamAccountBindingConflict = steamBinding;
            pendingSteamBindingConflictPlayerName = steamBinding is not null && steamBinding.PlayerId != bindingPlayerProfile.Id
                ? PlayerProfiles.Players.FirstOrDefault(player => player.Id == steamBinding.PlayerId)?.DisplayName
                : null;

            steamAccountBindingDialogState =
                !identity.OwnsKcd2
                    ? SteamAccountBindingDialogState.GameNotOwned
                    : playerBinding is not null && playerBinding.SteamId != identity.SteamId
                        ? SteamAccountBindingDialogState.PlayerBoundToDifferentSteamAccount
                        : steamBinding is not null && steamBinding.PlayerId != bindingPlayerProfile.Id
                            ? SteamAccountBindingDialogState.SteamAccountBoundToDifferentPlayer
                            : playerBinding is not null
                                ? SteamAccountBindingDialogState.AlreadyBound
                                : SteamAccountBindingDialogState.ReadyToBind;
            RefreshSteamAccountBindingDialogText();
        }
        catch (OperationCanceledException) when (detectionCancellation.IsCancellationRequested)
        {
            return;
        }
        catch (WorkshopException ex)
        {
            if (requestId != steamAccountBindingDialogRequestId || !IsSteamAccountBindingDialogOpen)
            {
                return;
            }

            steamAccountBindingDialogState = ex.FailureKind switch
            {
                WorkshopFailureKind.NotLoggedIn => SteamAccountBindingDialogState.LoginRequired,
                WorkshopFailureKind.GameNotOwned => SteamAccountBindingDialogState.GameNotOwned,
                WorkshopFailureKind.SteamUnavailable => SteamAccountBindingDialogState.SteamUnavailable,
                _ => SteamAccountBindingDialogState.Failed
            };
            SteamAccountBindingStatusText = FormatWorkshopError(ex);
            RefreshSteamAccountBindingDialogText(preserveStatusText: true);
        }
        catch (Exception ex)
        {
            if (requestId != steamAccountBindingDialogRequestId || !IsSteamAccountBindingDialogOpen)
            {
                return;
            }

            steamAccountBindingDialogState = SteamAccountBindingDialogState.Failed;
            SteamAccountBindingStatusText = ex.Message;
            RefreshSteamAccountBindingDialogText(preserveStatusText: true);
        }
        finally
        {
            if (requestId == steamAccountBindingDialogRequestId)
            {
                if (ReferenceEquals(steamAccountBindingDetectionCancellation, detectionCancellation))
                {
                    steamAccountBindingDetectionCancellation = null;
                    detectionCancellation.Dispose();
                }

                IsSteamAccountBindingBusy = false;
                RefreshCanSearchModDownloads();
                ConfirmSteamAccountBindingCommand.NotifyCanExecuteChanged();
            }
        }
    }

    private void RefreshSteamAccountBindingDialogText(bool preserveStatusText = false)
    {
        var offlinePlayer = pendingSteamBindingPlayerProfile;
        var steamIdentity = pendingSteamAccountIdentity;
        SteamAccountBindingDialogTitleText = T("SteamAccountBindingTitle");
        SteamAccountBindingOfflineAccountLabelText = T("SteamAccountBindingOfflineLabel");
        SteamAccountBindingSteamAccountLabelText = T("SteamAccountBindingSteamLabel");
        SteamAccountBindingOfflineAccountNameText = offlinePlayer?.DisplayName ?? T("SteamAccountBindingUnknownOffline");
        SteamAccountBindingSteamAccountNameText = steamIdentity?.PersonaName ?? T("SteamAccountBindingDetectingSteam");
        SteamAccountBindingSteamAccountIdText = steamIdentity is null
            ? string.Empty
            : string.Format(T("SteamAccountBindingSteamId"), steamIdentity.SteamId);
        SteamAccountBindingCancelText = T("SteamAccountBindingCancel");

        if (!preserveStatusText)
        {
            SteamAccountBindingStatusText = steamAccountBindingDialogState switch
            {
                SteamAccountBindingDialogState.Detecting => T("SteamAccountBindingDetectingStatus"),
                SteamAccountBindingDialogState.StartingSteam => T("SteamAccountBindingStartingStatus"),
                SteamAccountBindingDialogState.WaitingForSteamLogin => T("SteamAccountBindingWaitingLoginStatus"),
                SteamAccountBindingDialogState.ValidatingSteamAccount => T("SteamAccountBindingValidatingStatus"),
                SteamAccountBindingDialogState.ReadyToBind => T("SteamAccountBindingReadyStatus"),
                SteamAccountBindingDialogState.AlreadyBound => string.Format(
                    T("SteamAccountBindingAlreadyBoundStatus"),
                    pendingPlayerSteamAccountBinding?.PersonaName ?? steamIdentity?.PersonaName ?? T("SteamAccountBindingUnknownSteam")),
                SteamAccountBindingDialogState.PlayerBoundToDifferentSteamAccount => string.Format(
                    T("SteamAccountBindingPlayerConflictStatus"),
                    pendingPlayerSteamAccountBinding?.PersonaName ?? T("SteamAccountBindingUnknownSteam"),
                    pendingPlayerSteamAccountBinding?.SteamId ?? 0UL),
                SteamAccountBindingDialogState.SteamAccountBoundToDifferentPlayer => string.Format(
                    T("SteamAccountBindingSteamConflictStatus"),
                    pendingSteamBindingConflictPlayerName ?? T("SteamAccountBindingUnknownOffline")),
                SteamAccountBindingDialogState.NoCurrentPlayer => T("SteamAccountBindingNoCurrentPlayer"),
                SteamAccountBindingDialogState.SteamUnavailable => T("SteamAccountBindingSteamUnavailable"),
                SteamAccountBindingDialogState.LoginRequired => T("SteamAccountBindingLoginRequired"),
                SteamAccountBindingDialogState.GameNotOwned => T("SteamAccountBindingGameNotOwned"),
                SteamAccountBindingDialogState.Failed => T("SteamAccountBindingGenericFailure"),
                _ => string.Empty
            };
        }

        SteamAccountBindingDialogDescriptionText = steamAccountBindingDialogState switch
        {
            SteamAccountBindingDialogState.AlreadyBound => T("SteamAccountBindingAlreadyBoundDescription"),
            SteamAccountBindingDialogState.PlayerBoundToDifferentSteamAccount => T("SteamAccountBindingPlayerConflictDescription"),
            SteamAccountBindingDialogState.SteamAccountBoundToDifferentPlayer => T("SteamAccountBindingSteamConflictDescription"),
            SteamAccountBindingDialogState.GameNotOwned => T("SteamAccountBindingGameNotOwnedDescription"),
            SteamAccountBindingDialogState.LoginRequired => T("SteamAccountBindingLoginRequiredDescription"),
            SteamAccountBindingDialogState.SteamUnavailable => T("SteamAccountBindingSteamUnavailableDescription"),
            SteamAccountBindingDialogState.NoCurrentPlayer => T("SteamAccountBindingNoCurrentPlayerDescription"),
            SteamAccountBindingDialogState.Failed => T("SteamAccountBindingFailureDescription"),
            _ => T("SteamAccountBindingDescription")
        };

        SteamAccountBindingConfirmText = steamAccountBindingDialogState switch
        {
            SteamAccountBindingDialogState.AlreadyBound => T("SteamAccountBindingContinue"),
            SteamAccountBindingDialogState.ReadyToBind => T("SteamAccountBindingConfirm"),
            SteamAccountBindingDialogState.SteamUnavailable
                or SteamAccountBindingDialogState.LoginRequired
                or SteamAccountBindingDialogState.Failed => T("SteamAccountBindingRetry"),
            _ => T("SteamAccountBindingConfirm")
        };

        IsSteamAccountBindingConfirmEnabled = steamAccountBindingDialogState is SteamAccountBindingDialogState.ReadyToBind
            or SteamAccountBindingDialogState.AlreadyBound
            or SteamAccountBindingDialogState.SteamUnavailable
            or SteamAccountBindingDialogState.LoginRequired
            or SteamAccountBindingDialogState.Failed;
        ConfirmSteamAccountBindingCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanConfirmSteamAccountBinding))]
    private async Task ConfirmSteamAccountBindingAsync()
    {
        if (steamAccountBindingDialogState is SteamAccountBindingDialogState.SteamUnavailable
            or SteamAccountBindingDialogState.LoginRequired
            or SteamAccountBindingDialogState.Failed)
        {
            await ShowSteamAccountBindingDialogAsync().ConfigureAwait(true);
            return;
        }

        if (pendingSteamBindingPlayerProfile is null)
        {
            return;
        }

        var identity = pendingSteamAccountIdentity;
        if (steamAccountBindingDialogState == SteamAccountBindingDialogState.ReadyToBind && identity is null)
        {
            return;
        }

        try
        {
            IsSteamAccountBindingBusy = true;
            RefreshCanSearchModDownloads();
            ConfirmSteamAccountBindingCommand.NotifyCanExecuteChanged();

            if (steamAccountBindingDialogState == SteamAccountBindingDialogState.ReadyToBind && identity is not null)
            {
                await playerSteamAccountBindingService
                    .BindAsync(pendingSteamBindingPlayerProfile.Id, identity)
                    .ConfigureAwait(true);
            }

            var modPackToInstall = pendingSteamModPackInstall;
            pendingSteamModPackInstall = null;
            approvedModDownloadSourceKey = modPackToInstall is null
                ? ModDownloadSourceOptionViewModel.SteamWorkshopKey
                : ModDownloadSourceOptionViewModel.ModPackKey;
            CloseSteamAccountBindingDialog(restoreApprovedSource: false);
            if (modPackToInstall is not null)
            {
                await InstallSteamModPackCoreAsync(modPackToInstall);
            }
            else
            {
                ApplySelectedModDownloadSourceChange();
            }
        }
        catch (SteamAccountBindingException ex)
        {
            steamAccountBindingDialogState = ex.FailureKind switch
            {
                SteamAccountBindingFailureKind.PlayerAlreadyBoundToDifferentSteamAccount =>
                    SteamAccountBindingDialogState.PlayerBoundToDifferentSteamAccount,
                SteamAccountBindingFailureKind.SteamAccountAlreadyBoundToDifferentPlayer =>
                    SteamAccountBindingDialogState.SteamAccountBoundToDifferentPlayer,
                _ => SteamAccountBindingDialogState.Failed
            };
            SteamAccountBindingStatusText = ex.Message;
            RefreshSteamAccountBindingDialogText(preserveStatusText: true);
        }
        catch (Exception ex)
        {
            steamAccountBindingDialogState = SteamAccountBindingDialogState.Failed;
            SteamAccountBindingStatusText = ex.Message;
            RefreshSteamAccountBindingDialogText(preserveStatusText: true);
        }
        finally
        {
            IsSteamAccountBindingBusy = false;
            RefreshCanSearchModDownloads();
            ConfirmSteamAccountBindingCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private void CancelSteamAccountBinding()
    {
        CloseSteamAccountBindingDialog(restoreApprovedSource: true);
        StatusText = T("SteamAccountBindingCanceled");
        LastActionText = StatusText;
    }

    private void CloseSteamAccountBindingDialog(bool restoreApprovedSource)
    {
        steamAccountBindingDetectionCancellation?.Cancel();
        steamAccountBindingDetectionCancellation?.Dispose();
        steamAccountBindingDetectionCancellation = null;
        steamAccountBindingDialogRequestId++;
        pendingSteamAccountIdentity = null;
        pendingPlayerSteamAccountBinding = null;
        pendingSteamAccountBindingConflict = null;
        pendingSteamBindingPlayerProfile = null;
        pendingSteamBindingConflictPlayerName = null;
        pendingSteamModPackInstall = null;
        steamAccountBindingDialogState = SteamAccountBindingDialogState.Hidden;
        IsSteamAccountBindingDialogOpen = false;
        IsSteamAccountBindingBusy = false;
        RefreshCanSearchModDownloads();
        ConfirmSteamAccountBindingCommand.NotifyCanExecuteChanged();

        if (!restoreApprovedSource)
        {
            return;
        }

        var approvedSource = ModDownloadSources.FirstOrDefault(source =>
            string.Equals(source.Key, approvedModDownloadSourceKey, StringComparison.Ordinal))
            ?? ModDownloadSources.First();
        if (ReferenceEquals(SelectedModDownloadSource, approvedSource))
        {
            return;
        }

        suppressSelectedModDownloadSourceChanged = true;
        try
        {
            SelectedModDownloadSource = approvedSource;
        }
        finally
        {
            suppressSelectedModDownloadSourceChanged = false;
        }

        ApplySelectedModDownloadSourceChange();
    }

}
