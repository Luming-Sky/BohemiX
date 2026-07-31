namespace BohemiX.Core.PlayerProfiles;

public interface IPlayerContext
{
    PlayerProfile? CurrentPlayer { get; }

    event EventHandler<PlayerChangedEventArgs>? CurrentPlayerChanged;
}

public interface IPlayerContextAccessor : IPlayerContext
{
    void SetCurrentPlayer(PlayerProfile? player, PlayerChangeReason reason);
}
