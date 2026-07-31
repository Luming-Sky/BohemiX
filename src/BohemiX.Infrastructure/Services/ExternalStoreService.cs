using System.Diagnostics;
using BohemiX.Core.Services;
using Serilog;

namespace BohemiX.Infrastructure.Services;

public sealed class ExternalStoreService : IExternalStoreService
{
    private const string Kcd2SteamUrl = "https://store.steampowered.com/app/1771300/Kingdom_Come_Deliverance_II/";

    private readonly ILogger logger;

    public ExternalStoreService(ILogger logger)
    {
        this.logger = logger.ForContext<ExternalStoreService>();
    }

    public Task OpenKcd2SteamPageAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Kcd2SteamUrl,
                UseShellExecute = true
            });

            logger.Information("Opened KCD2 Steam store page");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            logger.Error(ex, "Unable to open KCD2 Steam store page");
        }

        return Task.CompletedTask;
    }
}

