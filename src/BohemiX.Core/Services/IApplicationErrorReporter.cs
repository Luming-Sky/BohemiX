using BohemiX.Core.Models;

namespace BohemiX.Core.Services;

public interface IApplicationErrorReporter
{
    Task ReportAsync(ApplicationErrorReport report, CancellationToken cancellationToken = default);
}
