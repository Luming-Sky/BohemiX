using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BohemiX.App.ViewModels;

public sealed partial class InstalledModGroupViewModel : ObservableObject
{
    public const string StandaloneGroupKey = "standalone";
    public const string CustomGroupKeyPrefix = "custom:";
    public const int MaxCustomNameLength = 48;

    public InstalledModGroupViewModel(
        string groupKey,
        IReadOnlyList<ModListItemViewModel> items,
        Func<string, string> translate,
        string? customName = null,
        ModListItemViewModel? sourceRow = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupKey);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(translate);

        GroupKey = groupKey;
        Items = new ObservableCollection<ModListItemViewModel>(items);
        var first = items.FirstOrDefault() ?? sourceRow;
        var modPackName = items
            .Select(item => item.ModPackName)
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))
            ?? sourceRow?.ModPackName;
        IsCustom = IsCustomGroupKey(groupKey);
        IsModPack = !IsCustom && first?.IsInstalledFromModPack == true;
        DefaultTitleText = IsCustom
            ? translate("CustomInstalledMods")
            : IsModPack
            ? FirstNonEmpty(modPackName, first?.ModPackId, translate("ModPackSource"))
            : translate("StandaloneInstalledMods");
        var normalizedCustomName = NormalizeCustomName(customName);
        HasCustomName = normalizedCustomName is not null;
        TitleText = normalizedCustomName ?? DefaultTitleText;
        KindText = IsCustom
            ? translate("CustomInstalledSection")
            : IsModPack
            ? translate("ModPackInstalledSection")
            : translate("StandaloneInstalledSection");
        CountText = string.Format(
            CultureInfo.CurrentCulture,
            translate("InstalledModGroupCount"),
            items.Count);
    }

    public string GroupKey { get; }

    public ObservableCollection<ModListItemViewModel> Items { get; }

    public string TitleText { get; }

    public string DefaultTitleText { get; }

    public string KindText { get; }

    public string CountText { get; }

    public bool IsModPack { get; }

    public bool IsCustom { get; }

    public bool IsEmpty => Items.Count == 0;

    public bool HasItems => !IsEmpty;

    public bool CanDelete => IsCustom;

    public bool HasCustomName { get; }

    public void SetEnabledState(bool isEnabled)
    {
        foreach (var item in Items)
        {
            item.IsEnabled = item.IsBuiltIn || isEnabled;
        }
    }

    [ObservableProperty]
    private bool isNameEditing;

    [ObservableProperty]
    private bool isDropTargetActive;

    public bool CanAcceptDrop(ModListItemViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (IsCustom)
        {
            return Items.All(item => !string.Equals(item.Id, row.Id, StringComparison.OrdinalIgnoreCase));
        }

        return row.IsAssignedToCustomGroup
            && string.Equals(GroupKey, GetGroupKey(row), StringComparison.OrdinalIgnoreCase);
    }

    public static string GetGroupKey(ModListItemViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (!row.IsInstalledFromModPack)
        {
            return StandaloneGroupKey;
        }

        return $"modpack:{row.ModPackId}";
    }

    public static string GetEffectiveGroupKey(
        ModListItemViewModel row,
        IReadOnlyDictionary<string, string> assignments,
        IReadOnlyDictionary<string, string> groupNames)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(assignments);
        ArgumentNullException.ThrowIfNull(groupNames);

        return assignments.TryGetValue(row.Id, out var assignedGroupKey)
               && IsCustomGroupKey(assignedGroupKey)
               && groupNames.ContainsKey(assignedGroupKey)
            ? assignedGroupKey
            : GetGroupKey(row);
    }

    public static bool IsCustomGroupKey(string? groupKey) =>
        !string.IsNullOrWhiteSpace(groupKey)
        && groupKey.StartsWith(CustomGroupKeyPrefix, StringComparison.OrdinalIgnoreCase);

    public static string CreateCustomGroupKey() =>
        $"{CustomGroupKeyPrefix}{Guid.NewGuid():N}";

    public static string? NormalizeCustomName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        return normalized.Length <= MaxCustomNameLength
            ? normalized
            : normalized[..MaxCustomNameLength];
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.First(value => !string.IsNullOrWhiteSpace(value))!.Trim();
}
