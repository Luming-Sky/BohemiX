using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BohemiX.App.ViewModels;

/// <summary>
/// A selectable card on the Lab workspace that opens one of the KCD2 gameplay
/// simulation modules (alchemy, forging, swordsmanship, ...).
/// </summary>
public partial class LabModuleCardViewModel : ViewModelBase
{
    public LabModuleCardViewModel(string moduleKey, string title, string status, StreamGeometry iconData, bool isAvailable)
    {
        ModuleKey = moduleKey;
        this.title = title;
        Status = status;
        IconData = iconData;
        IsAvailable = isAvailable;
    }

    /// <summary>
    /// Stable identifier used to route <see cref="SelectCommand"/> back to the
    /// owning view model (e.g. "Alchemy" or "Forging").
    /// </summary>
    public string ModuleKey { get; }

    [ObservableProperty]
    private string title;

    [ObservableProperty]
    private string status;

    public StreamGeometry IconData { get; }

    public bool IsAvailable { get; }

    [ObservableProperty]
    private bool isActive;

    /// <summary>
    /// Accent colour applied to the card border when it is the selected module.
    /// Resolved from the shared application resource dictionary by the host.
    /// </summary>
    [ObservableProperty]
    private IBrush? accentBrush;

    /// <summary>
    /// Border thickness: 2 when active, 0 otherwise. Kept as a property so the
    /// XAML can bind <c>BorderThickness</c> directly without a converter.
    /// </summary>
    [ObservableProperty]
    private Thickness highlightThickness;

    /// <summary>
    /// Backing command that the host wires up to its module-selection handler.
    /// </summary>
    public IRelayCommand SelectCommand { get; set; } = null!;
}
