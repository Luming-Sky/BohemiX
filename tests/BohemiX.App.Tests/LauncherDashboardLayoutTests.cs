using System.Xml.Linq;

namespace BohemiX.App.Tests;

public sealed class LauncherDashboardLayoutTests
{
    [Fact]
    public void ModOverview_DoesNotCreateANestedScrollViewer()
    {
        var sourcePath = FindRepositoryFile(Path.Combine(
            "src",
            "BohemiX.App",
            "Views",
            "MainWindow.axaml"));
        var document = XDocument.Load(sourcePath);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var modRows = Assert.Single(document
            .Descendants()
            .Where(element =>
                element.Name.LocalName == "ItemsControl"
                && (string?)element.Attribute("ItemsSource") == "{Binding ModRows}"));

        Assert.DoesNotContain(
            modRows.Ancestors(),
            element => element.Name.LocalName == "ScrollViewer");
        Assert.Equal("True", (string?)modRows.Parent?.Attribute("ClipToBounds"));
        Assert.DoesNotContain(
            document.Descendants(),
            element => (string?)element.Attribute(x + "Name") == "DashboardModRowsScrollViewer");
    }

    private static string FindRepositoryFile(string relativePath)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"Could not locate repository file '{relativePath}'.");
    }
}
