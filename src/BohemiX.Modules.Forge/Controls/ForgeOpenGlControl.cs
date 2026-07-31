using System.Numerics;
using Avalonia;
using Avalonia.Input;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BohemiX.Modules.Forge.Models;
using BohemiX.Modules.Forge.Rendering;
using BohemiX.Modules.Forge.ViewModels;

namespace BohemiX.Modules.Forge.Controls;

/// <summary>
/// Avalonia adapter only. ForgeSceneRenderer owns GPU resources and never mutates the domain snapshot.
/// </summary>
public sealed class ForgeOpenGlControl : OpenGlControlBase
{
    public static readonly StyledProperty<ForgeRenderQuality> RenderQualityProperty =
        AvaloniaProperty.Register<ForgeOpenGlControl, ForgeRenderQuality>(
            nameof(RenderQuality), ForgeRenderQuality.Medium);

    public static readonly StyledProperty<ForgeTextureQuality> TextureQualityProperty =
        AvaloniaProperty.Register<ForgeOpenGlControl, ForgeTextureQuality>(
            nameof(TextureQuality), ForgeTextureQuality.Medium);

    private readonly Cursor HandCursor = new(StandardCursorType.Hand);
    private readonly Cursor ArrowCursor = new(StandardCursorType.Arrow);
    private readonly ForgeSceneRenderer renderer;
    private bool renderUnavailable;
    private bool leftDragging;
    private bool rightDragging;
    private Point gestureStart;
    private Point operationAnchor;
    private Point lastPoint;
    private ForgeStationId? gestureStation;
    private bool gestureHitsWorkpiece;
    private bool gestureStartedOnWorkpiece;
    private Vector2 gestureLattice;
    private bool hammerTargetAvailable;
    private bool transferGestureActive;
    private bool quenchGestureActive;
    private bool inspectionGestureActive;
    private ForgeStationId? transferTarget;
    private DateTimeOffset hammerHoldStarted;
    private bool operationGestureChanged;
    private DateTimeOffset lastPointerSample;
    private readonly System.Diagnostics.Stopwatch renderTimer = System.Diagnostics.Stopwatch.StartNew();
    private double lastRenderSeconds;
    private float renderClock;
    private bool debugFrameCaptured;
    private Avalonia.Controls.TopLevel? subscribedTopLevel;
    private bool isAttached;
    private bool releaseRequested;

    public ForgeRenderQuality RenderQuality
    {
        get => GetValue(RenderQualityProperty);
        set => SetValue(RenderQualityProperty, value);
    }

    public ForgeTextureQuality TextureQuality
    {
        get => GetValue(TextureQualityProperty);
        set => SetValue(TextureQualityProperty, value);
    }

    static ForgeOpenGlControl()
    {
        RenderQualityProperty.Changed.AddClassHandler<ForgeOpenGlControl>((control, change) =>
            control.renderer.ConfigureBeforeInitialization(
                change.GetNewValue<ForgeRenderQuality>(),
                control.TextureQuality));
        TextureQualityProperty.Changed.AddClassHandler<ForgeOpenGlControl>((control, change) =>
            control.renderer.ConfigureBeforeInitialization(
                control.RenderQuality,
                change.GetNewValue<ForgeTextureQuality>()));
    }

    public ForgeOpenGlControl() : this(ForgeRenderSettings.Medium)
    {
    }

    public ForgeOpenGlControl(ForgeRenderSettings settings)
    {
        renderer = new ForgeSceneRenderer(settings);
        Focusable = true;
        ClipToBounds = true;
        PointerCaptureLost += OnPointerCaptureLost;
    }

    public void RequestRenderQuality(ForgeRenderQuality quality) => renderer.RequestQuality(quality);

