using CommunityToolkit.Mvvm.ComponentModel;

namespace BohemiX.App.ViewModels;

public sealed partial class ModDownloadCategoryFilterViewModel : ViewModelBase
{
    public ModDownloadCategoryFilterViewModel(string key, string displayName)
    {
        Key = key;
        DisplayName = displayName;
    }

    public string Key { get; }

    [ObservableProperty]
    private string displayName;

    [ObservableProperty]
    private bool isSelected;
}
