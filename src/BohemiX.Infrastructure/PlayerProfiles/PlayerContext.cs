using BohemiX.Core.PlayerProfiles;

namespace BohemiX.Infrastructure.PlayerProfiles;

public sealed class PlayerContext : IPlayerContextAccessor
{
    private readonly object syncRoot = new();
    private PlayerProfile? currentPlayer;

    public PlayerProfile? CurrentPlayer
    {
        get
        {
            lock (syncRoot)
            {
                return currentPlayer;
            }
        }
    }

    public event EventHandler<PlayerChangedEventArgs>? CurrentPlayerChanged;

    public void SetCurrentPlayer(PlayerProfile? player, PlayerChangeReason reason)
    {
        PlayerProfile? previous;
        lock (syncRoot)
        {
            previous = currentPlayer;
            currentPlayer = player;
        }

        if (previous == player && reason != PlayerChangeReason.Updated)
        {
            return;
        }

        CurrentPlayerChanged?.Invoke(this, new PlayerChangedEventArgs(previous, player, reason));
    }
}
