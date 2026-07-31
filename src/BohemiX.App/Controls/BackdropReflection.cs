using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BohemiX.App.Services;

namespace BohemiX.App.Controls;

public sealed class BackdropReflection : Control
{
    private static readonly HashSet<BackdropReflection> ActiveReflections = [];
    private static readonly List<BackdropReflection> RenderSnapshot = [];
    private static readonly DispatcherTimer RenderTimer = new(DispatcherPriority.Render)
    {
        Interval = TimeSpan.FromMilliseconds(16)
    };
    private BitmapLease? sourceLease;
    private BitmapLease? stripeLease;
    private string? leasedSource;
    private string? leasedStripeSource;
    private bool isAttached;
    private bool isInEffectiveViewport;

    internal static int ActiveReflectionCount => ActiveReflections.Count;

    internal static bool IsSharedRenderTimerRunning => RenderTimer.IsEnabled;
    private TopLevel? subscribedTopLevel;

    static BackdropReflection()
    {
        RenderTimer.Tick += RenderTimer_OnTick;
    }

    public static readonly StyledProperty<string> SourceProperty =
        AvaloniaProperty.Register<BackdropReflection, string>(
            nameof(Source),
            "avares://BohemiX.App/Assets/kcd2-cover-bg.jpg");

    public static readonly StyledProperty<string> StripeSourceProperty =
        AvaloniaProperty.Register<BackdropReflection, string>(
            nameof(StripeSource),
            "avares://BohemiX.App/Assets/striped-draft-card-surface.png");

    public static readonly StyledProperty<double> StripeOpacityProperty =
        AvaloniaProperty.Register<BackdropReflection, double>(nameof(StripeOpacity), 0.055);

    public static readonly StyledProperty<double> StripeSpeedProperty =
        AvaloniaProperty.Register<BackdropReflection, double>(nameof(StripeSpeed), 14);

    public BackdropReflection()
    {
        EffectiveViewportChanged += BackdropReflection_OnEffectiveViewportChanged;
        LayoutUpdated += BackdropReflection_OnLayoutUpdated;
    }

    public string Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public string StripeSource
    {
        get => GetValue(StripeSourceProperty);
        set => SetValue(StripeSourceProperty, value);
    }

    public double StripeOpacity
    {
        get => GetValue(StripeOpacityProperty);
        set => SetValue(StripeOpacityProperty, value);
    }

