using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace BohemiX.App.Controls;

public sealed class SmoothCornerBorder : Decorator
{
    private const double ContinuousCornerControl = 0.72;
    private StreamGeometry? shape;
    private StreamGeometry? clipShape;

    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        AvaloniaProperty.Register<SmoothCornerBorder, IBrush?>(nameof(Background));

    public static readonly StyledProperty<IBrush?> BorderBrushProperty =
        AvaloniaProperty.Register<SmoothCornerBorder, IBrush?>(nameof(BorderBrush));

    public static readonly StyledProperty<Thickness> BorderThicknessProperty =
        AvaloniaProperty.Register<SmoothCornerBorder, Thickness>(nameof(BorderThickness));

    public static readonly StyledProperty<double> CornerRadiusProperty =
        AvaloniaProperty.Register<SmoothCornerBorder, double>(nameof(CornerRadius), 24);

    public static readonly StyledProperty<bool> InsetStrokeProperty =
        AvaloniaProperty.Register<SmoothCornerBorder, bool>(nameof(InsetStroke));

    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    public IBrush? BorderBrush
    {
        get => GetValue(BorderBrushProperty);
        set => SetValue(BorderBrushProperty, value);
    }

    public Thickness BorderThickness
    {
        get => GetValue(BorderThicknessProperty);
        set => SetValue(BorderThicknessProperty, value);
    }

    public double CornerRadius
    {
        get => GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public bool InsetStroke
    {
        get => GetValue(InsetStrokeProperty);
        set => SetValue(InsetStrokeProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (shape is null)
        {
            return;
        }

        var borderWidth = Math.Max(
            Math.Max(BorderThickness.Left, BorderThickness.Top),
            Math.Max(BorderThickness.Right, BorderThickness.Bottom));
        var pen = BorderBrush is not null && borderWidth > 0
            ? new Pen(BorderBrush, borderWidth)
            : null;
        context.DrawGeometry(Background, pen, shape);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var padding = Padding;
        var childAvailable = new Size(
            Math.Max(0, availableSize.Width - padding.Left - padding.Right),
            Math.Max(0, availableSize.Height - padding.Top - padding.Bottom));

        Child?.Measure(childAvailable);
        return Child is null
            ? new Size(padding.Left + padding.Right, padding.Top + padding.Bottom)
            : new Size(
                Child.DesiredSize.Width + padding.Left + padding.Right,
                Child.DesiredSize.Height + padding.Top + padding.Bottom);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var borderWidth = Math.Max(
            Math.Max(BorderThickness.Left, BorderThickness.Top),
            Math.Max(BorderThickness.Right, BorderThickness.Bottom));
        var inset = InsetStroke ? borderWidth / 2 : 0;
        shape = CreateContinuousRoundedGeometry(finalSize, CornerRadius, inset);
        clipShape = CreateContinuousRoundedGeometry(finalSize, CornerRadius, 0);
        Clip = clipShape;

        var padding = Padding;
        Child?.Arrange(new Rect(
            padding.Left,
            padding.Top,
            Math.Max(0, finalSize.Width - padding.Left - padding.Right),
            Math.Max(0, finalSize.Height - padding.Top - padding.Bottom)));
        InvalidateVisual();
        return finalSize;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CornerRadiusProperty
            || change.Property == BorderThicknessProperty
            || change.Property == InsetStrokeProperty)
        {
            InvalidateArrange();
        }
        else if (change.Property == BackgroundProperty || change.Property == BorderBrushProperty)
        {
            InvalidateVisual();
        }
        else if (change.Property == PaddingProperty)
        {
            InvalidateMeasure();
        }
    }

    private static StreamGeometry CreateContinuousRoundedGeometry(Size size, double requestedRadius, double inset)
    {
        var x = Math.Max(0, inset);
        var y = Math.Max(0, inset);
        var width = Math.Max(0, size.Width - x * 2);
        var height = Math.Max(0, size.Height - y * 2);
        var radius = Math.Clamp(requestedRadius, 0, Math.Min(width, height) / 2);
        var control = radius * ContinuousCornerControl;
        var geometry = new StreamGeometry();

        using var path = geometry.Open();
        path.BeginFigure(new Point(x + radius, y), true);
        path.LineTo(new Point(x + width - radius, y));
        path.CubicBezierTo(
            new Point(x + width - radius + control, y),
            new Point(x + width, y + radius - control),
            new Point(x + width, y + radius));
        path.LineTo(new Point(x + width, y + height - radius));
        path.CubicBezierTo(
            new Point(x + width, y + height - radius + control),
            new Point(x + width - radius + control, y + height),
            new Point(x + width - radius, y + height));
        path.LineTo(new Point(x + radius, y + height));
        path.CubicBezierTo(
            new Point(x + radius - control, y + height),
            new Point(x, y + height - radius + control),
            new Point(x, y + height - radius));
        path.LineTo(new Point(x, y + radius));
        path.CubicBezierTo(
            new Point(x, y + radius - control),
            new Point(x + radius - control, y),
            new Point(x + radius, y));
        path.EndFigure(true);
        return geometry;
    }
}
