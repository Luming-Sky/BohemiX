namespace BohemiX.Core.PlayerProfiles;

public sealed record PlayerProfile(
    Guid Id,
    string DisplayName,
    string? Avatar,
    string Provider,
    string? Bio,
    DateTimeOffset CreatedTime,
    DateTimeOffset LastLoginTime,
    string? CloudId,
    string? Email,
    string? AccessToken,
    string? RefreshToken,
    bool IsCloudUser,
    DateTimeOffset? SyncTime,
    int Version)
{
    [Obsolete("游戏平台属于游戏环境，不属于账户资料。")]
    public string Platform { get; init; } = "unbound";

    [Obsolete("游戏目录属于游戏环境，不属于账户资料。")]
    public string? GameInstallPath { get; init; }

    [Obsolete("Use the account-only PlayerProfile constructor.")]
    public PlayerProfile(
        Guid Id,
        string DisplayName,
        string? Avatar,
        string Platform,
        string Provider,
        string? GameInstallPath,
        DateTimeOffset CreatedTime,
        DateTimeOffset LastLoginTime,
        string? CloudId,
        string? Email,
        string? AccessToken,
        string? RefreshToken,
        bool IsCloudUser,
        DateTimeOffset? SyncTime,
        int Version)
        : this(Id, DisplayName, Avatar, Provider, null, CreatedTime, LastLoginTime, CloudId, Email,
            AccessToken, RefreshToken, IsCloudUser, SyncTime, Version)
    {
        this.Platform = Platform;
        this.GameInstallPath = GameInstallPath;
    }
}

public sealed record PlayerProfileDraft(
    string DisplayName,
    string? Avatar,
    string Provider,
    string? Bio = null)
{
    [Obsolete("游戏平台属于游戏环境，不属于账户资料。")]
    public string Platform { get; init; } = "unbound";

    [Obsolete("游戏目录属于游戏环境，不属于账户资料。")]
    public string? GameInstallPath { get; init; }

    [Obsolete("Use the account-only PlayerProfileDraft constructor.")]
    public PlayerProfileDraft(
        string DisplayName,
        string? Avatar,
        string Platform,
        string Provider,
        string? GameInstallPath)
        : this(DisplayName, Avatar, Provider, null)
    {
        this.Platform = Platform;
        this.GameInstallPath = GameInstallPath;
    }
}

public sealed record PlayerProfileUpdate(
    string DisplayName,
    string? Avatar,
    string? Bio = null)
{
    [Obsolete("游戏平台属于游戏环境，不属于账户资料。")]
    public string Platform { get; init; } = "unbound";

    [Obsolete("游戏目录属于游戏环境，不属于账户资料。")]
    public string? GameInstallPath { get; init; }

    [Obsolete("Use the account-only PlayerProfileUpdate constructor.")]
    public PlayerProfileUpdate(
        string DisplayName,
        string? Avatar,
        string Platform,
        string? GameInstallPath)
        : this(DisplayName, Avatar, null)
    {
        this.Platform = Platform;
        this.GameInstallPath = GameInstallPath;
    }
}

public static class PlayerProviderIds
{
    public const string Local = "local";
    public const string BohemiX = "bohemix";
    public const string Steam = "steam";
    public const string Discord = "discord";
    public const string Google = "google";
}

[Obsolete("游戏平台属于游戏环境，不属于账户资料。")]
public static class PlayerPlatformIds
{
    public const string Steam = "steam";
    public const string Gog = "gog";
}

public enum PlayerChangeReason
{
    Initialized,
    Created,
    Switched,
    Updated,
    Deleted
}

public sealed class PlayerChangedEventArgs : EventArgs
{
    public PlayerChangedEventArgs(
        PlayerProfile? previousPlayer,
        PlayerProfile? currentPlayer,
        PlayerChangeReason reason)
    {
        PreviousPlayer = previousPlayer;
        CurrentPlayer = currentPlayer;
        Reason = reason;
    }

    public PlayerProfile? PreviousPlayer { get; }

    public PlayerProfile? CurrentPlayer { get; }

    public PlayerChangeReason Reason { get; }
}
