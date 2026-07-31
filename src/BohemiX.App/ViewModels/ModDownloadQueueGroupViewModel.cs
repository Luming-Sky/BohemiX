using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using BohemiX.Core.Models;
using BohemiX.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BohemiX.App.ViewModels;

public sealed partial class ModDownloadQueueGroupViewModel : ViewModelBase
{
    public const string StandaloneGroupKey = "standalone";

    private readonly Action<ModDownloadQueueRowViewModel?> selectionChanged;
    private bool suppressSelectionChanged;

    public ModDownloadQueueGroupViewModel(
        string groupKey,
        Action<ModDownloadQueueRowViewModel?> selectionChanged)
    {
        GroupKey = groupKey;
        this.selectionChanged = selectionChanged;
    }

    public string GroupKey { get; }

    public ObservableCollection<ModDownloadQueueRowViewModel> Items { get; } = [];

    [ObservableProperty]
    private string titleText = string.Empty;

    [ObservableProperty]
    private string kindText = string.Empty;

    [ObservableProperty]
    private string summaryText = string.Empty;

    [ObservableProperty]
    private string countText = "0/0";

    [ObservableProperty]
    private bool isModPack;

    [ObservableProperty]
    private ModDownloadQueueRowViewModel? selectedItem;

    public static string GetGroupKey(ModDownloadQueueRowViewModel row) =>
        row.ModPackSessionId is { } sessionId
            ? $"modpack:{sessionId:N}"
            : StandaloneGroupKey;

    public void Update(
        IReadOnlyList<ModDownloadQueueRowViewModel> rows,
        Func<string, string> translate)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(translate);

        ReplaceRows(rows);
        var first = rows.FirstOrDefault();
        IsModPack = first?.IsModPackDownload == true;
        TitleText = IsModPack
            ? FirstNonEmpty(first?.ModPackName, first?.ModPackId, translate("ModPackSource"))
            : translate("StandaloneDownloads");
        KindText = IsModPack
            ? translate("ModPackDownloadSection")
            : translate("StandaloneDownloadSection");

        var completed = rows.Count(row => row.Status == ModDownloadStatus.Completed);
        var active = rows.Count(row => ModDownloadQueuePolicy.IsActive(row.Status));
        var attention = rows.Count(row => ModDownloadQueuePolicy.RequiresAttention(row.Status));
        SummaryText = string.Format(
            CultureInfo.CurrentCulture,
            translate("DownloadQueueGroupSummary"),
            active,
            completed,
            attention);
        CountText = $"{completed.ToString(CultureInfo.InvariantCulture)}/{rows.Count.ToString(CultureInfo.InvariantCulture)}";
    }

    public void SyncSelectedItem(ModDownloadQueueRowViewModel? selectedItem)
    {
        suppressSelectionChanged = true;
        try
        {
            SelectedItem = selectedItem is not null && Items.Contains(selectedItem)
                ? selectedItem
                : null;
        }
        finally
        {
            suppressSelectionChanged = false;
        }
    }

    partial void OnSelectedItemChanged(ModDownloadQueueRowViewModel? value)
    {
        if (!suppressSelectionChanged && value is not null)
        {
            selectionChanged(value);
        }
    }

    private void ReplaceRows(IReadOnlyList<ModDownloadQueueRowViewModel> rows)
    {
        for (var index = Items.Count - 1; index >= 0; index--)
        {
            if (!rows.Contains(Items[index]))
            {
                Items.RemoveAt(index);
            }
        }

        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var currentIndex = Items.IndexOf(row);
            if (currentIndex < 0)
            {
                Items.Insert(index, row);
            }
            else if (currentIndex != index)
            {
                Items.Move(currentIndex, index);
            }
        }
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.First(value => !string.IsNullOrWhiteSpace(value))!.Trim();
}
