using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using BohemiX.Core.Services;
using Serilog;
using SkiaSharp;

namespace BohemiX.App.Services;

public sealed class ModCoverCacheService : IModCoverCacheService, IDisposable
{
    private const string CacheVersion = "v3";
    private const int MaxCachedCoverWidth = 768;
    private const int JpegQuality = 88;
    private const string UserAgent = "BohemiX/0.1";
    private static readonly TimeSpan CoverRequestTimeout = TimeSpan.FromSeconds(20);

    private readonly IApplicationPathService applicationPathService;
    private readonly ILogger logger;
    private readonly HttpClient httpClient;

    public ModCoverCacheService(IApplicationPathService applicationPathService)
        : this(applicationPathService, Log.Logger)
    {
    }

    public ModCoverCacheService(IApplicationPathService applicationPathService, ILogger logger)
    {
        this.applicationPathService = applicationPathService;
        this.logger = logger.ForContext<ModCoverCacheService>();
        httpClient = new HttpClient
        {
            Timeout = CoverRequestTimeout
        };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("image/png"));
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("image/jpeg"));
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*", 0.5));
    }

    public string? GetCachedCoverPath(int modId)
    {
        try
        {
            var cacheDirectory = Path.Combine(applicationPathService.GetPaths().DataDirectory, "mod-covers");
            if (!Directory.Exists(cacheDirectory))
            {
                return null;
            }

            return Directory.EnumerateFiles(cacheDirectory, $"{modId}-*", SearchOption.TopDirectoryOnly)
                .Where(path => Path.GetExtension(path) is ".jpg" or ".jpeg" or ".png")
                .Where(IsSupportedBitmapFile)
                .Select(path => new FileInfo(path))
                .Where(file => file.Length > 0)
                .OrderByDescending(file => file.Name.StartsWith($"{modId}-{CacheVersion}-", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(file => file.LastWriteTimeUtc)
                .Select(file => file.FullName)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.Debug(ex, "Unable to read cached cover for Nexus mod {ModId}", modId);
            return null;
        }
    }

    public async Task<string?> CacheCoverAsync(
        int modId,
        string? imageUrl,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(imageUrl) || !Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        try
        {
            var paths = applicationPathService.GetPaths();
            var cacheDirectory = Path.Combine(paths.DataDirectory, "mod-covers");
            Directory.CreateDirectory(cacheDirectory);
            var candidateUris = BuildImageCandidates(uri);
            foreach (var candidateUri in candidateUris)
            {
                var candidateCacheKey = CreateCacheKey(candidateUri);
                if (TryFindExistingCachePath(cacheDirectory, modId, candidateCacheKey, out var existingCachePath))
                {
                    return existingCachePath;
                }
            }

            foreach (var candidateUri in candidateUris)
            {
                try
                {
                    var cachedPath = await CacheCandidateAsync(
                        cacheDirectory,
                        modId,
                        candidateUri,
                        cancellationToken).ConfigureAwait(false);
                    if (cachedPath is not null)
                    {
                        return cachedPath;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidOperationException or TaskCanceledException)
                {
                    logger.Debug(ex, "Unable to cache Nexus mod cover candidate {ModId} from {ImageUrl}", modId, candidateUri);
                }
            }

            logger.Warning("Unable to cache Nexus mod cover {ModId} from {ImageUrl}", modId, imageUrl);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            logger.Warning(ex, "Unable to cache Nexus mod cover {ModId} from {ImageUrl}", modId, imageUrl);
            return null;
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.Warning(ex, "Timed out while caching Nexus mod cover {ModId} from {ImageUrl}", modId, imageUrl);
            return null;
        }
    }

    public void Dispose()
    {
        httpClient.Dispose();
    }

    internal static Uri[] BuildImageCandidates(Uri sourceUri)
    {
        const string thumbnailSegment = "/images/thumbnails/";
        const string fullImageSegment = "/images/";

        if (!sourceUri.Host.EndsWith("nexusmods.com", StringComparison.OrdinalIgnoreCase))
        {
            return [sourceUri];
        }

        var thumbnailIndex = sourceUri.AbsolutePath.IndexOf(thumbnailSegment, StringComparison.OrdinalIgnoreCase);
        if (thumbnailIndex < 0)
        {
            return [sourceUri];
        }

        var builder = new UriBuilder(sourceUri)
        {
            Path = string.Concat(
                sourceUri.AbsolutePath.AsSpan(0, thumbnailIndex),
                fullImageSegment,
                sourceUri.AbsolutePath.AsSpan(thumbnailIndex + thumbnailSegment.Length))
        };

        return [builder.Uri, sourceUri];
    }

    private async Task<string?> CacheCandidateAsync(
        string cacheDirectory,
        int modId,
        Uri uri,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient
            .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var cacheKey = CreateCacheKey(uri);
        var temporaryPath = Path.Combine(cacheDirectory, $"{modId}-{cacheKey}-{Guid.NewGuid():N}.tmp");
        var cacheSourcePath = temporaryPath;

        try
        {
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var output = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             81920,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            var fileInfo = new FileInfo(temporaryPath);
            if (!fileInfo.Exists || fileInfo.Length == 0)
            {
                return null;
            }

            var optimizedPathBase = temporaryPath + ".optimized";
            var optimized = await Task.Run(
                    () => TryCreateOptimizedCover(temporaryPath, optimizedPathBase),
                    cancellationToken)
                .ConfigureAwait(false);
            if (optimized is null)
            {
                return null;
            }

            SafeDelete(temporaryPath);
            cacheSourcePath = optimized.Value.Path;
            var cachePath = Path.Combine(cacheDirectory, $"{modId}-{cacheKey}{optimized.Value.Extension}");
            File.Move(cacheSourcePath, cachePath, overwrite: true);
            cacheSourcePath = string.Empty;
            return cachePath;
        }
        finally
        {
            SafeDelete(temporaryPath);
            if (!string.IsNullOrWhiteSpace(cacheSourcePath))
            {
                SafeDelete(cacheSourcePath);
            }
        }
    }

    private static string CreateCacheKey(Uri uri)
    {
        var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(uri.AbsoluteUri))).ToLowerInvariant();
        return $"{CacheVersion}-{hash}";
    }

    internal static (int Width, int Height) GetOptimizedDimensions(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Image dimensions must be positive.");
        }

        if (width <= MaxCachedCoverWidth)
        {
            return (width, height);
        }

        var scaledHeight = Math.Max(1, (int)Math.Round(height * (MaxCachedCoverWidth / (double)width)));
        return (MaxCachedCoverWidth, scaledHeight);
    }

    private static (string Path, string Extension)? TryCreateOptimizedCover(
        string inputPath,
        string outputPathBase)
    {
        string? outputPath = null;
        try
        {
            using var sourceBitmap = SKBitmap.Decode(inputPath);
            if (sourceBitmap is null || sourceBitmap.Width <= 0 || sourceBitmap.Height <= 0)
            {
                return null;
            }

            var dimensions = GetOptimizedDimensions(sourceBitmap.Width, sourceBitmap.Height);
            using var resizedBitmap = dimensions.Width == sourceBitmap.Width
                ? null
                : sourceBitmap.Resize(
                    new SKImageInfo(
                        dimensions.Width,
                        dimensions.Height,
                        sourceBitmap.ColorType,
                        sourceBitmap.AlphaType),
                    SKFilterQuality.Medium);
            var outputBitmap = resizedBitmap ?? sourceBitmap;
            var hasAlpha = outputBitmap.AlphaType is not SKAlphaType.Opaque;
            var extension = hasAlpha ? ".png" : ".jpg";
            var format = hasAlpha ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg;
            var quality = hasAlpha ? 100 : JpegQuality;
            outputPath = outputPathBase + extension;

            using var image = SKImage.FromBitmap(outputBitmap);
            using var data = image.Encode(format, quality);
            if (data is null)
            {
                return null;
            }

            using var output = File.Open(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            data.SaveTo(output);
            return output.Length > 0 ? (outputPath, extension) : null;
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or InvalidOperationException
                                   or ArgumentException)
        {
            if (outputPath is not null)
            {
                SafeDelete(outputPath);
            }

            return null;
        }
    }

    private static bool TryFindExistingCachePath(
        string cacheDirectory,
        int modId,
        string cacheKey,
        out string cachePath)
    {
        foreach (var extension in new[] { ".jpg", ".jpeg", ".png" })
        {
            var candidate = Path.Combine(cacheDirectory, $"{modId}-{cacheKey}{extension}");
            if (!File.Exists(candidate))
            {
                continue;
            }

            var fileInfo = new FileInfo(candidate);
            if (fileInfo.Length > 0 && IsSupportedBitmapFile(candidate))
            {
                cachePath = candidate;
                return true;
            }

            SafeDelete(candidate);
        }

        cachePath = string.Empty;
        return false;
    }

    private static string? GetImageExtension(string imagePath, Uri uri, string? mediaType)
    {
        var detectedExtension = DetectSupportedImageExtension(imagePath);
        if (detectedExtension is not null)
        {
            return detectedExtension;
        }

        if (IsWebpFile(imagePath) || IsAvifFile(imagePath))
        {
            return null;
        }

        var contentExtension = mediaType?.ToLowerInvariant() switch
        {
            "image/jpeg" or "image/jpg" => ".jpg",
            "image/png" => ".png",
            _ => null
        };

        if (contentExtension is not null)
        {
            return contentExtension;
        }

        var extension = Path.GetExtension(uri.AbsolutePath);
        return extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
            ? extension
            : null;
    }

    private static bool IsConvertibleImageFile(string path)
    {
        return IsWebpFile(path) || IsAvifFile(path);
    }

    private static bool TryConvertToPng(string inputPath, string outputPath)
    {
        try
        {
            using var bitmap = SKBitmap.Decode(inputPath);
            if (bitmap is null)
            {
                return false;
            }

            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            if (data is null)
            {
                return false;
            }

            using var output = File.Open(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
            data.SaveTo(output);
            return output.Length > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            SafeDelete(outputPath);
            return false;
        }
    }

    private static bool IsSupportedBitmapFile(string path)
    {
        return DetectSupportedImageExtension(path) is not null;
    }

    private static string? DetectSupportedImageExtension(string path)
    {
        Span<byte> header = stackalloc byte[12];
        var bytesRead = ReadHeader(path, header);
        if (bytesRead >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
        {
            return ".jpg";
        }

        if (bytesRead >= 8
            && header[0] == 0x89
            && header[1] == 0x50
            && header[2] == 0x4E
            && header[3] == 0x47
            && header[4] == 0x0D
            && header[5] == 0x0A
            && header[6] == 0x1A
            && header[7] == 0x0A)
        {
            return ".png";
        }

        return null;
    }

    private static bool IsWebpFile(string path)
    {
        Span<byte> header = stackalloc byte[12];
        var bytesRead = ReadHeader(path, header);
        return bytesRead >= 12
            && header[0] == 0x52
            && header[1] == 0x49
            && header[2] == 0x46
            && header[3] == 0x46
            && header[8] == 0x57
            && header[9] == 0x45
            && header[10] == 0x42
            && header[11] == 0x50;
    }

    private static bool IsAvifFile(string path)
    {
        Span<byte> header = stackalloc byte[12];
        var bytesRead = ReadHeader(path, header);
        return bytesRead >= 12
            && header[4] == 0x66
            && header[5] == 0x74
            && header[6] == 0x79
            && header[7] == 0x70
            && header[8] == 0x61
            && header[9] == 0x76
            && header[10] == 0x69
            && header[11] == 0x66;
    }

    private static int ReadHeader(string path, Span<byte> header)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return stream.Read(header);
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static void SafeDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
