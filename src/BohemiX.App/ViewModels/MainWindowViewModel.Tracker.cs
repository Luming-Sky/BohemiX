using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using BohemiX.Core.Models;
using CommunityToolkit.Mvvm.Input;

namespace BohemiX.App.ViewModels;

public partial class MainWindowViewModel
{

    [RelayCommand]
    private async Task PrepareTrackerModPackageAsync()
    {
        try
        {
            var result = await trackerModPackageService.PrepareAsync();
            TrackerModPackagePath = result.PackageDirectory;
            TrackerBridgePath = result.BridgeEventsPath;
            TrackerModStatusText = string.Format(T("TrackerModPrepared"), result.ModId);
            StatusText = string.Format(T("TrackerModPackagePreparedAt"), result.PackageDirectory);
            LastActionText = StatusText;
            await RefreshTrackerModHealthAsync();
        }
        catch (Exception ex)
        {
            TrackerModStatusText = string.Format(T("TrackerModPackageFailed"), ex.Message);
            StatusText = TrackerModStatusText;
            LastActionText = StatusText;
        }
    }

    [RelayCommand]
    private async Task InstallTrackerModAsync()
    {
        if (selectedGame is null || !selectedGame.IsVerified)
        {
            TrackerModInstallStatusText = T("VerifyKcd2BeforeTrackerInstall");
            Navigate("Install");
            StatusText = TrackerModInstallStatusText;
            LastActionText = StatusText;
            return;
        }

        var result = await trackerModInstallService.InstallAsync(selectedGame);
        TrackerModInstallStatusText = result.Message;
        StatusText = result.Message;
        LastActionText = StatusText;

        if (result.IsInstalled)
        {
            await RefreshModsAsync();
        }

        await RefreshTrackerModHealthAsync();
    }

