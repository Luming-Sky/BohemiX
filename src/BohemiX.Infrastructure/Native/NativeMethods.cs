using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace BohemiX.Infrastructure.Native;

internal static class NativeMethods
{
    internal const uint GenericRead = 0x80000000;
    internal const uint GenericWrite = 0x40000000;
    internal const uint FileShareRead = 0x00000001;
    internal const uint FileShareWrite = 0x00000002;
    internal const uint FileShareDelete = 0x00000004;
    internal const uint OpenExisting = 3;

    internal const uint FileFlagOpenReparsePoint = 0x00200000;
    internal const uint FileFlagBackupSemantics = 0x02000000;

    internal const uint IoReparseTagMountPoint = 0xA0000003;
    internal const uint FsctlSetReparsePoint = 0x000900A4;
    internal const uint FsctlGetReparsePoint = 0x000900A8;
    internal const uint FsctlDeleteReparsePoint = 0x000900AC;

    internal const int ReparseDataBufferHeaderSize = 8;
    internal const int MountPointReparseBufferHeaderSize = 8;
    internal const int ReparseMountPointFixedHeaderSize = 16;
    internal const int MaximumReparseDataBufferSize = 16 * 1024;

    /// <summary>
    /// Windows REPARSE_DATA_BUFFER for mount points. PathBuffer is variable-length and manually packed
    /// for DeviceIoControl because the Windows structure uses a flexible array member.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct REPARSE_DATA_BUFFER
    {
        public uint ReparseTag;
        public ushort ReparseDataLength;
        public ushort Reserved;
        public ushort SubstituteNameOffset;
        public ushort SubstituteNameLength;
        public ushort PrintNameOffset;
        public ushort PrintNameLength;
    }

    /// <summary>
    /// Opens files and directories. Directory junction operations require FILE_FLAG_BACKUP_SEMANTICS.
    /// </summary>
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    /// <summary>
    /// Sends reparse-point control codes to NTFS.
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        int nInBufferSize,
        IntPtr lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(nint handle);
}
