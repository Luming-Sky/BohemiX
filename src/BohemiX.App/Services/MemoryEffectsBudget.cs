using System;

namespace BohemiX.App.Services;

internal sealed class MemoryEffectsBudget
{
    internal const long SuppressThresholdBytes = 220L * 1024 * 1024;
    internal const long RestoreThresholdBytes = 190L * 1024 * 1024;
    internal static readonly TimeSpan RestoreDelay = TimeSpan.FromSeconds(60);

    private DateTimeOffset? belowRestoreThresholdSince;

    internal bool IsSuppressed { get; private set; }

    internal bool Update(long workingSetBytes, DateTimeOffset now)
    {
        if (workingSetBytes >= SuppressThresholdBytes)
        {
            IsSuppressed = true;
            belowRestoreThresholdSince = null;
            return true;
        }

        if (!IsSuppressed)
        {
            return false;
        }

        if (workingSetBytes > RestoreThresholdBytes)
        {
            belowRestoreThresholdSince = null;
            return true;
        }

        belowRestoreThresholdSince ??= now;
        if (now - belowRestoreThresholdSince < RestoreDelay)
        {
            return true;
        }

        IsSuppressed = false;
        belowRestoreThresholdSince = null;
        return false;
    }

    internal void RecordHighLoadExit()
    {
        IsSuppressed = true;
        belowRestoreThresholdSince = null;
    }
}
