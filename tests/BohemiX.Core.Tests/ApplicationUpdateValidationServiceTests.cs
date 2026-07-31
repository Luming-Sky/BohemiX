using BohemiX.Infrastructure.Services;

namespace BohemiX.Core.Tests;

public sealed class ApplicationUpdateValidationServiceTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "BohemiX.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ValidateAsync_ReturnsFingerprintWhenPackageIsComplete()
    {
        Directory.CreateDirectory(root);
        await File.WriteAllBytesAsync(Path.Combine(root, "BohemiX.App.exe"), [1]);
        await File.WriteAllBytesAsync(Path.Combine(root, "BohemiX.App.dll"), [2, 3, 4]);
        await File.WriteAllTextAsync(Path.Combine(root, "BohemiX.App.deps.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(root, "BohemiX.App.runtimeconfig.json"), "{}");

        var result = await new ApplicationUpdateValidationService()
            .ValidateAsync(root, "BohemiX.App");

        Assert.True(result.IsReady);
        Assert.Equal(64, result.AssemblyFingerprint?.Length);
        Assert.Empty(result.MissingFiles);
    }

    [Fact]
    public async Task ValidateAsync_ReportsEveryMissingPackageFile()
    {
        Directory.CreateDirectory(root);

        var result = await new ApplicationUpdateValidationService()
            .ValidateAsync(root, "BohemiX.App");

        Assert.False(result.IsReady);
        Assert.Null(result.AssemblyFingerprint);
        Assert.Equal(4, result.MissingFiles.Count);
        Assert.Contains("BohemiX.App.runtimeconfig.json", result.MissingFiles);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
