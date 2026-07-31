namespace BohemiX.Core.Models;

using System.Runtime.InteropServices;
using System.Security;

public sealed record NexusModsOptions(
    string DefaultGameDomainName = "kingdomcomedeliverance2",
    string GraphQlEndpoint = "https://api.nexusmods.com/v2/graphql",
    string RestApiBaseUrl = "https://api.nexusmods.com/v1/",
    bool EnableLegacyBrowserCookieAuth = false);

public sealed record NexusSsoOptions(
    string ApplicationSlug = "",
    string WebSocketEndpoint = "wss://sso.nexusmods.com",
    string AuthorizationEndpoint = "https://www.nexusmods.com/sso",
    TimeSpan? AuthorizationTimeout = null)
{
    public TimeSpan EffectiveAuthorizationTimeout => AuthorizationTimeout ?? TimeSpan.FromMinutes(3);
}

public sealed record NexusAccountBinding(
    long UserId,
    string Name,
    bool IsPremium,
    bool IsSupporter);

public sealed record NexusGameInfo(
    int Id,
    string DomainName,
    string Name,
    int ModCount);

public sealed record NexusModSearchRequest(
    string GameDomainName,
    string? Query = null,
    int Offset = 0,
    int Count = 25);

public sealed record NexusModSearchResult(
    IReadOnlyList<NexusModSummary> Mods,
    int TotalCount);

public sealed record NexusModSummary(
    int ModId,
    string Name,
    string Summary,
    string Version,
    string Author,
    int Downloads,
    int Endorsements,
    long? FileSizeInBytes,
    string? ThumbnailUrl,
    DateTimeOffset? UpdatedAt,
    bool DirectDownloadEnabled,
    string Status);

public sealed record NexusModDetails(
    int ModId,
    int GameId,
    string GameDomainName,
    string Name,
    string Summary,
    string Description,
    string Version,
    string Author,
    int Downloads,
    int Endorsements,
    bool DirectDownloadEnabled,
    string Status,
    IReadOnlyList<NexusModRequirement> Requirements,
    IReadOnlyList<NexusModFile> Files,
    string? ThumbnailUrl = null);

public sealed record NexusModRequirement(
    int? ModId,
    string ModName,
    int? GameId,
    bool IsExternal,
    string? Notes,
    string? Url);

public sealed record NexusModFile(
    int FileId,
    int ModId,
    string Name,
    string FileName,
    string Category,
    string Version,
    long? SizeInBytes,
    string? Md5,
    int UploadedTimestamp,
    bool IsPrimary,
    bool IsManagerDownload,
    string? Description = null);

public sealed record NexusDownloadLink(
    Uri Uri,
    string Name,
    string ShortName);

public sealed record NexusCookieAuthProbeResult(
    bool Success,
    string? BrowserName,
    string? ErrorCode,
    string Message,
    IReadOnlyList<NexusCookieAuthProbeAttempt> Attempts);

public sealed record NexusCookieAuthProbeAttempt(
    string BrowserName,
    string ErrorCode,
    string Message);

public sealed class NexusCookieAuthLease : IDisposable
{
    public NexusCookieAuthLease(string browserName, SecureCookieHeader cookieHeader)
    {
        BrowserName = browserName;
        CookieHeader = cookieHeader;
    }

    public string BrowserName { get; }

    public SecureCookieHeader CookieHeader { get; }

    public string RevealHeaderValue()
    {
        return CookieHeader.RevealAsString();
    }

    public void Dispose()
    {
        CookieHeader.Dispose();
    }
}

public sealed class SecureCookieHeader : IDisposable
{
    private readonly bool retainNativeAllocationAfterDisposeForAudit;
    private IntPtr buffer;
    private int charCount;
    private bool disposed;

    private SecureCookieHeader(string value, bool retainNativeAllocationAfterDisposeForAudit)
    {
        ArgumentNullException.ThrowIfNull(value);
        this.retainNativeAllocationAfterDisposeForAudit = retainNativeAllocationAfterDisposeForAudit;
        charCount = value.Length;
        buffer = Marshal.AllocHGlobal(Math.Max(1, charCount * sizeof(char)));

        var chars = value.ToCharArray();
        try
        {
            Marshal.Copy(chars, 0, buffer, charCount);
        }
        finally
        {
            Array.Clear(chars);
        }
    }

