using System;
using System.Diagnostics;
using System.Threading;

namespace BohemiX.App.Services;

internal sealed class SingleInstanceCoordinator : IDisposable
{
    private const string MutexName = "BohemiX_SingleInstance_Mutex_v1";
    private Mutex? instanceMutex;
    private bool ownsMutex;

    public bool TryAcquire()
    {
        if (instanceMutex is not null)
        {
            return ownsMutex;
        }

        instanceMutex = new Mutex(true, MutexName, out ownsMutex);
        if (!ownsMutex)
        {
            TryActivateExistingInstance();
        }

        return ownsMutex;
    }

    private static void TryActivateExistingInstance()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            using var currentProcess = Process.GetCurrentProcess();
            foreach (var process in Process.GetProcessesByName(currentProcess.ProcessName))
            {
                using (process)
                {
                    if (process.Id == currentProcess.Id)
                    {
                        continue;
                    }

                    var handle = process.MainWindowHandle;
                    if (handle == IntPtr.Zero)
                    {
                        handle = WindowsWindowInterop.FindActivatableWindow(process.Id);
                    }

                    if (handle != IntPtr.Zero)
                    {
                        WindowsWindowInterop.RestoreAndActivate(handle);
                        return;
                    }
                }
            }
        }
        catch
        {
            // Activation is best-effort; the second process still exits.
        }
    }

    public void Dispose()
    {
        if (ownsMutex)
        {
            instanceMutex?.ReleaseMutex();
        }

        instanceMutex?.Dispose();
        instanceMutex = null;
        ownsMutex = false;
    }
}
