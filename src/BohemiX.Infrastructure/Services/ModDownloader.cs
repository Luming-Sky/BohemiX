using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using BohemiX.Core.Models;
using BohemiX.Core.PlayerProfiles;
using BohemiX.Core.Services;
using Serilog;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace BohemiX.Infrastructure.Services;

public sealed class ModDownloader : IModDownloader, IDisposable
{
    private const string UserAgent = "BohemiX/0.1.0 (NexusAccountBinding)";
    private const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36 Edg/125.0.0.0";
    private const int MaxRedirects = 8;
    private const int MaxDownloadAttempts = 4;

    private sealed class DownloadTransportException(string message, Exception innerException)
        : IOException(message, innerException);

    private enum QueueAction
    {
        None,
        Pause,
        Cancel
    }

    private sealed class QueueExecutionState
    {
        public readonly object SyncRoot = new();
        public CancellationTokenSource? CancellationTokenSource;
        public QueueAction RequestedAction;
    }

    private readonly ConcurrentDictionary<string, ModDownloadQueueItem> queue = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, QueueExecutionState> executionStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly HttpClient httpClient;
    private readonly SemaphoreSlim downloadGate;
    private readonly INexusModService nexusModService;
    private readonly INexusCookieAuthService cookieAuthService;
    private readonly IModCatalogService modCatalogService;
    private readonly IApplicationPathService? applicationPathService;
    private readonly INexusDownloadAuthorizationService? authorizationService;
    private readonly ModDownloaderOptions options;
    private readonly ILogger logger;

    public ModDownloader(
        INexusModService nexusModService,
        INexusCookieAuthService cookieAuthService,
        IModCatalogService modCatalogService,
        ModDownloaderOptions options,
        ILogger logger)
        : this(
            nexusModService,
            cookieAuthService,
            modCatalogService,
            options,
            logger,
            (IApplicationPathService?)null,
            new HttpClientHandler { AllowAutoRedirect = false })
    {
    }

    public ModDownloader(
        INexusModService nexusModService,
        INexusCookieAuthService cookieAuthService,
        IModCatalogService modCatalogService,
        ModDownloaderOptions options,
        ILogger logger,
        IApplicationPathService applicationPathService)
        : this(
            nexusModService,
            cookieAuthService,
            modCatalogService,
            options,
            logger,
            applicationPathService,
            new HttpClientHandler { AllowAutoRedirect = false })
    {
    }

    public ModDownloader(
        INexusModService nexusModService,
        INexusCookieAuthService cookieAuthService,
        IModCatalogService modCatalogService,
        ModDownloaderOptions options,
        ILogger logger,
        IApplicationPathService applicationPathService,
        INexusDownloadAuthorizationService authorizationService)
        : this(
            nexusModService,
            cookieAuthService,
            modCatalogService,
            options,
            logger,
            applicationPathService,
            new HttpClientHandler { AllowAutoRedirect = false },
            authorizationService)
    {
    }

    internal ModDownloader(
        INexusModService nexusModService,
        INexusCookieAuthService cookieAuthService,
        IModCatalogService modCatalogService,
        ModDownloaderOptions options,
        ILogger logger,
        HttpMessageHandler httpMessageHandler)
        : this(
            nexusModService,
            cookieAuthService,
            modCatalogService,
            options,
            logger,
            (IApplicationPathService?)null,
            httpMessageHandler)
    {
    }

    [Obsolete("Mod downloads are scoped to game environments, not player profiles.")]
    internal ModDownloader(
        INexusModService nexusModService,
        INexusCookieAuthService cookieAuthService,
        IModCatalogService modCatalogService,
        ModDownloaderOptions options,
        ILogger logger,
        IPlayerContext legacyPlayerContext,
        HttpMessageHandler httpMessageHandler)
        : this(
            nexusModService,
            cookieAuthService,
            modCatalogService,
            options,
            logger,
            new LegacyPlayerEnvironmentPathService(legacyPlayerContext),
            httpMessageHandler)
    {
    }

    internal ModDownloader(
        INexusModService nexusModService,
        INexusCookieAuthService cookieAuthService,
        IModCatalogService modCatalogService,
        ModDownloaderOptions options,
        ILogger logger,
        IApplicationPathService? applicationPathService,
        HttpMessageHandler httpMessageHandler,
        INexusDownloadAuthorizationService? authorizationService = null)
    {
        this.nexusModService = nexusModService;
        this.cookieAuthService = cookieAuthService;
        this.modCatalogService = modCatalogService;
        this.applicationPathService = applicationPathService;
        this.authorizationService = authorizationService;
        this.options = options;
        this.logger = logger.ForContext<ModDownloader>();
        httpClient = new HttpClient(httpMessageHandler);
        downloadGate = new SemaphoreSlim(Math.Max(1, options.MaxConcurrentDownloads));
    }

    public IReadOnlyCollection<ModDownloadQueueItem> Queue
    {
        get
        {
            IEnumerable<ModDownloadQueueItem> items = queue.Values;
            if (applicationPathService is not null)
            {
                var currentEnvironmentId = applicationPathService.GetPaths().GameEnvironmentId;
                items = items.Where(item => item.Request.GameEnvironmentId == currentEnvironmentId);
            }

            return items
            .OrderBy(item => item.Request.ModId)
            .ThenBy(item => item.Request.FileId)
            .ToArray();
        }
    }

    public bool PauseQueueItem(string queueKey)
    {
        return RequestQueueAction(queueKey, QueueAction.Pause);
    }

