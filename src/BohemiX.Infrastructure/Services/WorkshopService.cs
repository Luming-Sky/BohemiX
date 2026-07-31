using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using BohemiX.Core.Models;
using BohemiX.Core.PlayerProfiles;
using BohemiX.Core.Services;
using Serilog;
using Steamworks;
using Steamworks.Data;

namespace BohemiX.Infrastructure.Services;

public sealed class WorkshopService : IWorkshopService, IDisposable
{
    private const string MappingFileName = "workshop_mapping.json";
    private const string ThumbnailCacheFileName = "workshop_thumbnail_cache.json";
    private const uint ItemStateSubscribed = 1;
    private const uint ItemStateInstalled = 4;
    private const uint ItemStateDownloading = 16;
    private const uint ItemStateDownloadPending = 32;

    private readonly IApplicationPathService applicationPathService;
    private readonly WorkshopOptions options;
    private readonly ILogger logger;
    private readonly SteamClientStartupCoordinator steamClientStartupCoordinator = new();
    private readonly SemaphoreSlim initializationLock = new(1, 1);
    private readonly SemaphoreSlim thumbnailCacheLock = new(1, 1);
    private readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private object? steamUgcInternal;
    private Type? steamUgcInternalType;
    private Type? ugcQueryType;
    private Type? itemStatisticType;
    private Type? queryCompletedType;
    private Type? steamUgcDetailsType;
    private bool steamClientInitialized;
    private bool initialized;
    private CancellationTokenSource? callbackPumpCancellation;
    private Task? callbackPumpTask;
    private Dictionary<ulong, ThumbnailCacheEntry>? thumbnailCache;
    private string? thumbnailCachePath;

