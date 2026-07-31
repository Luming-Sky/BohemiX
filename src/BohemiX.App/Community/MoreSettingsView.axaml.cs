using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace BohemiX.App.Community;

public partial class MoreSettingsView : UserControl
{
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<MoreSettingsView, string>(nameof(Title), string.Empty);

    public static readonly StyledProperty<bool> IsStripeMotionEnabledProperty =
        AvaloniaProperty.Register<MoreSettingsView, bool>(nameof(IsStripeMotionEnabled), true);

    public MoreSettingsView()
    {
        InitializeComponent();
    }

    public string Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public bool IsStripeMotionEnabled
    {
        get => GetValue(IsStripeMotionEnabledProperty);
        set => SetValue(IsStripeMotionEnabledProperty, value);
    }

    private void OnEchoCavePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control control
            || !e.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
        {
            return;
        }

        AdvanceEchoPreview();
        e.Handled = true;
    }

    private void OnEchoCaveKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Space))
        {
            return;
        }

        AdvanceEchoPreview();
        e.Handled = true;
    }

    private void AdvanceEchoPreview()
    {
        if (DataContext is MoreSettingsViewModel viewModel
            && viewModel.NextEchoPreviewCommand.CanExecute(null))
        {
            viewModel.NextEchoPreviewCommand.Execute(null);
        }
    }
}
