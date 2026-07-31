using System.Security.Cryptography;
using System.Text;
using BohemiX.Core.Models;
using BohemiX.Core.Services;
using BohemiX.Infrastructure.Persistence;
using BohemiX.Infrastructure.Services;
using Serilog;

namespace BohemiX.Core.Tests;

public sealed class BundledNativeDependencyInstallerTests
{
    [Fact]
    public async Task InstallIfPresent_VerifiesAndCopiesManifestedFiles()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var bundle = Path.Combine(root, "bundle");
            Directory.CreateDirectory(bundle);
            var source = Path.Combine(bundle, "usvfs_x64.dll");
            await File.WriteAllTextAsync(source, "verified-native-payload");
            await WriteManifestAsync(bundle, source);

            var paths = new ApplicationPathService(Path.Combine(root, "app-data")).GetPaths();
            var installer = new BundledNativeDependencyInstaller(CreateLogger(), bundle);
            await installer.InstallIfPresentAsync(paths);

            var installed = Path.Combine(paths.NativeDirectory, "usvfs_x64.dll");
            Assert.True(File.Exists(installed));
            Assert.Equal("verified-native-payload", await File.ReadAllTextAsync(installed));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InstallIfPresent_RejectsHashMismatchWithoutReplacingDestination()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var bundle = Path.Combine(root, "bundle");
            Directory.CreateDirectory(bundle);
            var source = Path.Combine(bundle, "usvfs.dll");
            await File.WriteAllTextAsync(source, "trusted");
            await WriteManifestAsync(bundle, source);
            await File.WriteAllTextAsync(source, "tampered");

            var paths = new ApplicationPathService(Path.Combine(root, "app-data")).GetPaths();
            Directory.CreateDirectory(paths.NativeDirectory);
            var destination = Path.Combine(paths.NativeDirectory, "usvfs.dll");
            await File.WriteAllTextAsync(destination, "existing");

