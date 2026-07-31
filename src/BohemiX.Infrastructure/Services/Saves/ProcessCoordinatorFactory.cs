using BohemiX.Core.Services.Saves;
using Serilog;

namespace BohemiX.Infrastructure.Services.Saves;

public sealed class ProcessCoordinatorFactory(ILogger logger) : IProcessCoordinatorFactory
{
    /// <summary>
    /// Creates a process and file-write safety coordinator for a specific official save directory.
    /// </summary>
    public IProcessCoordinator Create(string officialSavePath) => new ProcessCoordinator(officialSavePath, logger);
}
