using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Threading;

namespace BohemiX.App.Tests;

public sealed class ItemsRepeaterVirtualizationTests
{
    [AvaloniaFact]
    public void LongList_OnlyRealizesViewportElements()
    {
        const int itemCount = 1_000;
        var repeater = new ItemsRepeater
        {
            ItemsSource = Enumerable.Range(0, itemCount).ToArray(),
            Layout = new StackLayout { Orientation = Orientation.Vertical },
            VerticalCacheLength = 0,
            ItemTemplate = new FuncDataTemplate<int>((_, _) => new Border { Height = 40 })
        };
        var window = new Window
        {
            Width = 320,
            Height = 240,
            Content = new ScrollViewer { Content = repeater }
        };

        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var realizedCount = Enumerable.Range(0, itemCount)
                .Count(index => repeater.TryGetElement(index) is not null);

            Assert.InRange(realizedCount, 1, 20);
            Assert.Null(repeater.TryGetElement(itemCount - 1));
        }
        finally
        {
            window.Close();
        }
    }
}
