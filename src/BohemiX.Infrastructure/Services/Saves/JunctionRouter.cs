using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using BohemiX.Core.Models.Saves;
using BohemiX.Core.Services.Saves;
using BohemiX.Infrastructure.Native;
using Microsoft.Win32.SafeHandles;
using Serilog;

namespace BohemiX.Infrastructure.Services.Saves;

public sealed class JunctionRouter(ILogger logger) : IJunctionRouter
{
    private readonly ILogger logger = logger.ForContext<JunctionRouter>();

    /// <summary>
    /// Creates or replaces a Windows Directory Junction from linkPath to targetPath.
    /// </summary>
    public void MountJunction(string linkPath, string targetPath)
    {
        var linkFull = NormalizeDirectoryPath(linkPath);
        var targetFull = NormalizeDirectoryPath(targetPath);

        if (!Directory.Exists(targetFull))
        {
            throw new DirectoryNotFoundException("找不到这个备份的文件。可能已经被移动或删除。");
        }

        if (SamePath(linkFull, targetFull))
        {
            throw new InvalidOperationException("这个备份已经是当前正在使用的存档。");
        }

        var previous = TryGetJunctionInfo(linkFull);
        var previousUnmounted = false;

        try
        {
            if (previous is not null)
            {
                UnmountJunction(linkFull);
                previousUnmounted = true;
            }
            else if (Directory.Exists(linkFull))
            {
                EnsureEmptyPhysicalDirectory(linkFull);
            }

            MountJunctionInternal(linkFull, targetFull);
            this.logger.Information("Mounted save junction {LinkPath} -> {TargetPath}", linkFull, targetFull);
        }
        catch (Exception mountError)
        {
            this.logger.Error(mountError, "Failed to mount save junction {LinkPath} -> {TargetPath}", linkFull, targetFull);

            if (previousUnmounted && previous is not null)
            {
                try
                {
                    MountJunctionInternal(linkFull, previous.TargetPath);
                    this.logger.Warning("Restored previous save junction {LinkPath} -> {TargetPath}", linkFull, previous.TargetPath);
                }
                catch (Exception rollbackError)
                {
                    throw new IOException(
                        "切换失败，而且没能恢复到上一个备份。请先不要启动游戏，检查备份文件夹是否还在。",
                        new AggregateException(mountError, rollbackError));
                }
            }

            throw;
        }
    }

    /// <summary>
    /// Removes linkPath only when it is verified as an NTFS mount-point reparse point.
    /// </summary>
    public void UnmountJunction(string linkPath)
    {
        var linkFull = NormalizeDirectoryPath(linkPath);
        var info = TryGetJunctionInfo(linkFull);

        if (info is null)
        {
            if (!Directory.Exists(linkFull))
            {
                return;
            }

            throw new InvalidOperationException("当前没有正在使用的备份，不能取消切换。");
        }

        using var handle = OpenDirectory(linkFull, NativeMethods.GenericWrite);
        var deleteBuffer = new byte[NativeMethods.ReparseDataBufferHeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(deleteBuffer.AsSpan(0, 4), NativeMethods.IoReparseTagMountPoint);

        var input = Marshal.AllocHGlobal(deleteBuffer.Length);
        try
        {
            Marshal.Copy(deleteBuffer, 0, input, deleteBuffer.Length);

            // [防御性编程] 只对已验证的 mount-point reparse point 发送删除控制码；
            // 这一步删除的是 Junction 元数据，不会删除 Vault 中的真实存档。
            if (!NativeMethods.DeviceIoControl(
                    handle,
                    NativeMethods.FsctlDeleteReparsePoint,
                    input,
                    deleteBuffer.Length,
                    IntPtr.Zero,
                    0,
                    out _,
                    IntPtr.Zero))
            {
                ThrowLastWin32("取消当前切换失败。");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(input);
        }

        // [防御性编程] 删除 Reparse Point 后，linkPath 只剩空壳目录；
        // 绝不对未验证的目录调用 Directory.Delete，避免误删官方真实存档入口。
        if (Directory.Exists(linkFull) && TryGetJunctionInfo(linkFull) is null)
        {
            Directory.Delete(linkFull, recursive: false);
        }

        this.logger.Information("Unmounted save junction {LinkPath} previously targeting {TargetPath}", linkFull, info.TargetPath);
    }

    /// <summary>
    /// Reads junction metadata and returns null for normal physical directories.
    /// </summary>
    public JunctionInfo? TryGetJunctionInfo(string linkPath)
    {
        var linkFull = NormalizeDirectoryPath(linkPath);

        if (!Directory.Exists(linkFull))
        {
            return null;
        }

        if (!File.GetAttributes(linkFull).HasFlag(FileAttributes.ReparsePoint))
        {
            return null;
        }

        using var handle = OpenDirectory(linkFull, NativeMethods.GenericRead);
        var output = Marshal.AllocHGlobal(NativeMethods.MaximumReparseDataBufferSize);

        try
        {
            if (!NativeMethods.DeviceIoControl(
                    handle,
                    NativeMethods.FsctlGetReparsePoint,
                    IntPtr.Zero,
                    0,
                    output,
                    NativeMethods.MaximumReparseDataBufferSize,
                    out var bytesReturned,
                    IntPtr.Zero))
            {
                ThrowLastWin32("检查当前使用的备份失败。");
            }

            var buffer = new byte[bytesReturned];
            Marshal.Copy(output, buffer, 0, bytesReturned);
            var span = buffer.AsSpan();

            var tag = BinaryPrimitives.ReadUInt32LittleEndian(span[..4]);

            // [防御性编程] Symbolic Link 也是 Reparse Point，但本系统红线只允许 Directory Junction。
            if (tag != NativeMethods.IoReparseTagMountPoint)
            {
                throw new InvalidOperationException("游戏存档位置被其他工具接管了。请先还原后再切换备份。");
            }

            var substituteNameOffset = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(8, 2));
            var substituteNameLength = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(10, 2));
            var printNameOffset = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(12, 2));
            var printNameLength = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(14, 2));
            var pathBuffer = span[NativeMethods.ReparseMountPointFixedHeaderSize..];

