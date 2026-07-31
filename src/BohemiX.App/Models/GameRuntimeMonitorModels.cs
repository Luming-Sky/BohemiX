using System;
using BohemiX.Core.Models;

namespace BohemiX.App.Models;

public sealed record GameRuntimeMonitorRequest(
    int ProcessId,
    Guid? SessionId,
    string ExecutablePath);

public sealed record GameRuntimeExitUpdate(
    GameProcessExitResult ExitResult,
    byte[]? ExitThumbnail,
    VfsSessionState VfsState);
