using System.Net;

namespace BohemiX.Core.Models;

public sealed class NexusModsException : Exception
{
    public NexusModsException(
        string message,
        HttpStatusCode? statusCode = null,
        TimeSpan? retryAfter = null,
        string? responseBody = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        RetryAfter = retryAfter;
        ResponseBody = responseBody;
    }

    public HttpStatusCode? StatusCode { get; }

    public TimeSpan? RetryAfter { get; }

    public string? ResponseBody { get; }

    public bool IsAuthenticationFailure => StatusCode == HttpStatusCode.Unauthorized;

    public bool IsRateLimited => (int?)StatusCode == 429;
}
