using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BohemiX.Core.Models.Saves;

namespace BohemiX.Infrastructure.Services.Saves;

internal sealed class ChunkedSnapshotStore
{
    private const int ManifestSchemaVersion = 1;
    private const int MinimumChunkSize = 256 * 1024;
    private const int TargetChunkSize = 1024 * 1024;
    private const int MaximumChunkSize = 4 * 1024 * 1024;
    private const int ObjectHeaderSize = 14;
    private const byte ObjectVersion = 1;
    private const byte RawCodec = 0;
    private const byte BrotliCodec = 1;
    private static readonly byte[] ObjectMagic = "BXCH"u8.ToArray();
    private static readonly ulong[] GearTable = CreateGearTable();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly string snapshotsRoot;
    private readonly string objectsRoot;
    private readonly string manifestsRoot;
    private readonly string stagingRoot;

    public ChunkedSnapshotStore(string snapshotsRoot, string stagingRoot)
    {
        this.snapshotsRoot = NormalizeDirectoryPath(snapshotsRoot);
        this.stagingRoot = NormalizeDirectoryPath(stagingRoot);
        objectsRoot = Path.Combine(this.snapshotsRoot, "objects", "v1");
        manifestsRoot = Path.Combine(this.snapshotsRoot, "manifests");
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(objectsRoot);
        Directory.CreateDirectory(manifestsRoot);
    }

    public async Task<SnapshotCaptureResult> CaptureAsync(
        string sourceRoot,
        Guid profileId,
        Guid snapshotId,
        IProgress<SaveImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        EnsureDirectories();
        var files = EnumerateSafeFiles(sourceRoot);
        var totalBytes = files.Sum(file => file.Length);
        var manifestFiles = new List<ChunkManifestFile>(files.Count);
        var verifiedObjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var directoryHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long processedBytes = 0;

        for (var fileIndex = 0; fileIndex < files.Count; fileIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = files[fileIndex];
            var manifestFile = await CaptureSourceFileAsync(
                file,
                verifiedObjects,
                bytes =>
                {
                    processedBytes += bytes;
                    progress?.Report(new SaveImportProgress(
                        "Snapshotting", fileIndex + 1, files.Count, processedBytes, totalBytes, file.RelativePath));
                },
                cancellationToken).ConfigureAwait(false);
            var fileHashText = manifestFile.Sha256;
            directoryHash.AppendData(Encoding.UTF8.GetBytes(file.RelativePath.ToUpperInvariant()));
            directoryHash.AppendData([0]);
            directoryHash.AppendData(Convert.FromHexString(fileHashText));
            manifestFiles.Add(manifestFile);
        }

        var fingerprint = Convert.ToHexString(directoryHash.GetHashAndReset());
        var manifest = new ChunkSnapshotManifest(
            ManifestSchemaVersion,
            snapshotId,
            profileId,
            fingerprint,
            manifestFiles.Count,
            totalBytes,
            manifestFiles);
        var relativePath = GetManifestRelativePath(profileId, snapshotId);
        await WriteManifestAtomicallyAsync(relativePath, manifest, cancellationToken).ConfigureAwait(false);
        return new SnapshotCaptureResult(relativePath, fingerprint, manifestFiles.Count, totalBytes);
    }

