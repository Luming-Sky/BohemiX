using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using BohemiX.App.Controls;

namespace BohemiX.App.Tests;

public sealed class BackgroundLayerHostTests
{
    [AvaloniaFact]
    public void Activations_KeepExactlyOneRequestedLayer()
    {
        var host = new BackgroundLayerHost();
        var defaultLayer = new Border();
        var staticLayer = new Border();
        var animatedLayer = new Border();
        var videoLayer = new Border();

        Assert.True(host.ActivateDefault(defaultLayer));
        Assert.Equal(BackgroundLayerKind.Default, host.ActiveKind);
        Assert.Same(defaultLayer, Assert.Single(host.Children));

        Assert.True(host.ActivateStatic(staticLayer));
        Assert.Equal(BackgroundLayerKind.Static, host.ActiveKind);
        Assert.Same(staticLayer, Assert.Single(host.Children));

        Assert.True(host.ActivateAnimated(animatedLayer));
        Assert.Equal(BackgroundLayerKind.Animated, host.ActiveKind);
        Assert.Same(animatedLayer, Assert.Single(host.Children));

        Assert.True(host.ActivateVideo(videoLayer));
        Assert.Equal(BackgroundLayerKind.Video, host.ActiveKind);
        Assert.Same(videoLayer, Assert.Single(host.Children));
    }

    [AvaloniaFact]
    public void RepeatedActivationAndDeactivation_AreIdempotent()
    {
        var host = new BackgroundLayerHost();
        var layer = new Border();

        Assert.True(host.ActivateDefault(layer));
        Assert.False(host.ActivateDefault(layer));
        Assert.True(host.Deactivate());
        Assert.False(host.Deactivate());
        Assert.Equal(BackgroundLayerKind.None, host.ActiveKind);
        Assert.Empty(host.Children);
    }
}
