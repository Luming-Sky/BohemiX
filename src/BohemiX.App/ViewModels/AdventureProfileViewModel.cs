using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using BohemiX.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BohemiX.App.ViewModels;

public static class AdventureStatisticKeys
{
    public const string InGameDays = "in-game-days";
    public const string MainStory = PlayerStatisticKeys.MainStory;
    public const string Achievements = PlayerStatisticKeys.Achievements;
    public const string Exploration = PlayerStatisticKeys.Exploration;
    public const string SaveSlots = PlayerStatisticKeys.SaveSlots;
    public const string InstalledMods = PlayerStatisticKeys.InstalledMods;
}

public interface IAdventureStatisticDefinition
{
    string Key { get; }

    string EnglishTitle { get; }

    string ChineseTitle { get; }

    int DisplayOrder { get; }
}

public sealed record AdventureStatisticDefinition(
    string Key,
    string EnglishTitle,
    string ChineseTitle,
    int DisplayOrder) : IAdventureStatisticDefinition;

public sealed record AdventureProfileSnapshot(
    string PlayerName,
    string Platform,
    string Difficulty,
    int HoursPlayed,
    int? InGameDay,
    int? MainStoryCompleted,
    int? MainStoryTotal,
    int? AchievementsUnlocked,
    int? AchievementsTotal,
    int? LocationsDiscovered,
    int? LocationsTotal,
    int SaveSlots,
    int InstalledMods);

public sealed record AdventureSaveOption(Guid Id, string DisplayName);

public sealed partial class AdventureSaveOptionViewModel : ViewModelBase
{
    private readonly Func<Guid, Task> openSave;

    public AdventureSaveOptionViewModel(
        AdventureSaveOption option,
        bool isSelected,
        Func<Guid, Task> openSave)
    {
        ArgumentNullException.ThrowIfNull(option);
        ArgumentNullException.ThrowIfNull(openSave);

        Id = option.Id;
        DisplayName = option.DisplayName;
        this.isSelected = isSelected;
        this.openSave = openSave;
    }

    public Guid Id { get; }

    public string DisplayName { get; }

    [ObservableProperty]
    private bool isSelected;

    [RelayCommand]
    private Task OpenAsync() => openSave(Id);
}

public sealed partial class AdventureStatisticViewModel : ViewModelBase
{
    public AdventureStatisticViewModel(
        IAdventureStatisticDefinition definition,
        string value,
        bool useEnglish)
    {
        Key = definition.Key;
        EnglishTitle = definition.EnglishTitle;
        ChineseTitle = definition.ChineseTitle;
        DisplayOrder = definition.DisplayOrder;
        Value = value;
        Title = useEnglish ? EnglishTitle : ChineseTitle;
    }

    public string Key { get; }

    public string EnglishTitle { get; private set; }

    public string ChineseTitle { get; private set; }

    public int DisplayOrder { get; private set; }

    [ObservableProperty]
    private string title;

    [ObservableProperty]
    private string value;

    internal void UpdateDefinition(IAdventureStatisticDefinition definition, bool useEnglish)
    {
        EnglishTitle = definition.EnglishTitle;
        ChineseTitle = definition.ChineseTitle;
        DisplayOrder = definition.DisplayOrder;
        Title = useEnglish ? EnglishTitle : ChineseTitle;
    }

    internal void UseLanguage(bool useEnglish)
    {
        Title = useEnglish ? EnglishTitle : ChineseTitle;
    }
}

public sealed partial class AdventureProfileViewModel : ViewModelBase
{
    private static readonly AdventureStatisticDefinition[] CoreStatistics =
    [
        new(AdventureStatisticKeys.InGameDays, "In-Game Days", "游戏内天数", 0),
        new(AdventureStatisticKeys.MainStory, "Main Story", "主线进度", 1),
        new(AdventureStatisticKeys.Achievements, "Achievements", "成就", 2),
        new(AdventureStatisticKeys.Exploration, "Exploration", "地图探索", 3),
        new(AdventureStatisticKeys.SaveSlots, "Save Slots", "存档数量", 4),
        new(AdventureStatisticKeys.InstalledMods, "Installed Mods", "已启用 Mod", 5),
    ];

    private bool useEnglish;

    public AdventureProfileViewModel(AdventureProfileSnapshot snapshot, bool useEnglish = false)
    {
        this.useEnglish = useEnglish;
        PlayerName = snapshot.PlayerName;
        Platform = snapshot.Platform;
        Difficulty = snapshot.Difficulty;
        HoursPlayed = Math.Max(0, snapshot.HoursPlayed);

        RegisterStatistic(CoreStatistics[0], FormatValue(snapshot.InGameDay));
        RegisterStatistic(CoreStatistics[1], FormatProgress(snapshot.MainStoryCompleted, snapshot.MainStoryTotal));
        RegisterStatistic(CoreStatistics[2], FormatProgress(snapshot.AchievementsUnlocked, snapshot.AchievementsTotal));
        RegisterStatistic(CoreStatistics[3], FormatProgress(snapshot.LocationsDiscovered, snapshot.LocationsTotal));
        RegisterStatistic(CoreStatistics[4], Math.Max(0, snapshot.SaveSlots).ToString());
        RegisterStatistic(CoreStatistics[5], Math.Max(0, snapshot.InstalledMods).ToString());
        UseLanguage(useEnglish);
    }

