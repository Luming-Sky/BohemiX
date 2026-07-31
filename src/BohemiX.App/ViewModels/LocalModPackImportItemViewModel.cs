using System;
using BohemiX.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BohemiX.App.ViewModels;

public sealed partial class LocalModPackImportItemViewModel : ViewModelBase
{
    private readonly Action changed;
    private readonly Func<string, string> translate;

    public LocalModPackImportItemViewModel(
        ModPackImportItem item,
        Func<string, string> translate,
        Action changed)
    {
        Item = item;
        this.translate = translate;
        this.changed = changed;
        isSelected = item.State == ModPackImportItemState.New;
    }

    public ModPackImportItem Item { get; }

    public string Id => Item.Id;

    public string DisplayName => Item.DisplayName;

    public string DetailText => string.IsNullOrWhiteSpace(Item.Author)
        ? $"{Item.Version}  |  {Item.RelativePath}"
        : $"{Item.Version}  |  {Item.Author}  |  {Item.RelativePath}";

    public bool IsExisting => Item.State == ModPackImportItemState.AlreadyInstalled;

    public bool IsInvalid => Item.State is ModPackImportItemState.Invalid or ModPackImportItemState.DuplicateInPackage;

    public bool CanSelect => !IsInvalid;

    public string StateText => Item.State switch
    {
        ModPackImportItemState.New => translate("LocalModPackItemNew"),
        ModPackImportItemState.AlreadyInstalled => translate("LocalModPackItemExisting"),
        ModPackImportItemState.DuplicateInPackage => translate("LocalModPackItemDuplicate"),
        _ => translate("LocalModPackItemInvalid")
    };

    public string StatusText => Item.StatusMessage;

    public ModPackImportAction Action => IsInvalid || !IsSelected
        ? ModPackImportAction.Skip
        : IsExisting && IsReplace
            ? ModPackImportAction.Replace
            : IsExisting
                ? ModPackImportAction.Skip
                : ModPackImportAction.Install;

    [ObservableProperty]
    private bool isSelected;

    [ObservableProperty]
    private bool isReplace;

    partial void OnIsSelectedChanged(bool value)
    {
        if (!value && IsReplace)
        {
            IsReplace = false;
        }

        OnPropertyChanged(nameof(Action));
        changed();
    }

    partial void OnIsReplaceChanged(bool value)
    {
        if (value && !IsSelected)
        {
            IsSelected = true;
        }

        OnPropertyChanged(nameof(Action));
        changed();
    }
}
