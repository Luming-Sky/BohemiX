using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Serilog;

namespace BohemiX.App.Views;

public partial class StartupSplashWindow : Window
{
    internal const double LogoOnlyWindowSize = 256;
    internal const double FullWindowWidth = 1600;
    internal const double FullWindowHeight = 900;

    private const int LogoIntroDurationMs = 280;
    private const int LogoOnlyHoldDurationMs = 260;
    private const int LogoHoldDurationMs = 340;
    private const int StripeCoverDurationMs = 1140;
    private const int StripeHoldDurationMs = 180;
    private const int StripeSplitOpenDurationMs = 560;
    private const int ExitDurationMs = 80;

    private CancellationTokenSource? animationCancellation;
    private CancellationTokenSource? stripeScrollCancellation;
    private bool hasCompleted;

    private TransformGroup LogoTransforms => (TransformGroup)LogoStage.RenderTransform!;
    private ScaleTransform LogoScale => (ScaleTransform)LogoTransforms.Children[0];
    private TranslateTransform LogoTranslate => (TranslateTransform)LogoTransforms.Children[1];

    private TransformGroup StripeTransforms => (TransformGroup)StripeSurface.RenderTransform!;
    private ScaleTransform StripeScale => (ScaleTransform)StripeTransforms.Children[0];
    private TranslateTransform StripeTranslate => (TranslateTransform)StripeTransforms.Children[2];
    private TranslateTransform StripePatternTranslate => (TranslateTransform)StripePattern.RenderTransform!;
    private TranslateTransform StripeTopLeftCurtainTranslate => (TranslateTransform)StripeTopLeftCurtain.RenderTransform!;
    private TranslateTransform StripeBottomRightCurtainTranslate => (TranslateTransform)StripeBottomRightCurtain.RenderTransform!;

    public event EventHandler? SplashCompleted;
    public event EventHandler? MainWindowRevealRequested;

    public StartupSplashWindow()
    {
        InitializeComponent();

        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Background = null;
        Width = LogoOnlyWindowSize;
        Height = LogoOnlyWindowSize;

        Opened += (_, _) =>
        {
            ConfigureStartupOverlayWindow();
            _ = RunSplashSequenceAsync();
        };
        Closed += (_, _) =>
        {
            CancelAndDispose(ref animationCancellation);
            CancelAndDispose(ref stripeScrollCancellation);
        };
    }

    private async Task RunSplashSequenceAsync()
    {
        CancelAndDispose(ref animationCancellation);
        animationCancellation = new CancellationTokenSource();
        animationCancellation.CancelAfter(8000);
        var token = animationCancellation.Token;

        try
        {
            InitializeLayers();
            await AnimateLogoEntranceAsync(token);
            await Task.Delay(LogoOnlyHoldDurationMs, token);
            await Task.Delay(LogoHoldDurationMs, token);
            ExpandToFullWindow();
            StartStripeScrollLoop();
            await AnimateStripeCoverAsync(token);
            await Task.Delay(StripeHoldDurationMs, token);

            PrepareStripeSplitCurtain();
            RequestMainWindowReveal();
            await AnimateStripeSplitOpenAsync(token);
            CompleteSplash();
            await Task.Delay(24, token);
            await AnimateExitAsync(token);
        }
        catch (OperationCanceledException)
        {
            CompleteSplash();
        }
        catch (Exception ex)
        {
            Log.Logger.Error(ex, "Startup splash animation failed");
            CompleteSplash();
        }
        finally
        {
            CancelAndDispose(ref stripeScrollCancellation);
            CompleteSplash();
            Close();
        }
    }

