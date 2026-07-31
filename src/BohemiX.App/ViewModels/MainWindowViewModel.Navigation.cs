using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;

namespace BohemiX.App.ViewModels;

public partial class MainWindowViewModel
{

    private void NavigateCore(string destination, bool silent, bool updateStatus = true)
    {
        destination = destination is "Library" or "Install"
            ? "Dashboard"
            : destination;

        var isActualTransition = SelectedTopNav != destination;
        SelectedTopNav = destination;
        UpdateActiveStates(destination);
        UpdateWorkspaceVisibility(destination);

        var section = destination switch
        {
            "Home" or "Dashboard" => Sections[0],
            "Mods" or "Community" => Sections[2],
            "Saves" => Sections[3],
            "Downloads" => Sections[2],
            "Lab" => Sections[5],
            "Store" or "Settings" => Sections[4],
            _ => Sections[0]
        };

        SelectedSection = section;
        WorkspaceTitle = GetWorkspaceTitle(destination);
        WorkspaceDescription = GetWorkspaceDescription(destination);
        if (updateStatus)
        {
            StatusText = string.Format(T("OpenedWorkspace"), WorkspaceTitle);
            LastActionText = StatusText;
        }

        // The entrance animation ("pop in") must only replay when a top-level nav
        // button triggers the navigation. Left sub-navigation buttons reuse these
        // commands via their *Silent variants (silent: true), which skip the event
        // so the sidebar items do not re-slide in.
        if (!silent && isActualTransition)
        {
            NavigationTransitionRequested?.Invoke();
        }
    }

    [RelayCommand]
    private void Navigate(string destination)
    {
        NavigateCore(destination, silent: false);
    }

    // Invoked by the LEFT sub-navigation bar; suppresses the entrance animation.
    // Method name is chosen so the generated command property is "NavigateSilentCommand".
    [RelayCommand]
    private void NavigateSilent(string destination)
    {
        NavigateCore(destination, silent: true);
    }


    [RelayCommand]
    private async Task LoadModsAsync()
    {
        NavigateCore("Mods", silent: false);
        SelectModManagerPage("List");
        await RefreshModsAsync();
        StatusText = Sections[2].Detail;
    }

    [RelayCommand]
    private void SelectModManagerPage(string page)
    {
        var normalizedPage = page switch
        {
            "Download" => "Download",
            "Recommendation" => "Recommendation",
            _ => "List"
        };

        IsModListPageActive = normalizedPage == "List";
        IsModDownloadPageActive = normalizedPage == "Download";
        IsModRecommendationPageActive = normalizedPage == "Recommendation";
        RefreshModManagerPageVisibility();
        if (IsModDownloadPageActive)
        {
            SyncDownloadQueueRows(modDownloader.Queue);
            _ = EnsureNexusResultsLoadedAsync();
        }
        else if (IsModRecommendationPageActive)
        {
            SyncDownloadQueueRows(modDownloader.Queue);
            _ = EnsureModRecommendationsLoadedAsync();
        }

        RefreshModManagerPageText();

        StatusText = string.Format(T("ModManagerPageSelected"), ModManagerPageTitle);
        LastActionText = StatusText;
    }

    private void RefreshModManagerPageText()
    {
        ModManagerPageTitle = true switch
        {
            _ when IsModDownloadPageActive => T("ModDownloadPage"),
            _ when IsModRecommendationPageActive => T("ModRecommendationPage"),
            _ => T("ModListPage")
        };

        ModManagerPageDescription = true switch
        {
            _ when IsModDownloadPageActive => T("ModDownloadPageDescription"),
            _ when IsModRecommendationPageActive => T("ModRecommendationPageDescription"),
            _ => T("ModListPageDescription")
        };
    }

    private void RefreshModManagerPageVisibility()
    {
        IsModListPageVisible = IsModManagerVisible && IsModListPageActive;
        IsModDownloadPageVisible = IsModManagerVisible && IsModDownloadPageActive;
        IsModRecommendationPageVisible = IsModManagerVisible && IsModRecommendationPageActive;

        if (!IsModDownloadPageVisible && IsNexusAccountBindingDialogOpen)
        {
            PlayerProfiles.CancelNexusAccountBindingRequest();
            pendingNexusModDownload = null;
            CloseNexusAccountBindingDialog(restoreApprovedSource: false);
        }
    }


    private void UpdateActiveStates(string destination)
    {
        IsDashboardActive = destination is "Home" or "Dashboard";
        IsModsActive = destination is "Mods" or "Community";
        IsSavesActive = destination is "Saves";
        IsStoreActive = destination is "Store";
        IsLabActive = destination is "Lab";
        IsSettingsActive = destination is "Settings";
        IsModManagerVisible = IsModsActive;
        IsLauncherDashboardVisible = !IsModManagerVisible && !IsSavesActive;
        IsLauncherNavigationVisible = !IsModManagerVisible && !IsLabActive;
        RefreshModManagerPageVisibility();
    }

    private void UpdateWorkspaceVisibility(string destination)
    {
        IsDownloadCenterVisible = destination == "Downloads";
        IsSettingsView = destination == "Settings";
        IsLabView = destination == "Lab";
        var isLauncherView = !IsDownloadCenterVisible && !IsSettingsView && !IsLabView;
        IsLauncherView = isLauncherView;
        IsLauncherNavigationVisible = IsDownloadCenterVisible || (isLauncherView && !IsModManagerVisible);
    }

    partial void OnIsModManagerVisibleChanged(bool value) => SyncModVisualResources(value);

    partial void OnIsSavesActiveChanged(bool value)
    {
        if (value)
        {
            SaveManager.ActivateWorkspaceVisualResources();
        }
        else
        {
            SaveManager.DeactivateWorkspaceVisualResources();
        }
    }

    partial void OnIsSettingsActiveChanged(bool value)
    {
        if (value && IsSettingsMoreActive)
        {
            _ = MoreSettings.ActivateVisualResourcesAsync();
            _ = MoreSettings.PlayEchoPreviewAsync();
        }
        else if (!value)
        {
            MoreSettings.DeactivateVisualResources();
        }
    }

}
