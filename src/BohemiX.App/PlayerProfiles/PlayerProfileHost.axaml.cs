using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace BohemiX.App.PlayerProfiles;

public partial class PlayerProfileHost : UserControl
{
    private bool isDraggingAvatarCrop;
    private Point avatarCropDragStart;
    private double avatarCropHorizontalOffsetStart;
    private double avatarCropVerticalOffsetStart;

    public PlayerProfileHost()
    {
        InitializeComponent();
    }

    private void AvatarCropSurface_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control surface
            || DataContext is not PlayerProfileManagerViewModel viewModel
            || !e.GetCurrentPoint(surface).Properties.IsLeftButtonPressed)
        {
            return;
        }

        isDraggingAvatarCrop = true;
        avatarCropDragStart = e.GetPosition(surface);
        avatarCropHorizontalOffsetStart = viewModel.EditorAvatarCropHorizontalOffset;
        avatarCropVerticalOffsetStart = viewModel.EditorAvatarCropVerticalOffset;
        e.Pointer.Capture(surface);
        e.Handled = true;
    }

    private void AvatarCropSurface_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!isDraggingAvatarCrop
            || sender is not Control surface
            || DataContext is not PlayerProfileManagerViewModel viewModel)
        {
            return;
        }

        var delta = e.GetPosition(surface) - avatarCropDragStart;
        viewModel.EditorAvatarCropHorizontalOffset = viewModel.EditorAvatarCropPreviewMaxOffsetX > 0
            ? Math.Clamp(
                avatarCropHorizontalOffsetStart - (delta.X / viewModel.EditorAvatarCropPreviewMaxOffsetX),
                -1d,
                1d)
            : 0d;
        viewModel.EditorAvatarCropVerticalOffset = viewModel.EditorAvatarCropPreviewMaxOffsetY > 0
            ? Math.Clamp(
                avatarCropVerticalOffsetStart - (delta.Y / viewModel.EditorAvatarCropPreviewMaxOffsetY),
                -1d,
                1d)
            : 0d;
        e.Handled = true;
    }

    private void AvatarCropSurface_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!isDraggingAvatarCrop || sender is not Control surface)
        {
            return;
        }

        isDraggingAvatarCrop = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void AvatarCropSurface_OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (DataContext is not PlayerProfileManagerViewModel viewModel || e.Delta.Y == 0)
        {
            return;
        }

        const double zoomStep = 0.12d;
        viewModel.EditorAvatarCropZoom = Math.Clamp(
            viewModel.EditorAvatarCropZoom + (e.Delta.Y * zoomStep),
            1d,
            3d);
        e.Handled = true;
    }
}
