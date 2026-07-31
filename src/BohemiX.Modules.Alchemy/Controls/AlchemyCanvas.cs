using System.ComponentModel;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BohemiX.Modules.Alchemy.Data;
using BohemiX.Modules.Alchemy.Models;
using BohemiX.Modules.Alchemy.Services;
using BohemiX.Modules.Alchemy.ViewModels;

namespace BohemiX.Modules.Alchemy.Controls;

/// <summary>
/// A single immediate-mode game surface. Every workshop entity is rendered and hit-tested
/// here; no Button, Border or per-entity layout control participates in gameplay.
/// </summary>
public sealed class AlchemyCanvas : Control
{
    private const double SceneWidth = AlchemyHudLayout.SceneWidth;
    private const double SceneHeight = AlchemyHudLayout.SceneHeight;
    private const double GrindDistanceRequired = 700;
    private const double PoundReleaseTipY = 462;
    private const double PoundImpactTipY = 492;
    private const double PoundMinimumVelocity = 220;
    private const int PourStreamSampleCount = 14;

    private static readonly Dictionary<uint, IBrush> BrushCache = [];
    private static readonly Dictionary<PenKey, Pen> PenCache = [];
    private static readonly Dictionary<TextLayoutKey, FormattedText> TextLayoutCache = [];
    private static readonly Typeface DisplayTypeface = new("STKaiti");
    private static readonly Typeface UiTypeface = new("Microsoft YaHei");
    private static readonly IBrush Ink = Brush(0xFF2B2118);
    private static readonly IBrush PaleInk = Brush(0xFFF1DFB5);
    private static readonly IBrush MutedInk = Brush(0xFF745E43);
    private static readonly IBrush Brass = Brush(0xFFA7752E);
    private static readonly IBrush Iron = Brush(0xFF30291F);
    private static readonly IBrush IronLight = Brush(0xFF685B45);
    private static readonly IBrush Parchment = Brush(0xFFE5C991);
    private static readonly IBrush ParchmentDark = Brush(0xFFC69D5C);
    private static readonly IBrush WoodDark = Brush(0xFF302216);
    private static readonly IBrush Wood = Brush(0xFF765334);
    private static readonly IBrush WoodLight = Brush(0xFFA2794D);
    private static readonly IBrush HerbGreen = Brush(0xFF66754A);
    private static readonly StreamGeometry CauldronLeafGeometry = StreamGeometry.Parse(
        "M-10,0 C-6,-6 4,-7 10,-1 C5,6 -5,6 -10,0 Z");
    private const int MaximumCachedTextLayouts = 512;

