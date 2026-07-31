using Avalonia.Headless.XUnit;
using BohemiX.App.Views;

namespace BohemiX.App.Tests;

public sealed class StartupSplashWindowTests
{
    [AvaloniaFact]
    public void LogoOnlyPhase_UsesCompactWindowFootprint()
    {
        var window = new StartupSplashWindow();

        Assert.Equal(StartupSplashWindow.LogoOnlyWindowSize, window.Width);
        Assert.Equal(StartupSplashWindow.LogoOnlyWindowSize, window.Height);
        Assert.True(window.Width < StartupSplashWindow.FullWindowWidth);
        Assert.True(window.Height < StartupSplashWindow.FullWindowHeight);
    }
}
