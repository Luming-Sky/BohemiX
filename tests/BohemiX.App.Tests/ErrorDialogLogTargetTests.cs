using BohemiX.App.Views;

namespace BohemiX.App.Tests;

public sealed class ErrorDialogLogTargetTests
{
    [Fact]
    public async Task ResolveLogTarget_PrefersNewestRollingLog()
    {
        var directory = CreateTempDirectory();
        try
        {
            var older = Path.Combine(directory, "bohemix-20260727.log");
            var newer = Path.Combine(directory, "bohemix-20260728.log");
            await File.WriteAllTextAsync(older, "older");
            await File.WriteAllTextAsync(newer, "newer");
            File.SetLastWriteTimeUtc(older, new DateTime(2026, 7, 27, 12, 0, 0, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(newer, new DateTime(2026, 7, 28, 12, 0, 0, DateTimeKind.Utc));

            Assert.Equal(newer, ErrorDialogWindow.ResolveLogTarget(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ResolveLogTarget_UsesExistingDirectoryWhenNoRollingLogExists()
    {
        var directory = CreateTempDirectory();
        try
        {
            Assert.Equal(directory, ErrorDialogWindow.ResolveLogTarget(directory));
            Assert.Null(ErrorDialogWindow.ResolveLogTarget(Path.Combine(directory, "missing")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bohemix-error-dialog-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
