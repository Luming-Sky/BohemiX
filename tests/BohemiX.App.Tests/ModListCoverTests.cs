using BohemiX.App.ViewModels;
using BohemiX.Core.Models;

namespace BohemiX.App.Tests;

public sealed class ModListCoverTests
{
    private static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    [Fact]
    public void ModPackNexusFileIdUsesTheNexusModCoverCache()
    {
        var root = Path.Combine(Path.GetTempPath(), "BohemiX", Guid.NewGuid().ToString("N"));
        var cacheDirectory = Path.Combine(root, "mod-covers");
        Directory.CreateDirectory(cacheDirectory);
        var coverPath = Path.Combine(cacheDirectory, "123-cover.png");
        File.WriteAllBytes(coverPath, TinyPng);

        try
        {
            var manifest = new ModManifest(
                "nexus-123-file-456",
                "Collection child mod",
                "1.0.0",
                Path.Combine(root, "installed-mod"),
                0,
                true,
                []);

            using var row = new ModListItemViewModel(manifest, root);

            Assert.Equal(coverPath, row.CoverImagePath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CorruptCachedCoverDoesNotBlockRemoteCoverRecovery()
    {
        var root = Path.Combine(Path.GetTempPath(), "BohemiX", Guid.NewGuid().ToString("N"));
        var cacheDirectory = Path.Combine(root, "mod-covers");
        Directory.CreateDirectory(cacheDirectory);
        File.WriteAllBytes(Path.Combine(cacheDirectory, "123-corrupt.png"), []);

        try
        {
            var manifest = new ModManifest(
                "nexus-123-file-456",
                "Collection child mod",
                "1.0.0",
                Path.Combine(root, "installed-mod"),
                0,
                true,
                []);

            using var row = new ModListItemViewModel(manifest, root);

            Assert.Null(row.CoverImagePath);
            Assert.Equal(123, row.NexusModId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
