using BohemiX.Infrastructure.Persistence;
using BohemiX.Infrastructure.Services;
using Serilog;

namespace BohemiX.Core.Tests;

public sealed class AppSettingsGraphicsQualityTests
{
    [Fact]
    public async Task MissingGraphicsKeysDefaultToMediumAndValidValuesRoundTrip()
    {
        var root = CreateRoot();
        try
        {
            var (_, service) = await CreateServiceAsync(root);
            var initial = await service.LoadAsync();

            Assert.Equal("Medium", initial.ForgeRenderQuality);
            Assert.Equal("Medium", initial.ForgeTextureQuality);

            await service.SaveAsync(initial with
            {
                ForgeRenderQuality = "low",
                ForgeTextureQuality = "HIGH"
            });
            var reloaded = await service.LoadAsync();

            Assert.Equal("Low", reloaded.ForgeRenderQuality);
            Assert.Equal("High", reloaded.ForgeTextureQuality);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InvalidStoredGraphicsValuesFallBackToMedium()
    {
        var root = CreateRoot();
        try
        {
            var (connectionFactory, service) = await CreateServiceAsync(root);
            await using var connection = connectionFactory.CreateConnection();
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO app_settings (key, value) VALUES ('forgeRenderQuality', 'Ultra')
                ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                INSERT INTO app_settings (key, value) VALUES ('forgeTextureQuality', 'broken')
                ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                """;
            await command.ExecuteNonQueryAsync();

            var reloaded = await service.LoadAsync();

            Assert.Equal("Medium", reloaded.ForgeRenderQuality);
            Assert.Equal("Medium", reloaded.ForgeTextureQuality);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "BohemiX.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task<(SqliteConnectionFactory ConnectionFactory, AppSettingsService Service)> CreateServiceAsync(string root)
    {
        var pathService = new ApplicationPathService(root);
        var logger = new LoggerConfiguration().CreateLogger();
        var connectionFactory = new SqliteConnectionFactory(pathService);
        var service = new AppSettingsService(pathService, connectionFactory, logger);
        var startup = new AppStartupService(pathService, service, connectionFactory, logger);
        await startup.InitializeAsync();
        return (connectionFactory, service);
    }
}