    [RelayCommand]
    private async Task RefreshTrackerModHealthAsync()
    {
        var health = await trackerModHealthService.CheckAsync(selectedGame);
        IsTrackerHealthy = health.IsLocalPackagePrepared
                           && health.IsInstalledToGame
                           && health.IsListedInModOrder;
        TrackerModHealthStatusText = health.Message;
        TrackerBridgeLastWriteText = health.BridgeLastWriteUtc is null
            ? T("BridgeLastWriteNever")
            : string.Format(T("BridgeLastWriteAt"), health.BridgeLastWriteUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture));
    }

    [RelayCommand]
    private async Task RunTrackerSelfTestAsync()
    {
        var result = await trackerDiagnosticsService.RunBridgeSelfTestAsync();
        TrackerDiagnosticsStatusText = result.Message;
        StatusText = result.Message;
        LastActionText = StatusText;

        await RefreshTrackerAsync();
        StatusText = result.Message;
        LastActionText = StatusText;
    }


    private async Task EnsureBuiltInTrackerModAsync()
    {
        try
        {
            // Prepare is idempotent and also upgrades the bundled Tracker script when its schema changes.
            var result = await trackerModPackageService.PrepareAsync();
            TrackerModPackagePath = result.PackageDirectory;
            TrackerBridgePath = result.BridgeEventsPath;
            TrackerModStatusText = string.Format(T("TrackerModPrepared"), result.ModId);
        }
        catch (Exception ex)
        {
            TrackerModStatusText = string.Format(T("TrackerModPackageFailed"), ex.Message);
        }
    }


    private async Task RefreshTrackerAsync()
    {
        try
        {
            var paths = applicationPathService.GetPaths();
            TrackerBridgePath = paths.TrackerBridgeEventsPath;

            var result = await trackerRuntimeService.RefreshAsync();
            ApplyTrackerSnapshot(result.Snapshot);

            Sections[3].Detail = string.Format(T("TrackerProcessedSummary"), result.ProcessedEvents, result.SkippedLines);
            StatusText = Sections[3].Detail;
            LastActionText = StatusText;
        }
        catch (Exception ex)
        {
            StatusText = string.Format(T("TrackerRefreshFailed"), ex.Message);
            Sections[3].Detail = StatusText;
            LastActionText = StatusText;
        }
    }

    private void StartTrackerMonitor()
    {
        if (isTrackerRuntimeSubscribed)
        {
            return;
        }

        trackerRuntimeService.Updated += OnTrackerRuntimeUpdated;
        isTrackerRuntimeSubscribed = true;
        trackerRuntimeService.Start();
    }

    private void OnTrackerRuntimeUpdated(TrackerRuntimeUpdate update)
    {
        _ = Dispatcher.UIThread.InvokeAsync(() =>
        {
            ApplyTrackerSnapshot(update.Snapshot);
            Sections[3].Detail = string.Format(T("TrackerAutoProcessedSummary"), update.ProcessedEvents);
        });
    }

    private void ApplyTrackerSnapshot(TrackerRuntimeSnapshot snapshot)
    {
        ApplyTrackerSummary(snapshot.Summary);
        ApplyTrackerDetails(snapshot.VisibleEntities, snapshot.RecentEventLabels);
        ApplyTrackerAchievements(snapshot.Achievements);
    }

    private void ApplyTrackerSummary(TrackerSummary summary)
    {
        TrackerEventCount = summary.TotalEvents;
        TrackerVisibleEntityCount = summary.VisibleEntities;
        TrackerActiveQuestCount = summary.ActiveQuests;
        TrackerCompletedEntityCount = summary.CompletedEntities;
        TrackerUnconfirmedSessionCount = summary.UnconfirmedSessions;
        TrackerLastEventText = summary.LastEventLabel;
        TrackerPositionText = summary.PlayerX is null || summary.PlayerY is null
            ? T("AwaitingPosition")
            : $"X {summary.PlayerX:0.0} / Y {summary.PlayerY:0.0} / Z {summary.PlayerZ.GetValueOrDefault():0.0}";
        ApplyTrackerMapPosition(summary);
    }

    private void ApplyTrackerMapPosition(TrackerSummary summary)
    {
        if (summary.PlayerX is null || summary.PlayerY is null)
        {
            IsTrackerPositionVisible = false;
            TrackerMapStatusText = T("WaitingForPositionUpdate");
            TrackerMapMarkerLeft = 152;
            TrackerMapMarkerTop = 82;
            return;
        }

        IsTrackerPositionVisible = true;
        TrackerMapMarkerLeft = NormalizeMapAxis(summary.PlayerX.Value, -5000, 5000, 16, 288);
        TrackerMapMarkerTop = NormalizeMapAxis(summary.PlayerY.Value, -5000, 5000, 148, 16);
        TrackerMapStatusText = string.Format(T("PlayerProjection"), TrackerPositionText);
    }

    private static double NormalizeMapAxis(double value, double min, double max, double outputMin, double outputMax)
    {
        var normalized = Math.Clamp((value - min) / (max - min), 0, 1);
        return outputMin + ((outputMax - outputMin) * normalized);
    }

    private void ApplyTrackerDetails(
        IReadOnlyList<TrackerEntityState> entities,
        IReadOnlyList<string> recentEvents)
    {
        TrackerEntityRows.Clear();
        foreach (var entity in entities)
        {
            TrackerEntityRows.Add($"{entity.Kind} / {entity.Status} / {entity.DisplayName}");
        }

        if (TrackerEntityRows.Count == 0)
        {
            TrackerEntityRows.Add(T("NoVisibleTrackerEntities"));
        }

        TrackerRecentEventRows.Clear();
        foreach (var recentEvent in recentEvents)
        {
            TrackerRecentEventRows.Add(recentEvent);
        }

        if (TrackerRecentEventRows.Count == 0)
        {
            TrackerRecentEventRows.Add(T("AwaitingBridgeEvents"));
        }
    }

    private void ApplyTrackerAchievements(IReadOnlyList<AchievementProgress> progress)
    {
        TrackerAchievementTotalCount = progress.Count;
        TrackerAchievementUnlockedCount = progress.Count(item => item.IsUnlocked);
        TrackerAchievementRows.Clear();

        foreach (var item in progress.OrderByDescending(item => item.IsUnlocked).ThenBy(item => item.AchievementId))
        {
            var state = item.IsUnlocked ? T("Unlocked") : T("Locked");
            TrackerAchievementRows.Add($"{state} / {item.AchievementId} / {item.CurrentValue}/{item.TargetValue}");
        }

        if (TrackerAchievementRows.Count == 0)
        {
            TrackerAchievementRows.Add(T("NoTrackerAchievementRulesLoaded"));
        }
    }

}
