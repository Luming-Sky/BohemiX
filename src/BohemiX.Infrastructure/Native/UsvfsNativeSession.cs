using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace BohemiX.Infrastructure.Native;

internal sealed class UsvfsNativeSession : IUsvfsNativeSession
{
    private const uint LinkFlagMonitorChanges = 0x00000002;
    private const uint LinkFlagRecursive = 0x00000008;
    private const int StartfUseShowWindow = 0x00000001;
    private const short SwShowNormal = 1;

    private readonly nint libraryHandle;
    private readonly nint parameters;
    private readonly UsvfsClearVirtualMappings clearVirtualMappings;
    private readonly UsvfsVirtualLinkFile virtualLinkFile;
    private readonly UsvfsVirtualLinkDirectoryStatic virtualLinkDirectoryStatic;
    private readonly UsvfsDisconnectVfs disconnectVfs;
    private readonly UsvfsFreeParameters freeParameters;
    private readonly UsvfsCreateProcessHooked createProcessHooked;
    private bool disposed;

    public UsvfsNativeSession(string libraryPath, string instanceName, string crashDumpDirectory)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("usvfs is only supported on Windows.");
        }

        libraryHandle = NativeLibrary.Load(libraryPath);
        try
        {
            var createParameters = Load<UsvfsCreateParameters>("usvfsCreateParameters");
            freeParameters = Load<UsvfsFreeParameters>("usvfsFreeParameters");
            var setInstanceName = Load<UsvfsSetInstanceName>("usvfsSetInstanceName");
            var setDebugMode = Load<UsvfsSetDebugMode>("usvfsSetDebugMode");
            var setLogLevel = Load<UsvfsSetLogLevel>("usvfsSetLogLevel");
            var setCrashDumpType = Load<UsvfsSetCrashDumpType>("usvfsSetCrashDumpType");
            var setCrashDumpPath = Load<UsvfsSetCrashDumpPath>("usvfsSetCrashDumpPath");
            var createVfs = Load<UsvfsCreateVfs>("usvfsCreateVFS");
            clearVirtualMappings = Load<UsvfsClearVirtualMappings>("usvfsClearVirtualMappings");
            virtualLinkFile = Load<UsvfsVirtualLinkFile>("usvfsVirtualLinkFile");
            virtualLinkDirectoryStatic = Load<UsvfsVirtualLinkDirectoryStatic>("usvfsVirtualLinkDirectoryStatic");
            disconnectVfs = Load<UsvfsDisconnectVfs>("usvfsDisconnectVFS");
            createProcessHooked = Load<UsvfsCreateProcessHooked>("usvfsCreateProcessHooked");
            var versionString = Load<UsvfsVersionString>("usvfsVersionString");

            parameters = createParameters();
            if (parameters == 0)
            {
                throw new InvalidOperationException("usvfsCreateParameters returned a null pointer.");
            }

            Directory.CreateDirectory(crashDumpDirectory);
            setInstanceName(parameters, instanceName);
            setDebugMode(parameters, false);
            setLogLevel(parameters, UsvfsLogLevel.Warning);
            setCrashDumpType(parameters, UsvfsCrashDumpType.Mini);
            setCrashDumpPath(parameters, crashDumpDirectory);

            if (!createVfs(parameters))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "usvfsCreateVFS failed.");
            }

            var versionPointer = versionString();
            Version = versionPointer == 0
                ? "unknown"
                : Marshal.PtrToStringAnsi(versionPointer) ?? "unknown";
        }
        catch
        {
            if (parameters != 0 && freeParameters is not null)
            {
                freeParameters(parameters);
            }

            NativeLibrary.Free(libraryHandle);
            throw;
        }
    }

    public string Version { get; }

    public void ClearMappings()
    {
        ThrowIfDisposed();
        clearVirtualMappings();
    }

    public bool LinkDirectory(string sourcePath, string destinationPath, uint flags)
    {
        ThrowIfDisposed();
        return virtualLinkDirectoryStatic(
            sourcePath,
            destinationPath,
            flags | LinkFlagMonitorChanges | LinkFlagRecursive);
    }

    public bool LinkFile(string sourcePath, string destinationPath, uint flags)
    {
        ThrowIfDisposed();
        return virtualLinkFile(sourcePath, destinationPath, flags);
    }

    public int StartHookedProcess(string executablePath, string? arguments, string workingDirectory)
    {
        ThrowIfDisposed();

        var commandLine = new StringBuilder(BuildCommandLine(executablePath, arguments));
        var startupInfo = new StartupInfo
        {
            Cb = Marshal.SizeOf<StartupInfo>(),
            DwFlags = StartfUseShowWindow,
            WShowWindow = SwShowNormal
        };

        if (!createProcessHooked(
                executablePath,
                commandLine,
                0,
                0,
                false,
                0,
                0,
                workingDirectory,
                ref startupInfo,
                out var processInformation))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "usvfsCreateProcessHooked failed.");
        }

        try
        {
            return checked((int)processInformation.ProcessId);
        }
        finally
        {
            if (processInformation.Thread != 0)
            {
                NativeMethods.CloseHandle(processInformation.Thread);
            }

            if (processInformation.Process != 0)
            {
                NativeMethods.CloseHandle(processInformation.Process);
            }
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        try
        {
            clearVirtualMappings();
        }
        finally
        {
            try
            {
                disconnectVfs();
            }
            finally
            {
                freeParameters(parameters);
                NativeLibrary.Free(libraryHandle);
            }
        }
    }

    private T Load<T>(string exportName) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(libraryHandle, exportName));

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private static string BuildCommandLine(string executablePath, string? arguments) =>
        string.IsNullOrWhiteSpace(arguments)
            ? $"\"{executablePath}\""
            : $"\"{executablePath}\" {arguments}";

    private enum UsvfsLogLevel : byte
    {
        Debug,
        Info,
        Warning,
        Error
    }

    private enum UsvfsCrashDumpType : byte
    {
        None,
        Mini,
        Data,
        Full
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Cb;
        public nint Reserved;
        public nint Desktop;
        public nint Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int DwFlags;
        public short WShowWindow;
        public short Reserved2;
        public nint Reserved2Pointer;
        public nint StdInput;
        public nint StdOutput;
        public nint StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint UsvfsCreateParameters();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void UsvfsFreeParameters(nint parameters);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private delegate void UsvfsSetInstanceName(nint parameters, string name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void UsvfsSetDebugMode(nint parameters, [MarshalAs(UnmanagedType.Bool)] bool debugMode);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void UsvfsSetLogLevel(nint parameters, UsvfsLogLevel level);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void UsvfsSetCrashDumpType(nint parameters, UsvfsCrashDumpType type);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private delegate void UsvfsSetCrashDumpPath(nint parameters, string path);

    [UnmanagedFunctionPointer(CallingConvention.Winapi, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool UsvfsCreateVfs(nint parameters);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void UsvfsClearVirtualMappings();

    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool UsvfsVirtualLinkFile(string source, string destination, uint flags);

    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool UsvfsVirtualLinkDirectoryStatic(string source, string destination, uint flags);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void UsvfsDisconnectVfs();

    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool UsvfsCreateProcessHooked(
        string applicationName,
        StringBuilder commandLine,
        nint processAttributes,
        nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        nint environment,
        string currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint UsvfsVersionString();
}