    public bool ResumeQueueItem(string queueKey)
    {
        if (!TryGetCurrentEnvironmentQueueItem(queueKey, out var item)
            || item.Status != ModDownloadStatus.Paused)
        {
            return false;
        }

        UpdateQueueItem(item.Request, ModDownloadStatus.Pending);
        var executionState = GetExecutionState(queueKey);
        lock (executionState.SyncRoot)
        {
            executionState.RequestedAction = QueueAction.None;
        }

        return true;
    }

    public bool RetryQueueItem(string queueKey)
    {
        if (!TryGetCurrentEnvironmentQueueItem(queueKey, out var item)
            || item.Status is ModDownloadStatus.Downloading or ModDownloadStatus.Resolving or ModDownloadStatus.Completed)
        {
            return false;
        }

        UpdateQueueItem(item.Request, ModDownloadStatus.Pending);
        var executionState = GetExecutionState(queueKey);
        lock (executionState.SyncRoot)
        {
            executionState.RequestedAction = QueueAction.None;
        }

        return true;
    }

    public bool CancelQueueItem(string queueKey)
    {
        return RequestQueueAction(queueKey, QueueAction.Cancel);
    }

    public int PauseQueue()
    {
        return RequestQueueActionForQueue(QueueAction.Pause);
    }

    public int ResumeQueue()
    {
        var resumed = 0;
        foreach (var item in Queue)
        {
            if (ResumeQueueItem(item.QueueKey))
            {
                resumed++;
            }
        }

        return resumed;
    }

    public int CancelQueue()
    {
        return RequestQueueActionForQueue(QueueAction.Cancel);
    }

    public int ClearInactiveQueueItems()
    {
        var removed = 0;
        foreach (var item in Queue)
        {
            if (item.Status is not (ModDownloadStatus.Completed or ModDownloadStatus.Canceled or ModDownloadStatus.Failed or ModDownloadStatus.ChecksumFailed))
            {
                continue;
            }

            if (queue.TryRemove(item.QueueKey, out _))
            {
                executionStates.TryRemove(item.QueueKey, out _);
                removed++;
            }
        }

        return removed;
    }

    public int ClearCompletedQueueItems()
    {
        var removed = 0;
        foreach (var item in Queue)
        {
            if (item.Status != ModDownloadStatus.Completed)
            {
                continue;
            }

            if (queue.TryRemove(item.QueueKey, out _))
            {
                executionStates.TryRemove(item.QueueKey, out _);
                removed++;
            }
        }

        return removed;
    }

    public int ClearCanceledQueueItems()
    {
        var removed = 0;
        foreach (var item in Queue)
        {
            if (item.Status != ModDownloadStatus.Canceled)
            {
                continue;
            }

            if (queue.TryRemove(item.QueueKey, out _))
            {
                executionStates.TryRemove(item.QueueKey, out _);
                removed++;
            }
        }

        return removed;
    }

    public async Task<IReadOnlyList<ModDownloadQueueItem>> EnqueueWithDependenciesAsync(
        string gameDomainName,
        int modId,
        string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        var environmentId = CaptureCurrentEnvironmentId();
        var installedMods = await modCatalogService.LoadInstalledModsAsync(cancellationToken);
        var installedKeys = CreateInstalledLookup(installedMods);
        var visited = new HashSet<int>();
        var added = new List<ModDownloadQueueItem>();

        await EnqueueWithDependenciesCoreAsync(
            gameDomainName,
            modId,
            destinationDirectory,
            installedKeys,
            visited,
            added,
            environmentId,
            cancellationToken);

        return added;
    }