    ~SecureCookieHeader()
    {
        ReleaseNativeMemory();
    }

    public static SecureCookieHeader FromPlainText(string value, bool retainNativeAllocationAfterDisposeForAudit = false)
    {
        return new SecureCookieHeader(value, retainNativeAllocationAfterDisposeForAudit);
    }

    public string RevealAsString()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return Marshal.PtrToStringUni(buffer, charCount) ?? string.Empty;
    }

    public SecureString ToSecureString()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var secure = new SecureString();
        var value = RevealAsString();
        try
        {
            foreach (var character in value)
            {
                secure.AppendChar(character);
            }

            secure.MakeReadOnly();
            return secure;
        }
        finally
        {
            value = string.Empty;
        }
    }

    public IntPtr DangerousGetAddressForSecurityAudit()
    {
        return buffer;
    }

    public byte[] DangerousCopyNativeBytesForSecurityAudit()
    {
        if (buffer == IntPtr.Zero || charCount <= 0)
        {
            return [];
        }

        var bytes = new byte[charCount * sizeof(char)];
        Marshal.Copy(buffer, bytes, 0, bytes.Length);
        return bytes;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        ZeroNativeMemory();
        disposed = true;

        if (!retainNativeAllocationAfterDisposeForAudit)
        {
            ReleaseNativeMemory();
            GC.SuppressFinalize(this);
        }
    }

    public void ReleaseRetainedNativeAllocationForSecurityAudit()
    {
        ReleaseNativeMemory();
        GC.SuppressFinalize(this);
    }

    private void ZeroNativeMemory()
    {
        if (buffer == IntPtr.Zero || charCount <= 0)
        {
            return;
        }

        var zeroBytes = new byte[charCount * sizeof(char)];
        Marshal.Copy(zeroBytes, 0, buffer, zeroBytes.Length);
    }

    private void ReleaseNativeMemory()
    {
        if (buffer == IntPtr.Zero)
        {
            return;
        }

        ZeroNativeMemory();
        Marshal.FreeHGlobal(buffer);
        buffer = IntPtr.Zero;
        charCount = 0;
    }
}

public sealed record ModDownloaderOptions(
    int MaxConcurrentDownloads = 3,
    int BufferSize = 1024 * 1024,
    TimeSpan DownloadInactivityTimeout = default)
{
    public TimeSpan EffectiveDownloadInactivityTimeout =>
        DownloadInactivityTimeout > TimeSpan.Zero
            ? DownloadInactivityTimeout
            : TimeSpan.FromSeconds(30);
}

public sealed record NexusModDownloadRequest(
    string GameDomainName,
    int ModId,
    int FileId,
    Uri DownloadUri,
    string FileName,
    string DestinationDirectory,
    string? ExpectedMd5,
    Guid? GameEnvironmentId = null,
    Guid? ModPackSessionId = null,
    string? ModPackId = null,
    string? ModPackName = null)
{
    [Obsolete("Use GameEnvironmentId. Mod downloads are not account-scoped.")]
    public Guid? PlayerId => GameEnvironmentId;
}

public enum ModDownloadStatus
{
    Pending,
    Resolving,
    Downloading,
    Paused,
    Completed,
    Canceled,
    Failed,
    ChecksumFailed
}

public sealed record ModDownloadProgress(
    string QueueKey,
    int ModId,
    int FileId,
    string FileName,
    long BytesDownloaded,
    long? TotalBytes,
    double? Percent,
    double BytesPerSecond,
    ModDownloadStatus Status);

public sealed record ModDownloadResult(
    bool Success,
    ModDownloadStatus Status,
    NexusModDownloadRequest Request,
    string? FinalPath,
    string? ErrorMessage);

public sealed record ModDownloadQueueItem(
    string QueueKey,
    NexusModDownloadRequest Request,
    ModDownloadStatus Status,
    string? ErrorMessage);
