using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;

namespace BohemiX.App.Views;

public partial class BackgroundCropWindow : Window
{
    private Bitmap? bitmap;
    private double cropX;
    private double cropY;
    private double cropWidth;
    private double cropHeight;
    private Rect imageRect;
    private Point dragStart;
    private double dragStartCropX;
    private double dragStartCropY;
    private double dragStartCropWidth;
    private double dragStartCropHeight;
    private DragMode dragMode;
    private bool isDragging;
    private bool isNewSelectionArmed;

    private enum DragMode
    {
        None,
        NewSelection,
        Move,
        ResizeLeft,
        ResizeRight,
        ResizeTop,
        ResizeBottom,
        ResizeTopLeft,
        ResizeTopRight,
        ResizeBottomLeft,
        ResizeBottomRight
    }

    public BackgroundCropWindow()
    {
        InitializeComponent();
        PreviewCanvas.SizeChanged += PreviewCanvas_OnSizeChanged;
    }

    public BackgroundCropWindow(
        string imagePath,
        double cropX,
        double cropY,
        double cropWidth,
        double cropHeight) : this()
    {
        bitmap = new Bitmap(imagePath);
        PreviewImage.Source = bitmap;
        this.cropX = Math.Clamp(cropX, 0, 1);
        this.cropY = Math.Clamp(cropY, 0, 1);
        this.cropWidth = Math.Clamp(cropWidth, 0.01, 1);
        this.cropHeight = Math.Clamp(cropHeight, 0.01, 1);
        NormalizeCrop();
        Closed += (_, _) => bitmap?.Dispose();
    }

    public double CropX => cropX;
    public double CropY => cropY;
    public double CropWidth => cropWidth;
    public double CropHeight => cropHeight;

    private void PreviewCanvas_OnSizeChanged(object? sender, SizeChangedEventArgs e) => LayoutPreview();

    private void LayoutPreview()
    {
        if (bitmap is null || PreviewCanvas.Bounds.Width <= 0 || PreviewCanvas.Bounds.Height <= 0)
        {
            return;
        }

        var scale = Math.Min(
            PreviewCanvas.Bounds.Width / bitmap.Size.Width,
            PreviewCanvas.Bounds.Height / bitmap.Size.Height);
        var width = bitmap.Size.Width * scale;
        var height = bitmap.Size.Height * scale;
        var left = (PreviewCanvas.Bounds.Width - width) / 2;
        var top = (PreviewCanvas.Bounds.Height - height) / 2;
        imageRect = new Rect(left, top, width, height);

        PreviewImage.Width = width;
        PreviewImage.Height = height;
        Canvas.SetLeft(PreviewImage, left);
        Canvas.SetTop(PreviewImage, top);
        UpdateSelectionOverlay();
    }