    public double StripeSpeed
    {
        get => GetValue(StripeSpeedProperty);
        set => SetValue(StripeSpeedProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var controlRect = new Rect(Bounds.Size);
        if (controlRect.Width <= 0 || controlRect.Height <= 0)
        {
            return;
        }

        var bitmap = GetBitmap(Source, ref sourceLease, ref leasedSource);
        if (bitmap is null || bitmap.Size.Width <= 0 || bitmap.Size.Height <= 0)
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        var topLevelSize = topLevel?.Bounds.Size ?? default;
        var topLevelOffset = topLevel is null
            ? default(Point?)
            : this.TranslatePoint(new Point(0, 0), topLevel);

        if (topLevelSize.Width <= 0 || topLevelSize.Height <= 0 || topLevelOffset is null)
        {
            context.DrawImage(bitmap, new Rect(bitmap.Size), controlRect);
            return;
        }

        var sourceRect = CalculateUniformToFillSourceRect(
            bitmap.Size,
            topLevelSize,
            topLevelOffset.Value,
            Bounds.Size);

        using (context.PushClip(controlRect))
        {
            context.DrawImage(bitmap, sourceRect, controlRect);
            DrawStripeReflection(context, topLevelSize, topLevelOffset.Value);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        isAttached = true;
        subscribedTopLevel = TopLevel.GetTopLevel(this);
        if (subscribedTopLevel is not null)
        {
            subscribedTopLevel.PropertyChanged += TopLevel_OnPropertyChanged;
        }
        UpdateActiveState();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        isAttached = false;
        isInEffectiveViewport = false;
        ActiveReflections.Remove(this);
        if (subscribedTopLevel is not null)
        {
            subscribedTopLevel.PropertyChanged -= TopLevel_OnPropertyChanged;
            subscribedTopLevel = null;
        }
        ReleaseLeases();
        if (ActiveReflections.Count == 0)
        {
            RenderTimer.Stop();
        }

        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty
            || change.Property == StripeSourceProperty
            || change.Property == StripeOpacityProperty
            || change.Property == StripeSpeedProperty
            || change.Property == IsVisibleProperty)
        {
            if (change.Property == SourceProperty)
            {
                sourceLease?.Dispose();
                sourceLease = null;
                leasedSource = null;
            }
            else if (change.Property == StripeSourceProperty)
            {
                stripeLease?.Dispose();
                stripeLease = null;
                leasedStripeSource = null;
            }

            UpdateActiveState();
            InvalidateVisual();
        }
    }

    private void DrawStripeReflection(DrawingContext context, Size topLevelSize, Point topLevelOffset)
    {
        var opacity = Math.Clamp(StripeOpacity * 0.58, 0, 0.2);
        var pattern = GetBitmap(
            StripeSource,
            ref stripeLease,
            ref leasedStripeSource,
            StripedCardBorder.StripeDecodePixelWidth);
        if (opacity <= 0 || pattern is null || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        var sourceRect = new Rect(pattern.Size);
        var imageRect = StripePatternLayout.Create(
            pattern.Size,
            topLevelSize,
            topLevelOffset,
            StripedCardBorder.SharedAnimationSeconds * StripeSpeed,
            StripedCardBorder.StripeLoopLength).ImageRect;

        using (context.PushOpacity(opacity))
        {
            context.DrawImage(pattern, sourceRect, imageRect);
        }
    }

    private static Rect CalculateUniformToFillSourceRect(
        Size bitmapSize,
        Size targetSize,
        Point reflectedOffset,
        Size reflectedSize)
    {
        var scale = Math.Max(
            targetSize.Width / bitmapSize.Width,
            targetSize.Height / bitmapSize.Height);
        var renderedWidth = bitmapSize.Width * scale;
        var renderedHeight = bitmapSize.Height * scale;
        var renderedLeft = (targetSize.Width - renderedWidth) / 2d;
        var renderedTop = (targetSize.Height - renderedHeight) / 2d;

        var sourceLeft = (reflectedOffset.X - renderedLeft) / scale;
        var sourceTop = (reflectedOffset.Y - renderedTop) / scale;
        var sourceWidth = reflectedSize.Width / scale;
        var sourceHeight = reflectedSize.Height / scale;

        sourceLeft = Math.Clamp(sourceLeft, 0, Math.Max(0, bitmapSize.Width - sourceWidth));
        sourceTop = Math.Clamp(sourceTop, 0, Math.Max(0, bitmapSize.Height - sourceHeight));

        return new Rect(sourceLeft, sourceTop, sourceWidth, sourceHeight);
    }

    private Bitmap? GetBitmap(
        string source,
        ref BitmapLease? lease,
        ref string? leasedUri,
        int decodePixelWidth = 0)
    {
        var normalized = BitmapLeaseCache.NormalizeSource(source);
        if (!string.Equals(normalized, leasedUri, StringComparison.OrdinalIgnoreCase))
        {
            lease?.Dispose();
            lease = null;
            leasedUri = null;
        }

        if (lease is null && IsEffectivelyVisible && IsTopLevelRenderable())
        {
            leasedUri = normalized;
            lease = SharedBitmapLeaseCache.Instance.Acquire(normalized, decodePixelWidth);
        }

        return lease?.Bitmap;
    }

    private static void RenderTimer_OnTick(object? sender, EventArgs e)
    {
        RenderSnapshot.Clear();
        RenderSnapshot.AddRange(ActiveReflections);
        try
        {
            foreach (var reflection in RenderSnapshot)
            {
                if (!reflection.ShouldTrackAnimation())
                {
                    ActiveReflections.Remove(reflection);
                    reflection.ReleaseLeases();
                }
                else if (reflection.IsEffectivelyVisible)
                {
                    reflection.InvalidateVisual();
                }
                else
                {
                    reflection.ReleaseLeases();
                }
            }

            if (ActiveReflections.Count == 0)
            {
                RenderTimer.Stop();
            }
        }
        finally
        {
            RenderSnapshot.Clear();
        }
    }

    private void UpdateActiveState()
    {
        // Static reflections do not need a timer. Repainting them at 30fps while their
        // parent is animated makes the sampled backdrop appear to jump under glass panels.
        if (ShouldTrackAnimation())
        {
            ActiveReflections.Add(this);
        }
        else
        {
            ActiveReflections.Remove(this);
            ReleaseLeases();
        }

        if (ActiveReflections.Count > 0)
        {
            RenderTimer.Start();
        }
        else
        {
            RenderTimer.Stop();
        }
    }

    private void ReleaseLeases()
    {
        sourceLease?.Dispose();
        stripeLease?.Dispose();
        sourceLease = null;
        stripeLease = null;
        leasedSource = null;
        leasedStripeSource = null;
    }

    private bool ShouldTrackAnimation() =>
        isAttached && isInEffectiveViewport && IsTopLevelRenderable() && StripeOpacity > 0 && StripeSpeed != 0;

    private void BackdropReflection_OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (ShouldTrackAnimation() != ActiveReflections.Contains(this))
        {
            UpdateActiveState();
        }
    }

    private void BackdropReflection_OnEffectiveViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
    {
        isInEffectiveViewport = e.EffectiveViewport.Width > 0 && e.EffectiveViewport.Height > 0;
        UpdateActiveState();
    }

    private void TopLevel_OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty || e.Property == Window.WindowStateProperty)
        {
            UpdateActiveState();
            InvalidateVisual();
        }
    }

    private bool IsTopLevelRenderable() => subscribedTopLevel is { IsVisible: true }
        && (subscribedTopLevel is not Window window || window.WindowState != WindowState.Minimized);
}