    public ObservableCollection<AdventureStatisticViewModel> Statistics { get; } = [];

    public ObservableCollection<AdventureSaveOptionViewModel> SaveOptions { get; } = [];

    public bool HasSaveOptions => SaveOptions.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayerInitial))]
    private string playerName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAccountAvatar))]
    private Bitmap? accountAvatar;

    public bool HasAccountAvatar => AccountAvatar is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSteamPlatform))]
    [NotifyPropertyChangedFor(nameof(IsGogPlatform))]
    private string platform;

    [ObservableProperty]
    private string difficulty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HoursText))]
    private int hoursPlayed;

    [ObservableProperty]
    private string title = string.Empty;

    [ObservableProperty]
    private string platformLabel = string.Empty;

    [ObservableProperty]
    private string difficultyLabel = string.Empty;

    [ObservableProperty]
    private string playtimeLabel = string.Empty;

    public string PlayerInitial => string.IsNullOrWhiteSpace(PlayerName)
        ? "?"
        : char.ToUpperInvariant(PlayerName.Trim()[0]).ToString();

    public bool IsSteamPlatform => Platform.Contains("Steam", StringComparison.OrdinalIgnoreCase);

    public bool IsGogPlatform => Platform.Contains("GOG", StringComparison.OrdinalIgnoreCase);

    public string HoursText => useEnglish
        ? $"{HoursPlayed} Hours"
        : $"{HoursPlayed} 小时";

    public static AdventureProfileViewModel CreateEmpty(bool useEnglish = false)
    {
        return new AdventureProfileViewModel(
            new AdventureProfileSnapshot(
                PlayerName: string.Empty,
                Platform: string.Empty,
                Difficulty: "--",
                HoursPlayed: 0,
                InGameDay: null,
                MainStoryCompleted: null,
                MainStoryTotal: null,
                AchievementsUnlocked: null,
                AchievementsTotal: null,
                LocationsDiscovered: null,
                LocationsTotal: null,
                SaveSlots: 0,
                InstalledMods: 0),
            useEnglish);
    }

    public static AdventureProfileViewModel CreateDefault(bool useEnglish = false) =>
        CreateEmpty(useEnglish);

    public void UseLanguage(bool useEnglish)
    {
        this.useEnglish = useEnglish;
        Title = useEnglish ? "Adventure Profile" : "冒险履历";
        PlatformLabel = useEnglish ? "Platform" : "游戏平台";
        DifficultyLabel = useEnglish ? "Difficulty" : "当前难度";
        PlaytimeLabel = useEnglish ? "Playtime" : "累计时长";
        OnPropertyChanged(nameof(HoursText));

        foreach (var statistic in Statistics)
        {
            statistic.UseLanguage(useEnglish);
        }
    }

    public void RegisterStatistic(IAdventureStatisticDefinition definition, string value)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Key);

        var existing = Statistics.FirstOrDefault(item =>
            string.Equals(item.Key, definition.Key, StringComparison.Ordinal));
        if (existing is not null)
        {
            existing.UpdateDefinition(definition, useEnglish);
            existing.Value = value;
        }
        else
        {
            Statistics.Add(new AdventureStatisticViewModel(definition, value, useEnglish));
        }

        var ordered = Statistics.OrderBy(item => item.DisplayOrder).ThenBy(item => item.Key).ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            var currentIndex = Statistics.IndexOf(ordered[index]);
            if (currentIndex != index)
            {
                Statistics.Move(currentIndex, index);
            }
        }
    }

    public void SetStatisticValue(string key, string value)
    {
        var statistic = Statistics.FirstOrDefault(item =>
            string.Equals(item.Key, key, StringComparison.Ordinal));
        if (statistic is not null)
        {
            statistic.Value = value;
        }
    }

    public void UpdateRuntimeData(string? platform, int hoursPlayed, int saveSlots, int installedMods)
    {
        if (!string.IsNullOrWhiteSpace(platform))
        {
            Platform = platform;
        }

        HoursPlayed = Math.Max(0, hoursPlayed);
        SetStatisticValue(AdventureStatisticKeys.SaveSlots, Math.Max(0, saveSlots).ToString());
        SetStatisticValue(AdventureStatisticKeys.InstalledMods, Math.Max(0, installedMods).ToString());
    }

    public void SetAccountIdentity(string? displayName, Bitmap? avatar)
    {
        PlayerName = displayName?.Trim() ?? string.Empty;
        AccountAvatar = avatar;
    }

    public void UpdateSaveOptions(
        IEnumerable<AdventureSaveOption> options,
        Guid? selectedId,
        Func<Guid, Task> openSave)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(openSave);

        var normalizedOptions = options
            .Where(option => option.Id != Guid.Empty && !string.IsNullOrWhiteSpace(option.DisplayName))
            .DistinctBy(option => option.Id)
            .ToArray();

        SaveOptions.Clear();
        foreach (var option in normalizedOptions)
        {
            SaveOptions.Add(new AdventureSaveOptionViewModel(option, option.Id == selectedId, openSave));
        }

        OnPropertyChanged(nameof(HasSaveOptions));
    }

    public void ApplyGameData(AdventureProfileData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (!string.IsNullOrWhiteSpace(data.Platform))
        {
            Platform = data.Platform;
        }

        Difficulty = string.IsNullOrWhiteSpace(data.Difficulty) ? "--" : data.Difficulty;
        HoursPlayed = Math.Max(0, data.HoursPlayed);
        SetStatisticValue(AdventureStatisticKeys.InGameDays, FormatValue(data.InGameDay));
        SetStatisticValue(AdventureStatisticKeys.MainStory, FormatProgress(data.MainStoryCompleted, data.MainStoryTotal));
        SetStatisticValue(AdventureStatisticKeys.Achievements, FormatProgress(data.AchievementsUnlocked, data.AchievementsTotal));
        SetStatisticValue(AdventureStatisticKeys.Exploration, FormatProgress(data.LocationsDiscovered, data.LocationsTotal));
        SetStatisticValue(AdventureStatisticKeys.SaveSlots, Math.Max(0, data.SaveSlots).ToString());
        SetStatisticValue(AdventureStatisticKeys.InstalledMods, Math.Max(0, data.InstalledMods).ToString());
    }

    public void UpdateInstalledMods(int installedMods)
    {
        SetStatisticValue(AdventureStatisticKeys.InstalledMods, Math.Max(0, installedMods).ToString());
    }

    public void ApplyPlayerStatistics(IEnumerable<PlayerStatistic> statistics)
    {
        ArgumentNullException.ThrowIfNull(statistics);
        var values = statistics.ToDictionary(item => item.Key, StringComparer.OrdinalIgnoreCase);

        Difficulty = values.TryGetValue(PlayerStatisticKeys.Difficulty, out var difficulty)
            && !string.IsNullOrWhiteSpace(difficulty.TextValue)
                ? difficulty.TextValue
                : "--";
        HoursPlayed = GetValue(values, PlayerStatisticKeys.HoursPlayed);
        SetStatisticValue(AdventureStatisticKeys.InGameDays, "--");
        SetProgressValue(values, AdventureStatisticKeys.MainStory, PlayerStatisticKeys.MainStory);
        SetProgressValue(values, AdventureStatisticKeys.Achievements, PlayerStatisticKeys.Achievements);
        SetProgressValue(values, AdventureStatisticKeys.Exploration, PlayerStatisticKeys.Exploration);
    }

    public void ResetPlayerStatistics()
    {
        Difficulty = "--";
        HoursPlayed = 0;
        SetStatisticValue(AdventureStatisticKeys.InGameDays, "--");
        SetStatisticValue(AdventureStatisticKeys.MainStory, "--");
        SetStatisticValue(AdventureStatisticKeys.Achievements, "--");
        SetStatisticValue(AdventureStatisticKeys.Exploration, "--");
        SetStatisticValue(AdventureStatisticKeys.SaveSlots, "0");
        SetStatisticValue(AdventureStatisticKeys.InstalledMods, "0");
    }

    private void SetProgressValue(
        IReadOnlyDictionary<string, PlayerStatistic> values,
        string displayKey,
        string statisticKey)
    {
        if (!values.TryGetValue(statisticKey, out var statistic))
        {
            SetStatisticValue(displayKey, "--");
            return;
        }

        SetStatisticValue(
            displayKey,
            FormatProgress(ToDisplayValue(statistic.Value), statistic.Total is null ? null : ToDisplayValue(statistic.Total.Value)));
    }

    private static int GetValue(
        IReadOnlyDictionary<string, PlayerStatistic> values,
        string key) =>
        values.TryGetValue(key, out var statistic)
            ? ToDisplayValue(statistic.Value)
            : 0;

    private static int ToDisplayValue(long value) =>
        (int)Math.Clamp(value, 0, int.MaxValue);

    private static string FormatProgress(int? completed, int? total)
    {
        if (completed is null && total is null)
        {
            return "--";
        }

        if (completed is null)
        {
            return total > 0 ? $"-- / {total.Value}" : "--";
        }

        if (total is null || total <= 0)
        {
            return Math.Max(0, completed.Value).ToString();
        }

        var safeTotal = Math.Max(0, total.Value);
        var safeCompleted = Math.Clamp(completed.Value, 0, safeTotal);
        return $"{safeCompleted} / {safeTotal}";
    }

    private static string FormatValue(int? value) => value is >= 0
        ? value.Value.ToString()
        : "--";
}
