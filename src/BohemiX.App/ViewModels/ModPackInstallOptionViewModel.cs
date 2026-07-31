using System;
using BohemiX.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BohemiX.App.ViewModels;

public sealed partial class ModPackInstallOptionViewModel : ViewModelBase
{
    private readonly Action selectionChanged;

    public ModPackInstallOptionViewModel(ModPackInstallPlanItem item, Action selectionChanged)
    {
        Item = item;
        this.selectionChanged = selectionChanged;
        isSelected = !item.IsOptional || item.Support == ModPackInstallItemSupport.Supported;
    }

    public ModPackInstallPlanItem Item { get; }

    public string Id => Item.Id;

    public string Name => Item.Name;

    public bool IsRequired => !Item.IsOptional;

    public bool IsSupported => Item.Support == ModPackInstallItemSupport.Supported;

    public bool CanSelect => Item.IsOptional && IsSupported;

    public string RequirementText => IsRequired ? "必选" : "可选";

    public string SourceText => Item.Source switch
    {
        ModPackInstallItemSource.Nexus => "Nexus",
        ModPackInstallItemSource.Bundle => "合集内置",
        ModPackInstallItemSource.Direct => "Direct",
        ModPackInstallItemSource.Browse => "Browse",
        ModPackInstallItemSource.Manual => "Manual",
        _ => "未知来源"
    };

    public string PhaseText => $"阶段 {Item.Phase}";

    public string SizeText => Item.FileSizeInBytes is > 0 ? FormatSize(Item.FileSizeInBytes.Value) : "大小未知";

    public string SupportText => IsSupported ? "可在软件内安装" : Item.SupportMessage;

    [ObservableProperty]
    private bool isSelected;

    partial void OnIsSelectedChanged(bool value) => selectionChanged();

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.##} {units[unit]}";
    }
}
