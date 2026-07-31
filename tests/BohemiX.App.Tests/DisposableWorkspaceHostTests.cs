using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BohemiX.App.Controls;
using BohemiX.App.Views;

namespace BohemiX.App.Tests;

public sealed class DisposableWorkspaceHostTests
{
    [AvaloniaFact]
    public void ActivateAndDeactivate_ReleasesHostedContentWithoutDisposingState()
    {
        var state = new object();
        var host = new DisposableWorkspaceHost { Kind = WorkspaceKind.Mods };

        host.Activate(state);
        Assert.True(host.IsActive);
        Assert.Same(state, host.Content);

        host.Deactivate();
        Assert.False(host.IsActive);
        Assert.Null(host.Content);
    }

    [AvaloniaFact]
    public void RepeatedActivationAndDeactivation_AreIdempotent()
    {
        var state = new object();
        var host = new DisposableWorkspaceHost();

        Assert.True(host.Activate(state));
        Assert.False(host.Activate(state));
        Assert.True(host.Deactivate());
        Assert.False(host.Deactivate());
    }

    [AvaloniaFact]
    public void LauncherDetailsTemplate_IsCreatedOnlyWhileHostIsActive()
    {
        var host = new DisposableWorkspaceHost
        {
            LazyContentTemplate = new FuncDataTemplate<object>((_, _) => new LauncherDetailsView())
        };
        var window = new Window { Content = host };

        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(host.GetVisualDescendants().OfType<LauncherDetailsView>());

            host.Activate(new object());
            Dispatcher.UIThread.RunJobs();
            Assert.Single(host.GetVisualDescendants().OfType<LauncherDetailsView>());

            host.Deactivate();
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(host.GetVisualDescendants().OfType<LauncherDetailsView>());

            host.Deactivate();
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(host.GetVisualDescendants().OfType<LauncherDetailsView>());
        }
        finally
        {
            window.Close();
        }
    }
}
