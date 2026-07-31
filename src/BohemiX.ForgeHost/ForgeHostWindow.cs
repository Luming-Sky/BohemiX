using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using BohemiX.Core.Models;
using BohemiX.Modules.Forge.Models;
using BohemiX.Modules.Forge.ViewModels;
using BohemiX.Modules.Forge.Views;

namespace BohemiX.ForgeHost;

internal sealed class ForgeHostWindow : Window
{
    private const int GwlHwndParent = -8;
    private readonly ForgeWorkshopViewModel viewModel;
    private readonly ForgeWorkshopView workshopView;
    private readonly TextReader input = Console.In;
    private readonly TextWriter output = Console.Out;
    private CancellationTokenSource? protocolCancellation;
    private bool initialized;

    public ForgeHostWindow(ForgeWorkshopViewModel viewModel)
    {
        this.viewModel = viewModel;
        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false;
        CanResize = false;
        Background = null;
        workshopView = new ForgeWorkshopView { DataContext = viewModel };
        Content = workshopView;
        Opened += OnOpened;
        Closed += OnClosed;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        IsVisible = false;
        protocolCancellation = new CancellationTokenSource();
        try
        {
            await viewModel.ReloadPlayerProfileAsync();
            await RunProtocolAsync(protocolCancellation.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            await SendAsync(new ForgeHostMessage(ForgeHostProtocol.Version, "error", Error: ex.Message));
            Close();
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        protocolCancellation?.Cancel();
        protocolCancellation?.Dispose();
        protocolCancellation = null;
        viewModel.Deactivate();
    }

    private async Task RunProtocolAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await input.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                Close();
                return;
            }

            var message = JsonSerializer.Deserialize<ForgeHostMessage>(line, ForgeHostProtocol.JsonOptions)
                ?? throw new InvalidDataException("Forge host received an empty protocol message.");
            if (message.Version != ForgeHostProtocol.Version)
            {
                throw new InvalidDataException($"Forge host protocol {message.Version} is unsupported.");
            }

            switch (message.Type)
            {
                case "initialize":
                    ApplyOwner(message.OwnerHandle ?? 0);
                    ApplyBounds(message);
                    initialized = true;
                    IsVisible = message.Visible != false;
                    if (IsVisible)
                    {
                        Activate();
                    }
                    await SendAsync(ForgeHostMessage.Create("ready"));
                    break;
                case "bounds" when initialized:
                    ApplyBounds(message);
                    break;
                case "visibility" when initialized:
                    IsVisible = message.Visible == true;
                    break;
                case "quality" when initialized:
                    if (ParseQuality(message.Quality) is { } quality)
                    {
                        workshopView.RequestRenderQuality(quality);
                    }
                    break;
                case "shutdown":
                    Close();
                    return;
            }
        }
    }

    private static ForgeRenderQuality? ParseQuality(string? quality) => quality?.ToLowerInvariant() switch
    {
        "high" => ForgeRenderQuality.High,
        "balanced" or "medium" => ForgeRenderQuality.Medium,
        "low" or "low-memory" => ForgeRenderQuality.Low,
        _ => null
    };

    private void ApplyOwner(long ownerHandle)
    {
        if (!OperatingSystem.IsWindows() || ownerHandle == 0)
        {
            return;
        }

        var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle != IntPtr.Zero)
        {
            SetWindowLongPtr(handle, GwlHwndParent, new IntPtr(ownerHandle));
        }
    }

    private void ApplyBounds(ForgeHostMessage message)
    {
        var dpi = Math.Max(1, message.Dpi ?? 96);
        var scale = dpi / 96d;
        Position = new PixelPoint(message.X ?? Position.X, message.Y ?? Position.Y);
        Width = Math.Max(1, (message.Width ?? 1) / scale);
        Height = Math.Max(1, (message.Height ?? 1) / scale);
    }

    private async Task SendAsync(ForgeHostMessage message)
    {
        await output.WriteLineAsync(JsonSerializer.Serialize(message, ForgeHostProtocol.JsonOptions));
        await output.FlushAsync();
    }

    private static IntPtr SetWindowLongPtr(IntPtr handle, int index, IntPtr value) =>
        IntPtr.Size == 8
            ? SetWindowLongPtr64(handle, index, value)
            : new IntPtr(SetWindowLong32(handle, index, value.ToInt32()));

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
}
