using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BohemiX.App.Controls;
using BohemiX.App.Community;
using BohemiX.App.PlayerProfiles;
using BohemiX.App.Services;
using BohemiX.App.ViewModels;
using BohemiX.Modules.Alchemy.Views;
using BohemiX.Modules.Forge.Rendering;
using BohemiX.Modules.Forge.Views;
using LibVLCSharp.Avalonia;
using LibVLCSharp.Shared;
using SkiaSharp;

namespace BohemiX.App.Views;

public partial class MainWindow : Window
{
    private TranslateTransform DynamicBackgroundTranslate =>
        DynamicBackgroundImage.RenderTransform as TranslateTransform
        ?? throw new InvalidOperationException("The dynamic background image requires a TranslateTransform.");

    private const double SidebarItemEntranceOffsetX = -36;
    private const double SidebarItemEntranceOpacity = 0;
    private const double SidebarContentGap = 0;
    private const double SidebarNavigationHorizontalInset = 20;
    private const double WindowDragStartThresholdPixels = 8;
    private const int SidebarNavigationBlankDelayMs = 70;
    private const int SidebarItemEntranceDurationMs = 260;
    private const int SidebarItemEntranceStaggerMs = 38;
    private const int SidebarLayoutAnimationDurationMs = 220;
    private const double GameIdentityIconSize = 38;
    private const double ContentSurfaceCollapsedRevealRatio = 0.12;
    private const double ContentSurfaceOvershootRevealRatio = 1.026;
    private const double ContentSurfaceStartScaleY = 0.992;
    private const double ContentSurfaceOvershootScaleY = 1.012;
    private const double ContentSurfaceEntranceOffsetY = -10;
    private const int ContentSurfaceExpandDurationMs = 300;
    private const int ContentSurfaceSettleDurationMs = 135;
    private const int CloseWindowHoverSpinDurationMs = 360;
    private const double LauncherToggleArrowBounceDistance = 7;
    private const int LauncherToggleArrowBounceDurationMs = 430;
    private const int LauncherToggleArrowBounceRestMs = 120;
    private const int WindowDragHoldDelayMs = 160;
    private const double ScrollDragStartThresholdPixels = 10;
    private const double LauncherDetailsScrollStep = 72;
    private const double LauncherDetailsBottomPadding = 40;
    private const double ModDownloadPageVerticalMargin = 50;
    private const double ModSearchWheelScrollStep = 88;
    private const double ModSearchAutoLoadMoreThresholdPixels = 120;
    private const double ModListDragAutoScrollEdgePixels = 56;
    private const double ModListDragAutoScrollMaximumStep = 32;
    private static readonly DataFormat<string> ModDragDataFormat =
        DataFormat.CreateStringApplicationFormat("BohemiX.ModListItem");
    private const double ModSearchScrollThumbMinHeight = 42;
    private const double ModDownloadSearchViewportReservedHeight = 326;
    private const double ModDownloadQueueViewportReservedHeight = 458;
    private const double ModListViewportReservedHeight = 250;
    private const double ModRecommendationViewportReservedHeight = 250;
    private const double WindowFrameCornerRadius = 18;
    private const double StartupRevealScale = 1;
    private const int StartupRevealDelayMs = 0;
    private const int WindowResizeDurationMs = 0;
    private const int LaunchHighlightSweepDurationMs = 480;
    private const int LaunchHighlightRestDurationMs = 1500;
    private const int LaunchHighlightFrameDelayMs = 16;
    private const double TargetWindowWidth = 1600;
    private const double TargetWindowHeight = 900;
    private const string DefaultBackgroundSource = "avares://BohemiX.App/Assets/kcd2-cover-bg.jpg";
    private const string StartupStripeSource = "avares://BohemiX.App/Assets/striped-background-page.png";
    private static readonly PixelSize DefaultBackgroundPixelSize = new(2560, 1440);
    private static readonly PixelSize StartupStripePixelSize = new(2560, 2160);
    private static readonly object LibVlcInitializationGate = new();
    private static bool isLibVlcCoreInitializationAttempted;
    private static bool isLibVlcCoreInitialized;
    private readonly MemoryEffectsBudget memoryEffectsBudget = new();
    private readonly DispatcherTimer memoryBudgetTimer = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromSeconds(5)
    };

    private CancellationTokenSource? launcherPanelAnimationCancellation;
    private CancellationTokenSource? launcherToggleArrowBounceCancellation;
    private CancellationTokenSource? sidebarNavigationAnimationCancellation;
    private CancellationTokenSource? sidebarLayoutAnimationCancellation;
    private CancellationTokenSource? contentSurfaceAnimationCancellation;
    private CancellationTokenSource? closeWindowSpinCancellation;
    private CancellationTokenSource? startupFadeCancellation;
    private CancellationTokenSource? launchHighlightAnimationCancellation;
    private MainWindowViewModel? subscribedViewModel;
    private LibVLC? backgroundLibVlc;
    private MediaPlayer? backgroundMediaPlayer;
    private Media? backgroundMedia;
    private VideoView? backgroundVideoView;
    private string? activeVideoBackgroundPath;
    private BitmapLease? defaultBackgroundLease;
    private BitmapLease? startupStripeLease;
    private int defaultBackgroundDecodeWidth;
    private int startupStripeDecodeWidth;
    private Bitmap? staticBackgroundBitmap;
    private string? staticBackgroundPath;
    private string? staticBackgroundSourcePath;
    private PixelSize staticBackgroundSourceSize;
    private int staticBackgroundDecodeWidth;
    private int sidebarNavigationAnimationRequestVersion;
    private int contentSurfaceAnimationRequestVersion;
    private bool isLauncherDetailsOpen;
    private double launcherDetailsScrollOffset;
    private bool isLauncherTogglePointerOver;
    private bool isWindowDragActive;
    private bool isWindowDragPressed;
    private bool isModSearchResultsDragActive;
    private bool isModSearchResultsDragScrolling;
    private bool isModSearchResultsScrollThumbDragActive;
    private bool isModListRowsScrollThumbDragActive;
    private bool isModRecommendationRowsScrollThumbDragActive;
    private bool isAwaitingModWorkspaceLayout;
    private bool isSidebarLayoutAnimating;
    private bool isSidebarTextVisible = true;
    private ScrollViewer? modListWheelScrollViewer;
    private ScrollViewer? modSearchWheelScrollViewer;
    private ScrollViewer? modRecommendationWheelScrollViewer;
    private ScrollViewer? modInspectorWheelScrollViewer;
    private double modSearchResultsScrollThumbPointerOffsetY;
    private double modListRowsScrollThumbPointerOffsetY;
    private double modRecommendationRowsScrollThumbPointerOffsetY;
    private NexusModSearchRowViewModel? modSearchResultsPressedItem;
    private ModListItemViewModel? modDragCandidate;
    private IPointer? modDragPointer;
    private Point modDragStartPoint;
    private bool isModDragActive;
    private Point modSearchResultsDragStartPointer;
    private Vector modSearchResultsDragStartOffset;
    private double modListPageOffset;
    private double modDownloadPageOffset;
    private double modRecommendationPageOffset;
    private DateTimeOffset windowDragPressedAt;
    private PixelPoint windowDragStartPointerPosition;
    private PixelPoint windowDragStartWindowPosition;
    private TranslateTransform LauncherContentTrackTranslate => (TranslateTransform)LauncherContentTrack.RenderTransform!;
    private TranslateTransform LauncherToggleButtonTranslate => (TranslateTransform)LauncherToggleButton.RenderTransform!;
    private TransformGroup LauncherToggleIconTransforms => (TransformGroup)LauncherToggleIcon.RenderTransform!;
    private ScaleTransform LauncherToggleIconScale => (ScaleTransform)LauncherToggleIconTransforms.Children[0];
    private RotateTransform LauncherToggleIconRotate => (RotateTransform)LauncherToggleIconTransforms.Children[1];
    private TranslateTransform LauncherToggleIconTranslate => (TranslateTransform)LauncherToggleIconTransforms.Children[2];
    private RotateTransform CloseWindowGlyphRotate => (RotateTransform)CloseWindowGlyph.RenderTransform!;
    private TransformGroup LauncherDetailsTransforms => (TransformGroup)LauncherDetailsHost.RenderTransform!;
    private ScaleTransform LauncherDetailsScale => (ScaleTransform)LauncherDetailsTransforms.Children[0];
    private TransformGroup RootChromeTransforms => (TransformGroup)RootChrome.RenderTransform!;
    private ScaleTransform RootChromeScale => (ScaleTransform)RootChromeTransforms.Children[0];
    private TranslateTransform RootChromeTranslate => (TranslateTransform)RootChromeTransforms.Children[1];
    private TranslateTransform HeroLaunchHighlightTranslate => (TranslateTransform)((TransformGroup)HeroLaunchHighlight.RenderTransform!).Children[1];
    private ScrollViewer ModListPageScrollViewer => GetRequiredModControl<ScrollViewer>(nameof(ModListPageScrollViewer));
    private ListBox ModListRowsListBox => GetRequiredModControl<ListBox>(nameof(ModListRowsListBox));
    private Border ModListRowsScrollHost => GetRequiredModControl<Border>(nameof(ModListRowsScrollHost));
    private ScrollViewer ModListRowsScrollViewer =>
        ModListRowsScrollHost.FindDescendantOfType<ScrollViewer>()
        ?? throw new InvalidOperationException("The Mod list scroll viewer is unavailable.");
    private Border ModListRowsScrollTrack => GetRequiredModControl<Border>(nameof(ModListRowsScrollTrack));
    private Border ModListRowsScrollThumb => GetRequiredModControl<Border>(nameof(ModListRowsScrollThumb));
    private ScrollViewer ModDownloadPageScrollViewer => GetRequiredModControl<ScrollViewer>(nameof(ModDownloadPageScrollViewer));
    private Grid ModDownloadPageHost => GetRequiredModControl<Grid>(nameof(ModDownloadPageHost));
    private ScrollViewer ModSearchResultsScrollViewer => GetRequiredModControl<ScrollViewer>(nameof(ModSearchResultsScrollViewer));
    private Border ModSearchResultsScrollTrack => GetRequiredModControl<Border>(nameof(ModSearchResultsScrollTrack));
    private Border ModSearchResultsScrollThumb => GetRequiredModControl<Border>(nameof(ModSearchResultsScrollThumb));
    private ScrollViewer ModDownloadQueueScrollViewer => GetRequiredModControl<ScrollViewer>(nameof(ModDownloadQueueScrollViewer));
    private ScrollViewer ModRecommendationRowsScrollViewer => GetRequiredModControl<ScrollViewer>(nameof(ModRecommendationRowsScrollViewer));
    private Border ModRecommendationRowsScrollTrack => GetRequiredModControl<Border>(nameof(ModRecommendationRowsScrollTrack));
    private Border ModRecommendationRowsScrollThumb => GetRequiredModControl<Border>(nameof(ModRecommendationRowsScrollThumb));

    internal bool IsLauncherDetailsContentCreated => LauncherDetailsContentHost.IsActive;

    internal BackgroundLayerKind ActiveBackgroundKind => BackgroundHost.ActiveKind;

    internal bool AreVideoBackgroundResourcesInitialized =>
        backgroundVideoView is not null
        || backgroundLibVlc is not null
        || backgroundMediaPlayer is not null
        || backgroundMedia is not null;

    public MainWindow()
    {
        InitializeComponent();
        BackgroundHost.Deactivate();
        Opened += MainWindow_OnOpened;
        Closed += MainWindow_OnClosed;
        SizeChanged += MainWindow_OnSizeChanged;
        PropertyChanged += MainWindow_PropertyChanged;
        ModWorkspaceHost.LayoutUpdated += ModWorkspaceHost_OnLayoutUpdated;
        DynamicBackgroundHost.SizeChanged += DynamicBackgroundHost_OnSizeChanged;
        DynamicBackgroundImage.PropertyChanged += DynamicBackgroundImage_OnPropertyChanged;
        memoryBudgetTimer.Tick += MemoryBudgetTimer_OnTick;
        LauncherSurface.AddHandler(
            InputElement.PointerWheelChangedEvent,
            LauncherSurface_OnPointerWheelChanged,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
    }

    private void MainWindow_OnOpened(object? sender, EventArgs e)
    {
        memoryBudgetTimer.Start();
        SyncWindowFrameShape();
        AttachViewModel(DataContext as MainWindowViewModel);
        SyncWorkspaceViews();
        SyncLabModuleViews();
        ResetSidebarNavigationItems(GetAllSidebarNavigationItems());
        ResetContentSurface(LauncherSurface);
        ResetContentSurface(SettingsSurface);
        ResetContentSurface(LabSurface);
        ResetContentSurface(DownloadCenterSurface);
        ResetLauncherDetailsPanelIfUnavailable();
        SyncSidebarLayout();
        UpdateModManagerPageHeights();
        Dispatcher.UIThread.Post(UpdateModManagerPageHeights, DispatcherPriority.Render);
        Dispatcher.UIThread.Post(ForgeRenderWarmup.Begin, DispatcherPriority.Background);
        SyncLaunchHighlightAnimation();
    }

    private void MainWindow_OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        SyncWindowFrameShape();
        SyncBackgroundLayer();
        UpdateVideoCropGeometry();
        UpdateModManagerPageHeights();
        ClampLauncherDetailsScrollOffset();
    }

    private void MainWindow_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == WindowStateProperty)
        {
            SyncWindowFrameShape();
            SyncLaunchHighlightAnimation();
        }
    }

    private void MainWindow_OnClosed(object? sender, EventArgs e)
    {
        memoryBudgetTimer.Stop();
        memoryBudgetTimer.Tick -= MemoryBudgetTimer_OnTick;
        ReleaseLauncherDetailsContent();
        if (subscribedViewModel is not null)
        {
            subscribedViewModel.PropertyChanged -= ViewModel_OnPropertyChanged;
            subscribedViewModel.MoreSettings.PropertyChanged -= MoreSettings_OnPropertyChanged;
            subscribedViewModel.MoreSettings.BackgroundCropRequested -= MoreSettings_OnBackgroundCropRequested;
            subscribedViewModel.NavigationTransitionRequested -= OnNavigationTransitionRequested;
            subscribedViewModel.SaveWorkspaceTransitionRequested -= OnSaveWorkspaceTransitionRequested;
            subscribedViewModel.LaunchWindowActionRequested -= OnLaunchWindowActionRequested;
            subscribedViewModel.PlayerProfiles.PropertyChanged -= PlayerProfiles_OnPropertyChanged;
            subscribedViewModel = null;
        }

        InstallVerificationDialogHost.Deactivate();
        DialogWorkspaceHost.Deactivate();
        PlayerProfileOverlayHost.Deactivate();
        DeactivateBackgroundLayer();
        ReleaseStartupStripeImage();
        DynamicBackgroundHost.SizeChanged -= DynamicBackgroundHost_OnSizeChanged;
        DynamicBackgroundImage.PropertyChanged -= DynamicBackgroundImage_OnPropertyChanged;
        CancelAndDispose(ref launcherPanelAnimationCancellation);
        CancelAndDispose(ref launcherToggleArrowBounceCancellation);
        CancelAndDispose(ref sidebarNavigationAnimationCancellation);
        CancelAndDispose(ref sidebarLayoutAnimationCancellation);
        CancelAndDispose(ref contentSurfaceAnimationCancellation);
        CancelAndDispose(ref closeWindowSpinCancellation);
        CancelAndDispose(ref startupFadeCancellation);
        CancelAndDispose(ref launchHighlightAnimationCancellation);
        ModWorkspaceHost.LayoutUpdated -= ModWorkspaceHost_OnLayoutUpdated;
        DetachModListWheelHandler();

        ReleaseLabModuleViews();
        StopWindowDrag();
    }

    public void PrepareStartupReveal()
    {
        EnsureStartupRevealTransforms();
        SyncStartupStripeImage();
        Opacity = 1;
        RootChrome.Opacity = 1;
        RootChromeScale.ScaleX = StartupRevealScale;
        RootChromeScale.ScaleY = StartupRevealScale;
        RootChromeTranslate.Y = 0;
        StartupStripeBackdrop.Opacity = 0;
        StartupImpactFlash.Opacity = 0;
    }

    public void PrepareStartupPreloadHidden()
    {
        EnsureStartupRevealTransforms();
        SyncStartupStripeImage();
        Opacity = 0;
        RootChrome.Opacity = 0;
        RootChromeScale.ScaleX = StartupRevealScale;
        RootChromeScale.ScaleY = StartupRevealScale;
        RootChromeTranslate.Y = 0;
        StartupStripeBackdrop.Opacity = 0;
        StartupImpactFlash.Opacity = 0;
    }

    public async Task FadeInFromStartupAsync()
    {
        startupFadeCancellation = ReplaceCancellation(ref startupFadeCancellation);
        var token = startupFadeCancellation.Token;

        EnsureStartupRevealTransforms();
        Opacity = 1;
        RootChrome.Opacity = 1;
        RootChromeScale.ScaleX = 1;
        RootChromeScale.ScaleY = 1;
        RootChromeTranslate.Y = 0;
        StartupStripeBackdrop.Opacity = 0;
        StartupImpactFlash.Opacity = 0;

        var startWidth = Width;
        var startHeight = Height;
        var startX = Position.X;
        var startY = Position.Y;
        var shouldResizeWindow = Math.Abs(startWidth - TargetWindowWidth) > 0.5
            || Math.Abs(startHeight - TargetWindowHeight) > 0.5;

        if (shouldResizeWindow)
        {
            Width = TargetWindowWidth;
            Height = TargetWindowHeight;
            Position = new PixelPoint(
                (int)(startX - ((TargetWindowWidth - startWidth) / 2)),
                (int)(startY - ((TargetWindowHeight - startHeight) / 2)));
        }

        try
        {
            await Task.Delay(1, token);
        }
        finally
        {
            ReleaseStartupStripeImage();
        }
    }

    private async Task AnimateStartupRevealWithResizeAsync(
        CancellationToken token,
        double startWidth,
        double startHeight,
        double startX,
        double startY,
        bool shouldResizeWindow)
    {
        var start = DateTimeOffset.UtcNow;
        var widthDelta = TargetWindowWidth - startWidth;
        var heightDelta = TargetWindowHeight - startHeight;

        while (true)
        {
            token.ThrowIfCancellationRequested();

            var elapsed = (DateTimeOffset.UtcNow - start).TotalMilliseconds;
            var t = Math.Clamp(elapsed / WindowResizeDurationMs, 0, 1);
            var reveal = EaseOutExpo(t);
            var flash = 1 - EaseOutCubic(t);

            RootChrome.Opacity = 1;
            RootChromeScale.ScaleX = 1;
            RootChromeScale.ScaleY = 1;
            RootChromeTranslate.Y = 0;
            StartupStripeBackdrop.Opacity = 0;
            StartupImpactFlash.Opacity = 0.16 * flash;

            if (shouldResizeWindow)
            {
                var currentWidth = Lerp(startWidth, TargetWindowWidth, reveal);
                var currentHeight = Lerp(startHeight, TargetWindowHeight, reveal);
                var currentX = startX - (widthDelta * reveal / 2);
                var currentY = startY - (heightDelta * reveal / 2);

                Width = currentWidth;
                Height = currentHeight;
                Position = new PixelPoint((int)currentX, (int)currentY);
            }

            if (t >= 1)
            {
                break;
            }

            await Task.Delay(16, token);
        }

        RootChrome.Opacity = 1;
        RootChromeScale.ScaleX = 1;
        RootChromeScale.ScaleY = 1;
        RootChromeTranslate.Y = 0;
        StartupStripeBackdrop.Opacity = 0;
        StartupImpactFlash.Opacity = 0;
    }

    private void EnsureStartupRevealTransforms()
    {
        RootChrome.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);

        if (RootChrome.RenderTransform is TransformGroup existing
            && existing.Children.Count == 2
            && existing.Children[0] is ScaleTransform
            && existing.Children[1] is TranslateTransform)
        {
            return;
        }

        var transforms = new TransformGroup();
        transforms.Children.Add(new ScaleTransform(1, 1));
        transforms.Children.Add(new TranslateTransform());
        RootChrome.RenderTransform = transforms;
    }

    private void SyncWindowFrameShape()
    {
        var isMaximized = WindowState == WindowState.Maximized;
        WindowFrame.CornerRadius = isMaximized ? 0 : WindowFrameCornerRadius;
        WindowFrame.BorderThickness = isMaximized ? new Thickness(0) : new Thickness(1);
    }

    private void AttachViewModel(MainWindowViewModel? viewModel)
    {
        if (subscribedViewModel == viewModel)
        {
            return;
        }

        if (subscribedViewModel is not null)
        {
            ReleaseLauncherDetailsContent();
            subscribedViewModel.PropertyChanged -= ViewModel_OnPropertyChanged;
            subscribedViewModel.MoreSettings.PropertyChanged -= MoreSettings_OnPropertyChanged;
            subscribedViewModel.MoreSettings.BackgroundCropRequested -= MoreSettings_OnBackgroundCropRequested;
            subscribedViewModel.NavigationTransitionRequested -= OnNavigationTransitionRequested;
            subscribedViewModel.SaveWorkspaceTransitionRequested -= OnSaveWorkspaceTransitionRequested;
            subscribedViewModel.LaunchWindowActionRequested -= OnLaunchWindowActionRequested;
            subscribedViewModel.PlayerProfiles.PropertyChanged -= PlayerProfiles_OnPropertyChanged;
        }

        subscribedViewModel = viewModel;

        if (subscribedViewModel is not null)
        {
            subscribedViewModel.PropertyChanged += ViewModel_OnPropertyChanged;
            subscribedViewModel.MoreSettings.PropertyChanged += MoreSettings_OnPropertyChanged;
            subscribedViewModel.MoreSettings.BackgroundCropRequested += MoreSettings_OnBackgroundCropRequested;
            subscribedViewModel.NavigationTransitionRequested += OnNavigationTransitionRequested;
            subscribedViewModel.SaveWorkspaceTransitionRequested += OnSaveWorkspaceTransitionRequested;
            subscribedViewModel.LaunchWindowActionRequested += OnLaunchWindowActionRequested;
            subscribedViewModel.PlayerProfiles.PropertyChanged += PlayerProfiles_OnPropertyChanged;
            ApplyAppearanceSettings(subscribedViewModel);
            SyncBackgroundLayer();
            SyncLaunchHighlightAnimation();
            SyncWorkspaceViews();
            SyncLabModuleViews();
            SyncDialogViews();
            SyncPlayerProfileOverlay();
        }
    }

    private void MoreSettings_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MoreSettingsViewModel.StaticBackgroundPath)
            or nameof(MoreSettingsViewModel.ShowStaticBackground)
            or nameof(MoreSettingsViewModel.DynamicBackgroundPath)
            or nameof(MoreSettingsViewModel.UseDynamicBackground)
            or nameof(MoreSettingsViewModel.ShowDynamicVideoBackground)
            or nameof(MoreSettingsViewModel.BackgroundCropMode)
            or nameof(MoreSettingsViewModel.BackgroundCropX)
            or nameof(MoreSettingsViewModel.BackgroundCropY)
            or nameof(MoreSettingsViewModel.BackgroundCropWidth)
            or nameof(MoreSettingsViewModel.BackgroundCropHeight))
        {
            Dispatcher.UIThread.Post(SyncBackgroundLayer, DispatcherPriority.Background);
            Dispatcher.UIThread.Post(UpdateDynamicBackgroundCrop, DispatcherPriority.Render);
            Dispatcher.UIThread.Post(UpdateVideoCropGeometry, DispatcherPriority.Render);
        }
    }

    private async void MoreSettings_OnBackgroundCropRequested(object? sender, EventArgs e)
    {
        var settings = subscribedViewModel?.MoreSettings;
        if (settings is null || !settings.HasSelectedBackground)
        {
            return;
        }

        string? previewPath = null;
        try
        {
            var sourcePath = settings.UseDynamicBackground
                ? settings.DynamicBackgroundPath
                : settings.StaticBackgroundPath;
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                return;
            }

            if (settings.UseDynamicBackground && MoreSettingsViewModel.IsVideoFile(sourcePath))
            {
                previewPath = await CreateVideoCropPreviewAsync();
                if (string.IsNullOrWhiteSpace(previewPath))
                {
                    return;
                }
            }

            var dialog = new BackgroundCropWindow(
                previewPath ?? sourcePath,
                settings.BackgroundCropX,
                settings.BackgroundCropY,
                settings.BackgroundCropWidth,
                settings.BackgroundCropHeight);
            var accepted = await dialog.ShowDialog<bool>(this);
            if (accepted)
            {
                await settings.SetBackgroundCropAsync(
                    dialog.CropX,
                    dialog.CropY,
                    dialog.CropWidth,
                    dialog.CropHeight);
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Logger.Warning(ex, "Unable to open background crop editor");
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(previewPath))
            {
                try
                {
                    File.Delete(previewPath);
                }
                catch
                {
                }
            }
        }
    }

    private async Task<string?> CreateVideoCropPreviewAsync()
    {
        for (var attempt = 0; attempt < 20 && backgroundMediaPlayer?.IsPlaying != true; attempt++)
        {
            await Task.Delay(50);
        }

        if (backgroundMediaPlayer is null || !backgroundMediaPlayer.IsPlaying)
        {
            return null;
        }

        var path = Path.Combine(Path.GetTempPath(), $"bohemix-background-preview-{Guid.NewGuid():N}.png");
        try
        {
            if (!backgroundMediaPlayer.TakeSnapshot(0, path, 0, 0))
            {
                return null;
            }

            for (var attempt = 0; attempt < 20; attempt++)
            {
                if (File.Exists(path) && new FileInfo(path).Length > 0)
                {
                    return path;
                }

                await Task.Delay(50);
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Logger.Debug(ex, "Unable to create video background crop preview");
        }

        try
        {
            File.Delete(path);
        }
        catch
        {
        }

        return null;
    }

    private void SyncBackgroundLayer()
    {
        var settings = subscribedViewModel?.MoreSettings;
        var targetKind = settings switch
        {
            { ShowDynamicVideoBackground: true } => BackgroundLayerKind.Video,
            { ShowDynamicImageBackground: true } => BackgroundLayerKind.Animated,
            { ShowStaticBackground: true } => BackgroundLayerKind.Static,
            _ => BackgroundLayerKind.Default
        };

        if (BackgroundHost.ActiveKind != targetKind)
        {
            DeactivateBackgroundLayer();
            switch (targetKind)
            {
                case BackgroundLayerKind.Static:
                    BackgroundHost.ActivateStatic(StaticBackgroundImage);
                    break;
                case BackgroundLayerKind.Animated:
                    BackgroundHost.ActivateAnimated(DynamicBackgroundHost);
                    break;
                case BackgroundLayerKind.Video:
                    BackgroundHost.ActivateVideo(BackgroundVideoHost);
                    break;
                default:
                    BackgroundHost.ActivateDefault(DefaultBackgroundImage);
                    break;
            }
        }

        switch (BackgroundHost.ActiveKind)
        {
            case BackgroundLayerKind.Default:
                SyncDefaultBackgroundImage();
                break;
            case BackgroundLayerKind.Static:
                SyncStaticBackgroundImage();
                break;
            case BackgroundLayerKind.Animated:
                Dispatcher.UIThread.Post(UpdateDynamicBackgroundCrop, DispatcherPriority.Render);
                break;
            case BackgroundLayerKind.Video:
                if (!SyncVideoBackground())
                {
                    DeactivateBackgroundLayer();
                    BackgroundHost.ActivateDefault(DefaultBackgroundImage);
                    SyncDefaultBackgroundImage();
                }
                break;
        }
    }

    private void DeactivateBackgroundLayer()
    {
        var activeKind = BackgroundHost.ActiveKind;
        BackgroundHost.Deactivate();

        switch (activeKind)
        {
            case BackgroundLayerKind.Default:
                ReleaseDefaultBackground();
                break;
            case BackgroundLayerKind.Static:
                ReleaseStaticBackground();
                break;
            case BackgroundLayerKind.Video:
                StopVideoBackground();
                break;
        }

        if (activeKind != BackgroundLayerKind.None)
        {
            SharedBitmapLeaseCache.TrimUnused();
        }
    }

    private void SyncDefaultBackgroundImage()
    {
        var targetSize = BackgroundHost.Bounds.Size;
        if (targetSize.Width <= 0 || targetSize.Height <= 0)
        {
            targetSize = ClientSize.Width > 0 && ClientSize.Height > 0
                ? ClientSize
                : new Size(TargetWindowWidth, TargetWindowHeight);
        }

        var decodeWidth = BackgroundBitmapSizing.CalculateDecodeWidth(
            DefaultBackgroundPixelSize,
            targetSize,
            RenderScaling,
            maximumDecodeWidth: BackgroundBitmapSizing.MaximumBackgroundDecodeWidth);
        if (decodeWidth <= 0
            || defaultBackgroundLease is not null && defaultBackgroundDecodeWidth >= decodeWidth)
        {
            return;
        }

        var next = SharedBitmapLeaseCache.Instance.Acquire(DefaultBackgroundSource, decodeWidth);
        if (next is null)
        {
            return;
        }

        DefaultBackgroundImage.Source = null;
        defaultBackgroundLease?.Dispose();
        defaultBackgroundLease = next;
        defaultBackgroundDecodeWidth = decodeWidth;
        DefaultBackgroundImage.Source = next.Bitmap;
    }

    private void ReleaseDefaultBackground()
    {
        DefaultBackgroundImage.Source = null;
        defaultBackgroundLease?.Dispose();
        defaultBackgroundLease = null;
        defaultBackgroundDecodeWidth = 0;
    }

    private void SyncStartupStripeImage()
    {
        var targetSize = ClientSize.Width > 0 && ClientSize.Height > 0
            ? ClientSize
            : new Size(TargetWindowWidth, TargetWindowHeight);
        var decodeWidth = BackgroundBitmapSizing.CalculateDecodeWidth(
            StartupStripePixelSize,
            targetSize,
            RenderScaling,
            maximumDecodeWidth: BackgroundBitmapSizing.MaximumStartupStripeDecodeWidth);
        if (decodeWidth <= 0
            || startupStripeLease is not null && startupStripeDecodeWidth >= decodeWidth)
        {
            return;
        }

        var next = SharedBitmapLeaseCache.Instance.Acquire(StartupStripeSource, decodeWidth);
        if (next is null)
        {
            return;
        }

        StartupStripeImage.Source = null;
        startupStripeLease?.Dispose();
        startupStripeLease = next;
        startupStripeDecodeWidth = decodeWidth;
        StartupStripeImage.Source = next.Bitmap;
    }

    private void ReleaseStartupStripeImage()
    {
        StartupStripeImage.Source = null;
        startupStripeLease?.Dispose();
        startupStripeLease = null;
        startupStripeDecodeWidth = 0;
        SharedBitmapLeaseCache.TrimUnused();
    }

    private void SyncStaticBackgroundImage()
    {
        var settings = subscribedViewModel?.MoreSettings;
        var path = settings?.ShowStaticBackground == true
            ? settings.StaticBackgroundPath
            : null;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            ReleaseStaticBackground();
            return;
        }

        try
        {
            if (!string.Equals(staticBackgroundSourcePath, path, StringComparison.OrdinalIgnoreCase)
                || staticBackgroundSourceSize.Width <= 0
                || staticBackgroundSourceSize.Height <= 0)
            {
                using var codec = SKCodec.Create(path);
                if (codec is null)
                {
                    ReleaseStaticBackground();
                    return;
                }

                staticBackgroundSourcePath = path;
                staticBackgroundSourceSize = new PixelSize(codec.Info.Width, codec.Info.Height);
            }

            var targetSize = StaticBackgroundImage.Bounds.Size;
            if (targetSize.Width <= 0 || targetSize.Height <= 0)
            {
                targetSize = ClientSize;
            }

            var decodeWidth = BackgroundBitmapSizing.CalculateDecodeWidth(
                staticBackgroundSourceSize,
                targetSize,
                RenderScaling,
                settings?.BackgroundCropWidth ?? 1,
                settings?.BackgroundCropHeight ?? 1,
                BackgroundBitmapSizing.MaximumBackgroundDecodeWidth);
            if (decodeWidth <= 0)
            {
                return;
            }

            if (string.Equals(staticBackgroundPath, path, StringComparison.OrdinalIgnoreCase)
                && staticBackgroundBitmap is not null
                && staticBackgroundDecodeWidth >= decodeWidth)
            {
                return;
            }

            Bitmap next;
            if (decodeWidth < staticBackgroundSourceSize.Width)
            {
                using var stream = File.OpenRead(path);
                next = Bitmap.DecodeToWidth(stream, decodeWidth, BitmapInterpolationMode.HighQuality);
            }
            else
            {
                next = new Bitmap(path);
            }

            StaticBackgroundImage.Source = null;
            staticBackgroundBitmap?.Dispose();
            staticBackgroundBitmap = next;
            staticBackgroundPath = path;
            staticBackgroundDecodeWidth = decodeWidth;
            StaticBackgroundImage.Source = next;
        }
        catch (Exception ex)
        {
            Serilog.Log.Logger.Warning(ex, "Unable to load static background {Path}", path);
        }
    }

    private void ReleaseStaticBackground()
    {
        StaticBackgroundImage.Source = null;
        staticBackgroundBitmap?.Dispose();
        staticBackgroundBitmap = null;
        staticBackgroundPath = null;
        staticBackgroundSourcePath = null;
        staticBackgroundSourceSize = default;
        staticBackgroundDecodeWidth = 0;
    }

    private bool SyncVideoBackground()
    {
        var settings = subscribedViewModel?.MoreSettings;
        var path = settings?.ShowDynamicVideoBackground == true
            ? settings.DynamicBackgroundPath
            : null;

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            StopVideoBackground();
            return false;
        }

        if (string.Equals(activeVideoBackgroundPath, path, StringComparison.OrdinalIgnoreCase)
            && backgroundMediaPlayer is not null
            && backgroundVideoView is not null)
        {
            return true;
        }

        StopVideoBackground();

        try
        {
            if (!EnsureLibVlcCoreInitialized())
            {
                return false;
            }

            backgroundLibVlc = new LibVLC("--no-audio", "--no-video-title-show");
            backgroundMediaPlayer = new MediaPlayer(backgroundLibVlc)
            {
                EnableHardwareDecoding = true,
                Mute = true,
                Volume = 0
            };
            backgroundVideoView = new VideoView
            {
                IsHitTestVisible = false,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
                MediaPlayer = backgroundMediaPlayer
            };
            BackgroundVideoHost.Content = backgroundVideoView;
            backgroundMedia = new Media(backgroundLibVlc, path, FromType.FromPath);
            backgroundMedia.AddOption(":input-repeat=65535");
            backgroundMedia.AddOption(":no-audio");
            activeVideoBackgroundPath = path;
            backgroundMediaPlayer.Playing += BackgroundMediaPlayer_OnPlaying;
            backgroundMediaPlayer.Play(backgroundMedia);
            Dispatcher.UIThread.Post(UpdateVideoCropGeometry, DispatcherPriority.Render);
            return true;
        }
        catch (Exception ex)
        {
            StopVideoBackground();
            Serilog.Log.Logger.Warning(ex, "Unable to play video background {Path}", path);
            return false;
        }
    }

    private void StopVideoBackground()
    {
        activeVideoBackgroundPath = null;
        if (backgroundVideoView is not null)
        {
            backgroundVideoView.MediaPlayer = null;
        }

        BackgroundVideoHost.Content = null;

        if (backgroundMediaPlayer is not null)
        {
            backgroundMediaPlayer.Playing -= BackgroundMediaPlayer_OnPlaying;
        }

        try
        {
            backgroundMediaPlayer?.Stop();
        }
        catch
        {
        }

        backgroundMedia?.Dispose();
        backgroundMedia = null;
        backgroundMediaPlayer?.Dispose();
        backgroundMediaPlayer = null;
        backgroundLibVlc?.Dispose();
        backgroundLibVlc = null;
        backgroundVideoView = null;
    }

    private static bool EnsureLibVlcCoreInitialized()
    {
        lock (LibVlcInitializationGate)
        {
            if (isLibVlcCoreInitializationAttempted)
            {
                return isLibVlcCoreInitialized;
            }

            isLibVlcCoreInitializationAttempted = true;
            try
            {
                LibVLCSharp.Shared.Core.Initialize();
                isLibVlcCoreInitialized = true;
            }
            catch (Exception ex)
            {
                Serilog.Log.Logger.Warning(ex, "LibVLC could not be initialized; video backgrounds will be unavailable");
            }

            return isLibVlcCoreInitialized;
        }
    }

    private void UpdateVideoCropGeometry()
    {
        if (backgroundMediaPlayer is null || ClientSize.Width <= 0 || ClientSize.Height <= 0)
        {
            return;
        }

        var targetWidth = Math.Max(1, (int)Math.Round(ClientSize.Width));
        var targetHeight = Math.Max(1, (int)Math.Round(ClientSize.Height));
        var settings = subscribedViewModel?.MoreSettings;
        var cropX = settings?.BackgroundCropX ?? 0;
        var cropY = settings?.BackgroundCropY ?? 0;
        var cropWidth = settings?.BackgroundCropWidth ?? 1;
        var cropHeight = settings?.BackgroundCropHeight ?? 1;

        try
        {
            uint sourceWidth = 0;
            uint sourceHeight = 0;
            if (!backgroundMediaPlayer.Size(0, ref sourceWidth, ref sourceHeight)
                || sourceWidth == 0 || sourceHeight == 0)
            {
                var fallbackDivisor = GreatestCommonDivisor(targetWidth, targetHeight);
                backgroundMediaPlayer.CropGeometry = $"{targetWidth / fallbackDivisor}:{targetHeight / fallbackDivisor}";
                return;
            }

            cropWidth = Math.Clamp(cropWidth, 0.01, 1);
            cropHeight = Math.Clamp(cropHeight, 0.01, 1);
            cropX = Math.Clamp(cropX, 0, 1 - cropWidth);
            cropY = Math.Clamp(cropY, 0, 1 - cropHeight);

            int cropWidthPixels;
            int cropHeightPixels;
            int cropLeftPixels;
            int cropTopPixels;
            var hasFreeCrop = cropWidth < 0.999 || cropHeight < 0.999 || cropX > 0 || cropY > 0;
            if (hasFreeCrop)
            {
                cropWidthPixels = Math.Clamp((int)Math.Round(sourceWidth * cropWidth), 1, (int)sourceWidth);
                cropHeightPixels = Math.Clamp((int)Math.Round(sourceHeight * cropHeight), 1, (int)sourceHeight);
                cropLeftPixels = Math.Clamp((int)Math.Round(sourceWidth * cropX), 0, (int)sourceWidth - cropWidthPixels);
                cropTopPixels = Math.Clamp((int)Math.Round(sourceHeight * cropY), 0, (int)sourceHeight - cropHeightPixels);
            }
            else
            {
                var sourceAspect = (double)sourceWidth / sourceHeight;
                var targetAspect = (double)targetWidth / targetHeight;
                cropWidthPixels = (int)sourceWidth;
                cropHeightPixels = (int)sourceHeight;
                cropLeftPixels = 0;
                cropTopPixels = 0;

                if (sourceAspect > targetAspect)
                {
                    cropWidthPixels = Math.Clamp((int)Math.Round(sourceHeight * targetAspect), 1, (int)sourceWidth);
                    cropLeftPixels = (int)Math.Round(((int)sourceWidth - cropWidthPixels) / 2d);
                    if (settings?.BackgroundCropMode == 3)
                    {
                        cropLeftPixels = 0;
                    }
                    else if (settings?.BackgroundCropMode == 4)
                    {
                        cropLeftPixels = (int)sourceWidth - cropWidthPixels;
                    }
                }
                else if (sourceAspect < targetAspect)
                {
                    cropHeightPixels = Math.Clamp((int)Math.Round(sourceWidth / targetAspect), 1, (int)sourceHeight);
                    cropTopPixels = (int)Math.Round(((int)sourceHeight - cropHeightPixels) / 2d);
                    if (settings?.BackgroundCropMode == 1)
                    {
                        cropTopPixels = 0;
                    }
                    else if (settings?.BackgroundCropMode == 2)
                    {
                        cropTopPixels = (int)sourceHeight - cropHeightPixels;
                    }
                }
            }

            backgroundMediaPlayer.CropGeometry = $"{cropWidthPixels}x{cropHeightPixels}+{cropLeftPixels}+{cropTopPixels}";
            var divisor = GreatestCommonDivisor(targetWidth, targetHeight);
            backgroundMediaPlayer.AspectRatio = $"{targetWidth / divisor}:{targetHeight / divisor}";
        }
        catch
        {
        }
    }

    private void BackgroundMediaPlayer_OnPlaying(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(UpdateVideoCropGeometry, DispatcherPriority.Render);
    }

    private void DynamicBackgroundHost_OnSizeChanged(object? sender, SizeChangedEventArgs e) => UpdateDynamicBackgroundCrop();

    private void DynamicBackgroundImage_OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Image.SourceProperty)
        {
            UpdateDynamicBackgroundCrop();
        }
    }

    private void UpdateDynamicBackgroundCrop()
    {
        if (DynamicBackgroundHost.Bounds.Width <= 0 || DynamicBackgroundHost.Bounds.Height <= 0)
        {
            return;
        }

        var settings = subscribedViewModel?.MoreSettings;
        var cropWidth = Math.Clamp(settings?.BackgroundCropWidth ?? 1, 0.01, 1);
        var cropHeight = Math.Clamp(settings?.BackgroundCropHeight ?? 1, 0.01, 1);
        var cropX = Math.Clamp(settings?.BackgroundCropX ?? 0, 0, 1 - cropWidth);
        var cropY = Math.Clamp(settings?.BackgroundCropY ?? 0, 0, 1 - cropHeight);
        var width = DynamicBackgroundHost.Bounds.Width;
        var height = DynamicBackgroundHost.Bounds.Height;

        if (cropWidth < 0.999 || cropHeight < 0.999 || cropX > 0 || cropY > 0)
        {
            DynamicBackgroundImage.Width = width / cropWidth;
            DynamicBackgroundImage.Height = height / cropHeight;
            DynamicBackgroundImage.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
            DynamicBackgroundImage.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
            DynamicBackgroundImage.Stretch = Stretch.Fill;
            DynamicBackgroundTranslate.X = -cropX * DynamicBackgroundImage.Width;
            DynamicBackgroundTranslate.Y = -cropY * DynamicBackgroundImage.Height;
            return;
        }

        if (DynamicBackgroundImage.Source is not { Size.Width: > 0, Size.Height: > 0 } source)
        {
            DynamicBackgroundImage.Width = double.NaN;
            DynamicBackgroundImage.Height = double.NaN;
            DynamicBackgroundImage.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
            DynamicBackgroundImage.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
            DynamicBackgroundImage.Stretch = Stretch.UniformToFill;
            DynamicBackgroundTranslate.X = 0;
            DynamicBackgroundTranslate.Y = 0;
            return;
        }

        var scale = Math.Max(width / source.Size.Width, height / source.Size.Height);
        var displayWidth = source.Size.Width * scale;
        var displayHeight = source.Size.Height * scale;
        var offsetX = -(displayWidth - width) / 2;
        var offsetY = -(displayHeight - height) / 2;
        if (settings?.BackgroundCropMode == 1)
        {
            offsetY = 0;
        }
        else if (settings?.BackgroundCropMode == 2)
        {
            offsetY = height - displayHeight;
        }
        else if (settings?.BackgroundCropMode == 3)
        {
            offsetX = 0;
        }
        else if (settings?.BackgroundCropMode == 4)
        {
            offsetX = width - displayWidth;
        }

        DynamicBackgroundImage.Width = displayWidth;
        DynamicBackgroundImage.Height = displayHeight;
        DynamicBackgroundImage.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        DynamicBackgroundImage.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
        DynamicBackgroundImage.Stretch = Stretch.Fill;
        DynamicBackgroundTranslate.X = offsetX;
        DynamicBackgroundTranslate.Y = offsetY;
    }

    private static int GreatestCommonDivisor(int left, int right)
    {
        while (right != 0)
        {
            (left, right) = (right, left % right);
        }

        return Math.Max(1, left);
    }

    private void ViewModel_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is
            nameof(MainWindowViewModel.IsSettingsView) or
            nameof(MainWindowViewModel.IsModManagerVisible) or
            nameof(MainWindowViewModel.IsSavesActive))
        {
            SyncWorkspaceViews();
        }

        if (e.PropertyName is
            nameof(MainWindowViewModel.IsModListPageVisible) or
            nameof(MainWindowViewModel.IsModDownloadPageVisible) or
            nameof(MainWindowViewModel.IsModRecommendationPageVisible) or
            nameof(MainWindowViewModel.IsLauncherView))
        {
            Dispatcher.UIThread.Post(UpdateModManagerPageHeights, DispatcherPriority.Background);
            Dispatcher.UIThread.Post(UpdateModManagerPageHeights, DispatcherPriority.Render);
        }

        if (e.PropertyName == nameof(MainWindowViewModel.IsModRecommendationPageVisible)
            && subscribedViewModel?.IsModRecommendationPageVisible == true)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (FindModControl<ScrollViewer>(nameof(ModRecommendationRowsScrollViewer)) is { } scrollViewer)
                {
                    SetScrollViewerVerticalOffset(scrollViewer, 0);
                    SyncModRecommendationRowsScrollThumbFromScrollViewer();
                }
            }, DispatcherPriority.Render);
        }

        if (e.PropertyName is
            nameof(MainWindowViewModel.IsModManagerVisible) or
            nameof(MainWindowViewModel.IsLauncherDashboardVisible))
        {
            ResetLauncherDetailsPanelIfUnavailable();
        }

        if (e.PropertyName is
            nameof(MainWindowViewModel.SidebarWidth) or
            nameof(MainWindowViewModel.IsSidebarExpanded))
        {
            SyncSidebarLayout();
            Dispatcher.UIThread.Post(UpdateModManagerPageHeights, DispatcherPriority.Background);
        }

        if (sender is MainWindowViewModel viewModel
            && e.PropertyName is nameof(MainWindowViewModel.EnableAcrylicEffects))
        {
            ApplyAppearanceSettings(viewModel);
        }

        if (e.PropertyName is
            nameof(MainWindowViewModel.IsLaunchReady) or
            nameof(MainWindowViewModel.ReduceMotion) or
            nameof(MainWindowViewModel.IsLauncherDashboardVisible))
        {
            SyncLaunchHighlightAnimation();
        }

        if (e.PropertyName is
            nameof(MainWindowViewModel.IsLabView) or
            nameof(MainWindowViewModel.IsLabAlchemyActive) or
            nameof(MainWindowViewModel.IsLabForgingActive))
        {
            SyncLabModuleViews();
        }

        if (e.PropertyName is
            nameof(MainWindowViewModel.IsInstallVerificationDialogOpen) or
            nameof(MainWindowViewModel.IsNexusModFilePickerVisible) or
            nameof(MainWindowViewModel.IsModPackPreflightOpen) or
            nameof(MainWindowViewModel.IsLocalModPackImportOpen) or
            nameof(MainWindowViewModel.IsNexusAccountBindingDialogOpen) or
            nameof(MainWindowViewModel.IsSteamAccountBindingDialogOpen))
        {
            SyncDialogViews();
        }
    }

    private void PlayerProfiles_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is
            nameof(PlayerProfileManagerViewModel.IsPlayerMenuOpen) or
            nameof(PlayerProfileManagerViewModel.IsAnyModalOpen))
        {
            SyncPlayerProfileOverlay();
        }
    }

    private void SyncDialogViews()
    {
        var viewModel = subscribedViewModel;
        var showInstallVerification = viewModel?.IsInstallVerificationDialogOpen == true;
        if (showInstallVerification)
        {
            InstallVerificationDialogHost.Activate(viewModel!);
        }
        else if (InstallVerificationDialogHost.Deactivate())
        {
            SharedBitmapLeaseCache.TrimUnused();
        }

        var shouldShow = viewModel is not null
            && (viewModel.IsNexusModFilePickerVisible
                || viewModel.IsModPackPreflightOpen
                || viewModel.IsLocalModPackImportOpen
                || viewModel.IsNexusAccountBindingDialogOpen
                || viewModel.IsSteamAccountBindingDialogOpen);

        if (shouldShow)
        {
            DialogWorkspaceHost.Activate(viewModel!);
        }
        else if (DialogWorkspaceHost.Deactivate())
        {
            SharedBitmapLeaseCache.TrimUnused();
        }
    }

    private void SyncPlayerProfileOverlay()
    {
        var profiles = subscribedViewModel?.PlayerProfiles;
        if (profiles is { IsPlayerMenuOpen: true } || profiles is { IsAnyModalOpen: true })
        {
            PlayerProfileOverlayHost.Activate(profiles);
        }
        else if (PlayerProfileOverlayHost.Deactivate())
        {
            SharedBitmapLeaseCache.TrimUnused();
        }
    }

    private void SyncWorkspaceViews()
    {
        var viewModel = subscribedViewModel;
        var showMods = viewModel?.IsModManagerVisible == true;
        var releasedWorkspace = false;

        if (!showMods && ModWorkspaceHost.IsActive)
        {
            isAwaitingModWorkspaceLayout = false;
            SaveModWorkspaceState();
            DetachModListWheelHandler();
            ModWorkspaceHost.Deactivate();
            releasedWorkspace = true;
        }

        releasedWorkspace |= SaveWorkspaceHost.IsActive && viewModel?.IsSavesActive != true;
        releasedWorkspace |= SettingsWorkspaceHost.IsActive && viewModel?.IsSettingsView != true;
        SyncWorkspaceHost(SaveWorkspaceHost, viewModel?.IsSavesActive == true ? viewModel : null);
        var settingsActivated = SyncWorkspaceHost(
            SettingsWorkspaceHost,
            viewModel?.IsSettingsView == true ? viewModel : null);
        if (settingsActivated)
        {
            _ = viewModel!.PlayerProfiles.RefreshNexusAccountAsync();
        }

        if (showMods && !ModWorkspaceHost.IsActive)
        {
            isAwaitingModWorkspaceLayout = true;
            ModWorkspaceHost.Activate(viewModel!);
            Dispatcher.UIThread.Post(() =>
            {
                RestoreModWorkspaceState();
                UpdateModManagerPageHeights();
            }, DispatcherPriority.Render);
        }

        if (releasedWorkspace)
        {
            SharedBitmapLeaseCache.TrimUnused();
        }
    }

    private void ModWorkspaceHost_OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (!isAwaitingModWorkspaceLayout || !ModWorkspaceHost.IsActive)
        {
            return;
        }

        RestoreModWorkspaceState();
        if (TryUpdateModManagerPageHeights())
        {
            isAwaitingModWorkspaceLayout = false;
        }
    }

    private static bool SyncWorkspaceHost(DisposableWorkspaceHost host, object? content)
    {
        if (content is null)
        {
            host.Deactivate();
            return false;
        }

        return host.Activate(content);
    }

    private void SaveModWorkspaceState()
    {
        modListPageOffset = FindModControl<ScrollViewer>("ModListPageScrollViewer")?.Offset.Y ?? modListPageOffset;
        modDownloadPageOffset = FindModControl<ScrollViewer>("ModDownloadPageScrollViewer")?.Offset.Y ?? modDownloadPageOffset;
        modRecommendationPageOffset = FindModControl<ScrollViewer>("ModRecommendationRowsScrollViewer")?.Offset.Y ?? modRecommendationPageOffset;
    }

    private void RestoreModWorkspaceState()
    {
        RestoreScrollOffset("ModListPageScrollViewer", modListPageOffset);
        RestoreScrollOffset("ModDownloadPageScrollViewer", modDownloadPageOffset);
        RestoreScrollOffset("ModRecommendationRowsScrollViewer", modRecommendationPageOffset);
    }

    private T? FindModControl<T>(string name) where T : Control =>
        ModWorkspaceHost.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name);

    private ScrollViewer? FindModListRowsScrollViewer() =>
        FindModControl<Border>(nameof(ModListRowsScrollHost))?.FindDescendantOfType<ScrollViewer>();

    private T GetRequiredModControl<T>(string name) where T : Control =>
        FindModControl<T>(name) ?? throw new InvalidOperationException($"Mod workspace control '{name}' is unavailable.");

    private void RestoreScrollOffset(string name, double offset)
    {
        if (FindModControl<ScrollViewer>(name) is { } scrollViewer)
        {
            SetScrollViewerVerticalOffset(scrollViewer, offset);
        }
    }

    private void SyncLabModuleViews()
    {
        var viewModel = subscribedViewModel;
        var showAlchemy = viewModel?.IsLabView == true && viewModel.IsLabAlchemyActive;
        var showForge = viewModel?.IsLabView == true && viewModel.IsLabForgingActive;
        var releasedModule = false;

        if (showAlchemy)
        {
            if (AlchemyModuleHost.Content is null)
            {
                var alchemy = viewModel!.AlchemyWorkshop;
                alchemy.ExitRequested -= LabModule_OnExitRequested;
                alchemy.ExitRequested += LabModule_OnExitRequested;
                alchemy.Activate();
                AlchemyModuleHost.Content = new AlchemyWorkshopView
                {
                    DataContext = alchemy
                };
            }
        }
        else if (AlchemyModuleHost.Content is not null)
        {
            AlchemyModuleHost.Content = null;
            if (viewModel?.CreatedAlchemyWorkshop is { } alchemy)
            {
                alchemy.ExitRequested -= LabModule_OnExitRequested;
                alchemy.Deactivate();
            }
            releasedModule = true;
        }

        if (showForge)
        {
            if (ForgeModuleHost.Content is null)
            {
                viewModel!.ForgeWorkshop.ExitRequested -= LabModule_OnExitRequested;
                viewModel.ForgeWorkshop.ExitRequested += LabModule_OnExitRequested;
                ForgeModuleHost.Content = new ForgeWorkshopView
                {
                    DataContext = viewModel.ForgeWorkshop
                };
            }
        }
        else if (ForgeModuleHost.Content is not null)
        {
            (ForgeModuleHost.Content as ForgeWorkshopView)?.PrepareForRemoval();
            ForgeModuleHost.Content = null;
            if (viewModel?.CreatedForgeWorkshop is { } forge)
            {
                forge.ExitRequested -= LabModule_OnExitRequested;
                forge.Deactivate();
            }
            releasedModule = true;
        }

        if (!showAlchemy && !showForge && releasedModule)
        {
            SharedBitmapLeaseCache.TrimUnused();
        }
    }

    private void LabModule_OnExitRequested(object? sender, EventArgs e)
    {
        subscribedViewModel?.NavigateCommand.Execute("Dashboard");
    }

    private void ReleaseLabModuleViews()
    {
        (ForgeModuleHost.Content as ForgeWorkshopView)?.PrepareForRemoval();
        AlchemyModuleHost.Content = null;
        ForgeModuleHost.Content = null;
        if (subscribedViewModel?.CreatedAlchemyWorkshop is { } alchemy)
        {
            alchemy.ExitRequested -= LabModule_OnExitRequested;
            alchemy.Deactivate();
        }
        if (subscribedViewModel?.CreatedForgeWorkshop is { } forge)
        {
            forge.ExitRequested -= LabModule_OnExitRequested;
            forge.Deactivate();
        }
    }

    private void SyncLaunchHighlightAnimation()
    {
        CancelAndDispose(ref launchHighlightAnimationCancellation);
        ResetLaunchHighlights();

        if (subscribedViewModel is not { IsLaunchReady: true, ReduceMotion: false, IsLauncherDashboardVisible: true }
            || !IsVisible
            || WindowState == WindowState.Minimized)
        {
            return;
        }

        launchHighlightAnimationCancellation = ReplaceCancellation(ref launchHighlightAnimationCancellation);
        _ = AnimateLaunchHighlightsAsync(launchHighlightAnimationCancellation.Token);
    }

    private async Task AnimateLaunchHighlightsAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(320, token);

            while (true)
            {
                var start = DateTimeOffset.UtcNow;
                while (true)
                {
                    token.ThrowIfCancellationRequested();

                    var elapsed = (DateTimeOffset.UtcNow - start).TotalMilliseconds;
                    var t = Math.Clamp(elapsed / LaunchHighlightSweepDurationMs, 0, 1);
                    var curve = SmoothStep(t);
                    var opacity = Math.Sin(Math.PI * t) * 0.96;

                    UpdateLaunchHighlight(
                        HeroLaunchButtonHost,
                        HeroLaunchHighlight,
                        HeroLaunchHighlightTranslate,
                        curve,
                        opacity);

                    if (t >= 1)
                    {
                        break;
                    }

                    await Task.Delay(LaunchHighlightFrameDelayMs, token);
                }

                ResetLaunchHighlights();
                await Task.Delay(LaunchHighlightRestDurationMs, token);
            }
        }
        catch (OperationCanceledException)
        {
            ResetLaunchHighlights();
        }
    }

    private static void UpdateLaunchHighlight(
        Control host,
        Control highlight,
        TranslateTransform translate,
        double progress,
        double opacity)
    {
        var highlightWidth = highlight.Bounds.Width > 0 ? highlight.Bounds.Width : highlight.Width;
        translate.X = Lerp(-highlightWidth, host.Bounds.Width + highlightWidth, progress);
        highlight.Opacity = opacity;
    }

    private void ResetLaunchHighlights()
    {
        HeroLaunchHighlight.Opacity = 0;
        HeroLaunchHighlightTranslate.X = -HeroLaunchHighlight.Width - 28;
    }

    private void OnNavigationTransitionRequested()
    {
        QueueSidebarNavigationAnimation();
    }

    private void OnSaveWorkspaceTransitionRequested()
    {
        QueueContentSurfaceAnimation();
    }

    private void OnLaunchWindowActionRequested(LaunchWindowAction action)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (action == LaunchWindowAction.Close)
            {
                Close();
                return;
            }

            WindowState = WindowState.Minimized;
        });
    }

    private void ApplyAppearanceSettings(MainWindowViewModel viewModel)
    {
        WindowFrame.Background = new SolidColorBrush(Color.Parse(
            viewModel.EnableAcrylicEffects && !memoryEffectsBudget.IsSuppressed
            ? "#050A14"
            : "#FF050A14"));
    }

    private void MemoryBudgetTimer_OnTick(object? sender, EventArgs e)
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var wasSuppressed = memoryEffectsBudget.IsSuppressed;
        _ = memoryEffectsBudget.Update(process.WorkingSet64, DateTimeOffset.UtcNow);
        if (wasSuppressed != memoryEffectsBudget.IsSuppressed)
        {
            ApplyMemoryEffectsBudget();
        }
    }

    private void ApplyMemoryEffectsBudget()
    {
        if (memoryEffectsBudget.IsSuppressed)
        {
            // Effects are disabled at this point, so any bitmap without an
            // active visual lease can be released immediately as well.
            SharedBitmapLeaseCache.TrimUnused(0);
        }

        if (subscribedViewModel is not null)
        {
            ApplyAppearanceSettings(subscribedViewModel);
        }

        if (backgroundMediaPlayer is not null)
        {
            try
            {
                backgroundMediaPlayer.SetPause(memoryEffectsBudget.IsSuppressed);
            }
            catch
            {
            }
        }
    }

    private void SidebarToggleButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            _ = AnimateSidebarLayoutToAsync(viewModel.IsSidebarExpanded
                ? MainWindowViewModel.SidebarCollapsedWidth
                : MainWindowViewModel.SidebarComfortWidth);
        }
    }

    private void NexusModResult_OnTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Control sourceControl && IsCardActionSource(sourceControl))
        {
            return;
        }

        if (sender is Control { DataContext: NexusModSearchRowViewModel mod }
            && TrySelectNexusModDetails(mod))
        {
            e.Handled = true;
        }
        else if (sender is Control { DataContext: ModPackRowViewModel modPack }
                 && DataContext is MainWindowViewModel viewModel)
        {
            viewModel.SelectedModPack = modPack;
            viewModel.IsModPackDetailVisible = true;
            e.Handled = true;
        }
        else if (sender is Control { DataContext: ModRecommendationRowViewModel recommendation }
                 && TrySelectModRecommendation(recommendation))
        {
            e.Handled = true;
        }
    }

    private void ModListRow_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: ModListItemViewModel mod } row
            || e.Source is Control sourceControl && IsCardActionSource(sourceControl)
            || !e.GetCurrentPoint(row).Properties.IsLeftButtonPressed
            || DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        viewModel.SelectedMod = mod;
        modDragCandidate = mod;
        modDragPointer = e.Pointer;
        modDragStartPoint = e.GetPosition(row);
        isModDragActive = false;
        e.Pointer.Capture(row);
    }

    private void ModListRow_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not Control row
            || modDragCandidate is null
            || !ReferenceEquals(modDragPointer, e.Pointer)
            || isModDragActive)
        {
            return;
        }

        var currentPoint = e.GetPosition(row);
        var delta = currentPoint - modDragStartPoint;
        if (Math.Abs(delta.X) < 8 && Math.Abs(delta.Y) < 8)
        {
            return;
        }

        isModDragActive = true;
        var mod = modDragCandidate;
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(ModDragDataFormat, mod.Id));
        _ = RunModRowDragAsync(e, data);
        e.Handled = true;
    }

    private async Task RunModRowDragAsync(PointerEventArgs triggerEvent, IDataTransfer data)
    {
        try
        {
            await DragDrop.DoDragDropAsync(triggerEvent, data, DragDropEffects.Move);
        }
        finally
        {
            ClearModRowDragState(triggerEvent.Pointer);
        }
    }

    private void ModListRow_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (ReferenceEquals(modDragPointer, e.Pointer) && !isModDragActive)
        {
            ClearModRowDragState(e.Pointer);
        }
    }

    private void ModListRow_OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) =>
        ClearModRowDragState(modDragPointer);

    private void ClearModRowDragState(IPointer? pointer)
    {
        if (pointer is not null)
        {
            pointer.Capture(null);
        }

        modDragCandidate = null;
        modDragPointer = null;
        isModDragActive = false;
    }

    private void ModListRow_OnDragOver(object? sender, DragEventArgs e)
    {
        AutoScrollModListRows(e);

        if (sender is not Control { DataContext: ModListItemViewModel target }
            || DataContext is not MainWindowViewModel viewModel
            || !TryGetDraggedMod(e.DataTransfer, viewModel, out var dragged)
            || ReferenceEquals(dragged, target)
            || FindInstalledModGroup(sender as Visual) is not { } targetGroup
            || !CanDropModOnRow(dragged, targetGroup))
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        ClearModOrderDropTargets(viewModel, target);
        target.IsOrderDropTargetActive = true;
        e.DragEffects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void ModListRow_OnDragLeave(object? sender, DragEventArgs e)
    {
        if (sender is Control { DataContext: ModListItemViewModel target })
        {
            target.IsOrderDropTargetActive = false;
        }
    }

    private async void ModListRow_OnDrop(object? sender, DragEventArgs e)
    {
        if (sender is not Control { DataContext: ModListItemViewModel target } row
            || DataContext is not MainWindowViewModel viewModel
            || !TryGetDraggedMod(e.DataTransfer, viewModel, out var dragged)
            || ReferenceEquals(dragged, target)
            || FindInstalledModGroup(row) is not { } targetGroup
            || !CanDropModOnRow(dragged, targetGroup))
        {
            return;
        }

        var sourceIndex = viewModel.ModRows.IndexOf(dragged);
        var targetIndex = viewModel.ModRows.IndexOf(target);
        if (sourceIndex < 0 || targetIndex < 0)
        {
            return;
        }

        var placeAfter = e.GetPosition(row).Y >= row.Bounds.Height / 2;
        var dropIndex = CalculateModDropTargetIndex(sourceIndex, targetIndex, placeAfter);
        e.Handled = true;
        ClearModOrderDropTargets(viewModel);
        await viewModel.MoveModFromDropAsync(dragged, dropIndex, targetGroup);
    }

    internal static int CalculateModDropTargetIndex(int sourceIndex, int targetIndex, bool placeAfter)
    {
        var insertionIndex = targetIndex + (placeAfter ? 1 : 0);
        if (sourceIndex < insertionIndex)
        {
            insertionIndex--;
        }

        return Math.Max(0, insertionIndex);
    }

    private static bool CanDropModOnRow(
        ModListItemViewModel dragged,
        InstalledModGroupViewModel targetGroup) =>
        targetGroup.Items.Any(item => ReferenceEquals(item, dragged)) || targetGroup.CanAcceptDrop(dragged);

    private static InstalledModGroupViewModel? FindInstalledModGroup(Visual? visual)
    {
        for (var current = visual; current is not null; current = current.GetVisualParent())
        {
            if (current is StyledElement { DataContext: InstalledModGroupViewModel group })
            {
                return group;
            }
        }

        return null;
    }

    private static void ClearModOrderDropTargets(
        MainWindowViewModel viewModel,
        ModListItemViewModel? except = null)
    {
        foreach (var row in viewModel.ModRows)
        {
            if (!ReferenceEquals(row, except))
            {
                row.IsOrderDropTargetActive = false;
            }
        }
    }

    private void ModGroup_OnDragOver(object? sender, DragEventArgs e)
    {
        AutoScrollModListRows(e);

        if (sender is not Control { DataContext: InstalledModGroupViewModel group }
            || DataContext is not MainWindowViewModel viewModel
            || viewModel.IsModGroupNameSaving
            || !TryGetDraggedMod(e.DataTransfer, viewModel, out var mod)
            || !group.CanAcceptDrop(mod))
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        group.IsDropTargetActive = true;
        e.DragEffects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void ModGroup_OnDragLeave(object? sender, DragEventArgs e)
    {
        if (sender is Control { DataContext: InstalledModGroupViewModel group })
        {
            group.IsDropTargetActive = false;
        }
    }

    private void ModGroup_OnDrop(object? sender, DragEventArgs e)
    {
        if (sender is not Control { DataContext: InstalledModGroupViewModel group }
            || DataContext is not MainWindowViewModel viewModel
            || !TryGetDraggedMod(e.DataTransfer, viewModel, out var mod)
            || !group.CanAcceptDrop(mod))
        {
            return;
        }

        group.IsDropTargetActive = false;
        viewModel.MoveModToGroupCommand.Execute(new ModGroupAssignmentRequest(mod, group));
        e.Handled = true;
    }

    private static bool TryGetDraggedMod(
        IDataTransfer data,
        MainWindowViewModel viewModel,
        out ModListItemViewModel mod)
    {
        var modId = data.TryGetValue(ModDragDataFormat);
        var value = viewModel.ModRows.FirstOrDefault(item =>
            string.Equals(item.Id, modId, StringComparison.OrdinalIgnoreCase));
        if (value is not null)
        {
            mod = value;
            return true;
        }

        mod = null!;
        return false;
    }

    private void ModGroupAddButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: InstalledModGroupViewModel group } button
            || DataContext is not MainWindowViewModel viewModel
            || !group.IsCustom)
        {
            return;
        }

        var assignedIds = group.Items
            .Select(item => item.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var flyout = new MenuFlyout();
        var availableMods = viewModel.ModRows
            .Where(item => !assignedIds.Contains(item.Id))
            .ToArray();

        if (availableMods.Length == 0)
        {
            flyout.Items.Add(new MenuItem
            {
                Header = viewModel.NoModsAvailableForGroupText,
                IsEnabled = false
            });
        }
        else
        {
            foreach (var mod in availableMods)
            {
                var item = new MenuItem
                {
                    Header = mod.DisplayName
                };
                ToolTip.SetTip(item, mod.Id);
                item.Click += (_, _) =>
                    viewModel.AddModToGroupCommand.Execute(new ModGroupAssignmentRequest(mod, group));
                flyout.Items.Add(item);
            }
        }

        flyout.ShowAt(button);
        e.Handled = true;
    }

    private void ModGroupTitle_OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not TextBlock title
            || title.DataContext is not InstalledModGroupViewModel group
            || DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        viewModel.OpenModGroupNameEditorCommand.Execute(group);
        Dispatcher.UIThread.Post(
            () =>
            {
                var editor = title.GetVisualParent()?
                    .GetVisualDescendants()
                    .OfType<TextBox>()
                    .FirstOrDefault(textBox => textBox.IsVisible);
                editor?.Focus();
                editor?.SelectAll();
            },
            DispatcherPriority.Render);
        e.Handled = true;
    }

    private async void ModGroupNameEditor_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            viewModel.CancelModGroupNameEditorCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && viewModel.SaveModGroupNameCommand.CanExecute(null))
        {
            e.Handled = true;
            await viewModel.SaveModGroupNameCommand.ExecuteAsync(null);
        }
    }

    private async void ModGroupNameEditor_OnLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: InstalledModGroupViewModel { IsNameEditing: true } }
            && DataContext is MainWindowViewModel viewModel
            && viewModel.SaveModGroupNameCommand.CanExecute(null))
        {
            await viewModel.SaveModGroupNameCommand.ExecuteAsync(null);
        }
    }

    private void ModSearchResultsScrollViewer_OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer || subscribedViewModel?.IsModDownloadPageVisible != true)
        {
            return;
        }

        var maxOffset = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        if (maxOffset <= 0)
        {
            return;
        }

        if (!SetScrollViewerVerticalOffset(scrollViewer, scrollViewer.Offset.Y - (e.Delta.Y * ModSearchWheelScrollStep)))
        {
            return;
        }

        SyncModSearchResultsScrollThumbFromScrollViewer();
        e.Handled = true;
    }

    private void ModDownloadPageScrollViewer_OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (e.Handled || subscribedViewModel?.IsModDownloadPageVisible != true)
        {
            return;
        }

        if (ScrollByWheelDelta(ModSearchResultsScrollViewer, e.Delta.Y))
        {
            SyncModSearchResultsScrollThumbFromScrollViewer();
            e.Handled = true;
        }
    }

    private void ModRecommendationRowsScrollViewer_OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer)
        {
            return;
        }

        if (ScrollByWheelDelta(scrollViewer, e.Delta.Y))
        {
            SyncModRecommendationRowsScrollThumbFromScrollViewer();
            e.Handled = true;
        }
    }

    private void ModInspectorScrollViewer_OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer || subscribedViewModel?.IsModListPageVisible != true)
        {
            return;
        }

        if (ScrollByWheelDelta(scrollViewer, e.Delta.Y))
        {
            e.Handled = true;
        }
    }

    private void ModListRowsScrollViewer_OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer || subscribedViewModel?.IsModListPageVisible != true)
        {
            return;
        }

        if (ScrollByWheelDelta(scrollViewer, e.Delta.Y))
        {
            SyncModListRowsScrollThumbFromScrollViewer();
            e.Handled = true;
            return;
        }

        if (ScrollByWheelDelta(ModListPageScrollViewer, e.Delta.Y))
        {
            e.Handled = true;
        }
    }

    private void ModListRowsScrollViewer_OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        SyncModListRowsScrollThumbFromScrollViewer();
    }

    private void ModListRowsScrollThumb_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(ModListRowsScrollTrack).Properties.IsLeftButtonPressed)
        {
            return;
        }

        isModListRowsScrollThumbDragActive = true;
        modListRowsScrollThumbPointerOffsetY = e.GetPosition(ModListRowsScrollThumb).Y;
        e.Pointer.Capture(ModListRowsScrollThumb);
        e.Handled = true;
    }

    private void ModListRowsScrollThumb_OnPointerMoved(object? sender, PointerEventArgs e) =>
        ModListRowsScrollTrack_OnPointerMoved(sender, e);

    private void ModListRowsScrollThumb_OnPointerReleased(object? sender, PointerReleasedEventArgs e) =>
        ModListRowsScrollTrack_OnPointerReleased(sender, e);

    private void ModListRowsScrollThumb_OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) =>
        ModListRowsScrollTrack_OnPointerCaptureLost(sender, e);

    private void ModListRowsScrollTrack_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(ModListRowsScrollTrack).Properties.IsLeftButtonPressed)
        {
            return;
        }

        isModListRowsScrollThumbDragActive = true;
        modListRowsScrollThumbPointerOffsetY = GetScrollThumbHeight(ModListRowsScrollViewer, ModListRowsScrollTrack) / 2;
        ScrollModListRowsToTrackPosition(e.GetPosition(ModListRowsScrollTrack).Y);
        e.Pointer.Capture(ModListRowsScrollTrack);
        e.Handled = true;
    }

    private void ModListRowsScrollTrack_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!isModListRowsScrollThumbDragActive)
        {
            return;
        }

        if (!e.GetCurrentPoint(ModListRowsScrollTrack).Properties.IsLeftButtonPressed)
        {
            StopModListRowsScrollThumbDrag(e.Pointer);
            return;
        }

        ScrollModListRowsToTrackPosition(e.GetPosition(ModListRowsScrollTrack).Y);
        e.Handled = true;
    }

    private void ModListRowsScrollTrack_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        StopModListRowsScrollThumbDrag(e.Pointer);
        e.Handled = true;
    }

    private void ModListRowsScrollTrack_OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        isModListRowsScrollThumbDragActive = false;
    }

    private void StopModListRowsScrollThumbDrag(IPointer pointer)
    {
        isModListRowsScrollThumbDragActive = false;
        pointer.Capture(null);
    }

    private void ScrollModListRowsToTrackPosition(double pointerY)
    {
        var offset = GetScrollOffsetForTrackPosition(
            ModListRowsScrollViewer,
            ModListRowsScrollTrack,
            modListRowsScrollThumbPointerOffsetY,
            pointerY);

        if (offset.HasValue && SetScrollViewerVerticalOffset(ModListRowsScrollViewer, offset.Value))
        {
            SyncModListRowsScrollThumbFromScrollViewer();
        }
    }

    private void ModRecommendationRowsScrollThumb_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(ModRecommendationRowsScrollTrack).Properties.IsLeftButtonPressed)
        {
            return;
        }

        isModRecommendationRowsScrollThumbDragActive = true;
        modRecommendationRowsScrollThumbPointerOffsetY = e.GetPosition(ModRecommendationRowsScrollThumb).Y;
        e.Pointer.Capture(ModRecommendationRowsScrollThumb);
        e.Handled = true;
    }

    private void ModRecommendationRowsScrollThumb_OnPointerMoved(object? sender, PointerEventArgs e) =>
        ModRecommendationRowsScrollTrack_OnPointerMoved(sender, e);

    private void ModRecommendationRowsScrollThumb_OnPointerReleased(object? sender, PointerReleasedEventArgs e) =>
        ModRecommendationRowsScrollTrack_OnPointerReleased(sender, e);

    private void ModRecommendationRowsScrollThumb_OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) =>
        ModRecommendationRowsScrollTrack_OnPointerCaptureLost(sender, e);

    private void ModRecommendationRowsScrollTrack_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(ModRecommendationRowsScrollTrack).Properties.IsLeftButtonPressed)
        {
            return;
        }

        isModRecommendationRowsScrollThumbDragActive = true;
        modRecommendationRowsScrollThumbPointerOffsetY = GetScrollThumbHeight(ModRecommendationRowsScrollViewer, ModRecommendationRowsScrollTrack) / 2;
        ScrollModRecommendationRowsToTrackPosition(e.GetPosition(ModRecommendationRowsScrollTrack).Y);
        e.Pointer.Capture(ModRecommendationRowsScrollTrack);
        e.Handled = true;
    }

    private void ModRecommendationRowsScrollTrack_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!isModRecommendationRowsScrollThumbDragActive)
        {
            return;
        }

        if (!e.GetCurrentPoint(ModRecommendationRowsScrollTrack).Properties.IsLeftButtonPressed)
        {
            StopModRecommendationRowsScrollThumbDrag(e.Pointer);
            return;
        }

        ScrollModRecommendationRowsToTrackPosition(e.GetPosition(ModRecommendationRowsScrollTrack).Y);
        e.Handled = true;
    }

    private void ModRecommendationRowsScrollTrack_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        StopModRecommendationRowsScrollThumbDrag(e.Pointer);
        e.Handled = true;
    }

    private void ModRecommendationRowsScrollTrack_OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        isModRecommendationRowsScrollThumbDragActive = false;
    }

    private void StopModRecommendationRowsScrollThumbDrag(IPointer pointer)
    {
        isModRecommendationRowsScrollThumbDragActive = false;
        pointer.Capture(null);
    }

    private void ScrollModRecommendationRowsToTrackPosition(double pointerY)
    {
        var offset = GetScrollOffsetForTrackPosition(
            ModRecommendationRowsScrollViewer,
            ModRecommendationRowsScrollTrack,
            modRecommendationRowsScrollThumbPointerOffsetY,
            pointerY);

        if (offset.HasValue && SetScrollViewerVerticalOffset(ModRecommendationRowsScrollViewer, offset.Value))
        {
            SyncModRecommendationRowsScrollThumbFromScrollViewer();
        }
    }

    private void ModSearchResultsScrollThumb_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(ModSearchResultsScrollTrack).Properties.IsLeftButtonPressed)
        {
            return;
        }

        isModSearchResultsScrollThumbDragActive = true;
        modSearchResultsScrollThumbPointerOffsetY = e.GetPosition(ModSearchResultsScrollThumb).Y;
        e.Pointer.Capture(ModSearchResultsScrollTrack);
        e.Handled = true;
    }

    private void ModSearchResultsScrollTrack_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(ModSearchResultsScrollTrack).Properties.IsLeftButtonPressed)
        {
            return;
        }

        isModSearchResultsScrollThumbDragActive = true;
        modSearchResultsScrollThumbPointerOffsetY = GetModSearchResultsScrollThumbHeight() / 2;
        ScrollModSearchResultsToTrackPosition(e.GetPosition(ModSearchResultsScrollTrack).Y);
        e.Pointer.Capture(ModSearchResultsScrollTrack);
        e.Handled = true;
    }

    private void ModSearchResultsScrollTrack_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!isModSearchResultsScrollThumbDragActive)
        {
            return;
        }

        if (!e.GetCurrentPoint(ModSearchResultsScrollTrack).Properties.IsLeftButtonPressed)
        {
            StopModSearchResultsScrollThumbDrag(e.Pointer);
            return;
        }

        ScrollModSearchResultsToTrackPosition(e.GetPosition(ModSearchResultsScrollTrack).Y);
        e.Handled = true;
    }

    private void ModSearchResultsScrollTrack_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        StopModSearchResultsScrollThumbDrag(e.Pointer);
        e.Handled = true;
    }

    private void ModSearchResultsScrollTrack_OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        isModSearchResultsScrollThumbDragActive = false;
    }

    private void StopModSearchResultsScrollThumbDrag(IPointer pointer)
    {
        isModSearchResultsScrollThumbDragActive = false;
        pointer.Capture(null);
    }

    private void ScrollModSearchResultsToTrackPosition(double pointerY)
    {
        var maxOffset = GetModSearchResultsMaxOffset();
        var travel = GetModSearchResultsScrollThumbTravel();
        if (maxOffset <= 0 || travel <= 0)
        {
            return;
        }

        var thumbTop = Math.Clamp(pointerY - modSearchResultsScrollThumbPointerOffsetY, 0, travel);
        var ratio = thumbTop / travel;
        if (SetScrollViewerVerticalOffset(ModSearchResultsScrollViewer, ratio * maxOffset))
        {
            SyncModSearchResultsScrollThumbFromScrollViewer();
        }
    }

    private void ModSearchResultsScrollViewer_OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is ScrollViewer scrollViewer)
        {
            SyncModSearchResultsScrollThumbFromScrollViewer();
            TryLoadMoreModSearchResultsNearBottom(scrollViewer);
        }
    }

    private void ModRecommendationRowsScrollViewer_OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        SyncModRecommendationRowsScrollThumbFromScrollViewer();
    }

    private void TryLoadMoreModSearchResultsNearBottom(ScrollViewer scrollViewer)
    {
        if (subscribedViewModel is not { HasMoreModSearchResults: true }
            || subscribedViewModel.IsSearchingNexusMods
            || subscribedViewModel.IsLoadingMoreModSearchResults)
        {
            return;
        }

        var maxOffset = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        if (maxOffset <= 0 || maxOffset - scrollViewer.Offset.Y > ModSearchAutoLoadMoreThresholdPixels)
        {
            return;
        }

        var command = subscribedViewModel.LoadMoreModSearchResultsCommand;
        if (command.CanExecute(null))
        {
            command.Execute(null);
        }
    }

    private void ModSearchResultsScrollViewer_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer
            || !e.GetCurrentPoint(scrollViewer).Properties.IsLeftButtonPressed
            || e.Source is Control sourceControl && IsCardActionSource(sourceControl))
        {
            return;
        }

        isModSearchResultsDragActive = true;
        isModSearchResultsDragScrolling = false;
        modSearchResultsPressedItem = TryGetNexusModRowFromSource(e.Source);
        modSearchResultsDragStartPointer = e.GetPosition(scrollViewer);
        modSearchResultsDragStartOffset = scrollViewer.Offset;
    }

    private void ModSearchResultsScrollViewer_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!isModSearchResultsDragActive || sender is not ScrollViewer scrollViewer)
        {
            return;
        }

        if (!e.GetCurrentPoint(scrollViewer).Properties.IsLeftButtonPressed)
        {
            StopModSearchResultsDrag(e.Pointer);
            return;
        }

        var currentPointer = e.GetPosition(scrollViewer);
        var deltaY = currentPointer.Y - modSearchResultsDragStartPointer.Y;
        if (!isModSearchResultsDragScrolling)
        {
            if (Math.Abs(deltaY) < ScrollDragStartThresholdPixels)
            {
                return;
            }

            isModSearchResultsDragScrolling = true;
            e.Pointer.Capture(scrollViewer);
        }

        if (SetScrollViewerVerticalOffset(scrollViewer, modSearchResultsDragStartOffset.Y - deltaY))
        {
            SyncModSearchResultsScrollThumbFromScrollViewer();
        }

        e.Handled = true;
    }

    private void ModSearchResultsScrollViewer_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var pressedItem = modSearchResultsPressedItem;
        var shouldOpenDetails = !isModSearchResultsDragScrolling
            && pressedItem is not null
            && ReferenceEquals(pressedItem, TryGetNexusModRowFromSource(e.Source));

        if (isModSearchResultsDragScrolling)
        {
            e.Handled = true;
        }

        StopModSearchResultsDrag(e.Pointer);

        if (shouldOpenDetails && TrySelectNexusModDetails(pressedItem))
        {
            e.Handled = true;
        }
    }

    private void ModSearchResultsScrollViewer_OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        isModSearchResultsDragActive = false;
        isModSearchResultsDragScrolling = false;
        modSearchResultsPressedItem = null;
    }

    private void StopModSearchResultsDrag(IPointer pointer)
    {
        isModSearchResultsDragActive = false;
        isModSearchResultsDragScrolling = false;
        modSearchResultsPressedItem = null;
        pointer.Capture(null);
    }

    private void WindowDragSurface_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        StopWindowDrag();

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var pointerPosition = e.GetPosition(WindowDragSurface);
        if (IsPointerOverWindowDragExcludedControl(pointerPosition)
            || (e.Source is Control sourceControl && ShouldSkipWindowDrag(sourceControl)))
        {
            return;
        }

        try
        {
            BeginMoveDrag(e);
            e.Handled = true;
            return;
        }
        catch (InvalidOperationException)
        {
            // Fall back to the manual drag path below if the platform rejects native dragging.
        }

        isWindowDragPressed = true;
        isWindowDragActive = false;
        windowDragPressedAt = DateTimeOffset.UtcNow;
        windowDragStartPointerPosition = WindowDragSurface.PointToScreen(pointerPosition);
        windowDragStartWindowPosition = Position;
    }

    private void WindowDragSurface_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!isWindowDragPressed)
        {
            return;
        }

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            if (isWindowDragActive)
            {
                e.Pointer.Capture(null);
            }

            StopWindowDrag();
            return;
        }

        var currentPointerPosition = WindowDragSurface.PointToScreen(e.GetPosition(WindowDragSurface));
        var deltaX = currentPointerPosition.X - windowDragStartPointerPosition.X;
        var deltaY = currentPointerPosition.Y - windowDragStartPointerPosition.Y;
        var movedDistance = Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY));

        if (!isWindowDragActive)
        {
            var heldLongEnough = (DateTimeOffset.UtcNow - windowDragPressedAt).TotalMilliseconds >= WindowDragHoldDelayMs;
            if (!heldLongEnough || movedDistance < WindowDragStartThresholdPixels)
            {
                return;
            }

            if (WindowState == WindowState.Maximized)
            {
                WindowState = WindowState.Normal;
                windowDragStartPointerPosition = currentPointerPosition;
                windowDragStartWindowPosition = new PixelPoint(
                    currentPointerPosition.X - (int)(Bounds.Width / 2),
                    currentPointerPosition.Y - 28);
                Position = windowDragStartWindowPosition;
                deltaX = 0;
                deltaY = 0;
            }

            isWindowDragActive = true;
            e.Pointer.Capture(this);
        }

        Position = new PixelPoint(
            windowDragStartWindowPosition.X + deltaX,
            windowDragStartWindowPosition.Y + deltaY);
        e.Handled = true;
    }

    private void WindowDragSurface_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!isWindowDragPressed && !isWindowDragActive)
        {
            return;
        }

        if (isWindowDragActive)
        {
            e.Handled = true;
            e.Pointer.Capture(null);
        }

        StopWindowDrag();
    }

    private void WindowDragSurface_OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        StopWindowDrag();
    }

    private void StopWindowDrag()
    {
        isWindowDragPressed = false;
        isWindowDragActive = false;
    }

    private static bool ShouldSkipWindowDrag(Control sourceControl)
    {
        return sourceControl is Button or ToggleButton or TextBox or ComboBox or Slider or ScrollBar or ScrollViewer or ListBox or ListBoxItem
            || IsCustomScrollControl(sourceControl)
            || sourceControl.FindAncestorOfType<Button>() is not null
            || sourceControl.FindAncestorOfType<ToggleButton>() is not null
            || sourceControl.FindAncestorOfType<TextBox>() is not null
            || sourceControl.FindAncestorOfType<ComboBox>() is not null
            || sourceControl.FindAncestorOfType<Slider>() is not null
            || sourceControl.FindAncestorOfType<ScrollBar>() is not null
            || sourceControl.FindAncestorOfType<ScrollViewer>() is not null
            || sourceControl.FindAncestorOfType<ListBox>() is not null
            || sourceControl.FindAncestorOfType<ListBoxItem>() is not null;
    }

    private static bool IsCustomScrollControl(Control sourceControl)
    {
        for (var current = sourceControl; current is not null; current = current.Parent as Control)
        {
            if (string.Equals(current.Name, nameof(ModListRowsScrollTrack), StringComparison.Ordinal)
                || string.Equals(current.Name, nameof(ModListRowsScrollThumb), StringComparison.Ordinal)
                || string.Equals(current.Name, nameof(ModRecommendationRowsScrollTrack), StringComparison.Ordinal)
                || string.Equals(current.Name, nameof(ModRecommendationRowsScrollThumb), StringComparison.Ordinal)
                || string.Equals(current.Name, nameof(ModSearchResultsScrollTrack), StringComparison.Ordinal)
                || string.Equals(current.Name, nameof(ModSearchResultsScrollThumb), StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ShouldSkipScrollableDrag(Control sourceControl)
    {
        return sourceControl is Button or ToggleButton or TextBox or ComboBox or Slider or ScrollBar or ListBox or ListBoxItem
            || sourceControl.FindAncestorOfType<Button>() is not null
            || sourceControl.FindAncestorOfType<ToggleButton>() is not null
            || sourceControl.FindAncestorOfType<TextBox>() is not null
            || sourceControl.FindAncestorOfType<ComboBox>() is not null
            || sourceControl.FindAncestorOfType<Slider>() is not null
            || sourceControl.FindAncestorOfType<ScrollBar>() is not null
            || sourceControl.FindAncestorOfType<ListBox>() is not null
            || sourceControl.FindAncestorOfType<ListBoxItem>() is not null;
    }

    private static bool IsCardActionSource(Control sourceControl)
    {
        return sourceControl is Button or ToggleButton
            || sourceControl.FindAncestorOfType<Button>() is not null
            || sourceControl.FindAncestorOfType<ToggleButton>() is not null;
    }

    private bool TrySelectNexusModDetails(NexusModSearchRowViewModel? mod)
    {
        if (mod is null || DataContext is not MainWindowViewModel viewModel)
        {
            return false;
        }

        viewModel.SelectNexusModSearchResultCommand.Execute(mod);
        return true;
    }

    private bool TrySelectModRecommendation(ModRecommendationRowViewModel? recommendation)
    {
        if (recommendation is null || DataContext is not MainWindowViewModel viewModel)
        {
            return false;
        }

        viewModel.SelectModRecommendationCommand.Execute(recommendation);
        return true;
    }

    private static NexusModSearchRowViewModel? TryGetNexusModRowFromSource(object? source)
    {
        if (source is StyledElement { DataContext: NexusModSearchRowViewModel mod })
        {
            return mod;
        }

        for (var current = source as Visual; current is not null; current = current.GetVisualParent())
        {
            if (current is StyledElement { DataContext: NexusModSearchRowViewModel row })
            {
                return row;
            }
        }

        return null;
    }

    private bool IsPointerOverWindowDragExcludedControl(Point pointerPosition)
    {
        return WindowDragSurface
            .GetVisualDescendants()
            .OfType<Control>()
            .Where(ShouldSkipWindowDrag)
            .Any(control => IsPointerInsideControl(control, pointerPosition));
    }

    private bool IsPointerInsideControl(Control control, Point pointerPosition)
    {
        if (!control.IsVisible)
        {
            return false;
        }

        var translatedOrigin = control.TranslatePoint(new Point(0, 0), WindowDragSurface);
        if (translatedOrigin is null)
        {
            return false;
        }

        return new Rect(translatedOrigin.Value, control.Bounds.Size).Contains(pointerPosition);
    }

    private void LauncherSurface_OnPointerWheelChanged(object? sender, PointerWheelEventArgs e) =>
        HandleLauncherDashboardWheel(e);

    private void HandleLauncherDashboardWheel(PointerWheelEventArgs e)
    {
        if (subscribedViewModel?.IsLauncherDashboardVisible != true)
        {
            return;
        }

        // The dashboard owns wheel input so its compact cards never scroll independently.
        e.Handled = true;

        if (e.Delta.Y < 0 && !isLauncherDetailsOpen)
        {
            _ = AnimateLauncherToggleArrowBounceAsync(direction: 1);
            _ = AnimateLauncherDetailsPanelAsync(show: true);
        }
        else if (e.Delta.Y < 0 && isLauncherDetailsOpen)
        {
            var (_, maxScrollOffset) = GetLauncherDetailsScrollMetrics();
            if (launcherDetailsScrollOffset < maxScrollOffset)
            {
                launcherDetailsScrollOffset = Math.Min(
                    maxScrollOffset,
                    launcherDetailsScrollOffset + LauncherDetailsScrollStep);
                ApplyLauncherDetailsScrollOffset();
            }
        }
        else if (e.Delta.Y > 0 && isLauncherDetailsOpen && launcherDetailsScrollOffset > 0)
        {
            launcherDetailsScrollOffset = Math.Max(
                0,
                launcherDetailsScrollOffset - LauncherDetailsScrollStep);
            ApplyLauncherDetailsScrollOffset();
        }
        else if (e.Delta.Y > 0 && isLauncherDetailsOpen)
        {
            _ = AnimateLauncherToggleArrowBounceAsync(direction: -1);
            _ = AnimateLauncherDetailsPanelAsync(show: false);
        }
    }

    private async void LauncherToggleButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!isLauncherTogglePointerOver)
        {
            _ = AnimateLauncherToggleArrowBounceAsync(isLauncherDetailsOpen ? -1 : 1);
        }

        await AnimateLauncherDetailsPanelAsync(show: !isLauncherDetailsOpen);
    }

    private void LauncherToggleButton_OnPointerEntered(object? sender, PointerEventArgs e)
    {
        if (isLauncherTogglePointerOver)
        {
            return;
        }

        isLauncherTogglePointerOver = true;
        _ = AnimateLauncherToggleArrowHoverLoopAsync();
    }

    private async void LauncherToggleButton_OnPointerExited(object? sender, PointerEventArgs e)
    {
        await Dispatcher.UIThread.InvokeAsync(
            () =>
            {
                if (!LauncherToggleButton.IsPointerOver)
                {
                    isLauncherTogglePointerOver = false;
                    launcherToggleArrowBounceCancellation?.Cancel();
                    ResetLauncherToggleArrowBounce();
                }
            },
            DispatcherPriority.Background);
    }

    private void ToggleWindowScaleButton_OnClick(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void MinimizeWindowButton_OnClick(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void CloseWindowButton_OnClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void CloseWindowButton_OnPointerEntered(object? sender, PointerEventArgs e)
    {
        await AnimateCloseWindowGlyphSpinAsync();
    }

    private void QueueSidebarNavigationAnimation()
    {
        var requestVersion = Interlocked.Increment(ref sidebarNavigationAnimationRequestVersion);
        QueueContentSurfaceAnimation();

        Dispatcher.UIThread.Post(
            async () =>
            {
                if (requestVersion == sidebarNavigationAnimationRequestVersion)
                {
                    await Task.Delay(56);
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
                }

                if (requestVersion == sidebarNavigationAnimationRequestVersion)
                {
                    await AnimateCurrentSidebarNavigationAsync();
                }
            },
            DispatcherPriority.Background);
    }

    private void QueueContentSurfaceAnimation()
    {
        var requestVersion = Interlocked.Increment(ref contentSurfaceAnimationRequestVersion);

        Dispatcher.UIThread.Post(
            async () =>
            {
                if (requestVersion == contentSurfaceAnimationRequestVersion)
                {
                    await Task.Delay(56);
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
                }

                if (requestVersion == contentSurfaceAnimationRequestVersion)
                {
                    await AnimateCurrentContentSurfaceAsync();
                }
            },
            DispatcherPriority.Background);
    }

    private async Task AnimateCurrentSidebarNavigationAsync()
    {
        sidebarNavigationAnimationCancellation = ReplaceCancellation(ref sidebarNavigationAnimationCancellation);
        var token = sidebarNavigationAnimationCancellation.Token;
        var items = GetVisibleSidebarNavigationItems();

        if (items.Count == 0)
        {
            return;
        }

        EnsureSidebarNavigationTransforms(items);

        if (subscribedViewModel?.ReduceMotion == true)
        {
            ResetSidebarNavigationItems(items);
            return;
        }

        foreach (var item in items)
        {
            ((TranslateTransform)item.RenderTransform!).X = SidebarItemEntranceOffsetX;
            item.Opacity = SidebarItemEntranceOpacity;
        }

        try
        {
            var animations = items
                .Select((item, index) => AnimateSidebarNavigationItemAsync(
                    item,
                    SidebarNavigationBlankDelayMs + (index * SidebarItemEntranceStaggerMs),
                    token))
                .ToArray();

            await Task.WhenAll(animations);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task AnimateCurrentContentSurfaceAsync()
    {
        contentSurfaceAnimationCancellation = ReplaceCancellation(ref contentSurfaceAnimationCancellation);
        var token = contentSurfaceAnimationCancellation.Token;
        var surface = GetVisibleContentSurface();

        if (surface is null)
        {
            return;
        }

        var transforms = EnsureContentSurfaceTransforms(surface);
        var scale = (ScaleTransform)transforms.Children[0];
        var translate = (TranslateTransform)transforms.Children[1];

        if (subscribedViewModel?.ReduceMotion == true)
        {
            ResetContentSurface(surface);
            return;
        }

        var fullHeight = GetContentSurfaceFullHeight(surface);
        if (fullHeight <= 0)
        {
            ResetContentSurface(surface);
            return;
        }

        var collapsedHeight = Math.Max(32, fullHeight * ContentSurfaceCollapsedRevealRatio);
        var overshootHeight = fullHeight * ContentSurfaceOvershootRevealRatio;

        surface.ClipToBounds = true;
        surface.Height = collapsedHeight;
        surface.Opacity = 0;
        scale.ScaleX = 1;
        scale.ScaleY = ContentSurfaceStartScaleY;
        translate.Y = ContentSurfaceEntranceOffsetY;

        try
        {
            await AnimateContentSurfacePhaseAsync(
                surface,
                scale,
                translate,
                collapsedHeight,
                overshootHeight,
                ContentSurfaceStartScaleY,
                ContentSurfaceOvershootScaleY,
                ContentSurfaceEntranceOffsetY,
                0,
                0,
                1,
                ContentSurfaceExpandDurationMs,
                token);

            await AnimateContentSurfacePhaseAsync(
                surface,
                scale,
                translate,
                overshootHeight,
                fullHeight,
                ContentSurfaceOvershootScaleY,
                1,
                0,
                0,
                1,
                1,
                ContentSurfaceSettleDurationMs,
                token);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                ResetContentSurface(surface);
            }
        }
    }

    private async Task AnimateCloseWindowGlyphSpinAsync()
    {
        closeWindowSpinCancellation = ReplaceCancellation(ref closeWindowSpinCancellation);
        var token = closeWindowSpinCancellation.Token;

        if (subscribedViewModel?.ReduceMotion == true)
        {
            CloseWindowGlyphRotate.Angle = 0;
            return;
        }

        var startAngle = CloseWindowGlyphRotate.Angle;
        var targetAngle = startAngle + 360;
        var start = DateTimeOffset.UtcNow;

        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();

                var elapsed = (DateTimeOffset.UtcNow - start).TotalMilliseconds;
                var t = Math.Clamp(elapsed / CloseWindowHoverSpinDurationMs, 0, 1);
                CloseWindowGlyphRotate.Angle = Lerp(startAngle, targetAngle, EaseOutCubic(t));

                if (t >= 1)
                {
                    break;
                }

                await Task.Delay(16, token);
            }

            CloseWindowGlyphRotate.Angle = 0;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task AnimateLauncherToggleArrowBounceAsync(double direction)
    {
        launcherToggleArrowBounceCancellation = ReplaceCancellation(ref launcherToggleArrowBounceCancellation);
        var token = launcherToggleArrowBounceCancellation.Token;

        if (subscribedViewModel?.ReduceMotion == true)
        {
            ResetLauncherToggleArrowBounce();
            return;
        }

        try
        {
            await AnimateLauncherToggleArrowBounceCycleAsync(direction, token);
            ResetLauncherToggleArrowBounce();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task AnimateLauncherToggleArrowHoverLoopAsync()
    {
        launcherToggleArrowBounceCancellation = ReplaceCancellation(ref launcherToggleArrowBounceCancellation);
        var token = launcherToggleArrowBounceCancellation.Token;

        if (subscribedViewModel?.ReduceMotion == true)
        {
            ResetLauncherToggleArrowBounce();
            return;
        }

        try
        {
            while (isLauncherTogglePointerOver)
            {
                token.ThrowIfCancellationRequested();

                await AnimateLauncherToggleArrowBounceCycleAsync(isLauncherDetailsOpen ? -1 : 1, token);
                ResetLauncherToggleArrowBounce();
                await Task.Delay(LauncherToggleArrowBounceRestMs, token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            ResetLauncherToggleArrowBounce();
        }
    }

    private async Task AnimateLauncherToggleArrowBounceCycleAsync(double direction, CancellationToken token)
    {
        var start = DateTimeOffset.UtcNow;

        while (true)
        {
            token.ThrowIfCancellationRequested();

            var elapsed = (DateTimeOffset.UtcNow - start).TotalMilliseconds;
            var t = Math.Clamp(elapsed / LauncherToggleArrowBounceDurationMs, 0, 1);
            var damping = 1 - t;
            var wave = Math.Sin(t * Math.PI * 2);
            var lift = Math.Sin(t * Math.PI);

            LauncherToggleIconTranslate.Y = direction * LauncherToggleArrowBounceDistance * wave * damping;
            LauncherToggleIconScale.ScaleX = 1 + (0.08 * lift * damping);
            LauncherToggleIconScale.ScaleY = 1 - (0.08 * lift * damping);

            if (t >= 1)
            {
                break;
            }

            await Task.Delay(16, token);
        }
    }

    private void ResetLauncherToggleArrowBounce()
    {
        LauncherToggleIconTranslate.Y = 0;
        LauncherToggleIconScale.ScaleX = 1;
        LauncherToggleIconScale.ScaleY = 1;
    }

    private void ResetLauncherDetailsPanelIfUnavailable()
    {
        if (subscribedViewModel?.IsLauncherDashboardVisible == true)
        {
            return;
        }

        launcherPanelAnimationCancellation?.Cancel();
        LauncherContentTrackTranslate.Y = 0;
        LauncherToggleButtonTranslate.Y = 612;
        LauncherToggleIconRotate.Angle = 0;
        LauncherDetailsHost.Opacity = 0;
        LauncherDetailsScale.ScaleX = 1;
        LauncherDetailsScale.ScaleY = 1;
        isLauncherDetailsOpen = false;
        ReleaseLauncherDetailsContent();
        ResetLauncherToggleArrowBounce();
    }

    private static async Task AnimateContentSurfacePhaseAsync(
        Control surface,
        ScaleTransform scale,
        TranslateTransform translate,
        double fromHeight,
        double toHeight,
        double fromScaleY,
        double toScaleY,
        double fromY,
        double toY,
        double fromOpacity,
        double toOpacity,
        int durationMs,
        CancellationToken token)
    {
        var start = DateTimeOffset.UtcNow;

        while (true)
        {
            token.ThrowIfCancellationRequested();

            var elapsed = (DateTimeOffset.UtcNow - start).TotalMilliseconds;
            var t = Math.Clamp(elapsed / durationMs, 0, 1);
            var curve = EaseOutCubic(t);

            surface.Height = Lerp(fromHeight, toHeight, curve);
            scale.ScaleY = Lerp(fromScaleY, toScaleY, curve);
            translate.Y = Lerp(fromY, toY, curve);
            surface.Opacity = Lerp(fromOpacity, toOpacity, curve);

            if (t >= 1)
            {
                break;
            }

            await Task.Delay(16, token);
        }

        surface.Height = toHeight;
        scale.ScaleY = toScaleY;
        translate.Y = toY;
        surface.Opacity = toOpacity;
    }

    private async Task AnimateSidebarNavigationItemAsync(Button item, int delayMs, CancellationToken token)
    {
        if (delayMs > 0)
        {
            await Task.Delay(delayMs, token);
        }

        var translate = (TranslateTransform)item.RenderTransform!;
        var startX = translate.X;
        var startOpacity = item.Opacity;
        var start = DateTimeOffset.UtcNow;

        while (true)
        {
            token.ThrowIfCancellationRequested();

            var elapsed = (DateTimeOffset.UtcNow - start).TotalMilliseconds;
            var t = Math.Clamp(elapsed / SidebarItemEntranceDurationMs, 0, 1);
            var curve = EaseOutCubic(t);

            translate.X = Lerp(startX, 0, curve);
            item.Opacity = Lerp(startOpacity, 1, EaseOutCubic(t));

            if (t >= 1)
            {
                break;
            }

            await Task.Delay(16, token);
        }

        translate.X = 0;
        item.Opacity = 1;
    }

    private IReadOnlyList<Button> GetVisibleSidebarNavigationItems()
    {
        if (SettingsSidebarNavigation.IsVisible)
        {
            return GetSidebarNavigationItems(SettingsSidebarNavigation);
        }

        if (LabSidebarNavigation.IsVisible)
        {
            return GetSidebarNavigationItems(LabSidebarNavigation);
        }

        if (LauncherSidebarNavigation.IsVisible)
        {
            return GetSidebarNavigationItems(LauncherSidebarNavigation);
        }

        if (ModSidebarNavigation.IsVisible)
        {
            return GetSidebarNavigationItems(ModSidebarNavigation);
        }

        return [];
    }

    private IEnumerable<Button> GetAllSidebarNavigationItems()
    {
        return GetSidebarNavigationItems(LauncherSidebarNavigation)
            .Concat(GetSidebarNavigationItems(ModSidebarNavigation))
            .Concat(GetSidebarNavigationItems(SettingsSidebarNavigation))
            .Concat(GetSidebarNavigationItems(LabSidebarNavigation));
    }

    private static IReadOnlyList<Button> GetSidebarNavigationItems(StackPanel navigation)
    {
        return navigation.Children.OfType<Button>().ToArray();
    }

    private static void EnsureSidebarNavigationTransforms(IEnumerable<Button> items)
    {
        foreach (var item in items)
        {
            if (item.RenderTransform is not TranslateTransform)
            {
                item.RenderTransform = new TranslateTransform();
            }
        }
    }

    private static void ResetSidebarNavigationItems(IEnumerable<Button> items)
    {
        EnsureSidebarNavigationTransforms(items);

        foreach (var item in items)
        {
            ((TranslateTransform)item.RenderTransform!).X = 0;
            item.Opacity = 1;
        }
    }

    private void SyncSidebarLayout()
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var sidebarWidth = Math.Clamp(
            viewModel.SidebarWidth,
            MainWindowViewModel.SidebarCollapsedWidth,
            MainWindowViewModel.SidebarComfortWidth);

        if (Math.Abs(viewModel.SidebarWidth - sidebarWidth) > 0.1)
        {
            viewModel.SidebarWidth = sidebarWidth;
            return;
        }

        if (!isSidebarLayoutAnimating)
        {
            var isExpanded = viewModel.IsSidebarExpanded;
            ApplySidebarLayout(sidebarWidth, isExpanded, isExpanded ? 1 : 0);
        }
    }

    private async Task AnimateSidebarLayoutToAsync(double targetWidth)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        targetWidth = Math.Clamp(
            targetWidth,
            MainWindowViewModel.SidebarCollapsedWidth,
            MainWindowViewModel.SidebarComfortWidth);

        sidebarLayoutAnimationCancellation = ReplaceCancellation(ref sidebarLayoutAnimationCancellation);
        var token = sidebarLayoutAnimationCancellation.Token;

        var startWidth = SidebarShell.Bounds.Width > 0 ? SidebarShell.Bounds.Width : viewModel.SidebarWidth;
        var targetExpanded = targetWidth > MainWindowViewModel.SidebarCollapsedWidth;
        var startTextOpacity = targetExpanded ? 0 : 1;
        var targetTextOpacity = targetExpanded ? 1 : 0;

        if (viewModel.ReduceMotion || Math.Abs(startWidth - targetWidth) < 0.1)
        {
            viewModel.SidebarWidth = targetWidth;
            ApplySidebarLayout(targetWidth, targetExpanded, targetExpanded ? 1 : 0);
            return;
        }

        isSidebarLayoutAnimating = true;
        try
        {
            if (targetExpanded)
            {
                UpdateSidebarTextVisibility(true);
                ApplySidebarLayout(startWidth, true, 0);
            }

            var start = DateTimeOffset.UtcNow;
            while (true)
            {
                token.ThrowIfCancellationRequested();

                var elapsed = (DateTimeOffset.UtcNow - start).TotalMilliseconds;
                var t = Math.Clamp(elapsed / SidebarLayoutAnimationDurationMs, 0, 1);
                var widthCurve = EaseOutCubic(t);
                var textCurve = targetExpanded ? SmoothStep(Math.Clamp((t - 0.28) / 0.72, 0, 1)) : EaseInCubic(t);
                var nextWidth = Lerp(startWidth, targetWidth, widthCurve);
                var textOpacity = Lerp(startTextOpacity, targetTextOpacity, textCurve);

                ApplySidebarLayout(nextWidth, targetExpanded || textOpacity > 0.01, textOpacity);

                if (t >= 1)
                {
                    break;
                }

                await Task.Delay(16, token);
            }

            viewModel.SidebarWidth = targetWidth;
            ApplySidebarLayout(targetWidth, targetExpanded, targetExpanded ? 1 : 0);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            isSidebarLayoutAnimating = false;
            if (!token.IsCancellationRequested)
            {
                SyncSidebarLayout();
            }
        }
    }

    private void ApplySidebarLayout(double sidebarWidth, bool showText, double textOpacity)
    {
        var contentLeft = sidebarWidth + SidebarContentGap;
        LauncherSurface.Margin = new Thickness(contentLeft, 56, 0, 0);
        SettingsSurface.Margin = new Thickness(contentLeft, 56, 0, 0);
        LabSurface.Margin = new Thickness(contentLeft, 56, 0, 0);
        DownloadCenterSurface.Margin = new Thickness(contentLeft, 56, 0, 0);
        SidebarShell.Width = sidebarWidth;

        var normalizedTextOpacity = Math.Clamp(textOpacity, 0, 1);
        SidebarToggleCollapseIcon.IsVisible = showText;
        SidebarToggleExpandIcon.IsVisible = !showText;
        SidebarToggleCollapseIcon.Opacity = normalizedTextOpacity;
        SidebarToggleExpandIcon.Opacity = 1 - normalizedTextOpacity;
        ApplyGameIdentityLayout(showText, normalizedTextOpacity);
        ApplySidebarNavigationItemWidth(sidebarWidth);
        SetSidebarTextOpacity(LauncherSidebarNavigation, normalizedTextOpacity);
        SetSidebarTextOpacity(ModSidebarNavigation, normalizedTextOpacity);
        SetSidebarTextOpacity(SettingsSidebarNavigation, normalizedTextOpacity);
        SetSidebarTextOpacity(LabSidebarNavigation, normalizedTextOpacity);
        UpdateSidebarTextVisibility(showText);
    }

    private void ApplyGameIdentityLayout(bool showText, double textOpacity)
    {
        var normalizedTextOpacity = Math.Clamp(textOpacity, 0, 1);
        GameIdentityButton.Margin = new Thickness(10, 16, 0, 0);
        GameIdentityButton.Width = Math.Max(0, SidebarShell.Width - SidebarNavigationHorizontalInset);
        GameIdentityBlock.ColumnDefinitions[0].Width = new GridLength(GameIdentityIconSize);
        GameIdentityBlock.ColumnSpacing = 10 * normalizedTextOpacity;
        GameIdentityIconFrame.Width = GameIdentityIconSize;
        GameIdentityIconFrame.Height = GameIdentityIconSize;
        GameIdentityTitle.IsVisible = showText;
        GameIdentityTitle.Opacity = normalizedTextOpacity;
    }

    private void ApplySidebarNavigationItemWidth(double sidebarWidth)
    {
        var itemWidth = Math.Max(0, sidebarWidth - SidebarNavigationHorizontalInset);

        foreach (var item in GetAllSidebarNavigationItems())
        {
            item.Width = itemWidth;
        }
    }

    private void UpdateSidebarTextVisibility(bool showText)
    {
        if (showText == isSidebarTextVisible)
        {
            return;
        }

        isSidebarTextVisible = showText;
        UpdateSidebarNavigationPresentation(LauncherSidebarNavigation);
        UpdateSidebarNavigationPresentation(ModSidebarNavigation);
        UpdateSidebarNavigationPresentation(SettingsSidebarNavigation);
        UpdateSidebarNavigationPresentation(LabSidebarNavigation);
    }

    private static void SetSidebarTextOpacity(StackPanel navigation, double opacity)
    {
        foreach (var button in GetSidebarNavigationItems(navigation))
        {
            if (button.Content is not Grid grid)
            {
                continue;
            }

            var children = grid.Children.OfType<Control>().ToArray();
            for (var i = 1; i < children.Length; i++)
            {
                children[i].Opacity = opacity;
            }
        }
    }

    private void UpdateSidebarNavigationPresentation(StackPanel navigation)
    {
        foreach (var button in GetSidebarNavigationItems(navigation))
        {
            if (button.Content is not Grid grid)
            {
                continue;
            }

            var children = grid.Children.OfType<Control>().ToArray();
            for (var i = 0; i < children.Length; i++)
            {
                children[i].IsVisible = i == 0 || isSidebarTextVisible;
            }
        }
    }

    private Control? GetVisibleContentSurface()
    {
        if (SettingsSurface.IsVisible)
        {
            return SettingsSurface;
        }

        if (LabSurface.IsVisible)
        {
            return LabSurface;
        }

        if (DownloadCenterSurface.IsVisible)
        {
            return DownloadCenterSurface;
        }

        if (LauncherSurface.IsVisible)
        {
            return LauncherSurface;
        }

        return null;
    }

    private static TransformGroup EnsureContentSurfaceTransforms(Control surface)
    {
        surface.RenderTransformOrigin = new RelativePoint(0.5, 0, RelativeUnit.Relative);

        if (surface.RenderTransform is TransformGroup existingGroup
            && existingGroup.Children.Count == 2
            && existingGroup.Children[0] is ScaleTransform
            && existingGroup.Children[1] is TranslateTransform)
        {
            return existingGroup;
        }

        var group = new TransformGroup();
        group.Children.Add(new ScaleTransform(1, 1));
        group.Children.Add(new TranslateTransform());
        surface.RenderTransform = group;
        return group;
    }

    private static void ResetContentSurface(Control surface)
    {
        var transforms = EnsureContentSurfaceTransforms(surface);
        var scale = (ScaleTransform)transforms.Children[0];
        var translate = (TranslateTransform)transforms.Children[1];

        surface.Height = double.NaN;
        scale.ScaleX = 1;
        scale.ScaleY = 1;
        translate.Y = 0;
        surface.Opacity = 1;
    }

    private static double GetContentSurfaceFullHeight(Control surface)
    {
        if (surface.Bounds.Height > 0)
        {
            return surface.Bounds.Height;
        }

        return surface.DesiredSize.Height;
    }

    private void UpdateModManagerPageHeights()
    {
        if (!TryUpdateModManagerPageHeights() && ModWorkspaceHost.IsActive)
        {
            isAwaitingModWorkspaceLayout = true;
        }
    }

    private bool TryUpdateModManagerPageHeights()
    {
        if (!ModWorkspaceHost.IsActive)
        {
            return false;
        }

        var modDownloadPageScrollViewer = FindModControl<ScrollViewer>(nameof(ModDownloadPageScrollViewer));
        var modDownloadPageHost = FindModControl<Grid>(nameof(ModDownloadPageHost));
        var modSearchResultsScrollViewer = FindModControl<ScrollViewer>(nameof(ModSearchResultsScrollViewer));
        var modDownloadQueueScrollViewer = FindModControl<ScrollViewer>(nameof(ModDownloadQueueScrollViewer));
        var modListPageScrollViewer = FindModControl<ScrollViewer>(nameof(ModListPageScrollViewer));
        var modListRowsListBox = FindModControl<ListBox>(nameof(ModListRowsListBox));
        var modListRowsScrollViewer = FindModListRowsScrollViewer();
        var modInspectorScrollViewer = FindModControl<ScrollViewer>("ModInspectorScrollViewer");
        var modRecommendationPageHost = FindModControl<Grid>("ModRecommendationPageHost");
        var modRecommendationRowsScrollViewer = FindModControl<ScrollViewer>(nameof(ModRecommendationRowsScrollViewer));

        if (modDownloadPageScrollViewer is null
            || modDownloadPageHost is null
            || modSearchResultsScrollViewer is null
            || modDownloadQueueScrollViewer is null
            || modListPageScrollViewer is null
            || modListRowsListBox is null
            || modListRowsScrollViewer is null
            || modInspectorScrollViewer is null
            || modRecommendationPageHost is null
            || modRecommendationRowsScrollViewer is null)
        {
            return false;
        }

        EnsureModWheelHandlers(
            modListRowsScrollViewer,
            modSearchResultsScrollViewer,
            modRecommendationRowsScrollViewer,
            modInspectorScrollViewer);

        if (LauncherSurface.Bounds.Height <= 0)
        {
            ResetModManagerPageHeights();
            return true;
        }

        var pageHeight = Math.Max(360, LauncherSurface.Bounds.Height - ModDownloadPageVerticalMargin);

        if (subscribedViewModel?.IsModDownloadPageVisible == true)
        {
            modDownloadPageScrollViewer.Height = pageHeight;
            modDownloadPageHost.Height = pageHeight;
            modSearchResultsScrollViewer.Height = Math.Max(280, pageHeight - ModDownloadSearchViewportReservedHeight);
            modDownloadQueueScrollViewer.Height = Math.Max(320, pageHeight - ModDownloadQueueViewportReservedHeight);
            Dispatcher.UIThread.Post(SyncModSearchResultsScrollThumbFromScrollViewer, DispatcherPriority.Render);
        }
        else
        {
            modDownloadPageHost.Height = double.NaN;
            modDownloadPageScrollViewer.Height = double.NaN;
            modSearchResultsScrollViewer.Height = double.NaN;
            modDownloadQueueScrollViewer.Height = double.NaN;
        }

        modListPageScrollViewer.Height = subscribedViewModel?.IsModListPageVisible == true
            ? pageHeight
            : double.NaN;

        modListRowsListBox.Height = subscribedViewModel?.IsModListPageVisible == true
            ? Math.Max(320, pageHeight - ModListViewportReservedHeight)
            : double.NaN;

        modInspectorScrollViewer.Height = subscribedViewModel?.IsModListPageVisible == true
            ? Math.Max(320, pageHeight - ModListViewportReservedHeight)
            : double.NaN;

        modRecommendationPageHost.Height = subscribedViewModel?.IsModRecommendationPageVisible == true
            ? pageHeight
            : double.NaN;

        modRecommendationRowsScrollViewer.Height = subscribedViewModel?.IsModRecommendationPageVisible == true
            ? Math.Max(260, pageHeight - ModRecommendationViewportReservedHeight)
            : double.NaN;

        Dispatcher.UIThread.Post(SyncModListRowsScrollThumbFromScrollViewer, DispatcherPriority.Render);
        Dispatcher.UIThread.Post(SyncModRecommendationRowsScrollThumbFromScrollViewer, DispatcherPriority.Render);
        return true;
    }

    private void EnsureModWheelHandlers(
        ScrollViewer listScrollViewer,
        ScrollViewer searchScrollViewer,
        ScrollViewer recommendationScrollViewer,
        ScrollViewer inspectorScrollViewer)
    {
        if (!ReferenceEquals(modListWheelScrollViewer, listScrollViewer))
        {
            DetachModListWheelHandler();
            modListWheelScrollViewer = listScrollViewer;
            listScrollViewer.AddHandler(
                InputElement.PointerWheelChangedEvent,
                ModListRowsScrollViewer_OnPointerWheelChanged,
                RoutingStrategies.Tunnel,
                handledEventsToo: true);
            listScrollViewer.ScrollChanged += ModListRowsScrollViewer_OnScrollChanged;
        }

        if (!ReferenceEquals(modSearchWheelScrollViewer, searchScrollViewer))
        {
            modSearchWheelScrollViewer?.RemoveHandler(
                InputElement.PointerWheelChangedEvent,
                ModSearchResultsScrollViewer_OnPointerWheelChanged);
            modSearchWheelScrollViewer = searchScrollViewer;
            searchScrollViewer.AddHandler(
                InputElement.PointerWheelChangedEvent,
                ModSearchResultsScrollViewer_OnPointerWheelChanged,
                RoutingStrategies.Tunnel,
                handledEventsToo: true);
        }

        if (!ReferenceEquals(modRecommendationWheelScrollViewer, recommendationScrollViewer))
        {
            modRecommendationWheelScrollViewer?.RemoveHandler(
                InputElement.PointerWheelChangedEvent,
                ModRecommendationRowsScrollViewer_OnPointerWheelChanged);
            modRecommendationWheelScrollViewer = recommendationScrollViewer;
            recommendationScrollViewer.AddHandler(
                InputElement.PointerWheelChangedEvent,
                ModRecommendationRowsScrollViewer_OnPointerWheelChanged,
                RoutingStrategies.Tunnel,
                handledEventsToo: true);
        }

        if (!ReferenceEquals(modInspectorWheelScrollViewer, inspectorScrollViewer))
        {
            modInspectorWheelScrollViewer?.RemoveHandler(
                InputElement.PointerWheelChangedEvent,
                ModInspectorScrollViewer_OnPointerWheelChanged);
            modInspectorWheelScrollViewer = inspectorScrollViewer;
            inspectorScrollViewer.AddHandler(
                InputElement.PointerWheelChangedEvent,
                ModInspectorScrollViewer_OnPointerWheelChanged,
                RoutingStrategies.Tunnel,
                handledEventsToo: true);
        }
    }

    private void DetachModListWheelHandler()
    {
        modListWheelScrollViewer?.RemoveHandler(
            InputElement.PointerWheelChangedEvent,
            ModListRowsScrollViewer_OnPointerWheelChanged);
        if (modListWheelScrollViewer is not null)
        {
            modListWheelScrollViewer.ScrollChanged -= ModListRowsScrollViewer_OnScrollChanged;
        }
        modListWheelScrollViewer = null;
    }

    private void ResetModManagerPageHeights()
    {
        if (!ModWorkspaceHost.IsActive)
        {
            return;
        }

        SetModControlHeight<Grid>(nameof(ModDownloadPageHost), double.NaN);
        SetModControlHeight<ScrollViewer>(nameof(ModDownloadPageScrollViewer), double.NaN);
        SetModControlHeight<ScrollViewer>(nameof(ModSearchResultsScrollViewer), double.NaN);
        SetModControlHeight<ScrollViewer>(nameof(ModDownloadQueueScrollViewer), double.NaN);
        SetModControlHeight<ScrollViewer>(nameof(ModListPageScrollViewer), double.NaN);
        SetModControlHeight<ListBox>(nameof(ModListRowsListBox), double.NaN);
        SetModControlHeight<ScrollViewer>("ModInspectorScrollViewer", double.NaN);
        SetModControlHeight<Grid>("ModRecommendationPageHost", double.NaN);
        SetModControlHeight<ScrollViewer>(nameof(ModRecommendationRowsScrollViewer), double.NaN);
        SyncModListRowsScrollThumbFromScrollViewer();
        SyncModRecommendationRowsScrollThumbFromScrollViewer();
    }

    private void SetModControlHeight<T>(string name, double height) where T : Control
    {
        if (FindModControl<T>(name) is { } control)
        {
            control.Height = height;
        }
    }

    private void SyncModSearchResultsScrollThumbFromScrollViewer()
    {
        var scrollViewer = FindModControl<ScrollViewer>(nameof(ModSearchResultsScrollViewer));
        var track = FindModControl<Border>(nameof(ModSearchResultsScrollTrack));
        var thumb = FindModControl<Border>(nameof(ModSearchResultsScrollThumb));
        if (track is not null && thumb is not null)
        {
            SyncScrollThumb(scrollViewer, track, thumb);
        }
    }

    private void SyncModListRowsScrollThumbFromScrollViewer()
    {
        var track = FindModControl<Border>(nameof(ModListRowsScrollTrack));
        var thumb = FindModControl<Border>(nameof(ModListRowsScrollThumb));
        if (track is not null && thumb is not null)
        {
            SyncScrollThumb(FindModListRowsScrollViewer(), track, thumb);
        }
    }

    private void SyncModRecommendationRowsScrollThumbFromScrollViewer()
    {
        var scrollViewer = FindModControl<ScrollViewer>(nameof(ModRecommendationRowsScrollViewer));
        var track = FindModControl<Border>(nameof(ModRecommendationRowsScrollTrack));
        var thumb = FindModControl<Border>(nameof(ModRecommendationRowsScrollThumb));
        if (track is not null && thumb is not null)
        {
            SyncScrollThumb(scrollViewer, track, thumb);
        }
    }

    private double GetModSearchResultsMaxOffset()
    {
        return GetMaxScrollOffset(ModSearchResultsScrollViewer);
    }

    private double GetModSearchResultsScrollThumbHeight()
    {
        return GetScrollThumbHeight(ModSearchResultsScrollViewer, ModSearchResultsScrollTrack);
    }

    private double GetModSearchResultsScrollThumbTravel()
    {
        return Math.Max(0, ModSearchResultsScrollTrack.Bounds.Height - GetModSearchResultsScrollThumbHeight());
    }

    private static bool SetScrollViewerVerticalOffset(ScrollViewer scrollViewer, double requestedOffset)
    {
        var maxOffset = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        var nextOffset = Math.Clamp(requestedOffset, 0, maxOffset);
        if (Math.Abs(nextOffset - scrollViewer.Offset.Y) < 0.1)
        {
            return false;
        }

        scrollViewer.Offset = new Vector(scrollViewer.Offset.X, nextOffset);
        return true;
    }

    private static bool ScrollByWheelDelta(ScrollViewer scrollViewer, double deltaY)
    {
        var nextOffset = CalculateScrollOffsetForWheel(
            scrollViewer.Offset.Y,
            scrollViewer.Extent.Height,
            scrollViewer.Viewport.Height,
            deltaY);
        if (nextOffset is null)
        {
            return false;
        }

        return SetScrollViewerVerticalOffset(scrollViewer, nextOffset.Value);
    }

    internal static double? CalculateScrollOffsetForWheelForTesting(
        double currentOffset,
        double extentHeight,
        double viewportHeight,
        double deltaY) =>
        CalculateScrollOffsetForWheel(currentOffset, extentHeight, viewportHeight, deltaY);

    private static double? CalculateScrollOffsetForWheel(
        double currentOffset,
        double extentHeight,
        double viewportHeight,
        double deltaY)
    {
        var maxOffset = Math.Max(0, extentHeight - viewportHeight);
        if (maxOffset <= 0)
        {
            return null;
        }

        var nextOffset = Math.Clamp(currentOffset - (deltaY * ModSearchWheelScrollStep), 0, maxOffset);
        return Math.Abs(nextOffset - currentOffset) < 0.1 ? null : nextOffset;
    }

    private void AutoScrollModListRows(DragEventArgs e)
    {
        if (!isModDragActive
            || FindModListRowsScrollViewer() is not { } scrollViewer)
        {
            return;
        }

        var requestedOffset = CalculateModListDragAutoScrollOffset(
            scrollViewer.Offset.Y,
            GetMaxScrollOffset(scrollViewer),
            scrollViewer.Bounds.Height,
            e.GetPosition(scrollViewer).Y);

        if (SetScrollViewerVerticalOffset(scrollViewer, requestedOffset))
        {
            SyncModListRowsScrollThumbFromScrollViewer();
        }
    }

    internal static double CalculateModListDragAutoScrollOffset(
        double currentOffset,
        double maxOffset,
        double viewportHeight,
        double pointerY)
    {
        if (maxOffset <= 0 || viewportHeight <= 0)
        {
            return Math.Clamp(currentOffset, 0, Math.Max(0, maxOffset));
        }

        var edge = Math.Min(ModListDragAutoScrollEdgePixels, viewportHeight / 3);
        if (edge <= 0)
        {
            return Math.Clamp(currentOffset, 0, maxOffset);
        }

        var direction = pointerY < edge
            ? -1
            : pointerY > viewportHeight - edge
                ? 1
                : 0;
        if (direction == 0)
        {
            return Math.Clamp(currentOffset, 0, maxOffset);
        }

        var distanceIntoEdge = direction < 0
            ? edge - pointerY
            : pointerY - (viewportHeight - edge);
        var intensity = Math.Clamp(distanceIntoEdge / edge, 0, 1);
        var step = Math.Max(4, ModListDragAutoScrollMaximumStep * intensity);
        return Math.Clamp(currentOffset + (direction * step), 0, maxOffset);
    }

    private static double GetMaxScrollOffset(ScrollViewer? scrollViewer)
    {
        return scrollViewer is null ? 0 : Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
    }

    private static double GetScrollThumbHeight(ScrollViewer? scrollViewer, Control track)
    {
        var trackHeight = track.Bounds.Height;
        if (trackHeight <= 0)
        {
            return ModSearchScrollThumbMinHeight;
        }

        if (scrollViewer is null)
        {
            return trackHeight;
        }

        var extentHeight = scrollViewer.Extent.Height;
        var viewportHeight = scrollViewer.Viewport.Height;
        if (extentHeight <= 0 || viewportHeight <= 0 || extentHeight <= viewportHeight)
        {
            return trackHeight;
        }

        return Math.Clamp(trackHeight * (viewportHeight / extentHeight), ModSearchScrollThumbMinHeight, trackHeight);
    }

    private static double? GetScrollOffsetForTrackPosition(
        ScrollViewer scrollViewer,
        Control track,
        double thumbPointerOffsetY,
        double pointerY)
    {
        var maxOffset = GetMaxScrollOffset(scrollViewer);
        var trackHeight = track.Bounds.Height;
        var thumbHeight = GetScrollThumbHeight(scrollViewer, track);
        return CalculateScrollOffsetForTrackPosition(
            maxOffset,
            trackHeight,
            thumbHeight,
            thumbPointerOffsetY,
            pointerY);
    }

    internal static double? CalculateScrollOffsetForTrackPositionForTesting(
        double maxOffset,
        double trackHeight,
        double thumbHeight,
        double thumbPointerOffsetY,
        double pointerY) =>
        CalculateScrollOffsetForTrackPosition(
            maxOffset,
            trackHeight,
            thumbHeight,
            thumbPointerOffsetY,
            pointerY);

    private static double? CalculateScrollOffsetForTrackPosition(
        double maxOffset,
        double trackHeight,
        double thumbHeight,
        double thumbPointerOffsetY,
        double pointerY)
    {
        var travel = Math.Max(0, trackHeight - thumbHeight);
        if (maxOffset <= 0 || travel <= 0)
        {
            return null;
        }

        var thumbTop = Math.Clamp(pointerY - thumbPointerOffsetY, 0, travel);
        return (thumbTop / travel) * maxOffset;
    }

    private static void SyncScrollThumb(ScrollViewer? scrollViewer, Control track, Border thumb)
    {
        var maxOffset = GetMaxScrollOffset(scrollViewer);
        var trackHeight = track.Bounds.Height;
        var thumbHeight = GetScrollThumbHeight(scrollViewer, track);
        var travel = Math.Max(0, trackHeight - thumbHeight);
        var thumbTop = scrollViewer is null || maxOffset <= 0 || travel <= 0
            ? 0
            : CalculateScrollThumbTop(scrollViewer.Offset.Y, maxOffset, trackHeight, thumbHeight);

        thumb.Height = thumbHeight;
        ((TranslateTransform)thumb.RenderTransform!).Y = thumbTop;
        track.Opacity = maxOffset > 0.5 ? 1 : 0.45;
    }

    internal static double CalculateScrollThumbTopForTesting(
        double verticalOffset,
        double maxOffset,
        double trackHeight,
        double thumbHeight) =>
        CalculateScrollThumbTop(verticalOffset, maxOffset, trackHeight, thumbHeight);

    private static double CalculateScrollThumbTop(
        double verticalOffset,
        double maxOffset,
        double trackHeight,
        double thumbHeight)
    {
        var travel = Math.Max(0, trackHeight - thumbHeight);
        return maxOffset <= 0 || travel <= 0
            ? 0
            : Math.Clamp(verticalOffset / maxOffset, 0, 1) * travel;
    }

    private async Task AnimateLauncherDetailsPanelAsync(bool show)
    {
        launcherPanelAnimationCancellation = ReplaceCancellation(ref launcherPanelAnimationCancellation);
        var token = launcherPanelAnimationCancellation.Token;

        try
        {
            if (show)
            {
                await EnsureLauncherDetailsContentAsync(token);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!show && !LauncherDetailsContentHost.IsActive)
        {
            isLauncherDetailsOpen = false;
            return;
        }

        var startY = LauncherContentTrackTranslate.Y;
        var startToggleY = LauncherToggleButtonTranslate.Y;
        var startIconAngle = LauncherToggleIconRotate.Angle;
        var (openTrackY, _) = GetLauncherDetailsScrollMetrics();
        var surfaceHeight = LauncherSurface.Bounds.Height;
        if (show)
        {
            launcherDetailsScrollOffset = 0;
        }

        var targetY = show ? openTrackY : 0;
        var targetToggleY = show
            ? 0
            : Math.Clamp(surfaceHeight - LauncherToggleButton.Bounds.Height - 4, 0, 612);
        var targetIconAngle = show ? 180 : 0;
        var durationMs = show ? 760 : 320;
        var start = DateTimeOffset.UtcNow;

        try
        {
            if (subscribedViewModel?.ReduceMotion == true)
            {
                const int fadeDurationMs = 140;
                var fadeStartOpacity = LauncherDetailsHost.Opacity;

                if (show)
                {
                    LauncherContentTrackTranslate.Y = targetY;
                    LauncherToggleButtonTranslate.Y = targetToggleY;
                    LauncherToggleIconRotate.Angle = targetIconAngle;
                    LauncherDetailsScale.ScaleX = 1;
                    LauncherDetailsScale.ScaleY = 1;
                    fadeStartOpacity = 0;
                    LauncherDetailsHost.Opacity = 0;
                    RefreshLauncherDetailsBackdrop();
                }

                var fadeStart = DateTimeOffset.UtcNow;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    var elapsed = (DateTimeOffset.UtcNow - fadeStart).TotalMilliseconds;
                    var t = Math.Clamp(elapsed / fadeDurationMs, 0, 1);
                    var eased = EaseOutCubic(t);
                    LauncherDetailsHost.Opacity = show
                        ? Lerp(fadeStartOpacity, 1, eased)
                        : Lerp(fadeStartOpacity, 0, eased);
                    RefreshLauncherDetailsBackdrop();

                    if (t >= 1)
                    {
                        break;
                    }

                    await Task.Delay(16, token);
                }

                if (!show)
                {
                    LauncherContentTrackTranslate.Y = targetY;
                    LauncherToggleButtonTranslate.Y = targetToggleY;
                    LauncherToggleIconRotate.Angle = targetIconAngle;
                    LauncherDetailsScale.ScaleX = 1;
                    LauncherDetailsScale.ScaleY = 1;
                    RefreshLauncherDetailsBackdrop();
                }

                isLauncherDetailsOpen = show;
                if (!show)
                {
                    launcherDetailsScrollOffset = 0;
                    ReleaseLauncherDetailsContent();
                }
                RefreshLauncherDetailsBackdrop();
                return;
            }

            LauncherDetailsHost.Opacity = 1;

            while (true)
            {
                token.ThrowIfCancellationRequested();

                var elapsed = (DateTimeOffset.UtcNow - start).TotalMilliseconds;
                var t = Math.Clamp(elapsed / durationMs, 0, 1);
                var curve = show ? SpringUp(t) : EaseInCubic(t);

                LauncherContentTrackTranslate.Y = Lerp(startY, targetY, curve);
                LauncherToggleButtonTranslate.Y = Lerp(startToggleY, targetToggleY, curve);
                LauncherToggleIconRotate.Angle = Lerp(startIconAngle, targetIconAngle, curve);
                LauncherDetailsScale.ScaleX = 1;
                LauncherDetailsScale.ScaleY = 1;
                RefreshLauncherDetailsBackdrop();

                if (t >= 1)
                {
                    break;
                }

                await Task.Delay(16, token);
            }

            LauncherContentTrackTranslate.Y = targetY;
            LauncherToggleButtonTranslate.Y = targetToggleY;
            LauncherToggleIconRotate.Angle = targetIconAngle;
            LauncherDetailsHost.Opacity = show ? 1 : 0;
            LauncherDetailsScale.ScaleX = 1;
            LauncherDetailsScale.ScaleY = 1;
            isLauncherDetailsOpen = show;
            if (!show)
            {
                launcherDetailsScrollOffset = 0;
                ReleaseLauncherDetailsContent();
            }
            RefreshLauncherDetailsBackdrop();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private (double OpenTrackY, double MaxScrollOffset) GetLauncherDetailsScrollMetrics()
    {
        const double detailsOverflowTop = 64;
        const double detailsOpenTop = 84;
        var detailsTop = LauncherDetailsHost.Bounds.Y + detailsOverflowTop;
        var detailsHeight = LauncherDetailsHost.Bounds.Height > detailsOverflowTop
            ? LauncherDetailsHost.Bounds.Height - detailsOverflowTop
            : 786;
        var surfaceHeight = LauncherSurface.Bounds.Height;
        var openTop = Math.Max(detailsOpenTop, (surfaceHeight - detailsHeight) / 2);
        var openTrackY = openTop - detailsTop;
        var maxScrollOffset = Math.Max(
            0,
            openTop + detailsHeight + LauncherDetailsBottomPadding - surfaceHeight);

        return (openTrackY, maxScrollOffset);
    }

    private void ApplyLauncherDetailsScrollOffset()
    {
        var (openTrackY, maxScrollOffset) = GetLauncherDetailsScrollMetrics();
        launcherDetailsScrollOffset = Math.Clamp(launcherDetailsScrollOffset, 0, maxScrollOffset);
        LauncherContentTrackTranslate.Y = openTrackY - launcherDetailsScrollOffset;
        RefreshLauncherDetailsBackdrop();
    }

    private void ClampLauncherDetailsScrollOffset()
    {
        if (!isLauncherDetailsOpen)
        {
            return;
        }

        ApplyLauncherDetailsScrollOffset();
    }

    private void RefreshLauncherDetailsBackdrop()
    {
        foreach (var reflection in LauncherDetailsContentHost.GetVisualDescendants().OfType<BackdropReflection>())
        {
            reflection.InvalidateVisual();
        }
    }

    private async Task EnsureLauncherDetailsContentAsync(CancellationToken token)
    {
        var viewModel = subscribedViewModel;
        if (viewModel is null)
        {
            return;
        }

        viewModel.ActivateLauncherDetailsVisualResources();
        if (LauncherDetailsContentHost.IsActive)
        {
            return;
        }

        if (LauncherDetailsContentHost.Activate(viewModel))
        {
            _ = viewModel.EnsureLauncherDetailsDataLoadedAsync();
        }
        LauncherDetailsContentHost.InvalidateMeasure();
        await Dispatcher.UIThread.InvokeAsync(
            () => LauncherDetailsContentHost.InvalidateArrange(),
            DispatcherPriority.Background);
        await Dispatcher.UIThread.InvokeAsync(
            RefreshLauncherDetailsBackdrop,
            DispatcherPriority.Render);
        token.ThrowIfCancellationRequested();
    }

    private void ReleaseLauncherDetailsContent()
    {
        var viewModel = subscribedViewModel;
        viewModel?.DeactivateLauncherDetailsVisualResources();
        var releasedContent = LauncherDetailsContentHost.Deactivate();
        if (viewModel is null && !releasedContent)
        {
            return;
        }

        SharedBitmapLeaseCache.TrimUnused();
    }

    private static CancellationTokenSource ReplaceCancellation(ref CancellationTokenSource? cancellation)
    {
        CancelAndDispose(ref cancellation);
        return new CancellationTokenSource();
    }

    private static void CancelAndDispose(ref CancellationTokenSource? cancellation)
    {
        var current = cancellation;
        cancellation = null;
        if (current is null)
        {
            return;
        }

        current.Cancel();
        current.Dispose();
    }

    private static double Lerp(double from, double to, double amount)
    {
        return from + ((to - from) * amount);
    }

    private static double EaseOutCubic(double value)
    {
        return 1 - Math.Pow(1 - value, 3);
    }

    private static double EaseOutQuart(double value)
    {
        return 1 - Math.Pow(1 - value, 4);
    }

    private static double EaseOutExpo(double value)
    {
        return value >= 1 ? 1 : 1 - Math.Pow(2, -10 * value);
    }

    private static double EaseOutBack(double value)
    {
        const double overshoot = 1.12;
        return 1 + ((overshoot + 1) * Math.Pow(value - 1, 3)) + (overshoot * Math.Pow(value - 1, 2));
    }

    private static double EaseInCubic(double value)
    {
        return value * value * value;
    }

    private static double SpringUp(double value)
    {
        return SmootherStep(value);
    }

    private static double SmoothStep(double value)
    {
        value = Math.Clamp(value, 0, 1);
        return value * value * (3 - (2 * value));
    }

    private static double SmootherStep(double value)
    {
        value = Math.Clamp(value, 0, 1);
        return value * value * value * ((value * ((value * 6) - 15)) + 10);
    }
}