    public async Task<IReadOnlyList<ModDownloadQueueItem>> EnqueueFileWithDependenciesAsync(
        string gameDomainName,
        int modId,
        NexusModFile selectedFile,
        string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selectedFile);
        return await EnqueueFilesWithDependenciesAsync(
            gameDomainName,
            modId,
            [selectedFile],
            destinationDirectory,
            cancellationToken);
    }

    public async Task<IReadOnlyList<ModDownloadQueueItem>> EnqueueFilesWithDependenciesAsync(
        string gameDomainName,
        int modId,
        IReadOnlyCollection<NexusModFile> selectedFiles,
        string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        return await EnqueueFilesCoreAsync(
            gameDomainName,
            modId,
            selectedFiles,
            destinationDirectory,
            includeDependencies: true,
            cancellationToken);
    }

    public async Task<IReadOnlyList<ModDownloadQueueItem>> EnqueueFilesAsync(
        string gameDomainName,
        int modId,
        IReadOnlyCollection<NexusModFile> selectedFiles,
        string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        return await EnqueueFilesCoreAsync(
            gameDomainName,
            modId,
            selectedFiles,
            destinationDirectory,
            includeDependencies: false,
            cancellationToken);
    }

    private async Task<IReadOnlyList<ModDownloadQueueItem>> EnqueueFilesCoreAsync(
        string gameDomainName,
        int modId,
        IReadOnlyCollection<NexusModFile> selectedFiles,
        string destinationDirectory,
        bool includeDependencies,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selectedFiles);
        var environmentId = CaptureCurrentEnvironmentId();
        var files = selectedFiles
            .GroupBy(file => file.FileId)
            .Select(group => group.First())
            .ToArray();
        if (files.Length == 0)
        {
            throw new ArgumentException("Select at least one Nexus file.", nameof(selectedFiles));
        }

        foreach (var selectedFile in files)
        {
            if (selectedFile.ModId != modId || selectedFile.FileId <= 0)
            {
                throw new ArgumentException("A selected Nexus file does not belong to this mod.", nameof(selectedFiles));
            }
        }

        var installedMods = await modCatalogService.LoadInstalledModsAsync(cancellationToken);
        var installedKeys = CreateInstalledLookup(installedMods);
        if (IsInstalled(modId, installedKeys))
        {
            logger.Information("Skipped Nexus mod {ModId} because it is already installed", modId);
            return [];
        }

        var visited = new HashSet<int> { modId };
        var added = new List<ModDownloadQueueItem>();
        var details = await nexusModService.GetModDetailsAsync(gameDomainName, modId, cancellationToken);

        if (includeDependencies)
        {
            foreach (var requirement in details.Requirements)
            {
                if (requirement.IsExternal
                    || requirement.ModId is not { } requiredModId
                    || IsInstalled(requiredModId, installedKeys))
                {
                    continue;
                }

                await EnqueueWithDependenciesCoreAsync(
                    gameDomainName,
                    requiredModId,
                    destinationDirectory,
                    installedKeys,
                    visited,
                    added,
                    environmentId,
                    cancellationToken);
            }
        }

        foreach (var selectedFile in files)
        {
            var link = await ResolveDownloadLinkAsync(details, selectedFile, cancellationToken);
            var request = new NexusModDownloadRequest(
                details.GameDomainName,
                modId,
                selectedFile.FileId,
                link.Uri,
                SanitizeFileName(selectedFile.FileName),
                destinationDirectory,
                selectedFile.Md5,
                environmentId);

            added.Add(AddOrUpdateQueueItem(request, ModDownloadStatus.Pending));
        }

        return added;
    }

    public async Task<IReadOnlyList<ModDownloadResult>> StartQueuedDownloadsAsync(
        IProgress<ModDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var pendingItems = Queue
            .Where(item => item.Status is ModDownloadStatus.Pending or ModDownloadStatus.Failed or ModDownloadStatus.ChecksumFailed)
            .OrderBy(item => item.Request.ModId)
            .ThenBy(item => item.Request.FileId)
            .ToArray();

        logger.Information("Starting {Count} queued mod download(s).", pendingItems.Length);
        var tasks = pendingItems.Select(item => DownloadAsync(item.Request, progress, cancellationToken));
        return await Task.WhenAll(tasks);
    }

    public async Task<ModDownloadResult> DownloadAsync(
        NexusModDownloadRequest request,
        IProgress<ModDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        request = BindRequestToCurrentEnvironment(request);
        var queueKey = BuildQueueKey(request);
        await downloadGate.WaitAsync(cancellationToken);
        var executionState = GetExecutionState(queueKey);
        CancellationTokenSource? linkedCancellationSource = null;

        try
        {
            QueueAction queuedAction;
            linkedCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lock (executionState.SyncRoot)
            {
                queuedAction = executionState.RequestedAction;
                if (queuedAction == QueueAction.None)
                {
                    executionState.CancellationTokenSource = linkedCancellationSource;
                }
            }

            if (queuedAction != QueueAction.None)
            {
                linkedCancellationSource.Dispose();
                linkedCancellationSource = null;
                return HandleControlledCancellation(queueKey, request);
            }

            UpdateQueueItem(request, ModDownloadStatus.Downloading);
            logger.Information(
                "Starting mod file download for {ModId}/{FileId} from {Host}.",
                request.ModId,
                request.FileId,
                request.DownloadUri.Host);
            return await DownloadCoreAsync(queueKey, request, progress, linkedCancellationSource.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (TryGetRequestedAction(queueKey, out var action) && action != QueueAction.None)
        {
            return HandleControlledCancellation(queueKey, request);
        }
        catch (TaskCanceledException ex)
        {
            return Fail(request, ModDownloadStatus.Failed, $"Download timed out: {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            return Fail(request, ModDownloadStatus.Failed, $"Unable to download mod file: {ex.Message}", ex);
        }
        finally
        {
            if (linkedCancellationSource is not null)
            {
                lock (executionState.SyncRoot)
                {
                    if (ReferenceEquals(executionState.CancellationTokenSource, linkedCancellationSource))
                    {
                        executionState.CancellationTokenSource = null;
                        if (executionState.RequestedAction == QueueAction.None)
                        {
                            executionStates.TryRemove(queueKey, out _);
                        }
                    }
                }

                linkedCancellationSource.Dispose();
            }

            downloadGate.Release();
        }
    }

    public void Dispose()
    {
        httpClient.Dispose();
        downloadGate.Dispose();
    }

    private bool RequestQueueAction(string queueKey, QueueAction action)
    {
        if (!TryGetCurrentEnvironmentQueueItem(queueKey, out var item))
        {
            return false;
        }

        var executionState = GetExecutionState(queueKey);
        lock (executionState.SyncRoot)
        {
            if (item.Status == ModDownloadStatus.Completed || item.Status == ModDownloadStatus.Canceled)
            {
                return false;
            }

            if (action == QueueAction.Pause
                && item.Status is ModDownloadStatus.Paused or ModDownloadStatus.Failed or ModDownloadStatus.ChecksumFailed)
            {
                return false;
            }

            executionState.RequestedAction = action;
            executionState.CancellationTokenSource?.Cancel();
        }

        if (item.Status is ModDownloadStatus.Pending or ModDownloadStatus.Resolving or ModDownloadStatus.Paused or ModDownloadStatus.Failed or ModDownloadStatus.ChecksumFailed)
        {
            UpdateQueueItem(
                item.Request,
                action == QueueAction.Pause ? ModDownloadStatus.Paused : ModDownloadStatus.Canceled,
                action == QueueAction.Cancel ? "Download canceled." : "Download paused.");
        }

        if (action == QueueAction.Cancel)
        {
            var tempPath = Path.Combine(item.Request.DestinationDirectory, SanitizeFileName(item.Request.FileName)) + ".tmp";
            TryDeleteFile(tempPath);
        }

        return true;
    }

    private int RequestQueueActionForQueue(QueueAction action)
    {
        var changed = 0;
        foreach (var item in Queue)
        {
            if (RequestQueueAction(item.QueueKey, action))
            {
                changed++;
            }
        }

        return changed;
    }

    private async Task EnqueueWithDependenciesCoreAsync(
        string gameDomainName,
        int modId,
        string destinationDirectory,
        IReadOnlySet<string> installedKeys,
        HashSet<int> visited,
        List<ModDownloadQueueItem> added,
        Guid? playerId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!visited.Add(modId))
        {
            return;
        }

        if (IsInstalled(modId, installedKeys))
        {
            logger.Information("Skipped Nexus mod {ModId} because it is already installed", modId);
            return;
        }

        var details = await nexusModService.GetModDetailsAsync(gameDomainName, modId, cancellationToken);
        foreach (var requirement in details.Requirements)
        {
            if (requirement.IsExternal
                || requirement.ModId is not { } requiredModId
                || IsInstalled(requiredModId, installedKeys))
            {
                continue;
            }

            await EnqueueWithDependenciesCoreAsync(
                gameDomainName,
                requiredModId,
                destinationDirectory,
                installedKeys,
                visited,
                added,
                playerId,
                cancellationToken);
        }

        var file = await nexusModService.GetPreferredFileAsync(gameDomainName, modId, cancellationToken);
        var link = await ResolveDownloadLinkAsync(details, file, cancellationToken);
        var request = new NexusModDownloadRequest(
            details.GameDomainName,
            modId,
            file.FileId,
            link.Uri,
            SanitizeFileName(file.FileName),
            destinationDirectory,
            file.Md5,
            playerId);

        var item = AddOrUpdateQueueItem(request, ModDownloadStatus.Pending);
        added.Add(item);
    }

    private async Task<NexusDownloadLink> ResolveDownloadLinkAsync(
        NexusModDetails details,
        NexusModFile file,
        CancellationToken cancellationToken)
    {
        try
        {
            return await nexusModService.GetDownloadLinkAsync(
                details.GameDomainName,
                details.ModId,
                file.FileId,
                cancellationToken);
        }
        catch (NexusModsException ex) when (authorizationService is not null && ShouldRequestDownloadAuthorization(ex))
        {
            logger.Information(
                "Direct Nexus download link was unavailable for mod {ModId}, file {FileId}; requesting browser authorization.",
                details.ModId,
                file.FileId);

            var authorizationResult = await authorizationService.AuthorizeAsync(
                new NexusDownloadAuthorizationRequest(
                    details.GameDomainName,
                    details.ModId,
                    file.FileId,
                    details.Name,
                    file.FileName,
                    BuildOfficialFilePageUri(details.GameDomainName, details.ModId, file.FileId)),
                cancellationToken);

            if (!authorizationResult.Authorized || authorizationResult.Authorization is null)
            {
                throw new NexusModsException(
                    authorizationResult.Message ?? "Nexus browser authorization was canceled.",
                    ex.StatusCode,
                    ex.RetryAfter,
                    ex.ResponseBody);
            }

            return await nexusModService.GetDownloadLinkAsync(
                details.GameDomainName,
                details.ModId,
                file.FileId,
                authorizationResult.Authorization,
                cancellationToken);
        }
    }

    private static bool ShouldRequestDownloadAuthorization(NexusModsException exception)
    {
        if (exception.IsRateLimited)
        {
            return false;
        }

        if (exception.StatusCode is HttpStatusCode.BadRequest
            or HttpStatusCode.Unauthorized
            or HttpStatusCode.Forbidden
            or HttpStatusCode.NotFound)
        {
            return true;
        }

        var text = $"{exception.Message} {exception.ResponseBody}";
        return text.Contains("download link", StringComparison.OrdinalIgnoreCase)
            || text.Contains("download URL", StringComparison.OrdinalIgnoreCase)
            || text.Contains("authorization", StringComparison.OrdinalIgnoreCase)
            || text.Contains("premium", StringComparison.OrdinalIgnoreCase);
    }

    private static Uri BuildOfficialFilePageUri(string gameDomainName, int modId, int fileId)
    {
        var builder = new UriBuilder("https", "www.nexusmods.com")
        {
            Path = $"{Uri.EscapeDataString(gameDomainName.Trim())}/mods/{modId}"
        };
        builder.Query = $"tab=files&file_id={fileId}";
        return builder.Uri;
    }

    private async Task<ModDownloadResult> DownloadCoreAsync(
        string queueKey,
        NexusModDownloadRequest request,
        IProgress<ModDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        string finalPath;
        string tempPath;

        try
        {
            Directory.CreateDirectory(request.DestinationDirectory);
            finalPath = Path.Combine(request.DestinationDirectory, SanitizeFileName(request.FileName));
            tempPath = finalPath + ".tmp";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Fail(request, ModDownloadStatus.Failed, $"Unable to prepare download folder: {ex.Message}", ex);
        }

        try
        {
            if (File.Exists(finalPath)
                && await IsExistingDownloadUsableAsync(finalPath, request, cancellationToken))
            {
                var existingSize = GetTempFileLength(finalPath);
                ReportProgress(progress, queueKey, request, existingSize, existingSize, 100, 0, ModDownloadStatus.Completed);
                UpdateQueueItem(request, ModDownloadStatus.Completed);
                return new ModDownloadResult(true, ModDownloadStatus.Completed, request, finalPath, null);
            }

            TryDeleteFile(finalPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.Warning(ex, "Unable to verify existing mod file {FilePath}", finalPath);
        }

        for (var attempt = 0; attempt < MaxDownloadAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var existingBytes = GetTempFileLength(tempPath);

            using var httpRequest = new HttpRequestMessage(HttpMethod.Get, request.DownloadUri);
            httpRequest.Version = HttpVersion.Version11;
            httpRequest.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
            httpRequest.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
            httpRequest.Headers.TryAddWithoutValidation("Accept", "application/zip,application/x-7z-compressed,application/octet-stream,*/*");
            if (existingBytes > 0)
            {
                httpRequest.Headers.Range = new RangeHeaderValue(existingBytes, null);
            }

            HttpResponseMessage response;
            try
            {
                response = await SendWithRateLimitRetryAsync(httpRequest, cancellationToken);
            }
            catch (HttpRequestException ex) when (attempt < MaxDownloadAttempts - 1)
            {
                logger.Warning(
                    ex,
                    "Mod file download connection failed for {ModId}/{FileId}; retrying from byte {ExistingBytes} using HTTP/1.1.",
                    request.ModId,
                    request.FileId,
                    existingBytes);
                continue;
            }

            using var responseScope = response;
            logger.Information(
                "Mod file download response for {ModId}/{FileId}: HTTP {StatusCode}, ContentLength={ContentLength}.",
                request.ModId,
                request.FileId,
                response.StatusCode,
                response.Content.Headers.ContentLength);

            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && existingBytes > 0)
            {
                TryDeleteFile(tempPath);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                var status = response.StatusCode == (HttpStatusCode)429
                    ? ModDownloadStatus.Failed
                    : ModDownloadStatus.Failed;
                var message = response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => "Download authentication failed.",
                    (HttpStatusCode)429 => "Download rate limit reached.",
                    _ => $"Download failed with HTTP {(int)response.StatusCode}."
                };

                return Fail(request, status, string.IsNullOrWhiteSpace(error) ? message : $"{message} {error}");
            }

            if (response.StatusCode == HttpStatusCode.OK && existingBytes > 0)
            {
                existingBytes = 0;
            }

            var totalBytes = ResolveTotalBytes(response, existingBytes);
            ReportProgress(progress, queueKey, request, existingBytes, totalBytes, CalculatePercent(existingBytes, totalBytes), 0, ModDownloadStatus.Downloading);

            try
            {
                await CopyResponseToTempFileAsync(
                    response,
                    tempPath,
                    existingBytes,
                    totalBytes,
                    queueKey,
                    request,
                    progress,
                    cancellationToken);
            }
            catch (Exception ex) when (
                (ex is TimeoutException or DownloadTransportException)
                && attempt < MaxDownloadAttempts - 1)
            {
                logger.Warning(
                    ex,
                    "Mod file download for {ModId}/{FileId} lost its connection; retrying from byte {ExistingBytes} using HTTP/1.1.",
                    request.ModId,
                    request.FileId,
                    GetTempFileLength(tempPath));
                continue;
            }
            catch (Exception ex) when (ex is TimeoutException or DownloadTransportException)
            {
                return Fail(
                    request,
                    ModDownloadStatus.Failed,
                    $"Download connection failed after {MaxDownloadAttempts} attempts: {ex.Message}",
                    ex);
            }
            catch (OperationCanceledException) when (TryHandleQueuedCancellation(queueKey, request, tempPath))
            {
                return HandleControlledCancellation(queueKey, request);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Fail(request, ModDownloadStatus.Failed, $"Unable to write download file: {ex.Message}", ex);
            }

            var downloadedBytes = GetTempFileLength(tempPath);
            if (totalBytes is > 0 && downloadedBytes < totalBytes.Value)
            {
                if (attempt < MaxDownloadAttempts - 1)
                {
                    logger.Warning(
                        "Mod file download for {ModId}/{FileId} ended early at {DownloadedBytes}/{TotalBytes} bytes; retrying with HTTP/1.1.",
                        request.ModId,
                        request.FileId,
                        downloadedBytes,
                        totalBytes.Value);
                    continue;
                }

                return Fail(
                    request,
                    ModDownloadStatus.Failed,
                    $"Download ended early after {MaxDownloadAttempts} attempts ({downloadedBytes}/{totalBytes.Value} bytes).");
            }

            return await CompleteDownloadAsync(queueKey, request, tempPath, finalPath, progress, cancellationToken);
        }

        return Fail(request, ModDownloadStatus.Failed, "Unable to resume download; temporary file was invalid.");
    }

    private async Task CopyResponseToTempFileAsync(
        HttpResponseMessage response,
        string tempPath,
        long existingBytes,
        long? totalBytes,
        string queueKey,
        NexusModDownloadRequest request,
        IProgress<ModDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var fileMode = existingBytes > 0 ? FileMode.Append : FileMode.Create;
        var buffer = new byte[Math.Max(16 * 1024, options.BufferSize)];
        var downloadedBytes = existingBytes;
        var stopwatch = Stopwatch.StartNew();
        var lastReport = Stopwatch.StartNew();
        var bytesAtLastReport = downloadedBytes;

        Stream responseStream;
        try
        {
            responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            throw new DownloadTransportException("Unable to open the download response stream.", ex);
        }

        await using (responseStream)
        {
            await using var fileStream = new FileStream(
                tempPath,
                fileMode,
                FileAccess.Write,
                FileShare.None,
                buffer.Length,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            while (true)
            {
                int read;
                using (var inactivityCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    inactivityCancellation.CancelAfter(options.EffectiveDownloadInactivityTimeout);
                    try
                    {
                        read = await responseStream.ReadAsync(buffer, inactivityCancellation.Token);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new TimeoutException(
                            $"No download data was received for {options.EffectiveDownloadInactivityTimeout.TotalSeconds:0.#} seconds.");
                    }
                    catch (Exception ex) when (ex is IOException or HttpRequestException)
                    {
                        throw new DownloadTransportException("The download connection closed before the file was complete.", ex);
                    }
                }

                if (read == 0)
                {
                    break;
                }

                await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                downloadedBytes += read;

                if (lastReport.ElapsedMilliseconds < 250)
                {
                    continue;
                }

                var seconds = Math.Max(0.001, lastReport.Elapsed.TotalSeconds);
                var speed = (downloadedBytes - bytesAtLastReport) / seconds;
                ReportProgress(
                    progress,
                    queueKey,
                    request,
                    downloadedBytes,
                    totalBytes,
                    CalculatePercent(downloadedBytes, totalBytes),
                    speed,
                    ModDownloadStatus.Downloading);
                bytesAtLastReport = downloadedBytes;
                lastReport.Restart();
            }
        }

        var averageSpeed = downloadedBytes / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds);
        ReportProgress(
            progress,
            queueKey,
            request,
            downloadedBytes,
            totalBytes,
            CalculatePercent(downloadedBytes, totalBytes),
            averageSpeed,
            ModDownloadStatus.Downloading);
    }

    private async Task<NexusCookieAuthLease?> CreateCookieAuthLeaseIfAllowedAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.RequestUri is null || !NexusCookieAuthService.IsNexusModsHost(request.RequestUri.Host))
        {
            return null;
        }

        NexusCookieAuthService.AssertNexusModsUri(request.RequestUri);
        var cookieLease = await cookieAuthService.TryCreateCookieHeaderLeaseAsync(request.RequestUri, cancellationToken);
        if (cookieLease is null)
        {
            return null;
        }

        return cookieLease;
    }

    private async Task<HttpResponseMessage> SendWithRateLimitRetryAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var currentRequest = request;
        for (var redirect = 0; redirect <= MaxRedirects; redirect++)
        {
            for (var attempt = 0; attempt < 4; attempt++)
            {
                using var attemptRequest = CloneRequest(currentRequest);
                HttpResponseMessage response;
                using (var cookieLease = await CreateCookieAuthLeaseIfAllowedAsync(attemptRequest, cancellationToken))
                {
                    ApplyCookieHeaderToSendAttempt(attemptRequest, cookieLease);
                    response = await httpClient.SendAsync(
                        attemptRequest,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken);
                }

                if (response.StatusCode == (HttpStatusCode)429 && attempt < 3)
                {
                    var delay = ResolveRetryDelay(response, attempt);
                    response.Dispose();
                    await Task.Delay(delay, cancellationToken);
                    continue;
                }

                if (!IsRedirectStatus(response.StatusCode))
                {
                    return response;
                }

                var redirectUri = ResolveRedirectUri(currentRequest.RequestUri, response.Headers.Location);
                if (redirectUri is null)
                {
                    return response;
                }

                response.Dispose();
                currentRequest = CreateRedirectRequest(currentRequest, redirectUri);
                break;
            }

            if (redirect == MaxRedirects)
            {
                throw new InvalidOperationException("Download redirect limit exceeded.");
            }
        }

        throw new InvalidOperationException("Unexpected download redirect flow.");
    }

    private static bool IsRedirectStatus(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.Moved
            or HttpStatusCode.Redirect
            or HttpStatusCode.RedirectMethod
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;
    }

    private static Uri? ResolveRedirectUri(Uri? requestUri, Uri? location)
    {
        if (location is null)
        {
            return null;
        }

        if (location.IsAbsoluteUri)
        {
            return location;
        }

        return requestUri is null ? null : new Uri(requestUri, location);
    }

    private static HttpRequestMessage CreateRedirectRequest(HttpRequestMessage previousRequest, Uri redirectUri)
    {
        var redirectRequest = new HttpRequestMessage(HttpMethod.Get, redirectUri)
        {
            Version = previousRequest.Version,
            VersionPolicy = previousRequest.VersionPolicy
        };

        foreach (var header in previousRequest.Headers)
        {
            if (string.Equals(header.Key, "Cookie", StringComparison.OrdinalIgnoreCase)
                || string.Equals(header.Key, "Host", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            redirectRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return redirectRequest;
    }

    private static void ApplyCookieHeaderToSendAttempt(
        HttpRequestMessage request,
        NexusCookieAuthLease? cookieLease)
    {
        if (cookieLease is null)
        {
            return;
        }

        if (request.RequestUri is null)
        {
            throw new InvalidOperationException("Refusing to attach Nexus browser Cookie auth to a request without a URI.");
        }

        NexusCookieAuthService.AssertNexusModsUri(request.RequestUri);
        request.Headers.TryAddWithoutValidation("Cookie", cookieLease.RevealHeaderValue());
    }

    private static async Task<bool> IsExistingDownloadUsableAsync(
        string filePath,
        NexusModDownloadRequest request,
        CancellationToken cancellationToken)
    {
        if (IsBlockedHtmlDownload(filePath))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(request.ExpectedMd5))
        {
            return await VerifyMd5Async(filePath, request.ExpectedMd5, cancellationToken);
        }

        return !IsArchiveFileName(request.FileName) || IsReadableArchive(filePath);
    }

    private static bool IsArchiveFileName(string fileName)
    {
        return Path.GetExtension(fileName).ToLowerInvariant() is ".zip" or ".7z" or ".rar";
    }

    private static bool IsReadableArchive(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            using var archive = ArchiveFactory.OpenArchive(stream);
            var entryCount = archive.Entries.Count();
            return true;
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or InvalidFormatException
                or ArchiveOperationException
                or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsBlockedHtmlDownload(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return false;
            }

            var length = new FileInfo(filePath).Length;
            if (length <= 0)
            {
                return false;
            }

            var buffer = new byte[Math.Min(1024, (int)Math.Min(int.MaxValue, length))];
            using var stream = File.OpenRead(filePath);
            var read = stream.Read(buffer, 0, buffer.Length);
            var text = Encoding.UTF8.GetString(buffer, 0, read).TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
            return text.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("<html", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("window._cf_chl_opt", StringComparison.OrdinalIgnoreCase)
                || text.Contains("challenges.cloudflare.com", StringComparison.OrdinalIgnoreCase)
                || text.Contains("cloudflare", StringComparison.OrdinalIgnoreCase)
                || text.Contains("sign in", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static HttpRequestMessage CloneRequest(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version,
            VersionPolicy = request.VersionPolicy
        };

        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return clone;
    }

    private static TimeSpan ResolveRetryDelay(HttpResponseMessage response, int attempt)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
        {
            return delta;
        }

        if (response.Headers.RetryAfter?.Date is { } date)
        {
            var delay = date - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                return delay;
            }
        }

        return TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt + 1)));
    }

    private async Task<ModDownloadResult> CompleteDownloadAsync(
        string queueKey,
        NexusModDownloadRequest request,
        string tempPath,
        string finalPath,
        IProgress<ModDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(request.ExpectedMd5)
                && !await VerifyMd5Async(tempPath, request.ExpectedMd5, cancellationToken))
            {
                UpdateQueueItem(request, ModDownloadStatus.ChecksumFailed, "MD5 checksum mismatch.");
                ReportProgress(progress, queueKey, request, 0, null, null, 0, ModDownloadStatus.ChecksumFailed);
                return new ModDownloadResult(false, ModDownloadStatus.ChecksumFailed, request, null, "MD5 checksum mismatch.");
            }

            if (IsBlockedHtmlDownload(tempPath))
            {
                TryDeleteFile(tempPath);
                return Fail(
                    request,
                    ModDownloadStatus.Failed,
                    "Nexus returned a browser challenge or sign-in page instead of the mod archive. Sign in to Nexus Mods in BohemiX and retry.");
            }

            File.Move(tempPath, finalPath, overwrite: true);
            var finalSize = GetTempFileLength(finalPath);
            UpdateQueueItem(request, ModDownloadStatus.Completed);
            ReportProgress(progress, queueKey, request, finalSize, finalSize, 100, 0, ModDownloadStatus.Completed);
            logger.Information(
                "Completed mod file download for {ModId}/{FileId}: {Bytes} bytes.",
                request.ModId,
                request.FileId,
                finalSize);
            return new ModDownloadResult(true, ModDownloadStatus.Completed, request, finalPath, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(request, ModDownloadStatus.Failed, $"Unable to finalize download: {ex.Message}", ex);
        }
    }

    private static long? ResolveTotalBytes(HttpResponseMessage response, long existingBytes)
    {
        if (response.Content.Headers.ContentRange?.Length is { } contentRangeLength)
        {
            return contentRangeLength;
        }

        if (response.Content.Headers.ContentLength is { } contentLength)
        {
            return existingBytes + contentLength;
        }

        return null;
    }

    private static double? CalculatePercent(long downloadedBytes, long? totalBytes)
    {
        if (totalBytes is null or <= 0)
        {
            return null;
        }

        return Math.Clamp(downloadedBytes * 100d / totalBytes.Value, 0, 100);
    }

    private static async Task<bool> VerifyMd5Async(
        string filePath,
        string expectedMd5,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        using var md5 = MD5.Create();
        var hash = await md5.ComputeHashAsync(stream, cancellationToken);
        var actual = Convert.ToHexString(hash).ToLowerInvariant();
        return string.Equals(actual, NormalizeMd5(expectedMd5), StringComparison.OrdinalIgnoreCase);
    }

    private static long GetTempFileLength(string tempPath)
    {
        try
        {
            return File.Exists(tempPath) ? new FileInfo(tempPath).Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private ModDownloadQueueItem AddOrUpdateQueueItem(
        NexusModDownloadRequest request,
        ModDownloadStatus status,
        string? errorMessage = null)
    {
        var queueKey = BuildQueueKey(request);
        return queue.AddOrUpdate(
            queueKey,
            _ => new ModDownloadQueueItem(queueKey, request, status, errorMessage),
            (_, _) => new ModDownloadQueueItem(queueKey, request, status, errorMessage));
    }

    private void UpdateQueueItem(
        NexusModDownloadRequest request,
        ModDownloadStatus status,
        string? errorMessage = null)
    {
        AddOrUpdateQueueItem(request, status, errorMessage);
    }

    private QueueExecutionState GetExecutionState(string queueKey)
    {
        return executionStates.GetOrAdd(queueKey, _ => new QueueExecutionState());
    }

    private bool TryGetRequestedAction(string queueKey, out QueueAction action)
    {
        if (executionStates.TryGetValue(queueKey, out var executionState))
        {
            lock (executionState.SyncRoot)
            {
                action = executionState.RequestedAction;
                return true;
            }
        }

        action = QueueAction.None;
        return false;
    }

    private ModDownloadResult HandleControlledCancellation(string queueKey, NexusModDownloadRequest request)
    {
        var executionState = GetExecutionState(queueKey);
        lock (executionState.SyncRoot)
        {
            return executionState.RequestedAction switch
            {
                QueueAction.Pause => new ModDownloadResult(false, ModDownloadStatus.Paused, request, null, "Download paused."),
                QueueAction.Cancel => new ModDownloadResult(false, ModDownloadStatus.Canceled, request, null, "Download canceled."),
                _ => throw new OperationCanceledException()
            };
        }
    }

    private bool TryHandleQueuedCancellation(string queueKey, NexusModDownloadRequest request, string tempPath)
    {
        var executionState = GetExecutionState(queueKey);
        lock (executionState.SyncRoot)
        {
            switch (executionState.RequestedAction)
            {
                case QueueAction.Pause:
                    UpdateQueueItem(request, ModDownloadStatus.Paused, "Download paused.");
                    return true;
                case QueueAction.Cancel:
                    TryDeleteFile(tempPath);
                    UpdateQueueItem(request, ModDownloadStatus.Canceled, "Download canceled.");
                    return true;
                default:
                    return false;
            }
        }
    }

    private ModDownloadResult Fail(
        NexusModDownloadRequest request,
        ModDownloadStatus status,
        string errorMessage,
        Exception? exception = null)
    {
        if (exception is null)
        {
            logger.Warning("Mod download failed for {ModId}/{FileId}: {ErrorMessage}", request.ModId, request.FileId, errorMessage);
        }
        else
        {
            logger.Warning(exception, "Mod download failed for {ModId}/{FileId}: {ErrorMessage}", request.ModId, request.FileId, errorMessage);
        }

        UpdateQueueItem(request, status, errorMessage);
        return new ModDownloadResult(false, status, request, null, errorMessage);
    }

    private static void ReportProgress(
        IProgress<ModDownloadProgress>? progress,
        string queueKey,
        NexusModDownloadRequest request,
        long bytesDownloaded,
        long? totalBytes,
        double? percent,
        double bytesPerSecond,
        ModDownloadStatus status)
    {
        progress?.Report(new ModDownloadProgress(
            queueKey,
            request.ModId,
            request.FileId,
            request.FileName,
            bytesDownloaded,
            totalBytes,
            percent,
            bytesPerSecond,
            status));
    }

    private static IReadOnlySet<string> CreateInstalledLookup(IEnumerable<ModManifest> installedMods)
    {
        var lookup = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in installedMods)
        {
            AddInstalledKey(lookup, mod.Id);
            AddInstalledKey(lookup, mod.DisplayName);
        }

        return lookup;
    }

    private static void AddInstalledKey(HashSet<string> lookup, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var normalized = NormalizeKey(value);
        lookup.Add(normalized);
        lookup.Add(normalized.Replace("nexus-", string.Empty, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsInstalled(int modId, IReadOnlySet<string> installedKeys)
    {
        return installedKeys.Contains(modId.ToString())
            || installedKeys.Contains($"nexus-{modId}")
            || installedKeys.Contains($"nexusmods-{modId}");
    }

    private static string BuildQueueKey(NexusModDownloadRequest request)
    {
        var environmentKey = request.GameEnvironmentId?.ToString("N") ?? "unscoped";
        return $"{environmentKey}:{request.GameDomainName}:{request.ModId}:{request.FileId}";
    }

    private Guid? CaptureCurrentEnvironmentId()
    {
        if (applicationPathService is null)
        {
            return null;
        }

        return applicationPathService.GetPaths().GameEnvironmentId;
    }

    private NexusModDownloadRequest BindRequestToCurrentEnvironment(NexusModDownloadRequest request)
    {
        var currentEnvironmentId = CaptureCurrentEnvironmentId();
        if (currentEnvironmentId is null)
        {
            return request;
        }

        if (request.GameEnvironmentId is { } requestEnvironmentId && requestEnvironmentId != currentEnvironmentId)
        {
            throw new InvalidOperationException("The mod download belongs to a different game environment.");
        }

        return request.GameEnvironmentId == currentEnvironmentId
            ? request
            : request with { GameEnvironmentId = currentEnvironmentId };
    }

    private bool TryGetCurrentEnvironmentQueueItem(string queueKey, out ModDownloadQueueItem item)
    {
        if (!queue.TryGetValue(queueKey, out item!))
        {
            return false;
        }

        if (applicationPathService is null)
        {
            return true;
        }

        return item.Request.GameEnvironmentId == applicationPathService.GetPaths().GameEnvironmentId;
    }

    private static string SanitizeFileName(string fileName)
    {
        var sanitized = string.IsNullOrWhiteSpace(fileName)
            ? "mod-download.zip"
            : fileName.Trim();

        foreach (var invalidChar in Path.GetInvalidFileNameChars())
        {
            sanitized = sanitized.Replace(invalidChar, '_');
        }

        return sanitized;
    }

    private static string NormalizeKey(string value)
    {
        return value.Trim().ToLowerInvariant();
    }

    private static string NormalizeMd5(string value)
    {
        return value.Trim().Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed class LegacyPlayerEnvironmentPathService(IPlayerContext playerContext) : IApplicationPathService
    {
        public ApplicationPaths GetPaths()
        {
            var environmentId = playerContext.CurrentPlayer?.Id
                ?? throw new InvalidOperationException("A current player is required by the legacy test adapter.");
            return new ApplicationPaths(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty,
                string.Empty, string.Empty, string.Empty, environmentId);
        }

        public GlobalApplicationPaths GetGlobalPaths() => throw new NotSupportedException();
    }
}
