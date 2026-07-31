using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using BohemiX.Core.Models;
using BohemiX.Core.Services;
using Serilog;

namespace BohemiX.Infrastructure.Services;

public sealed class ModPackInstallService : IModPackInstallService
{
    private const string GameDomainName = "kingdomcomedeliverance2";
    private const string SessionFileName = "session.json";
    private const string ManifestFileName = "bohemix.mod.json";
    private const int MaxConcurrentPhaseItems = 3;
    private readonly INexusCollectionService nexusCollectionService;
    private readonly INexusModService nexusModService;
    private readonly INexusAccountService nexusAccountService;
    private readonly INexusDownloadAuthorizationService authorizationService;
    private readonly IModDownloader modDownloader;
    private readonly IModPackageInstaller packageInstaller;
    private readonly IModCatalogService modCatalogService;
    private readonly IApplicationPathService applicationPathService;
    private readonly ILogger logger;
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly SemaphoreSlim phaseItemGate = new(MaxConcurrentPhaseItems, MaxConcurrentPhaseItems);
    private readonly SemaphoreSlim packageInstallGate = new(1, 1);
    private readonly SemaphoreSlim authorizationGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> activeOperations = new();
    private readonly ConcurrentDictionary<Guid, byte> pauseRequests = new();
    private readonly ConcurrentDictionary<Guid, byte> cancelRequests = new();

    public ModPackInstallService(
        INexusCollectionService nexusCollectionService,
        INexusModService nexusModService,
        INexusAccountService nexusAccountService,
        INexusDownloadAuthorizationService authorizationService,
        IModDownloader modDownloader,
        IModPackageInstaller packageInstaller,
        IModCatalogService modCatalogService,
        IApplicationPathService applicationPathService,
        ILogger logger)
    {
        this.nexusCollectionService = nexusCollectionService;
        this.nexusModService = nexusModService;
        this.nexusAccountService = nexusAccountService;
        this.authorizationService = authorizationService;
        this.modDownloader = modDownloader;
        this.packageInstaller = packageInstaller;
        this.modCatalogService = modCatalogService;
        this.applicationPathService = applicationPathService;
        this.logger = logger.ForContext<ModPackInstallService>();
    }

    public async Task<ModPackInstallPlan> PrepareAsync(
        ModPackCatalogEntry entry,
        bool allowAdultContent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Platform != ModPackPlatform.NexusCollection)
        {
            throw new InvalidOperationException("Only Nexus collections use the in-app mod-pack installer.");
        }

        var sessionId = Guid.NewGuid();
        var revision = await nexusCollectionService.GetLatestPublishedRevisionAsync(
            entry.PlatformIdentifier,
            allowAdultContent,
            cancellationToken);
        if ((entry.ContainsAdultContent || revision.AdultContent) && !allowAdultContent)
        {
            throw new InvalidOperationException("This collection contains adult content and requires confirmation.");
        }

