using Avalonia;
using System;
using System.Runtime.InteropServices;
using BohemiX.App.Services;
using BohemiX.Core.Models;

namespace BohemiX.App;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        using var singleInstanceCoordinator = new SingleInstanceCoordinator();
        if (!singleInstanceCoordinator.TryAcquire())
        {
            return;
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            var report = ApplicationErrorReport.FromException(
                ex,
                "BohemiX 无法继续运行",
                ex.Message,
                "应用启动或主事件循环");
            NativeMessageBox.ShowError(report.Title, report.ToClipboardText());
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UseWin32()
            .UseSkia()
            .WithInterFont()
            .LogToTrace();
}

internal static class NativeMessageBox
{
    private const uint Ok = 0x00000000;
    private const uint ErrorIcon = 0x00000010;

    public static void ShowError(string title, string message)
    {
        if (OperatingSystem.IsWindows())
        {
            MessageBox(IntPtr.Zero, message, title, Ok | ErrorIcon);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBox(IntPtr owner, string text, string caption, uint type);
}
