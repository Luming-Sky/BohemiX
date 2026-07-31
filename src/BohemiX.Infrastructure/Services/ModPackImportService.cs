using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;
using BohemiX.Core.Models;
using BohemiX.Core.Services;
using Serilog;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace BohemiX.Infrastructure.Services;

public sealed class ModPackImportService : IModPackImportService
{
    private const int MaxModCount = 500;
    private const int MaxEntryCount = 250_000;
    private const long MaxExpandedBytes = 50L * 1024 * 1024 * 1024;
    private static readonly string[] ArchiveExtensions = [".zip", ".7z", ".rar"];

    private readonly IApplicationPathService applicationPathService;
    private readonly IModCatalogService modCatalogService;
    private readonly IModPackageInstaller packageInstaller;
    private readonly ILogger logger;
    private readonly ConcurrentDictionary<Guid, ImportSession> sessions = new();

    public ModPackImportService(
        IApplicationPathService applicationPathService,
        IModCatalogService modCatalogService,
        IModPackageInstaller packageInstaller,
        ILogger logger)
    {
        this.applicationPathService = applicationPathService;
        this.modCatalogService = modCatalogService;
        this.packageInstaller = packageInstaller;
        this.logger = logger.ForContext<ModPackImportService>();
        CleanStaleSessions();
    }

