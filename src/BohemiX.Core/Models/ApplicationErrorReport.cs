using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace BohemiX.Core.Models;

public enum ApplicationErrorCategory
{
    Application = 0,
    GameLaunch = 1,
    GameProcess = 2,
    VirtualFileSystem = 3
}

/// <summary>
/// Structured error information that can be logged or presented without depending on a UI framework.
/// </summary>
public sealed record ApplicationErrorReport(
    ApplicationErrorCategory Category,
    string Title,
    string Message,
    string Location,
    string Source,
    string TechnicalDetails,
    DateTimeOffset OccurredAtUtc,
    string? LogPath = null,
    Guid? SessionId = null,
    int? ProcessId = null,
    int? ExitCode = null)
{
    public bool IsAbnormalGameExit =>
        Category == ApplicationErrorCategory.GameProcess
        && ExitCode is not null
        && ExitCode != 0;

    public static ApplicationErrorReport FromException(
        Exception exception,
        string title,
        string message,
        string source,
        ApplicationErrorCategory category = ApplicationErrorCategory.Application,
        string? locationHint = null,
        string? logPath = null,
        Guid? sessionId = null,
        int? processId = null)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return new ApplicationErrorReport(
            category,
            title,
            message,
            ResolveExceptionLocation(exception, locationHint),
            source,
            exception.ToString(),
            DateTimeOffset.UtcNow,
            logPath,
            sessionId,
            processId);
    }

    public static ApplicationErrorReport GameLaunchFailure(
        string message,
        string executablePath,
        string stage,
        string? technicalDetails = null,
        string? logPath = null,
        Guid? sessionId = null)
    {
        return new ApplicationErrorReport(
            ApplicationErrorCategory.GameLaunch,
            "游戏启动失败",
            message,
            $"启动阶段：{stage}\n可执行文件：{executablePath}",
            "Kingdom Come: Deliverance II 启动器",
            string.IsNullOrWhiteSpace(technicalDetails) ? message : technicalDetails,
            DateTimeOffset.UtcNow,
            logPath,
            sessionId);
    }

    public static ApplicationErrorReport GameProcessExit(
        int processId,
        int exitCode,
        string executablePath,
        string? logPath = null,
        Guid? sessionId = null)
    {
        var unsignedExitCode = unchecked((uint)exitCode);
        var details = new StringBuilder()
            .AppendLine($"Process ID: {processId}")
            .AppendLine($"Exit code: {exitCode} (0x{unsignedExitCode:X8})")
            .AppendLine($"Executable: {executablePath}");
        if (sessionId is not null)
        {
            details.AppendLine($"Launch session: {sessionId:D}");
        }

        return new ApplicationErrorReport(
            ApplicationErrorCategory.GameProcess,
            "游戏异常退出",
            $"游戏进程以非零退出码结束：{exitCode} (0x{unsignedExitCode:X8})。",
            $"游戏进程 PID {processId}\n可执行文件：{executablePath}",
            "Kingdom Come: Deliverance II 进程",
            details.ToString().TrimEnd(),
            DateTimeOffset.UtcNow,
            logPath,
            sessionId,
            processId,
            exitCode);
    }

    public string ToClipboardText()
    {
        var builder = new StringBuilder()
            .AppendLine(Title)
            .AppendLine($"时间：{OccurredAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}")
            .AppendLine($"类型：{Category}")
            .AppendLine($"来源：{Source}")
            .AppendLine($"位置：{Location}")
            .AppendLine($"说明：{Message}");

        if (SessionId is not null)
        {
            builder.AppendLine($"启动会话：{SessionId:D}");
        }

        if (ProcessId is not null)
        {
            builder.AppendLine($"进程 ID：{ProcessId}");
        }

        if (ExitCode is not null)
        {
            builder.AppendLine($"退出码：{ExitCode} (0x{unchecked((uint)ExitCode.Value):X8})");
        }

        if (!string.IsNullOrWhiteSpace(LogPath))
        {
            builder.AppendLine($"日志目录：{LogPath}");
        }

        return builder
            .AppendLine()
            .AppendLine("技术详情：")
            .AppendLine(TechnicalDetails)
            .ToString()
            .TrimEnd();
    }

    public static string ResolveExceptionLocation(Exception exception, string? locationHint = null)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var current = exception;
        while (current.InnerException is not null)
        {
            current = current.InnerException;
        }

        var frames = new StackTrace(current, true).GetFrames() ?? [];
        var preferredFrame = frames.FirstOrDefault(frame =>
        {
            var method = frame.GetMethod();
            var assemblyName = method?.DeclaringType?.Assembly.GetName().Name;
            return !string.IsNullOrWhiteSpace(frame.GetFileName())
                || (assemblyName is not null
                    && !assemblyName.StartsWith("System", StringComparison.Ordinal)
                    && !assemblyName.StartsWith("Microsoft", StringComparison.Ordinal)
                    && !assemblyName.StartsWith("Avalonia", StringComparison.Ordinal));
        });

        if (preferredFrame is not null)
        {
            var fileName = preferredFrame.GetFileName();
            var lineNumber = preferredFrame.GetFileLineNumber();
            var methodName = FormatMethod(preferredFrame.GetMethod());
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                return lineNumber > 0
                    ? $"{fileName}:{lineNumber} ({methodName})"
                    : $"{fileName} ({methodName})";
            }

            if (!string.IsNullOrWhiteSpace(methodName))
            {
                return string.IsNullOrWhiteSpace(locationHint)
                    ? methodName
                    : $"{locationHint}\n{methodName}";
            }
        }

        var targetMethod = FormatMethod(current.TargetSite);
        if (!string.IsNullOrWhiteSpace(targetMethod))
        {
            return string.IsNullOrWhiteSpace(locationHint)
                ? targetMethod
                : $"{locationHint}\n{targetMethod}";
        }

        return string.IsNullOrWhiteSpace(locationHint)
            ? "未能获得源码行号；请查看技术详情和日志。"
            : locationHint;
    }

    private static string FormatMethod(MethodBase? method)
    {
        if (method is null)
        {
            return string.Empty;
        }

        var typeName = method.DeclaringType?.FullName;
        return string.IsNullOrWhiteSpace(typeName)
            ? method.Name
            : $"{typeName}.{method.Name}";
    }
}
