using BohemiX.Core.Services;
using Microsoft.Data.Sqlite;

namespace BohemiX.Infrastructure.Persistence;

public sealed class ProfileConnectionFactory
{
    private readonly IApplicationPathService applicationPathService;

    public ProfileConnectionFactory(IApplicationPathService applicationPathService)
    {
        this.applicationPathService = applicationPathService;
    }

    public SqliteConnection CreateConnection()
    {
        var paths = applicationPathService.GetGlobalPaths();
        Directory.CreateDirectory(Path.GetDirectoryName(paths.AccountsDatabasePath)!);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = paths.AccountsDatabasePath,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
            Pooling = false
        };

        return new SqliteConnection(builder.ToString());
    }
}
