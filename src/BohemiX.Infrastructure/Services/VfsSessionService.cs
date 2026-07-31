using System.ComponentModel;
using BohemiX.Core.Models;
using BohemiX.Core.Services;
using BohemiX.Infrastructure.Native;
using Serilog;

namespace BohemiX.Infrastructure.Services;

public sealed class VfsSessionService : IVfsSessionService, IDisposable
{
    private static readonly string[] UsvfsLibraryNames = ["usvfs.dll", "usvfs_x64.dll"];

    private readonly ILogger logger;
    private readonly IApplicationPathService applicationPathService;
    private readonly IModMountPlanBuilder modMountPlanBuilder;
    private readonly IUsvfsNativeSessionFactory nativeSessionFactory;
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private IUsvfsNativeSession? nativeSession;
    private bool disposed;

    public VfsSessionService(
        ILogger logger,
        IApplicationPathService applicationPathService,
        IModMountPlanBuilder modMountPlanBuilder)
        : this(logger, applicationPathService, modMountPlanBuilder, new UsvfsNativeSessionFactory())
    {
    }

    internal VfsSessionService(
        ILogger logger,
        IApplicationPathService applicationPathService,
        IModMountPlanBuilder modMountPlanBuilder,
        IUsvfsNativeSessionFactory nativeSessionFactory)
    {
        this.logger = logger.ForContext<VfsSessionService>();
        this.applicationPathService = applicationPathService;
        this.modMountPlanBuilder = modMountPlanBuilder;
        this.nativeSessionFactory = nativeSessionFactory;
    }

    public VfsSessionState CurrentState { get; private set; } = VfsSessionState.Idle;

    public VfsSessionInfo? SessionInfo { get; private set; }

    public Exception? LastError { get; private set; }

