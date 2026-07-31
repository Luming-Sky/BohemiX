using System;
using System.Threading;
using System.Threading.Tasks;

namespace BohemiX.App.Services;

/// <summary>
/// Retains the last visible frame of a launched game process so it can be used
/// after the process has exited.
/// </summary>
public interface IGameExitThumbnailCaptureService : IDisposable
{
    void BeginCapture(int processId);

    Task<byte[]?> CompleteCaptureAsync(int processId, CancellationToken cancellationToken = default);

    void CancelAll();
}
