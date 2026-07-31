using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace BohemiX.App.Services;

/// <summary>
/// Samples the visible KCD2 window while it is running. Capturing before exit
/// avoids the empty/black frame that Windows returns after the process closes.
/// </summary>
public sealed class GameExitThumbnailCaptureService : IGameExitThumbnailCaptureService
{
    private const int CaptureIntervalMilliseconds = 1500;
    private const int MaxWidth = 640;
    private const int MaxHeight = 360;
    private readonly ConcurrentDictionary<int, CaptureSession> sessions = new();

    public void BeginCapture(int processId)
    {
        if (processId <= 0 || sessions.ContainsKey(processId))
        {
            return;
        }

        var session = new CaptureSession();
        if (!sessions.TryAdd(processId, session))
        {
            session.Dispose();
            return;
        }

        session.Worker = Task.Run(() => CaptureLoopAsync(processId, session));
    }

    public async Task<byte[]?> CompleteCaptureAsync(int processId, CancellationToken cancellationToken = default)
    {
        if (!sessions.TryRemove(processId, out var session))
        {
            return null;
        }

        session.Cancellation.Cancel();
        try
        {
            await session.Worker.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The capture loop is best-effort; use the most recent completed frame.
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The capture loop is best-effort; use the most recent completed frame.
        }
        finally
        {
            session.Cancellation.Dispose();
        }

        lock (session.Gate)
        {
            return session.LastFrame is null ? null : (byte[])session.LastFrame.Clone();
        }
    }

    public void Dispose()
    {
        CancelAll();
    }

    public void CancelAll()
    {
        foreach (var processId in sessions.Keys)
        {
            if (!sessions.TryRemove(processId, out var session))
            {
                continue;
            }

            session.Dispose();
        }
    }

    private static async Task CaptureLoopAsync(int processId, CaptureSession session)
    {
        while (!session.Cancellation.IsCancellationRequested)
        {
            var frame = TryCapture(processId);
            if (frame is not null)
            {
                lock (session.Gate)
                {
                    session.LastFrame = frame;
                }
            }

            try
            {
                await Task.Delay(CaptureIntervalMilliseconds, session.Cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static byte[]? TryCapture(int processId)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            var handle = process.MainWindowHandle;
            if (handle == IntPtr.Zero || GetForegroundWindow() != handle || !GetWindowRect(handle, out var bounds))
            {
                return null;
            }

            var width = bounds.Right - bounds.Left;
            var height = bounds.Bottom - bounds.Top;
            if (width < 64 || height < 64)
            {
                return null;
            }

            using var source = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
            using (var graphics = Graphics.FromImage(source))
            {
                graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, source.Size, CopyPixelOperation.SourceCopy);
            }

            var scale = Math.Min(1d, Math.Min((double)MaxWidth / width, (double)MaxHeight / height));
            var targetWidth = Math.Max(1, (int)Math.Round(width * scale));
            var targetHeight = Math.Max(1, (int)Math.Round(height * scale));
            using var thumbnail = new Bitmap(targetWidth, targetHeight, PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(thumbnail))
            {
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.DrawImage(source, new Rectangle(0, 0, targetWidth, targetHeight));
            }

            using var output = new MemoryStream();
            thumbnail.Save(output, ImageFormat.Png);
            return output.ToArray();
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect rectangle);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private sealed class CaptureSession : IDisposable
    {
        public object Gate { get; } = new();
        public CancellationTokenSource Cancellation { get; } = new();
        public Task Worker { get; set; } = Task.CompletedTask;
        public byte[]? LastFrame { get; set; }

        public void Dispose()
        {
            Cancellation.Cancel();
            Cancellation.Dispose();
        }
    }
}