    public async Task<ModPackImportPrepareResult> PrepareAsync(
        string packagePath,
        string? password = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packagePath) || !File.Exists(packagePath))
        {
            return new ModPackImportPrepareResult(ModPackImportPrepareStatus.InvalidArchive, "The selected mod-pack file was not found.");
        }

        if (!IsArchive(packagePath))
        {
            return new ModPackImportPrepareResult(ModPackImportPrepareStatus.InvalidArchive, "Choose a ZIP, 7Z, or RAR mod-pack archive.");
        }

        var sessionId = Guid.NewGuid();
        var sessionRoot = Path.Combine(GetSessionsRoot(), sessionId.ToString("N"));
        var outerRoot = Path.Combine(sessionRoot, "outer");
        Directory.CreateDirectory(outerRoot);
        var limits = new ExtractionLimits();

        try
        {
            await ExtractArchiveAsync(packagePath, outerRoot, password, limits, cancellationToken).ConfigureAwait(false);
            var initialRoots = FindModRoots(outerRoot);
            var nestedArchives = Directory.EnumerateFiles(outerRoot, "*", SearchOption.AllDirectories)
                .Where(IsArchive)
                .Where(path => !initialRoots.Any(root => IsPathInsideDirectory(root, path)))
                .OrderBy(path => Path.GetRelativePath(outerRoot, path), StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var warnings = new List<string>();
            var nestedFailures = new List<(string Path, string Message)>();
            for (var index = 0; index < nestedArchives.Length; index++)
            {
                var nestedRoot = Path.Combine(sessionRoot, "nested", index.ToString("D4"));
                Directory.CreateDirectory(nestedRoot);
                try
                {
                    await ExtractArchiveAsync(nestedArchives[index], nestedRoot, password, limits, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (IsArchivePasswordFailure(ex, nestedArchives[index], password) && !string.IsNullOrEmpty(password))
                {
                    DeleteSessionRoot(nestedRoot);
                    var relativeArchive = Path.GetRelativePath(outerRoot, nestedArchives[index]).Replace('\\', '/');
                    nestedFailures.Add((relativeArchive, ex.Message));
                    warnings.Add($"Could not decrypt nested archive {relativeArchive} with the supplied password.");
                }
            }

            var roots = FindModRoots(sessionRoot)
                .OrderBy(path => Path.GetRelativePath(sessionRoot, path).Length)
                .ThenBy(path => Path.GetRelativePath(sessionRoot, path), StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (roots.Length > MaxModCount)
            {
                throw new ImportLimitException($"The archive contains more than {MaxModCount} mods.");
            }

            var installed = await modCatalogService.LoadInstalledModsAsync(cancellationToken).ConfigureAwait(false);
            var installedById = installed.ToDictionary(mod => mod.Id, StringComparer.OrdinalIgnoreCase);
            var parsed = new List<ModPackImportItem>();
            foreach (var root in roots)
            {
                var relativePath = Path.GetRelativePath(sessionRoot, root).Replace('\\', '/');
                try
                {
                    var metadata = ReadNativeManifest(Path.Combine(root, "mod.manifest"));
                    var id = NormalizeId(string.IsNullOrWhiteSpace(metadata.Name) ? Path.GetFileName(root) : metadata.Name);
                    installedById.TryGetValue(id, out var existing);
                    parsed.Add(new ModPackImportItem(
                        id,
                        string.IsNullOrWhiteSpace(metadata.Name) ? Path.GetFileName(root) : metadata.Name,
                        string.IsNullOrWhiteSpace(metadata.Version) ? "local" : metadata.Version,
                        metadata.Author,
                        relativePath,
                        root,
                        existing is null ? ModPackImportItemState.New : ModPackImportItemState.AlreadyInstalled,
                        existing is null ? "Ready to import." : "A mod with this ID is already installed.",
                        existing?.RootPath,
                        metadata.Name));
                }
                catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    parsed.Add(new ModPackImportItem(
                        $"invalid-{parsed.Count + 1}",
                        Path.GetFileName(root),
                        "-",
                        string.Empty,
                        relativePath,
                        root,
                        ModPackImportItemState.Invalid,
                        $"Invalid mod.manifest: {ex.Message}"));
                }
            }

            var validRoots = roots.Select(Path.GetFullPath).ToArray();
            foreach (var dataDirectory in Directory.EnumerateDirectories(sessionRoot, "Data", SearchOption.AllDirectories))
            {
                var candidate = Directory.GetParent(dataDirectory)?.FullName;
                if (candidate is null || validRoots.Any(root => IsPathInsideDirectory(root, candidate) || string.Equals(root, candidate, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var relativePath = Path.GetRelativePath(sessionRoot, candidate).Replace('\\', '/');
                parsed.Add(new ModPackImportItem(
                    $"invalid-data-{parsed.Count + 1}",
                    Path.GetFileName(candidate),
                    "-",
                    string.Empty,
                    relativePath,
                    candidate,
                    ModPackImportItemState.Invalid,
                    "This directory contains Data but no mod.manifest, so it was not guessed as a mod."));
            }

            foreach (var failure in nestedFailures)
            {
                parsed.Add(new ModPackImportItem(
                    $"invalid-archive-{parsed.Count + 1}",
                    Path.GetFileName(failure.Path),
                    "-",
                    string.Empty,
                    failure.Path,
                    string.Empty,
                    ModPackImportItemState.Invalid,
                    "This nested archive uses a different or invalid password."));
            }

            var duplicateGroups = parsed
                .Where(item => item.State is ModPackImportItemState.New or ModPackImportItemState.AlreadyInstalled)
                .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1);
            foreach (var group in duplicateGroups)
            {
                foreach (var duplicate in group.Skip(1).ToArray())
                {
                    var itemIndex = parsed.IndexOf(duplicate);
                    parsed[itemIndex] = duplicate with
                    {
                        State = ModPackImportItemState.DuplicateInPackage,
                        StatusMessage = "Another mod in this package uses the same ID."
                    };
                }
            }

            var validItems = parsed.Count(item => item.State is ModPackImportItemState.New or ModPackImportItemState.AlreadyInstalled);
            if (validItems == 0)
            {
                DeleteSessionRoot(sessionRoot);
                return new ModPackImportPrepareResult(ModPackImportPrepareStatus.NoModsFound, "No valid KCD2 mods with mod.manifest were found.");
            }

            var orderPath = Directory.EnumerateFiles(sessionRoot, "mod_order.txt", SearchOption.AllDirectories)
                .OrderBy(path => Path.GetRelativePath(sessionRoot, path).Length)
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            var authorOrder = orderPath is null
                ? Array.Empty<string>()
                : (await File.ReadAllLinesAsync(orderPath, cancellationToken).ConfigureAwait(false))
                    .Select(line => line.Trim())
                    .Where(line => line.Length > 0 && !line.StartsWith('#') && !line.StartsWith(';'))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            if (nestedArchives.Length > 0)
            {
                warnings.Add($"Expanded {nestedArchives.Length} nested mod archive(s), limited to one nested level.");
            }

            var recognizedFiles = roots.Sum(root => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Count());
            var ignoredFileCount = Math.Max(0, limits.EntryCount - recognizedFiles - (orderPath is null ? 0 : 1));
            if (ignoredFileCount > 0)
            {
                warnings.Add($"Ignored {ignoredFileCount} file(s) outside recognized mod directories.");
            }

            await using var packageStream = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var packageHash = Convert.ToHexString(await SHA256.HashDataAsync(packageStream, cancellationToken).ConfigureAwait(false));
            var plan = new ModPackImportPlan(
                sessionId,
                Path.GetFileNameWithoutExtension(packagePath),
                Path.GetFullPath(packagePath),
                packageHash,
                parsed,
                authorOrder,
                warnings,
                ignoredFileCount);
            sessions[sessionId] = new ImportSession(sessionRoot, plan);
            return new ModPackImportPrepareResult(ModPackImportPrepareStatus.Ready, $"Found {validItems} importable mods.", plan);
        }
        catch (OperationCanceledException)
        {
            DeleteSessionRoot(sessionRoot);
            throw;
        }
        catch (ImportLimitException ex)
        {
            DeleteSessionRoot(sessionRoot);
            return new ModPackImportPrepareResult(ModPackImportPrepareStatus.LimitExceeded, ex.Message);
        }
        catch (Exception ex) when (IsArchivePasswordFailure(ex, packagePath, password))
        {
            DeleteSessionRoot(sessionRoot);
            var status = string.IsNullOrEmpty(password) ? ModPackImportPrepareStatus.PasswordRequired : ModPackImportPrepareStatus.InvalidPassword;
            return new ModPackImportPrepareResult(status, status == ModPackImportPrepareStatus.PasswordRequired ? "This archive requires a password." : "The archive password is incorrect.", PasswordArchivePath: ex.Message);
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidFormatException or ArchiveOperationException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            DeleteSessionRoot(sessionRoot);
            logger.Warning(ex, "Unable to prepare local mod pack {PackagePath}", packagePath);
            return new ModPackImportPrepareResult(ModPackImportPrepareStatus.InvalidArchive, $"Unable to read this mod pack: {ex.Message}");
        }
    }

    public async Task<ModPackImportResult> ImportAsync(
        Guid sessionId,
        IReadOnlyCollection<ModPackImportSelection> selections,
        bool applyAuthorLoadOrder,
        IProgress<ModPackImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!sessions.TryGetValue(sessionId, out var session))
        {
            throw new InvalidOperationException("The local mod-pack import session was not found.");
        }

        var selectionMap = selections
            .GroupBy(selection => selection.ItemId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last().Action, StringComparer.OrdinalIgnoreCase);
        var executable = session.Plan.Items
            .Where(item => item.State is ModPackImportItemState.New or ModPackImportItemState.AlreadyInstalled)
            .ToArray();
        var results = new List<ModPackImportItemResult>();
        var retainedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            for (var index = 0; index < executable.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = executable[index];
                var action = selectionMap.GetValueOrDefault(
                    item.Id,
                    item.State == ModPackImportItemState.AlreadyInstalled ? ModPackImportAction.Skip : ModPackImportAction.Install);
                progress?.Report(new ModPackImportProgress($"Importing {item.DisplayName}...", index, executable.Length, item.Id, item.DisplayName));

                if (action == ModPackImportAction.Skip)
                {
                    results.Add(new ModPackImportItemResult(item.Id, ModPackImportItemResultState.Skipped, "Skipped."));
                    if (item.State == ModPackImportItemState.AlreadyInstalled)
                    {
                        retainedIds.Add(item.Id);
                    }
                    continue;
                }

                if (item.State == ModPackImportItemState.AlreadyInstalled && action != ModPackImportAction.Replace)
                {
                    results.Add(new ModPackImportItemResult(item.Id, ModPackImportItemResultState.Failed, "Choose Replace to overwrite an installed mod."));
                    continue;
                }

                var metadata = new ModPackageSourceMetadata(
                    Platform: "LocalArchive",
                    ModPackId: $"local-{session.Plan.PackageSha256}",
                    ModPackSessionId: sessionId,
                    ModPackName: session.Plan.PackageName);
                var installResult = await packageInstaller.InstallPreparedDirectoryAsync(
                    new PreparedModPackageInstallRequest(
                        item.PayloadDirectory,
                        session.Plan.PackagePath,
                        applicationPathService.GetPaths().ModsDirectory,
                        item.Id,
                        item.DisplayName,
                        item.Version,
                        Source: metadata,
                        ExistingInstallPolicy: action == ModPackImportAction.Replace
                            ? ModPackageExistingInstallPolicy.Replace
                            : ModPackageExistingInstallPolicy.Reject,
                        ExistingRootPath: item.ExistingRootPath),
                    cancellationToken).ConfigureAwait(false);
                if (installResult.Success)
                {
                    retainedIds.Add(item.Id);
                    results.Add(new ModPackImportItemResult(
                        item.Id,
                        action == ModPackImportAction.Replace ? ModPackImportItemResultState.Replaced : ModPackImportItemResultState.Installed,
                        installResult.Message));
                }
                else
                {
                    results.Add(new ModPackImportItemResult(item.Id, ModPackImportItemResultState.Failed, installResult.Message));
                }

                progress?.Report(new ModPackImportProgress(installResult.Message, index + 1, executable.Length, item.Id, item.DisplayName));
            }

            if (applyAuthorLoadOrder && session.Plan.AuthorLoadOrder.Count > 0 && retainedIds.Count > 0)
            {
                await ApplyAuthorOrderAsync(session.Plan, retainedIds, cancellationToken).ConfigureAwait(false);
            }

            var installedCount = results.Count(result => result.State == ModPackImportItemResultState.Installed);
            var replacedCount = results.Count(result => result.State == ModPackImportItemResultState.Replaced);
            var skippedCount = results.Count(result => result.State == ModPackImportItemResultState.Skipped);
            var failedCount = results.Count(result => result.State == ModPackImportItemResultState.Failed);
            var message = $"Imported {installedCount}, replaced {replacedCount}, skipped {skippedCount}, failed {failedCount}.";
            return new ModPackImportResult(sessionId, installedCount, replacedCount, skippedCount, failedCount, results, message);
        }
        finally
        {
            await DiscardAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
        }
    }

    public Task DiscardAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (sessions.TryRemove(sessionId, out var session))
        {
            DeleteSessionRoot(session.RootDirectory);
        }

        return Task.CompletedTask;
    }

    private async Task ApplyAuthorOrderAsync(ModPackImportPlan plan, HashSet<string> retainedIds, CancellationToken cancellationToken)
    {
        var installed = await modCatalogService.LoadInstalledModsAsync(cancellationToken).ConfigureAwait(false);
        var itemByOrderName = plan.Items
            .Where(item => retainedIds.Contains(item.Id))
            .SelectMany(item => new[] { item.NativeOrderName, item.Id }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => (Name: value!, item.Id)))
            .GroupBy(pair => pair.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Id, StringComparer.OrdinalIgnoreCase);
        var orderedPackIds = plan.AuthorLoadOrder
            .Where(itemByOrderName.ContainsKey)
            .Select(name => itemByOrderName[name])
            .Concat(plan.Items.Where(item => retainedIds.Contains(item.Id)).Select(item => item.Id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var merged = installed
            .OrderBy(mod => mod.LoadOrder)
            .Select(mod => mod.Id)
            .Where(id => !retainedIds.Contains(id))
            .Concat(orderedPackIds)
            .ToArray();
        await modCatalogService.SaveLoadOrderAsync(merged, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExtractArchiveAsync(
        string archivePath,
        string destinationRoot,
        string? password,
        ExtractionLimits limits,
        CancellationToken cancellationToken)
    {
        await using var archiveStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = ArchiveFactory.OpenArchive(
            archiveStream,
            SharpCompress.Readers.ReaderOptions.ForExternalStream with
            {
                Password = password,
                LeaveStreamOpen = true
            });
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.IsDirectory || string.IsNullOrWhiteSpace(entry.Key))
            {
                continue;
            }

            limits.EntryCount++;
            if (limits.EntryCount > MaxEntryCount)
            {
                throw new ImportLimitException($"The archive contains more than {MaxEntryCount:N0} files.");
            }

            var destinationPath = GetSafeDestination(destinationRoot, entry.Key);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            using var source = entry.OpenEntryStream();
            await using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
            var buffer = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                limits.ExpandedBytes += read;
                if (limits.ExpandedBytes > MaxExpandedBytes)
                {
                    throw new ImportLimitException("The archive expands beyond the 50 GiB safety limit.");
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static string[] FindModRoots(string searchRoot) => Directory
        .EnumerateFiles(searchRoot, "mod.manifest", SearchOption.AllDirectories)
        .Select(Path.GetDirectoryName)
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .Select(path => Path.GetFullPath(path!))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static NativeManifest ReadNativeManifest(string manifestPath)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 1024 * 1024
        };
        using var stream = File.OpenRead(manifestPath);
        using var reader = XmlReader.Create(stream, settings);
        var document = XDocument.Load(reader, LoadOptions.None);
        var info = document.Root?.Element("info") ?? throw new InvalidDataException("Missing kcd_mod/info element.");
        return new NativeManifest(
            info.Element("name")?.Value.Trim() ?? string.Empty,
            info.Element("version")?.Value.Trim() ?? string.Empty,
            info.Element("author")?.Value.Trim() ?? string.Empty);
    }

    private string GetSessionsRoot()
    {
        var paths = applicationPathService.GetPaths();
        return Path.Combine(paths.CacheDirectory ?? Path.Combine(paths.DataDirectory, "Cache"), "ModPackImports");
    }

    private void CleanStaleSessions()
    {
        try
        {
            var root = GetSessionsRoot();
            if (!Directory.Exists(root))
            {
                return;
            }

            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                if (Directory.GetLastWriteTimeUtc(directory) < DateTime.UtcNow.AddDays(-1))
                {
                    DeleteSessionRoot(directory);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.Warning(ex, "Unable to clean stale local mod-pack import sessions");
        }
    }

    private void DeleteSessionRoot(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.Warning(ex, "Unable to remove local mod-pack import session {SessionRoot}", path);
        }
    }

    private static string GetSafeDestination(string rootDirectory, string entryPath)
    {
        var normalizedEntry = entryPath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var destinationPath = Path.GetFullPath(Path.Combine(rootDirectory, normalizedEntry));
        if (!IsPathInsideDirectory(rootDirectory, destinationPath))
        {
            throw new InvalidDataException($"Archive entry escapes the staging directory: {entryPath}");
        }

        return destinationPath;
    }

    private static bool IsPathInsideDirectory(string rootDirectory, string candidatePath)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(candidatePath).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsArchive(string path) => ArchiveExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static bool IsArchivePasswordFailure(Exception exception, string archivePath, string? password)
    {
        if (exception is SharpCompress.Common.CryptographicException
            || exception.InnerException is SharpCompress.Common.CryptographicException)
        {
            return true;
        }

        return !string.IsNullOrEmpty(password)
            && string.Equals(Path.GetExtension(archivePath), ".rar", StringComparison.OrdinalIgnoreCase)
            && exception is ArchiveOperationException or InvalidFormatException
            && exception.Message.Contains("Unknown Rar Header", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeId(string value)
    {
        var normalized = new string(value.Trim().Select(character => char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : '-').ToArray());
        return string.IsNullOrWhiteSpace(normalized) ? Guid.NewGuid().ToString("N") : normalized;
    }

    private sealed record ImportSession(string RootDirectory, ModPackImportPlan Plan);

    private sealed record NativeManifest(string Name, string Version, string Author);

    private sealed class ExtractionLimits
    {
        public int EntryCount { get; set; }

        public long ExpandedBytes { get; set; }
    }

    private sealed class ImportLimitException(string message) : Exception(message);
}
