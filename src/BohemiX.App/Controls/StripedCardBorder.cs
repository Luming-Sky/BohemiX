using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Layout;
using BohemiX.App.Services;

namespace BohemiX.App.Controls;

public enum StripeAnchorMode
{
    Viewport,
    Local
}

public class StripedCardBorder : Decorator
{
    private const string DefaultStripeAssetUri = "avares://BohemiX.App/Assets/striped-card-surface.png";
    internal const int StripeDecodePixelWidth = 1536;
    internal const double StripeLoopLength = 764;
    private static readonly HashSet<StripedCardBorder> AnimatedCards = [];
    private static readonly List<StripedCardBorder> AnimationSnapshot = [];
    private static readonly HashSet<StripedCardBorder> LiveCards = [];
    private static readonly DateTimeOffset AnimationStart = DateTimeOffset.UtcNow;
    private static readonly DispatcherTimer AnimationTimer = new(DispatcherPriority.Render)
    {
        Interval = TimeSpan.FromMilliseconds(16)
    };

    internal static double SharedAnimationSeconds => (DateTimeOffset.UtcNow - AnimationStart).TotalSeconds;

    private static string? globalStripeAssetUri;
    private static bool? globalStripeMotionEnabled;

    private bool isAttached;
    private bool isInEffectiveViewport;
    private TopLevel? subscribedTopLevel;
    private BitmapLease? stripePatternLease;
    private string? leasedStripeAssetUri;

    static StripedCardBorder()
    {
        AnimationTimer.Tick += AnimationTimer_OnTick;
    }

    public static readonly StyledProperty<bool> IsStripeMotionEnabledProperty =
        AvaloniaProperty.Register<StripedCardBorder, bool>(
            nameof(IsStripeMotionEnabled),
            false,
            inherits: true);

    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        AvaloniaProperty.Register<StripedCardBorder, IBrush?>(nameof(Background));

    public static readonly StyledProperty<IBrush?> BorderBrushProperty =
        AvaloniaProperty.Register<StripedCardBorder, IBrush?>(nameof(BorderBrush));

    public static readonly StyledProperty<Thickness> BorderThicknessProperty =
        AvaloniaProperty.Register<StripedCardBorder, Thickness>(nameof(BorderThickness));

    public static readonly StyledProperty<CornerRadius> CornerRadiusProperty =
        AvaloniaProperty.Register<StripedCardBorder, CornerRadius>(nameof(CornerRadius));

    public static readonly StyledProperty<BoxShadows> BoxShadowProperty =
        AvaloniaProperty.Register<StripedCardBorder, BoxShadows>(nameof(BoxShadow));

    public static readonly StyledProperty<double> StripeOpacityProperty =
        AvaloniaProperty.Register<StripedCardBorder, double>(nameof(StripeOpacity), 0.1);

    public static readonly StyledProperty<double> StripeSpeedProperty =
        AvaloniaProperty.Register<StripedCardBorder, double>(nameof(StripeSpeed), 18);

    public static readonly StyledProperty<string?> StripeAssetUriProperty =
        AvaloniaProperty.Register<StripedCardBorder, string?>(nameof(StripeAssetUri), DefaultStripeAssetUri);

    public static readonly StyledProperty<StripeAnchorMode> StripeAnchorModeProperty =
        AvaloniaProperty.Register<StripedCardBorder, StripeAnchorMode>(
            nameof(StripeAnchorMode),
            StripeAnchorMode.Viewport);

    public StripedCardBorder()
    {
        EffectiveViewportChanged += StripedCardBorder_OnEffectiveViewportChanged;
        LayoutUpdated += StripedCardBorder_OnLayoutUpdated;
    }

    public static void SetGlobalAppearance(string? stripeAssetUri, bool isMotionEnabled)
    {
        globalStripeAssetUri = string.IsNullOrWhiteSpace(stripeAssetUri) ? null : stripeAssetUri;
        globalStripeMotionEnabled = ResolveGlobalMotionOverride(globalStripeAssetUri, isMotionEnabled);

        foreach (var card in LiveCards.ToArray())
        {
            card.ReleaseStripePattern();
            card.UpdateAnimationAndLease();
            card.InvalidateVisual();
        }
    }

    public bool IsStripeMotionEnabled
    {
        get => GetValue(IsStripeMotionEnabledProperty);
        set => SetValue(IsStripeMotionEnabledProperty, value);
    }

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

