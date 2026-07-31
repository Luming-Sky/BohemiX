using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using BohemiX.App.Controls;

namespace BohemiX.App.Tests;

public sealed class VisualResourceLifetimeTests
{
    [AvaloniaFact]
    public void VisibleContainer_ActivatesOwnerAndReleasesItWhenHiddenOrDetached()
    {
        var owner = new TestVisualResourceOwner();
        var row = new Border { DataContext = owner };
        VisualResourceLifetime.SetIsEnabled(row, true);
        var host = new Border { Child = row };
        var window = new Window { Content = host };

        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, owner.ActivationCount);
            Assert.Equal(0, owner.DeactivationCount);

            host.IsVisible = false;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, owner.DeactivationCount);

            host.IsVisible = true;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, owner.ActivationCount);

            window.Content = null;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, owner.DeactivationCount);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void MultipleVisibleContainersKeepOwnerActiveUntilTheLastReferenceIsHidden()
    {
        var owner = new TestVisualResourceOwner();
        var listRow = new Border { DataContext = owner };
        var inspector = new Border { DataContext = owner };
        VisualResourceLifetime.SetIsEnabled(listRow, true);
        VisualResourceLifetime.SetIsEnabled(inspector, true);
        var host = new StackPanel { Children = { listRow, inspector } };
        var window = new Window { Content = host };

        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, owner.ActivationCount);

            listRow.IsVisible = false;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, owner.DeactivationCount);

            inspector.IsVisible = false;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, owner.DeactivationCount);
        }
        finally
        {
            window.Close();
        }
    }

    private sealed class TestVisualResourceOwner : IVisualResourceOwner
    {
        public int ActivationCount { get; private set; }

        public int DeactivationCount { get; private set; }

        public void ActivateVisualResources() => ActivationCount++;

        public void DeactivateVisualResources() => DeactivationCount++;
    }
}
