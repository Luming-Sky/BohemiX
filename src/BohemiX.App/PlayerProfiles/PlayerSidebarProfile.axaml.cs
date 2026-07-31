using Avalonia;
using Avalonia.Controls;

namespace BohemiX.App.PlayerProfiles;

public partial class PlayerSidebarProfile : UserControl
{
    public static readonly StyledProperty<bool> IsSidebarExpandedProperty =
        AvaloniaProperty.Register<PlayerSidebarProfile, bool>(nameof(IsSidebarExpanded), true);

    public PlayerSidebarProfile()
    {
        InitializeComponent();
        UpdateSidebarStatePseudoClasses(IsSidebarExpanded);
    }

    public bool IsSidebarExpanded
    {
        get => GetValue(IsSidebarExpandedProperty);
        set => SetValue(IsSidebarExpandedProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsSidebarExpandedProperty && change.NewValue is bool isExpanded)
        {
            UpdateSidebarStatePseudoClasses(isExpanded);
        }
    }

    private void UpdateSidebarStatePseudoClasses(bool isExpanded)
    {
        PseudoClasses.Set(":expanded", isExpanded);
        PseudoClasses.Set(":collapsed", !isExpanded);
    }
}
