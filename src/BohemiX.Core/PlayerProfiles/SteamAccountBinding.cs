namespace BohemiX.Core.PlayerProfiles;

public sealed record SteamAccountIdentity(
    ulong SteamId,
    string PersonaName,
    bool OwnsKcd2);

public sealed record SteamAccountBinding(
    Guid PlayerId,
    ulong SteamId,
    string PersonaName,
    DateTimeOffset BoundAt,
    DateTimeOffset LastValidatedAt);

public interface IPlayerSteamAccountBindingService
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<SteamAccountBinding?> GetBindingByPlayerIdAsync(Guid playerId, CancellationToken cancellationToken = default);

    Task<SteamAccountBinding?> GetBindingBySteamIdAsync(ulong steamId, CancellationToken cancellationToken = default);

    Task<SteamAccountBinding> BindAsync(
        Guid playerId,
        SteamAccountIdentity identity,
        CancellationToken cancellationToken = default);

    Task UnbindAsync(Guid playerId, CancellationToken cancellationToken = default);
}

public sealed class SteamAccountBindingException : Exception
{
    public SteamAccountBindingException(
        string message,
        SteamAccountBindingFailureKind failureKind)
        : base(message)
    {
        FailureKind = failureKind;
    }

    public SteamAccountBindingFailureKind FailureKind { get; }
}

public enum SteamAccountBindingFailureKind
{
    PlayerAlreadyBoundToDifferentSteamAccount,
    SteamAccountAlreadyBoundToDifferentPlayer
}
