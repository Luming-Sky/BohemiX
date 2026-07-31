using CommunityToolkit.Mvvm.ComponentModel;

namespace BohemiX.App.ViewModels;

public sealed partial class ModRecommendationQualifierViewModel : ViewModelBase
{
    public ModRecommendationQualifierViewModel(string value, string? displayName = null)
    {
        Value = value;
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? value : displayName;
    }

    [ObservableProperty]
    private string value;

    [ObservableProperty]
    private string displayName;

    [ObservableProperty]
    private bool isSelected;
}