    protected override void OnOpenGlInit(GlInterface gl)
    {
        renderUnavailable = false;
        renderer.Initialize(gl);
        if (!renderer.IsInitialized)
        {
            SetRenderError(renderer.InitializationError ?? "无法显示锻造画面，请更新显卡驱动后重试。\n当前显卡需要支持较新的 3D 图形功能。");
        }
        else
        {
            SetRenderError(null);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        releaseRequested = false;
        isAttached = true;
        subscribedTopLevel = Avalonia.Controls.TopLevel.GetTopLevel(this);
        if (subscribedTopLevel is not null)
        {
            subscribedTopLevel.PropertyChanged += TopLevel_OnPropertyChanged;
        }

        RequestNextFrameRendering();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        PrepareForRemoval();
        if (subscribedTopLevel is not null)
        {
            subscribedTopLevel.PropertyChanged -= TopLevel_OnPropertyChanged;
            subscribedTopLevel = null;
        }

        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnOpenGlRender(GlInterface gl, int framebuffer)
    {
        if (releaseRequested || renderUnavailable || !renderer.IsInitialized)
        {
            return;
        }

        var scaling = (VisualRoot as Avalonia.Controls.TopLevel)?.RenderScaling ?? 1;
        var width = (int)Math.Max(1, Bounds.Width * scaling);
        var height = (int)Math.Max(1, Bounds.Height * scaling);
        var snapshot = (DataContext as ForgeWorkshopViewModel)?.Snapshot;
        if (snapshot is not null)
        {
            try
            {
                var now = renderTimer.Elapsed.TotalSeconds;
                var elapsed = (float)Math.Clamp(now - lastRenderSeconds, 1d / 240d, .05d);
                lastRenderSeconds = now;
                renderClock = (float)now;
                renderer.Render(snapshot, (uint)framebuffer, width, height, elapsed, renderClock);
                if (DataContext is ForgeWorkshopViewModel vm && leftDragging && vm.Snapshot.State == ForgeStateId.Hammering)
                {
                    vm.SetHammerInteraction(renderer.HammerCharge, hammerTargetAvailable);
                }
                if (renderer.LastRenderError is not null)
                {
                    SetRenderError(renderer.LastRenderError);
                }
                else if (!debugFrameCaptured && Environment.GetEnvironmentVariable("BOHEMIX_FORGE_CAPTURE") is { Length: > 0 } capturePath)
                {
                    debugFrameCaptured = renderer.CaptureFrame(capturePath, width, height);
                }
            }
            catch (Exception ex)
            {
                SetRenderError($"Forge Simulator 渲染帧失败。\n{ex.Message}");
            }
        }
        if (ShouldContinueRendering())
        {
            RequestNextFrameRendering();
        }
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        try
        {
            renderer.Deinitialize();
        }
        finally
        {
            base.OnOpenGlDeinit(gl);
        }
    }

    protected override void OnOpenGlLost()
    {
        renderer.AbandonContext();
        SetRenderError("Forge Simulator 的 OpenGL 上下文已丢失。请重新打开模块以重建 3D 资源。");
        base.OnOpenGlLost();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        if (DataContext is not ForgeWorkshopViewModel vm)
        {
            return;
        }

        Focus();
        if (point.Properties.IsRightButtonPressed)
        {
            leftDragging = false;
            ResetGestureTarget();
            rightDragging = true;
            lastPoint = e.GetPosition(this);
            lastPointerSample = DateTimeOffset.UtcNow;
            if (IsInspectionMode(vm.Snapshot))
            {
                inspectionGestureActive = true;
                renderer.BeginInspectionDrag();
            }
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        leftDragging = true;
        gestureStart = operationAnchor = lastPoint = e.GetPosition(this);
        var grinderClick = vm.Snapshot.State == ForgeStateId.Grinding &&
                           TryHitStation(gestureStart, vm.Snapshot, out var pressedStation) &&
                           pressedStation == ForgeStationId.Grinder;
        if (IsInspectionMode(vm.Snapshot) && !grinderClick)
        {
            inspectionGestureActive = true;
            operationGestureChanged = false;
            lastPointerSample = DateTimeOffset.UtcNow;
            renderer.BeginInspectionDrag();
            renderer.SetWorkpieceHovered(false);
            Cursor = HandCursor;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }
        gestureStation = TryHitStation(gestureStart, vm.Snapshot, out var station) ? station : null;
        gestureHitsWorkpiece = TryMapWorkpiece(gestureStart, out gestureLattice);
        gestureStartedOnWorkpiece = gestureHitsWorkpiece;
        quenchGestureActive = vm.Snapshot.State == ForgeStateId.Quenching &&
                              (gestureHitsWorkpiece || ForgeWorkbenchLayout.IsActiveQuenchStation(
                                  gestureStation,
                                  vm.Snapshot.ActiveQuenchMedium));
        renderer.SetWorkpieceHovered(gestureHitsWorkpiece);
        hammerTargetAvailable = vm.Snapshot.State == ForgeStateId.Hammering && gestureHitsWorkpiece;
        if (vm.Snapshot.State == ForgeStateId.Hammering && gestureHitsWorkpiece)
        {
            renderer.SetHammerTarget(gestureLattice);
            renderer.BeginHammerGesture(gestureLattice);
            hammerHoldStarted = DateTimeOffset.UtcNow;
            vm.SetHammerInteraction(0, true);
        }
        operationGestureChanged = false;
        lastPointerSample = DateTimeOffset.UtcNow;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (DataContext is not ForgeWorkshopViewModel vm)
        {
            return;
        }

        var point = e.GetPosition(this);
        var now = DateTimeOffset.UtcNow;
        var seconds = Math.Max(.001, (now - lastPointerSample).TotalSeconds);
        var dx = point.X - lastPoint.X;
        var dy = point.Y - lastPoint.Y;
        if (rightDragging)
        {
            if (inspectionGestureActive)
            {
                renderer.RotateInspection((float)(-dx * .008), (float)(-dy * .008), (float)seconds);
            }
            else
            {
                renderer.Camera.AddOrbit((float)(-dx * .006), (float)(-dy * .006));
            }
            lastPoint = point;
            lastPointerSample = now;
            e.Handled = true;
            return;
        }

        if (!leftDragging)
        {
            if (IsInspectionMode(vm.Snapshot))
            {
                if (vm.Snapshot.State == ForgeStateId.Grinding &&
                    TryHitStation(point, vm.Snapshot, out var inspectionStation) &&
                    inspectionStation == ForgeStationId.Grinder)
                {
                    renderer.SetHoveredStation(ForgeStationId.Grinder);
                    renderer.SetWorkpieceHovered(false);
                    Cursor = HandCursor;
                    return;
                }
                renderer.SetHoveredStation(null);
                renderer.SetWorkpieceHovered(false);
                Cursor = HandCursor;
                return;
            }
            var interactive = IsInteractiveAt(vm.Snapshot, point);
            var hitsWorkpiece = vm.Snapshot.State is ForgeStateId.Hammering or ForgeStateId.Quenching &&
                                TryMapWorkpiece(point, out _);
            renderer.SetHoveredStation(
                interactive && TryHitStation(point, vm.Snapshot, out var hovered) ? hovered : null);
            renderer.SetWorkpieceHovered(hitsWorkpiece);
            if (vm.Snapshot.State == ForgeStateId.Hammering && TryMapWorkpiece(point, out var hoverTarget))
            {
                renderer.SetHammerTarget(hoverTarget);
                hammerTargetAvailable = true;
            }
            else if (vm.Snapshot.State == ForgeStateId.Hammering)
            {
                renderer.SetHammerTarget(default, false);
                hammerTargetAvailable = false;
            }
            Cursor = interactive ? HandCursor : ArrowCursor;
            return;
        }

        if (inspectionGestureActive)
        {
            renderer.RotateInspection((float)(-dx * .008), (float)(-dy * .008), (float)seconds);
            operationGestureChanged |= Math.Abs(dx) + Math.Abs(dy) > .5;
        }
        else switch (vm.Snapshot.State)
        {
            case ForgeStateId.Hammering:
                var transferDistance = Distance(gestureStart, point);
                var canTransfer = gestureStartedOnWorkpiece && vm.Snapshot.CanProceed &&
                                  transferDistance >= ForgeProcessGestures.WorkpieceTransferThreshold;
                var hoveredStation = TryHitStation(point, vm.Snapshot, out var dropStation)
                    ? dropStation
                    : (ForgeStationId?)null;
                var resolvedTransfer = ForgeProcessGestures.ResolveQuenchTransfer(
                    gestureStartedOnWorkpiece,
                    vm.Snapshot.CanProceed,
                    transferDistance,
                    hoveredStation);
                if (canTransfer)
                {
                    transferGestureActive = true;
                    transferTarget = resolvedTransfer;
                    renderer.CancelHammerGesture();
                    renderer.SetHammerTarget(default, false);
                    renderer.SetTransferPreview(true);
                    renderer.SetHoveredStation(resolvedTransfer);
                    vm.SetHammerInteraction(0, false);
                    break;
                }

                if (TryMapWorkpiece(point, out var movedTarget))
                {
                    if (!hammerTargetAvailable)
                    {
                        hammerHoldStarted = now;
                        renderer.BeginHammerGesture(movedTarget);
                    }
                    gestureLattice = movedTarget;
                    gestureHitsWorkpiece = true;
                    hammerTargetAvailable = true;
                    renderer.SetHammerTarget(movedTarget);
                }
                renderer.UpdateHammerGesture((float)Math.Clamp((now - hammerHoldStarted).TotalSeconds / ForgeHammerGesture.ChargeDurationSeconds, 0, 1));
                vm.SetHammerInteraction(
                    Math.Clamp((now - hammerHoldStarted).TotalSeconds / ForgeHammerGesture.ChargeDurationSeconds, 0, 1),
                    hammerTargetAvailable);
                break;
            case ForgeStateId.Heating:
            case ForgeStateId.Reheat:
                var bellowsTravel = point.Y - operationAnchor.Y;
                if (gestureStation == ForgeStationId.Bellows && Math.Abs(bellowsTravel) >= 14)
                {
                    vm.PumpFromGesture(Math.Clamp(Math.Abs(bellowsTravel) / 80, .18, 1));
                    operationAnchor = point;
                    operationGestureChanged = true;
                }
                break;
            case ForgeStateId.Quenching:
                if (quenchGestureActive && Math.Abs(dy) >= 1)
                {
                    var quench = ForgeProcessGestures.ResolveQuench(
                        new Vector2((float)gestureStart.X, (float)gestureStart.Y),
                        new Vector2((float)point.X, (float)point.Y),
                        (float)dy,
                        seconds);
                    if (quench.HasChanged)
                    {
                        vm.UpdateQuench(quench.Depth, quench.Speed);
                        operationGestureChanged = true;
                    }
                }
                break;
            case ForgeStateId.Grinding:
                if (gestureStation == ForgeStationId.Grinder && Math.Abs(dx) >= 1)
                {
                    var grinding = ForgeProcessGestures.ResolveGrinding(
                        new Vector2((float)gestureStart.X, (float)gestureStart.Y),
                        new Vector2((float)point.X, (float)point.Y),
                        (float)dx,
                        seconds);
                    if (grinding.HasChanged)
                    {
                        vm.GrindAt(grinding.Position, grinding.Speed, grinding.Delta);
                        operationGestureChanged = true;
                    }
                }
                break;
        }

        lastPoint = point;
        lastPointerSample = now;
        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (DataContext is ForgeWorkshopViewModel vm && IsInspectionMode(vm.Snapshot))
        {
            renderer.AddInspectionZoom((float)e.Delta.Y * .055f);
        }
        else
        {
            renderer.Camera.AddZoom((float)-e.Delta.Y * .025f);
        }
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (DataContext is not ForgeWorkshopViewModel vm)
        {
            return;
        }

        var point = e.GetPosition(this);
        var movement = Math.Sqrt(Math.Pow(point.X - gestureStart.X, 2) + Math.Pow(point.Y - gestureStart.Y, 2));
        if (inspectionGestureActive)
        {
            renderer.EndInspectionDrag();
        }
        else if (leftDragging && vm.Snapshot.State == ForgeStateId.Hammering)
        {
            CompleteHammerOrTransfer(vm, point);
        }
        else if (leftDragging && vm.Snapshot.State == ForgeStateId.Quenching)
        {
            if (quenchGestureActive && operationGestureChanged)
            {
                vm.FinishQuench();
            }
        }
        else if (leftDragging && movement < 10 && gestureStation is { } station)
        {
            ActivateStation(vm, station);
        }

        leftDragging = false;
        rightDragging = false;
        ResetGestureTarget();
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private bool TryHitStation(Point point, ForgeSnapshot snapshot, out ForgeStationId station)
    {
        var scaling = (VisualRoot as Avalonia.Controls.TopLevel)?.RenderScaling ?? 1;
        var viewport = new Vector2((float)(Bounds.Width * scaling), (float)(Bounds.Height * scaling));
        var pixel = new Vector2((float)(point.X * scaling), (float)(point.Y * scaling));
        return renderer.TryHitStation(pixel, viewport, snapshot.State, snapshot.CanProceed, out station);
    }

    private bool TryMapWorkpiece(Point point, out Vector2 lattice)
    {
        var scaling = (VisualRoot as Avalonia.Controls.TopLevel)?.RenderScaling ?? 1;
        var viewport = new Vector2((float)(Bounds.Width * scaling), (float)(Bounds.Height * scaling));
        var pixel = new Vector2((float)(point.X * scaling), (float)(point.Y * scaling));
        return renderer.TryMapPointer(pixel, viewport, out lattice);
    }

    internal void TrackHammerHover(Point point)
    {
        if (leftDragging || rightDragging || DataContext is not ForgeWorkshopViewModel vm)
        {
            return;
        }

        if (IsInspectionMode(vm.Snapshot))
        {
            if (vm.Snapshot.State == ForgeStateId.Grinding &&
                TryHitStation(point, vm.Snapshot, out var inspectionStation) &&
                inspectionStation == ForgeStationId.Grinder)
            {
                renderer.SetHoveredStation(ForgeStationId.Grinder);
                renderer.SetWorkpieceHovered(false);
                Cursor = HandCursor;
                RequestNextFrameRendering();
                return;
            }
            renderer.SetHoveredStation(null);
            renderer.SetWorkpieceHovered(false);
            Cursor = HandCursor;
            RequestNextFrameRendering();
            return;
        }

        if (vm.Snapshot.State != ForgeStateId.Hammering) return;

        if (point.X >= 0 && point.Y >= 0 && point.X <= Bounds.Width && point.Y <= Bounds.Height &&
            TryMapWorkpiece(point, out var target))
        {
            renderer.SetHammerTarget(target);
            renderer.SetWorkpieceHovered(false);
            hammerTargetAvailable = true;
            Cursor = HandCursor;
            RequestNextFrameRendering();
            return;
        }

        renderer.SetHammerTarget(default, false);
        renderer.SetWorkpieceHovered(false);
        hammerTargetAvailable = false;
        Cursor = ArrowCursor;
        RequestNextFrameRendering();
    }

    internal bool TryBeginPrimaryInput(Point point, IPointer pointer)
    {
        if (leftDragging || rightDragging || DataContext is not ForgeWorkshopViewModel vm)
        {
            return false;
        }

        var state = vm.Snapshot.State;
        var station = TryHitStation(point, vm.Snapshot, out var hitStation)
            ? hitStation
            : (ForgeStationId?)null;
        var hitsWorkpiece = TryMapWorkpiece(point, out var target);
        var startsHammer = state == ForgeStateId.Hammering && hitsWorkpiece;
        var startsQuench = state == ForgeStateId.Quenching &&
                            (hitsWorkpiece || ForgeWorkbenchLayout.IsActiveQuenchStation(
                                station,
                                vm.Snapshot.ActiveQuenchMedium));
        var startsGrinding = state == ForgeStateId.Grinding && station == ForgeStationId.Grinder;
        var startsInspection = IsInspectionMode(vm.Snapshot) &&
                               !(state == ForgeStateId.Grinding && station == ForgeStationId.Grinder);
        if (!startsHammer && !startsQuench && !startsGrinding && !startsInspection)
        {
            return false;
        }

        Focus();
        leftDragging = true;
        gestureStart = operationAnchor = lastPoint = point;
        gestureStation = station;
        gestureHitsWorkpiece = hitsWorkpiece;
        gestureStartedOnWorkpiece = hitsWorkpiece;
        gestureLattice = target;
        hammerHoldStarted = lastPointerSample = DateTimeOffset.UtcNow;
        operationGestureChanged = false;
        quenchGestureActive = startsQuench;
        inspectionGestureActive = startsInspection;
        if (startsInspection)
        {
            renderer.BeginInspectionDrag();
            renderer.SetWorkpieceHovered(true);
            Cursor = HandCursor;
        }
        else if (startsHammer)
        {
            hammerTargetAvailable = true;
            renderer.SetWorkpieceHovered(true);
            renderer.SetHammerTarget(target);
            renderer.BeginHammerGesture(target);
            vm.SetHammerInteraction(0, true);
        }
        else
        {
            hammerTargetAvailable = false;
            renderer.SetWorkpieceHovered(hitsWorkpiece);
        }
        pointer.Capture(this);
        RequestNextFrameRendering();
        return true;
    }

    internal bool UpdatePrimaryInput(Point point)
    {
        if (!leftDragging || DataContext is not ForgeWorkshopViewModel vm)
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        if (inspectionGestureActive)
        {
            var seconds = Math.Max(.001, (now - lastPointerSample).TotalSeconds);
            var dx = point.X - lastPoint.X;
            var dy = point.Y - lastPoint.Y;
            renderer.RotateInspection((float)(-dx * .008), (float)(-dy * .008), (float)seconds);
            operationGestureChanged |= Math.Abs(dx) + Math.Abs(dy) > .5;
            lastPoint = point;
            lastPointerSample = now;
            RequestNextFrameRendering();
            return true;
        }

        if (vm.Snapshot.State == ForgeStateId.Quenching && quenchGestureActive)
        {
            var seconds = Math.Max(.001, (now - lastPointerSample).TotalSeconds);
            var dy = point.Y - lastPoint.Y;
            if (Math.Abs(dy) >= 1)
            {
                var quench = ForgeProcessGestures.ResolveQuench(
                    new Vector2((float)gestureStart.X, (float)gestureStart.Y),
                    new Vector2((float)point.X, (float)point.Y),
                    (float)dy,
                    seconds);
                if (quench.HasChanged)
                {
                    vm.UpdateQuench(quench.Depth, quench.Speed);
                    operationGestureChanged = true;
                }
            }

            lastPoint = point;
            lastPointerSample = now;
            RequestNextFrameRendering();
            return true;
        }

        if (vm.Snapshot.State == ForgeStateId.Grinding && gestureStation == ForgeStationId.Grinder)
        {
            var seconds = Math.Max(.001, (now - lastPointerSample).TotalSeconds);
            var dx = point.X - lastPoint.X;
            if (vm.Snapshot.GrindingEngaged && Math.Abs(dx) >= 1)
            {
                var grinding = ForgeProcessGestures.ResolveGrinding(
                    new Vector2((float)gestureStart.X, (float)gestureStart.Y),
                    new Vector2((float)point.X, (float)point.Y),
                    (float)dx,
                    seconds);
                if (grinding.HasChanged)
                {
                    vm.GrindAt(grinding.Position, grinding.Speed, grinding.Delta);
                    operationGestureChanged = true;
                }
            }

            lastPoint = point;
            lastPointerSample = now;
            RequestNextFrameRendering();
            return true;
        }

        if (vm.Snapshot.State != ForgeStateId.Hammering)
        {
            return false;
        }

        var hoveredStation = TryHitStation(point, vm.Snapshot, out var dropStation)
            ? dropStation
            : (ForgeStationId?)null;
        var targetStation = ForgeProcessGestures.ResolveQuenchTransfer(
            gestureStartedOnWorkpiece,
            vm.Snapshot.CanProceed,
            Distance(gestureStart, point),
            hoveredStation);
        var canTransfer = gestureStartedOnWorkpiece && vm.Snapshot.CanProceed &&
                          Distance(gestureStart, point) >= ForgeProcessGestures.WorkpieceTransferThreshold;
        if (canTransfer)
        {
            transferGestureActive = true;
            transferTarget = targetStation;
            renderer.CancelHammerGesture();
            renderer.SetHammerTarget(default, false);
            renderer.SetTransferPreview(true);
            renderer.SetHoveredStation(targetStation);
            vm.SetHammerInteraction(0, false);
            lastPoint = point;
            lastPointerSample = now;
            RequestNextFrameRendering();
            return true;
        }

        if (TryMapWorkpiece(point, out var target))
        {
            gestureLattice = target;
            gestureHitsWorkpiece = true;
            hammerTargetAvailable = true;
            renderer.SetHammerTarget(target);
        }

        var charge = Math.Clamp(
            (now - hammerHoldStarted).TotalSeconds / ForgeHammerGesture.ChargeDurationSeconds,
            0,
            1);
        renderer.UpdateHammerGesture((float)charge);
        vm.SetHammerInteraction(charge, true);
        lastPoint = point;
        lastPointerSample = now;
        RequestNextFrameRendering();
        return true;
    }

    internal bool TryEndPrimaryInput(Point point, IPointer pointer)
    {
        if (!leftDragging || DataContext is not ForgeWorkshopViewModel vm)
        {
            return false;
        }

        if (inspectionGestureActive)
        {
            renderer.EndInspectionDrag();
        }
        else if (vm.Snapshot.State == ForgeStateId.Hammering)
        {
            CompleteHammerOrTransfer(vm, point);
        }
        else if (vm.Snapshot.State == ForgeStateId.Quenching && quenchGestureActive && operationGestureChanged)
        {
            vm.FinishQuench();
        }
        else if (vm.Snapshot.State == ForgeStateId.Grinding && gestureStation == ForgeStationId.Grinder)
        {
            if (!vm.Snapshot.GrindingEngaged && Distance(gestureStart, point) < 10)
            {
                vm.BeginGrindingCommand.Execute(null);
            }
        }
        else if (vm.Snapshot.State is not ForgeStateId.Quenching and not ForgeStateId.Grinding)
        {
            return false;
        }

        leftDragging = false;
        rightDragging = false;
        ResetGestureTarget();
        pointer.Capture(null);
        RequestNextFrameRendering();
        return true;
    }

    internal void ClearHammerHover()
    {
        if (leftDragging || rightDragging)
        {
            return;
        }

        renderer.SetHammerTarget(default, false);
        renderer.SetWorkpieceHovered(false);
        hammerTargetAvailable = false;
        Cursor = DataContext is ForgeWorkshopViewModel vm && IsInspectionMode(vm.Snapshot)
            ? HandCursor
            : ArrowCursor;
        RequestNextFrameRendering();
    }

    private bool IsInteractiveAt(ForgeSnapshot snapshot, Point point)
    {
        var station = TryHitStation(point, snapshot, out var hitStation) ? hitStation : (ForgeStationId?)null;
        if (station is { } candidate && ForgeWorkbenchLayout.CanActivate(snapshot.State, snapshot.CanProceed, candidate))
        {
            return true;
        }

        var hitsWorkpiece = snapshot.State is ForgeStateId.Hammering or ForgeStateId.Quenching && TryMapWorkpiece(point, out _);
        return ForgeWorkbenchLayout.CanStartGesture(
            snapshot.State,
            station,
            hitsWorkpiece,
            snapshot.ActiveQuenchMedium);
    }

    private static void ActivateStation(ForgeWorkshopViewModel vm, ForgeStationId station)
    {
        if (!ForgeWorkbenchLayout.CanActivate(vm.Snapshot.State, vm.Snapshot.CanProceed, station))
        {
            return;
        }

        switch (vm.Snapshot.State, station)
        {
            case (ForgeStateId.Heating or ForgeStateId.Reheat, ForgeStationId.Bellows):
                vm.PumpFromGesture(.85);
                break;
            case (ForgeStateId.Heating or ForgeStateId.Reheat, ForgeStationId.Anvil):
                vm.MoveToAnvilCommand.Execute(null);
                break;
            case (ForgeStateId.Hammering, ForgeStationId.Hearth or ForgeStationId.Bellows):
                vm.MoveToForgeCommand.Execute(null);
                break;
            case (ForgeStateId.Hammering, ForgeStationId.WaterVat):
                vm.QuenchWaterCommand.Execute(null);
                break;
            case (ForgeStateId.Hammering, ForgeStationId.OilVat):
                vm.QuenchOilCommand.Execute(null);
                break;
            case (ForgeStateId.Grinding, ForgeStationId.Grinder):
                vm.BeginGrindingCommand.Execute(null);
                break;
        }
    }

    private void ResetGestureTarget()
    {
        renderer.EndInspectionDrag();
        renderer.CancelHammerGesture();
        renderer.SetTransferPreview(false);
        renderer.SetWorkpieceHovered(false);
        gestureStation = null;
        gestureHitsWorkpiece = false;
        gestureStartedOnWorkpiece = false;
        gestureLattice = default;
        operationGestureChanged = false;
        hammerTargetAvailable = false;
        transferGestureActive = false;
        quenchGestureActive = false;
        inspectionGestureActive = false;
        transferTarget = null;
        if (DataContext is ForgeWorkshopViewModel vm)
        {
            vm.SetHammerInteraction(0, false);
        }
    }

    private void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (leftDragging && !transferGestureActive && DataContext is ForgeWorkshopViewModel vm && vm.Snapshot.State == ForgeStateId.Hammering)
        {
            // Avalonia can revoke capture when the GL surface loses focus. Treat the last
            // sampled point as the release so a valid hammer gesture is never silently lost.
            CommitHammerGesture(vm, lastPoint);
        }

        leftDragging = false;
        rightDragging = false;
        ResetGestureTarget();
    }

    private void CommitHammerGesture(ForgeWorkshopViewModel vm, Point endPoint)
    {
        if (TryMapWorkpiece(endPoint, out var releaseTarget))
        {
            gestureLattice = releaseTarget;
            hammerTargetAvailable = true;
        }
        var result = ForgeHammerGesture.ResolveHold(
            hammerTargetAvailable,
            gestureLattice,
            (DateTimeOffset.UtcNow - hammerHoldStarted).TotalSeconds);
        renderer.EndHammerGesture();
        if (!result.ShouldStrike)
        {
            return;
        }

        var strike = vm.StrikeAt(result.Lattice.X, result.Lattice.Y, result.Force);
        if (!strike.Accepted)
        {
            return;
        }

        // Submit immediately so a fast click cannot lose its visual response between the
        // input event, the ViewModel timer and the next OpenGL frame. The direct fallback
        // also keeps the tool animation visible during a transient snapshot race.
        if (strike.Snapshot.VisualEvent is { } visual && renderer.SubmitVisualEvent(visual, strike.Snapshot))
        {
            RequestNextFrameRendering();
            return;
        }

        renderer.PlayHammerStrike(result.Lattice, result.Force, strike.Snapshot);
        RequestNextFrameRendering();
    }

    private void CompleteHammerOrTransfer(ForgeWorkshopViewModel vm, Point endPoint)
    {
        if (!transferGestureActive)
        {
            CommitHammerGesture(vm, endPoint);
            return;
        }

        renderer.CancelHammerGesture();
        if (transferTarget == ForgeStationId.WaterVat)
        {
            vm.QuenchWaterCommand.Execute(null);
        }
        else if (transferTarget == ForgeStationId.OilVat)
        {
            vm.QuenchOilCommand.Execute(null);
        }
    }

    private static double Distance(Point from, Point to) =>
        Math.Sqrt(Math.Pow(to.X - from.X, 2) + Math.Pow(to.Y - from.Y, 2));

    private static bool IsInspectionMode(ForgeSnapshot snapshot) =>
        snapshot.State is ForgeStateId.Inspection or ForgeStateId.Result ||
        snapshot.State == ForgeStateId.Grinding && !snapshot.GrindingEngaged;

    protected override void OnPointerExited(PointerEventArgs e)
    {
        renderer.SetHoveredStation(null);
        renderer.SetWorkpieceHovered(false);
        Cursor = DataContext is ForgeWorkshopViewModel vm && IsInspectionMode(vm.Snapshot)
            ? HandCursor
            : ArrowCursor;
        base.OnPointerExited(e);
    }

    private void SetRenderError(string? message)
    {
        renderUnavailable = !string.IsNullOrWhiteSpace(message);
        if (DataContext is ForgeWorkshopViewModel vm)
        {
            Dispatcher.UIThread.Post(() => vm.SetRenderError(message));
        }
    }

    private void TopLevel_OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Visual.IsVisibleProperty || e.Property == Avalonia.Controls.Window.WindowStateProperty)
        {
            if (ShouldContinueRendering())
            {
                lastRenderSeconds = renderTimer.Elapsed.TotalSeconds;
                RequestNextFrameRendering();
            }
        }
    }

    private bool ShouldContinueRendering() => ShouldRenderFrames(
        isAttached,
        IsEffectivelyVisible,
        subscribedTopLevel?.IsVisible == true,
        subscribedTopLevel is Avalonia.Controls.Window { WindowState: Avalonia.Controls.WindowState.Minimized },
        releaseRequested);

    internal static bool ShouldRenderFrames(
        bool attached,
        bool effectivelyVisible,
        bool topLevelVisible,
        bool minimized,
        bool releaseRequested = false) =>
        attached && effectivelyVisible && topLevelVisible && !minimized && !releaseRequested;

    public void PrepareForRemoval()
    {
        releaseRequested = true;
        isAttached = false;
        leftDragging = false;
        rightDragging = false;
        ResetGestureTarget();
        renderer.SetHoveredStation(null);
        renderer.SetWorkpieceHovered(false);
    }
}