    private void PreviewCanvas_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(PreviewCanvas).Properties.IsLeftButtonPressed || !imageRect.Contains(e.GetPosition(PreviewCanvas)))
        {
            return;
        }

        var point = ClampToImage(e.GetPosition(PreviewCanvas));
        var selectionRect = GetSelectionRect();
        var resizeMode = GetResizeMode(point, selectionRect);
        if (isNewSelectionArmed)
        {
            dragMode = DragMode.NewSelection;
            isNewSelectionArmed = false;
            dragStart = point;
        }
        else if (resizeMode != DragMode.None)
        {
            dragMode = resizeMode;
            dragStart = point;
            dragStartCropX = cropX;
            dragStartCropY = cropY;
            dragStartCropWidth = cropWidth;
            dragStartCropHeight = cropHeight;
        }
        else if (selectionRect.Contains(point))
        {
            dragMode = DragMode.Move;
            dragStart = point;
            dragStartCropX = cropX;
            dragStartCropY = cropY;
            dragStartCropWidth = cropWidth;
            dragStartCropHeight = cropHeight;
        }
        else
        {
            dragMode = DragMode.NewSelection;
            dragStart = point;
        }

        isDragging = true;
        PreviewCanvas.PointerCaptureLost += PreviewCanvas_OnPointerCaptureLost;
        e.Pointer.Capture(PreviewCanvas);
    }

    private void PreviewCanvas_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (isDragging)
        {
            UpdateDrag(ClampToImage(e.GetPosition(PreviewCanvas)));
        }
    }

    private void PreviewCanvas_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!isDragging)
        {
            return;
        }

        UpdateDrag(ClampToImage(e.GetPosition(PreviewCanvas)));
        isDragging = false;
        dragMode = DragMode.None;
        e.Pointer.Capture(null);
        PreviewCanvas.PointerCaptureLost -= PreviewCanvas_OnPointerCaptureLost;
    }

    private void PreviewCanvas_OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        isDragging = false;
        dragMode = DragMode.None;
        PreviewCanvas.PointerCaptureLost -= PreviewCanvas_OnPointerCaptureLost;
    }

    private Point ClampToImage(Point point) => new(
        Math.Clamp(point.X, imageRect.Left, imageRect.Right),
        Math.Clamp(point.Y, imageRect.Top, imageRect.Bottom));

    private Rect GetSelectionRect() => new(
        imageRect.Left + (cropX * imageRect.Width),
        imageRect.Top + (cropY * imageRect.Height),
        cropWidth * imageRect.Width,
        cropHeight * imageRect.Height);

    private DragMode GetResizeMode(Point point, Rect selectionRect)
    {
        const double handleHitSize = 18;
        var nearLeft = Math.Abs(point.X - selectionRect.Left) <= handleHitSize;
        var nearRight = Math.Abs(point.X - selectionRect.Right) <= handleHitSize;
        var nearTop = Math.Abs(point.Y - selectionRect.Top) <= handleHitSize;
        var nearBottom = Math.Abs(point.Y - selectionRect.Bottom) <= handleHitSize;

        if (nearTop && nearLeft) return DragMode.ResizeTopLeft;
        if (nearTop && nearRight) return DragMode.ResizeTopRight;
        if (nearBottom && nearLeft) return DragMode.ResizeBottomLeft;
        if (nearBottom && nearRight) return DragMode.ResizeBottomRight;
        if (nearLeft && point.Y >= selectionRect.Top - handleHitSize && point.Y <= selectionRect.Bottom + handleHitSize) return DragMode.ResizeLeft;
        if (nearRight && point.Y >= selectionRect.Top - handleHitSize && point.Y <= selectionRect.Bottom + handleHitSize) return DragMode.ResizeRight;
        if (nearTop && point.X >= selectionRect.Left - handleHitSize && point.X <= selectionRect.Right + handleHitSize) return DragMode.ResizeTop;
        if (nearBottom && point.X >= selectionRect.Left - handleHitSize && point.X <= selectionRect.Right + handleHitSize) return DragMode.ResizeBottom;
        return DragMode.None;
    }

    private void UpdateDrag(Point point)
    {
        var dx = (point.X - dragStart.X) / imageRect.Width;
        var dy = (point.Y - dragStart.Y) / imageRect.Height;

        switch (dragMode)
        {
            case DragMode.NewSelection:
                UpdateSelection(point);
                return;
            case DragMode.Move:
                cropX = Math.Clamp(dragStartCropX + dx, 0, 1 - dragStartCropWidth);
                cropY = Math.Clamp(dragStartCropY + dy, 0, 1 - dragStartCropHeight);
                break;
            case DragMode.ResizeLeft:
                cropX = Math.Clamp(dragStartCropX + dx, 0, dragStartCropX + dragStartCropWidth - 0.01);
                cropWidth = dragStartCropWidth + (dragStartCropX - cropX);
                break;
            case DragMode.ResizeRight:
                cropWidth = Math.Clamp(dragStartCropWidth + dx, 0.01, 1 - dragStartCropX);
                break;
            case DragMode.ResizeTop:
                cropY = Math.Clamp(dragStartCropY + dy, 0, dragStartCropY + dragStartCropHeight - 0.01);
                cropHeight = dragStartCropHeight + (dragStartCropY - cropY);
                break;
            case DragMode.ResizeBottom:
                cropHeight = Math.Clamp(dragStartCropHeight + dy, 0.01, 1 - dragStartCropY);
                break;
            case DragMode.ResizeTopLeft:
                cropX = Math.Clamp(dragStartCropX + dx, 0, dragStartCropX + dragStartCropWidth - 0.01);
                cropY = Math.Clamp(dragStartCropY + dy, 0, dragStartCropY + dragStartCropHeight - 0.01);
                cropWidth = dragStartCropWidth + (dragStartCropX - cropX);
                cropHeight = dragStartCropHeight + (dragStartCropY - cropY);
                break;
            case DragMode.ResizeTopRight:
                cropY = Math.Clamp(dragStartCropY + dy, 0, dragStartCropY + dragStartCropHeight - 0.01);
                cropWidth = Math.Clamp(dragStartCropWidth + dx, 0.01, 1 - dragStartCropX);
                cropHeight = dragStartCropHeight + (dragStartCropY - cropY);
                break;
            case DragMode.ResizeBottomLeft:
                cropX = Math.Clamp(dragStartCropX + dx, 0, dragStartCropX + dragStartCropWidth - 0.01);
                cropWidth = dragStartCropWidth + (dragStartCropX - cropX);
                cropHeight = Math.Clamp(dragStartCropHeight + dy, 0.01, 1 - dragStartCropY);
                break;
            case DragMode.ResizeBottomRight:
                cropWidth = Math.Clamp(dragStartCropWidth + dx, 0.01, 1 - dragStartCropX);
                cropHeight = Math.Clamp(dragStartCropHeight + dy, 0.01, 1 - dragStartCropY);
                break;
        }

        NormalizeCrop();
        UpdateSelectionOverlay();
    }

    private void UpdateSelection(Point end)
    {
        var left = Math.Min(dragStart.X, end.X);
        var top = Math.Min(dragStart.Y, end.Y);
        var right = Math.Max(dragStart.X, end.X);
        var bottom = Math.Max(dragStart.Y, end.Y);
        cropX = Math.Clamp((left - imageRect.Left) / imageRect.Width, 0, 1);
        cropY = Math.Clamp((top - imageRect.Top) / imageRect.Height, 0, 1);
        cropWidth = Math.Clamp((right - left) / imageRect.Width, 0.01, 1);
        cropHeight = Math.Clamp((bottom - top) / imageRect.Height, 0.01, 1);
        NormalizeCrop();
        UpdateSelectionOverlay();
    }

    private void UpdateSelectionOverlay()
    {
        if (imageRect.Width <= 0 || imageRect.Height <= 0)
        {
            return;
        }

        Canvas.SetLeft(SelectionOverlay, imageRect.Left + (cropX * imageRect.Width));
        Canvas.SetTop(SelectionOverlay, imageRect.Top + (cropY * imageRect.Height));
        SelectionOverlay.Width = cropWidth * imageRect.Width;
        SelectionOverlay.Height = cropHeight * imageRect.Height;

        var left = imageRect.Left + (cropX * imageRect.Width);
        var top = imageRect.Top + (cropY * imageRect.Height);
        var right = left + (cropWidth * imageRect.Width);
        var bottom = top + (cropHeight * imageRect.Height);
        SetHandle(HandleTopLeft, left, top);
        SetHandle(HandleTop, (left + right) / 2, top);
        SetHandle(HandleTopRight, right, top);
        SetHandle(HandleRight, right, (top + bottom) / 2);
        SetHandle(HandleBottomRight, right, bottom);
        SetHandle(HandleBottom, (left + right) / 2, bottom);
        SetHandle(HandleBottomLeft, left, bottom);
        SetHandle(HandleLeft, left, (top + bottom) / 2);
    }

    private static void SetHandle(Control handle, double centerX, double centerY)
    {
        Canvas.SetLeft(handle, centerX - (handle.Width / 2));
        Canvas.SetTop(handle, centerY - (handle.Height / 2));
    }

    private void NormalizeCrop()
    {
        cropWidth = Math.Clamp(cropWidth, 0.01, 1);
        cropHeight = Math.Clamp(cropHeight, 0.01, 1);
        cropX = Math.Clamp(cropX, 0, 1 - cropWidth);
        cropY = Math.Clamp(cropY, 0, 1 - cropHeight);
    }

    private void NewSelectionButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        isNewSelectionArmed = true;
        dragMode = DragMode.None;
    }

    private void CancelButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close(false);

    private void ConfirmButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close(true);
}
