using System;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace BohemiX.App;

public static class BohemiXApplicationHost
{
    public static ServiceProvider BuildServiceProvider()
    {
        Log.Logger = CreateLogger();

        var services = new ServiceCollection();
        services.AddBohemiXApplication(Log.Logger);
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
    }

    private static ILogger CreateLogger()
    {
        var logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BohemiX",
            "logs");
        Directory.CreateDirectory(logDirectory);

        return new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(Path.Combine(logDirectory, "bohemix-.log"), rollingInterval: RollingInterval.Day)
            .CreateLogger();
    }
}