    public async Task MountAsync(VfsMountRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LastError = null;
            await UnmountCoreAsync().ConfigureAwait(false);
            CurrentState = VfsSessionState.Mounting;

            var executablePath = Path.GetFullPath(request.GameExecutablePath);
            if (!File.Exists(executablePath))
            {
                throw new FileNotFoundException("The game executable required for VFS mounting was not found.", executablePath);
            }

            var mountPlan = request.MountPlan ?? modMountPlanBuilder.BuildPlan(request.EnabledMods);
            var enabledMods = request.EnabledMods
                .Where(mod => mod.IsEnabled)
                .OrderBy(mod => mod.LoadOrder)
                .ThenBy(mod => mod.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            ValidatePlan(mountPlan, enabledMods);

            var paths = applicationPathService.GetPaths();
            var nativeLibraryPath = UsvfsLibraryNames
                .Select(name => Path.Combine(paths.NativeDirectory, name))
                .FirstOrDefault(File.Exists);
            if (nativeLibraryPath is null)
            {
                throw new FileNotFoundException(
                    $"The native usvfs library was not found. Expected one of: {string.Join(", ", UsvfsLibraryNames)}",
                    Path.Combine(paths.NativeDirectory, UsvfsLibraryNames[0]));
            }

            var sessionId = Guid.NewGuid();
            var instanceName = $"bohemix-{sessionId:N}";
            var crashDumpDirectory = Path.Combine(paths.LogsDirectory, "vfs-crash-dumps");
            var gameRoot = ResolveGameRoot(executablePath);
            var createdSession = nativeSessionFactory.Create(nativeLibraryPath, instanceName, crashDumpDirectory);
            try
            {
                createdSession.ClearMappings();
                foreach (var mod in enabledMods)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var sourceRoot = Path.GetFullPath(mod.RootPath);
                    if (!Directory.Exists(sourceRoot))
                    {
                        throw new DirectoryNotFoundException($"Enabled mod '{mod.Id}' is missing its root directory: {sourceRoot}");
                    }

                    LinkModPayload(createdSession, mod, sourceRoot, gameRoot);
                }

                nativeSession = createdSession;
                SessionInfo = new VfsSessionInfo(
                    sessionId,
                    instanceName,
                    gameRoot,
                    enabledMods.Length,
                    createdSession.Version,
                    DateTimeOffset.UtcNow);
                CurrentState = VfsSessionState.Mounted;
                LastError = null;
                logger.Information(
                    "Mounted native VFS session {VfsSessionId} version {NativeVersion} for {GameRootPath} with {MappedModCount} mods and {VirtualPathCount} winning paths",
                    sessionId,
                    createdSession.Version,
                    gameRoot,
                    enabledMods.Length,
                    mountPlan.UniqueVirtualPathCount);
            }
            catch
            {
                createdSession.Dispose();
                throw;
            }
        }
        catch (OperationCanceledException)
        {
            nativeSession = null;
            SessionInfo = null;
            CurrentState = VfsSessionState.Idle;
            LastError = null;
            throw;
        }
        catch (Exception ex)
        {
            nativeSession = null;
            SessionInfo = null;
            CurrentState = VfsSessionState.Faulted;
            LastError = ex;
            logger.Error(ex, "Unable to create a native VFS session for {GameExecutablePath}", request.GameExecutablePath);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async Task<VfsProcessLaunchResult> LaunchProcessAsync(
        VfsProcessLaunchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (CurrentState != VfsSessionState.Mounted || nativeSession is null || SessionInfo is null)
            {
                return new VfsProcessLaunchResult(false, null, "No mounted VFS session is available.");
            }

            var executablePath = Path.GetFullPath(request.ExecutablePath);
            if (!File.Exists(executablePath))
            {
                return new VfsProcessLaunchResult(false, null, "The VFS launch executable was not found.");
            }

            var workingDirectory = string.IsNullOrWhiteSpace(request.WorkingDirectory)
                ? Path.GetDirectoryName(executablePath)!
                : Path.GetFullPath(request.WorkingDirectory);
            var processId = nativeSession.StartHookedProcess(executablePath, request.Arguments, workingDirectory);
            logger.Information(
                "Started hooked process {ProcessId} in VFS session {VfsSessionId}",
                processId,
                SessionInfo.SessionId);
            return new VfsProcessLaunchResult(true, processId, "Game started through the native VFS session.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception)
        {
            LastError = ex;
            logger.Error(ex, "Failed to launch process through the mounted VFS session");
            return new VfsProcessLaunchResult(false, null, ex.Message);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async Task UnmountAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await UnmountCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        nativeSession?.Dispose();
        nativeSession = null;
        SessionInfo = null;
        LastError = null;
        CurrentState = VfsSessionState.Idle;
        lifecycleGate.Dispose();
    }

    private Task UnmountCoreAsync()
    {
        if (nativeSession is null)
        {
            SessionInfo = null;
            LastError = null;
            CurrentState = VfsSessionState.Idle;
            return Task.CompletedTask;
        }

        CurrentState = VfsSessionState.Unmounting;
        var sessionId = SessionInfo?.SessionId;
        try
        {
            nativeSession.Dispose();
            logger.Information("Unmounted native VFS session {VfsSessionId}", sessionId);
        }
        finally
        {
            nativeSession = null;
            SessionInfo = null;
            CurrentState = VfsSessionState.Idle;
        }

        return Task.CompletedTask;
    }

    private static void ValidatePlan(ModMountPlan mountPlan, IReadOnlyList<ModManifest> enabledMods)
    {
        if (mountPlan.EnabledModCount != enabledMods.Count)
        {
            throw new InvalidDataException(
                $"The VFS mount plan expects {mountPlan.EnabledModCount} enabled mods but {enabledMods.Count} were supplied.");
        }

        var modIds = enabledMods.Select(mod => mod.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingWinner = mountPlan.Entries.FirstOrDefault(entry => !modIds.Contains(entry.WinningModId));
        if (missingWinner is not null)
        {
            throw new InvalidDataException(
                $"The mount plan selects missing mod '{missingWinner.WinningModId}' for '{missingWinner.NormalizedVirtualPath}'.");
        }
    }

    private static void LinkModPayload(
        IUsvfsNativeSession session,
        ModManifest mod,
        string sourceRoot,
        string gameRoot)
    {
        var mappedItems = 0;
        foreach (var directory in Directory.EnumerateDirectories(sourceRoot).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            var destination = Path.Combine(gameRoot, Path.GetFileName(directory));
            if (!session.LinkDirectory(directory, destination, 0))
            {
                ThrowMappingFailure(mod.Id, directory);
            }

            mappedItems++;
        }

        foreach (var file in Directory.EnumerateFiles(sourceRoot).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            var fileName = Path.GetFileName(file);
            if (fileName.Equals("bohemix.mod.json", StringComparison.OrdinalIgnoreCase)
                || fileName.Equals("bohemix.install.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var destination = Path.Combine(gameRoot, fileName);
            if (!session.LinkFile(file, destination, 0))
            {
                ThrowMappingFailure(mod.Id, file);
            }

            mappedItems++;
        }

        if (mappedItems == 0)
        {
            throw new InvalidDataException($"Enabled mod '{mod.Id}' contains no payload that can be mapped.");
        }
    }

    private static void ThrowMappingFailure(string modId, string sourcePath) =>
        throw new Win32Exception(
            System.Runtime.InteropServices.Marshal.GetLastWin32Error(),
            $"usvfs could not map '{sourcePath}' for mod '{modId}'.");

    private static string ResolveGameRoot(string executablePath)
    {
        var executableDirectory = new DirectoryInfo(Path.GetDirectoryName(executablePath)!);
        for (var current = executableDirectory; current is not null; current = current.Parent)
        {
            var binDirectory = Path.Combine(current.FullName, "Bin");
            if (Directory.Exists(binDirectory)
                && executablePath.StartsWith(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(binDirectory)) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                return current.FullName;
            }
        }

        return executableDirectory.FullName;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }
}
