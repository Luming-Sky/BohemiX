using BohemiX.Core.Models;
using BohemiX.Infrastructure.Native;
using BohemiX.Infrastructure.Services;
using Serilog.Core;

namespace BohemiX.Core.Tests;

public sealed class VfsSessionServiceTests
{
    [Fact]
    public async Task MountAndLaunch_RegistersOrderedMappingsAndUsesHookedProcess()
    {
        var root = CreateTempDirectory();
        try
        {
            var gameRoot = Path.Combine(root, "game");
            var executable = Path.Combine(gameRoot, "Bin", "Win64", "KingdomCome2.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
            await File.WriteAllBytesAsync(executable, [0]);

            var pathService = new ApplicationPathService(Path.Combine(root, "runtime"));
            Directory.CreateDirectory(pathService.GetPaths().NativeDirectory);
            await File.WriteAllBytesAsync(Path.Combine(pathService.GetPaths().NativeDirectory, "usvfs.dll"), [0]);
            var low = await CreateModAsync(root, "low", 0, "Data/low.pak");
            var high = await CreateModAsync(root, "high", 10, "Data/high.pak");
            var native = new FakeNativeSession();
            var service = new VfsSessionService(
                Logger.None,
                pathService,
                new ModMountPlanBuilder(new ModConflictAnalyzer()),
                new FakeNativeSessionFactory(native));

            await service.MountAsync(new VfsMountRequest(executable, [high, low]));
            var launch = await service.LaunchProcessAsync(new VfsProcessLaunchRequest(executable, "-devmode"));

            Assert.Equal(VfsSessionState.Mounted, service.CurrentState);
            Assert.NotNull(service.SessionInfo);
            Assert.Equal(gameRoot, service.SessionInfo!.GameRootPath);
            Assert.Equal(
                [Path.Combine(low.RootPath, "Data"), Path.Combine(high.RootPath, "Data")],
                native.LinkedSources);
            Assert.All(native.LinkedDestinations, destination => Assert.Equal(Path.Combine(gameRoot, "Data"), destination));
            Assert.True(launch.IsStarted);
            Assert.Equal(4321, launch.ProcessId);
            Assert.Equal(executable, native.LaunchedExecutable);

            await service.UnmountAsync();
            Assert.Equal(VfsSessionState.Idle, service.CurrentState);
            Assert.True(native.Disposed);
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    [Fact]
    public async Task Mount_FailedNativeMappingNeverReportsMounted()
    {
        var root = CreateTempDirectory();
        try
        {
            var gameRoot = Path.Combine(root, "game");
            var executable = Path.Combine(gameRoot, "Bin", "Win64", "KingdomCome2.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
            await File.WriteAllBytesAsync(executable, [0]);
            var pathService = new ApplicationPathService(Path.Combine(root, "runtime"));
            Directory.CreateDirectory(pathService.GetPaths().NativeDirectory);
            await File.WriteAllBytesAsync(Path.Combine(pathService.GetPaths().NativeDirectory, "usvfs.dll"), [0]);
            var mod = await CreateModAsync(root, "broken", 0, "Data/broken.pak");
            var native = new FakeNativeSession { LinkResult = false };
            var service = new VfsSessionService(
                Logger.None,
                pathService,
                new ModMountPlanBuilder(new ModConflictAnalyzer()),
                new FakeNativeSessionFactory(native));

            await service.MountAsync(new VfsMountRequest(executable, [mod]));

            Assert.Equal(VfsSessionState.Faulted, service.CurrentState);
            Assert.Null(service.SessionInfo);
            Assert.NotNull(service.LastError);
            Assert.Contains("map", service.LastError!.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(native.Disposed);
        }
        finally
        {
            DeleteTempDirectory(root);
        }
    }

    private static async Task<ModManifest> CreateModAsync(string root, string id, int loadOrder, string relativePath)
    {
        var modRoot = Path.Combine(root, id);
        var file = Path.Combine(modRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, id);
        return new ModManifest(
            id,
            id,
            "1.0.0",
            modRoot,
            loadOrder,
            true,
            [new ModFileEntry(relativePath, new FileInfo(file).Length, null)]);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "bohemix-vfs-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTempDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed class FakeNativeSessionFactory(FakeNativeSession session) : IUsvfsNativeSessionFactory
    {
        public IUsvfsNativeSession Create(string libraryPath, string instanceName, string crashDumpDirectory) => session;
    }

    private sealed class FakeNativeSession : IUsvfsNativeSession
    {
        public List<string> LinkedSources { get; } = [];
        public List<string> LinkedDestinations { get; } = [];
        public bool LinkResult { get; init; } = true;
        public bool Disposed { get; private set; }
        public string? LaunchedExecutable { get; private set; }
        public string Version => "test-usvfs";

        public void ClearMappings()
        {
        }

        public bool LinkDirectory(string sourcePath, string destinationPath, uint flags)
        {
            LinkedSources.Add(sourcePath);
            LinkedDestinations.Add(destinationPath);
            return LinkResult;
        }

        public bool LinkFile(string sourcePath, string destinationPath, uint flags)
        {
            LinkedSources.Add(sourcePath);
            LinkedDestinations.Add(destinationPath);
            return LinkResult;
        }

        public int StartHookedProcess(string executablePath, string? arguments, string workingDirectory)
        {
            LaunchedExecutable = executablePath;
            return 4321;
        }

        public void Dispose() => Disposed = true;
    }
}
