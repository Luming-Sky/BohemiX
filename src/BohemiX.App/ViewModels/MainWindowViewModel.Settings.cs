using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BohemiX.Core.Models;
using CommunityToolkit.Mvvm.Input;

namespace BohemiX.App.ViewModels;

public partial class MainWindowViewModel
{

    private void ApplySettingsModel(AppSettings settings)
    {
        installedModGroupNames.Clear();
        foreach (var (groupKey, customName) in settings.InstalledModGroupNames
                     ?? new Dictionary<string, string>())
        {
            var normalizedName = InstalledModGroupViewModel.NormalizeCustomName(customName);
            if (!string.IsNullOrWhiteSpace(groupKey) && normalizedName is not null)
            {
                installedModGroupNames[groupKey] = normalizedName;
            }
        }

        installedModGroupAssignments.Clear();
        foreach (var (modId, groupKey) in settings.InstalledModGroupAssignments
                     ?? new Dictionary<string, string>())
        {
            if (!string.IsNullOrWhiteSpace(modId)
                && InstalledModGroupViewModel.IsCustomGroupKey(groupKey)
                && installedModGroupNames.ContainsKey(groupKey))
            {
                installedModGroupAssignments[modId] = groupKey;
            }
        }

        SelectedLanguage = textCatalog.NormalizeLanguage(settings.SelectedLanguage);
        ApplyLanguage();
        AutoDiscoverOnStartup = settings.AutoDiscoverOnStartup;
        EnableVfsBeforeLaunch = settings.EnableVfsBeforeLaunch;
        CheckModConflictsBeforeLaunch = settings.CheckModConflictsBeforeLaunch;
        MinimizeOnLaunch = settings.MinimizeOnLaunch;
        CloseAfterLaunch = settings.CloseAfterLaunch;
        UseSteamProtocol = settings.UseSteamProtocol;
        EnableAcrylicEffects = settings.EnableAcrylicEffects;
        ReduceMotion = settings.ReduceMotion;
        CompactSidebar = settings.CompactSidebar;
        AllowNexusBrowserCookieAuth = false;
        ShowNavigationTooltips = settings.ShowNavigationTooltips;
        KeepTopNavigationVisible = settings.KeepTopNavigationVisible;
        ShowRuntimeCardInSidebar = settings.ShowRuntimeCardInSidebar;
        BackgroundDimLevel = settings.BackgroundDimLevel;
        ForgeRenderQualityIndex = QualityIndex(settings.ForgeRenderQuality);
        ForgeTextureQualityIndex = QualityIndex(settings.ForgeTextureQuality);
        forgeGraphicsSettings.Apply(settings.ForgeRenderQuality, settings.ForgeTextureQuality);
        LaunchArguments = settings.LaunchArguments ?? string.Empty;
        DefaultNavigationTarget = LocalizeNavigationTarget(CanonicalizeNavigationTarget(settings.DefaultNavigationTarget));
        MoreSettings.ApplyAppearanceSettings(settings);
    }

    private async Task SaveCurrentSettingsAsync()
    {
        var existing = await appSettingsService.LoadAsync();
        var snapshot = CreateSettingsSnapshot(existing);
        await appSettingsService.SaveAsync(snapshot);
        forgeGraphicsSettings.Apply(snapshot.ForgeRenderQuality, snapshot.ForgeTextureQuality);
    }

    private AppSettings CreateSettingsSnapshot(AppSettings existing)
    {
        return existing with
        {
            ModsDirectory = string.IsNullOrWhiteSpace(ModsDirectory) ? existing.ModsDirectory : ModsDirectory,
            AllowNexusBrowserCookieAuth = false,
            SelectedLanguage = textCatalog.NormalizeLanguage(SelectedLanguage),
            DefaultNavigationTarget = CanonicalizeNavigationTarget(DefaultNavigationTarget),
            ShowNavigationTooltips = ShowNavigationTooltips,
            KeepTopNavigationVisible = KeepTopNavigationVisible,
            AutoDiscoverOnStartup = AutoDiscoverOnStartup,
            EnableVfsBeforeLaunch = EnableVfsBeforeLaunch,
            LaunchArguments = LaunchArguments ?? string.Empty,
            UseSteamProtocol = UseSteamProtocol,
            MinimizeOnLaunch = MinimizeOnLaunch,
            CloseAfterLaunch = CloseAfterLaunch,
            CheckModConflictsBeforeLaunch = CheckModConflictsBeforeLaunch,
            EnableAcrylicEffects = EnableAcrylicEffects,
            ReduceMotion = ReduceMotion,
            BackgroundDimLevel = Math.Clamp(BackgroundDimLevel, 30, 90),
            CompactSidebar = CompactSidebar,
            ShowRuntimeCardInSidebar = ShowRuntimeCardInSidebar,
            ForgeRenderQuality = QualityKey(ForgeRenderQualityIndex),
            ForgeTextureQuality = QualityKey(ForgeTextureQualityIndex),
            InstalledModGroupNames = new Dictionary<string, string>(
                installedModGroupNames,
                StringComparer.OrdinalIgnoreCase),
            InstalledModGroupAssignments = new Dictionary<string, string>(
                installedModGroupAssignments,
                StringComparer.OrdinalIgnoreCase)
        };
    }