    public async Task<NodeCaptureResult> CaptureFileAsync(
        string sourceRoot,
        string sourcePath,
        Guid profileId,
        Guid nodeId,
        CancellationToken cancellationToken)
    {
        EnsureDirectories();
        var root = NormalizeDirectoryPath(sourceRoot);
        var fullPath = Path.GetFullPath(sourcePath);
        EnsureInsideRoot(fullPath, root);
        var info = new FileInfo(fullPath);
        if (!info.Exists)
        {
            throw new FileNotFoundException("Save node source was not found.", fullPath);
        }

        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"Save node source is a reparse point: {fullPath}");
        }

        var relativePath = NormalizeEntryPath(Path.GetRelativePath(root, fullPath));
        var source = new SafeSourceFile(fullPath, relativePath, info.Length);
        var manifestFile = await CaptureSourceFileAsync(
            source,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            null,
            cancellationToken).ConfigureAwait(false);
        var fingerprint = ComputeManifestFingerprint([manifestFile]);
        var manifest = new ChunkSnapshotManifest(
            ManifestSchemaVersion,
            nodeId,
            profileId,
            fingerprint,
            1,
            manifestFile.Length,
            [manifestFile]);
        var manifestRelativePath = GetNodeManifestRelativePath(profileId, nodeId);
        await WriteManifestAtomicallyAsync(manifestRelativePath, manifest, cancellationToken).ConfigureAwait(false);
        return new NodeCaptureResult(
            manifestRelativePath,
            relativePath,
            manifestFile.Sha256,
            manifestFile.Length,
            fingerprint);
    }

    public async Task<NodeCaptureResult> CreateDerivedNodeManifestAsync(
        string sourceManifestRelativePath,
        string sourceRelativePath,
        Guid profileId,
        Guid nodeId,
        CancellationToken cancellationToken)
    {
        var sourceManifest = await ReadManifestAsync(sourceManifestRelativePath, cancellationToken).ConfigureAwait(false);
        var normalized = NormalizeEntryPath(sourceRelativePath);
        var sourceFile = sourceManifest.Files.SingleOrDefault(
            file => StringComparer.OrdinalIgnoreCase.Equals(file.Path, normalized))
            ?? throw new FileNotFoundException("The selected save file is not present in the snapshot manifest.", normalized);

        await WriteFileAsync(sourceFile, Stream.Null, cancellationToken).ConfigureAwait(false);
        var fingerprint = ComputeManifestFingerprint([sourceFile]);
        var manifest = new ChunkSnapshotManifest(
            ManifestSchemaVersion,
            nodeId,
            profileId,
            fingerprint,
            1,
            sourceFile.Length,
            [sourceFile]);
        var manifestRelativePath = GetNodeManifestRelativePath(profileId, nodeId);
        await WriteManifestAtomicallyAsync(manifestRelativePath, manifest, cancellationToken).ConfigureAwait(false);
        return new NodeCaptureResult(
            manifestRelativePath,
            sourceFile.Path,
            sourceFile.Sha256,
            sourceFile.Length,
            fingerprint);
    }

    public async Task<NodeManifestEntry> MaterializeFileAsync(
        string manifestRelativePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var manifest = await ReadManifestAsync(manifestRelativePath, cancellationToken).ConfigureAwait(false);
        if (manifest.Files.Count != 1)
        {
            throw new InvalidDataException("A save node manifest must contain exactly one file.");
        }

        var file = manifest.Files[0];
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);
        await using var output = new FileStream(
            destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await WriteFileAsync(file, output, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        return new NodeManifestEntry(file.Path, file.Sha256, file.Length);
    }

    public async Task<IReadOnlyList<NodeManifestEntry>> GetManifestEntriesAsync(
        string manifestRelativePath,
        CancellationToken cancellationToken)
    {
        var manifest = await ReadManifestAsync(manifestRelativePath, cancellationToken).ConfigureAwait(false);
        return manifest.Files
            .Select(file => new NodeManifestEntry(file.Path, file.Sha256, file.Length))
            .ToList();
    }

    public async Task<string> MaterializeAsync(
        string manifestRelativePath,
        string destinationRoot,
        IProgress<SaveImportProgress>? progress,
        string stage,
        CancellationToken cancellationToken)
    {
        var manifest = await ReadManifestAsync(manifestRelativePath, cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(destinationRoot);
        long processedBytes = 0;
        for (var fileIndex = 0; fileIndex < manifest.Files.Count; fileIndex++)
        {
            var file = manifest.Files[fileIndex];
            var destination = GetSafeDestinationPath(destinationRoot, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var output = new FileStream(
                destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await WriteFileAsync(file, output, cancellationToken).ConfigureAwait(false);
            processedBytes += file.Length;
            progress?.Report(new SaveImportProgress(
                stage, fileIndex + 1, manifest.Files.Count, processedBytes, manifest.TotalBytes, file.Path));
        }

        return manifest.ContentFingerprint;
    }

    public async Task<long> WriteFilesToArchiveAsync(
        string manifestRelativePath,
        ZipArchive archive,
        string prefix,
        IProgress<SaveImportProgress>? progress,
        long processedBytes,
        long totalBytes,
        CancellationToken cancellationToken)
    {
        var manifest = await ReadManifestAsync(manifestRelativePath, cancellationToken).ConfigureAwait(false);
        for (var fileIndex = 0; fileIndex < manifest.Files.Count; fileIndex++)
        {
            var file = manifest.Files[fileIndex];
            var entryPath = $"{prefix.TrimEnd('/')}/{file.Path}";
            var entry = archive.CreateEntry(entryPath, CompressionLevel.Optimal);
            await using var target = entry.Open();
            await WriteFileAsync(file, target, cancellationToken).ConfigureAwait(false);
            processedBytes += file.Length;
            progress?.Report(new SaveImportProgress(
                "Exporting", fileIndex + 1, manifest.Files.Count, processedBytes, totalBytes, entryPath));
        }

        return processedBytes;
    }

    public async Task ValidateManifestAsync(string manifestRelativePath, CancellationToken cancellationToken)
    {
        _ = await ReadManifestAsync(manifestRelativePath, cancellationToken).ConfigureAwait(false);
    }

    public async Task<long> EstimateAdditionalBytesAsync(string sourceRoot, CancellationToken cancellationToken)
    {
        var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long additionalBytes = 0;
        foreach (var file in EnumerateSafeFiles(sourceRoot))
        {
            await using var input = new FileStream(
                file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[MaximumChunkSize];
            var buffered = 0;
            var reachedEnd = false;
            while (!reachedEnd || buffered > 0)
            {
                while (!reachedEnd && buffered < buffer.Length)
                {
                    var read = await input.ReadAsync(buffer.AsMemory(buffered, buffer.Length - buffered), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        reachedEnd = true;
                        break;
                    }

                    buffered += read;
                }

                if (buffered == 0)
                {
                    break;
                }

                var chunkLength = FindChunkBoundary(buffer.AsSpan(0, buffered), reachedEnd);
                var hash = Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, chunkLength)));
                if (missing.Add(hash) && !File.Exists(GetObjectPath(hash)))
                {
                    // Raw length is a conservative estimate because objects may be compressed.
                    additionalBytes += chunkLength + ObjectHeaderSize;
                }

                buffered -= chunkLength;
                if (buffered > 0)
                {
                    Buffer.BlockCopy(buffer, chunkLength, buffer, 0, buffered);
                }
            }
        }

        return additionalBytes;
    }

    public async Task<IReadOnlyList<ChunkPackageFile>> GetPackageFilesAsync(
        string manifestRelativePath,
        string prefix,
        CancellationToken cancellationToken)
    {
        var manifest = await ReadManifestAsync(manifestRelativePath, cancellationToken).ConfigureAwait(false);
        return manifest.Files.Select(file => new ChunkPackageFile(
            $"{prefix.TrimEnd('/')}/{file.Path}", file.Sha256, file.Length)).ToList();
    }

    public string GetManifestFullPath(string manifestRelativePath)
    {
        var normalized = NormalizeManifestRelativePath(manifestRelativePath);
        var fullPath = Path.GetFullPath(Path.Combine(snapshotsRoot, normalized.Replace('/', Path.DirectorySeparatorChar)));
        EnsureInsideRoot(fullPath, manifestsRoot);
        return fullPath;
    }

    public Task DeleteManifestAsync(string manifestRelativePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = GetManifestFullPath(manifestRelativePath);
        if (!File.Exists(fullPath))
        {
            return Task.CompletedTask;
        }

        var trashDirectory = Path.Combine(stagingRoot, $"delete-manifest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(trashDirectory);
        var trashPath = Path.Combine(trashDirectory, Path.GetFileName(fullPath));
        File.Move(fullPath, trashPath);
        try
        {
            File.Delete(trashPath);
            Directory.Delete(trashDirectory);
        }
        catch
        {
            if (!File.Exists(fullPath) && File.Exists(trashPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                File.Move(trashPath, fullPath);
            }

            throw;
        }

        return Task.CompletedTask;
    }

    public async Task CollectGarbageAsync(
        IReadOnlyCollection<string> liveManifestRelativePaths,
        CancellationToken cancellationToken)
    {
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in liveManifestRelativePaths)
        {
            var manifest = await ReadManifestAsync(path, cancellationToken).ConfigureAwait(false);
            foreach (var chunk in manifest.Files.SelectMany(file => file.Chunks))
            {
                referenced.Add(chunk.Hash);
            }
        }

        if (!Directory.Exists(objectsRoot))
        {
            return;
        }

        foreach (var objectPath in Directory.EnumerateFiles(objectsRoot, "*.bxc", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hash = Path.GetFileNameWithoutExtension(objectPath);
            if (!referenced.Contains(hash))
            {
                File.Delete(objectPath);
            }
        }
    }

    public async Task<long> GetPhysicalBytesAsync(
        IReadOnlyCollection<string> manifestRelativePaths,
        CancellationToken cancellationToken)
    {
        var objects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var relativePath in manifestRelativePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var manifest = await ReadManifestAsync(relativePath, cancellationToken).ConfigureAwait(false);
            var manifestPath = GetManifestFullPath(relativePath);
            if (File.Exists(manifestPath))
            {
                total += new FileInfo(manifestPath).Length;
            }

            foreach (var chunk in manifest.Files.SelectMany(file => file.Chunks))
            {
                objects.Add(chunk.Hash);
            }
        }

        foreach (var hash in objects)
        {
            var path = GetObjectPath(hash);
            if (File.Exists(path))
            {
                total += new FileInfo(path).Length;
            }
        }

        return total;
    }

    public async Task ReconcileOrphanManifestsAsync(
        IReadOnlyCollection<string> liveManifestRelativePaths,
        CancellationToken cancellationToken)
    {
        var live = liveManifestRelativePaths
            .Select(NormalizeManifestRelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(manifestsRoot))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(manifestsRoot, "*.json", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(snapshotsRoot, path).Replace('\\', '/');
            if (!live.Contains(relative))
            {
                await DeleteManifestAsync(relative, cancellationToken).ConfigureAwait(false);
            }
        }

        await CollectGarbageAsync(liveManifestRelativePaths, cancellationToken).ConfigureAwait(false);
    }

    private async Task<long> EnsureObjectAsync(
        string hash,
        ReadOnlyMemory<byte> raw,
        HashSet<string> verifiedObjects,
        CancellationToken cancellationToken)
    {
        var objectPath = GetObjectPath(hash);
        if (File.Exists(objectPath))
        {
            if (verifiedObjects.Add(hash))
            {
                _ = await ReadObjectAsync(hash, raw.Length, cancellationToken).ConfigureAwait(false);
            }

            return new FileInfo(objectPath).Length;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(objectPath)!);
        Directory.CreateDirectory(stagingRoot);
        var temp = Path.Combine(stagingRoot, $"object-{Guid.NewGuid():N}.tmp");
        try
        {
            var compressed = Compress(raw.Span);
            var useCompressed = compressed.Length <= raw.Length * 0.9;
            var payload = useCompressed ? compressed : raw.ToArray();
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true))
            {
                var header = new byte[ObjectHeaderSize];
                ObjectMagic.CopyTo(header, 0);
                header[4] = ObjectVersion;
                header[5] = useCompressed ? BrotliCodec : RawCodec;
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(6, 4), raw.Length);
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(10, 4), payload.Length);
                await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            try
            {
                File.Move(temp, objectPath);
            }
            catch (IOException) when (File.Exists(objectPath))
            {
                File.Delete(temp);
                _ = await ReadObjectAsync(hash, raw.Length, cancellationToken).ConfigureAwait(false);
            }

            verifiedObjects.Add(hash);
            return new FileInfo(objectPath).Length;
        }
        catch
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }

            throw;
        }
    }

    private async Task<ChunkManifestFile> CaptureSourceFileAsync(
        SafeSourceFile file,
        HashSet<string> verifiedObjects,
        Action<int>? chunkCaptured,
        CancellationToken cancellationToken)
    {
        var chunks = new List<ChunkManifestChunk>();
        using var fileHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var input = new FileStream(
            file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[MaximumChunkSize];
        var buffered = 0;
        var reachedEnd = false;
        while (!reachedEnd || buffered > 0)
        {
            while (!reachedEnd && buffered < buffer.Length)
            {
                var read = await input.ReadAsync(buffer.AsMemory(buffered, buffer.Length - buffered), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    reachedEnd = true;
                    break;
                }

                buffered += read;
            }

            if (buffered == 0)
            {
                break;
            }

            var chunkLength = FindChunkBoundary(buffer.AsSpan(0, buffered), reachedEnd);
            var chunk = buffer.AsMemory(0, chunkLength);
            fileHash.AppendData(chunk.Span);
            var hash = Convert.ToHexString(SHA256.HashData(chunk.Span));
            var storedBytes = await EnsureObjectAsync(hash, chunk, verifiedObjects, cancellationToken).ConfigureAwait(false);
            chunks.Add(new ChunkManifestChunk(hash, chunkLength, storedBytes));
            chunkCaptured?.Invoke(chunkLength);

            buffered -= chunkLength;
            if (buffered > 0)
            {
                Buffer.BlockCopy(buffer, chunkLength, buffer, 0, buffered);
            }
        }

        return new ChunkManifestFile(
            file.RelativePath,
            file.Length,
            Convert.ToHexString(fileHash.GetHashAndReset()),
            chunks);
    }

    private async Task WriteFileAsync(ChunkManifestFile file, Stream destination, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long written = 0;
        foreach (var chunk in file.Chunks)
        {
            var raw = await ReadObjectAsync(chunk.Hash, chunk.Length, cancellationToken).ConfigureAwait(false);
            await destination.WriteAsync(raw, cancellationToken).ConfigureAwait(false);
            hash.AppendData(raw);
            written += raw.Length;
        }

        if (written != file.Length
            || !StringComparer.OrdinalIgnoreCase.Equals(Convert.ToHexString(hash.GetHashAndReset()), file.Sha256))
        {
            throw new InvalidDataException($"Snapshot file verification failed: {file.Path}");
        }
    }

    private async Task<byte[]> ReadObjectAsync(string hash, int expectedLength, CancellationToken cancellationToken)
    {
        var path = GetObjectPath(hash);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
        var header = new byte[ObjectHeaderSize];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        if (!header.AsSpan(0, ObjectMagic.Length).SequenceEqual(ObjectMagic)
            || header[4] != ObjectVersion)
        {
            throw new InvalidDataException($"Invalid snapshot object header: {hash}");
        }

        var rawLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(6, 4));
        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(10, 4));
        if (rawLength != expectedLength
            || rawLength <= 0
            || rawLength > MaximumChunkSize
            || payloadLength <= 0
            || payloadLength > MaximumChunkSize
            || payloadLength != stream.Length - ObjectHeaderSize)
        {
            throw new InvalidDataException($"Invalid snapshot object length: {hash}");
        }

        var payload = new byte[payloadLength];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        byte[] raw;
        switch (header[5])
        {
            case RawCodec:
                raw = payload;
                break;
            case BrotliCodec:
                await using (var source = new MemoryStream(payload, writable: false))
                await using (var brotli = new BrotliStream(source, CompressionMode.Decompress))
                {
                    raw = new byte[rawLength];
                    var offset = 0;
                    while (offset < raw.Length)
                    {
                        var read = await brotli.ReadAsync(raw.AsMemory(offset), cancellationToken).ConfigureAwait(false);
                        if (read == 0)
                        {
                            throw new InvalidDataException($"Truncated snapshot object: {hash}");
                        }

                        offset += read;
                    }

                    var extra = new byte[1];
                    if (await brotli.ReadAsync(extra, cancellationToken).ConfigureAwait(false) != 0)
                    {
                        throw new InvalidDataException($"Oversized snapshot object: {hash}");
                    }
                }

                break;
            default:
                throw new InvalidDataException($"Unsupported snapshot object codec: {header[5]}");
        }

        if (raw.Length != rawLength
            || !StringComparer.OrdinalIgnoreCase.Equals(Convert.ToHexString(SHA256.HashData(raw)), hash))
        {
            throw new InvalidDataException($"Snapshot object verification failed: {hash}");
        }

        return raw;
    }

    private async Task<ChunkSnapshotManifest> ReadManifestAsync(string manifestRelativePath, CancellationToken cancellationToken)
    {
        var path = GetManifestFullPath(manifestRelativePath);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
        var manifest = await JsonSerializer.DeserializeAsync<ChunkSnapshotManifest>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException($"Snapshot manifest is empty: {manifestRelativePath}");
        if (manifest.SchemaVersion != ManifestSchemaVersion
            || manifest.FileCount != manifest.Files.Count
            || manifest.TotalBytes != manifest.Files.Sum(file => file.Length))
        {
            throw new InvalidDataException($"Snapshot manifest is invalid: {manifestRelativePath}");
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            var normalized = NormalizeEntryPath(file.Path);
            if (!paths.Add(normalized)
                || file.Length < 0
                || file.Chunks.Sum(chunk => (long)chunk.Length) != file.Length
                || file.Chunks.Any(chunk => chunk.Length <= 0 || chunk.Length > MaximumChunkSize || !IsSha256(chunk.Hash)))
            {
                throw new InvalidDataException($"Snapshot manifest file is invalid: {file.Path}");
            }
        }

        return manifest;
    }

    private async Task WriteManifestAtomicallyAsync(
        string relativePath,
        ChunkSnapshotManifest manifest,
        CancellationToken cancellationToken)
    {
        var destination = GetManifestFullPath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        Directory.CreateDirectory(stagingRoot);
        var temp = Path.Combine(stagingRoot, $"manifest-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, true))
            {
                await JsonSerializer.SerializeAsync(stream, manifest, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temp, destination);
        }
        catch
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }

            throw;
        }
    }

    private string GetObjectPath(string hash)
    {
        if (!IsSha256(hash))
        {
            throw new InvalidDataException("Invalid snapshot object hash.");
        }

        var normalized = hash.ToUpperInvariant();
        return Path.Combine(objectsRoot, normalized[..2], normalized + ".bxc");
    }

    private static int FindChunkBoundary(ReadOnlySpan<byte> data, bool reachedEnd)
    {
        if (data.Length <= MinimumChunkSize && reachedEnd)
        {
            return data.Length;
        }

        var limit = Math.Min(data.Length, MaximumChunkSize);
        var normalLimit = Math.Min(limit, TargetChunkSize);
        ulong hash = 0;
        for (var i = MinimumChunkSize; i < normalLimit; i++)
        {
            hash = (hash << 1) + GearTable[data[i]];
            if ((hash & ((1UL << 21) - 1)) == 0)
            {
                return i + 1;
            }
        }

        for (var i = normalLimit; i < limit; i++)
        {
            hash = (hash << 1) + GearTable[data[i]];
            if ((hash & ((1UL << 19) - 1)) == 0)
            {
                return i + 1;
            }
        }

        return limit;
    }

    private static byte[] Compress(ReadOnlySpan<byte> raw)
    {
        using var output = new MemoryStream(raw.Length);
        using (var brotli = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            brotli.Write(raw);
        }

        return output.ToArray();
    }

    private static IReadOnlyList<SafeSourceFile> EnumerateSafeFiles(string root)
    {
        var normalizedRoot = NormalizeDirectoryPath(root);
        if (!Directory.Exists(normalizedRoot))
        {
            throw new DirectoryNotFoundException(normalizedRoot);
        }

        var result = new List<SafeSourceFile>();
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(normalizedRoot));
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException($"Snapshot source contains a reparse point: {entry.FullName}");
                }

                if (entry is DirectoryInfo childDirectory)
                {
                    pending.Push(childDirectory);
                }
                else if (entry is FileInfo file)
                {
                    var relative = NormalizeEntryPath(Path.GetRelativePath(normalizedRoot, file.FullName));
                    result.Add(new SafeSourceFile(file.FullName, relative, file.Length));
                }
                else
                {
                    throw new InvalidDataException($"Snapshot source contains an unsupported entry: {entry.FullName}");
                }
            }
        }

        return result.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string GetSafeDestinationPath(string root, string relativePath)
    {
        var normalized = NormalizeEntryPath(relativePath);
        var destination = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        EnsureInsideRoot(destination, root);
        return destination;
    }

    private static string GetManifestRelativePath(Guid profileId, Guid snapshotId) =>
        $"manifests/{profileId:D}/{snapshotId:D}.json";

    private static string GetNodeManifestRelativePath(Guid profileId, Guid nodeId) =>
        $"manifests/nodes/{profileId:D}/{nodeId:D}.json";

    private static string ComputeManifestFingerprint(IReadOnlyList<ChunkManifestFile> files)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files.OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(file.Path.ToUpperInvariant()));
            hash.AppendData([0]);
            hash.AppendData(Convert.FromHexString(file.Sha256));
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string NormalizeManifestRelativePath(string path)
    {
        var normalized = NormalizeEntryPath(path);
        if (!normalized.StartsWith("manifests/", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Snapshot manifest is outside the manifest root.");
        }

        return normalized;
    }

    private static string NormalizeEntryPath(string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        if (string.IsNullOrWhiteSpace(normalized)
            || Path.IsPathRooted(path)
            || normalized.Split('/').Any(part => part is "" or "." or ".."))
        {
            throw new InvalidDataException($"Unsafe snapshot path: {path}");
        }

        return normalized;
    }

    private static void EnsureInsideRoot(string path, string root)
    {
        var full = Path.GetFullPath(path);
        var rootFull = NormalizeDirectoryPath(root);
        if (!full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("Snapshot path is outside the managed root.");
        }
    }

    private static string NormalizeDirectoryPath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(character => Uri.IsHexDigit(character));

    private static ulong[] CreateGearTable()
    {
        var table = new ulong[256];
        ulong state = 0x9E3779B97F4A7C15UL;
        for (var i = 0; i < table.Length; i++)
        {
            state ^= state >> 12;
            state ^= state << 25;
            state ^= state >> 27;
            table[i] = state * 0x2545F4914F6CDD1DUL;
        }

        return table;
    }

    internal sealed record SnapshotCaptureResult(
        string ManifestRelativePath,
        string ContentFingerprint,
        int FileCount,
        long TotalBytes);

    internal sealed record NodeCaptureResult(
        string ManifestRelativePath,
        string RelativePath,
        string Sha256,
        long TotalBytes,
        string ContentFingerprint);

    internal sealed record NodeManifestEntry(string RelativePath, string Sha256, long Length);

    internal sealed record ChunkPackageFile(string Path, string Sha256, long Length);

    private sealed record SafeSourceFile(string FullName, string RelativePath, long Length);
    private sealed record ChunkSnapshotManifest(
        int SchemaVersion,
        Guid SnapshotId,
        Guid ProfileId,
        string ContentFingerprint,
        int FileCount,
        long TotalBytes,
        IReadOnlyList<ChunkManifestFile> Files);
    private sealed record ChunkManifestFile(
        string Path,
        long Length,
        string Sha256,
        IReadOnlyList<ChunkManifestChunk> Chunks);
    private sealed record ChunkManifestChunk(string Hash, int Length, long StoredBytes);
}