        var package = await nexusCollectionService.DownloadAndReadPackageAsync(revision, sessionId, cancellationToken);
        var items = package.Manifest.Items
            .Select(item => CreatePlanItem(item, package.BundledDirectory))
            .ToArray();
        var plan = new ModPackInstallPlan(
            sessionId,
            entry.Id,
            string.IsNullOrWhiteSpace(package.Manifest.Info.Name) ? entry.Title : package.Manifest.Info.Name,
            entry.OfficialPageUrl,
            entry.PlatformIdentifier,
            revision.RevisionNumber,
            revision.RevisionId,
            entry.ContainsAdultContent || revision.AdultContent || items.Any(item => item.ContainsAdultContent),
            BuildCompatibilityText(entry, package.Manifest.Info.GameVersions),
            revision.TotalSizeInBytes ?? items.Where(item => item.FileSizeInBytes is > 0).Sum(item => item.FileSizeInBytes),
            items,
            package.Manifest.LoadOrder);
        var session = new ModPackInstallSession(
            sessionId,
            plan,
            ModPackInstallSessionStatus.Prepared,
            [],
            items.Select(item => new ModPackInstallItemState(item.Id, ModPackInstallItemStatus.Pending)).ToArray(),
            DateTimeOffset.UtcNow);
        await SaveSessionAsync(session, cancellationToken);
        return plan;
    }

    public async Task<ModPackInstallResult> InstallAsync(
        Guid sessionId,
        IReadOnlyCollection<string> selectedOptionalItemIds,
        IProgress<ModPackInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selectedOptionalItemIds);
        var selected = selectedOptionalItemIds
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return await RunSessionAsync(sessionId, selected, progress, cancellationToken);
    }

    public async Task<ModPackInstallResult> ResumeAsync(
        Guid sessionId,
        IProgress<ModPackInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var session = await LoadSessionAsync(sessionId, cancellationToken)
            ?? throw new InvalidOperationException("The mod-pack installation session was not found.");
        return await RunSessionAsync(
            sessionId,
            session.SelectedOptionalItemIds.ToHashSet(StringComparer.OrdinalIgnoreCase),
            progress,
            cancellationToken);
    }

    public async Task PauseAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        pauseRequests[sessionId] = 0;
        if (activeOperations.TryGetValue(sessionId, out var operation))
        {
            operation.Cancel();
        }

        foreach (var queueItem in modDownloader.Queue.Where(item => item.Request.ModPackSessionId == sessionId))
        {
            modDownloader.PauseQueueItem(queueItem.QueueKey);
        }

        var session = await LoadSessionAsync(sessionId, cancellationToken);
        if (session is not null)
        {
            await SaveSessionAsync(session with
            {
                Status = ModPackInstallSessionStatus.Paused,
                UpdatedAt = DateTimeOffset.UtcNow,
                Message = "Installation paused."
            }, cancellationToken);
        }
    }

    public async Task CancelAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        cancelRequests[sessionId] = 0;
        if (activeOperations.TryGetValue(sessionId, out var operation))
        {
            operation.Cancel();
        }

        foreach (var queueItem in modDownloader.Queue.Where(item => item.Request.ModPackSessionId == sessionId))
        {
            modDownloader.CancelQueueItem(queueItem.QueueKey);
        }

        var session = await LoadSessionAsync(sessionId, cancellationToken);
        if (session is not null)
        {
            await SaveSessionAsync(session with
            {
                Status = ModPackInstallSessionStatus.Canceled,
                Items = session.Items.Select(item => item.Status is ModPackInstallItemStatus.Installed or ModPackInstallItemStatus.Reused
                    ? item
                    : item with { Status = ModPackInstallItemStatus.Canceled }).ToArray(),
                UpdatedAt = DateTimeOffset.UtcNow,
                Message = "Installation canceled."
            }, cancellationToken);
        }
    }

    public async Task<IReadOnlyList<ModPackInstallSession>> LoadSessionsAsync(
        CancellationToken cancellationToken = default)
    {
        var root = GetSessionsRoot();
        if (!Directory.Exists(root))
        {
            return [];
        }

        var sessions = new List<ModPackInstallSession>();
        foreach (var sessionFile in Directory.EnumerateFiles(root, SessionFileName, SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var session = await ReadSessionFileAsync(sessionFile, cancellationToken);
            if (session is not null)
            {
                sessions.Add(session);
            }
        }

        return sessions.OrderByDescending(session => session.UpdatedAt).ToArray();
    }

    private async Task<ModPackInstallResult> RunSessionAsync(
        Guid sessionId,
        HashSet<string> selectedOptionalItemIds,
        IProgress<ModPackInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            pauseRequests.TryRemove(sessionId, out _);
            cancelRequests.TryRemove(sessionId, out _);
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (!activeOperations.TryAdd(sessionId, operation))
            {
                throw new InvalidOperationException("This mod-pack installation is already running.");
            }

            try
            {
                var session = await LoadSessionAsync(sessionId, operation.Token)
                    ?? throw new InvalidOperationException("The mod-pack installation session was not found.");
                session = session with
                {
                    Status = ModPackInstallSessionStatus.Running,
                    SelectedOptionalItemIds = selectedOptionalItemIds.ToArray(),
                    UpdatedAt = DateTimeOffset.UtcNow,
                    Message = "Installation started."
                };
                await SaveSessionAsync(session, operation.Token);

                var account = await nexusAccountService.GetBoundAccountAsync(operation.Token)
                    ?? await nexusAccountService.BindAsync(operation.Token);
                var executableItems = session.Plan.Items
                    .Where(item => !item.IsOptional || selectedOptionalItemIds.Contains(item.Id))
                    .OrderBy(item => item.Phase)
                    .ThenBy(item => Array.IndexOf(session.Plan.Items.ToArray(), item))
                    .ToArray();
                var stateMap = session.Items.ToDictionary(item => item.ItemId, StringComparer.OrdinalIgnoreCase);
                foreach (var optionalItem in session.Plan.Items.Where(item => item.IsOptional && !selectedOptionalItemIds.Contains(item.Id)))
                {
                    stateMap[optionalItem.Id] = new ModPackInstallItemState(
                        optionalItem.Id,
                        ModPackInstallItemStatus.Skipped,
                        ErrorMessage: "Optional item was not selected.");
                }

                session = session with
                {
                    Items = session.Items.Select(item => stateMap[item.ItemId]).ToArray(),
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                await SaveSessionAsync(session, operation.Token);
                var completed = stateMap.Values.Count(IsSuccessfulState);

                foreach (var phase in executableItems.Select(item => item.Phase).Distinct().Order())
                {
                    var phaseItems = executableItems
                        .Where(item => item.Phase == phase)
                        .Where(item => !stateMap.TryGetValue(item.Id, out var existingState) || !IsSuccessfulState(existingState))
                        .ToArray();
                    var phaseCompleted = completed;
                    var phaseTasks = phaseItems.Select(async item =>
                    {
                        operation.Token.ThrowIfCancellationRequested();
                        if (item.Support != ModPackInstallItemSupport.Supported)
                        {
                            return new ModPackInstallItemState(
                                item.Id,
                                ModPackInstallItemStatus.Skipped,
                                ErrorMessage: item.SupportMessage);
                        }

                        await phaseItemGate.WaitAsync(operation.Token);
                        try
                        {
                            return await InstallItemAsync(
                                session,
                                item,
                                account,
                                phaseCompleted,
                                executableItems.Length,
                                progress,
                                operation.Token);
                        }
                        finally
                        {
                            phaseItemGate.Release();
                        }
                    }).ToArray();
                    var phaseStates = await Task.WhenAll(phaseTasks);

                    for (var index = 0; index < phaseItems.Length; index++)
                    {
                        var item = phaseItems[index];
                        var state = phaseStates[index];
                        stateMap[item.Id] = state;
                        if (IsSuccessfulState(state))
                        {
                            completed++;
                        }

                        session = session with
                        {
                            Items = session.Items.Select(value => value.ItemId.Equals(item.Id, StringComparison.OrdinalIgnoreCase) ? state : value).ToArray(),
                            UpdatedAt = DateTimeOffset.UtcNow,
                            Message = state.ErrorMessage
                        };
                        await SaveSessionAsync(session, operation.Token);
                    }
                }

                progress?.Report(new ModPackInstallProgress(
                    sessionId,
                    ModPackInstallStage.ApplyingLoadOrder,
                    "Applying the collection load order.",
                    completed,
                    executableItems.Length));
                await ApplyLoadOrderAsync(session.Plan, stateMap, operation.Token);
                return await CompleteSessionAsync(session with { Items = stateMap.Values.ToArray() }, progress, operation.Token);
            }
            catch (OperationCanceledException) when (pauseRequests.ContainsKey(sessionId) || cancelRequests.ContainsKey(sessionId))
            {
                var session = await LoadSessionAsync(sessionId, CancellationToken.None)
                    ?? throw new InvalidOperationException("The mod-pack installation session was not found after cancellation.");
                var canceled = cancelRequests.ContainsKey(sessionId);
                var status = canceled ? ModPackInstallSessionStatus.Canceled : ModPackInstallSessionStatus.Paused;
                session = session with
                {
                    Status = status,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    Message = canceled ? "Installation canceled." : "Installation paused."
                };
                await SaveSessionAsync(session, CancellationToken.None);
                progress?.Report(new ModPackInstallProgress(
                    sessionId,
                    ModPackInstallStage.Paused,
                    session.Message!,
                    session.Items.Count(IsSuccessfulState),
                    session.Items.Count));
                return CreateResult(session);
            }
            finally
            {
                activeOperations.TryRemove(sessionId, out _);
            }
        }
        finally
        {
            operationGate.Release();
        }
    }

    private async Task<ModPackInstallItemState> InstallItemAsync(
        ModPackInstallSession session,
        ModPackInstallPlanItem item,
        NexusAccountBinding account,
        int completed,
        int total,
        IProgress<ModPackInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            if (item.Source == ModPackInstallItemSource.Bundle)
            {
                return await InstallPackageAsync(session, item, item.BundlePath!, cancellationToken);
            }

            if (item.ModId is not > 0 || item.FileId is not > 0)
            {
                return new ModPackInstallItemState(item.Id, ModPackInstallItemStatus.Failed, ErrorMessage: "The Nexus mod or file id is missing.");
            }

            var reused = await FindInstalledVersionAsync(item.ModId.Value, item.FileId.Value, cancellationToken);
            if (reused is not null)
            {
                return new ModPackInstallItemState(item.Id, ModPackInstallItemStatus.Reused, reused.Id);
            }

            var files = await nexusModService.GetModFilesAsync(GameDomainName, item.ModId.Value, cancellationToken);
            var file = files.FirstOrDefault(value => value.FileId == item.FileId.Value);
            if (file is null)
            {
                return new ModPackInstallItemState(item.Id, ModPackInstallItemStatus.Failed, ErrorMessage: "The collection's declared Nexus file is unavailable.");
            }

            if (!string.IsNullOrWhiteSpace(item.Md5)
                && !string.IsNullOrWhiteSpace(file.Md5)
                && !NormalizeMd5(item.Md5).Equals(NormalizeMd5(file.Md5), StringComparison.OrdinalIgnoreCase))
            {
                return new ModPackInstallItemState(item.Id, ModPackInstallItemStatus.Failed, ErrorMessage: "The Nexus file checksum no longer matches the collection revision.");
            }

            NexusDownloadLink link;
            try
            {
                link = await nexusModService.GetDownloadLinkAsync(
                    GameDomainName,
                    item.ModId.Value,
                    item.FileId.Value,
                    cancellationToken);
            }
            catch (NexusModsException ex) when (ShouldRequestDownloadAuthorization(ex))
            {
                logger.Information(
                    "Direct download link was unavailable for mod-pack item {ItemId}; requesting Nexus file authorization.",
                    item.Id);
                progress?.Report(new ModPackInstallProgress(
                    session.SessionId,
                    ModPackInstallStage.WaitingForAuthorization,
                    $"Waiting for Nexus authorization: {item.Name}",
                    completed,
                    total,
                    item.Id,
                    item.Name));
                NexusDownloadAuthorizationResult authorizationResult;
                await authorizationGate.WaitAsync(cancellationToken);
                try
                {
                    authorizationResult = await authorizationService.AuthorizeAsync(
                        new NexusDownloadAuthorizationRequest(
                            GameDomainName,
                            item.ModId.Value,
                            item.FileId.Value,
                            item.Name,
                            file.FileName,
                            BuildOfficialFilePageUri(item.ModId.Value, item.FileId.Value)),
                        cancellationToken);
                }
                finally
                {
                    authorizationGate.Release();
                }
                if (!authorizationResult.Authorized || authorizationResult.Authorization is null)
                {
                    pauseRequests[session.SessionId] = 0;
                    throw new OperationCanceledException(authorizationResult.Message ?? "Nexus authorization was canceled.", cancellationToken);
                }

                link = await nexusModService.GetDownloadLinkAsync(
                    GameDomainName,
                    item.ModId.Value,
                    item.FileId.Value,
                    authorizationResult.Authorization,
                    cancellationToken);
            }
            progress?.Report(new ModPackInstallProgress(
                session.SessionId,
                ModPackInstallStage.Downloading,
                $"Downloading {item.Name}",
                completed,
                total,
                item.Id,
                item.Name));
            var downloadDirectory = Path.Combine(GetSessionDirectory(session.SessionId), "downloads", $"phase-{item.Phase}");
            var request = new NexusModDownloadRequest(
                GameDomainName,
                item.ModId.Value,
                item.FileId.Value,
                link.Uri,
                string.IsNullOrWhiteSpace(file.FileName) ? $"nexus-{item.ModId}-{item.FileId}.zip" : file.FileName,
                downloadDirectory,
                item.Md5 ?? file.Md5,
                applicationPathService.GetPaths().GameEnvironmentId,
                session.SessionId,
                session.Plan.ModPackId,
                session.Plan.ModPackName);
            var downloadProgress = new InlineProgress<ModDownloadProgress>(value =>
            {
                progress?.Report(new ModPackInstallProgress(
                    session.SessionId,
                    ModPackInstallStage.Downloading,
                    $"Downloading {item.Name}",
                    completed,
                    total,
                    item.Id,
                    item.Name,
                    value));
            });
            var result = await modDownloader.DownloadAsync(request, downloadProgress, cancellationToken);
            if (!result.Success || string.IsNullOrWhiteSpace(result.FinalPath))
            {
                if (result.Status == ModDownloadStatus.Paused)
                {
                    pauseRequests[session.SessionId] = 0;
                    throw new OperationCanceledException("Download paused.", cancellationToken);
                }

                return new ModPackInstallItemState(item.Id, ModPackInstallItemStatus.Failed, ErrorMessage: result.ErrorMessage ?? "Download failed.");
            }

            progress?.Report(new ModPackInstallProgress(
                session.SessionId,
                ModPackInstallStage.Installing,
                $"Installing {item.Name}",
                completed,
                total,
                item.Id,
                item.Name));
            return await InstallPackageAsync(session, item, result.FinalPath, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is NexusModsException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            logger.Warning(ex, "Mod-pack item {ItemId} failed in session {SessionId}.", item.Id, session.SessionId);
            return new ModPackInstallItemState(item.Id, ModPackInstallItemStatus.Failed, ErrorMessage: ex.Message);
        }
    }

    private async Task<ModPackInstallItemState> InstallPackageAsync(
        ModPackInstallSession session,
        ModPackInstallPlanItem item,
        string packagePath,
        CancellationToken cancellationToken)
    {
        await packageInstallGate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(packagePath))
            {
                return new ModPackInstallItemState(item.Id, ModPackInstallItemStatus.Failed, ErrorMessage: "The package file is missing.");
            }

            if (!string.IsNullOrWhiteSpace(item.Md5) && !await VerifyMd5Async(packagePath, item.Md5, cancellationToken))
            {
                return new ModPackInstallItemState(item.Id, ModPackInstallItemStatus.Failed, ErrorMessage: "The package checksum does not match the collection revision.");
            }

            if (item.ModId is > 0)
            {
                await DisableOlderNexusVersionsAsync(item.ModId.Value, item.FileId, cancellationToken);
            }

            var localModId = item.ModId is > 0 && item.FileId is > 0
                ? $"nexus-{item.ModId}-file-{item.FileId}"
                : $"modpack-{session.Plan.ModPackId}-{item.Id}";
            var installResult = await packageInstaller.InstallAsync(
                new ModPackageInstallRequest(
                    packagePath,
                    applicationPathService.GetPaths().ModsDirectory,
                    localModId,
                    item.Name,
                    item.Version,
                    Source: new ModPackageSourceMetadata(
                        "NexusCollection",
                        item.ModId,
                        item.FileId,
                        item.Md5,
                        session.Plan.ModPackId,
                        session.Plan.RevisionNumber,
                        session.SessionId,
                        session.Plan.ModPackName)),
                cancellationToken);
            return installResult.Success
                ? new ModPackInstallItemState(item.Id, ModPackInstallItemStatus.Installed, localModId)
                : new ModPackInstallItemState(item.Id, ModPackInstallItemStatus.Failed, ErrorMessage: installResult.Message);
        }
        finally
        {
            packageInstallGate.Release();
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

    private async Task ApplyLoadOrderAsync(
        ModPackInstallPlan plan,
        IReadOnlyDictionary<string, ModPackInstallItemState> states,
        CancellationToken cancellationToken)
    {
        var installedIds = states.Values
            .Where(IsSuccessfulState)
            .Select(state => state.InstalledModId)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (installedIds.Count == 0)
        {
            return;
        }

        var installed = await modCatalogService.LoadInstalledModsAsync(cancellationToken);
        var unrelated = installed
            .Where(mod => !installedIds.Contains(mod.Id))
            .OrderBy(mod => mod.LoadOrder)
            .Select(mod => mod.Id)
            .ToList();
        var orderedItems = ResolveCollectionOrder(plan);
        foreach (var item in orderedItems)
        {
            if (states.TryGetValue(item.Id, out var state)
                && IsSuccessfulState(state)
                && !string.IsNullOrWhiteSpace(state.InstalledModId)
                && !unrelated.Contains(state.InstalledModId, StringComparer.OrdinalIgnoreCase))
            {
                unrelated.Add(state.InstalledModId);
            }
        }

        await modCatalogService.SaveLoadOrderAsync(unrelated, cancellationToken);
    }

    private static IReadOnlyList<ModPackInstallPlanItem> ResolveCollectionOrder(ModPackInstallPlan plan)
    {
        if (plan.LoadOrder.Count == 0)
        {
            return plan.Items.OrderBy(item => item.Phase).ToArray();
        }

        var matched = new List<ModPackInstallPlanItem>();
        var remaining = plan.Items.ToList();
        foreach (var loadOrderValue in plan.LoadOrder)
        {
            var matches = remaining.Where(item => MatchesLoadOrder(item, loadOrderValue)).ToArray();
            if (matches.Length != 1)
            {
                continue;
            }

            matched.Add(matches[0]);
            remaining.Remove(matches[0]);
        }

        matched.AddRange(remaining.OrderBy(item => item.Phase));
        return matched;
    }

    private async Task<ModPackInstallResult> CompleteSessionAsync(
        ModPackInstallSession session,
        IProgress<ModPackInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var failed = session.Items.Count(item => item.Status == ModPackInstallItemStatus.Failed);
        var selectedOptionalIds = session.SelectedOptionalItemIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var requiresAttention = session.Items.Any(state =>
        {
            if (state.Status != ModPackInstallItemStatus.Skipped)
            {
                return false;
            }

            var planItem = session.Plan.Items.First(item => item.Id.Equals(state.ItemId, StringComparison.OrdinalIgnoreCase));
            return !planItem.IsOptional || selectedOptionalIds.Contains(planItem.Id);
        });
        var status = failed > 0 || requiresAttention
            ? ModPackInstallSessionStatus.PartiallyCompleted
            : ModPackInstallSessionStatus.Completed;
        session = session with
        {
            Status = status,
            UpdatedAt = DateTimeOffset.UtcNow,
            Message = status == ModPackInstallSessionStatus.Completed
                ? "The mod pack was installed."
                : "The mod pack was partially installed; successful items were kept."
        };
        await SaveSessionAsync(session, cancellationToken);
        progress?.Report(new ModPackInstallProgress(
            session.SessionId,
            ModPackInstallStage.Completed,
            session.Message,
            session.Items.Count(IsSuccessfulState),
            session.Items.Count));
        return CreateResult(session);
    }

    private static ModPackInstallResult CreateResult(ModPackInstallSession session)
    {
        return new ModPackInstallResult(
            session.SessionId,
            session.Status,
            session.Items.Count(item => item.Status == ModPackInstallItemStatus.Installed),
            session.Items.Count(item => item.Status == ModPackInstallItemStatus.Reused),
            session.Items.Count(item => item.Status == ModPackInstallItemStatus.Skipped),
            session.Items.Count(item => item.Status == ModPackInstallItemStatus.Failed),
            session.Items,
            session.Message ?? string.Empty);
    }

    private async Task<ModManifest?> FindInstalledVersionAsync(int modId, int fileId, CancellationToken cancellationToken)
    {
        foreach (var mod in await modCatalogService.LoadInstalledModsAsync(cancellationToken))
        {
            var source = await TryReadSourceAsync(mod.RootPath, cancellationToken);
            if (source?.NexusModId == modId && source.NexusFileId == fileId)
            {
                return mod;
            }
        }

        return null;
    }

    private async Task DisableOlderNexusVersionsAsync(int modId, int? currentFileId, CancellationToken cancellationToken)
    {
        foreach (var mod in await modCatalogService.LoadInstalledModsAsync(cancellationToken))
        {
            var source = await TryReadSourceAsync(mod.RootPath, cancellationToken);
            if (source?.NexusModId == modId && source.NexusFileId != currentFileId)
            {
                await modCatalogService.SaveModEnabledStateAsync(mod.Id, false, cancellationToken);
            }
        }
    }

    private static async Task<StoredSource?> TryReadSourceAsync(string rootPath, CancellationToken cancellationToken)
    {
        var path = Path.Combine(rootPath, ManifestFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty("source", out var source) || source.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return new StoredSource(
                ReadNullableInt(source, "nexusModId"),
                ReadNullableInt(source, "nexusFileId"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private async Task SaveSessionAsync(ModPackInstallSession session, CancellationToken cancellationToken)
    {
        var directory = GetSessionDirectory(session.SessionId);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, SessionFileName);
        var temporaryPath = path + ".tmp";
        await using (var stream = new FileStream(
            temporaryPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, session, JsonOptions, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        File.Move(temporaryPath, path, overwrite: true);
    }

    private async Task<ModPackInstallSession?> LoadSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        return await ReadSessionFileAsync(Path.Combine(GetSessionDirectory(sessionId), SessionFileName), cancellationToken);
    }

    private static async Task<ModPackInstallSession?> ReadSessionFileAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<ModPackInstallSession>(stream, JsonOptions, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private string GetSessionsRoot()
    {
        var cacheDirectory = applicationPathService.GetPaths().CacheDirectory;
        if (string.IsNullOrWhiteSpace(cacheDirectory))
        {
            throw new InvalidOperationException("The application cache directory is unavailable.");
        }

        return Path.Combine(cacheDirectory, "ModPacks", "Sessions");
    }

    private string GetSessionDirectory(Guid sessionId) => Path.Combine(GetSessionsRoot(), sessionId.ToString("N"));

    private static ModPackInstallPlanItem CreatePlanItem(NexusCollectionManifestItem item, string bundledDirectory)
    {
        var support = ModPackInstallItemSupport.Supported;
        var message = string.Empty;
        if (item.HasInstallerChoices || item.HasPatches || item.HasFileOverrides)
        {
            support = ModPackInstallItemSupport.RequiresVortex;
            message = "This item uses Vortex installer choices, patches, or file overrides.";
        }
        else if (item.Source is ModPackInstallItemSource.Direct or ModPackInstallItemSource.Browse or ModPackInstallItemSource.Manual)
        {
            support = ModPackInstallItemSupport.ManualActionRequired;
            message = "This source must be handled manually on its original page.";
        }
        else if (item.Source == ModPackInstallItemSource.Nexus && (item.ModId is not > 0 || item.FileId is not > 0))
        {
            support = ModPackInstallItemSupport.Invalid;
            message = "The collection item does not declare a valid Nexus mod/file id.";
        }

        string? bundlePath = null;
        if (item.Source == ModPackInstallItemSource.Bundle)
        {
            bundlePath = ResolveBundlePath(bundledDirectory, item.LogicalFileName ?? item.FileExpression);
            if (bundlePath is null)
            {
                support = ModPackInstallItemSupport.Invalid;
                message = "The declared bundle resource is missing from the official collection package.";
            }
        }

        if (item.Source == ModPackInstallItemSource.Unknown)
        {
            support = ModPackInstallItemSupport.Invalid;
            message = "The collection item uses an unknown source type.";
        }

        return new ModPackInstallPlanItem(
            item.Id,
            item.Name,
            item.Version,
            item.Optional,
            item.Source,
            support,
            message,
            item.Phase,
            item.ModId,
            item.FileId,
            item.Md5,
            item.FileSizeInBytes,
            bundlePath,
            item.Url,
            item.Instructions,
            item.ContainsAdultContent);
    }

    private static string? ResolveBundlePath(string bundledDirectory, string? declaredPath)
    {
        if (string.IsNullOrWhiteSpace(declaredPath) || !Directory.Exists(bundledDirectory))
        {
            return null;
        }

        var fileName = Path.GetFileName(declaredPath.Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        var matches = Directory.EnumerateFiles(bundledDirectory, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path).Equals(fileName, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static string BuildCompatibilityText(ModPackCatalogEntry entry, IReadOnlyList<string> gameVersions)
    {
        var declared = gameVersions.Count == 0 ? null : string.Join(", ", gameVersions);
        return string.IsNullOrWhiteSpace(declared)
            ? entry.GameVersionNote
            : $"{entry.GameVersionNote}; collection manifest: {declared}";
    }

    private static Uri BuildOfficialFilePageUri(int modId, int fileId) =>
        new($"https://www.nexusmods.com/{GameDomainName}/mods/{modId}?tab=files&file_id={fileId}");

    private static bool MatchesLoadOrder(ModPackInstallPlanItem item, string value)
    {
        return item.Id.Equals(value, StringComparison.OrdinalIgnoreCase)
            || item.Name.Equals(value, StringComparison.OrdinalIgnoreCase)
            || (item.ModId is > 0 && item.ModId.Value.ToString().Equals(value, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSuccessfulState(ModPackInstallItemState state) =>
        state.Status is ModPackInstallItemStatus.Installed or ModPackInstallItemStatus.Reused;

    private static async Task<bool> VerifyMd5Async(string path, string expectedMd5, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        using var md5 = MD5.Create();
        var actual = Convert.ToHexString(await md5.ComputeHashAsync(stream, cancellationToken));
        return actual.Equals(NormalizeMd5(expectedMd5), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeMd5(string value) => value.Trim().Replace("-", string.Empty, StringComparison.Ordinal);

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    private static int? ReadNullableInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), out var number) => number,
            _ => null
        };
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private sealed record StoredSource(int? NexusModId, int? NexusFileId);
}
