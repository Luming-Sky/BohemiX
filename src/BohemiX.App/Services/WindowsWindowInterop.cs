using System;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Controls;

namespace BohemiX.App.Services;

internal static class WindowsWindowInterop
{
    private const int SwRestore = 9;
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x00000020L;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExNoActivate = 0x08000000L;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

    public static void RestoreAndActivate(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return;
        }

        ShowWindow(handle, SwRestore);
        SetForegroundWindow(handle);
    }

    public static IntPtr FindActivatableWindow(int processId)
    {
        if (!OperatingSystem.IsWindows())
        {
            return IntPtr.Zero;
        }

        var preferred = IntPtr.Zero;
        var fallback = IntPtr.Zero;
        EnumWindows((handle, _) =>
        {
            GetWindowThreadProcessId(handle, out var ownerProcessId);
            if (ownerProcessId != processId || !IsWindowVisible(handle))
            {
                return true;
            }

            var style = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
            if ((style & WsExToolWindow) != 0)
            {
                return true;
            }

            fallback = fallback == IntPtr.Zero ? handle : fallback;
            var title = new StringBuilder(128);
            GetWindowText(handle, title, title.Capacity);
            if (string.Equals(title.ToString(), "BohemiX", StringComparison.OrdinalIgnoreCase))
            {
                preferred = handle;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        return preferred != IntPtr.Zero ? preferred : fallback;
    }

    public static void SetClickThrough(Window window, bool enabled)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var style = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        style = enabled
            ? style | WsExTransparent | WsExNoActivate | WsExToolWindow
            : style & ~(WsExTransparent | WsExNoActivate | WsExToolWindow);

        SetWindowLongPtr(handle, GwlExStyle, new IntPtr(style));
        SetWindowPos(
            handle,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
    }

    public static bool TrySetRoundedRegion(Window window, double cornerRadius)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        if (cornerRadius <= 0)
        {
            SetWindowRgn(handle, IntPtr.Zero, true);
            return true;
        }

        // SetWindowRgn receives physical HWND coordinates. Avalonia's logical
        // client size can still be stale while a transparent window is opening.
        if (!GetWindowRect(handle, out var windowRect))
        {
            return false;
        }

        var scale = window.RenderScaling;
        var width = windowRect.Right - windowRect.Left;
        var height = windowRect.Bottom - windowRect.Top;
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        var diameter = Math.Max(1, (int)Math.Round(cornerRadius * scale * 2));
        var region = CreateRoundRectRgn(0, 0, width, height, diameter, diameter);
        if (region == IntPtr.Zero)
        {
            return false;
        }

        if (SetWindowRgn(handle, region, true) != 0)
        {
            return true;
        }

        DeleteObject(region);
        return false;
    }

    private static IntPtr GetWindowLongPtr(IntPtr handle, int index) =>
        IntPtr.Size == 8
            ? GetWindowLongPtr64(handle, index)
            : new IntPtr(GetWindowLong32(handle, index));

    private static IntPtr SetWindowLongPtr(IntPtr handle, int index, IntPtr value) =>
        IntPtr.Size == 8
            ? SetWindowLongPtr64(handle, index, value)
            : new IntPtr(SetWindowLong32(handle, index, value.ToInt32()));

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

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

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect rectangle);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool redraw);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(
        int left,
        int top,
        int right,
        int bottom,
        int ellipseWidth,
        int ellipseHeight);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
