using BohemiX.Core.Services.Saves;
using Serilog;

namespace BohemiX.Infrastructure.Services.Saves;

public sealed class ProcessCoordinator(string officialSavePath, ILogger logger, TimeSpan? quietPeriod = null)
    : IProcessCoordinator, IDisposable
{
    private readonly string officialSavePath = NormalizeDirectoryPath(officialSavePath);
    private readonly ILogger logger = logger.ForContext<ProcessCoordinator>();
    private readonly TimeSpan quietPeriod = quietPeriod ?? TimeSpan.FromSeconds(3);

    /// <summary>
    /// Retained for compatibility. Junction targets are remounted during save
    /// switches, so a FileSystemWatcher on the link is deliberately avoided.
    /// </summary>
    public void Start() { }

    /// <summary>
    /// Returns false while KingdomCome2.exe is running or save files are still being written.
    /// </summary>
    public async Task<bool> IsSafeToSwitch(CancellationToken cancellationToken = default)
    {
        if (IsGameRunning())
        {
            this.logger.Warning("Save slot switch blocked because KingdomCome2.exe is running.");
            return false;
        }

        var latestSaveWriteUtc = GetLatestSaveWriteUtc(this.officialSavePath);
        if (latestSaveWriteUtc is not null && DateTimeOffset.UtcNow - latestSaveWriteUtc.Value < this.quietPeriod)
        {
            this.logger.Warning("Save slot switch blocked because a .whs save file was recently changed.");
            return false;
        }

        var containsLockedFiles = await Task.Run(() => ContainsLockedFiles(this.officialSavePath), cancellationToken).ConfigureAwait(false);
        if (containsLockedFiles)
        {
            this.logger.Warning("Save slot switch blocked because at least one save file is locked.");
        }

        return !containsLockedFiles;
    }

    private static DateTimeOffset? GetLatestSaveWriteUtc(string root)
    {
        if (!Directory.Exists(root))
        {
            return null;
        }

        try
        {
            DateTimeOffset? latest = null;
            foreach (var path in Directory.EnumerateFiles(root, "*.whs", SearchOption.AllDirectories))
            {
                var writeTime = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
                if (latest is null || writeTime > latest)
                {
                    latest = writeTime;
                }
            }

            return latest;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A remount can briefly make the junction unavailable. The lock
            // probe below remains the source of truth once it is reachable.
            return null;
        }
    }

    private static bool IsGameRunning()
    {
        foreach (var process in System.Diagnostics.Process.GetProcessesByName("KingdomCome2"))
        {
            using (process)
            {
                if (!process.HasExited)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool ContainsLockedFiles(string root)
    {
        if (!Directory.Exists(root))
        {
            return false;
        }

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            try
            {
                // [防御性编程] 独占读探测可发现游戏 Auto-save 正在写入的文件；
                // 发现锁定后必须硬性拦截切换，避免 CryEngine 存档半写入。
                using var _ = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
            }
            catch (IOException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeDirectoryPath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    public void Dispose() { }
}
