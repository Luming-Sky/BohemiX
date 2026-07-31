using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using BohemiX.Core.Models;
using BohemiX.Core.Services;
using Serilog;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace BohemiX.Infrastructure.Services;

public sealed class ModPackageInstaller : IModPackageInstaller
{
    private const string ManifestFileName = "bohemix.mod.json";
    private const string ReceiptFileName = "bohemix.install.json";
    private const string TransactionDirectoryName = ".transactions";

    private readonly ILogger logger;
    private readonly SemaphoreSlim installGate = new(1, 1);

    public ModPackageInstaller(ILogger logger)
    {
        this.logger = logger.ForContext<ModPackageInstaller>();
    }

    public async Task<ModPackageInstallResult> InstallAsync(
        ModPackageInstallRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await installGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await InstallCoreAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            installGate.Release();
        }
    }

    public async Task<ModPackageInstallResult> InstallPreparedDirectoryAsync(
        PreparedModPackageInstallRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await installGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await InstallPreparedDirectoryCoreAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            installGate.Release();
        }
    }

    private async Task<ModPackageInstallResult> InstallPreparedDirectoryCoreAsync(
        PreparedModPackageInstallRequest request,
        CancellationToken cancellationToken)
    {
        var transactionId = Guid.NewGuid();
        string? transactionRoot = null;
        string? installedRoot = null;
        string? backupRoot = null;
        var committed = false;

        try
        {
            if (!Directory.Exists(request.PayloadDirectory))
            {
                return new ModPackageInstallResult(false, null, $"Prepared payload not found: {request.PayloadDirectory}", transactionId);
            }

            if (!File.Exists(request.SourcePackagePath))
            {
                return new ModPackageInstallResult(false, null, $"Source package not found: {request.SourcePackagePath}", transactionId);
            }

            var modsDirectory = Path.GetFullPath(request.ModsDirectory);
            Directory.CreateDirectory(modsDirectory);
            var transactionsRoot = Path.Combine(modsDirectory, TransactionDirectoryName);
            Directory.CreateDirectory(transactionsRoot);
            RecoverAbandonedTransactions(transactionsRoot, modsDirectory);

            transactionRoot = Path.Combine(transactionsRoot, transactionId.ToString("N"));
            var stagingRoot = Path.Combine(transactionRoot, "payload");
            Directory.CreateDirectory(stagingRoot);
            await CopyDirectoryAsync(request.PayloadDirectory, stagingRoot, cancellationToken).ConfigureAwait(false);
            await WritePreparedManifestAsync(stagingRoot, request, cancellationToken).ConfigureAwait(false);
            var receiptPath = Path.Combine(stagingRoot, ReceiptFileName);
            if (File.Exists(receiptPath))
            {
                File.Delete(receiptPath);
            }

            var receipt = await BuildReceiptAsync(transactionId, request.SourcePackagePath, stagingRoot, cancellationToken)
                .ConfigureAwait(false);
            await WriteReceiptAsync(stagingRoot, receipt, cancellationToken).ConfigureAwait(false);
            await VerifyPreparedPayloadAsync(stagingRoot, receipt, cancellationToken).ConfigureAwait(false);

            if (request.ExistingInstallPolicy == ModPackageExistingInstallPolicy.Replace)
            {
                installedRoot = Path.GetFullPath(request.ExistingRootPath
                    ?? throw new InvalidDataException("Replacing a mod requires its existing root path."));
                if (!IsDirectChildDirectory(modsDirectory, installedRoot) || !Directory.Exists(installedRoot))
                {
                    throw new InvalidDataException("The existing mod directory is missing or outside the managed Mods directory.");
                }

                backupRoot = Path.Combine(transactionRoot, "backup");
            }
            else
            {
                installedRoot = EnsureUniqueDirectory(Path.Combine(modsDirectory, SanitizePathSegment(request.ModId)));
            }

            await WriteTransactionJournalAsync(
                transactionRoot,
                new InstallTransactionJournal(transactionId, "Prepared", installedRoot, DateTimeOffset.UtcNow, backupRoot),
                cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            if (backupRoot is not null)
            {
                Directory.Move(installedRoot, backupRoot);
            }

            Directory.Move(stagingRoot, installedRoot);
            committed = true;
            var committedReceipt = await ReadReceiptAsync(installedRoot, cancellationToken).ConfigureAwait(false);
            await VerifyCommittedPayloadAsync(installedRoot, committedReceipt, cancellationToken).ConfigureAwait(false);
            TryDeleteDirectory(transactionRoot, "completed prepared mod install transaction");

            return new ModPackageInstallResult(
                true,
                installedRoot,
                request.ExistingInstallPolicy == ModPackageExistingInstallPolicy.Replace
                    ? $"Replaced {request.DisplayName}."
                    : $"Installed {request.DisplayName}.",
                transactionId);
        }
        catch (OperationCanceledException)
        {
            RollBackPrepared(transactionRoot, installedRoot, backupRoot, committed);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or JsonException or System.Security.Cryptography.CryptographicException)
        {
            var rolledBack = RollBackPrepared(transactionRoot, installedRoot, backupRoot, committed);
            logger.Warning(ex, "Prepared mod install transaction {TransactionId} failed for {PackagePath}", transactionId, request.SourcePackagePath);
            return new ModPackageInstallResult(false, null, $"Unable to install prepared package: {ex.Message}", transactionId, rolledBack);
        }
    }

    private async Task<ModPackageInstallResult> InstallCoreAsync(
        ModPackageInstallRequest request,
        CancellationToken cancellationToken)
    {
        var transactionId = Guid.NewGuid();
        string? transactionRoot = null;
        string? installedRoot = null;
        var committed = false;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(request.PackagePath))
            {
                return new ModPackageInstallResult(
                    false,
                    null,
                    $"Package not found: {request.PackagePath}",
                    transactionId);
            }

            var modsDirectory = Path.GetFullPath(request.ModsDirectory);
            Directory.CreateDirectory(modsDirectory);
            var transactionsRoot = Path.Combine(modsDirectory, TransactionDirectoryName);
            Directory.CreateDirectory(transactionsRoot);
            RecoverAbandonedTransactions(transactionsRoot, modsDirectory);

            transactionRoot = Path.Combine(transactionsRoot, transactionId.ToString("N"));
            var stagingRoot = Path.Combine(transactionRoot, "payload");
            Directory.CreateDirectory(stagingRoot);
            await WriteTransactionJournalAsync(
                transactionRoot,
                new InstallTransactionJournal(transactionId, "Preparing", null, DateTimeOffset.UtcNow),
                cancellationToken).ConfigureAwait(false);

            await ExtractPackageAsync(request.PackagePath, stagingRoot, cancellationToken).ConfigureAwait(false);
            NormalizePackageRoot(stagingRoot);
            await WriteManifestAsync(stagingRoot, request, cancellationToken).ConfigureAwait(false);
            var receipt = await BuildReceiptAsync(transactionId, request.PackagePath, stagingRoot, cancellationToken)
                .ConfigureAwait(false);
            await WriteReceiptAsync(stagingRoot, receipt, cancellationToken).ConfigureAwait(false);
            await VerifyPreparedPayloadAsync(stagingRoot, receipt, cancellationToken).ConfigureAwait(false);

            installedRoot = EnsureUniqueDirectory(Path.Combine(modsDirectory, SanitizePathSegment(request.ModId)));
            await WriteTransactionJournalAsync(
                transactionRoot,
                new InstallTransactionJournal(transactionId, "Prepared", installedRoot, DateTimeOffset.UtcNow),
                cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(stagingRoot, installedRoot);
            committed = true;

            var committedReceipt = await ReadReceiptAsync(installedRoot, cancellationToken).ConfigureAwait(false);
            await VerifyCommittedPayloadAsync(installedRoot, committedReceipt, cancellationToken).ConfigureAwait(false);
            TryDeleteDirectory(transactionRoot, "completed mod install transaction");
            logger.Information(
                "Committed mod install transaction {TransactionId} for {PackagePath} to {InstalledRoot}",
                transactionId,
                request.PackagePath,
                installedRoot);
            return new ModPackageInstallResult(
                true,
                installedRoot,
                $"Installed {request.DisplayName}.",
                transactionId);
        }
        catch (InvalidDataException ex)
        {
            var rolledBack = RollBack(transactionRoot, installedRoot, committed);
            logger.Warning(ex, "Mod install transaction {TransactionId} rejected invalid package {PackagePath}", transactionId, request.PackagePath);
            return new ModPackageInstallResult(false, null, $"Invalid package archive: {ex.Message}", transactionId, rolledBack);
        }
        catch (InvalidFormatException ex)
        {
            var rolledBack = RollBack(transactionRoot, installedRoot, committed);
            logger.Warning(ex, "Mod install transaction {TransactionId} rejected invalid package {PackagePath}", transactionId, request.PackagePath);
            return new ModPackageInstallResult(false, null, $"Invalid package archive: {ex.Message}", transactionId, rolledBack);
        }
        catch (SharpCompress.Common.CryptographicException ex)
        {
            var rolledBack = RollBack(transactionRoot, installedRoot, committed);
            logger.Warning(ex, "Mod install transaction {TransactionId} could not decrypt package {PackagePath}", transactionId, request.PackagePath);
            return new ModPackageInstallResult(false, null, $"Invalid package archive: {ex.Message}", transactionId, rolledBack);
        }
        catch (OperationCanceledException)
        {
            RollBack(transactionRoot, installedRoot, committed);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.Cryptography.CryptographicException or JsonException)
        {
            var rolledBack = RollBack(transactionRoot, installedRoot, committed);
            logger.Warning(ex, "Mod install transaction {TransactionId} failed for {PackagePath}", transactionId, request.PackagePath);
            return new ModPackageInstallResult(false, null, $"Unable to install package: {ex.Message}", transactionId, rolledBack);
        }
    }

    private static async Task ExtractPackageAsync(
        string packagePath,
        string stagingRoot,
        CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(packagePath);
        if (string.Equals(extension, ".zip", StringComparison.OrdinalIgnoreCase))
        {
            await ExtractZipAsync(packagePath, stagingRoot, cancellationToken).ConfigureAwait(false);
        }
        else if (IsSupportedArchiveExtension(extension))
        {
            await ExtractArchiveAsync(packagePath, stagingRoot, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var fileName = Path.GetFileName(packagePath);
            var destination = Path.Combine(stagingRoot, string.IsNullOrWhiteSpace(fileName) ? "package.bin" : fileName);
            File.Copy(packagePath, destination, overwrite: true);
        }
    }

    private static async Task ExtractArchiveAsync(
        string packagePath,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        await using var packageStream = new FileStream(
            packagePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = ReaderFactory.OpenReader(packageStream);

        while (reader.MoveToNextEntry())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.Entry.IsDirectory || string.IsNullOrWhiteSpace(reader.Entry.Key))
            {
                continue;
            }

            var destinationPath = GetSafeArchiveDestination(destinationDirectory, reader.Entry.Key);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            using var source = reader.OpenEntryStream();
            await using var destination = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ExtractZipAsync(
        string packagePath,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            var destinationPath = GetSafeArchiveDestination(destinationDirectory, entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await using var source = entry.Open();
            await using var destination = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string GetSafeArchiveDestination(string rootDirectory, string entryPath)
    {
        var destinationPath = Path.GetFullPath(Path.Combine(rootDirectory, entryPath));
        if (!IsPathInsideDirectory(rootDirectory, destinationPath))
        {
            throw new InvalidDataException($"Archive entry escapes the mod directory: {entryPath}");
        }

        return destinationPath;
    }

    private static void NormalizePackageRoot(string stagingRoot)
    {
        var entries = Directory.GetFileSystemEntries(stagingRoot);
        if (entries.Length != 1 || !Directory.Exists(entries[0]))
        {
            return;
        }

        var nestedRoot = entries[0];
        var liftRoot = stagingRoot + ".lift";
        Directory.CreateDirectory(liftRoot);
        foreach (var directory in Directory.GetDirectories(nestedRoot))
        {
            Directory.Move(directory, Path.Combine(liftRoot, Path.GetFileName(directory)));
        }

        foreach (var file in Directory.GetFiles(nestedRoot))
        {
            File.Move(file, Path.Combine(liftRoot, Path.GetFileName(file)));
        }

        Directory.Delete(stagingRoot, recursive: true);
        Directory.Move(liftRoot, stagingRoot);
    }

    private static async Task WriteManifestAsync(
        string installedRoot,
        ModPackageInstallRequest request,
        CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(installedRoot, ManifestFileName);
        var manifest = new
        {
            id = request.ModId,
            displayName = request.DisplayName,
            version = request.Version,
            isEnabled = request.IsEnabled,
            source = request.Source is null
                ? null
                : new
                {
                    platform = request.Source.Platform,
                    nexusModId = request.Source.NexusModId,
                    nexusFileId = request.Source.NexusFileId,
                    md5 = request.Source.Md5,
                    modPackId = request.Source.ModPackId,
                    modPackRevision = request.Source.ModPackRevision,
                    modPackSessionId = request.Source.ModPackSessionId,
                    modPackName = request.Source.ModPackName
                }
        };

        await using var stream = new FileStream(manifestPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
        await JsonSerializer.SerializeAsync(stream, manifest, new JsonSerializerOptions { WriteIndented = true }, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task WritePreparedManifestAsync(
        string installedRoot,
        PreparedModPackageInstallRequest request,
        CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(installedRoot, ManifestFileName);
        var manifest = new
        {
            id = request.ModId,
            displayName = request.DisplayName,
            version = request.Version,
            isEnabled = request.IsEnabled,
            source = request.Source is null
                ? null
                : new
                {
                    platform = request.Source.Platform,
                    nexusModId = request.Source.NexusModId,
                    nexusFileId = request.Source.NexusFileId,
                    md5 = request.Source.Md5,
                    modPackId = request.Source.ModPackId,
                    modPackRevision = request.Source.ModPackRevision,
                    modPackSessionId = request.Source.ModPackSessionId,
                    modPackName = request.Source.ModPackName
                }
        };

        await using var stream = new FileStream(manifestPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
        await JsonSerializer.SerializeAsync(stream, manifest, new JsonSerializerOptions { WriteIndented = true }, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task CopyDirectoryAsync(string sourceRoot, string destinationRoot, CancellationToken cancellationToken)
    {
        var normalizedSource = Path.GetFullPath(sourceRoot);
        foreach (var directory in Directory.EnumerateDirectories(normalizedSource, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidDataException($"The prepared payload contains a reparse point: {Path.GetRelativePath(normalizedSource, directory)}");
            }

            Directory.CreateDirectory(Path.Combine(destinationRoot, Path.GetRelativePath(normalizedSource, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(normalizedSource, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidDataException($"The prepared payload contains a reparse point: {Path.GetRelativePath(normalizedSource, file)}");
            }

            var destination = Path.Combine(destinationRoot, Path.GetRelativePath(normalizedSource, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
            await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<InstallReceipt> BuildReceiptAsync(
        Guid transactionId,
        string packagePath,
        string stagingRoot,
        CancellationToken cancellationToken)
    {
        var files = Directory.EnumerateFiles(stagingRoot, "*", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith(ReceiptFileName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => Path.GetRelativePath(stagingRoot, path), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (files.Length == 0)
        {
            throw new InvalidDataException("The package contains no installable files.");
        }

        long totalBytes = 0;
        using var payloadHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(stagingRoot, file).Replace('\\', '/');
            var fileInfo = new FileInfo(file);
            totalBytes += fileInfo.Length;
            payloadHash.AppendData(System.Text.Encoding.UTF8.GetBytes(relativePath.ToUpperInvariant()));
            payloadHash.AppendData([0]);
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var fileHash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            payloadHash.AppendData(fileHash);
        }

        await using var packageStream = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var packageHash = await SHA256.HashDataAsync(packageStream, cancellationToken).ConfigureAwait(false);
        return new InstallReceipt(
            transactionId,
            Convert.ToHexString(packageHash),
            Convert.ToHexString(payloadHash.GetHashAndReset()),
            files.Length,
            totalBytes,
            DateTimeOffset.UtcNow);
    }

    private static async Task WriteReceiptAsync(string stagingRoot, InstallReceipt receipt, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            Path.Combine(stagingRoot, ReceiptFileName),
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous);
        await JsonSerializer.SerializeAsync(stream, receipt, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static async Task<InstallReceipt> ReadReceiptAsync(string installedRoot, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            Path.Combine(installedRoot, ReceiptFileName),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<InstallReceipt>(stream, cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The committed install receipt is empty.");
    }

    private static Task VerifyPreparedPayloadAsync(string stagingRoot, InstallReceipt receipt, CancellationToken cancellationToken) =>
        VerifyCommittedPayloadAsync(stagingRoot, receipt, cancellationToken);

    private static async Task VerifyCommittedPayloadAsync(
        string root,
        InstallReceipt receipt,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(Path.Combine(root, ManifestFileName)) || !File.Exists(Path.Combine(root, ReceiptFileName)))
        {
            throw new InvalidDataException("The prepared mod is missing its manifest or transaction receipt.");
        }

        var verification = await BuildPayloadVerificationAsync(root, cancellationToken).ConfigureAwait(false);
        if (verification.FileCount != receipt.FileCount
            || verification.TotalBytes != receipt.TotalBytes
            || !StringComparer.OrdinalIgnoreCase.Equals(verification.PayloadSha256, receipt.PayloadSha256))
        {
            throw new InvalidDataException("The installed payload changed during transaction commit.");
        }
    }

    private static async Task<PayloadVerification> BuildPayloadVerificationAsync(string root, CancellationToken cancellationToken)
    {
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith(ReceiptFileName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => Path.GetRelativePath(root, path), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        long totalBytes = 0;
        using var payloadHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attributes = File.GetAttributes(file);
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidDataException($"The mod payload contains a reparse point: {Path.GetRelativePath(root, file)}");
            }

            var relativePath = Path.GetRelativePath(root, file).Replace('\\', '/');
            var info = new FileInfo(file);
            totalBytes += info.Length;
            payloadHash.AppendData(System.Text.Encoding.UTF8.GetBytes(relativePath.ToUpperInvariant()));
            payloadHash.AppendData([0]);
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            payloadHash.AppendData(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        }

        return new PayloadVerification(files.Length, totalBytes, Convert.ToHexString(payloadHash.GetHashAndReset()));
    }

    private static async Task WriteTransactionJournalAsync(
        string transactionRoot,
        InstallTransactionJournal journal,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(transactionRoot);
        var journalPath = Path.Combine(transactionRoot, "transaction.json");
        await using var stream = new FileStream(journalPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
        await JsonSerializer.SerializeAsync(stream, journal, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private void RecoverAbandonedTransactions(string transactionsRoot, string modsDirectory)
    {
        foreach (var transactionDirectory in Directory.EnumerateDirectories(transactionsRoot))
        {
            try
            {
                var journalPath = Path.Combine(transactionDirectory, "transaction.json");
                InstallTransactionJournal? journal = null;
                if (File.Exists(journalPath))
                {
                    journal = JsonSerializer.Deserialize<InstallTransactionJournal>(File.ReadAllText(journalPath));
                }

                if (journal?.TargetPath is { Length: > 0 } targetPath
                    && !IsPathInsideDirectory(modsDirectory, Path.GetFullPath(targetPath)))
                {
                    logger.Warning("Ignored unsafe abandoned transaction target {TargetPath}", targetPath);
                    continue;
                }

                if (journal?.BackupPath is { Length: > 0 } backupPath
                    && Directory.Exists(backupPath)
                    && journal.TargetPath is { Length: > 0 } restoreTarget
                    && IsDirectChildDirectory(modsDirectory, Path.GetFullPath(restoreTarget))
                    && !Directory.Exists(restoreTarget))
                {
                    Directory.Move(backupPath, restoreTarget);
                }

                TryDeleteDirectory(transactionDirectory, "abandoned mod install transaction");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            {
                logger.Warning(ex, "Unable to reconcile abandoned mod transaction {TransactionDirectory}", transactionDirectory);
            }
        }
    }

    private bool RollBack(string? transactionRoot, string? installedRoot, bool committed)
    {
        var attempted = false;
        if (committed && installedRoot is not null && Directory.Exists(installedRoot))
        {
            attempted = true;
            TryDeleteDirectory(installedRoot, "committed mod payload during rollback");
        }

        if (transactionRoot is not null && Directory.Exists(transactionRoot))
        {
            attempted = true;
            TryDeleteDirectory(transactionRoot, "mod install transaction during rollback");
        }

        return attempted;
    }

    private bool RollBackPrepared(string? transactionRoot, string? installedRoot, string? backupRoot, bool committed)
    {
        var attempted = false;
        if (committed && installedRoot is not null && Directory.Exists(installedRoot))
        {
            attempted = true;
            TryDeleteDirectory(installedRoot, "prepared mod payload during rollback");
        }

        if (backupRoot is not null && Directory.Exists(backupRoot) && installedRoot is not null)
        {
            attempted = true;
            Directory.Move(backupRoot, installedRoot);
        }

        if (transactionRoot is not null && Directory.Exists(transactionRoot))
        {
            attempted = true;
            TryDeleteDirectory(transactionRoot, "prepared mod install transaction during rollback");
        }

        return attempted;
    }

    private static string EnsureUniqueDirectory(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            return directoryPath;
        }

        for (var index = 2; index < 1000; index++)
        {
            var candidate = $"{directoryPath}-{index}";
            if (!Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return $"{directoryPath}-{Guid.NewGuid():N}";
    }

    private static bool IsPathInsideDirectory(string rootDirectory, string candidatePath)
    {
        var normalizedRoot = EnsureTrailingDirectorySeparator(Path.GetFullPath(rootDirectory));
        var normalizedCandidate = Path.GetFullPath(candidatePath);
        return normalizedCandidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDirectChildDirectory(string rootDirectory, string candidatePath) =>
        string.Equals(
            Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidatePath))),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory)),
            StringComparison.OrdinalIgnoreCase);

    private static string EnsureTrailingDirectorySeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;

    private static string SanitizePathSegment(string value)
    {
        var sanitized = new string(value
            .Trim()
            .Select(character => char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : '-')
            .ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? Guid.NewGuid().ToString("N") : sanitized;
    }

    private static bool IsSupportedArchiveExtension(string extension) =>
        extension.Equals(".7z", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".rar", StringComparison.OrdinalIgnoreCase);

    private void TryDeleteDirectory(string? directoryPath, string reason)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(directoryPath) && Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.Warning(ex, "Unable to remove {DirectoryPath} while cleaning {Reason}", directoryPath, reason);
        }
    }

    private sealed record InstallTransactionJournal(
        Guid TransactionId,
        string State,
        string? TargetPath,
        DateTimeOffset UpdatedAtUtc,
        string? BackupPath = null);

    private sealed record InstallReceipt(
        Guid TransactionId,
        string PackageSha256,
        string PayloadSha256,
        int FileCount,
        long TotalBytes,
        DateTimeOffset PreparedAtUtc);

    private sealed record PayloadVerification(int FileCount, long TotalBytes, string PayloadSha256);
}
