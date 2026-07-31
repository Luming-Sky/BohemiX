using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using BohemiX.Core.Models.Saves;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BohemiX.App.ViewModels;

public partial class SaveSlotRowViewModel(SaveSlot slot) : ViewModelBase
{
    public Guid Id { get; } = slot.Id;

    public string PhysicalName { get; } = slot.PhysicalName;

    public string PhysicalPath { get; } = slot.PhysicalPath;

    public string DisplayName { get; } = slot.DisplayName;

    public string GameSaveName { get; } = string.IsNullOrWhiteSpace(slot.GameSaveName) ? "没有读取到游戏内名称" : slot.GameSaveName;

    public string DetailTitleText { get; } = CreateDetailTitle(slot);

    public string DetailSubtitleText { get; } = CreateDetailSubtitle(slot);

    public string DetailLocationText { get; } = string.IsNullOrWhiteSpace(slot.DisplayData?.CurrentLocation)
        ? "地点待识别"
        : slot.DisplayData.CurrentLocation;

    public string DetailQuestText { get; } = string.IsNullOrWhiteSpace(slot.DisplayData?.ActiveQuest)
        ? "任务信息待识别"
        : slot.DisplayData.ActiveQuest;

    public string SaveKindText { get; } = CreateSaveTypeText(slot.Type);

    public string DetailBuildText { get; } = string.IsNullOrWhiteSpace(slot.GameVersion)
        ? "版本未记录"
        : slot.GameVersion;

    public string PlayTimeText { get; } = slot.PlayTime is null
        ? "-"
        : $"{(int)slot.PlayTime.Value.TotalHours:00}:{slot.PlayTime.Value.Minutes:00}:{slot.PlayTime.Value.Seconds:00}";

    public string LastSavedText { get; } = slot.LastSavedAtUtc is null
        ? "-"
        : slot.LastSavedAtUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public string CurrentLocationText { get; } = string.IsNullOrWhiteSpace(slot.DisplayData?.CurrentLocation)
        ? "地点未知"
        : slot.DisplayData.CurrentLocation;

    public string ActiveQuestText { get; } = string.IsNullOrWhiteSpace(slot.DisplayData?.ActiveQuest)
        ? "任务未知"
        : slot.DisplayData.ActiveQuest;

    public string HenryLevelText { get; } = slot.DisplayData is null || slot.DisplayData.HenryLevel <= 0
        ? slot.PlayTime is null ? "游玩时间未记录" : $"游玩 {FormatPlayTime(slot.PlayTime.Value)}"
        : $"等级 {slot.DisplayData.HenryLevel}";

    public string GroschenText { get; } = slot.DisplayData is null || slot.DisplayData.GroschenCount <= 0
        ? slot.LastSavedAtUtc is null ? CreateSaveTypeText(slot.Type) : $"保存 {slot.LastSavedAtUtc.Value.ToLocalTime():yyyy-MM-dd HH:mm}"
        : $"{slot.DisplayData.GroschenCount} 格罗申";

    public string StatusEffectsText { get; } = slot.DisplayData?.PlayerStatusEffects.Count > 0
        ? string.Join("、", slot.DisplayData.PlayerStatusEffects)
        : CreateBuildInfoText(slot);

    public string SaveInfoSummary { get; } = CreateSaveInfoSummary(slot);

    public string UpdatedText { get; } = slot.UpdatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public bool IsCurrentGameSave { get; init; }

    [ObservableProperty]
    private bool isMounted;

    [ObservableProperty]
    private bool isSelected;

    [ObservableProperty]
    private int zIndex;

    [ObservableProperty]
    private double opacity = 1.0;

    [ObservableProperty]
    private Thickness layoutMargin;

    [ObservableProperty]
    private double stackOffsetX;

    [ObservableProperty]
    private double stackOffsetY;

    [ObservableProperty]
    private double stackScale = 1.0;

    [ObservableProperty]
    private string transformMatrix = "translate(0px, 0px) scale(1)";

    [ObservableProperty]
    private ITransform stackTransform = CreateStackTransform(0, 0, 1.0);

    public static ITransform CreateStackTransform(double skewXDegrees, double rotateDegrees, double scale)
    {
        return new TransformGroup
        {
            Children =
            {
                new SkewTransform(skewXDegrees, 0),
                new RotateTransform(rotateDegrees),
                new ScaleTransform(scale, scale)
            }
        };
    }

    private static string CreateSaveInfoSummary(SaveSlot slot)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(slot.DisplayData?.CurrentLocation))
        {
            parts.Add(slot.DisplayData.CurrentLocation);
        }

        if (!string.IsNullOrWhiteSpace(slot.DisplayData?.ActiveQuest))
        {
            parts.Add(slot.DisplayData.ActiveQuest);
        }

        if (slot.DisplayData?.HenryLevel > 0)
        {
            parts.Add($"等级 {slot.DisplayData.HenryLevel}");
        }

        if (slot.DisplayData?.GroschenCount > 0)
        {
            parts.Add($"{slot.DisplayData.GroschenCount} 格罗申");
        }

        return parts.Count == 0 ? GameSaveNameFallback(slot) : string.Join(" · ", parts);
    }

    private static string CreateDetailTitle(SaveSlot slot)
    {
        if (!string.IsNullOrWhiteSpace(slot.DisplayData?.CurrentLocation))
        {
            return slot.DisplayData.CurrentLocation;
        }

        if (!string.IsNullOrWhiteSpace(slot.GameSaveName))
        {
            return slot.GameSaveName;
        }

        return slot.DisplayName;
    }

    private static string CreateDetailSubtitle(SaveSlot slot)
    {
        var parts = new List<string>
        {
            CreateSaveTypeText(slot.Type)
        };

        if (slot.PlayTime is not null)
        {
            parts.Add($"游戏时间 {FormatPlayTime(slot.PlayTime.Value)}");
        }

        if (slot.LastSavedAtUtc is not null)
        {
            parts.Add($"保存于 {slot.LastSavedAtUtc.Value.ToLocalTime():yyyy-MM-dd HH:mm}");
        }

        return string.Join(" · ", parts);
    }

    private static string GameSaveNameFallback(SaveSlot slot) =>
        string.IsNullOrWhiteSpace(slot.GameSaveName) ? "没有读取到存档信息" : slot.GameSaveName;

    private static string FormatPlayTime(TimeSpan playTime) =>
        $"{(int)playTime.TotalHours:00}:{playTime.Minutes:00}:{playTime.Seconds:00}";

    private static string CreateSaveTypeText(SaveType? type) =>
        type switch
        {
            SaveType.Auto => "自动存档",
            SaveType.Bed => "床铺存档",
            SaveType.Exit => "退出存档",
            SaveType.Potion => "永久存档",
            _ => "存档类型未记录"
        };

    private static string CreateBuildInfoText(SaveSlot slot)
    {
        var typeText = CreateSaveTypeText(slot.Type);
        return string.IsNullOrWhiteSpace(slot.GameVersion)
            ? typeText
            : $"{typeText} · {slot.GameVersion}";
    }
}
