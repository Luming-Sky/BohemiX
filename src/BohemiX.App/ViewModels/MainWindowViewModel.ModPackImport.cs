using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BohemiX.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BohemiX.App.ViewModels;

public partial class MainWindowViewModel
{
    private ModPackImportPlan? localModPackImportPlan;
    private string? localModPackImportPath;
    private CancellationTokenSource? localModPackImportCancellation;

    public ObservableCollection<LocalModPackImportItemViewModel> LocalModPackImportItems { get; } = [];

    public bool CanConfirmLocalModPackImport =>
        IsLocalModPackImportOpen
        && !IsLocalModPackImportBusy
        && localModPackImportPlan is not null
        && LocalModPackImportItems.Any(item => item.Action is ModPackImportAction.Install or ModPackImportAction.Replace);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirmLocalModPackImport))]
    private bool isLocalModPackImportOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirmLocalModPackImport))]
    private bool isLocalModPackImportBusy;

    [ObservableProperty]
    private bool isLocalModPackImportPasswordRequired;

    [ObservableProperty]
    private string localModPackImportPassword = string.Empty;

    [ObservableProperty]
    private string localModPackImportTitleText = string.Empty;

    [ObservableProperty]
    private string localModPackImportSummaryText = string.Empty;

    [ObservableProperty]
    private string localModPackImportStatusText = string.Empty;

    [ObservableProperty]
    private string localModPackImportWarningsText = string.Empty;

    [ObservableProperty]
    private double localModPackImportProgressPercent;

    [ObservableProperty]
    private string localModPackImportProgressText = string.Empty;

    [ObservableProperty]
    private bool useLocalModPackAuthorLoadOrder = true;

    [ObservableProperty]
    private string importModPackText = "Import mod pack";

    [ObservableProperty]
    private string localModPackPasswordText = "Archive password";

    [ObservableProperty]
    private string localModPackRetryText = "Retry";

    [ObservableProperty]
    private string localModPackUseAuthorOrderText = "Use the author's load order";

    [ObservableProperty]
    private string localModPackReplaceText = "Replace installed mod";

    [ObservableProperty]
    private string localModPackConfirmText = "Import selected";

    [ObservableProperty]
    private string localModPackCancelText = "Cancel";

    [RelayCommand]
    private async Task ImportLocalModPackAsync()
    {
        var path = await gamePathPickerService.PickModPackAsync();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        localModPackImportPath = path;
        IsLocalModPackImportOpen = true;
        LocalModPackImportTitleText = Path.GetFileNameWithoutExtension(path);
        await PrepareLocalModPackAsync(password: null);
    }

    [RelayCommand]
    private async Task RetryLocalModPackImportAsync()
    {
        if (string.IsNullOrWhiteSpace(localModPackImportPath))
        {
            return;
        }

        await PrepareLocalModPackAsync(LocalModPackImportPassword);
    }

    [RelayCommand]
    private async Task ConfirmLocalModPackImportAsync()
    {
        if (localModPackImportPlan is null || IsLocalModPackImportBusy)
        {
            return;
        }

        IsLocalModPackImportBusy = true;
        localModPackImportCancellation = new CancellationTokenSource();
        LocalModPackImportProgressPercent = 0;
        try
        {
            var selections = LocalModPackImportItems
                .Select(item => new ModPackImportSelection(item.Id, item.Action))
                .ToArray();
            var progress = new Progress<ModPackImportProgress>(value =>
            {
                LocalModPackImportProgressText = value.Message;
                LocalModPackImportProgressPercent = value.TotalCount == 0
                    ? 0
                    : Math.Clamp(value.CompletedCount * 100d / value.TotalCount, 0, 100);
            });
            var result = await modPackImportService.ImportAsync(
                localModPackImportPlan.SessionId,
                selections,
                UseLocalModPackAuthorLoadOrder,
                progress,
                localModPackImportCancellation.Token);
            StatusText = result.Message;
            LastActionText = result.Message;
            await RefreshModsAsync();
            ResetLocalModPackImportState();
        }
        catch (OperationCanceledException)
        {
            LocalModPackImportStatusText = T("LocalModPackCanceled");
            StatusText = LocalModPackImportStatusText;
            LastActionText = StatusText;
            ResetLocalModPackImportState();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            LocalModPackImportStatusText = string.Format(T("LocalModPackImportFailed"), ex.Message);
            StatusText = LocalModPackImportStatusText;
            LastActionText = StatusText;
        }
        finally
        {
            localModPackImportCancellation?.Dispose();
            localModPackImportCancellation = null;
            IsLocalModPackImportBusy = false;
        }
    }

    [RelayCommand]
    private async Task CloseLocalModPackImportAsync()
    {
        if (IsLocalModPackImportBusy)
        {
            localModPackImportCancellation?.Cancel();
            return;
        }

        if (localModPackImportPlan is not null)
        {
            await modPackImportService.DiscardAsync(localModPackImportPlan.SessionId);
        }

        ResetLocalModPackImportState();
    }

    private async Task PrepareLocalModPackAsync(string? password)
    {
        if (string.IsNullOrWhiteSpace(localModPackImportPath))
        {
            return;
        }

        if (localModPackImportPlan is not null)
        {
            await modPackImportService.DiscardAsync(localModPackImportPlan.SessionId);
            localModPackImportPlan = null;
        }

        IsLocalModPackImportBusy = true;
        IsLocalModPackImportPasswordRequired = false;
        LocalModPackImportItems.Clear();
        LocalModPackImportStatusText = T("LocalModPackPreparing");
        localModPackImportCancellation?.Dispose();
        localModPackImportCancellation = new CancellationTokenSource();
        try
        {
            var result = await modPackImportService.PrepareAsync(
                localModPackImportPath,
                password,
                localModPackImportCancellation.Token);
            LocalModPackImportStatusText = result.Message;
            IsLocalModPackImportPasswordRequired = result.Status is ModPackImportPrepareStatus.PasswordRequired or ModPackImportPrepareStatus.InvalidPassword;
            if (result.Plan is null)
            {
                LocalModPackImportSummaryText = string.Empty;
                LocalModPackImportWarningsText = string.Empty;
                return;
            }

            localModPackImportPlan = result.Plan;
            LocalModPackImportTitleText = result.Plan.PackageName;
            foreach (var item in result.Plan.Items)
            {
                LocalModPackImportItems.Add(new LocalModPackImportItemViewModel(item, T, OnLocalModPackImportSelectionChanged));
            }

            var valid = result.Plan.Items.Count(item => item.State is ModPackImportItemState.New or ModPackImportItemState.AlreadyInstalled);
            var existing = result.Plan.Items.Count(item => item.State == ModPackImportItemState.AlreadyInstalled);
            var invalid = result.Plan.Items.Count - valid;
            LocalModPackImportSummaryText = string.Format(T("LocalModPackSummary"), valid, existing, invalid);
            LocalModPackImportWarningsText = string.Join(Environment.NewLine, result.Plan.Warnings);
            UseLocalModPackAuthorLoadOrder = result.Plan.AuthorLoadOrder.Count > 0;
            OnPropertyChanged(nameof(CanConfirmLocalModPackImport));
        }
        catch (OperationCanceledException)
        {
            ResetLocalModPackImportState();
        }
        finally
        {
            localModPackImportCancellation?.Dispose();
            localModPackImportCancellation = null;
            IsLocalModPackImportBusy = false;
        }
    }

    private void OnLocalModPackImportSelectionChanged() => OnPropertyChanged(nameof(CanConfirmLocalModPackImport));

    private void ResetLocalModPackImportState()
    {
        IsLocalModPackImportOpen = false;
        IsLocalModPackImportPasswordRequired = false;
        LocalModPackImportPassword = string.Empty;
        LocalModPackImportTitleText = string.Empty;
        LocalModPackImportSummaryText = string.Empty;
        LocalModPackImportStatusText = string.Empty;
        LocalModPackImportWarningsText = string.Empty;
        LocalModPackImportProgressPercent = 0;
        LocalModPackImportProgressText = string.Empty;
        LocalModPackImportItems.Clear();
        localModPackImportPlan = null;
        localModPackImportPath = null;
        OnPropertyChanged(nameof(CanConfirmLocalModPackImport));
    }
}