            var substituteName = Encoding.Unicode.GetString(pathBuffer.Slice(substituteNameOffset, substituteNameLength));
            var printName = Encoding.Unicode.GetString(pathBuffer.Slice(printNameOffset, printNameLength));
            var targetPath = NormalizeReturnedTarget(substituteName, printName);

            return new JunctionInfo(linkFull, targetPath, substituteName, printName);
        }
        finally
        {
            Marshal.FreeHGlobal(output);
        }
    }

    private static void MountJunctionInternal(string linkFull, string targetFull)
    {
        var createdShell = false;

        try
        {
            if (!Directory.Exists(linkFull))
            {
                Directory.CreateDirectory(linkFull);
                createdShell = true;
            }
            else
            {
                EnsureEmptyPhysicalDirectory(linkFull);
            }

            using var handle = OpenDirectory(linkFull, NativeMethods.GenericWrite);
            var buffer = BuildMountPointBuffer(targetFull);
            var input = Marshal.AllocHGlobal(buffer.Length);

            try
            {
                Marshal.Copy(buffer, 0, input, buffer.Length);

                // [防御性编程] 通过 FSCTL_SET_REPARSE_POINT 创建 Directory Junction；
                // 不使用 Symbolic Link，避免管理员权限要求与 CryEngine 路径兼容风险。
                if (!NativeMethods.DeviceIoControl(
                        handle,
                        NativeMethods.FsctlSetReparsePoint,
                        input,
                        buffer.Length,
                        IntPtr.Zero,
                        0,
                        out _,
                        IntPtr.Zero))
                {
                    ThrowLastWin32("切换到这个备份失败。");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(input);
            }
        }
        catch
        {
            // [防御性编程] 挂载失败时只清理本方法新建的空壳目录。
            // 绝不递归删除 linkFull，因为它可能是官方存档入口或用户真实目录。
            if (createdShell && Directory.Exists(linkFull) && !File.GetAttributes(linkFull).HasFlag(FileAttributes.ReparsePoint))
            {
                Directory.Delete(linkFull, recursive: false);
            }

            throw;
        }
    }

    private static byte[] BuildMountPointBuffer(string targetFull)
    {
        var substituteName = $@"\??\{targetFull}";
        var printName = targetFull;
        var substituteBytes = Encoding.Unicode.GetBytes(substituteName);
        var printBytes = Encoding.Unicode.GetBytes(printName);
        var pathBytes = Encoding.Unicode.GetBytes($"{substituteName}\0{printName}\0");

        var reparseDataLength = checked((ushort)(NativeMethods.MountPointReparseBufferHeaderSize + pathBytes.Length));
        var totalLength = NativeMethods.ReparseDataBufferHeaderSize + reparseDataLength;

        if (totalLength > NativeMethods.MaximumReparseDataBufferSize)
        {
            throw new PathTooLongException("备份位置太长了，Windows 无法完成切换。");
        }

        var buffer = new byte[totalLength];
        var span = buffer.AsSpan();

        BinaryPrimitives.WriteUInt32LittleEndian(span[..4], NativeMethods.IoReparseTagMountPoint);
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(4, 2), reparseDataLength);
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(8, 2), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(10, 2), checked((ushort)substituteBytes.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(12, 2), checked((ushort)(substituteBytes.Length + sizeof(char))));
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(14, 2), checked((ushort)printBytes.Length));
        pathBytes.CopyTo(span[NativeMethods.ReparseMountPointFixedHeaderSize..]);

        return buffer;
    }

    private static SafeFileHandle OpenDirectory(string path, uint access)
    {
        var handle = NativeMethods.CreateFile(
            path,
            access,
            NativeMethods.FileShareRead | NativeMethods.FileShareWrite | NativeMethods.FileShareDelete,
            IntPtr.Zero,
            NativeMethods.OpenExisting,
            NativeMethods.FileFlagBackupSemantics | NativeMethods.FileFlagOpenReparsePoint,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            ThrowLastWin32("无法访问游戏存档位置。请检查权限后再试。");
        }

        return handle;
    }

    private static void EnsureEmptyPhysicalDirectory(string path)
    {
        var attributes = File.GetAttributes(path);

        // [防御性编程] 不能把 Symbolic Link、Junction 或其他 Reparse Point 当普通目录处理。
        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidOperationException("游戏存档位置已被其他工具占用。请先还原后再切换备份。");
        }

        if (Directory.EnumerateFileSystemEntries(path).Any())
        {
            throw new InvalidOperationException("游戏原本的存档位置里还有文件。请先备份或手动整理后再切换。");
        }
    }

    private static string NormalizeReturnedTarget(string substituteName, string printName)
    {
        var candidate = string.IsNullOrWhiteSpace(printName) ? substituteName : printName;
        return NormalizeDirectoryPath(candidate.StartsWith(@"\??\", StringComparison.Ordinal) ? candidate[4..] : candidate);
    }

    private static string NormalizeDirectoryPath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool SamePath(string left, string right) =>
        StringComparer.OrdinalIgnoreCase.Equals(NormalizeDirectoryPath(left), NormalizeDirectoryPath(right));

    private static void ThrowLastWin32(string message) =>
        throw new IOException(message, new Win32Exception(Marshal.GetLastWin32Error()));
}
