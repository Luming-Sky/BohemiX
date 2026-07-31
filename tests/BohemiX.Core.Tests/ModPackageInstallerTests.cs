using System.IO.Compression;
using BohemiX.Core.Models;
using BohemiX.Infrastructure.Services;
using Serilog.Core;

namespace BohemiX.Core.Tests;

public sealed class ModPackageInstallerTests
{
    [Fact]
    public async Task InstallAsync_LiftsSingleNestedZipRootAndWritesManifest()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var packagePath = Path.Combine(tempRoot, "nested.zip");
            using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("Some Mod/Data/config.xml");
                await using var stream = entry.Open();
                await using var writer = new StreamWriter(stream);
                await writer.WriteAsync("<config />");
            }

            var installer = new ModPackageInstaller(Logger.None);
            var modsDirectory = Path.Combine(tempRoot, "Mods");
            var result = await installer.InstallAsync(new ModPackageInstallRequest(
                packagePath,
                modsDirectory,
                "nexus-42",
                "Some Mod",
                "1.2.3"));

            Assert.True(result.Success, result.Message);
            Assert.NotNull(result.InstalledRootPath);
            Assert.True(File.Exists(Path.Combine(result.InstalledRootPath, "Data", "config.xml")));
            Assert.False(Directory.Exists(Path.Combine(result.InstalledRootPath, "Some Mod")));
            Assert.True(File.Exists(Path.Combine(result.InstalledRootPath, "bohemix.mod.json")));
            Assert.True(File.Exists(Path.Combine(result.InstalledRootPath, "bohemix.install.json")));
            Assert.NotNull(result.TransactionId);
            Assert.Empty(Directory.GetDirectories(Path.Combine(modsDirectory, ".transactions")));
        }
        finally
        {
            DeleteTempDirectory(tempRoot);
        }
    }

    [Fact]
    public async Task InstallAsync_RejectsZipEntriesEscapingInstallRoot()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var packagePath = Path.Combine(tempRoot, "escape.zip");
            using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("../escape.txt");
                await using var stream = entry.Open();
                await using var writer = new StreamWriter(stream);
                await writer.WriteAsync("bad");
            }

            var installer = new ModPackageInstaller(Logger.None);
            var modsDirectory = Path.Combine(tempRoot, "Mods");
            var result = await installer.InstallAsync(new ModPackageInstallRequest(
                packagePath,
                modsDirectory,
                "nexus-escape",
                "Escape",
                "1.0.0"));

            Assert.False(result.Success);
            Assert.False(File.Exists(Path.Combine(tempRoot, "escape.txt")));
            Assert.DoesNotContain(
                Directory.Exists(modsDirectory) ? Directory.GetDirectories(modsDirectory) : [],
                directory => directory.EndsWith(".installing", StringComparison.OrdinalIgnoreCase));
            Assert.Empty(Directory.GetDirectories(Path.Combine(modsDirectory, ".transactions")));
        }
        finally
        {
            DeleteTempDirectory(tempRoot);
        }
    }

    [Fact]
    public async Task InstallAsync_ExtractsSevenZipPackage()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var packagePath = Path.Combine(tempRoot, "package.7z");
            using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("Nested/Data/config.xml");
                await using var stream = entry.Open();
                await using var writer = new StreamWriter(stream);
                await writer.WriteAsync("<config />");
            }

            var installer = new ModPackageInstaller(Logger.None);
            var result = await installer.InstallAsync(new ModPackageInstallRequest(
                packagePath,
                Path.Combine(tempRoot, "Mods"),
                "nexus-7z",
                "Seven Zip Mod",
                "1.0.0"));

            Assert.True(result.Success, result.Message);
            Assert.NotNull(result.InstalledRootPath);
            Assert.True(File.Exists(Path.Combine(result.InstalledRootPath, "Data", "config.xml")));
        }
        finally
        {
            DeleteTempDirectory(tempRoot);
        }
    }

    [Fact]
    public async Task InstallAsync_ReturnsFailureForInvalidSevenZipPackage()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var packagePath = Path.Combine(tempRoot, "invalid.7z");
            await File.WriteAllTextAsync(packagePath, "This is not an archive.");

            var installer = new ModPackageInstaller(Logger.None);
            var modsDirectory = Path.Combine(tempRoot, "Mods");
            var result = await installer.InstallAsync(new ModPackageInstallRequest(
                packagePath,
                modsDirectory,
                "nexus-invalid",
                "Invalid package",
                "1.0.0"));

            Assert.False(result.Success);
            Assert.Contains("Invalid package archive", result.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(
                Directory.Exists(modsDirectory) ? Directory.GetDirectories(modsDirectory) : [],
                directory => directory.EndsWith(".installing", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DeleteTempDirectory(tempRoot);
        }
    }

    [Fact]
    public async Task InstallPreparedDirectoryAsync_ReplacesExistingModTransactionally()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var sourcePackage = Path.Combine(tempRoot, "creator-pack.zip");
            await File.WriteAllTextAsync(sourcePackage, "source package fingerprint");
            var modsDirectory = Path.Combine(tempRoot, "Mods");
            var existingRoot = Path.Combine(modsDirectory, "demo");
            Directory.CreateDirectory(Path.Combine(existingRoot, "Data"));
            await File.WriteAllTextAsync(Path.Combine(existingRoot, "Data", "old.pak"), "old");

            var preparedRoot = Path.Combine(tempRoot, "prepared");
            Directory.CreateDirectory(Path.Combine(preparedRoot, "Data"));
            await File.WriteAllTextAsync(Path.Combine(preparedRoot, "Data", "new.pak"), "new");

            var installer = new ModPackageInstaller(Logger.None);
            var result = await installer.InstallPreparedDirectoryAsync(new PreparedModPackageInstallRequest(
                preparedRoot,
                sourcePackage,
                modsDirectory,
                "demo",
                "Demo",
                "2.0",
                ExistingInstallPolicy: ModPackageExistingInstallPolicy.Replace,
                ExistingRootPath: existingRoot));

            Assert.True(result.Success, result.Message);
            Assert.Equal(existingRoot, result.InstalledRootPath);
            Assert.False(File.Exists(Path.Combine(existingRoot, "Data", "old.pak")));
            Assert.True(File.Exists(Path.Combine(existingRoot, "Data", "new.pak")));
            Assert.True(File.Exists(Path.Combine(existingRoot, "bohemix.mod.json")));
            Assert.True(File.Exists(Path.Combine(existingRoot, "bohemix.install.json")));
            Assert.Empty(Directory.GetDirectories(Path.Combine(modsDirectory, ".transactions")));
        }
        finally
        {
            DeleteTempDirectory(tempRoot);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "bohemix-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTempDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