    [RelayCommand]
    private async Task ResetSettingsAsync()
    {
        AutoDiscoverOnStartup = true;
        EnableVfsBeforeLaunch = true;
        CheckModConflictsBeforeLaunch = true;
        MinimizeOnLaunch = false;
        CloseAfterLaunch = false;
        UseSteamProtocol = false;
        EnableAcrylicEffects = true;
        ReduceMotion = false;
        CompactSidebar = false;
        AllowNexusBrowserCookieAuth = false;
        ShowNavigationTooltips = true;
        KeepTopNavigationVisible = true;
        ShowRuntimeCardInSidebar = true;
        SelectedLanguage = SimplifiedChineseLanguage;
        DefaultNavigationTarget = LocalizeNavigationTarget(NavigationTargetDashboard);
        BackgroundDimLevel = 68;
        LaunchArguments = string.Empty;
        ForgeRenderQualityIndex = 1;
        ForgeTextureQualityIndex = 1;
        installedModGroupNames.Clear();
        installedModGroupAssignments.Clear();
        SyncInstalledModGroups();
        SelectSettingsCategory("Navigation");
        await SaveCurrentSettingsAsync();
        StatusText = T("SettingsReset");
        LastActionText = StatusText;
    }

    [RelayCommand]
    private void SelectSettingsCategory(string category)
    {
        if (!string.Equals(category, "More", StringComparison.Ordinal))
        {
            MoreSettings.DeactivateVisualResources();
        }

        currentSettingsCategory = category;
        IsSettingsPlayerProfilesActive = category == "PlayerProfiles";
        IsSettingsNavigationActive = category == "Navigation";
        IsSettingsLaunchActive = category == "Launch";
        IsSettingsModsActive = category == "Mods";
        IsSettingsAppearanceActive = category == "Appearance";
        IsSettingsGraphicsActive = category == "Graphics";
        IsSettingsStorageActive = category == "Storage";
        IsSettingsAdvancedActive = category == "Advanced";
        IsSettingsMoreActive = category == "More";

        SettingsCategoryTitle = category switch
        {
            "PlayerProfiles" => T("CategoryPlayerProfilesTitle"),
            "Navigation" => T("CategoryNavigationTitle"),
            "Launch" => T("CategoryLaunchTitle"),
            "Mods" => T("CategoryModsTitle"),
            "Appearance" => SettingsAppearanceText,
            "Graphics" => SettingsGraphicsText,
            "Storage" => T("CategoryStorageTitle"),
            "Advanced" => SettingsAdvancedText,
            "More" => SettingsMoreText,
            _ => T("CategoryLaunchTitle")
        };

        SettingsCategoryDescription = category switch
        {
            "PlayerProfiles" => T("CategoryPlayerProfilesDescription"),
            "Navigation" => T("CategoryNavigationDescription"),
            "Launch" => T("CategoryLaunchDescription"),
            "Mods" => T("CategoryModsDescription"),
            "Appearance" => T("CategoryAppearanceDescription"),
            "Graphics" => T("CategoryGraphicsDescription"),
            "Storage" => T("CategoryStorageDescription"),
            "Advanced" => T("CategoryAdvancedDescription"),
            "More" => T("CategoryMoreDescription"),
            _ => T("CategoryLaunchDescription")
        };

        SettingsWorkspaceTitle = category == "PlayerProfiles"
            ? T("PlayerProfiles")
            : LauncherSettingsTitleText;

        StatusText = $"{SettingsCategorySelectedPrefixText}: {SettingsCategoryTitle}.";
        LastActionText = StatusText;

        if (category == "More")
        {
            _ = MoreSettings.ActivateVisualResourcesAsync();
            _ = MoreSettings.PlayEchoPreviewAsync();
        }
    }

    [RelayCommand]

    private async Task ApplySettingsAsync()
    {
        await SaveCurrentSettingsAsync();
        StatusText = $"{T("SettingsApplied")} {ForgeSettingsApplyNextEntryText}";
        LastActionText = StatusText;
    }

    private static int QualityIndex(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "low" => 0,
        "high" => 2,
        _ => 1
    };

    private static string QualityKey(int index) => index switch
    {
        0 => "Low",
        2 => "High",
        _ => "Medium"
    };


}