    private void InitializeLayers()
    {
        Opacity = 1;

        LogoStage.Opacity = 1;
        LogoScale.ScaleX = 0.86;
        LogoScale.ScaleY = 0.86;
        LogoTranslate.Y = 8;

        AcrylicMask.Opacity = 0;
        AcrylicBloom.Opacity = 0;
        AcrylicBody.Opacity = 0;
        AcrylicFrost.Opacity = 0;
        AcrylicShade.Opacity = 0;
        AcrylicGrain.Opacity = 0;
        AcrylicRim.Opacity = 0;

        StripeMask.Opacity = 0;
        StripeScale.ScaleX = 0.08;
        StripeScale.ScaleY = 0.08;
        StripeTranslate.X = 0;
        StripeTranslate.Y = 0;
        StripePatternTranslate.X = 0;
        StripePatternTranslate.Y = 0;
        StripeSurface.Opacity = 1;
        StripeSplitCurtain.Opacity = 0;
        StripeTopLeftCurtainTranslate.X = 0;
        StripeTopLeftCurtainTranslate.Y = 0;
        StripeBottomRightCurtainTranslate.X = 0;
        StripeBottomRightCurtainTranslate.Y = 0;
        StripeVignette.Opacity = 0;
    }

    private async Task AnimateLogoEntranceAsync(CancellationToken token)
    {
        var start = DateTimeOffset.UtcNow;

        while (true)
        {
            token.ThrowIfCancellationRequested();

            var t = Progress(start, LogoIntroDurationMs);
            var curve = EaseOutCubic(t);

            LogoScale.ScaleX = Lerp(0.86, 1, curve);
            LogoScale.ScaleY = Lerp(0.86, 1, curve);
            LogoTranslate.Y = Lerp(8, 0, curve);

            if (t >= 1)
            {
                break;
            }

            await Task.Delay(16, token);
        }
    }

    private async Task AnimateStripeCoverAsync(CancellationToken token)
    {
        var start = DateTimeOffset.UtcNow;

        while (true)
        {
            token.ThrowIfCancellationRequested();

            var t = Progress(start, StripeCoverDurationMs);
            var reveal = EaseOutExpo(t);
            var logoOut = EaseInCubic(Clamp01((t - 0.68) / 0.32));

            StripeMask.Opacity = Lerp(0, 1, EaseOutCubic(Clamp01(t * 1.35)));
            StripeScale.ScaleX = Lerp(0.08, 1.0, reveal);
            StripeScale.ScaleY = Lerp(0.08, 1.0, reveal);
            StripeVignette.Opacity = Lerp(0, 1, EaseOutCubic(Clamp01((t - 0.2) / 0.8)));

            LogoStage.Opacity = Lerp(1, 0, logoOut);
            LogoScale.ScaleX = Lerp(1, 0.94, logoOut);
            LogoScale.ScaleY = Lerp(1, 0.94, logoOut);

            if (t >= 1)
            {
                break;
            }

            await Task.Delay(16, token);
        }

        StripeMask.Opacity = 1;
        StripeScale.ScaleX = 1.0;
        StripeScale.ScaleY = 1.0;
        StripeVignette.Opacity = 1;
        AcrylicMask.Opacity = 0;
        LogoStage.Opacity = 0;
    }

    private void PrepareStripeSplitCurtain()
    {
        BaseBackdrop.Opacity = 0;
        StripeSurface.Opacity = 0;
        StripeSplitCurtain.Opacity = 1;
        StripeVignette.Opacity = 0;
        StripeTopLeftCurtainTranslate.X = 0;
        StripeTopLeftCurtainTranslate.Y = 0;
        StripeBottomRightCurtainTranslate.X = 0;
        StripeBottomRightCurtainTranslate.Y = 0;
    }

    private async Task AnimateStripeSplitOpenAsync(CancellationToken token)
    {
        var start = DateTimeOffset.UtcNow;
        var travelX = Math.Max(1120, Bounds.Width * 0.82 + 180);
        var travelY = Math.Max(760, Bounds.Height * 0.82 + 160);

        while (true)
        {
            token.ThrowIfCancellationRequested();

            var t = Progress(start, StripeSplitOpenDurationMs);
            var open = EaseOutCubic(t);

            StripeTopLeftCurtainTranslate.X = Lerp(0, -travelX, open);
            StripeTopLeftCurtainTranslate.Y = Lerp(0, -travelY, open);
            StripeBottomRightCurtainTranslate.X = Lerp(0, travelX, open);
            StripeBottomRightCurtainTranslate.Y = Lerp(0, travelY, open);
            StripeSplitCurtain.Opacity = Lerp(1, 0.86, EaseInCubic(Clamp01((t - 0.72) / 0.28)));

            if (t >= 1)
            {
                break;
            }

            await Task.Delay(16, token);
        }

        StripeTopLeftCurtainTranslate.X = -travelX;
        StripeTopLeftCurtainTranslate.Y = -travelY;
        StripeBottomRightCurtainTranslate.X = travelX;
        StripeBottomRightCurtainTranslate.Y = travelY;
        StripeSplitCurtain.Opacity = 0;
    }

