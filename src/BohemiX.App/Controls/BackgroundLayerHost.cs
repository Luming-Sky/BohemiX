using System;
using Avalonia;
using Avalonia.Controls;

namespace BohemiX.App.Controls;

internal enum BackgroundLayerKind
{
    None,
    Default,
    Static,
    Animated,
    Video
}

public sealed class BackgroundLayerHost : Panel, IDisposable
{
    internal BackgroundLayerKind ActiveKind { get; private set; }

    internal bool ActivateDefault(Control layer) => Activate(BackgroundLayerKind.Default, layer);

    internal bool ActivateStatic(Control layer) => Activate(BackgroundLayerKind.Static, layer);

    internal bool ActivateAnimated(Control layer) => Activate(BackgroundLayerKind.Animated, layer);

    internal bool ActivateVideo(Control layer) => Activate(BackgroundLayerKind.Video, layer);

    internal bool Deactivate()
    {
        if (ActiveKind == BackgroundLayerKind.None && Children.Count == 0)
        {
            return false;
        }

        Children.Clear();
        ActiveKind = BackgroundLayerKind.None;
        return true;
    }

    public void Dispose() => Deactivate();

    protected override Size MeasureOverride(Size availableSize)
    {
        var desired = default(Size);
        foreach (var child in Children)
        {
            child.Measure(availableSize);
            desired = new Size(
                Math.Max(desired.Width, child.DesiredSize.Width),
                Math.Max(desired.Height, child.DesiredSize.Height));
        }

        return desired;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var child in Children)
        {
            child.Arrange(new Rect(finalSize));
        }

        return finalSize;
    }

    private bool Activate(BackgroundLayerKind kind, Control layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        if (ActiveKind == kind && Children.Count == 1 && ReferenceEquals(Children[0], layer))
        {
            return false;
        }

        Children.Clear();
        Children.Add(layer);
        ActiveKind = kind;
        return true;
    }
}