    public WorkshopService(
        IApplicationPathService applicationPathService,
        WorkshopOptions options,
        ILogger logger)
    {
        this.applicationPathService = applicationPathService;
        this.options = options;
        this.logger = logger.ForContext<WorkshopService>();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await initializationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (initialized)
            {
                return;
            }

            EnsureSteamClientReady();

            var kcd2AppId = (AppId)options.Kcd2AppId;
            if (!SteamApps.IsSubscribedToApp(kcd2AppId))
            {
                throw new WorkshopException(
                    "The current Steam account does not own Kingdom Come: Deliverance II.",
                    WorkshopFailureKind.GameNotOwned);
            }

            InitializeReflectionBindings();
            callbackPumpCancellation = new CancellationTokenSource();
            callbackPumpTask = Task.Run(() => RunCallbackPumpAsync(callbackPumpCancellation.Token), CancellationToken.None);
            initialized = true;
        }
        finally
        {
            initializationLock.Release();
        }
    }

    public async Task<SteamAccountIdentity> GetCurrentSteamAccountAsync(
        IProgress<SteamAccountDetectionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var activeAccount = await steamClientStartupCoordinator
            .EnsureActiveAccountAsync(progress, cancellationToken)
            .ConfigureAwait(false);

        await initializationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                EnsureSteamClientReady();
            }
            catch (WorkshopException ex) when (
                ex.FailureKind == WorkshopFailureKind.GameNotOwned)
            {
                logger.Information(
                    "Steam account {SteamId} was identified locally after KCD2 ownership validation was denied",
                    activeAccount.SteamId);
                return new SteamAccountIdentity(activeAccount.SteamId, activeAccount.PersonaName, OwnsKcd2: false);
            }

            return new SteamAccountIdentity(
                SteamClient.SteamId.Value,
                SteamClient.Name,
                SteamApps.IsSubscribedToApp((AppId)options.Kcd2AppId));
        }
        finally
        {
            initializationLock.Release();
        }
    }

    public async Task<WorkshopSearchResult> SearchModsAsync(
        WorkshopSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, Math.Max(1, options.PageSize));
        var handle = CreateAllUgcQueryHandle(request.SortOrder, page);

        try
        {
            SetQueryReturnOptions(handle);

            if (!string.IsNullOrWhiteSpace(request.Query))
            {
                InvokeInternal("SetSearchText", handle, request.Query.Trim());
            }

            InvokeInternal("SetAllowCachedResponse", handle, 60u);

            var queryResult = await SendQueryAsync(handle, cancellationToken).ConfigureAwait(false);
            var totalCount = Convert.ToInt32(GetFieldValue(queryResult, "TotalMatchingResults"));
            var returnedCount = Convert.ToInt32(GetFieldValue(queryResult, "NumResultsReturned"));
            var mods = new List<WorkshopModInfo>(Math.Min(pageSize, returnedCount));

            for (var i = 0; i < returnedCount && mods.Count < pageSize; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var details = GetQueryDetails(handle, (uint)i);
                if (details is null)
                {
                    continue;
                }

                mods.Add(await MapDetailsAsync(handle, (uint)i, details, cancellationToken).ConfigureAwait(false));
            }

            return new WorkshopSearchResult(
                WorkshopServiceUtilities.SortSearchResults(mods, request.SortOrder),
                totalCount,
                page,
                pageSize);
        }
        finally
        {
            ReleaseQuery(handle);
        }
    }

    public async Task<WorkshopModInfo> GetModInfoAsync(
        ulong publishedFileId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var handle = CreateDetailsQueryHandle([CreatePublishedFileId(publishedFileId)]);
        try
        {
            SetQueryReturnOptions(handle);
            var queryResult = await SendQueryAsync(handle, cancellationToken).ConfigureAwait(false);
            var returnedCount = Convert.ToInt32(GetFieldValue(queryResult, "NumResultsReturned"));
            if (returnedCount <= 0)
            {
                throw new WorkshopException(
                    $"Steam Workshop did not return item {publishedFileId}.",
                    WorkshopFailureKind.QueryFailed);
            }

            var details = GetQueryDetails(handle, 0)
                ?? throw new WorkshopException(
                    $"Steam Workshop returned empty details for item {publishedFileId}.",
                    WorkshopFailureKind.QueryFailed);
            return await MapDetailsAsync(handle, 0, details, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ReleaseQuery(handle);
        }
    }

    public async Task<WorkshopCollectionInfo> GetCollectionInfoAsync(
        ulong collectionId,
        CancellationToken cancellationToken = default)
    {
        if (collectionId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(collectionId));
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var handle = CreateDetailsQueryHandle([CreatePublishedFileId(collectionId)]);
        try
        {
            SetQueryReturnOptions(handle);
            var queryResult = await SendQueryAsync(handle, cancellationToken).ConfigureAwait(false);
            var returnedCount = Convert.ToInt32(GetFieldValue(queryResult, "NumResultsReturned"));
            if (returnedCount <= 0)
            {
                throw new WorkshopException(
                    $"Steam Workshop did not return collection {collectionId}.",
                    WorkshopFailureKind.QueryFailed);
            }

            var details = GetQueryDetails(handle, 0)
                ?? throw new WorkshopException(
                    $"Steam Workshop returned empty details for collection {collectionId}.",
                    WorkshopFailureKind.QueryFailed);
            var fileType = Convert.ToString(GetFieldValue(details, "FileType"));
            if (!string.Equals(fileType, "Collection", StringComparison.OrdinalIgnoreCase))
            {
                throw new WorkshopException(
                    $"Steam Workshop item {collectionId} is not a collection.",
                    WorkshopFailureKind.QueryFailed);
            }

            var name = InvokeUtf8(details, "TitleUTF8");
            var description = InvokeUtf8(details, "DescriptionUTF8");
            var children = GetQueryChildren(handle, 0, Convert.ToUInt32(GetFieldValue(details, "NumChildren")));
            var thumbnailUrl = await GetThumbnailUrlAsync(handle, 0, collectionId, cancellationToken).ConfigureAwait(false);

            return new WorkshopCollectionInfo(
                collectionId,
                string.IsNullOrWhiteSpace(name) ? $"Workshop collection {collectionId}" : name,
                WorkshopServiceUtilities.CreateSummary(description),
                Convert.ToString(GetFieldValue(details, "SteamIDOwner")) ?? string.Empty,
                thumbnailUrl,
                FromUnixTime(Convert.ToUInt32(GetFieldValue(details, "TimeUpdated"))),
                children);
        }
        finally
        {
            ReleaseQuery(handle);
        }
    }

    public async Task<WorkshopInstallResult> SubscribeAndInstallAsync(
        WorkshopInstallRequest request,
        IProgress<WorkshopInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.ModsDirectory))
        {
            throw new WorkshopException("A target KCD2 Mods directory is required.", WorkshopFailureKind.DeploymentFailed);
        }

        Directory.CreateDirectory(request.ModsDirectory);
        var processed = new HashSet<ulong>();
        var installedDependencies = new List<ulong>();
        return await SubscribeAndInstallCoreAsync(
            request,
            progress,
            processed,
            installedDependencies,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<WorkshopCollectionInstallResult> InstallCollectionAsync(
        WorkshopCollectionInstallRequest request,
        IProgress<WorkshopCollectionInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (request.CollectionId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request.CollectionId));
        }

        if (string.IsNullOrWhiteSpace(request.ModsDirectory))
        {
            throw new WorkshopException("A target KCD2 Mods directory is required.", WorkshopFailureKind.DeploymentFailed);
        }

        var collection = await GetCollectionInfoAsync(request.CollectionId, cancellationToken).ConfigureAwait(false);
        return await WorkshopCollectionInstallCoordinator.InstallAsync(
            collection,
            request,
            SubscribeAndInstallAsync,
            progress,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<WorkshopInstallResult> SubscribeAndInstallCoreAsync(
        WorkshopInstallRequest request,
        IProgress<WorkshopInstallProgress>? progress,
        HashSet<ulong> processed,
        List<ulong> installedDependencies,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        if (!processed.Add(request.PublishedFileId))
        {
            return new WorkshopInstallResult(
                true,
                request.PublishedFileId,
                null,
                null,
                installedDependencies,
                "Workshop dependency was already processed.");
        }

        progress?.Report(new WorkshopInstallProgress(
            request.PublishedFileId,
            WorkshopInstallStage.ResolvingDependencies,
            "Resolving Steam Workshop dependencies."));

        var mod = await GetModInfoAsync(request.PublishedFileId, cancellationToken).ConfigureAwait(false);
        if (request.IncludeDependencies)
        {
            foreach (var dependencyId in mod.DependencyPublishedFileIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await IsWorkshopItemMappedAsync(request.ModsDirectory, dependencyId, cancellationToken).ConfigureAwait(false))
                {
                    if (!installedDependencies.Contains(dependencyId))
                    {
                        installedDependencies.Add(dependencyId);
                    }

                    continue;
                }

                var dependencyRequest = request with { PublishedFileId = dependencyId };
                var dependencyResult = await SubscribeAndInstallCoreAsync(
                    dependencyRequest,
                    progress,
                    processed,
                    installedDependencies,
                    cancellationToken).ConfigureAwait(false);

                if (dependencyResult.Success && !installedDependencies.Contains(dependencyId))
                {
                    installedDependencies.Add(dependencyId);
                }
            }
        }

        progress?.Report(new WorkshopInstallProgress(
            request.PublishedFileId,
            WorkshopInstallStage.Subscribing,
            $"Subscribing to {mod.Name}."));

        await SubscribeItemAsync(request.PublishedFileId, cancellationToken).ConfigureAwait(false);

        progress?.Report(new WorkshopInstallProgress(
            request.PublishedFileId,
            WorkshopInstallStage.WaitingForSteamDownload,
            "Waiting for Steam to finish the local Workshop download."));

        var workshopPath = await WaitForStableWorkshopDirectoryAsync(
            request.PublishedFileId,
            cancellationToken).ConfigureAwait(false);

        progress?.Report(new WorkshopInstallProgress(
            request.PublishedFileId,
            WorkshopInstallStage.Deploying,
            "Deploying Workshop content to the KCD2 Mods directory."));

        var installedPath = await DeployWorkshopDirectoryAsync(
            mod,
            workshopPath,
            request.ModsDirectory,
            request.DeployMode,
            cancellationToken).ConfigureAwait(false);

        await SaveMappingAsync(
            request.ModsDirectory,
            new WorkshopMappingEntry(
                request.PublishedFileId,
                mod.Name,
                workshopPath,
                installedPath,
                DateTimeOffset.UtcNow,
                mod.DependencyPublishedFileIds),
            cancellationToken).ConfigureAwait(false);

        progress?.Report(new WorkshopInstallProgress(
            request.PublishedFileId,
            WorkshopInstallStage.Completed,
            $"Installed {mod.Name}."));

        return new WorkshopInstallResult(
            true,
            request.PublishedFileId,
            workshopPath,
            installedPath,
            installedDependencies,
            $"Installed {mod.Name} from Steam Workshop.");
    }

    private async Task<bool> IsWorkshopItemMappedAsync(
        string modsDirectory,
        ulong publishedFileId,
        CancellationToken cancellationToken)
    {
        var mappings = await LoadMappingsAsync(modsDirectory, cancellationToken).ConfigureAwait(false);
        return mappings.TryGetValue(publishedFileId, out var mapping)
            && !string.IsNullOrWhiteSpace(mapping.InstalledPath)
            && Directory.Exists(mapping.InstalledPath)
            && WorkshopServiceUtilities.IsPathInsideDirectory(modsDirectory, mapping.InstalledPath);
    }

    private void InitializeReflectionBindings()
    {
        var steamworksAssembly = typeof(SteamUGC).Assembly;
        steamUgcInternal = typeof(SteamUGC)
            .GetProperty("Internal", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            ?.GetValue(null);
        steamUgcInternalType = steamUgcInternal?.GetType();
        ugcQueryType = steamworksAssembly.GetType("Steamworks.UGCQuery");
        itemStatisticType = steamworksAssembly.GetType("Steamworks.ItemStatistic");
        queryCompletedType = steamworksAssembly.GetType("Steamworks.Data.SteamUGCQueryCompleted_t");
        steamUgcDetailsType = steamworksAssembly.GetType("Steamworks.Data.SteamUGCDetails_t");

        if (steamUgcInternal is null
            || steamUgcInternalType is null
            || ugcQueryType is null
            || itemStatisticType is null
            || queryCompletedType is null
            || steamUgcDetailsType is null
            || !(bool)(steamUgcInternalType.GetProperty("IsValid")?.GetValue(steamUgcInternal) ?? false))
        {
            throw new WorkshopException(
                "Steam UGC interface is not available.",
                WorkshopFailureKind.SteamUnavailable);
        }
    }

    private void EnsureSteamClientReady()
    {
        if (!steamClientInitialized)
        {
            if (!SteamLoginAccountReader.IsSteamClientRunning())
            {
                throw new WorkshopException(
                    "The Steam client is not running. Start Steam and sign in before using Steam Workshop.",
                    WorkshopFailureKind.SteamUnavailable);
            }

            try
            {
                SteamClient.Init(options.Kcd2AppId, asyncCallbacks: true);
                steamClientInitialized = true;
            }
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or InvalidOperationException)
            {
                throw WorkshopServiceUtilities.CreateSteamInitializationException(ex, options.Kcd2AppId);
            }
            catch (Exception ex)
            {
                throw WorkshopServiceUtilities.CreateSteamInitializationException(ex, options.Kcd2AppId);
            }
        }

        if (!SteamClient.IsValid)
        {
            throw new WorkshopException(
                "Steamworks initialized, but the Steam client interface is not valid.",
                WorkshopFailureKind.SteamUnavailable);
        }

        if (!SteamClient.IsLoggedOn)
        {
            throw new WorkshopException(
                "Steam is running, but the current user is not logged in.",
                WorkshopFailureKind.NotLoggedIn);
        }
    }

    private object CreateAllUgcQueryHandle(WorkshopSortOrder sortOrder, int page)
    {
        var queryValue = CreateUgcQueryValue(sortOrder);
        return InvokeInternal(
            "CreateQueryAllUGCRequest",
            parameters => parameters.Length == 5 && parameters[4].ParameterType == typeof(uint),
            queryValue,
            UgcType.Items,
            (AppId)options.Kcd2AppId,
            (AppId)options.Kcd2AppId,
            (uint)page);
    }

    private object CreateDetailsQueryHandle(PublishedFileId[] ids)
    {
        return InvokeInternal("CreateQueryUGCDetailsRequest", ids, (uint)ids.Length);
    }

    private object CreateUgcQueryValue(WorkshopSortOrder sortOrder)
    {
        var enumName = sortOrder switch
        {
            // Facepunch.Steamworks 2.3.3 exposes no Steam UGC query enum for true last-updated sorting.
            // We ask Steam for the closest recency-oriented page, then sort mapped results by TimeUpdated locally.
            WorkshopSortOrder.Updated => "RankedByPublicationDate",
            _ => "RankedByTrend"
        };

        return Enum.Parse(ugcQueryType!, enumName);
    }

    private void SetQueryReturnOptions(object handle)
    {
        InvokeInternal("SetReturnLongDescription", handle, true);
        InvokeInternal("SetReturnMetadata", handle, true);
        InvokeInternal("SetReturnChildren", handle, true);
    }

    private async Task<object> SendQueryAsync(object handle, CancellationToken cancellationToken)
    {
        var callResult = InvokeInternal("SendQueryUGCRequest", handle);
        var result = await AwaitCallResultAsync(callResult, cancellationToken).ConfigureAwait(false)
            ?? throw new WorkshopException("Steam Workshop query returned no result.", WorkshopFailureKind.QueryFailed);
        EnsureSteamResultOk(result, "Steam Workshop query failed.", WorkshopFailureKind.QueryFailed);
        return result;
    }

    private object? GetQueryDetails(object handle, uint index)
    {
        var details = Activator.CreateInstance(steamUgcDetailsType!);
        var args = new[] { handle, index, details };
        var success = (bool)InvokeInternal("GetQueryUGCResult", args);
        return success ? args[2] : null;
    }

    private async Task<WorkshopModInfo> MapDetailsAsync(
        object handle,
        uint index,
        object details,
        CancellationToken cancellationToken)
    {
        var publishedFileId = GetPublishedFileId(details);
        var name = InvokeUtf8(details, "TitleUTF8");
        var description = InvokeUtf8(details, "DescriptionUTF8");
        var children = GetQueryChildren(handle, index, Convert.ToUInt32(GetFieldValue(details, "NumChildren")));
        var thumbnailUrl = await GetThumbnailUrlAsync(handle, index, publishedFileId, cancellationToken).ConfigureAwait(false);
        var subscriptions = GetQueryStatistic(handle, index, "NumSubscriptions");

        return new WorkshopModInfo(
            publishedFileId,
            string.IsNullOrWhiteSpace(name) ? $"Workshop {publishedFileId}" : name,
            WorkshopServiceUtilities.CreateSummary(description),
            description,
            Convert.ToString(GetFieldValue(details, "SteamIDOwner")) ?? string.Empty,
            Convert.ToInt64(GetFieldValue(details, "FileSize")),
            thumbnailUrl,
            FromUnixTime(Convert.ToUInt32(GetFieldValue(details, "TimeCreated"))),
            FromUnixTime(Convert.ToUInt32(GetFieldValue(details, "TimeUpdated"))),
            subscriptions,
            Convert.ToUInt32(GetFieldValue(details, "VotesUp")),
            Convert.ToUInt32(GetFieldValue(details, "VotesDown")),
            Convert.ToSingle(GetFieldValue(details, "Score")),
            children);
    }

    private IReadOnlyList<ulong> GetQueryChildren(object handle, uint index, uint childCount)
    {
        if (childCount == 0)
        {
            return [];
        }

        var children = new PublishedFileId[childCount];
        var success = (bool)InvokeInternal("GetQueryUGCChildren", handle, index, children, childCount);
        if (!success)
        {
            return [];
        }

        return children
            .Select(child => (ulong)child)
            .Where(id => id > 0)
            .Distinct()
            .ToArray();
    }

    private ulong GetQueryStatistic(object handle, uint index, string statisticName)
    {
        var statistic = Enum.Parse(itemStatisticType!, statisticName);
        object? value = 0UL;
        var args = new[] { handle, index, statistic, value };
        var success = (bool)InvokeInternal("GetQueryUGCStatistic", args);
        return success ? Convert.ToUInt64(args[3]) : 0;
    }

    private async Task<string?> GetThumbnailUrlAsync(
        object handle,
        uint index,
        ulong publishedFileId,
        CancellationToken cancellationToken)
    {
        var cachedUrl = await TryGetCachedThumbnailUrlAsync(publishedFileId, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(cachedUrl))
        {
            return cachedUrl;
        }

        object? previewUrl = string.Empty;
        var args = new[] { handle, index, previewUrl };
        var success = (bool)InvokeInternal("GetQueryUGCPreviewURL", args);
        var url = success ? args[2]?.ToString() : null;
        if (!string.IsNullOrWhiteSpace(url))
        {
            await CacheThumbnailUrlAsync(publishedFileId, url, cancellationToken).ConfigureAwait(false);
        }

        return url;
    }

    private async Task SubscribeItemAsync(ulong publishedFileId, CancellationToken cancellationToken)
    {
        var itemId = CreatePublishedFileId(publishedFileId);
        var state = GetItemState(itemId);
        if ((state & ItemStateSubscribed) != 0)
        {
            SteamUGC.Download(itemId, highPriority: true);
            return;
        }

        var callResult = InvokeInternal("SubscribeItem", itemId);
        var result = await AwaitCallResultAsync(callResult, cancellationToken).ConfigureAwait(false)
            ?? throw new WorkshopException(
                $"Steam did not return a subscription result for Workshop item {publishedFileId}.",
                WorkshopFailureKind.SubscriptionFailed);
        EnsureSteamResultOk(
            result,
            $"Unable to subscribe to Workshop item {publishedFileId}.",
            WorkshopFailureKind.SubscriptionFailed);
        SteamUGC.Download(itemId, highPriority: true);
    }

    private async Task<string> WaitForStableWorkshopDirectoryAsync(
        ulong publishedFileId,
        CancellationToken cancellationToken)
    {
        var itemId = CreatePublishedFileId(publishedFileId);
        var timeout = TimeSpan.FromMinutes(Math.Max(1, options.WorkshopInstallTimeoutMinutes));
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        WorkshopServiceUtilities.DirectorySnapshotData? previousSnapshot = null;
        var stablePolls = 0;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SteamClient.RunCallbacks();

            var installInfoPath = TryGetInstalledWorkshopPath(itemId);
            var candidatePath = installInfoPath ?? TryGetFallbackWorkshopPath(publishedFileId);
            if (!string.IsNullOrWhiteSpace(candidatePath) && Directory.Exists(candidatePath))
            {
                var snapshot = WorkshopServiceUtilities.TryCreateDirectorySnapshot(candidatePath);
                if (snapshot is { FileCount: > 0 })
                {
                    stablePolls = snapshot.Equals(previousSnapshot) ? stablePolls + 1 : 1;
                    previousSnapshot = snapshot;

                    if (stablePolls >= Math.Max(1, options.StableDirectoryPollCount))
                    {
                        return candidatePath;
                    }
                }
            }

            var itemState = GetItemState(itemId);
            if ((itemState & (ItemStateDownloading | ItemStateDownloadPending | ItemStateInstalled)) == 0)
            {
                SteamUGC.Download(itemId, highPriority: true);
            }

            await Task.Delay(
                Math.Max(250, options.WorkshopPollIntervalMilliseconds),
                cancellationToken).ConfigureAwait(false);
        }

        throw new WorkshopException(
            $"Timed out waiting for Steam to install Workshop item {publishedFileId}.",
            WorkshopFailureKind.DownloadTimeout);
    }

    private string? TryGetInstalledWorkshopPath(PublishedFileId itemId)
    {
        object? sizeOnDisk = 0UL;
        object? folder = string.Empty;
        object? timestamp = 0u;
        var args = new[] { itemId, sizeOnDisk, folder, timestamp };
        var success = (bool)InvokeInternal("GetItemInstallInfo", args);
        return success ? args[2]?.ToString() : null;
    }

    private string? TryGetFallbackWorkshopPath(ulong publishedFileId)
    {
        var appInstallDir = SteamApps.AppInstallDir((AppId)options.Kcd2AppId);
        if (string.IsNullOrWhiteSpace(appInstallDir))
        {
            return null;
        }

        var gameDirectory = new DirectoryInfo(appInstallDir);
        var steamAppsDirectory = gameDirectory.Parent?.Parent;
        if (steamAppsDirectory is null || !string.Equals(steamAppsDirectory.Name, "steamapps", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Path.Combine(
            steamAppsDirectory.FullName,
            "workshop",
            "content",
            options.Kcd2AppId.ToString(),
            publishedFileId.ToString());
    }

    private async Task<string> DeployWorkshopDirectoryAsync(
        WorkshopModInfo mod,
        string workshopPath,
        string modsDirectory,
        WorkshopDeployMode deployMode,
        CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(modsDirectory);
            var mappings = await LoadMappingsAsync(modsDirectory, cancellationToken).ConfigureAwait(false);
            var mappedPath = mappings.TryGetValue(mod.PublishedFileId, out var existingMapping)
                ? existingMapping.InstalledPath
                : null;
            var targetPath = !string.IsNullOrWhiteSpace(mappedPath) && WorkshopServiceUtilities.IsPathInsideDirectory(modsDirectory, mappedPath)
                ? mappedPath
                : WorkshopServiceUtilities.EnsureUniqueDirectory(
                    Path.Combine(modsDirectory, WorkshopServiceUtilities.SanitizePathSegment($"steam-{mod.PublishedFileId}-{mod.Name}")));
            var stagingPath = targetPath + ".installing";

            TryDeleteDirectory(stagingPath);
            Directory.CreateDirectory(stagingPath);
            await CopyDirectoryAsync(workshopPath, stagingPath, deployMode, cancellationToken).ConfigureAwait(false);

            if (Directory.Exists(targetPath))
            {
                TryDeleteDirectory(targetPath);
            }

            Directory.Move(stagingPath, targetPath);
            return targetPath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new WorkshopException(
                $"Unable to deploy Workshop content: {ex.Message}",
                WorkshopFailureKind.DeploymentFailed,
                ex);
        }
    }

    private async Task CopyDirectoryAsync(
        string sourceDirectory,
        string targetDirectory,
        WorkshopDeployMode deployMode,
        CancellationToken cancellationToken)
    {
        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(sourceDirectory, directory);
            Directory.CreateDirectory(Path.Combine(targetDirectory, relativePath));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(sourceDirectory, file);
            var destinationPath = Path.Combine(targetDirectory, relativePath);
            var parentDirectory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(parentDirectory))
            {
                Directory.CreateDirectory(parentDirectory);
            }

            if (deployMode is WorkshopDeployMode.HardLink or WorkshopDeployMode.PreferHardLink
                && TryCreateHardLink(destinationPath, file))
            {
                continue;
            }

            if (deployMode == WorkshopDeployMode.HardLink)
            {
                throw new IOException($"Unable to create hard link for {file}.");
            }

            await using var source = new FileStream(
                file,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var destination = new FileStream(
                destinationPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SaveMappingAsync(
        string modsDirectory,
        WorkshopMappingEntry entry,
        CancellationToken cancellationToken)
    {
        var mappings = await LoadMappingsAsync(modsDirectory, cancellationToken).ConfigureAwait(false);
        mappings[entry.PublishedFileId] = entry;

        var mappingPath = Path.Combine(modsDirectory, MappingFileName);
        await WorkshopServiceUtilities.WriteJsonAtomicallyAsync(
            mappingPath,
            new WorkshopMappingDocument(mappings.Values.OrderBy(value => value.PublishedFileId).ToArray()),
            jsonOptions,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<Dictionary<ulong, WorkshopMappingEntry>> LoadMappingsAsync(
        string modsDirectory,
        CancellationToken cancellationToken)
    {
        var mappingPath = Path.Combine(modsDirectory, MappingFileName);
        if (!File.Exists(mappingPath))
        {
            return [];
        }

        try
        {
            await using var stream = new FileStream(
                mappingPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var document = await JsonSerializer.DeserializeAsync<WorkshopMappingDocument>(
                stream,
                jsonOptions,
                cancellationToken).ConfigureAwait(false);
            return document?.Items?.ToDictionary(item => item.PublishedFileId) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            logger.Warning(ex, "Unable to read Workshop mapping file {MappingPath}", mappingPath);
            return [];
        }
    }

    private async Task<string?> TryGetCachedThumbnailUrlAsync(ulong publishedFileId, CancellationToken cancellationToken)
    {
        await thumbnailCacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureThumbnailCacheLoadedUnsafeAsync(cancellationToken).ConfigureAwait(false);
            if (thumbnailCache is null
                || !thumbnailCache.TryGetValue(publishedFileId, out var entry)
                || entry.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                return null;
            }

            thumbnailCache[publishedFileId] = entry with { LastAccessedAt = DateTimeOffset.UtcNow };
            return entry.Url;
        }
        finally
        {
            thumbnailCacheLock.Release();
        }
    }

    private async Task CacheThumbnailUrlAsync(
        ulong publishedFileId,
        string url,
        CancellationToken cancellationToken)
    {
        await thumbnailCacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureThumbnailCacheLoadedUnsafeAsync(cancellationToken).ConfigureAwait(false);
            thumbnailCache ??= [];
            var now = DateTimeOffset.UtcNow;
            thumbnailCache[publishedFileId] = new ThumbnailCacheEntry(
                publishedFileId,
                url,
                now,
                now.AddHours(Math.Max(1, options.ThumbnailCacheTtlHours)));

            foreach (var expired in thumbnailCache
                         .Where(pair => pair.Value.ExpiresAt <= now)
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                thumbnailCache.Remove(expired);
            }

            foreach (var overflow in thumbnailCache.Values
                         .OrderBy(entry => entry.LastAccessedAt)
                         .Take(Math.Max(0, thumbnailCache.Count - Math.Max(1, options.ThumbnailCacheLimit)))
                         .Select(entry => entry.PublishedFileId)
                         .ToArray())
            {
                thumbnailCache.Remove(overflow);
            }

            await SaveThumbnailCacheUnsafeAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            thumbnailCacheLock.Release();
        }
    }

    private async Task EnsureThumbnailCacheLoadedUnsafeAsync(CancellationToken cancellationToken)
    {
        var path = GetThumbnailCachePath();
        if (thumbnailCache is not null
            && string.Equals(thumbnailCachePath, path, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        thumbnailCachePath = path;
        try
        {
            if (!File.Exists(path))
            {
                thumbnailCache = [];
                return;
            }

            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var document = await JsonSerializer.DeserializeAsync<ThumbnailCacheDocument>(
                stream,
                jsonOptions,
                cancellationToken).ConfigureAwait(false);
            thumbnailCache = document?.Items?.ToDictionary(item => item.PublishedFileId) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            logger.Warning(ex, "Unable to read Workshop thumbnail cache {ThumbnailCachePath}", path);
            thumbnailCache = [];
        }
    }

    private async Task SaveThumbnailCacheUnsafeAsync(CancellationToken cancellationToken)
    {
        var path = thumbnailCachePath ?? GetThumbnailCachePath();
        await WorkshopServiceUtilities.WriteJsonAtomicallyAsync(
            path,
            new ThumbnailCacheDocument(thumbnailCache?.Values.OrderBy(value => value.PublishedFileId).ToArray() ?? []),
            jsonOptions,
            cancellationToken).ConfigureAwait(false);
    }

    private string GetThumbnailCachePath()
    {
        return Path.Combine(applicationPathService.GetPaths().DataDirectory, ThumbnailCacheFileName);
    }

    private object InvokeInternal(string methodName, params object?[] args)
    {
        return InvokeInternal(methodName, _ => true, args);
    }

    private object InvokeInternal(string methodName, Func<ParameterInfo[], bool> predicate, params object?[] args)
    {
        var method = steamUgcInternalType!
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method => method.Name == methodName)
            .FirstOrDefault(method =>
            {
                var parameters = method.GetParameters();
                return parameters.Length == args.Length && predicate(parameters);
            })
            ?? throw new MissingMethodException(steamUgcInternalType!.FullName, methodName);

        return method.Invoke(steamUgcInternal, args)
            ?? throw new WorkshopException(
                $"Steam UGC call {methodName} returned no value.",
                WorkshopFailureKind.SteamUnavailable);
    }

    private static async Task<object?> AwaitCallResultAsync(object callResult, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        var callResultType = callResult.GetType();
        var onCompleted = callResultType.GetMethod("OnCompleted", BindingFlags.Instance | BindingFlags.Public)
            ?? throw new MissingMethodException(callResultType.FullName, "OnCompleted");
        var getResult = callResultType.GetMethod("GetResult", BindingFlags.Instance | BindingFlags.Public)
            ?? throw new MissingMethodException(callResultType.FullName, "GetResult");

        onCompleted.Invoke(callResult, [new Action(() =>
        {
            try
            {
                tcs.TrySetResult(getResult.Invoke(callResult, null));
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                tcs.TrySetException(ex.InnerException);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        })]);

        try
        {
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            await registration.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static void EnsureSteamResultOk(object result, string message, WorkshopFailureKind failureKind)
    {
        var resultValue = GetFieldValue(result, "Result");
        if (string.Equals(resultValue?.ToString(), "OK", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new WorkshopException($"{message} Steam result: {resultValue}.", failureKind);
    }

    private uint GetItemState(PublishedFileId itemId)
    {
        return Convert.ToUInt32(InvokeInternal("GetItemState", itemId));
    }

    private void ReleaseQuery(object handle)
    {
        try
        {
            InvokeInternal("ReleaseQueryUGCRequest", handle);
        }
        catch (Exception ex)
        {
            logger.Debug(ex, "Unable to release Steam Workshop query handle");
        }
    }

    private async Task RunCallbackPumpAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                SteamClient.RunCallbacks();
            }
            catch (Exception ex)
            {
                logger.Warning(ex, "Steam callback pump failed");
            }

            try
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static PublishedFileId CreatePublishedFileId(ulong value)
    {
        return (PublishedFileId)value;
    }

    private static ulong GetPublishedFileId(object details)
    {
        return (ulong)(PublishedFileId)GetFieldValue(details, "PublishedFileId")!;
    }

    private static object? GetFieldValue(object target, string fieldName)
    {
        return target.GetType()
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(target);
    }

    private static string InvokeUtf8(object target, string methodName)
    {
        return target.GetType()
            .GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.Invoke(target, null)
            ?.ToString() ?? string.Empty;
    }

    private static DateTimeOffset? FromUnixTime(uint unixTime)
    {
        return unixTime == 0 ? null : DateTimeOffset.FromUnixTimeSeconds(unixTime);
    }

    private static void TryDeleteDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return;
        }

        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // Best effort cleanup; the caller will surface the actual deployment failure.
        }
    }

    private static bool TryCreateHardLink(string destinationPath, string sourcePath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        return CreateHardLink(destinationPath, sourcePath, IntPtr.Zero);
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateHardLink(
        string lpFileName,
        string lpExistingFileName,
        IntPtr lpSecurityAttributes);

    public void Dispose()
    {
        callbackPumpCancellation?.Cancel();
        try
        {
            callbackPumpTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // Ignore shutdown races; SteamClient.Shutdown below releases the native state.
        }

        callbackPumpCancellation?.Dispose();
        if (steamClientInitialized)
        {
            SteamClient.Shutdown();
        }

        initializationLock.Dispose();
        thumbnailCacheLock.Dispose();
    }

    private sealed record WorkshopMappingDocument(IReadOnlyList<WorkshopMappingEntry> Items);

    private sealed record ThumbnailCacheDocument(IReadOnlyList<ThumbnailCacheEntry> Items);

    private sealed record ThumbnailCacheEntry(
        ulong PublishedFileId,
        string Url,
        DateTimeOffset LastAccessedAt,
        DateTimeOffset ExpiresAt);
}
