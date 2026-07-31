using CommunityToolkit.Mvvm.ComponentModel;

namespace BohemiX.App.ViewModels;

public partial class ShellSectionViewModel : ViewModelBase
{
    public ShellSectionViewModel(string title, string summary, string detail)
    {
        Title = title;
        Summary = summary;
        Detail = detail;
    }

    public string Title { get; }

    public string Summary { get; }

    [ObservableProperty]
    private string detail;
}

