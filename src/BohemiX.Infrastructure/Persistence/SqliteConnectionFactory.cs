using BohemiX.Core.Services;
using Microsoft.Data.Sqlite;

namespace BohemiX.Infrastructure.Persistence;

public sealed class SqliteConnectionFactory
{
    private readonly IApplicationPathService applicationPathService;

    public SqliteConnectionFactory(IApplicationPathService applicationPathService)
    {
        this.applicationPathService = applicationPathService;
    }

    public SqliteConnection CreateConnection()
    {
        var paths = applicationPathService.GetPaths();
        Directory.CreateDirectory(Path.GetDirectoryName(paths.DatabasePath)!);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabasePath,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        };

        return new SqliteConnection(builder.ToString());
    }
}
