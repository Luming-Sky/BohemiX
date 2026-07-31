using System.Security.Cryptography;
using System.Text;
using BohemiX.Core.Models;
using Serilog;

namespace BohemiX.Infrastructure.Services;

/// <summary>
/// Installs a release-provided, hash-manifested native bundle into the per-user
/// native directory used by the VFS service. Development builds simply skip this
/// step when the bundle is absent.
/// </summary>
public sealed class BundledNativeDependencyInstaller
{
    private const string ManifestFileName = "SHA256SUMS";
    private readonly ILogger logger;
    private readonly string sourceRoot;

    public BundledNativeDependencyInstaller(ILogger logger, string? sourceRoot = null)
    {
        this.logger = logger.ForContext<BundledNativeDependencyInstaller>();
        this.sourceRoot = sourceRoot ?? Path.Combine(AppContext.BaseDirectory, "native");
    }

    public async Task InstallIfPresentAsync(
        ApplicationPaths paths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);

        if (!Directory.Exists(sourceRoot))
        {
            return;
        }

        var manifestPath = Path.Combine(sourceRoot, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            throw new InvalidDataException(
                $"The bundled native directory exists but does not contain {ManifestFileName}.");
        }

        var entries = await ReadManifestAsync(sourceRoot, manifestPath, cancellationToken);
        Directory.CreateDirectory(paths.NativeDirectory);
        var stagingRoot = Path.Combine(paths.NativeDirectory, $".bohemix.install.{Guid.NewGuid():N}");
        var stagedFiles = new List<StagedFile>();
        try
        {
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourcePath = ResolveContainedPath(sourceRoot, entry.RelativePath);
                var sourceHash = await ComputeSha256Async(sourcePath, cancellationToken);
                if (!string.Equals(sourceHash, entry.Hash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"The bundled native dependency '{entry.RelativePath}' failed SHA-256 verification.");
                }

                var destinationPath = ResolveContainedPath(paths.NativeDirectory, entry.RelativePath);
                if (File.Exists(destinationPath)
                    && string.Equals(
                        await ComputeSha256Async(destinationPath, cancellationToken),
                        entry.Hash,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var stagedPath = ResolveContainedPath(stagingRoot, entry.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(stagedPath)!);
                await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                await using (var target = new FileStream(stagedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await source.CopyToAsync(target, cancellationToken);
                }

                var copiedHash = await ComputeSha256Async(stagedPath, cancellationToken);
                if (!string.Equals(copiedHash, entry.Hash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"The bundled native dependency '{entry.RelativePath}' failed SHA-256 verification.");
                }

                stagedFiles.Add(new StagedFile(entry.RelativePath, stagedPath, destinationPath));
            }

            foreach (var stagedFile in stagedFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.GetDirectoryName(stagedFile.DestinationPath)!);
                File.Move(stagedFile.StagedPath, stagedFile.DestinationPath, overwrite: true);
                logger.Information("Installed verified native dependency {RelativePath}.", stagedFile.RelativePath);
            }
        }
        finally
        {
            if (Directory.Exists(stagingRoot))
            {
                Directory.Delete(stagingRoot, recursive: true);
            }
        }
    }

    private static async Task<IReadOnlyList<ManifestEntry>> ReadManifestAsync(
        string sourceRoot,
        string manifestPath,
        CancellationToken cancellationToken)
    {
        var entries = new List<ManifestEntry>();
        var resolvedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lines = await File.ReadAllLinesAsync(manifestPath, Encoding.UTF8, cancellationToken);
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separator = line.IndexOfAny([' ', '\t']);
            if (separator <= 0)
            {
                throw new InvalidDataException($"Invalid native dependency manifest line: '{rawLine}'.");
            }

            var hash = line[..separator].Trim();
            var relativePath = line[(separator + 1)..].Trim().TrimStart('*');
            if (hash.Length != 64 || !hash.All(Uri.IsHexDigit) || string.IsNullOrWhiteSpace(relativePath))
            {
                throw new InvalidDataException($"Invalid native dependency manifest line: '{rawLine}'.");
            }

            var resolved = ResolveContainedPath(sourceRoot, relativePath);
            if (string.Equals(Path.GetFileName(resolved), ManifestFileName, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(resolved))
            {
                throw new InvalidDataException($"Invalid native dependency manifest path: '{relativePath}'.");
            }

            if (!resolvedPaths.Add(resolved))
            {
                throw new InvalidDataException($"Duplicate native dependency manifest path: '{relativePath}'.");
            }

            entries.Add(new ManifestEntry(relativePath, hash.ToUpperInvariant()));
        }

        if (entries.Count == 0)
        {
            throw new InvalidDataException("The native dependency manifest does not contain any files.");
        }

        return entries;
    }

    private static string ResolveContainedPath(string root, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException($"Native dependency path must be relative: '{relativePath}'.");
        }

        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        if (!fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Native dependency path escapes its bundle: '{relativePath}'.");
        }

        return fullPath;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    private sealed record ManifestEntry(string RelativePath, string Hash);

    private sealed record StagedFile(string RelativePath, string StagedPath, string DestinationPath);
}