            var installer = new BundledNativeDependencyInstaller(CreateLogger(), bundle);
            await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallIfPresentAsync(paths));

            Assert.Equal("existing", await File.ReadAllTextAsync(destination));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InstallIfPresent_DoesNotRewriteExistingVerifiedFile()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var bundle = Path.Combine(root, "bundle");
            Directory.CreateDirectory(bundle);
            var source = Path.Combine(bundle, "usvfs.dll");
            await File.WriteAllTextAsync(source, "verified");
            await WriteManifestAsync(bundle, source);

            var paths = new ApplicationPathService(Path.Combine(root, "app-data")).GetPaths();
            Directory.CreateDirectory(paths.NativeDirectory);
            var destination = Path.Combine(paths.NativeDirectory, "usvfs.dll");
            await File.WriteAllTextAsync(destination, "verified");
            var originalWriteTime = new DateTime(2020, 1, 2, 3, 4, 6, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(destination, originalWriteTime);

            var installer = new BundledNativeDependencyInstaller(CreateLogger(), bundle);
            await installer.InstallIfPresentAsync(paths);

            Assert.Equal(originalWriteTime, File.GetLastWriteTimeUtc(destination));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InstallIfPresent_VerifiesEverySourceBeforeCommittingAnyFile()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var bundle = Path.Combine(root, "bundle");
            Directory.CreateDirectory(bundle);
            var firstSource = Path.Combine(bundle, "usvfs.dll");
            var secondSource = Path.Combine(bundle, "helper.dll");
            await File.WriteAllTextAsync(firstSource, "new-first");
            await File.WriteAllTextAsync(secondSource, "new-second");
            await WriteManifestAsync(bundle, firstSource, secondSource);
            await File.WriteAllTextAsync(secondSource, "tampered-second");

            var paths = new ApplicationPathService(Path.Combine(root, "app-data")).GetPaths();
            Directory.CreateDirectory(paths.NativeDirectory);
            var firstDestination = Path.Combine(paths.NativeDirectory, "usvfs.dll");
            await File.WriteAllTextAsync(firstDestination, "previous-first");

            var installer = new BundledNativeDependencyInstaller(CreateLogger(), bundle);
            await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallIfPresentAsync(paths));

            Assert.Equal("previous-first", await File.ReadAllTextAsync(firstDestination));
            Assert.False(File.Exists(Path.Combine(paths.NativeDirectory, "helper.dll")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InstallIfPresent_RejectsNativeDirectoryWithoutManifest()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var bundle = Path.Combine(root, "bundle");
            Directory.CreateDirectory(bundle);
            await File.WriteAllTextAsync(Path.Combine(bundle, "usvfs.dll"), "unmanifested");

            var paths = new ApplicationPathService(Path.Combine(root, "app-data")).GetPaths();
            var installer = new BundledNativeDependencyInstaller(CreateLogger(), bundle);

            var exception = await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallIfPresentAsync(paths));
            Assert.Contains("SHA256SUMS", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InstallIfPresent_SkipsDevelopmentBuildWithoutNativeDirectory()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var bundle = Path.Combine(root, "missing-bundle");
            var paths = new ApplicationPathService(Path.Combine(root, "app-data")).GetPaths();
            var installer = new BundledNativeDependencyInstaller(CreateLogger(), bundle);

            await installer.InstallIfPresentAsync(paths);

            Assert.False(Directory.Exists(paths.NativeDirectory));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InstallIfPresent_RejectsDuplicateManifestPaths()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var bundle = Path.Combine(root, "bundle");
            Directory.CreateDirectory(bundle);
            var source = Path.Combine(bundle, "usvfs.dll");
            await File.WriteAllTextAsync(source, "verified");
            var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(source)));
            await File.WriteAllTextAsync(
                Path.Combine(bundle, "SHA256SUMS"),
                $"{hash}  usvfs.dll{Environment.NewLine}{hash}  .\\usvfs.dll{Environment.NewLine}",
                Encoding.ASCII);

            var paths = new ApplicationPathService(Path.Combine(root, "app-data")).GetPaths();
            var installer = new BundledNativeDependencyInstaller(CreateLogger(), bundle);

            await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallIfPresentAsync(paths));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AppStartup_ReportsNativeVerificationFailureAndStopsInitialization()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var bundle = Path.Combine(root, "bundle");
            Directory.CreateDirectory(bundle);
            await File.WriteAllTextAsync(Path.Combine(bundle, "usvfs.dll"), "unmanifested");

            var pathService = new ApplicationPathService(Path.Combine(root, "app-data"));
            var logger = CreateLogger();
            var connectionFactory = new SqliteConnectionFactory(pathService);
            var settingsService = new AppSettingsService(pathService, connectionFactory, logger);
            var reporter = new CapturingErrorReporter();
            var installer = new BundledNativeDependencyInstaller(logger, bundle);
            var startup = new AppStartupService(
                pathService,
                settingsService,
                connectionFactory,
                logger,
                reporter,
                installer);

            await Assert.ThrowsAsync<InvalidDataException>(() => startup.InitializeAsync());

            Assert.NotNull(reporter.Report);
            Assert.Equal(ApplicationErrorCategory.VirtualFileSystem, reporter.Report!.Category);
            Assert.Contains("Startup was stopped", reporter.Report.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(pathService.GetPaths().DatabasePath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task WriteManifestAsync(string bundle, params string[] files)
    {
        var lines = new List<string>();
        foreach (var file in files)
        {
            var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(file)));
            lines.Add($"{hash}  {Path.GetFileName(file)}");
        }

        await File.WriteAllTextAsync(
            Path.Combine(bundle, "SHA256SUMS"),
            string.Join(Environment.NewLine, lines) + Environment.NewLine,
            Encoding.ASCII);
    }

    private static string CreateTemporaryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "BohemiX.NativeInstaller.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static ILogger CreateLogger() => new LoggerConfiguration().CreateLogger();

    private sealed class CapturingErrorReporter : IApplicationErrorReporter
    {
        public ApplicationErrorReport? Report { get; private set; }

        public Task ReportAsync(ApplicationErrorReport report, CancellationToken cancellationToken = default)
        {
            Report = report;
            return Task.CompletedTask;
        }
    }
}
