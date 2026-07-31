namespace BohemiX.Core.Models;

public sealed class WorkshopException : Exception
{
    public WorkshopException(
        string message,
        WorkshopFailureKind failureKind,
        Exception? innerException = null)
        : base(message, innerException)
    {
        FailureKind = failureKind;
    }

    public WorkshopFailureKind FailureKind { get; }
}

public enum WorkshopFailureKind
{
    SteamUnavailable,
    NotLoggedIn,
    GameNotOwned,
    QueryFailed,
    SubscriptionFailed,
    DownloadTimeout,
    DeploymentFailed
}
