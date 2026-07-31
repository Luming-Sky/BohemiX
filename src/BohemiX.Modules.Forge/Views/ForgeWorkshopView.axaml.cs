using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BohemiX.Modules.Forge.Controls;
using BohemiX.Modules.Forge.Input;
using BohemiX.Modules.Forge.Models;
using BohemiX.Modules.Forge.ViewModels;

namespace BohemiX.Modules.Forge.Views;

public partial class ForgeWorkshopView : UserControl
{
    private ForgeOpenGlControl? forgeSurface;
    private TopLevel? inputTopLevel;

    public ForgeWorkshopView()
    {
        AvaloniaXamlLoader.Load(this);
        forgeSurface = this.FindControl<ForgeOpenGlControl>("ForgeSurface");
        AddHandler(PointerPressedEvent, OnViewPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerMovedEvent, OnViewPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnViewPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerExitedEvent, OnViewPointerExited, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    public void RequestRenderQuality(ForgeRenderQuality quality) => forgeSurface?.RequestRenderQuality(quality);

    public void PrepareForRemoval() => forgeSurface?.PrepareForRemoval();

    private void OnViewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (IsOverlayCommandSource(e.Source))
        {
            return;
        }

        var surface = forgeSurface;
        if (surface is not null && surface.TryBeginPrimaryInput(e.GetPosition(surface), e.Pointer))
        {
            e.Handled = true;
        }
    }

    private void OnViewPointerMoved(object? sender, PointerEventArgs e)
    {
        if (IsOverlayCommandSource(e.Source))
        {
            return;
        }

        var surface = forgeSurface;
        if (surface is null)
        {
            return;
        }

        var pointer = e.GetCurrentPoint(surface);
        if (pointer.Properties.IsLeftButtonPressed)
        {
            if (surface.UpdatePrimaryInput(e.GetPosition(surface)))
            {
                e.Handled = true;
            }
            return;
        }
        if (pointer.Properties.IsRightButtonPressed)
        {
            return;
        }

        surface.TrackHammerHover(e.GetPosition(surface));
    }

    private void OnViewPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (IsOverlayCommandSource(e.Source))
        {
            Dispatcher.UIThread.Post(RequestGameplayFocus, DispatcherPriority.Input);
            return;
        }

        var surface = forgeSurface;
        if (surface is not null && surface.TryEndPrimaryInput(e.GetPosition(surface), e.Pointer))
        {
            e.Handled = true;
        }
    }

    private void OnViewPointerExited(object? sender, PointerEventArgs e)
    {
        forgeSurface?.ClearHammerHover();
    }

    private void OnViewKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not ForgeWorkshopViewModel viewModel)
        {
            return;
        }

        if (!IsEffectivelyVisible || !ForgeKeyboardMap.TryResolve(e, out var action))
        {
            return;
        }

        if (action != ForgeInputAction.TogglePause && IsOverlayCommandSource(e.Source))
        {
            return;
        }

        if (viewModel.HandleKeyboardAction(action))
        {
            e.Handled = true;
        }
    }

    private static bool IsOverlayCommandSource(object? source)
    {
        if (source is not Visual visual)
        {
            return false;
        }

        return visual is Button or Slider ||
               visual.GetVisualAncestors().Any(ancestor => ancestor is Button or Slider);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        inputTopLevel = TopLevel.GetTopLevel(this);
        if (inputTopLevel is not null)
        {
            inputTopLevel.AddHandler(KeyDownEvent, OnViewKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
            if (inputTopLevel is Window window)
            {
                window.Activated += OnWindowActivated;
            }
        }

        if (DataContext is ForgeWorkshopViewModel viewModel)
        {
            viewModel.Start();
        }

        Dispatcher.UIThread.Post(RequestGameplayFocus, DispatcherPriority.Input);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (inputTopLevel is not null)
        {
            inputTopLevel.RemoveHandler(KeyDownEvent, OnViewKeyDown);
            if (inputTopLevel is Window window)
            {
                window.Activated -= OnWindowActivated;
            }
            inputTopLevel = null;
        }

        PrepareForRemoval();
        forgeSurface?.ClearHammerHover();
        if (DataContext is ForgeWorkshopViewModel viewModel)
        {
            viewModel.Stop();
        }
        base.OnDetachedFromVisualTree(e);
    }

    private void OnWindowActivated(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(RequestGameplayFocus, DispatcherPriority.Input);

    private void RequestGameplayFocus()
    {
        if (!IsEffectivelyVisible || DataContext is ForgeWorkshopViewModel { IsExitConfirmationOpen: true })
        {
            return;
        }

        forgeSurface?.Focus(NavigationMethod.Unspecified);
    }
}
