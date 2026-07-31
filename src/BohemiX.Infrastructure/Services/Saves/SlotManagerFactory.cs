using BohemiX.Core.Services;
using BohemiX.Core.Services.Saves;
using Serilog;

namespace BohemiX.Infrastructure.Services.Saves;

public sealed class SlotManagerFactory(
    IApplicationPathService applicationPathService,
    IKcdSaveParser parser,
    ILogger logger)
    : ISlotManagerFactory
{
    /// <summary>
    /// Creates a slot manager bound to the official KCD2 save directory while keeping Vault storage under BohemiX data.
    /// </summary>
    public ISlotManager Create(string officialSavePath, string? vaultRoot = null, string? databasePath = null)
    {
        var paths = applicationPathService.GetPaths();
        var resolvedVaultRoot = string.IsNullOrWhiteSpace(vaultRoot)
            ? Path.Combine(paths.DataDirectory, "saves", "vault")
            : vaultRoot;
        var resolvedDatabasePath = string.IsNullOrWhiteSpace(databasePath)
            ? paths.DatabasePath
            : databasePath;

        // [防御性编程] ProcessCoordinator 与 SlotManager 共用同一个官方入口路径，
        // 确保导入/切换前的进程和写入拦截针对的就是 CryEngine 读取的目录。
        var coordinator = new ProcessCoordinator(officialSavePath, logger);

        return new SlotManager(
            officialSavePath,
            resolvedVaultRoot,
            resolvedDatabasePath,
            parser,
            coordinator,
            logger,
            paths.GameEnvironmentId);
    }
}