    public CornerRadius CornerRadius
    {
        get => GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public BoxShadows BoxShadow
    {
        get => GetValue(BoxShadowProperty);
        set => SetValue(BoxShadowProperty, value);
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

    public string? StripeAssetUri
    {
        get => GetValue(StripeAssetUriProperty);
        set => SetValue(StripeAssetUriProperty, value);
    }

    public StripeAnchorMode StripeAnchorMode
    {
        get => GetValue(StripeAnchorModeProperty);
        set => SetValue(StripeAnchorModeProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var rect = new Rect(Bounds.Size);
        var radius = Math.Max(0, CornerRadius.TopLeft);
        var roundedRect = new RoundedRect(rect, radius);
        var borderWidth = Math.Max(
            Math.Max(BorderThickness.Left, BorderThickness.Top),
            Math.Max(BorderThickness.Right, BorderThickness.Bottom));
        var pen = BorderBrush is not null && borderWidth > 0
            ? new Pen(BorderBrush, borderWidth)
            : null;

        context.DrawRectangle(Background, pen, roundedRect, BoxShadow);

        var opacity = CalculateStripeOpacity(StripeOpacity, globalStripeAssetUri is not null);
        var pattern = GetStripePattern();
        if (opacity <= 0 || Bounds.Width <= 0 || Bounds.Height <= 0 || pattern is null)
        {
            base.Render(context);
            return;
        }

        var sourceRect = new Rect(pattern.Size);
        var imageRect = CreateStripeImageRect(
            pattern,
            IsStripeMotionEffective ? SharedAnimationSeconds * StripeSpeed : 0);

        using (context.PushClip(roundedRect))
        using (context.PushOpacity(opacity))
        {
            context.DrawImage(pattern, sourceRect, imageRect);
        }

        base.Render(context);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var padding = Padding;
        var childAvailable = new Size(
            Math.Max(0, availableSize.Width - padding.Left - padding.Right),
            Math.Max(0, availableSize.Height - padding.Top - padding.Bottom));

        if (Child is null)
        {
            return new Size(padding.Left + padding.Right, padding.Top + padding.Bottom);
        }

        Child.Measure(childAvailable);
        return new Size(
            Child.DesiredSize.Width + padding.Left + padding.Right,
            Child.DesiredSize.Height + padding.Top + padding.Bottom);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var padding = Padding;
        Child?.Arrange(new Rect(
            padding.Left,
            padding.Top,
            Math.Max(0, finalSize.Width - padding.Left - padding.Right),
            Math.Max(0, finalSize.Height - padding.Top - padding.Bottom)));
        return finalSize;
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
        LiveCards.Add(this);
        UpdateAnimationAndLease();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        isAttached = false;
        isInEffectiveViewport = false;
        AnimatedCards.Remove(this);
        LiveCards.Remove(this);
        if (subscribedTopLevel is not null)
        {
            subscribedTopLevel.PropertyChanged -= TopLevel_OnPropertyChanged;
            subscribedTopLevel = null;
        }
        ReleaseStripePattern();
        UpdateSharedAnimationTimer();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsStripeMotionEnabledProperty
            || change.Property == StripeOpacityProperty
            || change.Property == StripeSpeedProperty
            || change.Property == IsVisibleProperty)
        {
            UpdateAnimationAndLease();
            InvalidateVisual();
        }
        else if (change.Property == StripeAssetUriProperty)
        {
            ReleaseStripePattern();
            UpdateAnimationAndLease();
            InvalidateVisual();
        }
        else if (change.Property == BackgroundProperty
                 || change.Property == BorderBrushProperty
                 || change.Property == BorderThicknessProperty
                 || change.Property == CornerRadiusProperty
                 || change.Property == BoxShadowProperty
                 || change.Property == StripeAnchorModeProperty)
        {
            InvalidateVisual();
        }
        else if (change.Property == PaddingProperty)
        {
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    private void UpdateAnimationAndLease()
    {
        var shouldRenderStripes = isAttached && isInEffectiveViewport && IsEffectivelyVisible && IsTopLevelRenderable() && StripeOpacity > 0;
        if (shouldRenderStripes && stripePatternLease is null)
        {
            leasedStripeAssetUri = NormalizeStripeAssetUri(EffectiveStripeAssetUri);
            stripePatternLease = SharedBitmapLeaseCache.Instance.Acquire(leasedStripeAssetUri, StripeDecodePixelWidth);
        }
        else if (!shouldRenderStripes)
        {
            ReleaseStripePattern();
        }

        var shouldAnimate = isAttached && isInEffectiveViewport && IsTopLevelRenderable() && IsStripeMotionEffective && StripeOpacity > 0;
        if (shouldAnimate)
        {
            AnimatedCards.Add(this);
        }
        else
        {
            AnimatedCards.Remove(this);
        }

        UpdateSharedAnimationTimer();
    }

    private static void AnimationTimer_OnTick(object? sender, EventArgs e)
    {
        AnimationSnapshot.Clear();
        AnimationSnapshot.AddRange(AnimatedCards);
        try
        {
            foreach (var card in AnimationSnapshot)
            {
                if (!card.isAttached || !card.isInEffectiveViewport || !card.IsTopLevelRenderable() || !card.IsStripeMotionEffective || card.StripeOpacity <= 0)
                {
                    AnimatedCards.Remove(card);
                    card.ReleaseStripePattern();
                }
                else if (card.IsEffectivelyVisible)
                {
                    card.InvalidateVisual();
                }
                else
                {
                    card.ReleaseStripePattern();
                }
            }

            UpdateSharedAnimationTimer();
        }
        finally
        {
            AnimationSnapshot.Clear();
        }
    }

    private static void UpdateSharedAnimationTimer()
    {
        if (AnimatedCards.Count > 0)
        {
            AnimationTimer.Start();
        }
        else
        {
            AnimationTimer.Stop();
        }
    }

    private Bitmap? GetStripePattern()
    {
        var normalizedAssetUri = NormalizeStripeAssetUri(EffectiveStripeAssetUri);
        if (!string.Equals(leasedStripeAssetUri, normalizedAssetUri, StringComparison.OrdinalIgnoreCase))
        {
            ReleaseStripePattern();
        }

        if (stripePatternLease is null && isAttached && isInEffectiveViewport && IsEffectivelyVisible && IsTopLevelRenderable() && StripeOpacity > 0)
        {
            leasedStripeAssetUri = normalizedAssetUri;
            stripePatternLease = SharedBitmapLeaseCache.Instance.Acquire(normalizedAssetUri, StripeDecodePixelWidth);
        }

        return stripePatternLease?.Bitmap;
    }

    private void StripedCardBorder_OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (!IsStripeMotionEffective)
        {
            return;
        }

        var shouldAnimate = isAttached && isInEffectiveViewport && IsTopLevelRenderable() && IsStripeMotionEffective && StripeOpacity > 0;
        if (shouldAnimate == AnimatedCards.Contains(this))
        {
            return;
        }

        UpdateAnimationAndLease();
        if (shouldAnimate)
        {
            InvalidateVisual();
        }
    }

    private void StripedCardBorder_OnEffectiveViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
    {
        isInEffectiveViewport = e.EffectiveViewport.Width > 0 && e.EffectiveViewport.Height > 0;
        UpdateAnimationAndLease();
    }

    private void TopLevel_OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty || e.Property == Window.WindowStateProperty)
        {
            UpdateAnimationAndLease();
        }
    }

    private bool IsTopLevelRenderable() => subscribedTopLevel is { IsVisible: true }
        && (subscribedTopLevel is not Window window || window.WindowState != WindowState.Minimized);

    private Rect CreateStripeImageRect(Bitmap pattern, double travel)
    {
        if (StripeAnchorMode == StripeAnchorMode.Local)
        {
            return StripePatternLayout.Create(
                pattern.Size,
                Bounds.Size,
                default,
                travel,
                StripeLoopLength).ImageRect;
        }

        var visualRoot = TopLevel.GetTopLevel(this);
        var rootOffset = visualRoot is null
            ? default
            : this.TranslatePoint(new Point(0, 0), visualRoot) ?? default;
        var rootSize = visualRoot?.Bounds.Size ?? Bounds.Size;

        return StripePatternLayout.Create(
            pattern.Size,
            rootSize,
            rootOffset,
            travel,
            StripeLoopLength).ImageRect;
    }

    private static string NormalizeStripeAssetUri(string? assetUri) => string.IsNullOrWhiteSpace(assetUri)
        ? DefaultStripeAssetUri
        : BitmapLeaseCache.NormalizeSource(assetUri);

    internal static bool? ResolveGlobalMotionOverride(string? assetUri, bool isMotionEnabled) =>
        string.IsNullOrWhiteSpace(assetUri) ? null : isMotionEnabled;

    internal static double CalculateStripeOpacity(double configuredOpacity, bool hasCustomTexture)
    {
        if (configuredOpacity <= 0)
        {
            return 0;
        }

        return hasCustomTexture
            ? Math.Clamp(Math.Max(configuredOpacity, 0.22), 0, 0.42)
            : Math.Clamp(configuredOpacity * 0.58, 0, 0.2);
    }

    private string EffectiveStripeAssetUri => globalStripeAssetUri ?? StripeAssetUri ?? DefaultStripeAssetUri;

    private bool IsStripeMotionEffective => globalStripeMotionEnabled ?? IsStripeMotionEnabled;

    private void ReleaseStripePattern()
    {
        stripePatternLease?.Dispose();
        stripePatternLease = null;
        leasedStripeAssetUri = null;
    }

}