    private void StartStripeScrollLoop()
    {
        CancelAndDispose(ref stripeScrollCancellation);
        stripeScrollCancellation = new CancellationTokenSource();
        _ = RunStripeScrollLoopAsync(stripeScrollCancellation.Token);
    }

    private void ExpandToFullWindow()
    {
        var scale = RenderScaling;
        var centerX = Position.X + (Width * scale / 2);
        var centerY = Position.Y + (Height * scale / 2);

        Width = FullWindowWidth;
        Height = FullWindowHeight;
        Position = new PixelPoint(
            (int)Math.Round(centerX - (FullWindowWidth * scale / 2)),
            (int)Math.Round(centerY - (FullWindowHeight * scale / 2)));
    }

    private async Task RunStripeScrollLoopAsync(CancellationToken token)
    {
        var start = DateTimeOffset.UtcNow;

        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();

                var elapsed = (DateTimeOffset.UtcNow - start).TotalMilliseconds;
                StripeTranslate.X = 0;
                StripeTranslate.Y = 0;
                var drift = elapsed * 0.052 % 180;
                StripePatternTranslate.X = -drift;
                StripePatternTranslate.Y = -drift;

                await Task.Delay(16, token);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task AnimateExitAsync(CancellationToken token)
    {
        var start = DateTimeOffset.UtcNow;

        while (true)
        {
            token.ThrowIfCancellationRequested();

            var t = Progress(start, ExitDurationMs);
            Opacity = Lerp(1, 0, EaseInCubic(t));

            if (t >= 1)
            {
                break;
            }

            await Task.Delay(16, token);
        }
    }

    private void CompleteSplash()
    {
        if (hasCompleted)
        {
            return;
        }

        hasCompleted = true;
        SplashCompleted?.Invoke(this, EventArgs.Empty);
    }

    private static void CancelAndDispose(ref CancellationTokenSource? cancellation)
    {
        var current = cancellation;
        cancellation = null;
        current?.Cancel();
        current?.Dispose();
    }

    private void RequestMainWindowReveal()
    {
        MainWindowRevealRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ConfigureStartupOverlayWindow()
    {
        Topmost = true;

        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var style = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        style |= WsExNoActivate | WsExTransparent | WsExToolWindow;
        SetWindowLongPtr(handle, GwlExStyle, new IntPtr(style));
        SetWindowPos(
            handle,
            HwndTopmost,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpNoActivate | SwpFrameChanged);
    }

    private static double Progress(DateTimeOffset start, double durationMs)
    {
        return Clamp01((DateTimeOffset.UtcNow - start).TotalMilliseconds / durationMs);
    }

    private static double Clamp01(double value)
    {
        return Math.Clamp(value, 0, 1);
    }

    private static double Lerp(double start, double end, double t)
    {
        return start + (end - start) * t;
    }

    private static double EaseOutCubic(double t)
    {
        return 1 - Math.Pow(1 - t, 3);
    }

    private static double EaseInCubic(double t)
    {
        return t * t * t;
    }

    private static double EaseOutExpo(double t)
    {
        return t >= 1 ? 1 : 1 - Math.Pow(2, -10 * t);
    }

    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x00000020L;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExNoActivate = 0x08000000L;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private static readonly IntPtr HwndTopmost = new(-1);

    private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
    {
        return IntPtr.Size == 8
            ? GetWindowLongPtr64(hWnd, nIndex)
            : new IntPtr(GetWindowLong32(hWnd, nIndex));
    }

    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
    {
        return IntPtr.Size == 8
            ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong)
            : new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);
}
