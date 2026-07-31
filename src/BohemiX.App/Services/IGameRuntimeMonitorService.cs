using System;
using System.Threading.Tasks;
using BohemiX.App.Models;

namespace BohemiX.App.Services;

public interface IGameRuntimeMonitorService
{
    event Action<GameRuntimeExitUpdate>? Exited;

    void Start(GameRuntimeMonitorRequest request);

    Task StopAsync();
}

public sealed record GameRuntimeMonitorOptions(TimeSpan PostExitDelay)
{
    public static GameRuntimeMonitorOptions Default { get; } = new(TimeSpan.FromSeconds(4));
}
