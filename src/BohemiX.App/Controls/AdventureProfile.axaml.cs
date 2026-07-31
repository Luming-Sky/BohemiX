using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace BohemiX.App.Controls;

public partial class AdventureProfile : UserControl
{
    private const double CompactLayoutWidth = 1120;
    private static readonly Thickness StandardContentMargin = new(22, 18, 292, 12);
    private static readonly Thickness CompactContentMargin = new(22, 18, 22, 12);

    public AdventureProfile()
    {
        InitializeComponent();
        SizeChanged += AdventureProfile_OnSizeChanged;
    }

    private void AdventureProfile_OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        var isCompact = e.NewSize.Width < CompactLayoutWidth;

        AdventureProfilePortraitLayer.IsVisible = !isCompact;
        AdventureProfilePortraitAtmosphere.IsVisible = !isCompact;
        AdventureProfileContent.Margin = isCompact ? CompactContentMargin : StandardContentMargin;
    }
}