    private static readonly Rect MortarHitbox = AlchemyHudLayout.Mortar;
    private static readonly Rect MortarDropHitbox = AlchemyHudLayout.MortarDrop;
    private static readonly Rect MortarPowderHitbox = AlchemyHudLayout.MortarPowder;
    private static readonly Rect PestleHitbox = AlchemyHudLayout.Pestle;
    private static readonly Rect CauldronHitbox = AlchemyHudLayout.Cauldron;
    private static readonly Rect CauldronMouth = AlchemyHudLayout.CauldronMouth;
    private static readonly Rect CauldronHookHitbox = AlchemyHudLayout.CauldronHook;
    private static readonly Rect BellowsHandleHitbox = AlchemyHudLayout.BellowsHandle;
    private static readonly Rect HourglassHitbox = AlchemyHudLayout.Hourglass;
    private static readonly Rect DistillerLeverHitbox = AlchemyHudLayout.DistillerLever;
    private static readonly Rect ProductBottleHitbox = AlchemyHudLayout.ProductBottle;
    private static readonly Rect DistillerReceiverHitbox = AlchemyHudLayout.DistillerReceiver;
    private static readonly Rect RecipeTabHitbox = AlchemyHudLayout.RecipeButton;
    private static readonly Rect ResetRagHitbox = AlchemyHudLayout.ResetButton;
    private static readonly Cursor ArrowCursor = new(StandardCursorType.Arrow);
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);
    private static readonly Cursor DragCursor = new(StandardCursorType.SizeAll);

    private readonly DispatcherTimer renderTimer;
    private readonly Point[] pourStreamCenters = new Point[PourStreamSampleCount];
    private readonly Point[] pourStreamLeft = new Point[PourStreamSampleCount];
    private readonly Point[] pourStreamRight = new Point[PourStreamSampleCount];
    private readonly StringBuilder pourBodyBuilder = new(512);
    private readonly StringBuilder pourHighlightBuilder = new(256);
    private readonly SpriteAtlas sprites = new();
    private readonly ParticleSystem particles = new();
    private DateTimeOffset lastFrame = DateTimeOffset.UtcNow;
    private DateTimeOffset lastPointerSample = DateTimeOffset.UtcNow;
    private DragMode dragMode;
    private QuantityPickerMode quantityPickerMode;
    private IngredientSlotViewModel? quantityPickerIngredient;
    private IngredientSlotViewModel? armedIngredient;
    private bool hourglassFlipArmed;
    private IngredientSlotViewModel? draggedIngredient;
    private int draggedIngredientCount = 1;
    private BaseSlotViewModel? draggedBase;
    private Point dragStart;
    private Point dragTarget;
    private Point dragVisual;
    private Point previousPointer;
    private Vector pointerVelocity;
    private AlchemyHudTarget hoveredHudTarget = AlchemyHudTarget.None;
    private AlchemyHudTarget pressedHudTarget = AlchemyHudTarget.None;
    private AlchemyHudTarget activatedHudTarget = AlchemyHudTarget.None;
    private double activationFeedbackRemaining;
    private bool pointerInside;
    private Point pestlePoint = new(530, 420);
    private double grindingDistance;
    private double grindingVisualProgress;
    private bool poundArmed;
    private double previousPestleTipY;
    private double powderPickup;
    private string? draggedPowderIngredientId;
    private bool powderReturning;
    private string? returningPowderIngredientId;
    private Point powderReturnPosition;
    private string? mortarVisualIngredientId;
    private double mortarDebrisAccumulator;
    private double powderTrailAccumulator;
    private Point mortarContactPoint = new(528, 490);
    private double mortarReveal;
    private bool mortarDropActive;
    private Point mortarDropPosition;
    private Vector mortarDropVelocity;
    private double mortarDropRotation;
    private double mortarDropAngularVelocity;
    private int mortarDropBounces;
    private int mortarDropRemaining;
    private int mortarDropSettledCount;
    private string? mortarDropIngredientId;
    private double mortarImpact;
    private double mortarGrindingEnergy;
    private double mortarGrindingPhase;
    private Vector mortarHerbOffset;
    private Vector mortarHerbVelocity;
    private double mortarHerbRotation;
    private double mortarHerbAngularVelocity;
    private double mortarHerbCompression;
    private double mortarHerbIdlePhase;
    private double bellowsPull;
    private double bellowsMaxPull;
    private double lastBellowsY;
    private bool bellowsBlown;
    private bool bellowsSoundPlayed;
    private double pourTilt;
    private double pourProgress;
    private bool pourCommitted;
    private BaseLiquid? pendingPourBase;
    private double pendingPourProgress;
    private BaseLiquid? pourVisualLiquid;
    private BottleCapPhase bottleCapPhase;
    private double bottleCapProgress;
    private double pourFlowRate;
    private double pourTail;
    private double pourPhase;
    private double pourImpactAccumulator;
    private PourTrajectory? activePourTrajectory;
    private Point pourImpactPoint = new(820, 400);
    private double cauldronLiquidLevel;
    private double liquidRippleTime;
    private double liquidImpact;
    private bool completionPresentationWasComplete;
    private bool completionPresentationDismissed;
    private double completionPresentationProgress;
    private double completionPresentationTime;
    private double hourglassRotation;
    private double hourglassSandPhase;
    private bool hourglassVisualRunning;
    private double hourglassVisualProgress;
    private double cauldronGestureOffset;
    private double distillerPull;
    private bool bottleCollectionActive;
    private BottleCollectionSource bottleCollectionSource;
    private double bottleCollectionProgress;
    private Point bottleCollectionPosition;
    private Point bottleCollectionStart;
    private bool distillerBottleInstalled;
    private double distillerBottleInstallProgress;
    private Point distillerBottleInstallStart;
    private Point distillerBottlePosition;
    private Point completionBottleOrigin = AlchemyHudLayout.BrewShowcaseBottleStart;
    private double recipeTurnProgress;
    private int recipeTurnDirection;
    private bool recipeTurnActive;
    private double sceneScale = 1;
    private Point sceneOffset;
    private double bubbleSpawnAccumulator;
    private double steamSpawnAccumulator;
    private readonly Random random = new(314159);
    private AlchemyWorkshopViewModel? subscribedViewModel;
    private TopLevel? subscribedTopLevel;
    private bool isAttached;

    public AlchemyCanvas()
    {
        ClipToBounds = true;
        Focusable = true;
        renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        renderTimer.Tick += RenderFrame;
        PointerCaptureLost += (_, _) => CancelPointerInteraction();
    }

    public double GrindProgress => DataContext is AlchemyWorkshopViewModel vm ? vm.GrindingProgress : 0;

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        CalculateSceneTransform();
        sprites.DecodeScale = sceneScale * (TopLevel.GetTopLevel(this)?.RenderScaling ?? 1);
        context.DrawRectangle(Brush(0xFF090705), null, new Rect(Bounds.Size));

        using (context.PushTransform(Matrix.CreateTranslation(sceneOffset.X, sceneOffset.Y)))
        using (context.PushTransform(Matrix.CreateScale(sceneScale, sceneScale)))
        using (context.PushClip(new Rect(0, 0, SceneWidth, SceneHeight)))
        {
            DrawWorkshop(context);
            particles.Render(context);
            DrawDragLayer(context);
            DrawQuantityPickerModern(context);
            DrawRecipeBookModern(context);
            DrawCompletionPresentation(context);
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? SceneWidth : availableSize.Width;
        var height = double.IsInfinity(availableSize.Height) ? SceneHeight : availableSize.Height;
        return new Size(Math.Max(1, width), Math.Max(1, height));
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
        SubscribeToViewModel();
        RequestRenderFrame();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        isAttached = false;
        renderTimer.Stop();
        if (subscribedTopLevel is not null)
        {
            subscribedTopLevel.PropertyChanged -= TopLevel_OnPropertyChanged;
            subscribedTopLevel = null;
        }
        UnsubscribeFromViewModel();
        CancelPointerInteraction();
        sprites.Clear();
        ReleaseSharedRenderCaches();
        base.OnDetachedFromVisualTree(e);
    }

    private static void ReleaseSharedRenderCaches()
    {
        TextLayoutCache.Clear();
        PenCache.Clear();
        BrushCache.Clear();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DataContextProperty)
        {
            SubscribeToViewModel();
            InvalidateVisual();
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        RequestRenderFrame();
        if (DataContext is not AlchemyWorkshopViewModel vm)
        {
            return;
        }

        Focus();
        var point = ToScene(e.GetPosition(this));
        pointerInside = true;
        previousPointer = point;
        dragStart = point;
        dragTarget = point;
        dragVisual = point;
        pointerVelocity = default;
        lastPointerSample = DateTimeOffset.UtcNow;
        UpdateHoveredTarget(vm, point);
        pressedHudTarget = hoveredHudTarget;

        if (vm.IsBrewComplete && !completionPresentationDismissed)
        {
            completionPresentationDismissed = true;
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (RecipeTabHitbox.Contains(point))
        {
            CloseQuantityPicker();
            var opening = !vm.IsRecipeBookOpen;
            ActivateHudTarget(
                new AlchemyHudTarget(AlchemyHudTargetKind.RecipeBook),
                vm,
                opening ? AlchemySoundCue.BookOpen : AlchemySoundCue.BookClose);
            vm.IsRecipeBookOpen = !vm.IsRecipeBookOpen;
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (vm.IsRecipeBookOpen)
        {
            if (!HandleRecipeBookPress(vm, point))
            {
                vm.PlayUiInteraction(AlchemySoundCue.BookClose, .42);
                vm.IsRecipeBookOpen = false;
                InvalidateVisual();
            }

            e.Handled = true;
            return;
        }

        if (ResetRagHitbox.Contains(point))
        {
            ActivateHudTarget(new AlchemyHudTarget(AlchemyHudTargetKind.Reset), vm);
            ResetPourTransaction();
            ResetHourglassVisual();
            ResetBottleCollection();
            CloseQuantityPicker();
            armedIngredient = null;
            hourglassFlipArmed = false;
            vm.ResetFromGesture();
            particles.EmitDust(new Point(1395, 42), 16, BrushColor(0xFFCDB88F));
            e.Handled = true;
            return;
        }

        if (TryHandleQuantityPickerPress(vm, point))
        {
            e.Handled = true;
            return;
        }

        CloseQuantityPicker();

        if (TryHitIngredient(vm, point, out var ingredient))
        {
            if (ingredient.Quantity <= 0)
            {
                return;
            }

            if (!ReferenceEquals(armedIngredient, ingredient))
            {
                ActivateHudTarget(new AlchemyHudTarget(AlchemyHudTargetKind.Ingredient, ingredient.Index), vm);
                quantityPickerMode = QuantityPickerMode.Ingredient;
                quantityPickerIngredient = ingredient;
                hourglassFlipArmed = false;
                InvalidateVisual();
                e.Handled = true;
                return;
            }

            armedIngredient = null;
            dragMode = DragMode.Ingredient;
            draggedIngredient = ingredient;
            draggedIngredientCount = Math.Clamp(vm.SelectedCount, 1, Math.Max(1, ingredient.Quantity));
            dragVisual = IngredientSlotCenter(ingredient.Index);
            CapturePointer(e);
            e.Handled = true;
            return;
        }

        if (TryHitBase(vm, point, out var baseSlot))
        {
            if (pendingPourBase is { } pendingBase && pendingBase != baseSlot.Value)
            {
                particles.EmitDust(point, 8, BrushColor(0xFFC9A66A));
                e.Handled = true;
                return;
            }

            dragMode = DragMode.BaseBottle;
            draggedBase = baseSlot;
            pourTilt = 0;
            pourProgress = pendingPourBase == baseSlot.Value ? pendingPourProgress : 0;
            pourCommitted = false;
            pourVisualLiquid = baseSlot.Value;
            bottleCapPhase = BottleCapPhase.Closed;
            bottleCapProgress = 0;
            pourFlowRate = 0;
            pourTail = 0;
            dragVisual = BaseSlotCenter(Array.IndexOf(vm.BaseLiquids.ToArray(), baseSlot));
            CapturePointer(e);
            e.Handled = true;
            return;
        }

        if (vm.IsMortarLoaded
            && vm.GrindingProgress >= 0.999
            && grindingVisualProgress >= 0.985
            && MortarPowderHitbox.Contains(point))
        {
            dragMode = DragMode.GroundPowder;
            draggedPowderIngredientId = MortarIngredientId(vm) ?? mortarVisualIngredientId ?? "belladonna";
            dragVisual = MortarPowderHitbox.Center;
            powderTrailAccumulator = 0;
            CapturePointer(e);
            e.Handled = true;
            return;
        }

        if (PestleHitbox.Contains(point) && vm.IsMortarLoaded && vm.GrindingProgress < 0.999)
        {
            dragMode = DragMode.Pestle;
            grindingDistance = vm.GrindingProgress * GrindDistanceRequired;
            pestlePoint = point;
            previousPestleTipY = point.Y + 70;
            poundArmed = previousPestleTipY <= PoundReleaseTipY;
            CapturePointer(e);
            e.Handled = true;
            return;
        }

        if (BellowsHandleHitbox.Contains(point))
        {
            dragMode = DragMode.Bellows;
            lastBellowsY = point.Y;
            bellowsMaxPull = bellowsPull;
            bellowsBlown = false;
            bellowsSoundPlayed = false;
            CapturePointer(e);
            e.Handled = true;
            return;
        }

        if (HourglassHitbox.Contains(point) && !vm.IsHourglassRunning)
        {
            if (!hourglassFlipArmed)
            {
                ActivateHudTarget(new AlchemyHudTarget(AlchemyHudTargetKind.Hourglass), vm);
                quantityPickerMode = QuantityPickerMode.Hourglass;
                quantityPickerIngredient = null;
                armedIngredient = null;
                InvalidateVisual();
                e.Handled = true;
                return;
            }

            hourglassFlipArmed = false;
            dragMode = DragMode.Hourglass;
            hourglassRotation = 0;
            CapturePointer(e);
            e.Handled = true;
            return;
        }

        if (CauldronHookHitbox.Contains(point))
        {
            dragMode = DragMode.CauldronHook;
            cauldronGestureOffset = 0;
            CapturePointer(e);
            e.Handled = true;
            return;
        }

        if (DistillerLeverHitbox.Contains(point))
        {
            if (CurrentStepKind(vm) == RecipeStepKind.Distill
                && !BottleCollectionVisual.CanOperatePump(
                    vm.Snapshot.Recipe,
                    CurrentStepKind(vm),
                    distillerBottleInstalled))
            {
                particles.EmitDust(DistillerReceiverHitbox.Center, 7, BrushColor(0xFFC9A66A));
                e.Handled = true;
                InvalidateVisual();
                return;
            }

            dragMode = DragMode.DistillerLever;
            distillerPull = 0;
            CapturePointer(e);
            e.Handled = true;
            return;
        }

        if (ProductBottleHitbox.Contains(point)
            && !bottleCollectionActive
            && !distillerBottleInstalled)
        {
            dragMode = DragMode.ProductBottle;
            dragVisual = ProductBottleHitbox.Center;
            CapturePointer(e);
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        pointerInside = true;
        if (dragMode != DragMode.None)
        {
            RequestRenderFrame();
        }
        var point = ToScene(e.GetPosition(this));
        var now = DateTimeOffset.UtcNow;
        var seconds = Math.Max(0.001, (now - lastPointerSample).TotalSeconds);
        var delta = point - previousPointer;
        pointerVelocity = new Vector(delta.X / seconds, delta.Y / seconds);
        lastPointerSample = now;
        dragTarget = point;
        UpdateHoveredTarget(DataContext as AlchemyWorkshopViewModel, point);

        switch (dragMode)
        {
            case DragMode.Pestle:
                MovePestle(point, delta);
                break;
            case DragMode.GroundPowder:
                break;
            case DragMode.Bellows:
                MoveBellows(point);
                break;
            case DragMode.BaseBottle:
                pourTilt = Math.Clamp(((dragStart.Y - point.Y) * 0.78) + 12, 0, 112);
                break;
            case DragMode.Hourglass:
                hourglassRotation = Math.Clamp(
                    (Math.Abs(point.X - dragStart.X) * 1.15) + (Math.Max(0, dragStart.Y - point.Y) * 1.4),
                    0,
                    180);
                break;
            case DragMode.CauldronHook:
                cauldronGestureOffset = Math.Clamp(point.Y - dragStart.Y, -82, 82);
                break;
            case DragMode.DistillerLever:
                distillerPull = Math.Clamp((point.Y - dragStart.Y) / 108, 0, 1);
                break;
        }

        previousPointer = point;
        if (dragMode != DragMode.None)
        {
            e.Handled = true;
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (DataContext is not AlchemyWorkshopViewModel vm)
        {
            CancelPointerInteraction();
            return;
        }

        var point = ToScene(e.GetPosition(this));
        switch (dragMode)
        {
            case DragMode.Ingredient when draggedIngredient is not null:
                ReleaseIngredient(vm, draggedIngredient, point);
                break;
            case DragMode.BaseBottle:
                if (!pourCommitted && draggedBase is not null && pourProgress > 0)
                {
                    pendingPourBase = draggedBase.Value;
                    pendingPourProgress = pourProgress;
                }
                break;
            case DragMode.GroundPowder:
                if (CauldronMouth.Contains(point))
                {
                    var ingredientId = draggedPowderIngredientId ?? MortarIngredientId(vm) ?? "belladonna";
                    if (vm.CompleteGrindingIntoCauldronGesture())
                    {
                        particles.EmitPowderTrail(new Point(816, 382), 26, IngredientColor(ingredientId));
                        pourImpactPoint = new Point(816, 386);
                        liquidImpact = Math.Max(liquidImpact, 0.62);
                        particles.EmitSplash(pourImpactPoint, 6, IngredientColor(ingredientId));
                    }
                    else
                    {
                        powderReturning = true;
                        returningPowderIngredientId = ingredientId;
                        powderReturnPosition = dragVisual;
                    }
                }
                else
                {
                    powderReturning = true;
                    returningPowderIngredientId = draggedPowderIngredientId ?? MortarIngredientId(vm) ?? "belladonna";
                    powderReturnPosition = dragVisual;
                }
                break;
            case DragMode.Bellows:
                CompleteBellowsBlow(vm);
                break;
            case DragMode.Hourglass:
                if (hourglassRotation >= 132)
                {
                    hourglassVisualRunning = true;
                    hourglassVisualProgress = 0;
                    vm.FlipHourglassFromGesture();
                    particles.EmitDust(new Point(1245, 455), 7, BrushColor(0xFFDAB65D));
                }
                break;
            case DragMode.CauldronHook:
                if (Math.Abs(cauldronGestureOffset) >= 42)
                {
                    vm.ToggleCauldronFromGesture();
                }
                break;
            case DragMode.DistillerLever:
                if (distillerPull >= 0.72)
                {
                    var wasDistilled = vm.Snapshot.Distilled;
                    vm.DistillFromGesture();
                    particles.EmitSteam(new Point(1328, 542), 14);
                    if (distillerBottleInstalled
                        && !wasDistilled
                        && vm.Snapshot.Distilled
                        && CurrentStepKind(vm) == RecipeStepKind.Bottle)
                    {
                        var target = BottleCollectionVisual.Resolve(vm.Snapshot.Recipe, CurrentPotOffset());
                        BeginBottleCollection(target, target.RestingCenter);
                    }
                }
                break;
            case DragMode.ProductBottle:
            {
                var target = BottleCollectionVisual.Resolve(vm.Snapshot.Recipe, CurrentPotOffset());
                if (target.Hitbox.Contains(point))
                {
                    if (target.Source == BottleCollectionSource.Cauldron)
                    {
                        BeginBottleCollection(target, point);
                    }
                    else if (BottleCollectionVisual.CanInstallCondenserBottle(
                                 vm.Snapshot.Recipe,
                                 CurrentStepKind(vm),
                                 vm.Snapshot.Distilled))
                    {
                        InstallDistillerBottle(target, point);
                    }
                }

                break;
            }
        }

        e.Pointer.Capture(null);
        ResetPointerState();
        pressedHudTarget = AlchemyHudTarget.None;
        UpdateHoveredTarget(vm, point);
        e.Handled = true;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        pointerInside = false;
        hoveredHudTarget = AlchemyHudTarget.None;
        if (dragMode == DragMode.None)
        {
            pressedHudTarget = AlchemyHudTarget.None;
        }

        Cursor = ArrowCursor;
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (DataContext is not AlchemyWorkshopViewModel vm)
        {
            return;
        }

        if (e.Key == Key.B)
        {
            vm.PlayUiInteraction(vm.IsRecipeBookOpen ? AlchemySoundCue.BookClose : AlchemySoundCue.BookOpen, .42);
            vm.IsRecipeBookOpen = !vm.IsRecipeBookOpen;
            CloseQuantityPicker();
            e.Handled = true;
        }
        else if (vm.IsBrewComplete && !completionPresentationDismissed && e.Key == Key.Escape)
        {
            completionPresentationDismissed = true;
            InvalidateVisual();
            e.Handled = true;
        }
        else if (vm.IsRecipeBookOpen && e.Key == Key.Escape)
        {
            vm.PlayUiInteraction(AlchemySoundCue.BookClose, .42);
            vm.IsRecipeBookOpen = false;
            CloseQuantityPicker();
            e.Handled = true;
        }
        else if (vm.IsRecipeBookOpen)
        {
            e.Handled = true;
        }
        else if (e.Key == Key.R)
        {
            vm.PlayUiInteraction(AlchemySoundCue.UiClick, .38);
            ResetPourTransaction();
            ResetHourglassVisual();
            ResetBottleCollection();
            CloseQuantityPicker();
            armedIngredient = null;
            hourglassFlipArmed = false;
            vm.ResetFromGesture();
            e.Handled = true;
        }
    }

    private void RenderFrame(object? sender, EventArgs e)
    {
        if (!ShouldRenderFrames())
        {
            renderTimer.Stop();
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var dt = Math.Clamp((now - lastFrame).TotalSeconds, 0, 0.05);
        lastFrame = now;

        dragVisual = Lerp(dragVisual, dragTarget, 1 - Math.Exp(-dt * 18));
        particles.Update(dt);
        UpdatePour(dt);
        UpdateCauldronLiquidVisual(dt);
        UpdateAmbientParticles(dt);
        UpdatePowderTransferVisuals(dt);
        UpdateMortarVisuals(dt);
        UpdateDistillerBottleInstallation(dt);
        UpdateBottleCollection(dt);
        UpdateRecipeBookTurn(dt);
        UpdateCompletionPresentation(dt);
        if (activationFeedbackRemaining > 0)
        {
            activationFeedbackRemaining = Math.Max(0, activationFeedbackRemaining - dt);
            if (activationFeedbackRemaining <= 0)
            {
                activatedHudTarget = AlchemyHudTarget.None;
            }
        }

        if (DataContext is AlchemyWorkshopViewModel hourglassVm)
        {
            if (hourglassVm.IsHourglassRunning)
            {
                hourglassVisualRunning = true;
                hourglassVisualProgress = hourglassVm.HourglassProgress;
            }
            else if (hourglassVisualRunning)
            {
                var visualDuration = Math.Max(1, hourglassVm.SelectedTurns) * 4.0;
                hourglassVisualProgress = Math.Min(1, hourglassVisualProgress + (dt / visualDuration));
                if (hourglassVisualProgress >= 0.999)
                {
                    hourglassVisualRunning = false;
                }
            }
        }

        if (hourglassVisualRunning
            || (dragMode == DragMode.Hourglass && hourglassRotation > 24))
        {
            hourglassSandPhase += dt * 11.5;
        }
        else
        {
            hourglassSandPhase = 0;
        }

        if (dragMode != DragMode.Bellows)
        {
            bellowsPull = Lerp(bellowsPull, 0, 1 - Math.Exp(-dt * 14));
        }

        if (dragMode != DragMode.Hourglass)
        {
            hourglassRotation = Lerp(hourglassRotation, 0, 1 - Math.Exp(-dt * 10));
        }

        if (dragMode != DragMode.CauldronHook)
        {
            cauldronGestureOffset = Lerp(cauldronGestureOffset, 0, 1 - Math.Exp(-dt * 12));
        }

        if (dragMode != DragMode.DistillerLever)
        {
            distillerPull = Lerp(distillerPull, 0, 1 - Math.Exp(-dt * 12));
        }

        if (HasActiveVisualAnimation())
        {
            InvalidateVisual();
        }
        else
        {
            renderTimer.Stop();
        }
    }

    private void UpdateCompletionPresentation(double dt)
    {
        var complete = DataContext is AlchemyWorkshopViewModel { IsBrewComplete: true };
        if (!complete)
        {
            completionPresentationWasComplete = false;
            completionPresentationDismissed = false;
            completionPresentationProgress = 0;
            completionPresentationTime = 0;
            return;
        }

        if (!completionPresentationWasComplete)
        {
            completionPresentationWasComplete = true;
            completionPresentationDismissed = false;
            completionPresentationProgress = 0;
            completionPresentationTime = 0;
        }

        if (!completionPresentationDismissed)
        {
            completionPresentationProgress = Math.Min(1, completionPresentationProgress + (dt / 0.62));
            completionPresentationTime += dt;
        }
    }

    private bool HasActiveVisualAnimation()
    {
        var vm = DataContext as AlchemyWorkshopViewModel;
        var targetLiquidLevel = vm?.LiquidOpacity ?? 0;
        var targetMortarReveal = vm?.IsMortarLoaded == true ? 1d : 0d;

        return dragMode != DragMode.None
               || particles.HasActiveParticles
               || powderReturning
               || mortarDropActive
               || hourglassVisualRunning
               || vm?.IsHourglassRunning == true
               || Math.Abs(mortarReveal - targetMortarReveal) > 0.002
               || Math.Abs(cauldronLiquidLevel - targetLiquidLevel) > 0.002
               || Math.Abs(grindingVisualProgress - (vm?.GrindingProgress ?? 0)) > 0.002
               || (vm?.IsBrewComplete == true && !completionPresentationDismissed
                   && completionPresentationProgress < 0.999)
               || mortarImpact > 0.002
               || mortarGrindingEnergy > 0.002
               || powderPickup > 0.002
               || pourTail > 0.002
               || liquidImpact > 0.002
               || bellowsPull > 0.002
               || hourglassRotation > 0.002
               || Math.Abs(cauldronGestureOffset) > 0.002
               || distillerPull > 0.002
               || bottleCollectionActive
               || (distillerBottleInstalled && distillerBottleInstallProgress < 0.999)
               || recipeTurnActive
               || activationFeedbackRemaining > 0
               || Math.Abs(dragVisual.X - dragTarget.X) > 0.1
               || Math.Abs(dragVisual.Y - dragTarget.Y) > 0.1;
    }

    private void SubscribeToViewModel()
    {
        var next = DataContext as AlchemyWorkshopViewModel;
        if (ReferenceEquals(subscribedViewModel, next))
        {
            return;
        }

        UnsubscribeFromViewModel();
        subscribedViewModel = next;
        if (subscribedViewModel is not null)
        {
            subscribedViewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void UnsubscribeFromViewModel()
    {
        if (subscribedViewModel is not null)
        {
            subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            subscribedViewModel = null;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        RequestRenderFrame();
        InvalidateVisual();
    }

    private void RequestRenderFrame()
    {
        if (!ShouldRenderFrames() || renderTimer.IsEnabled)
        {
            return;
        }

        lastFrame = DateTimeOffset.UtcNow;
        renderTimer.Start();
    }

    private void TopLevel_OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != Visual.IsVisibleProperty && e.Property != Window.WindowStateProperty)
        {
            return;
        }

        if (ShouldRenderFrames())
        {
            RequestRenderFrame();
            InvalidateVisual();
        }
        else
        {
            renderTimer.Stop();
        }
    }

    internal static bool ShouldRenderFrames(
        bool isAttached,
        bool isEffectivelyVisible,
        bool isTopLevelVisible,
        bool isWindowMinimized) =>
        isAttached && isEffectivelyVisible && isTopLevelVisible && !isWindowMinimized;

    private bool ShouldRenderFrames() => ShouldRenderFrames(
        isAttached,
        IsEffectivelyVisible,
        subscribedTopLevel?.IsVisible == true,
        subscribedTopLevel is Window { WindowState: WindowState.Minimized });

    private void UpdatePowderTransferVisuals(double dt)
    {
        if (!powderReturning)
        {
            return;
        }

        powderReturnPosition = Lerp(
            powderReturnPosition,
            MortarPowderHitbox.Center,
            1 - Math.Exp(-dt * 15));
        var remaining = powderReturnPosition - MortarPowderHitbox.Center;
        if ((remaining.X * remaining.X) + (remaining.Y * remaining.Y) < 9)
        {
            powderReturning = false;
            returningPowderIngredientId = null;
        }
    }

    private void UpdatePour(double dt)
    {
        pourPhase += dt * (4.2 + (pourFlowRate * 7.8));

        if (dragMode != DragMode.BaseBottle || draggedBase is null)
        {
            pourFlowRate = 0;
            SetPouringSound(false);
            pourTail = Math.Max(0, pourTail - (dt * 4.8));
            if (pourTail <= 0.001)
            {
                activePourTrajectory = null;
            }

            return;
        }

        var overCauldron = CauldronHitbox.Contains(dragTarget);
        if (bottleCapPhase == BottleCapPhase.Closed && overCauldron)
        {
            bottleCapPhase = BottleCapPhase.Opening;
        }

        if (bottleCapPhase == BottleCapPhase.Opening && overCauldron)
        {
            bottleCapProgress = Math.Min(1, bottleCapProgress + (dt / 0.30));
            if (bottleCapProgress >= 1)
            {
                bottleCapPhase = BottleCapPhase.Open;
            }
        }
        else if (bottleCapPhase == BottleCapPhase.Opening)
        {
            bottleCapProgress = Math.Max(0, bottleCapProgress - (dt / 0.18));
            if (bottleCapProgress <= 0)
            {
                bottleCapPhase = BottleCapPhase.Closed;
            }
        }

        if (pourCommitted)
        {
            pourFlowRate = 0;
            SetPouringSound(false);
            pourTail = Math.Max(0, pourTail - (dt * 5.4));
            return;
        }

        var remaining = Math.Max(0, 1 - pourProgress);
        var flow = PourVisualPhysics.CalculateFlow(
            draggedBase.Value,
            pourTilt,
            bottleCapPhase,
            bottleCapProgress,
            remaining);
        var mouth = PourVisualPhysics.GetBottleMouth(
            dragVisual,
            pourTilt,
            PourVisualPhysics.BottleScaleWhileDragging);
        var surfaceY = GetCauldronSurfaceBaselineY(cauldronLiquidLevel, CurrentPotOffset());
        var trajectory = PourVisualPhysics.CreateTrajectory(
            mouth,
            pourTilt,
            draggedBase.Value,
            flow,
            surfaceY);

        if (trajectory is null && flow > 0.001)
        {
            trajectory = PourVisualPhysics.CreateTrajectory(
                mouth,
                pourTilt,
                draggedBase.Value,
                flow,
                Math.Max(surfaceY, mouth.Y + 180));
        }

        pourFlowRate = flow;
        SetPouringSound(flow > 0.01, flow);
        if (trajectory is { } visibleTrajectory)
        {
            activePourTrajectory = visibleTrajectory;
            pourTail = Math.Min(1, pourTail + (dt * 12));
        }
        else
        {
            pourTail = Math.Max(0, pourTail - (dt * 5.4));
        }

        if (trajectory is not { HitsCauldron: true } hitTrajectory)
        {
            return;
        }

        var delta = PourVisualPhysics.ProgressDelta(draggedBase.Value, flow, dt, true);
        if (delta <= 0)
        {
            return;
        }

        pendingPourBase ??= draggedBase.Value;
        pourProgress = Math.Clamp(pourProgress + delta, 0, 1);
        pendingPourProgress = pourProgress;
        pourImpactPoint = hitTrajectory.Impact;
        liquidImpact = Math.Max(liquidImpact, 0.28 + (flow * 0.72));
        pourImpactAccumulator += dt * (4 + (flow * 18));
        while (pourImpactAccumulator >= 1)
        {
            pourImpactAccumulator--;
            particles.EmitPourImpact(hitTrajectory.Impact, 1, LiquidColor(draggedBase.Value), flow);
        }

        if (pourProgress >= 1 && DataContext is AlchemyWorkshopViewModel vm)
        {
            CommitPour(vm);
        }
    }

    private void UpdateCauldronLiquidVisual(double dt)
    {
        var confirmedLevel = DataContext is AlchemyWorkshopViewModel vm ? vm.LiquidOpacity : 0;
        var inProgressPour = Math.Max(
            pendingPourProgress,
            dragMode == DragMode.BaseBottle ? pourProgress : 0);
        var pouringLevel = inProgressPour * 0.78;
        var targetLevel = Math.Max(confirmedLevel, pouringLevel);
        var rate = targetLevel > cauldronLiquidLevel ? 1.18 : 3.6;
        cauldronLiquidLevel = targetLevel > cauldronLiquidLevel
            ? Math.Min(targetLevel, cauldronLiquidLevel + (dt * rate))
            : Math.Max(targetLevel, cauldronLiquidLevel - (dt * rate));
        liquidRippleTime += dt * (2.2 + (cauldronLiquidLevel * 2.4));
        liquidImpact = Math.Max(0, liquidImpact - (dt * 1.85));
    }

    private void BeginBottleCollection(BottleCollectionTarget target, Point droppedAt)
    {
        bottleCollectionActive = true;
        bottleCollectionSource = target.Source;
        bottleCollectionProgress = 0;
        bottleCollectionStart = droppedAt;
        bottleCollectionPosition = droppedAt;
        completionBottleOrigin = BottleCollectionVisual.ClampRestingCenter(target, droppedAt);
        if (target.Source == BottleCollectionSource.Cauldron)
        {
            pourImpactPoint = new Point(completionBottleOrigin.X, GetCauldronSurfaceBaselineY(cauldronLiquidLevel, CurrentPotOffset()));
            liquidImpact = Math.Max(liquidImpact, 0.9);
        }
        else
        {
            distillerBottlePosition = target.RestingCenter;
            distillerBottleInstallProgress = 1;
        }

        RequestRenderFrame();
    }

    private void InstallDistillerBottle(BottleCollectionTarget target, Point droppedAt)
    {
        distillerBottleInstalled = true;
        distillerBottleInstallProgress = 0;
        distillerBottleInstallStart = droppedAt;
        distillerBottlePosition = droppedAt;
        bottleCollectionProgress = 0;
        completionBottleOrigin = target.RestingCenter;
        particles.EmitSparkle(target.RestingCenter + new Vector(0, 38), 6);
        RequestRenderFrame();
    }

    private void UpdateDistillerBottleInstallation(double dt)
    {
        if (!distillerBottleInstalled
            || bottleCollectionActive
            || distillerBottleInstallProgress >= 0.999
            || DataContext is not AlchemyWorkshopViewModel vm)
        {
            return;
        }

        var target = BottleCollectionVisual.Resolve(vm.Snapshot.Recipe, CurrentPotOffset());
        if (target.Source != BottleCollectionSource.Condenser)
        {
            ResetBottleCollection();
            return;
        }

        distillerBottleInstallProgress = Math.Min(1, distillerBottleInstallProgress + (dt / 0.24));
        var settle = EaseOutBack(distillerBottleInstallProgress);
        distillerBottlePosition = Lerp(distillerBottleInstallStart, target.RestingCenter, settle);
    }

    private void UpdateBottleCollection(double dt)
    {
        if (!bottleCollectionActive)
        {
            return;
        }

        if (DataContext is not AlchemyWorkshopViewModel vm || vm.Snapshot.Base is null)
        {
            ResetBottleCollection();
            return;
        }

        var target = BottleCollectionVisual.Resolve(vm.Snapshot.Recipe, CurrentPotOffset());
        if (target.Source != bottleCollectionSource)
        {
            ResetBottleCollection();
            return;
        }

        bottleCollectionProgress = Math.Min(
            1,
            bottleCollectionProgress + (dt / BottleCollectionVisual.FillDurationSeconds));
        var restingCenter = BottleCollectionVisual.ClampRestingCenter(target, bottleCollectionStart);
        var settle = SmoothStep(0, 0.34, bottleCollectionProgress);
        bottleCollectionPosition = Lerp(bottleCollectionStart, restingCenter, settle);
        completionBottleOrigin = bottleCollectionPosition;
        if (bottleCollectionSource == BottleCollectionSource.Cauldron)
        {
            pourImpactPoint = new Point(
                bottleCollectionPosition.X,
                GetCauldronSurfaceBaselineY(cauldronLiquidLevel, CurrentPotOffset()));
            liquidImpact = Math.Max(liquidImpact, 0.34 + ((1 - bottleCollectionProgress) * 0.42));
        }
        else
        {
            distillerBottlePosition = restingCenter;
        }

        if (bottleCollectionProgress < 0.999)
        {
            return;
        }

        bottleCollectionActive = false;
        if (bottleCollectionSource == BottleCollectionSource.Condenser)
        {
            distillerBottleInstalled = false;
        }

        vm.BottleFromGesture();
        particles.EmitSparkle(completionBottleOrigin, 18);
    }

    private void UpdateAmbientParticles(double dt)
    {
        if (DataContext is not AlchemyWorkshopViewModel vm || vm.LiquidOpacity <= 0 || cauldronLiquidLevel < 0.08)
        {
            return;
        }

        var heatFactor = Math.Clamp((vm.Temperature - 28) / 56, 0, 1.25);
        if (heatFactor > 0.01)
        {
            bubbleSpawnAccumulator += dt * (0.35 + (heatFactor * 7.5));
            while (bubbleSpawnAccumulator >= 1)
            {
                bubbleSpawnAccumulator--;
                particles.EmitBubble(
                    new Point(704 + (random.NextDouble() * 232), 390 + (random.NextDouble() * 14)),
                    heatFactor);
            }
        }

        if (vm.Temperature > 54)
        {
            steamSpawnAccumulator += dt * (2 + (heatFactor * 5));
            while (steamSpawnAccumulator >= 1)
            {
                steamSpawnAccumulator--;
                particles.EmitSteam(new Point(675 + (random.NextDouble() * 205), 348), 1);
            }
        }
    }

    private void UpdateMortarVisuals(double dt)
    {
        var vm = DataContext as AlchemyWorkshopViewModel;
        var loaded = vm?.IsMortarLoaded == true;
        var revealTarget = loaded ? 1.0 : 0.0;
        var revealRate = loaded ? 3.25 : 5.5;
        mortarReveal = Lerp(mortarReveal, revealTarget, 1 - Math.Exp(-dt * revealRate));
        if (Math.Abs(mortarReveal - revealTarget) < 0.002)
        {
            mortarReveal = revealTarget;
        }

        mortarImpact = Math.Max(0, mortarImpact - (dt * 3.8));
        mortarGrindingEnergy = Math.Max(0, mortarGrindingEnergy - (dt * 4.6));
        var pickupTarget = dragMode == DragMode.GroundPowder || powderReturning ? 1.0 : 0.0;
        var pickupRate = pickupTarget > powderPickup ? 20.0 : 10.0;
        powderPickup = Lerp(powderPickup, pickupTarget, 1 - Math.Exp(-dt * pickupRate));

        if (!loaded || vm is null)
        {
            mortarDropActive = false;
            mortarDropRemaining = 0;
            mortarDropSettledCount = 0;
            mortarDropIngredientId = null;
            mortarVisualIngredientId = null;
            powderReturning = false;
            returningPowderIngredientId = null;
            grindingVisualProgress = Lerp(grindingVisualProgress, 0, 1 - Math.Exp(-dt * 18));
            mortarHerbOffset = default;
            mortarHerbVelocity = default;
            mortarHerbRotation = 0;
            mortarHerbAngularVelocity = 0;
            mortarHerbCompression = 0;
            return;
        }

        var ingredientId = MortarIngredientId(vm) ?? mortarVisualIngredientId ?? "belladonna";
        if (!string.Equals(mortarVisualIngredientId, ingredientId, StringComparison.Ordinal))
        {
            mortarVisualIngredientId = ingredientId;
            grindingVisualProgress = vm.GrindingProgress;
            mortarDebrisAccumulator = 0;
        }

        grindingVisualProgress = Lerp(
            grindingVisualProgress,
            vm.GrindingProgress,
            1 - Math.Exp(-dt * 13.5));

        if (dragMode == DragMode.GroundPowder)
        {
            powderTrailAccumulator += dt * 18;
            while (powderTrailAccumulator >= 1)
            {
                powderTrailAccumulator--;
                particles.EmitPowderTrail(dragVisual + new Vector(0, 10), 1, IngredientColor(ingredientId));
            }
        }
        else if (mortarGrindingEnergy > 0.035 && vm.GrindingProgress < 0.999)
        {
            mortarDebrisAccumulator += dt * (4 + (mortarGrindingEnergy * 24));
            while (mortarDebrisAccumulator >= 1)
            {
                mortarDebrisAccumulator--;
                particles.EmitGrindingDebris(
                    mortarContactPoint,
                    1,
                    IngredientColor(ingredientId),
                    vm.GrindingProgress);
            }
        }

        if (!mortarDropActive)
        {
            UpdateMortarHerbPhysics(dt);
            return;
        }

        mortarDropVelocity += new Vector(0, 720) * dt;
        mortarDropPosition += mortarDropVelocity * dt;
        mortarDropRotation += mortarDropAngularVelocity * dt;

        const double mortarFloor = 468;
        if (mortarDropPosition.Y < mortarFloor)
        {
            return;
        }

        mortarDropPosition = new Point(mortarDropPosition.X, mortarFloor);
        mortarImpact = 1;
        if (mortarDropIngredientId is not null)
        {
            particles.EmitGrindingDebris(
                new Point(mortarDropPosition.X, mortarFloor + 22),
                mortarDropBounces == 0 ? 10 : 4,
                IngredientColor(mortarDropIngredientId),
                0);
        }

        if (mortarDropBounces < 2 && Math.Abs(mortarDropVelocity.Y) > 72)
        {
            mortarDropVelocity = new Vector(mortarDropVelocity.X * 0.62, -Math.Abs(mortarDropVelocity.Y) * 0.31);
            mortarDropAngularVelocity *= -0.48;
            mortarDropBounces++;
            return;
        }

        mortarDropSettledCount++;
        if (mortarDropRemaining > 0 && mortarDropIngredientId is not null)
        {
            mortarDropRemaining--;
            BeginMortarDrop(
                mortarDropIngredientId,
                new Point(500 + (random.NextDouble() * 56), 374 + (random.NextDouble() * 16)),
                new Vector(-42 + (random.NextDouble() * 84), 18 + (random.NextDouble() * 42)),
                -10 + (random.NextDouble() * 20),
                -125 + (random.NextDouble() * 250));
            return;
        }

        mortarDropActive = false;
        mortarHerbOffset = new Vector(Math.Clamp(mortarDropPosition.X - 528, -56, 56), 0);
        mortarHerbVelocity = new Vector(mortarDropVelocity.X * 0.18, -8);
        mortarHerbRotation = mortarDropRotation;
        mortarHerbAngularVelocity = mortarDropAngularVelocity * 0.22;
        mortarHerbCompression = 0.16;
        mortarDropVelocity = default;
    }

    private void BeginMortarDrop(
        string ingredientId,
        Point position,
        Vector velocity,
        double rotation,
        double angularVelocity)
    {
        mortarDropActive = true;
        mortarDropIngredientId = ingredientId;
        mortarDropPosition = position;
        mortarDropVelocity = velocity;
        mortarDropRotation = rotation;
        mortarDropAngularVelocity = angularVelocity;
        mortarDropBounces = 0;
    }

    private void UpdateMortarHerbPhysics(double dt)
    {
        mortarHerbIdlePhase += dt * 1.7;

        var spring = new Vector(
            (-mortarHerbOffset.X * 17.5) + (Math.Sin(mortarHerbIdlePhase) * 1.25),
            (-mortarHerbOffset.Y * 25) + (Math.Cos(mortarHerbIdlePhase * 0.73) * 0.42));
        mortarHerbVelocity += spring * dt;
        mortarHerbVelocity *= Math.Exp(-dt * 3.6);
        mortarHerbOffset += mortarHerbVelocity * dt;

        const double horizontalLimit = 62;
        const double upperLimit = -10;
        const double lowerLimit = 11;
        if (mortarHerbOffset.X < -horizontalLimit || mortarHerbOffset.X > horizontalLimit)
        {
            mortarHerbOffset = new Vector(Math.Clamp(mortarHerbOffset.X, -horizontalLimit, horizontalLimit), mortarHerbOffset.Y);
            mortarHerbVelocity = new Vector(-mortarHerbVelocity.X * 0.42, mortarHerbVelocity.Y * 0.84);
        }

        if (mortarHerbOffset.Y < upperLimit || mortarHerbOffset.Y > lowerLimit)
        {
            mortarHerbOffset = new Vector(mortarHerbOffset.X, Math.Clamp(mortarHerbOffset.Y, upperLimit, lowerLimit));
            mortarHerbVelocity = new Vector(mortarHerbVelocity.X * 0.86, -mortarHerbVelocity.Y * 0.34);
        }

        mortarHerbAngularVelocity += -mortarHerbRotation * 9.5 * dt;
        mortarHerbAngularVelocity *= Math.Exp(-dt * 4.1);
        mortarHerbRotation = Math.Clamp(mortarHerbRotation + (mortarHerbAngularVelocity * dt), -18, 18);

        var compressionTarget = Math.Clamp((mortarGrindingEnergy * 0.18) + (mortarImpact * 0.12), 0, 0.24);
        mortarHerbCompression = Lerp(mortarHerbCompression, compressionTarget, 1 - Math.Exp(-dt * 10));
    }

    private void MovePestle(Point point, Vector delta)
    {
        if (DataContext is not AlchemyWorkshopViewModel vm || !vm.IsMortarLoaded)
        {
            return;
        }

        var constrained = new Point(
            Math.Clamp(point.X, MortarHitbox.Left + 44, MortarHitbox.Right - 40),
            Math.Clamp(point.Y, MortarHitbox.Top + 28, MortarHitbox.Bottom - 62));
        var distance = Math.Sqrt((delta.X * delta.X) + (delta.Y * delta.Y));
        pestlePoint = constrained;
        var pestleTip = constrained + new Vector(0, 70);
        mortarContactPoint = new Point(pestleTip.X, Math.Min(pestleTip.Y, 503));

        if (pestleTip.Y <= PoundReleaseTipY)
        {
            poundArmed = true;
        }

        var previousProgress = vm.GrindingProgress;
        var contactDelta = new Point(528 + mortarHerbOffset.X, 478 + mortarHerbOffset.Y) - pestleTip;
        var contactDistance = Math.Sqrt((contactDelta.X * contactDelta.X) + (contactDelta.Y * contactDelta.Y));
        var contactInfluence = 1 - Math.Clamp(contactDistance / 132, 0, 1);
        var pounded = poundArmed
            && previousPestleTipY < PoundImpactTipY
            && pestleTip.Y >= PoundImpactTipY
            && pointerVelocity.Y >= PoundMinimumVelocity;

        if (distance is > 0.4 and < 74)
        {
            var radial = constrained - MortarHitbox.Center;
            var radialLength = Math.Max(1, Math.Sqrt((radial.X * radial.X) + (radial.Y * radial.Y)));
            var tangentialRatio = Math.Abs((delta.X * radial.Y) - (delta.Y * radial.X)) / (distance * radialLength);
            var lateralRatio = Math.Abs(delta.X) / distance;
            var rubbingQuality = Math.Clamp(Math.Max(tangentialRatio, lateralRatio), 0, 1);
            var weightedDistance = distance * (0.22 + (rubbingQuality * 0.78)) * Math.Max(0.18, contactInfluence);
            grindingDistance += weightedDistance;
            mortarGrindingEnergy = Math.Clamp(mortarGrindingEnergy + (weightedDistance / 32), 0, 1);
            var direction = Math.Sign((delta.X * (constrained.Y - MortarHitbox.Center.Y)) - (delta.Y * (constrained.X - MortarHitbox.Center.X)));
            mortarGrindingPhase += weightedDistance * 0.048 * (direction == 0 ? 1 : direction);

            if (contactInfluence > 0.001)
            {
                var safeDistance = Math.Max(1, contactDistance);
                var push = new Vector(contactDelta.X / safeDistance, contactDelta.Y / safeDistance);
                mortarHerbVelocity += new Vector(
                    (delta.X * 8.5) + (push.X * distance * 12),
                    (delta.Y * 2.4) + (push.Y * distance * 4.5)) * contactInfluence;
                mortarHerbAngularVelocity += ((delta.X * 4.2) - (delta.Y * 1.2)) * contactInfluence;
                mortarHerbCompression = Math.Max(mortarHerbCompression, contactInfluence * 0.22);
            }
        }

        if (pounded)
        {
            var strength = Math.Clamp((pointerVelocity.Y - PoundMinimumVelocity) / 780, 0, 1);
            grindingDistance += GrindDistanceRequired * (0.018 + (strength * 0.018));
            mortarImpact = 1;
            mortarGrindingEnergy = Math.Clamp(mortarGrindingEnergy + 0.52 + (strength * 0.28), 0, 1);
            mortarHerbVelocity += new Vector(pointerVelocity.X * 0.016, 24 + (strength * 34));
            mortarHerbAngularVelocity += pointerVelocity.X * 0.035;
            poundArmed = false;
            var ingredientId = MortarIngredientId(vm) ?? "belladonna";
            particles.EmitGrindingDebris(mortarContactPoint, 8 + (int)(strength * 7), IngredientColor(ingredientId), previousProgress);
        }

        var progress = Math.Clamp(grindingDistance / GrindDistanceRequired, 0, 1);
        var rotation = Math.Clamp((delta.X * 1.4) + ((constrained.X - MortarHitbox.Center.X) * 0.18), -34, 34);
        vm.UpdateGrindingGesture(progress, rotation);
        if (previousProgress < 0.999 && progress >= 0.999)
        {
            var ingredientId = MortarIngredientId(vm) ?? "belladonna";
            particles.EmitGrindingDebris(new Point(528, 488), 24, IngredientColor(ingredientId), 1);
        }

        previousPestleTipY = pestleTip.Y;
    }

    private void MoveBellows(Point point)
    {
        var deltaY = point.Y - lastBellowsY;
        var previousPull = bellowsPull;
        bellowsPull = Math.Clamp(bellowsPull + (deltaY / 118), 0, 1);
        bellowsMaxPull = Math.Max(bellowsMaxPull, bellowsPull);
        lastBellowsY = point.Y;

        if (!bellowsSoundPlayed
            && bellowsPull >= 0.06
            && DataContext is AlchemyWorkshopViewModel soundVm)
        {
            bellowsSoundPlayed = true;
            soundVm.PlayBellowsSound(Math.Clamp(0.45 + (bellowsPull * 0.55), 0.45, 1));
        }

        if (!bellowsBlown && previousPull > 0.32 && bellowsPull < 0.08 && DataContext is AlchemyWorkshopViewModel vm)
        {
            CompleteBellowsBlow(vm);
        }
    }

    private void CompleteBellowsBlow(AlchemyWorkshopViewModel vm)
    {
        if (bellowsBlown || bellowsMaxPull < 0.12)
        {
            return;
        }

        bellowsBlown = true;
        vm.BlowBellows(Math.Clamp(bellowsMaxPull, 0.15, 1));
        particles.EmitFire(new Point(927, 626), 16 + (int)(bellowsMaxPull * 30));
        particles.EmitSparkle(new Point(927, 612), 8 + (int)(bellowsMaxPull * 14));
        bellowsPull = 0;
    }

    private void ReleaseIngredient(AlchemyWorkshopViewModel vm, IngredientSlotViewModel ingredient, Point point)
    {
        if (MortarDropHitbox.Contains(point))
        {
            var wasLoaded = vm.IsMortarLoaded;
            var existingCount = vm.Snapshot.Mortar?.Count ?? 0;
            var accepted = vm.DropIngredient(ingredient, IngredientDropTarget.Mortar);
            if (accepted && vm.IsMortarLoaded)
            {
                mortarDropRemaining = Math.Max(0, draggedIngredientCount - 1);
                mortarDropSettledCount = existingCount;
                BeginMortarDrop(
                    ingredient.Id,
                    new Point(Math.Clamp(point.X, 472, 590), Math.Min(point.Y - 34, 388)),
                    new Vector(
                        Math.Clamp(pointerVelocity.X * 0.11, -150, 150),
                        Math.Clamp(pointerVelocity.Y * 0.08, -48, 105)),
                    Math.Clamp(pointerVelocity.X * 0.035, -22, 22),
                    Math.Clamp(pointerVelocity.X * 0.14, -210, 210));
                mortarImpact = 0;
                mortarHerbOffset = default;
                mortarHerbVelocity = default;
                mortarHerbRotation = 0;
                mortarHerbAngularVelocity = 0;
                mortarHerbCompression = 0;
                mortarReveal = wasLoaded ? Math.Max(mortarReveal, 0.5) : Math.Max(mortarReveal, 0.02);
                particles.EmitDust(new Point(mortarDropPosition.X, 415), 7, IngredientColor(ingredient.Id));
            }
        }
        else if (CauldronMouth.Contains(point))
        {
            if (vm.DropIngredient(ingredient, IngredientDropTarget.Cauldron))
            {
                pourImpactPoint = new Point(Math.Clamp(point.X, 704, 936), 386);
                liquidImpact = Math.Max(liquidImpact, 0.78);
                particles.EmitSplash(pourImpactPoint, 7, IngredientColor(ingredient.Id));
            }
        }
    }

    private void CommitPour(AlchemyWorkshopViewModel vm)
    {
        if (pourCommitted || draggedBase is null)
        {
            return;
        }

        pourCommitted = true;
        pourProgress = 1;
        pendingPourProgress = 1;
        vm.PourBase(draggedBase);
        pendingPourBase = null;
        pendingPourProgress = 0;
        liquidImpact = 1;
        particles.EmitPourImpact(pourImpactPoint, 18, LiquidColor(draggedBase.Value), 1);
    }

    private void CapturePointer(PointerPressedEventArgs e)
    {
        e.Pointer.Capture(this);
    }

    private void CancelPointerInteraction()
    {
        ResetPointerState();
        InvalidateVisual();
    }

    private void ResetPointerState()
    {
        SetPouringSound(false);
        pressedHudTarget = AlchemyHudTarget.None;
        dragMode = DragMode.None;
        draggedIngredient = null;
        draggedIngredientCount = 1;
        draggedBase = null;
        draggedPowderIngredientId = null;
        pourTilt = 0;
        pourProgress = 0;
        pourCommitted = false;
        bottleCapPhase = BottleCapPhase.Closed;
        bottleCapProgress = 0;
        pourFlowRate = 0;
        bellowsBlown = false;
        bellowsSoundPlayed = false;
        bellowsMaxPull = 0;
    }

    private void ResetPourTransaction()
    {
        SetPouringSound(false);
        pendingPourBase = null;
        pendingPourProgress = 0;
        pourVisualLiquid = null;
        pourProgress = 0;
        pourFlowRate = 0;
        pourTail = 0;
        pourImpactAccumulator = 0;
        activePourTrajectory = null;
        cauldronLiquidLevel = 0;
        liquidImpact = 0;
    }

    private void SetPouringSound(bool isPouring, double flowRate = 0)
    {
        if (DataContext is AlchemyWorkshopViewModel vm)
        {
            vm.SetPouringSound(isPouring, flowRate);
        }
    }

    private void ResetHourglassVisual()
    {
        hourglassVisualRunning = false;
        hourglassVisualProgress = 0;
        hourglassSandPhase = 0;
        hourglassRotation = 0;
    }

    private void ResetBottleCollection()
    {
        bottleCollectionActive = false;
        bottleCollectionSource = BottleCollectionSource.Cauldron;
        bottleCollectionProgress = 0;
        bottleCollectionPosition = default;
        bottleCollectionStart = default;
        distillerBottleInstalled = false;
        distillerBottleInstallProgress = 0;
        distillerBottleInstallStart = default;
        distillerBottlePosition = default;
        completionBottleOrigin = AlchemyHudLayout.BrewShowcaseBottleStart;
    }

    private bool HandleRecipeBookPress(AlchemyWorkshopViewModel vm, Point point)
    {
        if (AlchemyHudLayout.RecipeBookPrevious.Contains(point))
        {
            if (recipeTurnActive)
            {
                return true;
            }

            ActivateHudTarget(new AlchemyHudTarget(AlchemyHudTargetKind.RecipePrevious), vm, AlchemySoundCue.BookPage);
            ResetBottleCollection();
            vm.CycleRecipeFromGesture(-1);
            BeginRecipeBookTurn(-1);
            return true;
        }

        if (AlchemyHudLayout.RecipeBookNext.Contains(point))
        {
            if (recipeTurnActive)
            {
                return true;
            }

            ActivateHudTarget(new AlchemyHudTarget(AlchemyHudTargetKind.RecipeNext), vm, AlchemySoundCue.BookPage);
            ResetBottleCollection();
            vm.CycleRecipeFromGesture(1);
            BeginRecipeBookTurn(1);
            return true;
        }

        if (AlchemyHudLayout.RecipeBookClose.Contains(point))
        {
            ActivateHudTarget(new AlchemyHudTarget(AlchemyHudTargetKind.RecipeClose), vm, AlchemySoundCue.BookClose);
            vm.IsRecipeBookOpen = false;
            InvalidateVisual();
            return true;
        }

        return AlchemyHudLayout.RecipeBook.Contains(point);
    }

    private void BeginRecipeBookTurn(int direction)
    {
        recipeTurnDirection = Math.Sign(direction);
        recipeTurnProgress = 1;
        recipeTurnActive = true;
        InvalidateVisual();
    }

    private void UpdateRecipeBookTurn(double dt)
    {
        if (!recipeTurnActive)
        {
            return;
        }

        recipeTurnProgress = Math.Max(0, recipeTurnProgress - (dt / 0.38));
        if (recipeTurnProgress <= 0.001)
        {
            recipeTurnProgress = 0;
            recipeTurnActive = false;
        }
    }

    private bool TryHandleQuantityPickerPress(AlchemyWorkshopViewModel vm, Point point)
    {
        switch (quantityPickerMode)
        {
            case QuantityPickerMode.Ingredient when quantityPickerIngredient is not null:
            {
                for (var index = 0; index < 3; index++)
                {
                    if (!IngredientPickerOptionRect(quantityPickerIngredient.Index, index).Contains(point))
                    {
                        continue;
                    }

                    vm.SelectedCount = index + 1;
                    armedIngredient = quantityPickerIngredient;
                    ActivateHudTarget(new AlchemyHudTarget(AlchemyHudTargetKind.QuantityOption, index), vm);
                    CloseQuantityPicker();
                    InvalidateVisual();
                    return true;
                }

                break;
            }
            case QuantityPickerMode.Hourglass:
            {
                for (var index = 0; index < 3; index++)
                {
                    if (!HourglassPickerOptionRect(index).Contains(point))
                    {
                        continue;
                    }

                    vm.SelectedTurns = index + 1;
                    hourglassFlipArmed = true;
                    ActivateHudTarget(new AlchemyHudTarget(AlchemyHudTargetKind.QuantityOption, index), vm);
                    CloseQuantityPicker();
                    InvalidateVisual();
                    return true;
                }

                break;
            }
        }

        return false;
    }

    private void CloseQuantityPicker()
    {
        quantityPickerMode = QuantityPickerMode.None;
        quantityPickerIngredient = null;
    }

    private static bool TryHitIngredient(AlchemyWorkshopViewModel vm, Point point, out IngredientSlotViewModel ingredient)
    {
        foreach (var slot in vm.Ingredients)
        {
            if (IngredientSlotRect(slot.Index).Contains(point))
            {
                ingredient = slot;
                return true;
            }
        }

        ingredient = null!;
        return false;
    }

    private static bool TryHitBase(AlchemyWorkshopViewModel vm, Point point, out BaseSlotViewModel baseSlot)
    {
        for (var index = 0; index < vm.BaseLiquids.Count; index++)
        {
            if (BaseSlotRect(index).Contains(point))
            {
                baseSlot = vm.BaseLiquids[index];
                return true;
            }
        }

        baseSlot = null!;
        return false;
    }

    private void ActivateHudTarget(
        AlchemyHudTarget target,
        AlchemyWorkshopViewModel vm,
        AlchemySoundCue cue = AlchemySoundCue.UiClick)
    {
        activatedHudTarget = target;
        activationFeedbackRemaining = AlchemyInteractionPresentation.FeedbackSeconds;
        vm.PlayUiInteraction(cue, cue == AlchemySoundCue.UiClick ? .34 : .44);
        RequestRenderFrame();
    }

    private void UpdateHoveredTarget(AlchemyWorkshopViewModel? vm, Point point)
    {
        hoveredHudTarget = pointerInside && vm is not null
            ? ResolveHudTarget(vm, point)
            : AlchemyHudTarget.None;
        Cursor = hoveredHudTarget.IsNone || vm is null || IsTargetDisabled(vm, hoveredHudTarget)
            ? ArrowCursor
            : hoveredHudTarget.IsTool
                || hoveredHudTarget.Kind is AlchemyHudTargetKind.Ingredient or AlchemyHudTargetKind.BaseLiquid
                    ? DragCursor
                    : HandCursor;
        InvalidateVisual();
    }

    private AlchemyHudTarget ResolveHudTarget(AlchemyWorkshopViewModel vm, Point point)
    {
        if (RecipeTabHitbox.Contains(point))
        {
            return new AlchemyHudTarget(AlchemyHudTargetKind.RecipeBook);
        }

        if (vm.IsRecipeBookOpen)
        {
            if (AlchemyHudLayout.RecipeBookPrevious.Contains(point))
            {
                return new AlchemyHudTarget(AlchemyHudTargetKind.RecipePrevious);
            }

            if (AlchemyHudLayout.RecipeBookNext.Contains(point))
            {
                return new AlchemyHudTarget(AlchemyHudTargetKind.RecipeNext);
            }

            return AlchemyHudLayout.RecipeBookClose.Contains(point)
                ? new AlchemyHudTarget(AlchemyHudTargetKind.RecipeClose)
                : AlchemyHudTarget.None;
        }

        if (ResetRagHitbox.Contains(point))
        {
            return new AlchemyHudTarget(AlchemyHudTargetKind.Reset);
        }

        if (quantityPickerMode == QuantityPickerMode.Ingredient && quantityPickerIngredient is not null)
        {
            for (var index = 0; index < 3; index++)
            {
                if (IngredientPickerOptionRect(quantityPickerIngredient.Index, index).Contains(point))
                {
                    return new AlchemyHudTarget(AlchemyHudTargetKind.QuantityOption, index);
                }
            }
        }
        else if (quantityPickerMode == QuantityPickerMode.Hourglass)
        {
            for (var index = 0; index < 3; index++)
            {
                if (HourglassPickerOptionRect(index).Contains(point))
                {
                    return new AlchemyHudTarget(AlchemyHudTargetKind.QuantityOption, index);
                }
            }
        }

        foreach (var ingredient in vm.Ingredients)
        {
            if (IngredientSlotRect(ingredient.Index).Contains(point))
            {
                return new AlchemyHudTarget(AlchemyHudTargetKind.Ingredient, ingredient.Index);
            }
        }

        for (var index = 0; index < vm.BaseLiquids.Count; index++)
        {
            if (BaseSlotRect(index).Contains(point))
            {
                return new AlchemyHudTarget(AlchemyHudTargetKind.BaseLiquid, index);
            }
        }

        if (MortarPowderHitbox.Contains(point))
        {
            return new AlchemyHudTarget(AlchemyHudTargetKind.MortarPowder);
        }

        if (PestleHitbox.Contains(point))
        {
            return new AlchemyHudTarget(AlchemyHudTargetKind.Pestle);
        }

        if (BellowsHandleHitbox.Contains(point))
        {
            return new AlchemyHudTarget(AlchemyHudTargetKind.Bellows);
        }

        if (HourglassHitbox.Contains(point))
        {
            return new AlchemyHudTarget(AlchemyHudTargetKind.Hourglass);
        }

        if (CauldronHookHitbox.Contains(point))
        {
            return new AlchemyHudTarget(AlchemyHudTargetKind.CauldronHook);
        }

        if (DistillerLeverHitbox.Contains(point))
        {
            return new AlchemyHudTarget(AlchemyHudTargetKind.DistillerLever);
        }

        return ProductBottleHitbox.Contains(point)
            ? new AlchemyHudTarget(AlchemyHudTargetKind.ProductBottle)
            : AlchemyHudTarget.None;
    }

    private bool IsTargetDisabled(AlchemyWorkshopViewModel vm, AlchemyHudTarget target) => target.Kind switch
    {
        AlchemyHudTargetKind.RecipePrevious or AlchemyHudTargetKind.RecipeNext => recipeTurnActive,
        AlchemyHudTargetKind.Ingredient => target.Index < 0
            || target.Index >= vm.Ingredients.Count
            || vm.Ingredients[target.Index].Quantity <= 0,
        AlchemyHudTargetKind.BaseLiquid => target.Index < 0
            || target.Index >= vm.BaseLiquids.Count
            || pendingPourBase is { } pending && pending != vm.BaseLiquids[target.Index].Value,
        AlchemyHudTargetKind.MortarPowder => !vm.IsMortarLoaded
            || vm.GrindingProgress < .999
            || grindingVisualProgress < .985,
        AlchemyHudTargetKind.Pestle => !vm.IsMortarLoaded || vm.GrindingProgress >= .999,
        AlchemyHudTargetKind.Hourglass => vm.IsHourglassRunning,
        AlchemyHudTargetKind.DistillerLever => CurrentStepKind(vm) == RecipeStepKind.Distill
            && !BottleCollectionVisual.CanOperatePump(vm.Snapshot.Recipe, CurrentStepKind(vm), distillerBottleInstalled),
        AlchemyHudTargetKind.ProductBottle => bottleCollectionActive || distillerBottleInstalled,
        _ => false
    };

    private AlchemyInteractionState InteractionStateFor(
        AlchemyWorkshopViewModel vm,
        AlchemyHudTarget target,
        bool selected = false)
    {
        if (IsTargetDisabled(vm, target))
        {
            return AlchemyInteractionState.Disabled;
        }

        if (pressedHudTarget == target
            || (activatedHudTarget == target && activationFeedbackRemaining > 0))
        {
            return AlchemyInteractionState.Pressed;
        }

        if (selected)
        {
            return AlchemyInteractionState.Selected;
        }

        return hoveredHudTarget == target
            ? AlchemyInteractionState.Hover
            : AlchemyInteractionState.Default;
    }

    private static Rect? ToolBoundsFor(AlchemyHudTarget target) => target.Kind switch
    {
        AlchemyHudTargetKind.MortarPowder => MortarPowderHitbox,
        AlchemyHudTargetKind.Pestle => PestleHitbox,
        AlchemyHudTargetKind.Bellows => BellowsHandleHitbox,
        AlchemyHudTargetKind.Hourglass => HourglassHitbox,
        AlchemyHudTargetKind.CauldronHook => CauldronHookHitbox,
        AlchemyHudTargetKind.DistillerLever => DistillerLeverHitbox,
        AlchemyHudTargetKind.ProductBottle => ProductBottleHitbox,
        _ => null
    };

    private void DrawWorkshop(DrawingContext context)
    {
        var vm = DataContext as AlchemyWorkshopViewModel;
        DrawRoom(context);
        DrawHeader(context, vm);
        DrawInventory(context, vm);
        DrawHearthBackdrop(context);
        DrawMortar(context, vm);
        DrawProductBottleSupply(context, vm);
        DrawCauldron(context, vm);
        DrawBellows(context, vm);
        DrawHourglass(context, vm);
        DrawDistiller(context, vm);
        DrawBottomRailModern(context, vm);
        DrawGuideFocus(context, vm);
        DrawToolInteractionAffordance(context, vm);
        DrawResetRagModern(context);
    }

    private void DrawToolInteractionAffordance(DrawingContext context, AlchemyWorkshopViewModel? vm)
    {
        if (vm is null || vm.IsRecipeBookOpen || !pointerInside)
        {
            return;
        }

        var target = pressedHudTarget.IsTool ? pressedHudTarget : hoveredHudTarget;
        if (!target.IsTool || IsTargetDisabled(vm, target) || ToolBoundsFor(target) is not { } bounds)
        {
            return;
        }

        var pressed = pressedHudTarget == target;
        var center = new Point(
            Math.Clamp(previousPointer.X, bounds.Left + 9, bounds.Right - 9),
            Math.Clamp(previousPointer.Y, bounds.Top + 9, bounds.Bottom - 9));
        var radius = pressed ? 11d : 9d;
        var color = pressed ? AlchemyHudTheme.AccentBright : AlchemyHudTheme.Accent;
        context.DrawEllipse(
            Brush(BrushColor(color), pressed ? .2 : .12),
            CachedPen(Brush(BrushColor(color), pressed ? .9 : .72), pressed ? 1.8 : 1.3),
            new Rect(center.X - radius, center.Y - radius, radius * 2, radius * 2));
        context.DrawEllipse(Brush(BrushColor(color), .92), null,
            new Rect(center.X - 2.2, center.Y - 2.2, 4.4, 4.4));

        var tick = pressed ? 6d : 4d;
        var pen = CachedPen(Brush(BrushColor(color), .78), 1.2);
        context.DrawLine(pen, new Point(center.X - radius - tick, center.Y), new Point(center.X - radius + 1, center.Y));
        context.DrawLine(pen, new Point(center.X + radius - 1, center.Y), new Point(center.X + radius + tick, center.Y));
        context.DrawLine(pen, new Point(center.X, center.Y - radius - tick), new Point(center.X, center.Y - radius + 1));
        context.DrawLine(pen, new Point(center.X, center.Y + radius - 1), new Point(center.X, center.Y + radius + tick));
    }

    private void DrawGuideFocus(DrawingContext context, AlchemyWorkshopViewModel? vm)
    {
        if (vm is null
            || vm.IsBrewFailed
            || vm.IsBrewComplete
            || vm.IsHourglassRunning
            || bottleCollectionActive)
        {
            return;
        }

        var snapshot = vm.Snapshot;
        if (snapshot.CurrentStepIndex >= snapshot.Recipe.Steps.Count)
        {
            return;
        }

        var step = snapshot.Recipe.Steps[snapshot.CurrentStepIndex];
        switch (step.Kind)
        {
            case RecipeStepKind.PourBase when step.Base is not null:
            {
                var slotIndex = vm.BaseLiquids.ToList().FindIndex(slot => slot.Value == step.Base.Value);
                if (slotIndex >= 0)
                {
                    DrawTargetHalo(context, BaseSlotRect(slotIndex), true);
                }

                DrawTargetHalo(context, CauldronMouth, true);
                break;
            }
            case RecipeStepKind.Grind:
                DrawIngredientGuide(context, vm, step.IngredientId);
                if (vm.IsMortarLoaded && vm.GrindingProgress >= 0.999)
                {
                    DrawTargetHalo(context, MortarPowderHitbox, true);
                    DrawTargetHalo(context, CauldronMouth, false);
                }
                else
                {
                    DrawTargetHalo(context, MortarDropHitbox, true);
                }
                break;
            case RecipeStepKind.AddIngredient when step.Form == IngredientForm.Raw:
                DrawIngredientGuide(context, vm, step.IngredientId);
                DrawTargetHalo(context, CauldronMouth, true);
                break;
            case RecipeStepKind.AddIngredient:
                DrawTargetHalo(context, CauldronMouth, true);
                break;
            case RecipeStepKind.Cook:
                if (!vm.IsCauldronLowered)
                {
                    DrawTargetHalo(context, CauldronHookHitbox, true);
                }
                else if (snapshot.HeatBand != step.Heat)
                {
                    DrawTargetHalo(context, BellowsHandleHitbox, true);
                }
                else
                {
                    DrawTargetHalo(context, HourglassHitbox, true);
                }

                break;
            case RecipeStepKind.Distill:
                if (distillerBottleInstalled)
                {
                    DrawTargetHalo(context, DistillerLeverHitbox, true);
                }
                else
                {
                    DrawTargetHalo(context, ProductBottleHitbox, true);
                    DrawTargetHalo(context, DistillerReceiverHitbox, true);
                }

                break;
            case RecipeStepKind.Bottle:
                DrawTargetHalo(context, ProductBottleHitbox, true);
                DrawTargetHalo(
                    context,
                    BottleCollectionVisual.Resolve(snapshot.Recipe, CurrentPotOffset()).Hitbox,
                    true);
                break;
        }
    }

    private static void DrawIngredientGuide(DrawingContext context, AlchemyWorkshopViewModel vm, string? ingredientId)
    {
        var slot = vm.Ingredients.FirstOrDefault(item => string.Equals(item.Id, ingredientId, StringComparison.OrdinalIgnoreCase));
        if (slot is not null)
        {
            DrawTargetHalo(context, IngredientSlotRect(slot.Index), true);
        }
    }

    private void DrawRoom(DrawingContext context)
    {
        if (sprites.Draw(context, "painted_hearth_background", new Rect(0, 0, SceneWidth, SceneHeight), 0, 1))
        {
            context.DrawRectangle(Brush(0x16E7C98E), null, new Rect(0, 0, SceneWidth, SceneHeight));
            return;
        }

        context.DrawRectangle(Brush(0xFFDBC18D), null, new Rect(0, 0, SceneWidth, SceneHeight));
        for (var y = 72; y < 742; y += 96)
        {
            context.DrawRectangle(y % 192 == 72 ? Brush(0xFFCEAE72) : Brush(0xFFD6BA82), new Pen(Brush(0xFF8A653E), 2), new Rect(0, y, SceneWidth, 96));
            context.DrawLine(new Pen(Brush(0x4481643D), 2), new Point(0, y + 23), new Point(SceneWidth, y + 31));
            context.DrawLine(new Pen(Brush(0x3381643D), 1), new Point(0, y + 72), new Point(SceneWidth, y + 65));
        }

        context.DrawRectangle(Brush(0xC9C49D61), new Pen(Ink, 3), new Rect(0, 72, 273, 670));
        context.DrawRectangle(Brush(0xAFC49D61), new Pen(Ink, 3), new Rect(1122, 72, 318, 670));
        context.DrawRectangle(Brush(0xFF9B7044), new Pen(Ink, 4), new Rect(274, 674, 848, 68));

        for (var index = 0; index < 18; index++)
        {
            var x = 292 + (index * 48);
            context.DrawLine(new Pen(Brush(0x5580643F), 1), new Point(x, 82), new Point(x - 40, 670));
        }
    }

    private void DrawHearthBackdrop(DrawingContext context)
    {
        if (sprites.GetAvailable("painted_hearth_background"))
        {
            return;
        }

        var arch = StreamGeometry.Parse("M578,676 L592,535 C600,465 650,426 700,416 L942,416 C1001,430 1026,480 1034,535 L1050,676 Z");
        context.DrawGeometry(Brush(0xFF8D6745), new Pen(Ink, 5), arch);
        context.DrawGeometry(null, new Pen(Brush(0xFF5F4631), 2), StreamGeometry.Parse("M603,665 L616,544 C621,492 656,459 705,450 M1022,665 L1010,544 C1005,492 972,459 935,450"));
        for (var row = 0; row < 4; row++)
        {
            var y = 472 + (row * 47);
            context.DrawLine(new Pen(Brush(0xFF664A33), 2), new Point(602, y), new Point(1024, y));
        }
        context.DrawEllipse(Brush(0xFF4A3527), new Pen(Ink, 4), new Rect(704, 581, 253, 100));
    }

    private void DrawHeader(DrawingContext context, AlchemyWorkshopViewModel? vm)
    {
        var hud = AlchemyHudPresentation.From(vm);
        var recipeActive = vm?.IsRecipeBookOpen == true;
        DrawHudBandFrame(context, AlchemyHudLayout.Header);
        var recipeTarget = new AlchemyHudTarget(AlchemyHudTargetKind.RecipeBook);
        var recipeState = vm is null
            ? AlchemyInteractionState.Default
            : InteractionStateFor(vm, recipeTarget, recipeActive);
        var recipeVisual = DrawInteractivePlaque(context, RecipeTabHitbox, recipeState);
        using (context.PushTransform(Matrix.CreateTranslation(0, recipeVisual.ContentOffset)))
        {
            sprites.Draw(context, "hud_recipe_book_tab", new Rect(80.5, 19, 33, 34), 0,
                recipeState is AlchemyInteractionState.Hover or AlchemyInteractionState.Pressed or AlchemyInteractionState.Selected ? 1 : 0.9);
            DrawText(context, vm?.RecipeBookText ?? "配方书", new Point(120, 21), 14,
                Brush(recipeVisual.Text), DisplayTypeface, 66);
            DrawText(context, vm?.RecipeBookSubtitle ?? "配方与步骤", new Point(120, 42), 8,
                Brush(recipeState == AlchemyInteractionState.Disabled ? AlchemyHudTheme.TextMuted : AlchemyHudTheme.TextSecondary), UiTypeface, 66);
            var keycap = new Rect(190, 25, 24, 20);
            context.DrawRectangle(Brush(AlchemyHudTheme.SurfaceInset), new Pen(Brush(recipeVisual.Border), 1), new RoundedRect(keycap, 2));
            DrawText(context, "B", new Point(keycap.X, keycap.Y + 2), 10, Brush(AlchemyHudTheme.AccentBright), UiTypeface,
                keycap.Width, TextAlignment.Center);
        }
        DrawHudDivider(context, 234, 11, 50);

        DrawText(context, Ui(vm, AlchemyTextKey.CurrentRecipe), new Point(AlchemyHudLayout.HeaderRecipe.X, 11), 10, Brush(AlchemyHudTheme.TextMuted), UiTypeface);
        DrawText(context, vm is null ? Ui(null, AlchemyTextKey.AlchemyWorkshop) : vm.RecipeName(vm.SelectedRecipe), new Point(AlchemyHudLayout.HeaderRecipe.X, 29), 21,
            Brush(AlchemyHudTheme.TextPrimary), DisplayTypeface, AlchemyHudLayout.HeaderRecipe.Width);

        DrawText(context, $"{Ui(vm, AlchemyTextKey.Step)} {hud.StepNumber}", new Point(AlchemyHudLayout.HeaderStep.X, 10), 10,
            Brush(AlchemyHudTheme.AccentBright), UiTypeface);
        DrawText(context, vm?.CurrentStepText ?? Ui(null, AlchemyTextKey.Loading), new Point(AlchemyHudLayout.HeaderStep.X + 86, 9), 15,
            Brush(AlchemyHudTheme.TextPrimary), UiTypeface, AlchemyHudLayout.HeaderStep.Width - 86);
        var stepCount = vm?.Snapshot.Recipe.Steps.Count ?? 0;
        var currentStep = vm?.Snapshot.CurrentStepIndex ?? 0;
        context.DrawRectangle(Brush(AlchemyHudTheme.SurfaceInset), new Pen(Brush(AlchemyHudTheme.BorderDark), 1),
            new RoundedRect(new Rect(AlchemyHudLayout.HeaderStep.X, 53, AlchemyHudLayout.HeaderStep.Width, 8), 2));
        for (var index = 0; index < stepCount; index++)
        {
            var segment = AlchemyHudLayout.StepSegment(index, stepCount);
            var fill = index < currentStep
                ? Brush(AlchemyHudTheme.Success)
                : index == currentStep
                    ? Brush(AlchemyHudTheme.AccentBright)
                    : Brush(AlchemyHudTheme.SurfaceSoft);
            context.DrawRectangle(fill, new Pen(Brush(AlchemyHudTheme.BorderMuted), 0.75), new RoundedRect(segment, 2));
        }

        DrawHudDivider(context, 1054, 12, 60);
        DrawHudIconBadge(context, "hud_quality", new Rect(1064, 22, 28, 28), false);
        DrawText(context, Ui(vm, AlchemyTextKey.Mistakes), new Point(1098, 14), 10,
            Brush(AlchemyHudTheme.TextMuted), UiTypeface);
        DrawStatusDot(context, new Point(1090, 49),
            vm?.Snapshot.Mistakes > 0 ? AlchemyHudTone.Warning : AlchemyHudTone.Success);
        DrawText(context, hud.DeviationValue, new Point(1098, 31), 18,
            Brush(vm?.Snapshot.Mistakes > 0 ? AlchemyHudTheme.Simmering : AlchemyHudTheme.TextPrimary), UiTypeface);

        DrawHudDivider(context, 1208, 12, 60);
        DrawHudIconBadge(context, "hud_potion", new Rect(1218, 21, 30, 30), false);
        DrawText(context, Ui(vm, AlchemyTextKey.BrewStatus), new Point(1254, 14), 10,
            Brush(AlchemyHudTheme.TextMuted), UiTypeface);
        DrawStatusDot(context, new Point(1247, 49), hud.OutcomeTone);
        DrawText(context, hud.OutcomeLabel, new Point(1257, 31), 13,
            Brush(AlchemyHudTheme.ColorFor(hud.OutcomeTone)), UiTypeface, 98);
    }

    private void DrawInventory(DrawingContext context, AlchemyWorkshopViewModel? vm)
    {
        var herbHeader = new Rect(14, 82, 246, 45);
        DrawHudPlaque(context, herbHeader, false, false);
        DrawHudIconBadge(context, "hud_herbs", new Rect(20, 87, 34, 34), false);
        DrawText(context, Ui(vm, AlchemyTextKey.Herbs), new Point(62, 89), 19, Brush(AlchemyHudTheme.TextPrimary), DisplayTypeface);
        DrawText(context, Ui(vm, AlchemyTextKey.Inventory), new Point(194, 98), 9, Brush(AlchemyHudTheme.TextMuted), UiTypeface, 54, TextAlignment.Right);

        if (vm is not null)
        {
            foreach (var ingredient in vm.Ingredients)
            {
                var rect = IngredientSlotRect(ingredient.Index);
                var armed = ReferenceEquals(armedIngredient, ingredient);
                var available = ingredient.Quantity > 0;
                var target = new AlchemyHudTarget(AlchemyHudTargetKind.Ingredient, ingredient.Index);
                var state = InteractionStateFor(vm, target, armed);
                var interaction = AlchemyInteractionPresentation.From(state);
                var currentStep = vm.Snapshot.CurrentStepIndex < vm.Snapshot.Recipe.Steps.Count
                    ? vm.Snapshot.Recipe.Steps[vm.Snapshot.CurrentStepIndex]
                    : null;
                var guided = currentStep?.IngredientId == ingredient.Id;
                var borderColor = state == AlchemyInteractionState.Default && guided
                    ? AlchemyHudTheme.Success
                    : interaction.Border;
                context.DrawRectangle(Brush(AlchemyHudTheme.HudShadow), null,
                    new RoundedRect(new Rect(rect.X + 2, rect.Y + interaction.ShadowOffset, rect.Width, rect.Height), 3));
                context.DrawRectangle(Brush(interaction.Surface),
                    new Pen(Brush(borderColor), guided && state == AlchemyInteractionState.Default ? 2 : interaction.BorderThickness), new RoundedRect(rect, 3));
                var parchment = new Rect(rect.X + 4, rect.Y + 4, rect.Width - 8, rect.Height - 8);
                context.DrawRectangle(state is AlchemyInteractionState.Hover or AlchemyInteractionState.Pressed or AlchemyInteractionState.Selected
                        ? Brush(0xFFE8D4AA)
                        : Parchment,
                    new Pen(Brush(0xFF8E673C), 0.8), parchment);
                context.DrawLine(new Pen(Brush(0x66FFF0C7), 1),
                    new Point(parchment.X + 2, parchment.Y + 2), new Point(parchment.Right - 2, parchment.Y + 2));
                var nameBar = new Rect(rect.X + 4, rect.Bottom - 31, rect.Width - 8, 27);
                context.DrawRectangle(Brush(AlchemyHudTheme.SurfaceInset), new Pen(Brush(AlchemyHudTheme.BrassDark), 0.8), nameBar);
                DrawHudStud(context, new Point(rect.X + 7, rect.Y + 7), 1.8);
                DrawHudStud(context, new Point(rect.Right - 7, rect.Bottom - 7), 1.8);
                DrawHerb(context, ingredient.Id, rect.Center + new Vector(0, -16), 1, available ? 1 : 0.34);
                DrawText(context, ingredient.Name, new Point(rect.X + 7, rect.Bottom - 23), 13,
                    available ? Brush(AlchemyHudTheme.TextPrimary) : Brush(AlchemyHudTheme.TextMuted), UiTypeface,
                    rect.Width - 14, TextAlignment.Center);

                var countBadge = new Rect(rect.Right - 38, rect.Y + 7, 30, 21);
                context.DrawRectangle(
                    available ? Brush(AlchemyHudTheme.SurfaceRaised) : Brush(AlchemyHudTheme.Disabled),
                    new Pen(Brush(available ? AlchemyHudTheme.BrassMid : AlchemyHudTheme.BorderMuted), 1),
                    new RoundedRect(countBadge, 3));
                DrawText(context, $"×{ingredient.Quantity}", new Point(countBadge.X, countBadge.Y + 3), 10,
                    Brush(available ? AlchemyHudTheme.TextPrimary : AlchemyHudTheme.TextMuted), UiTypeface,
                    countBadge.Width, TextAlignment.Center);
                if (armed)
                {
                    var selection = new Rect(rect.X + 7, rect.Y + 7, 42, 21);
                    context.DrawRectangle(Brush(AlchemyHudTheme.Accent), null, new RoundedRect(selection, 3));
                    DrawText(context, $"{Ui(vm, AlchemyTextKey.Take)} {vm.SelectedCount}", new Point(selection.X, selection.Y + 3), 10, Ink, UiTypeface,
                        selection.Width, TextAlignment.Center);
                }

                if (!available)
                {
                    context.DrawRectangle(Brush(AlchemyHudTheme.Disabled), null, new RoundedRect(rect, 3));
                }
            }

            var liquidHeader = new Rect(14, 548, 246, 39);
            DrawHudPlaque(context, liquidHeader, false, false);
            DrawHudIconBadge(context, "hud_liquids", new Rect(20, 552, 30, 30), false);
            DrawText(context, Ui(vm, AlchemyTextKey.Base), new Point(59, 556), 16, Brush(AlchemyHudTheme.TextPrimary), DisplayTypeface);
            DrawText(context, Ui(vm, AlchemyTextKey.ChooseSolvent), new Point(180, 559), 9, Brush(AlchemyHudTheme.TextMuted), UiTypeface, 68, TextAlignment.Right);
            for (var index = 0; index < vm.BaseLiquids.Count; index++)
            {
                var baseSlot = vm.BaseLiquids[index];
                var baseRect = BaseSlotRect(index);
                var target = new AlchemyHudTarget(AlchemyHudTargetKind.BaseLiquid, index);
                var state = InteractionStateFor(vm, target);
                var interaction = AlchemyInteractionPresentation.From(state);
                context.DrawRectangle(Brush(AlchemyHudTheme.HudShadow), null,
                    new RoundedRect(new Rect(baseRect.X + 1, baseRect.Y + interaction.ShadowOffset, baseRect.Width, baseRect.Height), 3));
                context.DrawRectangle(Brush(interaction.Surface), new Pen(Brush(interaction.Border), interaction.BorderThickness),
                    new RoundedRect(baseRect, 3));
                context.DrawRectangle(null, new Pen(Brush(AlchemyHudTheme.SurfaceHighlight), 0.7),
                    new RoundedRect(new Rect(baseRect.X + 3, baseRect.Y + 3, baseRect.Width - 6, baseRect.Height - 6), 2));
                var recipeBase = vm.Snapshot.CurrentStepIndex < vm.Snapshot.Recipe.Steps.Count
                    ? vm.Snapshot.Recipe.Steps[vm.Snapshot.CurrentStepIndex].Base
                    : null;
                if (recipeBase == baseSlot.Value && state == AlchemyInteractionState.Default)
                {
                    context.DrawRectangle(null, new Pen(Brush(AlchemyHudTheme.Success), 2), new RoundedRect(baseRect, 4));
                }

                DrawBaseBottle(context, baseSlot, BaseSlotCenter(index), 0, 0.82);
                context.DrawRectangle(Brush(0xD916100C), null, new Rect(baseRect.X + 2, baseRect.Bottom - 18, baseRect.Width - 4, 16));
                DrawText(context, vm.BaseName(baseSlot.Value), new Point(baseRect.X, baseRect.Bottom - 16), 9,
                    Brush(AlchemyHudTheme.TextSecondary), UiTypeface, baseRect.Width, TextAlignment.Center);
            }

        }
    }

    private void DrawMortar(DrawingContext context, AlchemyWorkshopViewModel? vm)
    {
        var mortarRect = new Rect(386, 368, 300, 252);
        var mortarCutawayRect = new Rect(374, 360, 324, 260);
        var loaded = vm?.IsMortarLoaded == true;
        var reveal = loaded ? Math.Clamp(mortarReveal, 0, 1) : 0;
        var cutawayOpacity = SmoothStep(0.02, 0.96, reveal);
        var shellOpacity = 1 - SmoothStep(0.08, 0.88, reveal);
        if (shellOpacity > 0.001)
        {
            sprites.Draw(context, "image5_mortar_shell", mortarRect, 0, shellOpacity);
        }

        if (cutawayOpacity > 0.001)
        {
            sprites.Draw(context, "image6_mortar_side_cutaway", mortarCutawayRect, 0, cutawayOpacity);
        }
        if (loaded && vm is not null)
        {
            var ingredientId = MortarIngredientId(vm) ?? "belladonna";
            using (context.PushOpacity(Math.Clamp(reveal * 1.18, 0, 1)))
            {
                if (mortarDropActive)
                {
                    DrawMortarRestingHerbs(context, ingredientId, mortarDropSettledCount);
                    DrawMortarDroppingHerb(context, mortarDropIngredientId ?? ingredientId);
                }
                else
                {
                    DrawMortarGrindingContents(context, ingredientId, vm.Snapshot.Mortar?.Count ?? 1, grindingVisualProgress);
                }
            }
        }

        particles.RenderMortar(context);

        var pestleAngle = vm?.PestleRotation ?? -18;
        var pestleCenter = dragMode == DragMode.Pestle ? pestlePoint : new Point(565, 395);
        DrawPestle(context, pestleCenter, pestleAngle);

        var progress = vm?.GrindingProgress ?? 0;
        var grindingState = !loaded
            ? Ui(vm, AlchemyTextKey.AddHerbs)
            : progress >= 0.999
                ? Ui(vm, AlchemyTextKey.GroundHerbsReady)
                : $"{Ui(vm, AlchemyTextKey.Grinding)} {progress:P0}";
        DrawText(context, grindingState, new Point(426, 628), 11, Ink, UiTypeface, 204, TextAlignment.Center);
    }

    private void DrawMortarDroppingHerb(DrawingContext context, string ingredientId)
    {
        var speedStretch = Math.Clamp(Math.Abs(mortarDropVelocity.Y) / 720, 0, 0.18);
        var impactSquash = mortarImpact * 0.16;
        using (context.PushClip(new Rect(425, 352, 206, 168)))
        using (context.PushTransform(Matrix.CreateTranslation(mortarDropPosition.X, mortarDropPosition.Y)))
        using (context.PushTransform(Matrix.CreateRotation(mortarDropRotation * Math.PI / 180)))
        using (context.PushTransform(Matrix.CreateScale(1 + impactSquash - (speedStretch * 0.25), 1 + speedStretch - impactSquash)))
        using (context.PushTransform(Matrix.CreateTranslation(-mortarDropPosition.X, -mortarDropPosition.Y)))
        {
            DrawHerb(context, ingredientId, mortarDropPosition, 0.79, 1);
        }
    }

    private void DrawCauldron(DrawingContext context, AlchemyWorkshopViewModel? vm)
    {
        var loweredOffset = vm?.IsCauldronLowered == true ? 34 : 0;
        var gestureOffset = cauldronGestureOffset;
        var potOffset = loweredOffset + gestureOffset;

        context.DrawLine(new Pen(Ink, 7), new Point(682, 72), new Point(750, 318 + potOffset));
        context.DrawLine(new Pen(Ink, 7), new Point(948, 72), new Point(882, 318 + potOffset));
        context.DrawEllipse(ParchmentDark, new Pen(Ink, 4), new Rect(777, 158 + gestureOffset, 78, 78));
        context.DrawEllipse(Brush(0xFF7D5B38), new Pen(Ink, 2), new Rect(798, 179 + gestureOffset, 36, 36));

        DrawFire(context, vm, potOffset);
        var drainingForBottle = bottleCollectionActive && bottleCollectionSource == BottleCollectionSource.Cauldron
            ? bottleCollectionProgress * 0.11
            : 0;
        var liquidLevel = Math.Max(0, cauldronLiquidLevel - drainingForBottle);
        var hasCauldronSolids = (vm?.Snapshot.Cauldron.Count ?? 0) > 0;
        var cauldronRect = new Rect(604, 302 + potOffset, 432, 374);
        sprites.Draw(context, "image5_cauldron_shell", cauldronRect, 0, 1);
        DrawFireBedForeground(context, vm, potOffset);
        if (liquidLevel > 0.001)
        {
            var liquid = vm?.LiquidOpacity > 0
                ? vm.LiquidBrush
                : draggedBase is not null
                    ? new SolidColorBrush(LiquidColor(draggedBase.Value))
                    : pendingPourBase is { } pendingBase
                        ? new SolidColorBrush(LiquidColor(pendingBase))
                    : Brush(0xFF759DA2);
            DrawCauldronLiquid(context, liquid, liquidLevel, potOffset);
        }

        DrawCauldronContents(context, vm, potOffset);
        DrawPourStream(context, potOffset);
        using (context.PushGeometryClip(CreateCauldronMouthClip(potOffset)))
        {
            particles.RenderCauldron(context);
        }

        if (bottleCollectionActive && bottleCollectionSource == BottleCollectionSource.Cauldron && vm is not null)
        {
            DrawDirectBottleCollection(context, vm, potOffset);
        }

        if (liquidLevel > 0.001 || hasCauldronSolids)
        {
            sprites.Draw(context, "image5_cauldron_front_lip", cauldronRect, 0, 1);
        }
        DrawText(context, vm?.CauldronContentsText ?? Ui(null, AlchemyTextKey.EmptyCauldron), new Point(661, 646), 11, Ink, UiTypeface, 310, TextAlignment.Center);
    }

    private void DrawCauldronLiquid(DrawingContext context, IBrush liquid, double level, double potOffset)
    {
        // Keep all liquid geometry registered to the source cauldron's optical centre.
        // The shell is 432 px wide at x=604, so its centre is x=820; using the
        // old interaction centre (816) left the animated surface a few pixels left
        // of the drawn rim whenever the foreground lip was composited over it.
        const double cauldronVisualCenterX = 820;
        level = Math.Clamp(level, 0, 0.78);
        var surfaceTop = 401 - (level * 55) + (Math.Sin(liquidRippleTime * 1.13) * (1.8 + (liquidImpact * 4.0)));
        var surfaceWidth = 104 + (level * 196);
        var surfaceHeight = 16 + (level * 46);
        var surfaceLeft = cauldronVisualCenterX - (surfaceWidth / 2);
        var surfaceCenterY = surfaceTop + (surfaceHeight / 2);
        var surfaceRight = surfaceLeft + surfaceWidth;
        var waveA = Math.Sin(liquidRippleTime * 4.8) * (3.4 + (liquidImpact * 6.0));
        var waveB = Math.Sin((liquidRippleTime * 6.2) + 1.1) * (2.4 + (liquidImpact * 4.4));
        var mouthClip = CreateCauldronMouthClip(potOffset);

        using (context.PushGeometryClip(mouthClip))
        using (context.PushOpacity(Math.Clamp(0.42 + (level * 0.78), 0, 1)))
        {
            context.DrawEllipse(liquid, null, new Rect(surfaceLeft, surfaceTop + potOffset, surfaceWidth, surfaceHeight));
            context.DrawGeometry(
                null,
                new Pen(Brush(0x44251B13), 1.1),
                StreamGeometry.Parse($"M{surfaceLeft + (surfaceWidth * 0.08):0.##},{surfaceCenterY + 2 + potOffset:0.##} C{surfaceLeft + (surfaceWidth * 0.30):0.##},{surfaceTop + surfaceHeight + 7 + potOffset:0.##} {surfaceLeft + (surfaceWidth * 0.69):0.##},{surfaceTop + surfaceHeight + 6 + potOffset:0.##} {surfaceRight - (surfaceWidth * 0.08):0.##},{surfaceCenterY + 2 + potOffset:0.##}"));

            var highlightWidth = Math.Max(24, surfaceWidth * 0.39);
            context.DrawGeometry(
                null,
                new Pen(Brush(0x99F5E8C8), 1.75),
                StreamGeometry.Parse($"M{cauldronVisualCenterX - (highlightWidth / 2):0.##},{surfaceCenterY - 3 + waveA + potOffset:0.##} C{cauldronVisualCenterX - (highlightWidth * 0.14):0.##},{surfaceCenterY - 8 + waveB + potOffset:0.##} {cauldronVisualCenterX + (highlightWidth * 0.18):0.##},{surfaceCenterY + 2 - waveA + potOffset:0.##} {cauldronVisualCenterX + (highlightWidth / 2):0.##},{surfaceCenterY - 2 - waveB + potOffset:0.##}"));
            context.DrawGeometry(
                null,
                new Pen(Brush(0x77F5E8C8), 1.15),
                StreamGeometry.Parse($"M{surfaceLeft + (surfaceWidth * 0.23):0.##},{surfaceCenterY + 8 - waveB + potOffset:0.##} C{surfaceLeft + (surfaceWidth * 0.39):0.##},{surfaceCenterY + 3 + waveA + potOffset:0.##} {surfaceLeft + (surfaceWidth * 0.59):0.##},{surfaceCenterY + 11 - waveA + potOffset:0.##} {surfaceLeft + (surfaceWidth * 0.76):0.##},{surfaceCenterY + 6 + waveB + potOffset:0.##}"));
            context.DrawGeometry(
                null,
                new Pen(Brush(0x55F5E8C8), 0.9),
                StreamGeometry.Parse($"M{surfaceLeft + (surfaceWidth * 0.31):0.##},{surfaceCenterY + 16 + waveA + potOffset:0.##} C{surfaceLeft + (surfaceWidth * 0.43):0.##},{surfaceCenterY + 10 - waveB + potOffset:0.##} {surfaceLeft + (surfaceWidth * 0.58):0.##},{surfaceCenterY + 19 + waveB + potOffset:0.##} {surfaceLeft + (surfaceWidth * 0.69):0.##},{surfaceCenterY + 14 - waveA + potOffset:0.##}"));

            if (liquidImpact > 0.01)
            {
                var ringWidth = 34 + ((1 - liquidImpact) * 78);
                var ringHeight = 7 + ((1 - liquidImpact) * 14);
                var ringCenterX = Math.Clamp(
                    pourImpactPoint.X,
                    surfaceLeft + (ringWidth / 2),
                    surfaceRight - (ringWidth / 2));
                context.DrawEllipse(
                    null,
                    new Pen(Brush(0xAAE7F0D6), 1.2 * liquidImpact),
                    new Rect(ringCenterX - (ringWidth / 2), surfaceCenterY - (ringHeight / 2) + potOffset, ringWidth, ringHeight));
            }
        }

    }

    private static StreamGeometry CreateCauldronMouthClip(double potOffset) => StreamGeometry.Parse(
        $"M684,{382 + potOffset:0.##} C684,{351 + potOffset:0.##} 956,{351 + potOffset:0.##} 956,{382 + potOffset:0.##} C956,{410 + potOffset:0.##} 684,{410 + potOffset:0.##} 684,{382 + potOffset:0.##} Z");

    private double CurrentPotOffset()
    {
        var loweredOffset = DataContext is AlchemyWorkshopViewModel { IsCauldronLowered: true } ? 34 : 0;
        return loweredOffset + cauldronGestureOffset;
    }

    private static double GetCauldronSurfaceBaselineY(double level, double potOffset) =>
        401 - (Math.Clamp(level, 0, 0.78) * 55) + potOffset;

    private void DrawPourStream(DrawingContext context, double potOffset)
    {
        if (activePourTrajectory is not { } trajectory
            || pourVisualLiquid is not { } liquid
            || pourTail <= 0.002)
        {
            return;
        }

        var visibleWidth = trajectory.Width * Math.Clamp(pourTail, 0, 1);
        var wobbleStrength = (0.38 + ((1 - trajectory.Flow) * 1.25)) * pourTail;

        for (var index = 0; index < PourStreamSampleCount; index++)
        {
            var t = index / (double)(PourStreamSampleCount - 1);
            var center = trajectory.PositionAt(t);
            var before = trajectory.PositionAt(Math.Max(0, t - 0.025));
            var after = trajectory.PositionAt(Math.Min(1, t + 0.025));
            var tangent = NormalizeVector(after - before);
            var normal = new Vector(-tangent.Y, tangent.X);
            var taper = 1 - (0.48 * t);
            var pulse = Math.Sin((pourPhase * 5.2) + (index * 1.17)) * wobbleStrength * (1 - (t * 0.5));
            var halfWidth = Math.Max(0.65, (visibleWidth * taper) / 2);
            pourStreamCenters[index] = center + (normal * pulse);
            pourStreamLeft[index] = pourStreamCenters[index] + (normal * halfWidth);
            pourStreamRight[index] = pourStreamCenters[index] - (normal * halfWidth);
        }

        var liquidColor = LiquidColor(liquid);
        if (visibleWidth < 2.25 || pourTail < 0.24)
        {
            var phaseOffset = (int)Math.Floor(pourPhase * 4) % 3;
            for (var index = 1 + phaseOffset; index < PourStreamSampleCount; index += 3)
            {
                var t = index / (double)(PourStreamSampleCount - 1);
                var size = Math.Max(1.25, visibleWidth * (0.62 - (t * 0.16)));
                context.DrawEllipse(
                    Brush(liquidColor, 0.78 * pourTail),
                    new Pen(Brush(MixColor(liquidColor, BrushColor(0xFFF2E5C4), 0.18), 0.72), 0.7),
                    new Rect(pourStreamCenters[index].X - size, pourStreamCenters[index].Y - (size * 1.35), size * 2, size * 2.7));
            }
        }
        else
        {
            pourBodyBuilder.Clear();
            pourBodyBuilder.Append('M');
            AppendGeometryPoint(pourBodyBuilder, pourStreamLeft[0]);
            for (var index = 1; index < PourStreamSampleCount; index++)
            {
                pourBodyBuilder.Append(" L");
                AppendGeometryPoint(pourBodyBuilder, pourStreamLeft[index]);
            }

            for (var index = PourStreamSampleCount - 1; index >= 0; index--)
            {
                pourBodyBuilder.Append(" L");
                AppendGeometryPoint(pourBodyBuilder, pourStreamRight[index]);
            }

            pourBodyBuilder.Append(" Z");
            context.DrawGeometry(
                Brush(liquidColor, 0.72 * pourTail),
                new Pen(Brush(MixColor(liquidColor, BrushColor(0xFF2B2118), 0.24), 0.68 * pourTail), 0.8),
                StreamGeometry.Parse(pourBodyBuilder.ToString()));

            pourHighlightBuilder.Clear();
            pourHighlightBuilder.Append('M');
            AppendGeometryPoint(pourHighlightBuilder, pourStreamCenters[1]);
            for (var index = 2; index < PourStreamSampleCount - 1; index++)
            {
                pourHighlightBuilder.Append(" L");
                AppendGeometryPoint(pourHighlightBuilder, pourStreamCenters[index] + new Vector(-0.7, -0.25));
            }

            context.DrawGeometry(
                null,
                new Pen(Brush(MixColor(liquidColor, BrushColor(0xFFF7EBCB), 0.72), 0.68 * pourTail), Math.Max(0.65, visibleWidth * 0.16)),
                StreamGeometry.Parse(pourHighlightBuilder.ToString()));
        }

        if (trajectory.HitsCauldron && cauldronLiquidLevel > 0.002)
        {
            var impactWidth = 10 + (trajectory.Flow * 24);
            var impactY = GetCauldronSurfaceBaselineY(cauldronLiquidLevel, potOffset);
            context.DrawEllipse(
                Brush(MixColor(liquidColor, BrushColor(0xFF2B2118), 0.22), 0.32 * pourTail),
                null,
                new Rect(trajectory.Impact.X - (impactWidth / 2), impactY - 2.3, impactWidth, 4.6));
            context.DrawEllipse(
                null,
                new Pen(Brush(MixColor(liquidColor, BrushColor(0xFFF5E8C8), 0.7), 0.72 * pourTail), 1.05),
                new Rect(trajectory.Impact.X - (impactWidth * 0.68), impactY - 5.2, impactWidth * 1.36, 10.4));
        }
    }

    private static void AppendGeometryPoint(StringBuilder builder, Point point)
    {
        builder.Append(point.X.ToString("0.##", CultureInfo.InvariantCulture));
        builder.Append(',');
        builder.Append(point.Y.ToString("0.##", CultureInfo.InvariantCulture));
    }

    private static Vector NormalizeVector(Vector vector)
    {
        var length = Math.Sqrt((vector.X * vector.X) + (vector.Y * vector.Y));
        return length <= 0.0001 ? new Vector(0, 1) : vector / length;
    }

    private void DrawBellows(DrawingContext context, AlchemyWorkshopViewModel? vm)
    {
        var open = bellowsPull * 42;
        sprites.Draw(context, "image3_bellows", new Rect(884, 518 - (open * 0.35), 326, 158 + (open * 0.7)), 0, 1);
        var body = StreamGeometry.Parse($"M982,{538 - open:0.##} C1030,{512 - open:0.##} 1120,{516 - open:0.##} 1162,{544 - open:0.##} L1150,{622 + open:0.##} C1100,{647 + open:0.##} 1026,{644 + open:0.##} 984,{616 + open:0.##} Z");
        context.DrawGeometry(Brush(0x068F5A3B), new Pen(Brush(0x24241B14), 1.2), body);
        context.DrawLine(new Pen(Brush(0xBB795031), 9), new Point(1070, 620 + open), new Point(1070, 680 + open));
        context.DrawLine(new Pen(Brush(0xAA2B2118), 3), new Point(1016, 682 + open), new Point(1124, 682 + open));
        context.DrawLine(new Pen(Brush(0xFFD08B38), 4), new Point(922, 599), new Point(904, 604));
        DrawText(context, bellowsPull > 0.08
            ? $"{Ui(vm, AlchemyTextKey.Pull)} {bellowsPull:P0}"
            : Ui(vm, AlchemyTextKey.Ready), new Point(991, 710), 11, Ink, UiTypeface, 165, TextAlignment.Center);
    }

    private void DrawHourglass(DrawingContext context, AlchemyWorkshopViewModel? vm)
    {
        var center = new Point(1095, 326);
        var engineRunning = vm?.IsHourglassRunning == true;
        var running = engineRunning || hourglassVisualRunning;
        var turning = dragMode == DragMode.Hourglass && hourglassRotation > 24;
        var visualFlowing = running || turning;
        var visualProgress = engineRunning ? vm?.HourglassProgress ?? 0 : hourglassVisualProgress;
        var angle = (running ? 180 : 0) + hourglassRotation;
        using (context.PushTransform(Matrix.CreateTranslation(center.X, center.Y)))
        using (context.PushTransform(Matrix.CreateRotation(angle * Math.PI / 180)))
        {
            var hourglassInterior = CreateHourglassInteriorClip();
            // Sand belongs behind the glass. Drawing it over the PNG erased the painted
            // reflections and made the fill appear to cross the physical glass wall.
            using (context.PushGeometryClip(hourglassInterior))
            {
                DrawHourglassSand(context, visualFlowing, visualProgress);
            }

            // The generated source contains an unrelated object on its right edge. Crop to
            // the hourglass itself so its optical centre matches this local coordinate system.
            // Draw it last so the original glass rim and highlights mask the sand edges.
            sprites.DrawRegion(
                context,
                "image2_hourglass",
                new Rect(0, 0, 0.61, 1),
                new Rect(-46, -82, 92, 164),
                0,
                1);
        }

        DrawText(context, running
            ? $"{Ui(vm, AlchemyTextKey.Timing)} {visualProgress:P0}"
            : Ui(vm, AlchemyTextKey.Ready), new Point(1018, 411), 10, Ink, UiTypeface, 154, TextAlignment.Center);
        if (hourglassFlipArmed)
        {
            DrawText(context, $"{vm?.SelectedTurns ?? 1} {Ui(vm, AlchemyTextKey.TurnsSelected)}", new Point(1018, 426), 10, Brass, UiTypeface, 154, TextAlignment.Center);
        }
    }

    private static StreamGeometry CreateHourglassInteriorClip() => StreamGeometry.Parse(
        // Pixel-traced from the two enclosed transparent components in the source PNG.
        // Each side follows its own measured row samples; the narrow middle path keeps
        // the falling stream visible through the neck while the gold collar stays above it.
        "M-18.3,-46.4 L-21.5,-44.3 L-21.5,-42.2 L-20.7,-38 L-20.2,-33.7 L-18.3,-29.5 L-15.9,-25.2 L-13,-21 L-9.8,-16.7 L-6.6,-12.5 L-3.1,-8.2 L-1,-5.6 " +
        "L0.1,-5.6 L1.7,-8.2 L4.9,-12.5 L8.4,-16.7 L11.6,-21 L14.2,-25.2 L16.6,-29.5 L18.5,-33.7 L19,-38 L18.8,-42.2 L17.7,-44.3 L16.4,-46.4 Z " +
        "M-1.8,0 L-4.7,2.1 L-8.2,6.4 L-11.1,10.6 L-14.3,14.9 L-17,19.1 L-21.2,23.4 L-20.4,27.6 L-20.7,31.8 L-20.2,36.1 L-18.3,40.3 L-15.9,42.2 " +
        "L14,42.2 L16.6,40.3 L18.8,36.1 L19.6,31.8 L19,27.6 L17.7,23.4 L15.3,19.1 L12.6,14.9 L9.7,10.6 L6.5,6.4 L2.8,2.1 L0.4,0 Z " +
        "M-1.1,-6 L0.2,-6 L0.5,2.5 L-2.1,2.5 Z");

    private void DrawHourglassSand(DrawingContext context, bool running, double progress)
    {
        var sand = Brush(0xFFD4A13B);
        var sandShadow = Brush(0xCC8C642A);
        var sandHighlight = Brush(0xFFE9C566);
        if (!running)
        {
            var surfaceY = HourglassSandVisual.SettledSurfaceY;
            var settledBounds = HourglassSandVisual.InnerBounds(surfaceY);
            context.DrawGeometry(
                sand,
                new Pen(sandShadow, 0.75),
                StreamGeometry.Parse(
                    $"M-30,53 L30,53 L30,{surfaceY:0.##} " +
                    $"C{settledBounds.Center + (settledBounds.HalfWidth * 0.34):0.##},{surfaceY - 2.5:0.##} " +
                    $"{settledBounds.Center - (settledBounds.HalfWidth * 0.34):0.##},{surfaceY - 2.5:0.##} -30,{surfaceY:0.##} Z"));
            context.DrawGeometry(
                null,
                new Pen(sandHighlight, 1.1),
                StreamGeometry.Parse(
                    $"M{settledBounds.Center - (settledBounds.HalfWidth * 0.72):0.##},{surfaceY - 0.4:0.##} " +
                    $"C{settledBounds.Center - (settledBounds.HalfWidth * 0.22):0.##},{surfaceY - 2.2:0.##} " +
                    $"{settledBounds.Center + (settledBounds.HalfWidth * 0.28):0.##},{surfaceY - 2.1:0.##} " +
                    $"{settledBounds.Center + (settledBounds.HalfWidth * 0.72):0.##},{surfaceY - 0.3:0.##}"));
            return;
        }

        var state = HourglassSandVisual.Calculate(progress);
        if (state.Remaining > 0.001)
        {
            var source = StreamGeometry.Parse(
                $"M-30,{state.SourceSurfaceY:0.##} " +
                $"C-12,{state.SourceSurfaceY - 2.2:0.##} 12,{state.SourceSurfaceY - 2.2:0.##} 30,{state.SourceSurfaceY:0.##} " +
                "L30,3.2 L-30,3.2 Z");
            context.DrawGeometry(sand, new Pen(sandShadow, 0.7), source);
            context.DrawGeometry(
                null,
                new Pen(sandHighlight, 0.9),
                StreamGeometry.Parse(
                    $"M{state.SourceCenterX - (state.SourceHalfWidth * 0.72):0.##},{state.SourceSurfaceY - 0.8:0.##} " +
                    $"C{state.SourceCenterX - (state.SourceHalfWidth * 0.2):0.##},{state.SourceSurfaceY - 2.1:0.##} {state.SourceCenterX + (state.SourceHalfWidth * 0.34):0.##},{state.SourceSurfaceY - 1.8:0.##} {state.SourceCenterX + (state.SourceHalfWidth * 0.72):0.##},{state.SourceSurfaceY - 0.6:0.##}"));
        }

        if (state.Progress > 0.001)
        {
            var receiver = StreamGeometry.Parse(
                $"M-30,-53 L30,-53 L30,{state.ReceiverSurfaceY:0.##} " +
                $"C12,{state.ReceiverSurfaceY + 3.2:0.##} -12,{state.ReceiverSurfaceY + 3.2:0.##} -30,{state.ReceiverSurfaceY:0.##} Z");
            context.DrawGeometry(sand, new Pen(sandShadow, 0.75), receiver);
            context.DrawGeometry(
                null,
                new Pen(sandHighlight, 1),
                StreamGeometry.Parse(
                    $"M{state.ReceiverCenterX - (state.ReceiverHalfWidth * 0.74):0.##},{state.ReceiverSurfaceY + 0.5:0.##} " +
                    $"C{state.ReceiverCenterX - (state.ReceiverHalfWidth * 0.22):0.##},{state.ReceiverSurfaceY + 2.6:0.##} {state.ReceiverCenterX + (state.ReceiverHalfWidth * 0.28):0.##},{state.ReceiverSurfaceY + 2.3:0.##} {state.ReceiverCenterX + (state.ReceiverHalfWidth * 0.74):0.##},{state.ReceiverSurfaceY + 0.4:0.##}"));
        }

        if (!state.StreamVisible)
        {
            return;
        }

        const double neckCenterX = -0.65;
        var streamX = neckCenterX + (Math.Sin(hourglassSandPhase * 2.35) * 0.38);
        var streamWidth = 1.15 + ((Math.Sin(hourglassSandPhase * 3.8) + 1) * 0.24);
        context.DrawLine(
            new Pen(sandShadow, streamWidth + 0.9),
            new Point(streamX, -1.5),
            new Point(streamX * 0.35, state.ReceiverSurfaceY - 1.6));
        context.DrawLine(
            new Pen(sandHighlight, streamWidth),
            new Point(streamX, -1.5),
            new Point(streamX * 0.35, state.ReceiverSurfaceY - 1.6));

        for (var index = 0; index < 3; index++)
        {
            var fall = ((hourglassSandPhase * 0.36) + (index / 3.0)) % 1;
            var grainY = Lerp(-2.2, state.ReceiverSurfaceY - 2.3, fall);
            var grainX = streamX + (Math.Sin(hourglassSandPhase + (index * 2.1)) * 0.75);
            var grainSize = 0.65 + ((index % 2) * 0.25);
            context.DrawEllipse(
                sandHighlight,
                null,
                new Rect(grainX - grainSize, grainY - grainSize, grainSize * 2, grainSize * 2));
        }
    }

    private void DrawDistiller(DrawingContext context, AlchemyWorkshopViewModel? vm)
    {
        DrawEmptyOriginalDistiller(context);
        context.DrawEllipse(Brush(0x10C69D5C), new Pen(Brush(0x552A2118), 1.2), new Rect(1178, 452, 124, 112));
        context.DrawGeometry(null, new Pen(Brush(0xAA2B2118), 3), StreamGeometry.Parse("M1210,466 C1214,420 1260,410 1274,456 L1322,456 C1354,456 1370,432 1378,408"));
        context.DrawLine(new Pen(Brush(0xFF775437), 12), new Point(1328, 385), new Point(1328, 508 + (distillerPull * 78)));
        context.DrawLine(new Pen(Ink, 5), new Point(1290, 507 + (distillerPull * 78)), new Point(1366, 507 + (distillerPull * 78)));
        context.DrawEllipse(Brush(0x6B3A2A1C), new Pen(Brush(0xCC241B13), 1.4), new Rect(1274, 662, 68, 16));
        context.DrawEllipse(null, new Pen(Brush(0xA6B98B45), 1), new Rect(1282, 665, 52, 9));

        var status = distillerPull > 0
            ? $"{Ui(vm, AlchemyTextKey.Press)} {distillerPull:P0}"
            : bottleCollectionActive && bottleCollectionSource == BottleCollectionSource.Condenser
                ? vm?.UseEnglish == true
                    ? $"Collecting {bottleCollectionProgress:P0}"
                    : $"收集中 {bottleCollectionProgress:P0}"
                : distillerBottleInstalled
                    ? vm?.UseEnglish == true ? "Bottle fitted - press the pump" : "空瓶已就位，按压泵杆"
                    : CurrentStepKind(vm) == RecipeStepKind.Distill
                        ? vm?.UseEnglish == true ? "Fit an empty bottle first" : "先放置空瓶"
                        : vm?.DistillerText ?? Ui(null, AlchemyTextKey.Ready);
        DrawText(context, status, new Point(1168, 698), 10, Ink, UiTypeface, 242, TextAlignment.Center);

        var collectingDirectly = bottleCollectionActive && bottleCollectionSource == BottleCollectionSource.Cauldron;
        if (!collectingDirectly)
        {
            if (dragMode == DragMode.ProductBottle)
            {
                DrawProductBottle(context, dragVisual, 0, vm?.LiquidBrush);
            }
            else if (distillerBottleInstalled)
            {
                if (bottleCollectionActive && bottleCollectionSource == BottleCollectionSource.Condenser && vm is not null)
                {
                    DrawCondenserBottleCollection(context, vm);
                }
                else
                {
                    DrawProductBottle(context, distillerBottlePosition, 0, vm?.LiquidBrush);
                }
            }
        }
    }

    private void DrawEmptyOriginalDistiller(DrawingContext context)
    {
        var bounds = new Rect(1138, 376, 266, 246);

        // Keep the original round-bellied apparatus, but exclude the receiver bottle
        // that is baked into the lower-right corner of the source sprite.
        using (context.PushClip(new Rect(bounds.X, bounds.Y, 168, bounds.Height)))
        {
            sprites.Draw(context, "image3_alembic", bounds, 0, 1);
        }

        using (context.PushClip(new Rect(bounds.X + 168, bounds.Y, bounds.Width - 168, 121)))
        {
            sprites.Draw(context, "image3_alembic", bounds, 0, 1);
        }

        // Continue the fixed condenser into a free-standing outlet. The receiver area
        // below remains empty until the player installs the separate product bottle.
        context.DrawLine(
            new Pen(Brush(0xFF8C5E31), 9),
            new Point(1327, 493),
            new Point(1327, 535));
        context.DrawLine(
            new Pen(Brush(0xFFD39A56), 2.2),
            new Point(1324.5, 494),
            new Point(1324.5, 532));
        context.DrawEllipse(
            Brush(0xFF9D6A36),
            new Pen(Brush(0xFF3A2518), 1.6),
            new Rect(1318, 528, 18, 9));
    }

    private void DrawProductBottleSupply(DrawingContext context, AlchemyWorkshopViewModel? vm)
    {
        if (vm?.IsBrewComplete == true
            || dragMode == DragMode.ProductBottle
            || bottleCollectionActive
            || distillerBottleInstalled)
        {
            return;
        }

        DrawProductBottle(context, ProductBottleHitbox.Center, 0, vm?.LiquidBrush);
    }

    private void DrawQuantityPickerModern(DrawingContext context)
    {
        if (DataContext is not AlchemyWorkshopViewModel vm)
        {
            return;
        }

        switch (quantityPickerMode)
        {
            case QuantityPickerMode.Ingredient when quantityPickerIngredient is not null:
                DrawSegmentPicker(context, IngredientPickerBounds(quantityPickerIngredient.Index), Ui(vm, AlchemyTextKey.Quantity),
                    vm.SelectedCount, index => IngredientPickerOptionRect(quantityPickerIngredient.Index, index));
                break;
            case QuantityPickerMode.Hourglass:
                DrawSegmentPicker(context, HourglassPickerBounds, Ui(vm, AlchemyTextKey.Turns), vm.SelectedTurns, HourglassPickerOptionRect);
                break;
        }
    }

    private void DrawSegmentPicker(DrawingContext context, Rect bounds, string title, int selected, Func<int, Rect> optionRect)
    {
        DrawHudPlaque(context, bounds, true, false);
        context.DrawRectangle(null, new Pen(Brush(AlchemyHudTheme.Accent), 0.8),
            new RoundedRect(new Rect(bounds.X + 3, bounds.Y + 3, bounds.Width - 6, bounds.Height - 6), 2));
        DrawText(context, title, new Point(bounds.X + 7, bounds.Y + 4), 10, Brush(AlchemyHudTheme.TextPrimary), UiTypeface,
            bounds.Width - 14, TextAlignment.Center);
        for (var index = 0; index < 3; index++)
        {
            var rect = optionRect(index);
            var isSelected = selected == index + 1;
            var target = new AlchemyHudTarget(AlchemyHudTargetKind.QuantityOption, index);
            var state = DataContext is AlchemyWorkshopViewModel vm
                ? InteractionStateFor(vm, target, isSelected)
                : isSelected
                    ? AlchemyInteractionState.Selected
                    : AlchemyInteractionState.Default;
            var interaction = AlchemyInteractionPresentation.From(state);
            var shifted = new Rect(rect.X, rect.Y + interaction.ContentOffset, rect.Width, rect.Height);
            context.DrawRectangle(
                state == AlchemyInteractionState.Selected ? Brush(AlchemyHudTheme.Accent) : Brush(interaction.Surface),
                new Pen(Brush(interaction.Border), interaction.BorderThickness),
                new RoundedRect(shifted, 2));
            DrawText(context, (index + 1).ToString(CultureInfo.InvariantCulture), new Point(rect.X, rect.Y + 4 + interaction.ContentOffset), 11,
                state == AlchemyInteractionState.Selected ? Ink : Brush(interaction.Text), UiTypeface, rect.Width, TextAlignment.Center);
        }
    }

    private void DrawBottomRailModern(DrawingContext context, AlchemyWorkshopViewModel? vm)
    {
        var hud = AlchemyHudPresentation.From(vm);
        DrawHudBandFrame(context, AlchemyHudLayout.BottomRail);
        DrawHudDivider(context, 1034, 751, 59);
        context.DrawRectangle(Brush(AlchemyHudTheme.Accent), null, new Rect(18, 750, 3, 54));

        DrawText(context, Ui(vm, AlchemyTextKey.Temperature), new Point(24, 752), 10, Brush(AlchemyHudTheme.TextMuted), UiTypeface);
        DrawText(context, vm?.TemperatureText ?? "0°", new Point(24, 769), 23,
            vm?.HeatBrush ?? Brush(AlchemyHudTheme.TextPrimary), DisplayTypeface);
        DrawText(context, vm?.HeatBandText ?? Ui(null, AlchemyTextKey.Cold), new Point(92, 778), 12, Brush(AlchemyHudTheme.TextSecondary), UiTypeface);

        var rail = new Rect(156, 768, 820, 14);
        context.DrawRectangle(Brush(AlchemyHudTheme.SurfaceInset), new Pen(Brush(AlchemyHudTheme.BorderDark), 1),
            new RoundedRect(new Rect(rail.X - 3, rail.Y - 3, rail.Width + 6, rail.Height + 6), 3));
        var heatSegments = new[] { AlchemyHudTheme.Cold, AlchemyHudTheme.Simmering, AlchemyHudTheme.Boiling, AlchemyHudTheme.Danger };
        var segmentWidth = rail.Width / heatSegments.Length;
        for (var index = 0; index < heatSegments.Length; index++)
        {
            context.DrawRectangle(Brush(heatSegments[index]), null,
                new Rect(rail.X + (index * segmentWidth), rail.Y, segmentWidth, rail.Height));
        }
        context.DrawRectangle(null, new Pen(Brush(AlchemyHudTheme.TextSecondary), 1.25), new RoundedRect(rail, 2));
        var temp = Math.Clamp(vm?.Temperature ?? 0, 0, 100);
        var markerX = rail.X + ((temp / 100) * rail.Width);
        context.DrawGeometry(Brush(AlchemyHudTheme.TextPrimary), null,
            StreamGeometry.Parse($"M{markerX - 6:0.##},765 L{markerX + 6:0.##},765 L{markerX:0.##},757 Z"));
        DrawText(context, Ui(vm, AlchemyTextKey.Cold), new Point(158, 789), 9, Brush(AlchemyHudTheme.TextMuted), UiTypeface);
        DrawText(context, Ui(vm, AlchemyTextKey.Simmer), new Point(374, 789), 9, Brush(AlchemyHudTheme.TextMuted), UiTypeface);
        DrawText(context, Ui(vm, AlchemyTextKey.Boiling), new Point(590, 789), 9, Brush(AlchemyHudTheme.TextMuted), UiTypeface);
        DrawText(context, Ui(vm, AlchemyTextKey.Hot), new Point(896, 789), 9, Brush(AlchemyHudTheme.Danger), UiTypeface);

        DrawHudIconBadge(context, "hud_potion", new Rect(1054, 763, 36, 36), hud.OutcomeTone != AlchemyHudTone.Neutral);
        DrawText(context, Ui(vm, AlchemyTextKey.Result), new Point(1100, 752), 10, Brush(AlchemyHudTheme.TextMuted), UiTypeface);
        DrawStatusDot(context, new Point(1097, 787), hud.OutcomeTone);
        DrawText(context, hud.OutcomeLabel, new Point(1108, 772), 14, Brush(AlchemyHudTheme.ColorFor(hud.OutcomeTone)), UiTypeface, 112);
        DrawText(context, Ui(vm, AlchemyTextKey.Yield), new Point(1260, 752), 10, Brush(AlchemyHudTheme.TextMuted), UiTypeface);
        DrawText(context, hud.YieldValue, new Point(1260, 772), 17, Brush(AlchemyHudTheme.TextPrimary), UiTypeface, 92);
        DrawFeedbackToast(context, hud);
    }

    private static void DrawFeedbackToast(DrawingContext context, AlchemyHudPresentation hud)
    {
        if (string.IsNullOrWhiteSpace(hud.Feedback))
        {
            return;
        }

        var bounds = AlchemyHudLayout.Feedback;
        var tone = AlchemyHudTheme.ColorFor(hud.FeedbackTone);
        DrawHudPlaque(context, bounds, false, false);
        context.DrawRectangle(Brush(tone), null, new Rect(bounds.X, bounds.Y + 3, 3, bounds.Height - 6));
        DrawStatusDot(context, new Point(bounds.X + 14, bounds.Center.Y), hud.FeedbackTone);
        DrawText(context, hud.Feedback, new Point(bounds.X + 26, bounds.Y + 7), 10, Brush(AlchemyHudTheme.TextSecondary), UiTypeface,
            bounds.Width - 38);
    }

    private void DrawRecipeBookModern(DrawingContext context)
    {
        if (DataContext is not AlchemyWorkshopViewModel { IsRecipeBookOpen: true } vm)
        {
            return;
        }

        context.DrawRectangle(Brush(AlchemyHudTheme.ModalScrim), null, new Rect(0, 72, SceneWidth, 670));
        var book = AlchemyHudLayout.RecipeBook;
        context.DrawRectangle(Brush(0x66000000), null, new RoundedRect(new Rect(book.X + 10, book.Y + 12, book.Width, book.Height), 8));
        context.DrawRectangle(Brush(AlchemyHudTheme.BookCover), new Pen(Brush(AlchemyHudTheme.BookCoverEdge), 2.5), new RoundedRect(book, 7));

        var leftPage = StreamGeometry.Parse("M262,112 C376,96 596,98 713,116 L713,682 C588,668 374,670 262,686 Z");
        var rightPage = StreamGeometry.Parse("M727,116 C844,98 1064,96 1178,112 L1178,686 C1066,670 852,668 727,682 Z");
        context.DrawGeometry(Brush(AlchemyHudTheme.BookPage), new Pen(Brush(AlchemyHudTheme.BookRule), 1.2), leftPage);
        context.DrawGeometry(Brush(AlchemyHudTheme.BookPageSecondary), new Pen(Brush(AlchemyHudTheme.BookRule), 1.2), rightPage);
        context.DrawRectangle(Brush(0x24302116), null, new Rect(book.Center.X - 10, 112, 20, 570));
        context.DrawLine(new Pen(Brush(0xA46E4D30), 1), new Point(book.Center.X - 7, 116), new Point(book.Center.X - 7, 680));
        context.DrawLine(new Pen(Brush(0x66F1DFB5), 1), new Point(book.Center.X + 7, 116), new Point(book.Center.X + 7, 680));
        context.DrawGeometry(Brush(0xFF9D4B3C), new Pen(Brush(0xFF6E332B), 0.8),
            StreamGeometry.Parse("M714,104 L726,104 L726,174 L720,164 L714,174 Z"));
        context.DrawLine(new Pen(Brush(0x7C8A653D), 0.8), new Point(278, 118), new Point(278, 672));
        context.DrawLine(new Pen(Brush(0x7C8A653D), 0.8), new Point(1162, 118), new Point(1162, 672));
        DrawHudStud(context, new Point(book.X + 12, book.Y + 12), 3);
        DrawHudStud(context, new Point(book.Right - 12, book.Y + 12), 3);
        DrawHudStud(context, new Point(book.X + 12, book.Bottom - 12), 3);
        DrawHudStud(context, new Point(book.Right - 12, book.Bottom - 12), 3);

        DrawHudIconBadge(context, "hud_recipe_book", new Rect(288, 122, 42, 42), false);
        DrawText(context, Ui(vm, AlchemyTextKey.PotionRecipe), new Point(342, 128), 10, Brush(AlchemyHudTheme.BookInkMuted), UiTypeface);
        DrawText(context, vm.RecipeName(vm.SelectedRecipe), new Point(342, 149), 27, Brush(AlchemyHudTheme.BookInk), DisplayTypeface, 320);
        DrawText(context, Ui(vm, AlchemyTextKey.Effect), new Point(292, 194), 10, Brush(AlchemyHudTheme.BookInkMuted), UiTypeface);
        DrawText(context, vm.RecipeEffect(vm.SelectedRecipe), new Point(292, 214), 12, Brush(AlchemyHudTheme.BookInk), UiTypeface, 370);

        var current = Math.Min(vm.Snapshot.CurrentStepIndex + 1, vm.SelectedRecipe.Steps.Count);
        DrawHudIconBadge(context, "hud_quality", new Rect(754, 122, 42, 42), false);
        DrawText(context, Ui(vm, AlchemyTextKey.BrewingOrder), new Point(808, 128), 10, Brush(AlchemyHudTheme.BookInkMuted), UiTypeface);
        DrawText(context, Ui(vm, AlchemyTextKey.BrewingOrderInstruction), new Point(808, 149), 21, Brush(AlchemyHudTheme.BookInk), DisplayTypeface, 220);
        DrawText(context, $"{Ui(vm, AlchemyTextKey.Step)} {current:00} / {vm.SelectedRecipe.Steps.Count:00}", new Point(1020, 154), 11,
            Brush(AlchemyHudTheme.BookInkMuted), UiTypeface, 118, TextAlignment.Right);
        DrawText(context, vm.CurrentStepText, new Point(758, 194), 12, Brush(0xFF7E3E22), UiTypeface, 376);

        context.DrawLine(new Pen(Brush(AlchemyHudTheme.BookRule), 1), new Point(286, 232), new Point(686, 232));
        context.DrawLine(new Pen(Brush(AlchemyHudTheme.BookRule), 1), new Point(752, 232), new Point(1152, 232));

        for (var index = 0; index < vm.SelectedRecipe.Steps.Count; index++)
        {
            var step = vm.SelectedRecipe.Steps[index];
            var bounds = AlchemyHudLayout.RecipeStep(index);
            var completed = index < vm.Snapshot.CurrentStepIndex;
            var isCurrent = index == vm.Snapshot.CurrentStepIndex;
            if (isCurrent)
            {
                context.DrawRectangle(Brush(0x2EB9853A), null, bounds);
                context.DrawRectangle(Brush(AlchemyHudTheme.Accent), null, new Rect(bounds.X, bounds.Y, 3, bounds.Height));
            }

            var tone = completed ? AlchemyHudTone.Success : isCurrent ? AlchemyHudTone.Warning : AlchemyHudTone.Neutral;
            var marker = new Rect(bounds.X + 10, bounds.Y + 16, 28, 28);
            var markerFill = completed || isCurrent ? Brush(AlchemyHudTheme.ColorFor(tone)) : Brush(0x00FFFFFF);
            var markerStroke = completed || isCurrent ? AlchemyHudTheme.ColorFor(tone) : AlchemyHudTheme.BookInkMuted;
            context.DrawEllipse(markerFill, new Pen(Brush(markerStroke), 1.2), marker);
            DrawText(context, completed ? "✓" : (index + 1).ToString(CultureInfo.InvariantCulture),
                new Point(marker.X, marker.Y + 5), 11, completed || isCurrent ? Brush(AlchemyHudTheme.BookInk) : Brush(AlchemyHudTheme.BookInkMuted),
                UiTypeface, marker.Width, TextAlignment.Center);
            DrawText(context, vm.StepLabel(step), new Point(bounds.X + 52, bounds.Y + 9), 13,
                isCurrent ? Brush(0xFF7E3E22) : Brush(AlchemyHudTheme.BookInk), UiTypeface, bounds.Width - 132);
            DrawText(context, completed
                    ? Ui(vm, AlchemyTextKey.Complete)
                    : isCurrent
                        ? Ui(vm, AlchemyTextKey.Current)
                        : Ui(vm, AlchemyTextKey.Pending),
                new Point(bounds.Right - 66, bounds.Y + 10), 10,
                Brush(AlchemyHudTheme.ColorFor(tone)), UiTypeface, 56, TextAlignment.Right);
            DrawText(context, vm.StepHint(step), new Point(bounds.X + 52, bounds.Y + 36), 10,
                Brush(AlchemyHudTheme.BookInkMuted), UiTypeface, bounds.Width - 66);
            context.DrawLine(new Pen(Brush(AlchemyHudTheme.BookRule), 0.8),
                new Point(bounds.X + 52, bounds.Bottom - 5), new Point(bounds.Right - 10, bounds.Bottom - 5));
        }

        var recipeIndex = 0;
        for (var index = 0; index < vm.Recipes.Count; index++)
        {
            if (ReferenceEquals(vm.Recipes[index], vm.SelectedRecipe) || vm.Recipes[index].Id == vm.SelectedRecipe.Id)
            {
                recipeIndex = index;
                break;
            }
        }

        DrawRecipeBookButton(
            context,
            AlchemyHudLayout.RecipeBookPrevious,
            "‹",
            InteractionStateFor(vm, new AlchemyHudTarget(AlchemyHudTargetKind.RecipePrevious)));
        DrawText(context, Ui(vm, AlchemyTextKey.Previous), new Point(344, 660), 11, Brush(AlchemyHudTheme.BookInkMuted), UiTypeface);
        DrawRecipeBookButton(
            context,
            AlchemyHudLayout.RecipeBookNext,
            "›",
            InteractionStateFor(vm, new AlchemyHudTarget(AlchemyHudTargetKind.RecipeNext)));
        DrawText(context, Ui(vm, AlchemyTextKey.Next), new Point(1038, 660), 11, Brush(AlchemyHudTheme.BookInkMuted), UiTypeface, 58, TextAlignment.Right);
        DrawText(context, $"{Ui(vm, AlchemyTextKey.Recipe)} {recipeIndex + 1} / {vm.Recipes.Count}", new Point(book.Center.X - 44, book.Bottom - 46), 10,
            Brush(AlchemyHudTheme.BookInkMuted), UiTypeface, 88, TextAlignment.Center);
        DrawRecipeBookButton(
            context,
            AlchemyHudLayout.RecipeBookClose,
            "×",
            InteractionStateFor(vm, new AlchemyHudTarget(AlchemyHudTargetKind.RecipeClose)));
        DrawRecipeBookTurn(context, book);
    }

    private void DrawRecipeBookTurn(DrawingContext context, Rect book)
    {
        if (!recipeTurnActive || recipeTurnDirection == 0)
        {
            return;
        }

        var eased = recipeTurnProgress * recipeTurnProgress * (3 - (2 * recipeTurnProgress));
        var pageSpan = 451d;
        if (recipeTurnDirection > 0)
        {
            var edge = book.Center.X + 7 + (pageSpan * eased);
            var edgeBottom = edge - Math.Min(18, 18 * (1 - eased));
            var overlay = StreamGeometry.Parse(
                $"M720,116 C{edge - 38:0.##},100 {edge - 14:0.##},106 {edge:0.##},116 L{edgeBottom:0.##},682 C{edge - 14:0.##},692 {edge - 38:0.##},686 720,682 Z");
            context.DrawGeometry(Brush(AlchemyHudTheme.BookPageSecondary), new Pen(Brush(AlchemyHudTheme.BookRule), 1), overlay);
            context.DrawLine(new Pen(Brush(0x80664A32), 2), new Point(edge, 122), new Point(edgeBottom, 676));
        }
        else
        {
            var edge = book.Center.X - 7 - (pageSpan * eased);
            var edgeBottom = edge + Math.Min(18, 18 * (1 - eased));
            var overlay = StreamGeometry.Parse(
                $"M720,116 C{edge + 38:0.##},100 {edge + 14:0.##},106 {edge:0.##},116 L{edgeBottom:0.##},682 C{edge + 14:0.##},692 {edge + 38:0.##},686 720,682 Z");
            context.DrawGeometry(Brush(AlchemyHudTheme.BookPage), new Pen(Brush(AlchemyHudTheme.BookRule), 1), overlay);
            context.DrawLine(new Pen(Brush(0x80664A32), 2), new Point(edge, 122), new Point(edgeBottom, 676));
        }
    }

    private static void DrawRecipeBookButton(
        DrawingContext context,
        Rect bounds,
        string glyph,
        AlchemyInteractionState state)
    {
        var visual = DrawInteractivePlaque(context, bounds, state);
        var shifted = new Rect(bounds.X, bounds.Y + visual.ContentOffset, bounds.Width, bounds.Height);
        context.DrawRectangle(null, new Pen(Brush(visual.Border), 0.8),
            new RoundedRect(new Rect(shifted.X + 3, shifted.Y + 3, shifted.Width - 6, shifted.Height - 6), 2));
        DrawText(context, glyph, new Point(bounds.X, bounds.Y + 5 + visual.ContentOffset), 23,
            Brush(visual.Text), UiTypeface,
            bounds.Width, TextAlignment.Center);
    }

    private void DrawCompletionPresentation(DrawingContext context)
    {
        if (DataContext is not AlchemyWorkshopViewModel vm
            || !vm.IsBrewComplete
            || completionPresentationDismissed)
        {
            return;
        }

        var progress = completionPresentationProgress;
        var stageOpacity = SmoothStep(0.04, 0.54, progress);
        var detailOpacity = SmoothStep(0.48, 0.88, progress);
        var completion = AlchemyCompletionPresentation.From(
            vm.Snapshot.Outcome,
            vm.Snapshot.Mistakes,
            vm.Snapshot.Yield,
            vm.UseEnglish);
        var accent = Brush(AlchemyHudTheme.ColorFor(completion.Tone));

        using (context.PushOpacity(SmoothStep(0, 0.3, progress)))
        {
            context.DrawRectangle(Brush(AlchemyHudTheme.ModalScrim), null, new Rect(0, 0, SceneWidth, SceneHeight));
        }
        using (context.PushOpacity(stageOpacity))
        {
            var stage = AlchemyHudLayout.BrewShowcase;
            context.DrawLine(new Pen(Brush(AlchemyHudTheme.BrassDark), 1.2),
                new Point(stage.X + 40, stage.Y + 16), new Point(stage.Right - 40, stage.Y + 16));
            context.DrawLine(new Pen(Brush(AlchemyHudTheme.BrassDark), 1.2),
                new Point(stage.X + 40, stage.Bottom - 20), new Point(stage.Right - 40, stage.Bottom - 20));
            DrawText(context, Ui(vm, AlchemyTextKey.BrewComplete), new Point(0, 103), 30, Brush(AlchemyHudTheme.TextPrimary), DisplayTypeface,
                SceneWidth, TextAlignment.Center);
            DrawText(context, vm.RecipeName(vm.SelectedRecipe), new Point(0, 145), 15, Brush(AlchemyHudTheme.TextSecondary), UiTypeface,
                SceneWidth, TextAlignment.Center);
        }

        var travel = SmoothStep(0, 0.72, progress);
        var pop = EaseOutBack(Math.Clamp(progress / 0.78, 0, 1));
        var bottleCenter = Lerp(completionBottleOrigin,
            AlchemyHudLayout.BrewShowcaseBottleCenter, travel);
        var bottleScale = Lerp(0.46, 2.72, pop);
        var bottleRotation = Lerp(-9, 0, travel)
            + (Math.Sin(completionPresentationTime * 4.8) * Math.Exp(-completionPresentationTime * 1.65) * 2.4);
        DrawShowcaseBottle(context, vm, bottleCenter, bottleScale, bottleRotation);

        using (context.PushOpacity(detailOpacity))
        {
            var infoX = 808d;
            DrawText(context, Ui(vm, AlchemyTextKey.PotionQuality), new Point(infoX, 245), 13, Brush(AlchemyHudTheme.TextMuted), UiTypeface);
            DrawText(context, completion.QualityScore.ToString(CultureInfo.InvariantCulture), new Point(infoX - 8, 265), 68,
                accent, DisplayTypeface, 184, TextAlignment.Center);
            DrawText(context, "/ 100", new Point(infoX + 174, 310), 12, Brush(AlchemyHudTheme.TextMuted), UiTypeface);
            DrawText(context, completion.Grade, new Point(infoX, 344), 20, accent, UiTypeface, 184, TextAlignment.Center);

            var scoreTrack = new Rect(infoX, 390, 286, 7);
            context.DrawRectangle(Brush(AlchemyHudTheme.SurfaceInset), null, new RoundedRect(scoreTrack, 3));
            context.DrawRectangle(accent, null,
                new RoundedRect(new Rect(scoreTrack.X, scoreTrack.Y, scoreTrack.Width * (completion.QualityScore / 100d), scoreTrack.Height), 3));

            context.DrawLine(new Pen(Brush(AlchemyHudTheme.BorderMuted), 1), new Point(infoX, 432), new Point(infoX + 286, 432));
            DrawStatusDot(context, new Point(infoX + 8, 464), completion.Tone);
            DrawText(context, Ui(vm, AlchemyTextKey.Status), new Point(infoX + 24, 452), 11, Brush(AlchemyHudTheme.TextMuted), UiTypeface);
            DrawText(context, completion.OutcomeLabel, new Point(infoX + 112, 449), 14, accent, UiTypeface, 174,
                TextAlignment.Right);
            DrawText(context, Ui(vm, AlchemyTextKey.Yield), new Point(infoX + 24, 491), 11, Brush(AlchemyHudTheme.TextMuted), UiTypeface);
            DrawText(context, completion.YieldLabel, new Point(infoX + 112, 488), 14, Brush(AlchemyHudTheme.TextPrimary), UiTypeface, 174,
                TextAlignment.Right);
            DrawText(context, Ui(vm, AlchemyTextKey.Mistakes), new Point(infoX + 24, 530), 11, Brush(AlchemyHudTheme.TextMuted), UiTypeface);
            DrawText(context, vm.Snapshot.Mistakes == 0
                    ? Ui(vm, AlchemyTextKey.None)
                    : $"{vm.Snapshot.Mistakes} {Ui(vm, AlchemyTextKey.Total)}",
                new Point(infoX + 112, 527), 14,
                vm.Snapshot.Mistakes == 0 ? Brush(AlchemyHudTheme.Success) : Brush(AlchemyHudTheme.Simmering),
                UiTypeface, 174, TextAlignment.Right);
        }
    }

    private void DrawShowcaseBottle(
        DrawingContext context,
        AlchemyWorkshopViewModel vm,
        Point center,
        double scale,
        double rotation)
    {
        var shadowWidth = 78 * scale;
        context.DrawEllipse(Brush(0x66000000), null,
            new Rect(center.X - (shadowWidth / 2), center.Y + (52 * scale), shadowWidth, 14 * scale));

        using (context.PushTransform(Matrix.CreateTranslation(center.X, center.Y)))
        using (context.PushTransform(Matrix.CreateRotation(rotation * Math.PI / 180)))
        using (context.PushTransform(Matrix.CreateScale(scale, scale)))
        {
            var decay = Math.Exp(-completionPresentationTime * 1.08);
            var slosh = (Math.Sin(completionPresentationTime * 5.2) * 5.2 * decay)
                + (Math.Sin((completionPresentationTime * 2.15) + 0.5) * 1.8);
            var wave = Math.Sin(completionPresentationTime * 7.2) * (1.2 + (decay * 1.2));
            // Follow the bottle's inner silhouette so the finished liquid reads as a
            // full vessel rather than a pool suspended in the lower bulb.
            var leftTop = -29 - (slosh * 0.30);
            var rightTop = -29 + (slosh * 0.30);
            var liquid = StreamGeometry.Parse(
                $"M-9,{leftTop:0.##} C-4,{leftTop + wave:0.##} 4,{rightTop - wave:0.##} 9,{rightTop:0.##} "
                + "L10,-22 C10,-15 11,-10 15,-4 "
                + "C20,4 24,10 27,18 C29,27 28,36 24,43 "
                + "C20,48 14,50 8,51 C0,52 -8,51 -15,48 "
                + "C-22,44 -26,37 -28,29 C-30,20 -28,12 -24,4 "
                + "C-20,-3 -15,-10 -12,-16 C-10,-21 -10,-25 -9,-29 Z");
            context.DrawGeometry(vm.LiquidBrush, null, liquid);
            context.DrawGeometry(null, new Pen(Brush(0x99F1DFB5), 1.1),
                StreamGeometry.Parse($"M-14,{leftTop + 2:0.##} C-6,{leftTop + wave + 1:0.##} 7,{rightTop - wave + 1:0.##} 14,{rightTop + 2:0.##}"));

            for (var index = 0; index < 3; index++)
            {
                var bubblePhase = (completionPresentationTime * (1.2 + (index * 0.35))) + (index * 2.1);
                var bubbleX = -8 + (Math.Sin(bubblePhase) * (6 + (index * 2)));
                var bubbleY = 31 - ((bubblePhase % 1.0) * 46);
                var bubbleSize = 0.9 + (index * 0.3);
                context.DrawEllipse(Brush(0x66F1DFB5), null,
                    new Rect(bubbleX - bubbleSize, bubbleY - bubbleSize, bubbleSize * 2, bubbleSize * 2));
            }

        }

        sprites.Draw(context, "image2_bottle_empty",
            new Rect(center.X - (31 * scale), center.Y - (57 * scale), 62 * scale, 114 * scale),
            rotation,
            1);
    }

    private void DrawQuantityPickerLegacy(DrawingContext context)
    {
        if (DataContext is not AlchemyWorkshopViewModel vm)
        {
            return;
        }

        switch (quantityPickerMode)
        {
            case QuantityPickerMode.Ingredient when quantityPickerIngredient is not null:
            {
                var bounds = IngredientPickerBounds(quantityPickerIngredient.Index);
                context.DrawRectangle(Brush(0xF52B1D12), new Pen(Brass, 1.5), new RoundedRect(bounds, 4));
                DrawText(context, "Quantity", new Point(bounds.X + 7, bounds.Y + 4), 10, PaleInk, UiTypeface, bounds.Width - 14, TextAlignment.Center);
                for (var index = 0; index < 3; index++)
                {
                    var rect = IngredientPickerOptionRect(quantityPickerIngredient.Index, index);
                    var selected = vm.SelectedCount == index + 1;
                    context.DrawEllipse(selected ? Brass : Brush(0xFF594027), new Pen(Brush(0xFFD4B06E), 1), rect);
                    DrawText(context, (index + 1).ToString(CultureInfo.InvariantCulture), new Point(rect.X, rect.Y + 5), 11, selected ? Ink : PaleInk, UiTypeface, rect.Width, TextAlignment.Center);
                }

                break;
            }
            case QuantityPickerMode.Hourglass:
            {
                var bounds = HourglassPickerBounds;
                context.DrawRectangle(Brush(0xF52B1D12), new Pen(Brass, 1.5), new RoundedRect(bounds, 4));
                DrawText(context, "Choose turns", new Point(bounds.X + 8, bounds.Y + 4), 10, PaleInk, UiTypeface, bounds.Width - 16, TextAlignment.Center);
                for (var index = 0; index < 3; index++)
                {
                    var rect = HourglassPickerOptionRect(index);
                    var selected = vm.SelectedTurns == index + 1;
                    context.DrawEllipse(selected ? Brass : Brush(0xFF594027), new Pen(Brush(0xFFD4B06E), 1), rect);
                    DrawText(context, (index + 1).ToString(CultureInfo.InvariantCulture), new Point(rect.X, rect.Y + 5), 11, selected ? Ink : PaleInk, UiTypeface, rect.Width, TextAlignment.Center);
                }

                break;
            }
        }
    }

    private void DrawBottomRail(DrawingContext context, AlchemyWorkshopViewModel? vm)
    {
        context.DrawRectangle(Brush(0xFF17100B), new Pen(Brush(0xFF765034), 2), new Rect(0, 742, SceneWidth, 78));
        DrawText(context, "Temperature", new Point(24, 766), 13, PaleInk, UiTypeface);
        var rail = new Rect(92, 770, 786, 18);
        context.DrawRectangle(Brush(0xFF27393F), null, new Rect(rail.X, rail.Y, rail.Width * 0.28, rail.Height));
        context.DrawRectangle(Brush(0xFFD5A84D), null, new Rect(rail.X + (rail.Width * 0.28), rail.Y, rail.Width * 0.28, rail.Height));
        context.DrawRectangle(Brush(0xFFE36D32), null, new Rect(rail.X + (rail.Width * 0.56), rail.Y, rail.Width * 0.28, rail.Height));
        context.DrawRectangle(Brush(0xFFB9332D), null, new Rect(rail.X + (rail.Width * 0.84), rail.Y, rail.Width * 0.16, rail.Height));
        context.DrawRectangle(null, new Pen(Brush(0xFFB69A72), 2), new RoundedRect(rail, 3));
        var temp = Math.Clamp(vm?.Temperature ?? 0, 0, 100);
        var markerX = rail.X + ((temp / 100) * rail.Width);
        context.DrawGeometry(PaleInk, null, StreamGeometry.Parse($"M{markerX - 7:0.##},765 L{markerX + 7:0.##},765 L{markerX:0.##},755 Z"));
        DrawText(context, "Cold", new Point(98, 794), 10, MutedInk, UiTypeface);
        DrawText(context, "Simmer", new Point(352, 794), 10, MutedInk, UiTypeface);
        DrawText(context, "Boiling", new Point(576, 794), 10, MutedInk, UiTypeface);
        DrawText(context, "Overheated", new Point(812, 794), 10, Brush(0xFFD86C58), UiTypeface);
        DrawText(context, vm?.TemperatureText ?? "0°", new Point(900, 756), 23, vm?.HeatBrush ?? PaleInk, DisplayTypeface);
        DrawText(context, vm?.HeatBandText ?? "Cold", new Point(970, 768), 12, MutedInk, UiTypeface);
        DrawText(context, vm?.YieldText ?? "Yield --", new Point(1050, 767), 13, PaleInk, UiTypeface);
        DrawText(context, "Select a quantity from a herb or the hourglass before picking it up", new Point(1174, 770), 10, MutedInk, UiTypeface, 245, TextAlignment.Center);
    }

    private void DrawResetRagModern(DrawingContext context)
    {
        var vm = DataContext as AlchemyWorkshopViewModel;
        var target = new AlchemyHudTarget(AlchemyHudTargetKind.Reset);
        var state = vm is null ? AlchemyInteractionState.Default : InteractionStateFor(vm, target);
        var interaction = DrawInteractivePlaque(context, ResetRagHitbox, state);
        context.DrawRectangle(null, new Pen(Brush(interaction.Border), 0.8),
            new RoundedRect(new Rect(ResetRagHitbox.X + 3, ResetRagHitbox.Y + 3 + interaction.ContentOffset,
                ResetRagHitbox.Width - 6, ResetRagHitbox.Height - 6), 2));
        DrawText(context, "↻", new Point(ResetRagHitbox.X, ResetRagHitbox.Y + 7 + interaction.ContentOffset), 18,
            Brush(interaction.Text), UiTypeface,
            ResetRagHitbox.Width, TextAlignment.Center);
        if (state == AlchemyInteractionState.Hover)
        {
            DrawText(context, Ui(vm, AlchemyTextKey.Reset), new Point(ResetRagHitbox.X - 18, ResetRagHitbox.Bottom + 5), 10,
                Brush(AlchemyHudTheme.TextPrimary), UiTypeface, 84, TextAlignment.Center);
        }
    }

    private static void DrawResetRagLegacy(DrawingContext context)
    {
        context.DrawGeometry(Brush(0xFF8E806B), new Pen(Brush(0xFFC0AC88), 1), StreamGeometry.Parse("M1374,24 C1386,10 1410,19 1417,31 C1407,39 1406,55 1387,54 C1381,44 1369,39 1374,24 Z"));
        DrawText(context, "Clear", new Point(1388, 26), 12, Ink, DisplayTypeface);
    }

    private void DrawDragLayer(DrawingContext context)
    {
        switch (dragMode)
        {
            case DragMode.Ingredient when draggedIngredient is not null:
                DrawHerbBundle(context, draggedIngredient.Id, dragVisual, draggedIngredientCount, 1.18, 1);
                DrawTargetHalo(context, MortarDropHitbox, MortarDropHitbox.Contains(dragTarget));
                DrawTargetHalo(context, CauldronMouth, CauldronMouth.Contains(dragTarget));
                break;
            case DragMode.BaseBottle when draggedBase is not null:
                var openAmount = bottleCapPhase == BottleCapPhase.Open
                    ? 1
                    : SmoothStep(0.05, 0.72, bottleCapProgress);
                DrawBaseBottle(
                    context,
                    draggedBase,
                    dragVisual,
                    pourTilt,
                    PourVisualPhysics.BottleScaleWhileDragging,
                    openAmount);
                DrawDetachedBottleCork(context, draggedBase, dragVisual, pourTilt, openAmount);
                DrawTargetHalo(context, CauldronMouth, CauldronHitbox.Contains(dragTarget));
                if (bottleCapPhase == BottleCapPhase.Opening)
                {
                    var vm = DataContext as AlchemyWorkshopViewModel;
                    DrawText(context, Ui(vm, AlchemyTextKey.Uncork), new Point(dragVisual.X - 48, dragVisual.Y + 62), 11, PaleInk, UiTypeface, 120, TextAlignment.Center);
                }
                else if (pourFlowRate > 0.01 || pourProgress > 0)
                {
                    var vm = DataContext as AlchemyWorkshopViewModel;
                    DrawText(context, $"{Ui(vm, AlchemyTextKey.Pouring)} {pourProgress:P0}", new Point(dragVisual.X - 48, dragVisual.Y + 62), 11, PaleInk, UiTypeface, 120, TextAlignment.Center);
                }

                break;
            case DragMode.GroundPowder when draggedPowderIngredientId is not null:
                DrawPowderClump(context, draggedPowderIngredientId, dragVisual, 1.04, 1);
                DrawTargetHalo(context, CauldronMouth, CauldronMouth.Contains(dragTarget));
                break;
            case DragMode.ProductBottle:
            {
                var vm = DataContext as AlchemyWorkshopViewModel;
                var target = vm is null
                    ? new BottleCollectionTarget(BottleCollectionSource.Condenser, DistillerReceiverHitbox, DistillerReceiverHitbox.Center)
                    : BottleCollectionVisual.Resolve(vm.Snapshot.Recipe, CurrentPotOffset());
                DrawTargetHalo(context, target.Hitbox, target.Hitbox.Contains(dragTarget));
                break;
            }
        }

        if (powderReturning && returningPowderIngredientId is not null)
        {
            DrawPowderClump(context, returningPowderIngredientId, powderReturnPosition, 1.02, 1);
        }
    }

    private void DrawRecipeBook(DrawingContext context)
    {
        if (DataContext is not AlchemyWorkshopViewModel { IsRecipeBookOpen: true } vm)
        {
            return;
        }

        context.DrawRectangle(Brush(0xB0000000), null, new Rect(0, 72, SceneWidth, 670));
        context.DrawRectangle(Parchment, new Pen(Brush(0xFF5D3C24), 5), new RoundedRect(new Rect(287, 84, 596, 626), 8));
        context.DrawLine(new Pen(Brush(0xFF9A774E), 3), new Point(585, 91), new Point(585, 700));
        DrawText(context, vm.SelectedRecipe.Name, new Point(333, 122), 30, Ink, DisplayTypeface, 500, TextAlignment.Center);
        DrawText(context, vm.SelectedRecipe.Effect, new Point(342, 166), 13, Brush(0xFF6F5034), UiTypeface, 485, TextAlignment.Center);

        for (var index = 0; index < vm.SelectedRecipe.Steps.Count; index++)
        {
            var step = vm.SelectedRecipe.Steps[index];
            var leftPage = index < 4;
            var localIndex = leftPage ? index : index - 4;
            var x = leftPage ? 326 : 612;
            var y = 222 + (localIndex * 94);
            var completed = index < vm.RecipeRows.Count && vm.RecipeRows[index].StateText == "Complete";
            var current = index < vm.RecipeRows.Count && vm.RecipeRows[index].StateText == "Current";
            context.DrawEllipse(completed ? HerbGreen : current ? Brass : Brush(0xFF9B805C), null, new Rect(x, y, 28, 28));
            DrawText(context, (index + 1).ToString(CultureInfo.InvariantCulture), new Point(x, y + 5), 11, completed || current ? PaleInk : Ink, UiTypeface, 28, TextAlignment.Center);
            DrawText(context, step.Label, new Point(x + 40, y + 2), 13, current ? Brush(0xFF7E3E22) : Ink, UiTypeface, 205);
            DrawText(context, vm.StepHint(step), new Point(x + 40, y + 30), 10, Brush(0xFF7C6346), UiTypeface, 205);
        }

        context.DrawRectangle(Brush(0xFFB08A54), new Pen(Brush(0xFF654126), 2), new RoundedRect(new Rect(334, 650, 74, 44), 3));
        context.DrawRectangle(Brush(0xFFB08A54), new Pen(Brush(0xFF654126), 2), new RoundedRect(new Rect(782, 650, 74, 44), 3));
        DrawText(context, "Previous", new Point(342, 662), 12, Ink, UiTypeface, 58, TextAlignment.Center);
        DrawText(context, "Next", new Point(790, 662), 12, Ink, UiTypeface, 58, TextAlignment.Center);
    }

    private void DrawHerb(DrawingContext context, string id, Point center, double scale, double opacity)
    {
        var rect = new Rect(center.X - (46 * scale), center.Y - (70 * scale), 92 * scale, 140 * scale);
        if (sprites.Draw(context, $"potioncraft_herb_{id}_raw", rect, 0, opacity))
        {
            return;
        }

        if (sprites.Draw(context, $"painted_herb_{id}_raw", rect, 0, opacity))
        {
            return;
        }

        DrawBotanicalGlyph(context, id, center, scale, opacity);
    }

    private void DrawHerbBundle(DrawingContext context, string id, Point center, int count, double scale, double opacity)
    {
        count = Math.Clamp(count, 1, 3);
        for (var index = count - 1; index >= 0; index--)
        {
            var offset = index switch
            {
                1 => new Vector(20, 8),
                2 => new Vector(-18, 15),
                _ => default
            };
            DrawHerb(context, id, center + offset, scale * (index == 0 ? 1 : 0.9), opacity * (index == 0 ? 1 : 0.92));
        }

        if (count > 1)
        {
            var badge = new Rect(center.X + 35, center.Y - 58, 24, 21);
            context.DrawEllipse(Brass, new Pen(Ink, 1), badge);
            DrawText(context, $"×{count}", new Point(badge.X, badge.Y + 3), 10, Ink, UiTypeface, badge.Width, TextAlignment.Center);
        }
    }

    private void DrawCrushedHerb(DrawingContext context, string id, Point center, double scale, double opacity)
    {
        var rect = new Rect(center.X - (55 * scale), center.Y - (34 * scale), 110 * scale, 68 * scale);
        if (sprites.Draw(context, $"potioncraft_herb_{id}_crushed", rect, 0, opacity))
        {
            return;
        }

        if (sprites.Draw(context, $"painted_herb_{id}_crushed", rect, 0, opacity))
        {
            return;
        }

        DrawGroundDots(context, id, center, 1, opacity, scale);
    }

    /// <summary>
    /// Keeps the ingredient readable as matter throughout the entire gesture: a bundled plant
    /// collapses into torn leaf pieces, then becomes a compressed bed of coloured powder.
    /// </summary>
    private void DrawMortarGrindingContents(DrawingContext context, string ingredientId, int ingredientCount, double progress)
    {
        progress = Math.Clamp(progress, 0, 1);
        var physicalMotionX = mortarHerbOffset.X + (Math.Sin(mortarGrindingPhase) * mortarGrindingEnergy * 4.8);
        var physicalMotionY = mortarHerbOffset.Y + (Math.Cos(mortarGrindingPhase * 1.31) * mortarGrindingEnergy * 2.2);
        var physicalRotation = mortarHerbRotation * (1 - (progress * 0.68));
        var physicalCompression = mortarHerbCompression * (1 - (progress * 0.55));
        var wholeOpacity = 1 - SmoothStep(0.10, 0.22, progress);
        var slicedOpacity = SmoothStep(0.08, 0.18, progress) * (1 - SmoothStep(0.58, 0.78, progress));
        var fragmentAmount = SmoothStep(0.18, 0.52, progress) * (1 - (SmoothStep(0.76, 0.98, progress) * 0.88));
        var crushedOpacity = SmoothStep(0.38, 0.62, progress);
        var powderAmount = SmoothStep(0.56, 0.99, progress);
        var pickupFade = SmoothStep(0.94, 1, progress) * powderPickup;
        var rawScale = 0.78 - (Math.Min(progress, 0.42) * 0.31);
        var rawCenter = new Point(528, 466 + (Math.Min(progress, 0.58) * 31));

        using (context.PushClip(new Rect(430, 420, 196, 104)))
        using (context.PushTransform(Matrix.CreateTranslation(physicalMotionX, physicalMotionY)))
        using (context.PushTransform(Matrix.CreateTranslation(528, 493)))
        using (context.PushTransform(Matrix.CreateRotation(physicalRotation * Math.PI / 180)))
        using (context.PushTransform(Matrix.CreateScale(1 + physicalCompression, 1 - physicalCompression)))
        using (context.PushTransform(Matrix.CreateTranslation(-528, -493)))
        {
            if (powderAmount > 0.001)
            {
                using (context.PushOpacity(1 - pickupFade))
                {
                    DrawMortarPowderBed(context, ingredientId, powderAmount);
                }
            }

            if (crushedOpacity > 0.001)
            {
                using (context.PushOpacity(1 - pickupFade))
                using (context.PushTransform(Matrix.CreateTranslation(528, 498)))
                using (context.PushTransform(Matrix.CreateScale(1, 1 - (SmoothStep(0.64, 1, progress) * 0.28))))
                using (context.PushTransform(Matrix.CreateTranslation(-528, -498)))
                {
                    DrawCrushedHerb(context, ingredientId, new Point(528, 498), 1.42 - (progress * 0.12), crushedOpacity);
                }
            }

            if (wholeOpacity > 0.001)
            {
                var flatten = (1 - (Math.Min(progress, 0.30) * 0.92)) * (1 - (mortarGrindingEnergy * 0.16));
                using (context.PushTransform(Matrix.CreateTranslation(rawCenter.X, rawCenter.Y)))
                using (context.PushTransform(Matrix.CreateScale(1 + (progress * 0.10) + (mortarGrindingEnergy * 0.08), flatten)))
                using (context.PushTransform(Matrix.CreateTranslation(-rawCenter.X, -rawCenter.Y)))
                {
                    DrawSeparateMortarHerbs(context, ingredientId, ingredientCount, rawCenter, rawScale, wholeOpacity, flatten);
                }
            }

            if (slicedOpacity > 0.001)
            {
                DrawSlicedHerb(context, ingredientId, rawCenter, rawScale, slicedOpacity, progress);
            }

            if (fragmentAmount > 0.001)
            {
                DrawMortarLeafFragments(context, ingredientId, fragmentAmount, progress, mortarGrindingPhase, mortarGrindingEnergy);
            }

            if (powderAmount > 0.68)
            {
                using (context.PushOpacity(1 - pickupFade))
                {
                    DrawMortarPowderGrains(context, ingredientId, powderAmount);
                    DrawMortarPowderGrooves(context, ingredientId, (powderAmount - 0.68) / 0.32);
                }
            }
        }
    }

    private void DrawMortarRestingHerbs(DrawingContext context, string ingredientId, int ingredientCount)
    {
        if (ingredientCount <= 0)
        {
            return;
        }

        using (context.PushClip(new Rect(430, 420, 196, 104)))
        {
            DrawSeparateMortarHerbs(context, ingredientId, ingredientCount, new Point(528, 489), 0.62, 1, 0.88);
        }
    }

    private void DrawSeparateMortarHerbs(
        DrawingContext context,
        string ingredientId,
        int ingredientCount,
        Point center,
        double scale,
        double opacity,
        double verticalCompression)
    {
        ingredientCount = Math.Clamp(ingredientCount, 1, 3);
        var perHerbScale = scale * (ingredientCount switch
        {
            1 => 1,
            2 => 0.67,
            _ => 0.54
        });

        for (var index = 0; index < ingredientCount; index++)
        {
            var offset = ingredientCount switch
            {
                2 => index == 0 ? new Vector(-39, 4) : new Vector(39, -3),
                3 => index switch
                {
                    0 => new Vector(-52, 7),
                    1 => new Vector(0, -10),
                    _ => new Vector(52, 6)
                },
                _ => default
            };
            var herbCenter = center + offset;
            using (context.PushTransform(Matrix.CreateTranslation(herbCenter.X, herbCenter.Y)))
            using (context.PushTransform(Matrix.CreateScale(1, verticalCompression)))
            using (context.PushTransform(Matrix.CreateTranslation(-herbCenter.X, -herbCenter.Y)))
            {
                DrawHerb(context, ingredientId, herbCenter, perHerbScale, opacity);
            }
        }
    }

    private void DrawSlicedHerb(
        DrawingContext context,
        string ingredientId,
        Point center,
        double scale,
        double opacity,
        double progress)
    {
        const int columns = 3;
        const int rows = 5;
        var baseRect = new Rect(center.X - (46 * scale), center.Y - (70 * scale), 92 * scale, 140 * scale);
        var key = $"potioncraft_herb_{ingredientId}_raw";
        if (!sprites.GetAvailable(key))
        {
            key = $"painted_herb_{ingredientId}_raw";
        }

        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                var index = (row * columns) + column;
                var noise = StableNoise(ingredientId, index, 17);
                var breakStart = 0.13 + (noise * 0.15) + (row * 0.012);
                var tear = SmoothStep(breakStart, breakStart + 0.28, progress);
                var source = new Rect(
                    column / (double)columns,
                    row / (double)rows,
                    1.0 / columns,
                    1.0 / rows);
                var cellWidth = baseRect.Width / columns;
                var cellHeight = baseRect.Height / rows;
                var cellCenter = new Point(
                    baseRect.X + ((column + 0.5) * cellWidth),
                    baseRect.Y + ((row + 0.5) * cellHeight));
                var horizontalDirection = column - ((columns - 1) / 2.0);
                var offset = new Vector(
                    (horizontalDirection * (6 + (noise * 8)) * tear) + (Math.Sin((mortarGrindingPhase * 0.42) + index) * mortarGrindingEnergy * 2.5),
                    ((5 + (row * 3.1) + (noise * 5)) * tear) + (Math.Cos((mortarGrindingPhase * 0.37) + index) * mortarGrindingEnergy * 1.5));
                var shrink = 1 - (tear * 0.24);
                var destination = new Rect(
                    cellCenter.X + offset.X - ((cellWidth * shrink) / 2) - 0.5,
                    cellCenter.Y + offset.Y - ((cellHeight * shrink) / 2) - 0.5,
                    (cellWidth * shrink) + 1,
                    (cellHeight * shrink) + 1);
                var rotation = ((noise - 0.5) * 30 + (horizontalDirection * 7)) * tear;
                sprites.DrawRegion(context, key, source, destination, rotation, opacity);
            }
        }
    }

    private static void DrawMortarLeafFragments(
        DrawingContext context,
        string ingredientId,
        double amount,
        double progress,
        double motionPhase,
        double grindingEnergy)
    {
        var color = IngredientColor(ingredientId);
        var dark = MixColor(color, BrushColor(0xFF292017), 0.44);
        var light = MixColor(color, BrushColor(0xFFD1B36F), 0.18);
        const int count = 30;
        using (context.PushOpacity(Math.Clamp(amount, 0, 1)))
        {
            for (var index = 0; index < count; index++)
            {
                var noise = StableNoise(ingredientId, index, 43);
                var appearAt = 0.16 + (noise * 0.20);
                var disappearAt = 0.66 + (noise * 0.18);
                var fragmentOpacity = SmoothStep(appearAt, appearAt + 0.12, progress)
                    * (1 - SmoothStep(disappearAt, disappearAt + 0.18, progress));
                if (fragmentOpacity <= 0.001)
                {
                    continue;
                }

                var phase = (index * 2.399) + (progress * 1.83) + (motionPhase * 0.16);
                var radius = 12 + ((index % 8) * 7.1) + (progress * 13);
                var x = 528 + (Math.Cos(phase) * radius) + (Math.Cos(motionPhase + index) * grindingEnergy * 5.2);
                var y = 493 + (Math.Sin(phase * 1.37) * radius * 0.26) + (Math.Sin((motionPhase * 1.2) + index) * grindingEnergy * 2.4);
                var fragmentScale = 1 - (SmoothStep(0.48, 0.92, progress) * 0.42);
                var width = (8.5 + ((index % 3) * 2.9)) * fragmentScale;
                var height = (4.2 + ((index % 2) * 1.8)) * fragmentScale;
                var angle = (phase * 180 / Math.PI) + (index * 19);

                using (context.PushOpacity(fragmentOpacity))
                using (context.PushTransform(Matrix.CreateTranslation(x, y)))
                using (context.PushTransform(Matrix.CreateRotation(angle * Math.PI / 180)))
                {
                    context.DrawEllipse(Brush(index % 4 == 0 ? light : color, 0.92), new Pen(Brush(dark, 0.82), 0.75), new Rect(-width / 2, -height / 2, width, height));
                    context.DrawLine(new Pen(Brush(dark, 0.58), 0.55), new Point((-width / 2) + 1, 0), new Point((width / 2) - 1, 0));
                }
            }
        }
    }

    private void DrawPowderClump(
        DrawingContext context,
        string ingredientId,
        Point center,
        double scale,
        double opacity)
    {
        using (context.PushOpacity(Math.Clamp(opacity, 0, 1)))
        using (context.PushTransform(Matrix.CreateTranslation(center.X, center.Y)))
        using (context.PushTransform(Matrix.CreateScale(scale, scale)))
        using (context.PushTransform(Matrix.CreateTranslation(-528, -502)))
        {
            DrawMortarPowderBed(context, ingredientId, 1);
            using (context.PushTransform(Matrix.CreateTranslation(528, 499)))
            using (context.PushTransform(Matrix.CreateScale(1, 0.72)))
            using (context.PushTransform(Matrix.CreateTranslation(-528, -499)))
            {
                DrawCrushedHerb(context, ingredientId, new Point(528, 499), 1.38, 1);
            }

            DrawMortarPowderGrains(context, ingredientId, 1);
            DrawMortarPowderGrooves(context, ingredientId, 0.72);
        }
    }

    private static double StableNoise(string value, int index, int salt)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (var character in value)
            {
                hash = (hash ^ character) * 16777619;
            }

            hash = (hash ^ (uint)(index * 374761393)) * 16777619;
            hash = (hash ^ (uint)salt) * 16777619;
            return (hash & 0xFFFF) / 65535.0;
        }
    }

    private static void DrawMortarPowderBed(DrawingContext context, string ingredientId, double amount)
    {
        var color = IngredientColor(ingredientId);
        var shadow = MixColor(color, BrushColor(0xFF211811), 0.50);
        var mound = StreamGeometry.Parse("M445,504 C456,487 484,481 528,482 C572,481 601,487 612,504 C600,515 571,522 528,523 C484,522 456,515 445,504 Z");

        using (context.PushOpacity(amount))
        {
            context.DrawGeometry(Brush(shadow, 0.18), null, StreamGeometry.Parse("M439,507 C452,496 485,493 528,494 C572,493 604,496 617,507 C603,518 570,524 528,525 C485,524 452,518 439,507 Z"));
            context.DrawGeometry(null, new Pen(Brush(shadow, 0.34), 0.9), mound);
        }
    }

    private static void DrawMortarPowderGrains(DrawingContext context, string ingredientId, double amount)
    {
        var color = IngredientColor(ingredientId);
        var shadow = MixColor(color, BrushColor(0xFF211811), 0.50);
        var body = MixColor(color, BrushColor(0xFF756039), 0.26);
        var highlight = MixColor(color, BrushColor(0xFFE6CF91), 0.24);
        var grainCount = Math.Clamp((int)Math.Round(14 + (amount * 42)), 14, 56);
        using (context.PushOpacity(Math.Clamp(amount, 0, 1)))
        {
            for (var index = 0; index < grainCount; index++)
            {
                var phase = index * 2.399963;
                var radial = 8 + ((index % 11) * 5.45);
                var x = 528 + (Math.Cos(phase) * radial);
                var y = 502 + (Math.Sin(phase) * radial * 0.22);
                var size = 0.85 + ((index % 4) * 0.42);
                var grain = index % 5 == 0 ? highlight : index % 3 == 0 ? shadow : body;
                context.DrawEllipse(Brush(grain, 0.82), null, new Rect(x - size, y - (size * 0.66), size * 2, size * 1.32));
            }
        }
    }

    private static void DrawMortarPowderGrooves(DrawingContext context, string ingredientId, double amount)
    {
        var groove = MixColor(IngredientColor(ingredientId), BrushColor(0xFF1E1710), 0.62);
        using (context.PushOpacity(Math.Clamp(amount, 0, 1)))
        {
            context.DrawGeometry(null, new Pen(Brush(groove, 0.62), 1.2), StreamGeometry.Parse("M476,502 C494,497 516,506 534,501 C551,497 569,505 582,501"));
            context.DrawGeometry(null, new Pen(Brush(groove, 0.38), 0.8), StreamGeometry.Parse("M486,508 C503,504 519,511 538,507 C553,504 566,510 575,507"));
        }
    }

    private void DrawPestle(DrawingContext context, Point center, double rotation)
    {
        var rect = new Rect(center.X - 42, center.Y - 109, 84, 218);
        if (sprites.Draw(context, "image2_pestle", rect, rotation, 1))
        {
            return;
        }

        using (context.PushTransform(Matrix.CreateTranslation(center.X, center.Y)))
        using (context.PushTransform(Matrix.CreateRotation(rotation * Math.PI / 180)))
        {
            context.DrawRectangle(Brush(0xFF896847), new Pen(Brush(0xFFC3A476), 3), new RoundedRect(new Rect(-15, -94, 30, 184), 14));
            context.DrawEllipse(Brush(0xFF69513A), new Pen(Brush(0xFFAF8C62), 2), new Rect(-24, 65, 48, 37));
        }
    }

    private static void DrawBotanicalGlyph(DrawingContext context, string id, Point center, double scale, double opacity)
    {
        var color = Brush(IngredientColor(id), opacity);
        var outline = new Pen(Brush(0xFF33281C), 2 * scale);
        using (context.PushOpacity(opacity))
        {
            context.DrawLine(new Pen(Brush(0xFF4D4B31), 3.5 * scale), center + new Vector(0, 42 * scale), center + new Vector(0, -38 * scale));
            var leaves = new[]
            {
                new Rect(center.X - (39 * scale), center.Y - (15 * scale), 42 * scale, 22 * scale),
                new Rect(center.X - (4 * scale), center.Y - (2 * scale), 42 * scale, 22 * scale),
                new Rect(center.X - (34 * scale), center.Y + (12 * scale), 36 * scale, 20 * scale),
                new Rect(center.X - (2 * scale), center.Y + (22 * scale), 34 * scale, 18 * scale)
            };
            foreach (var leaf in leaves)
            {
                context.DrawEllipse(color, outline, leaf);
                context.DrawLine(new Pen(Brush(0xFF4A422D), scale), leaf.Center, new Point(leaf.Right - (5 * scale), leaf.Center.Y));
            }

            if (id == "belladonna")
            {
                context.DrawEllipse(Brush(0xFF5D465B), outline, new Rect(center.X - (18 * scale), center.Y - (40 * scale), 17 * scale, 17 * scale));
                context.DrawEllipse(Brush(0xFF5D465B), outline, new Rect(center.X + (4 * scale), center.Y - (31 * scale), 17 * scale, 17 * scale));
            }
            else
            {
                context.DrawEllipse(Brush(IngredientColor(id), 0.92 * opacity), outline, new Rect(center.X - (20 * scale), center.Y - (49 * scale), 40 * scale, 32 * scale));
                context.DrawLine(new Pen(Ink, scale), new Point(center.X - (13 * scale), center.Y - (32 * scale)), new Point(center.X + (13 * scale), center.Y - (32 * scale)));
            }
        }
    }

    private static void DrawGroundDots(DrawingContext context, string id, Point center, double progress, double opacity, double scale = 1)
    {
        var count = Math.Clamp((int)Math.Round(6 + (progress * 26)), 0, 32);
        var color = Brush(IngredientColor(id), opacity);
        using (context.PushOpacity(opacity))
        {
            for (var index = 0; index < count; index++)
            {
                var angle = index * 2.399;
                var radius = (5 + ((index % 7) * 5.2)) * scale * (0.55 + (progress * 0.45));
                var x = center.X + (Math.Cos(angle) * radius);
                var y = center.Y + (Math.Sin(angle) * radius * 0.35);
                var size = (1.8 + ((index % 3) * 0.9)) * scale;
                context.DrawEllipse(color, new Pen(Ink, 0.7), new Rect(x - size, y - size, size * 2, size * 2));
            }
        }
    }

    private void DrawCauldronContents(DrawingContext context, AlchemyWorkshopViewModel? vm, double potOffset)
    {
        if (vm is null)
        {
            return;
        }

        var contents = vm.Snapshot.Cauldron;
        var level = Math.Clamp(vm.LiquidOpacity, 0.12, 0.78);
        var surfaceWidth = 104 + (level * 196);
        var surfaceHeight = 16 + (level * 46);
        var surfaceCenter = new Point(820, 401 - (level * 55) + (surfaceHeight / 2) + potOffset);
        var heatMotion = 0.42 + (Math.Clamp((vm.Temperature - 18) / 66, 0, 1) * 0.76);
        var circulationTime = liquidRippleTime * 0.22 * heatMotion;
        for (var ingredientIndex = 0; ingredientIndex < contents.Count; ingredientIndex++)
        {
            var item = contents[ingredientIndex];
            var count = item.Form == IngredientForm.Ground
                ? Math.Clamp(item.Count * 3, 1, 8)
                : Math.Clamp(item.Count, 1, 4);
            var ingredientColor = IngredientColor(item.IngredientId);
            var settledColor = MixColor(ingredientColor, BrushColor(0xFF241B13), 0.24);
            for (var index = 0; index < count; index++)
            {
                var phase = (ingredientIndex * 1.7) + (index * 2.39);
                var direction = ((ingredientIndex + index) & 1) == 0 ? 1d : -0.72;
                var orbit = phase + (circulationTime * direction);
                var orbitWidth = 0.22 + (((ingredientIndex + index) % 3) * 0.055);
                var orbitHeight = 0.12 + (((ingredientIndex + (index * 2)) % 3) * 0.035);
                var x = surfaceCenter.X + (Math.Cos(orbit) * surfaceWidth * orbitWidth);
                var y = surfaceCenter.Y + (Math.Sin(orbit) * surfaceHeight * orbitHeight);
                if (item.Form == IngredientForm.Ground)
                {
                    var size = 1.15 + (((index + ingredientIndex) % 3) * 0.38);
                    context.DrawEllipse(Brush(settledColor, 0.64), null,
                        new Rect(x - size, y - (size * 0.55), size * 2, size * 1.1));
                    context.DrawEllipse(Brush(MixColor(settledColor, BrushColor(0xFFE0C789), 0.12), 0.42), null,
                        new Rect(x + 2.4, y - 1.2, size * 0.9, size * 0.65));
                }
                else
                {
                    var tumble = (phase * 1.31) + (circulationTime * direction * (2.15 + ((index % 3) * 0.34)));
                    var rotation = orbit + (direction > 0 ? Math.PI / 2 : -Math.PI / 2) + (Math.Sin(tumble) * 0.36);
                    DrawCauldronLeaf(
                        context,
                        new Point(x, y),
                        rotation,
                        tumble,
                        0.62 + (((index + ingredientIndex) % 3) * 0.11),
                        settledColor);
                }
            }
        }
    }

    private static void DrawCauldronLeaf(
        DrawingContext context,
        Point center,
        double rotation,
        double tumble,
        double scale,
        Color color)
    {
        var face = Math.Abs(Math.Cos(tumble));
        var faceScale = 0.24 + (face * 0.52);
        var facingUp = Math.Cos(tumble) >= 0;
        var visibleColor = facingUp ? color : MixColor(color, BrushColor(0xFF18130F), 0.2);

        using (context.PushTransform(Matrix.CreateTranslation(center.X, center.Y)))
        using (context.PushTransform(Matrix.CreateRotation(rotation)))
        using (context.PushTransform(Matrix.CreateScale(scale, scale * faceScale)))
        {
            context.DrawGeometry(
                Brush(visibleColor, 0.58 + (face * 0.16)),
                new Pen(Brush(0x99211812), 0.9),
                CauldronLeafGeometry);
            if (face > 0.18)
            {
                context.DrawLine(
                    new Pen(Brush(0xAA2B2118), 0.55 + (face * 0.15)),
                    new Point(-6.5, 0),
                    new Point(6.2, -0.6));
            }
        }
    }

    private void DrawBaseBottle(
        DrawingContext context,
        BaseSlotViewModel bottle,
        Point center,
        double rotation,
        double scale,
        double openAmount = 0)
    {
        var spriteKey = bottle.Value switch
        {
            BaseLiquid.Water => "image2_bottle_water",
            BaseLiquid.Wine => "image2_bottle_wine",
            BaseLiquid.Spirits => "image2_bottle_spirits",
            BaseLiquid.Oil => "image2_bottle_oil",
            _ => "image2_bottle_water"
        };
        var spriteRect = new Rect(center.X - (28 * scale), center.Y - (52 * scale), 56 * scale, 104 * scale);
        openAmount = Math.Clamp(openAmount, 0, 1);
        using (context.PushTransform(Matrix.CreateTranslation(center.X, center.Y)))
        using (context.PushTransform(Matrix.CreateRotation(rotation * Math.PI / 180)))
        using (context.PushTransform(Matrix.CreateTranslation(-center.X, -center.Y)))
        {
            if (sprites.GetAvailable(spriteKey))
            {
                var closedOpacity = 1 - SmoothStep(0.03, 0.38, openAmount);
                if (closedOpacity > 0.001)
                {
                    sprites.Draw(context, spriteKey, spriteRect, 0, closedOpacity);
                }

                if (openAmount > 0.001)
                {
                    DrawOpenBaseBottle(context, spriteKey, spriteRect, scale, openAmount);
                }

                return;
            }

            context.DrawRectangle(ParchmentDark, new Pen(Ink, 2), new RoundedRect(new Rect(center.X - (10 * scale), center.Y - (42 * scale), 20 * scale, 20 * scale), 2));
            context.DrawGeometry(Brush(0x55F0E0B6), new Pen(Ink, 2.4), StreamGeometry.Parse($"M{center.X - (10 * scale):0.##},{center.Y - (24 * scale):0.##} C{center.X - (29 * scale):0.##},{center.Y - (7 * scale):0.##} {center.X - (28 * scale):0.##},{center.Y + (34 * scale):0.##} {center.X - (17 * scale):0.##},{center.Y + (40 * scale):0.##} L{center.X + (17 * scale):0.##},{center.Y + (40 * scale):0.##} C{center.X + (28 * scale):0.##},{center.Y + (34 * scale):0.##} {center.X + (29 * scale):0.##},{center.Y - (7 * scale):0.##} {center.X + (10 * scale):0.##},{center.Y - (24 * scale):0.##} Z"));
            context.DrawRectangle(bottle.AccentBrush, new Pen(Ink, 1.5), new RoundedRect(new Rect(center.X - (21 * scale), center.Y + (9 * scale), 42 * scale, 27 * scale), 3));
            context.DrawLine(new Pen(Brush(0x99F5E7C2), 2), new Point(center.X - (14 * scale), center.Y + (15 * scale)), new Point(center.X - (14 * scale), center.Y + (29 * scale)));
        }
    }

    private void DrawOpenBaseBottle(
        DrawingContext context,
        string spriteKey,
        Rect spriteRect,
        double scale,
        double opacity)
    {
        using (context.PushOpacity(opacity))
        {
            var center = spriteRect.Center;
            var neck = StreamGeometry.Parse(
                $"M{center.X - (9.2 * scale):0.##},{center.Y - (34 * scale):0.##} " +
                $"L{center.X - (9.2 * scale):0.##},{center.Y - (49 * scale):0.##} " +
                $"C{center.X - (7.4 * scale):0.##},{center.Y - (53 * scale):0.##} {center.X + (7.4 * scale):0.##},{center.Y - (53 * scale):0.##} {center.X + (9.2 * scale):0.##},{center.Y - (49 * scale):0.##} " +
                $"L{center.X + (9.2 * scale):0.##},{center.Y - (34 * scale):0.##} Z");
            context.DrawGeometry(Brush(0x55E7E3D5), new Pen(Brush(0xCC2B2118), 1.45 * scale), neck);

            const double bodyStart = 0.145;
            sprites.DrawRegion(
                context,
                spriteKey,
                new Rect(0, bodyStart, 1, 1 - bodyStart),
                new Rect(
                    spriteRect.X,
                    spriteRect.Y + (spriteRect.Height * bodyStart),
                    spriteRect.Width,
                    spriteRect.Height * (1 - bodyStart)),
                0,
                1);

            var mouthRect = new Rect(
                center.X - (10.2 * scale),
                center.Y - (53.1 * scale),
                20.4 * scale,
                7.2 * scale);
            context.DrawEllipse(Brush(0xB12A211A), new Pen(Brush(0xFF261C15), 1.3 * scale), mouthRect);
            context.DrawEllipse(
                null,
                new Pen(Brush(0xCCEADFC4), 1.1 * scale),
                new Rect(mouthRect.X + (1.4 * scale), mouthRect.Y + (0.8 * scale), mouthRect.Width - (2.8 * scale), mouthRect.Height - (1.6 * scale)));
        }
    }

    private void DrawDetachedBottleCork(
        DrawingContext context,
        BaseSlotViewModel bottle,
        Point bottleCenter,
        double bottleRotation,
        double openAmount)
    {
        if (openAmount <= 0.001 || openAmount >= 0.995)
        {
            return;
        }

        var spriteKey = bottle.Value switch
        {
            BaseLiquid.Water => "image2_bottle_water",
            BaseLiquid.Wine => "image2_bottle_wine",
            BaseLiquid.Spirits => "image2_bottle_spirits",
            BaseLiquid.Oil => "image2_bottle_oil",
            _ => "image2_bottle_water"
        };
        var eased = openAmount * openAmount * (3 - (2 * openAmount));
        var scale = PourVisualPhysics.BottleScaleWhileDragging;
        var mouth = PourVisualPhysics.GetBottleMouth(bottleCenter, bottleRotation, scale);
        var outward = PourVisualPhysics.GetBottleOutward(bottleRotation);
        var sideways = new Vector(-outward.Y, outward.X);
        var corkCenter = mouth
            + (outward * (4 + (22 * eased)))
            + (sideways * (13 * eased))
            + new Vector(0, 18 * eased * eased);
        var corkOpacity = 1 - SmoothStep(0.72, 1, openAmount);
        var destination = new Rect(
            corkCenter.X - (15.5 * scale),
            corkCenter.Y - (7.2 * scale),
            31 * scale,
            14.4 * scale);
        sprites.DrawRegion(
            context,
            spriteKey,
            new Rect(0.22, 0, 0.56, 0.14),
            destination,
            bottleRotation + (eased * 52),
            corkOpacity);
    }

    private void DrawDirectBottleCollection(
        DrawingContext context,
        AlchemyWorkshopViewModel vm,
        double potOffset)
    {
        var progress = Math.Clamp(bottleCollectionProgress, 0, 1);
        var surfaceY = GetCauldronSurfaceBaselineY(cauldronLiquidLevel, potOffset);
        var settle = SmoothStep(0, 0.28, progress);
        var rippleWidth = 42 + (progress * 68);
        var rippleOpacity = (1 - (progress * 0.52)) * settle;
        context.DrawEllipse(
            null,
            new Pen(Brush(BrushColor(0xB9EEDFB7), rippleOpacity), 1.3),
            new Rect(
                bottleCollectionPosition.X - (rippleWidth / 2),
                surfaceY - 7,
                rippleWidth,
                14));
        context.DrawEllipse(
            null,
            new Pen(Brush(BrushColor(0x7CC6D8C0), rippleOpacity * 0.72), 0.9),
            new Rect(
                bottleCollectionPosition.X - (rippleWidth * 0.34),
                surfaceY - 4.5,
                rippleWidth * 0.68,
                9));

        var bob = Math.Sin(progress * Math.PI * 4.2) * (1 - progress) * 3.2;
        var rotation = Math.Sin(progress * Math.PI * 2.4) * (1 - progress) * 3.8;
        DrawProductBottle(
            context,
            bottleCollectionPosition + new Vector(0, bob),
            SmoothStep(0.08, 0.96, progress),
            vm.LiquidBrush,
            rotation);

        if (progress > 0.12 && progress < 0.94)
        {
            for (var index = 0; index < 3; index++)
            {
                var phase = (progress * 8.4) + (index * 2.1);
                var dropX = bottleCollectionPosition.X + (Math.Sin(phase) * (12 + (index * 4)));
                var dropY = surfaceY - 3 - (((progress * 38) + (index * 9)) % 22);
                var size = 1.3 + (index * 0.35);
                context.DrawEllipse(
                    Brush(BrushColor(vm.LiquidBrush), 0.64),
                    null,
                    new Rect(dropX - size, dropY - size, size * 2, size * 2.5));
            }
        }
    }

    private void DrawCondenserBottleCollection(DrawingContext context, AlchemyWorkshopViewModel vm)
    {
        var progress = Math.Clamp(bottleCollectionProgress, 0, 1);
        var bottleCenter = distillerBottlePosition;
        var outlet = new Point(1327, 535);
        var bottleMouth = bottleCenter + new Vector(0, -52);
        var flowIn = SmoothStep(0.02, 0.12, progress);
        var flowOut = 1 - SmoothStep(0.88, 1, progress);
        var flowOpacity = flowIn * flowOut;
        var sway = Math.Sin((liquidRippleTime * 6.8) + (progress * 5.2)) * 1.4;
        var stream = StreamGeometry.Parse(
            $"M{outlet.X:0.##},{outlet.Y:0.##} "
            + $"C{outlet.X + sway:0.##},{outlet.Y + 9:0.##} {bottleMouth.X - sway:0.##},{bottleMouth.Y - 8:0.##} {bottleMouth.X:0.##},{bottleMouth.Y:0.##}");
        context.DrawGeometry(
            null,
            new Pen(Brush(BrushColor(vm.LiquidBrush), 0.78 * flowOpacity), 3.1),
            stream);
        context.DrawGeometry(
            null,
            new Pen(Brush(BrushColor(0xFFF2E4C2), 0.66 * flowOpacity), 1.05),
            stream);

        for (var index = 0; index < 4; index++)
        {
            var fall = ((progress * 12.5) + (index * 0.25)) % 1;
            var y = Lerp(outlet.Y + 2, bottleMouth.Y - 2, fall);
            var x = Lerp(outlet.X, bottleMouth.X, fall) + (Math.Sin((fall * Math.PI) + index) * 1.2);
            var size = 1.1 + ((index & 1) * 0.45);
            context.DrawEllipse(
                Brush(BrushColor(vm.LiquidBrush), 0.72 * flowOpacity),
                null,
                new Rect(x - size, y - size, size * 2, size * 2.5));
        }

        var bottleBob = Math.Sin(progress * Math.PI * 5.2) * (1 - progress) * 1.5;
        DrawProductBottle(
            context,
            bottleCenter + new Vector(0, bottleBob),
            SmoothStep(0.04, 0.96, progress),
            vm.LiquidBrush,
            Math.Sin(progress * Math.PI * 2.2) * (1 - progress) * 1.4);
    }

    private void DrawProductBottle(
        DrawingContext context,
        Point center,
        double fillAmount,
        IBrush? liquidBrush = null,
        double rotation = 0)
    {
        fillAmount = Math.Clamp(fillAmount, 0, 1);
        liquidBrush ??= Brush(0xFF8F4A54);
        using (context.PushTransform(Matrix.CreateTranslation(center.X, center.Y)))
        using (context.PushTransform(Matrix.CreateRotation(rotation * Math.PI / 180)))
        {
            var drewSprite = sprites.Draw(context, "image2_bottle_empty", new Rect(-31, -57, 62, 114), 0, 1);
            if (fillAmount > 0.001)
            {
                var liquidTop = Lerp(40, -28, fillAmount);
                var topHalfWidth = liquidTop < -15
                    ? 8.4
                    : Math.Clamp(17.5 + ((liquidTop + 4) * 0.08), 15.5, 21);
                var wave = Math.Sin((liquidRippleTime * 7.4) + (fillAmount * 4.2)) * (1.5 - fillAmount);
                var liquid = StreamGeometry.Parse(
                    $"M{-topHalfWidth:0.##},{liquidTop:0.##} "
                    + $"C{-topHalfWidth * 0.35:0.##},{liquidTop + wave:0.##} {topHalfWidth * 0.36:0.##},{liquidTop - wave:0.##} {topHalfWidth:0.##},{liquidTop:0.##} "
                    + "C22,8 24,17 24,28 C24,37 19,43 13,46 "
                    + "C5,49 -5,49 -13,46 C-19,43 -24,37 -24,28 C-24,17 -22,8 "
                    + $"{-topHalfWidth:0.##},{liquidTop:0.##} Z");
                context.DrawGeometry(Brush(BrushColor(liquidBrush), drewSprite ? 0.68 : 0.92), null, liquid);
                context.DrawGeometry(
                    null,
                    new Pen(Brush(0xBFF4E7C8), 0.9),
                    StreamGeometry.Parse(
                        $"M{-topHalfWidth + 1.2:0.##},{liquidTop:0.##} "
                        + $"C{-topHalfWidth * 0.28:0.##},{liquidTop + wave:0.##} {topHalfWidth * 0.28:0.##},{liquidTop - wave:0.##} {topHalfWidth - 1.2:0.##},{liquidTop:0.##}"));
            }

            if (drewSprite)
            {
                return;
            }

            context.DrawRectangle(Brush(0xFFB6A88E), new Pen(Brush(0xFF51483D), 2), new RoundedRect(new Rect(-9, -50, 18, 19), 3));
            context.DrawGeometry(Brush(0x557B9995), new Pen(Brush(0xFF8C9D96), 2), StreamGeometry.Parse("M-9,-33 C-27,-18 -25,41 -15,48 L15,48 C25,41 27,-18 9,-33 Z"));
        }
    }

    private void DrawFire(DrawingContext context, AlchemyWorkshopViewModel? vm, double potOffset)
    {
        var heat = Math.Clamp((vm?.Temperature ?? 0) / 100, 0, 1);
        var active = vm?.IsCauldronLowered == true;
        var opacity = vm?.FireOpacity ?? (active ? 0.76 : 0.34);
        var pulse = Math.Sin(liquidRippleTime * 5.4);
        var sway = Math.Sin((liquidRippleTime * 3.1) + 0.8);
        var width = (active ? 486 : 424) + (heat * 64) + (pulse * 8);
        var height = (active ? 270 : 226) + (heat * 44) + (pulse * 10);
        var bottom = 711 + potOffset;
        var flameRect = new Rect(
            820 - (width / 2) + (sway * 3.4),
            bottom - height,
            width,
            height);

        var glowOpacity = active ? 0.30 + (heat * 0.24) : 0.16;
        context.DrawEllipse(Brush(BrushColor(0xFFFF6A20), glowOpacity * 0.34), null,
            new Rect(535, 430 + potOffset, 570, 300));
        context.DrawEllipse(Brush(BrushColor(0xFFFFA12C), glowOpacity * 0.26), null,
            new Rect(620, 528 + potOffset, 400, 176));
        sprites.Draw(
            context,
            "alchemy_furnace_flame_v2",
            flameRect,
            sway * 0.8,
            Math.Clamp(opacity, 0.3, 1));
    }

    private void DrawFireBedForeground(DrawingContext context, AlchemyWorkshopViewModel? vm, double potOffset)
    {
        var heat = Math.Clamp((vm?.Temperature ?? 0) / 100, 0, 1);
        var active = vm?.IsCauldronLowered == true;
        var opacity = active ? 0.72 + (heat * 0.24) : 0.32;
        var pulse = Math.Sin((liquidRippleTime * 6.2) + 1.4);
        var destination = new Rect(
            641 + (pulse * 2),
            620 + potOffset,
            358 + (heat * 34),
            104 + (heat * 14));

        using (context.PushClip(new Rect(626, 665 + potOffset, 388, 70)))
        {
            sprites.DrawRegion(
                context,
                "alchemy_furnace_flame_v2",
                new Rect(0, 0.54, 1, 0.46),
                destination,
                pulse * 0.3,
                opacity);
        }

        context.DrawEllipse(Brush(BrushColor(0xFFFFC54A), active ? 0.22 + (heat * 0.18) : 0.09), null,
            new Rect(680, 680 + potOffset, 280, 24));
    }

    private static void DrawTargetHalo(DrawingContext context, Rect rect, bool active)
    {
        if (!active)
        {
            return;
        }

        context.DrawEllipse(Brush(0x22E6C887), CachedPen(Brush(0xFFE6C887), 3), new Rect(rect.X - 8, rect.Y - 8, rect.Width + 16, rect.Height + 16));
    }

    private static void DrawHudDivider(DrawingContext context, double x, double y, double height)
    {
        context.DrawLine(CachedPen(Brush(AlchemyHudTheme.BorderDark), 2), new Point(x, y), new Point(x, y + height));
        context.DrawLine(CachedPen(Brush(AlchemyHudTheme.SurfaceHighlight), 0.7), new Point(x + 1, y), new Point(x + 1, y + height));
    }

    private static void DrawStatusDot(DrawingContext context, Point center, AlchemyHudTone tone)
    {
        var color = Brush(AlchemyHudTheme.ColorFor(tone));
        context.DrawEllipse(color, CachedPen(Brush(AlchemyHudTheme.Surface), 1), new Rect(center.X - 4, center.Y - 4, 8, 8));
    }

    private static string ProcessIconKey(AlchemyWorkshopViewModel? vm)
    {
        if (vm?.IsHourglassRunning == true)
        {
            return "hud_hourglass";
        }

        if (vm?.IsMortarLoaded == true)
        {
            return "hud_mortar";
        }

        return vm?.Snapshot.Distilled == true ? "hud_potion" : "hud_recipe_book";
    }

    private static void DrawHudBandFrame(DrawingContext context, Rect bounds)
    {
        context.DrawRectangle(Brush(AlchemyHudTheme.SurfaceOuter), CachedPen(Brush(AlchemyHudTheme.BorderDark), 2), bounds);
        context.DrawRectangle(null, CachedPen(Brush(AlchemyHudTheme.Border), 0.9),
            new Rect(bounds.X + 3, bounds.Y + 3, bounds.Width - 6, bounds.Height - 6));
        context.DrawLine(CachedPen(Brush(AlchemyHudTheme.SurfaceHighlight), 0.8),
            new Point(bounds.X + 4, bounds.Y + 4), new Point(bounds.Right - 4, bounds.Y + 4));
        context.DrawLine(CachedPen(Brush(AlchemyHudTheme.BrassMid), 1.2),
            new Point(bounds.X + 2, bounds.Bottom - 3), new Point(bounds.Right - 2, bounds.Bottom - 3));
    }

    private static void DrawHudPlaque(DrawingContext context, Rect bounds, bool highlighted, bool active)
    {
        DrawInteractivePlaque(
            context,
            bounds,
            active
                ? AlchemyInteractionState.Selected
                : highlighted
                    ? AlchemyInteractionState.Hover
                    : AlchemyInteractionState.Default);
    }

    private static AlchemyInteractionVisual DrawInteractivePlaque(
        DrawingContext context,
        Rect bounds,
        AlchemyInteractionState state)
    {
        var visual = AlchemyInteractionPresentation.From(state);
        var shifted = new Rect(bounds.X, bounds.Y + visual.ContentOffset, bounds.Width, bounds.Height);
        context.DrawRectangle(Brush(BrushColor(AlchemyHudTheme.HudShadow), visual.Opacity), null,
            new RoundedRect(new Rect(
                bounds.X + 2,
                bounds.Y + visual.ContentOffset + visual.ShadowOffset,
                bounds.Width,
                bounds.Height), 4));
        context.DrawRectangle(
            Brush(BrushColor(visual.Surface), visual.Opacity),
            CachedPen(Brush(BrushColor(visual.Border), visual.Opacity), visual.BorderThickness),
            new RoundedRect(shifted, 4));
        context.DrawRectangle(null, CachedPen(Brush(BrushColor(AlchemyHudTheme.SurfaceHighlight), visual.Opacity), 0.7),
            new RoundedRect(new Rect(shifted.X + 3, shifted.Y + 3, shifted.Width - 6, shifted.Height - 6), 2));

        if (state == AlchemyInteractionState.Selected)
        {
            context.DrawRectangle(Brush(AlchemyHudTheme.AccentBright), null,
                new Rect(shifted.X + 2, shifted.Y + 6, 3, shifted.Height - 12));
        }

        if (state == AlchemyInteractionState.Focused)
        {
            context.DrawRectangle(null, CachedPen(Brush(AlchemyHudTheme.FocusRing), 1),
                new RoundedRect(new Rect(shifted.X - 2, shifted.Y - 2, shifted.Width + 4, shifted.Height + 4), 5));
        }

        return visual;
    }

    private void DrawHudIconBadge(DrawingContext context, string key, Rect bounds, bool highlighted)
    {
        context.DrawRectangle(Brush(AlchemyHudTheme.HudShadow), null,
            new RoundedRect(new Rect(bounds.X + 1.5, bounds.Y + 2, bounds.Width, bounds.Height), 4));
        context.DrawRectangle(Brush(AlchemyHudTheme.SurfaceRaised),
            CachedPen(Brush(highlighted ? AlchemyHudTheme.AccentBright : AlchemyHudTheme.BrassDark), highlighted ? 1.4 : 1),
            new RoundedRect(bounds, 4));
        context.DrawRectangle(null, CachedPen(Brush(AlchemyHudTheme.SurfaceHighlight), 0.7),
            new RoundedRect(new Rect(bounds.X + 3, bounds.Y + 3, bounds.Width - 6, bounds.Height - 6), 2));
        sprites.Draw(context, key, new Rect(bounds.X + 3, bounds.Y + 3, bounds.Width - 6, bounds.Height - 6), 0,
            highlighted ? 1 : 0.88);
    }

    private static void DrawHudStud(DrawingContext context, Point center, double radius)
    {
        context.DrawEllipse(Brush(AlchemyHudTheme.BrassMid), CachedPen(Brush(AlchemyHudTheme.BrassDark), 0.8),
            new Rect(center.X - radius, center.Y - radius, radius * 2, radius * 2));
        context.DrawEllipse(Brush(0x88F1DFB5), null,
            new Rect(center.X - (radius * 0.45), center.Y - (radius * 0.55), radius * 0.7, radius * 0.7));
    }

    private static void DrawText(
        DrawingContext context,
        string text,
        Point point,
        double size,
        IBrush brush,
        Typeface typeface,
        double maxWidth = double.PositiveInfinity,
        TextAlignment alignment = TextAlignment.Left)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var key = new TextLayoutKey(text, typeface, size, brush, maxWidth, alignment);
        if (!TextLayoutCache.TryGetValue(key, out var formatted))
        {
            if (TextLayoutCache.Count >= MaximumCachedTextLayouts)
            {
                TextLayoutCache.Clear();
            }

            formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, brush)
            {
                MaxTextWidth = maxWidth,
                TextAlignment = alignment,
                Trimming = TextTrimming.CharacterEllipsis
            };
            TextLayoutCache[key] = formatted;
        }
        context.DrawText(formatted, point);
    }

    private void CalculateSceneTransform()
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            sceneScale = 1;
            sceneOffset = default;
            return;
        }

        sceneScale = Math.Min(Bounds.Width / SceneWidth, Bounds.Height / SceneHeight);
        sceneOffset = new Point(
            (Bounds.Width - (SceneWidth * sceneScale)) / 2,
            (Bounds.Height - (SceneHeight * sceneScale)) / 2);
    }

    private Point ToScene(Point point)
    {
        CalculateSceneTransform();
        return new Point(
            (point.X - sceneOffset.X) / Math.Max(0.0001, sceneScale),
            (point.Y - sceneOffset.Y) / Math.Max(0.0001, sceneScale));
    }

    private static Rect IngredientSlotRect(int index)
    {
        var column = index % 2;
        var row = index / 2;
        return new Rect(18 + (column * 119), 150 + (row * 130), 111, 120);
    }

    private static Point IngredientSlotCenter(int index) => IngredientSlotRect(index).Center;

    private static Rect BaseSlotRect(int index) => new(17 + (index * 62), 595, 56, 66);

    private static Point BaseSlotCenter(int index) => BaseSlotRect(Math.Max(0, index)).Center;

    private static Rect IngredientPickerBounds(int index)
    {
        var slot = IngredientSlotRect(index);
        return new Rect(slot.X + 4, slot.Bottom - 53, slot.Width - 8, 49);
    }

    private static Rect IngredientPickerOptionRect(int ingredientIndex, int optionIndex)
    {
        var bounds = IngredientPickerBounds(ingredientIndex);
        return new Rect(bounds.X + 5 + (optionIndex * 31), bounds.Y + 19, 29, 24);
    }

    private static readonly Rect HourglassPickerBounds = new(1013, 412, 166, 48);

    private static Rect HourglassPickerOptionRect(int optionIndex) =>
        new(HourglassPickerBounds.X + 36 + (optionIndex * 32), HourglassPickerBounds.Y + 19, 30, 24);

    private static Point Lerp(Point from, Point to, double amount) =>
        new(from.X + ((to.X - from.X) * amount), from.Y + ((to.Y - from.Y) * amount));

    private static double Lerp(double from, double to, double amount) => from + ((to - from) * amount);

    private static string? MortarIngredientId(AlchemyWorkshopViewModel vm) =>
        vm.Snapshot.Mortar?.IngredientId;

    private static RecipeStepKind? CurrentStepKind(AlchemyWorkshopViewModel? vm)
    {
        if (vm is null
            || vm.Snapshot.CurrentStepIndex < 0
            || vm.Snapshot.CurrentStepIndex >= vm.Snapshot.Recipe.Steps.Count)
        {
            return null;
        }

        return vm.Snapshot.Recipe.Steps[vm.Snapshot.CurrentStepIndex].Kind;
    }

    private static string Ui(AlchemyWorkshopViewModel? vm, AlchemyTextKey key) =>
        AlchemyTextCatalog.Get(key, vm?.UseEnglish == true);

    private static Color IngredientColor(string id) =>
        BrushColor(AlchemyCatalog.Ingredients.FirstOrDefault(item => item.Id == id)?.Color ?? 0xFF71825D);

    private static Color LiquidColor(BaseLiquid liquid) => liquid switch
    {
        BaseLiquid.Water => BrushColor(0xFF72A9B5),
        BaseLiquid.Wine => BrushColor(0xFF8A4049),
        BaseLiquid.Spirits => BrushColor(0xFFD0C39B),
        BaseLiquid.Oil => BrushColor(0xFFC59A3E),
        _ => BrushColor(0xFF8A9A8A)
    };

    private static IBrush Brush(uint argb)
    {
        if (BrushCache.TryGetValue(argb, out var brush))
        {
            return brush;
        }

        if (BrushCache.Count >= 512)
        {
            BrushCache.Clear();
        }

        brush = new SolidColorBrush(BrushColor(argb));
        BrushCache[argb] = brush;
        return brush;
    }

    private static IBrush Brush(Color color, double opacity)
    {
        var alpha = (byte)(color.A * Math.Clamp(opacity, 0, 1));
        var argb = ((uint)alpha << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
        return Brush(argb);
    }

    private static Pen CachedPen(IBrush brush, double thickness)
    {
        var key = new PenKey(brush, thickness);
        if (PenCache.TryGetValue(key, out var pen))
        {
            return pen;
        }

        if (PenCache.Count >= 256)
        {
            PenCache.Clear();
        }

        pen = new Pen(brush, thickness);
        PenCache[key] = pen;
        return pen;
    }

    private static double SmoothStep(double from, double to, double value)
    {
        var t = Math.Clamp((value - from) / (to - from), 0, 1);
        return t * t * (3 - (2 * t));
    }

    private static double EaseOutBack(double value)
    {
        const double overshoot = 1.28;
        var t = Math.Clamp(value, 0, 1) - 1;
        return 1 + ((overshoot + 1) * t * t * t) + (overshoot * t * t);
    }

    private static Color MixColor(Color from, Color to, double amount) =>
        Color.FromArgb(
            (byte)Lerp(from.A, to.A, Math.Clamp(amount, 0, 1)),
            (byte)Lerp(from.R, to.R, Math.Clamp(amount, 0, 1)),
            (byte)Lerp(from.G, to.G, Math.Clamp(amount, 0, 1)),
            (byte)Lerp(from.B, to.B, Math.Clamp(amount, 0, 1)));

    private static Color BrushColor(uint argb) => Color.FromArgb(
        (byte)(argb >> 24),
        (byte)(argb >> 16),
        (byte)(argb >> 8),
        (byte)argb);

    private static Color BrushColor(IBrush brush) =>
        brush is ISolidColorBrush solid ? solid.Color : BrushColor(0xFF8F4A54);

    private enum DragMode
    {
        None,
        Ingredient,
        BaseBottle,
        Pestle,
        Bellows,
        Hourglass,
        GroundPowder,
        CauldronHook,
        DistillerLever,
        ProductBottle
    }

    private enum QuantityPickerMode
    {
        None,
        Ingredient,
        Hourglass
    }

    private readonly record struct TextLayoutKey(
        string Text,
        Typeface Typeface,
        double Size,
        IBrush Brush,
        double MaxWidth,
        TextAlignment Alignment);

    private readonly record struct PenKey(IBrush Brush, double Thickness);

    private sealed class SpriteAtlas
    {
        private readonly Dictionary<string, SpriteEntry?> cache = new(StringComparer.OrdinalIgnoreCase);

        public double DecodeScale { get; set; } = 1;

        public void Clear()
        {
            foreach (var entry in cache.Values)
            {
                entry?.Dispose();
            }

            cache.Clear();
        }

        public bool Draw(DrawingContext context, string key, Rect destination, double rotation, double opacity)
        {
            if (opacity <= 0.001 || destination.Width <= 0 || destination.Height <= 0)
            {
                return GetAvailable(key);
            }

            var image = Get(key, destination.Width);
            if (image is null)
            {
                return false;
            }

            var center = destination.Center;
            using (context.PushOpacity(Math.Clamp(opacity, 0, 1)))
            using (context.PushTransform(Matrix.CreateTranslation(center.X, center.Y)))
            using (context.PushTransform(Matrix.CreateRotation(rotation * Math.PI / 180)))
            using (context.PushTransform(Matrix.CreateTranslation(-center.X, -center.Y)))
            {
                // Direct IImage rendering: PNG sprite pixels are submitted to Avalonia's DrawingContext.
                context.DrawImage(image, new Rect(image.Size), destination);
            }

            return true;
        }

        public bool DrawRegion(
            DrawingContext context,
            string key,
            Rect normalizedSource,
            Rect destination,
            double rotation,
            double opacity)
        {
            if (opacity <= 0.001 || destination.Width <= 0 || destination.Height <= 0)
            {
                return GetAvailable(key);
            }

            var image = Get(key, destination.Width / Math.Max(0.01, normalizedSource.Width));
            if (image is null)
            {
                return false;
            }

            var source = new Rect(
                normalizedSource.X * image.Size.Width,
                normalizedSource.Y * image.Size.Height,
                normalizedSource.Width * image.Size.Width,
                normalizedSource.Height * image.Size.Height);
            var center = destination.Center;
            using (context.PushOpacity(Math.Clamp(opacity, 0, 1)))
            using (context.PushTransform(Matrix.CreateTranslation(center.X, center.Y)))
            using (context.PushTransform(Matrix.CreateRotation(rotation * Math.PI / 180)))
            using (context.PushTransform(Matrix.CreateTranslation(-center.X, -center.Y)))
            {
                context.DrawImage(image, source, destination);
            }

            return true;
        }

        public bool GetAvailable(string key) =>
            cache.TryGetValue(key, out var cached)
                ? cached is not null
                : File.Exists(GetPath(key));

        private Bitmap? Get(string key, double logicalWidth)
        {
            var decodeWidth = Math.Max(1, (int)Math.Ceiling(logicalWidth * Math.Max(0.1, DecodeScale)));
            if (cache.TryGetValue(key, out var cached) &&
                (cached is null || cached.DecodeWidth >= decodeWidth))
            {
                return cached?.Bitmap;
            }

            try
            {
                cached?.Dispose();
                var path = GetPath(key);
                if (!File.Exists(path))
                {
                    cached = null;
                }
                else
                {
                    using var stream = File.OpenRead(path);
                    cached = new SpriteEntry(
                        Bitmap.DecodeToWidth(stream, decodeWidth, BitmapInterpolationMode.HighQuality),
                        decodeWidth);
                }
            }
            catch
            {
                cached?.Dispose();
                cached = null;
            }

            cache[key] = cached;
            return cached?.Bitmap;
        }

        private static string GetPath(string key) => Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "Alchemy",
            "Sprites",
            $"{key}.png");

        private sealed record SpriteEntry(Bitmap Bitmap, int DecodeWidth) : IDisposable
        {
            public void Dispose() => Bitmap.Dispose();
        }
    }

    private sealed class ParticleSystem
    {
        private const int MaximumParticles = 260;
        private readonly List<Particle> active = [];
        private readonly Random random = new(271828);

        public bool HasActiveParticles => active.Count > 0;

        public void Update(double dt)
        {
            for (var index = active.Count - 1; index >= 0; index--)
            {
                var particle = active[index];
                particle.Age += dt;
                if (particle.Age >= particle.Lifetime)
                {
                    active.RemoveAt(index);
                    continue;
                }

                if (particle.Kind is ParticleKind.Dust or ParticleKind.Splash or ParticleKind.PourImpact or ParticleKind.Fire or ParticleKind.Sparkle or ParticleKind.LeafChip or ParticleKind.Powder or ParticleKind.PowderTrail)
                {
                    var gravity = particle.Kind switch
                    {
                        ParticleKind.Fire => -8,
                        ParticleKind.Powder => 26,
                        ParticleKind.PowderTrail => 34,
                        ParticleKind.LeafChip => 58,
                        _ => 42
                    };
                    particle.Velocity += new Vector(0, gravity) * dt;
                }

                particle.Position += particle.Velocity * dt;
                if (particle.Kind is ParticleKind.LeafChip or ParticleKind.Powder
                    && particle.Position.X is > 430 and < 626
                    && particle.Position.Y > 510)
                {
                    particle.Position = new Point(particle.Position.X, 510);
                    particle.Velocity = new Vector(
                        particle.Velocity.X * 0.68,
                        -Math.Abs(particle.Velocity.Y) * (particle.Kind == ParticleKind.LeafChip ? 0.32 : 0.18));
                }

                if (particle.Kind is ParticleKind.Bubble or ParticleKind.Steam)
                {
                    particle.Position += new Vector(Math.Sin((particle.Age * 6) + particle.Phase) * 12 * dt, 0);
                }
            }
        }

        public void Render(DrawingContext context)
        {
            foreach (var particle in active)
            {
                if (particle.Kind is not (ParticleKind.LeafChip or ParticleKind.Powder or ParticleKind.PourImpact
                    or ParticleKind.Bubble or ParticleKind.Splash or ParticleKind.PowderTrail))
                {
                    RenderParticle(context, particle);
                }
            }
        }

        public void RenderCauldron(DrawingContext context)
        {
            foreach (var particle in active)
            {
                if (particle.Kind is ParticleKind.PourImpact or ParticleKind.Bubble or ParticleKind.Splash
                    or ParticleKind.PowderTrail)
                {
                    RenderParticle(context, particle);
                }
            }
        }

        public void RenderMortar(DrawingContext context)
        {
            using (context.PushClip(new Rect(430, 420, 196, 104)))
            {
                foreach (var particle in active)
                {
                    if (particle.Kind is ParticleKind.LeafChip or ParticleKind.Powder)
                    {
                        RenderParticle(context, particle);
                    }
                }
            }
        }

        private static void RenderParticle(DrawingContext context, Particle particle)
        {
            var life = 1 - (particle.Age / particle.Lifetime);
            var alpha = particle.Kind == ParticleKind.Bubble ? Math.Sin(life * Math.PI) : life;
            var color = Color.FromArgb(
                (byte)(particle.Color.A * Math.Clamp(alpha, 0, 1)),
                particle.Color.R,
                particle.Color.G,
                particle.Color.B);
            var brush = new SolidColorBrush(color);
            var size = particle.Size * (particle.Kind == ParticleKind.Bubble ? 0.7 + ((1 - life) * 0.8) : 1);

            if (particle.Kind == ParticleKind.Bubble)
            {
                context.DrawEllipse(null, new Pen(brush, 0.8),
                    new Rect(particle.Position.X - size, particle.Position.Y - (size * 0.58), size * 2, size * 1.16));
            }
            else if (particle.Kind == ParticleKind.Steam)
            {
                context.DrawEllipse(brush, null, new Rect(particle.Position.X - (size * 1.8), particle.Position.Y - size, size * 3.6, size * 2));
            }
            else if (particle.Kind is ParticleKind.Splash or ParticleKind.PourImpact)
            {
                var angle = Math.Atan2(particle.Velocity.Y, particle.Velocity.X) + (Math.PI / 2);
                using (context.PushTransform(Matrix.CreateTranslation(particle.Position.X, particle.Position.Y)))
                using (context.PushTransform(Matrix.CreateRotation(angle)))
                {
                    context.DrawEllipse(brush, null, new Rect(-size * 0.38, -size * 1.35, size * 0.76, size * 2.7));
                }
            }
            else if (particle.Kind == ParticleKind.LeafChip)
            {
                using (context.PushTransform(Matrix.CreateTranslation(particle.Position.X, particle.Position.Y)))
                using (context.PushTransform(Matrix.CreateRotation(particle.Phase + (particle.Age * 7))))
                {
                    context.DrawEllipse(brush, new Pen(new SolidColorBrush(BrushColor(0xAA2B2118)), 0.6), new Rect(-size * 1.2, -size * 0.52, size * 2.4, size * 1.04));
                }
            }
            else
            {
                context.DrawEllipse(brush, null, new Rect(particle.Position.X - size, particle.Position.Y - size, size * 2, size * 2));
            }
        }

        public void EmitBubble(Point origin, double intensity)
        {
            Add(new Particle(
                ParticleKind.Bubble,
                origin,
                new Vector(0, -22 - (random.NextDouble() * 34 * Math.Max(0.2, intensity))),
                0.9 + random.NextDouble(),
                1.4 + (random.NextDouble() * 2.4),
                BrushColor(0x88E4D8C3),
                random.NextDouble() * Math.PI * 2));
        }

        public void EmitDust(Point origin, int count, Color color)
        {
            for (var index = 0; index < count; index++)
            {
                Add(new Particle(
                    ParticleKind.Dust,
                    origin + new Vector(RandomRange(-22, 22), RandomRange(-11, 11)),
                    new Vector(RandomRange(-33, 33), RandomRange(-52, -8)),
                    RandomRange(0.35, 0.82),
                    RandomRange(1.4, 3.5),
                    color,
                    0));
            }
        }

        public void EmitGrindingDebris(Point origin, int count, Color ingredientColor, double grindingProgress)
        {
            var powder = SmoothStep(0.42, 0.94, grindingProgress);
            var powderColor = MixColor(ingredientColor, BrushColor(0xFFE0C789), 0.16);
            for (var index = 0; index < count; index++)
            {
                var isPowder = random.NextDouble() < powder;
                Add(new Particle(
                    isPowder ? ParticleKind.Powder : ParticleKind.LeafChip,
                    origin + new Vector(RandomRange(-14, 14), RandomRange(-7, 7)),
                    new Vector(RandomRange(-30, 30), RandomRange(-44, -12)),
                    isPowder ? RandomRange(0.24, 0.50) : RandomRange(0.32, 0.64),
                    isPowder ? RandomRange(0.75, 1.9) : RandomRange(1.9, 3.9),
                    isPowder ? powderColor : ingredientColor,
                    random.NextDouble() * Math.PI * 2));
            }
        }

        public void EmitPowderTrail(Point origin, int count, Color ingredientColor)
        {
            var powderColor = MixColor(ingredientColor, BrushColor(0xFFE0C789), 0.16);
            for (var index = 0; index < count; index++)
            {
                Add(new Particle(
                    ParticleKind.PowderTrail,
                    origin + new Vector(RandomRange(-12, 12), RandomRange(-5, 5)),
                    new Vector(RandomRange(-24, 24), RandomRange(-28, 4)),
                    RandomRange(0.25, 0.52),
                    RandomRange(0.7, 1.7),
                    powderColor,
                    random.NextDouble() * Math.PI * 2));
            }
        }

        public void EmitSplash(Point origin, int count, Color color)
        {
            var splashColor = MixColor(color, BrushColor(0xFF241B13), 0.22);
            for (var index = 0; index < count; index++)
            {
                Add(new Particle(
                    ParticleKind.Splash,
                    origin + new Vector(RandomRange(-4, 4), RandomRange(-1.5, 1.5)),
                    new Vector(RandomRange(-62, 62), RandomRange(-104, -38)),
                    RandomRange(0.28, 0.54),
                    RandomRange(1.1, 2.7),
                    splashColor,
                    random.NextDouble() * Math.PI * 2));
            }
        }

        public void EmitPourImpact(Point origin, int count, Color color, double flow)
        {
            flow = Math.Clamp(flow, 0.1, 1);
            for (var index = 0; index < count; index++)
            {
                var direction = index % 2 == 0 ? -1 : 1;
                Add(new Particle(
                    ParticleKind.PourImpact,
                    origin + new Vector(RandomRange(-2.5, 2.5), RandomRange(-1.5, 1.5)),
                    new Vector(
                        direction * RandomRange(18, 72) * (0.45 + (flow * 0.55)),
                        RandomRange(-88, -28) * (0.5 + (flow * 0.5))),
                    RandomRange(0.24, 0.52),
                    RandomRange(1.2, 3.1) * (0.72 + (flow * 0.28)),
                    color,
                    0));
            }
        }

        public void EmitSteam(Point origin, int count)
        {
            for (var index = 0; index < count; index++)
            {
                Add(new Particle(
                    ParticleKind.Steam,
                    origin + new Vector(RandomRange(-18, 18), RandomRange(-3, 3)),
                    new Vector(RandomRange(-8, 8), RandomRange(-42, -24)),
                    RandomRange(0.8, 1.6),
                    RandomRange(5, 10),
                    BrushColor(0x66E9E1D5),
                    random.NextDouble() * Math.PI * 2));
            }
        }

        public void EmitFire(Point origin, int count)
        {
            for (var index = 0; index < count; index++)
            {
                Add(new Particle(
                    ParticleKind.Fire,
                    origin + new Vector(RandomRange(-45, 45), RandomRange(-5, 5)),
                    new Vector(RandomRange(-24, 24), RandomRange(-125, -55)),
                    RandomRange(0.3, 0.65),
                    RandomRange(2.5, 6),
                    index % 2 == 0 ? BrushColor(0xFFFFA13D) : BrushColor(0xFFE3552D),
                    0));
            }
        }

        public void EmitPourDrop(Point origin, Color color)
        {
            Add(new Particle(
                ParticleKind.Splash,
                origin,
                new Vector(RandomRange(50, 90), RandomRange(110, 180)),
                RandomRange(0.22, 0.38),
                RandomRange(2.2, 4.3),
                color,
                0));
        }

        public void EmitSparkle(Point origin, int count)
        {
            for (var index = 0; index < count; index++)
            {
                Add(new Particle(
                    ParticleKind.Sparkle,
                    origin,
                    new Vector(RandomRange(-85, 85), RandomRange(-110, 18)),
                    RandomRange(0.5, 1.1),
                    RandomRange(1.5, 4),
                    BrushColor(0xFFEBD58B),
                    0));
            }
        }

        private void Add(Particle particle)
        {
            if (active.Count >= MaximumParticles)
            {
                active.RemoveAt(0);
            }

            active.Add(particle);
        }

        private double RandomRange(double minimum, double maximum) => minimum + (random.NextDouble() * (maximum - minimum));

        private sealed class Particle(
            ParticleKind kind,
            Point position,
            Vector velocity,
            double lifetime,
            double size,
            Color color,
            double phase)
        {
            public ParticleKind Kind { get; } = kind;
            public Point Position { get; set; } = position;
            public Vector Velocity { get; set; } = velocity;
            public double Lifetime { get; } = lifetime;
            public double Size { get; } = size;
            public Color Color { get; } = color;
            public double Phase { get; } = phase;
            public double Age { get; set; }
        }

        private enum ParticleKind
        {
            Bubble,
            Steam,
            Dust,
            Splash,
            PourImpact,
            Fire,
            Sparkle,
            LeafChip,
            Powder,
            PowderTrail
        }
    }
}
