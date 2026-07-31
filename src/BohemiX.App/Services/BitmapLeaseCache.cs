using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace BohemiX.App.Services;

internal interface IBitmapLeaseCache
{
    BitmapLease? Acquire(string source, int decodePixelWidth = 0, int decodePixelHeight = 0);
    void TrimUnused(long targetPixelBytes = 0);
}

internal sealed class BitmapLease : IDisposable
{
    private Action? release;

    internal BitmapLease(Bitmap bitmap, Action release)
    {
        Bitmap = bitmap;
        this.release = release;
    }

    public Bitmap Bitmap { get; }

    public void Dispose() => System.Threading.Interlocked.Exchange(ref release, null)?.Invoke();
}

internal sealed class BitmapLeaseCache : IBitmapLeaseCache, IDisposable
{
    // Keep the shared decoded-image cache bounded so hidden pages do not retain
    // a large native pixel surface after their leases have been released.
    internal const long DefaultPixelBudget = 32L * 1024 * 1024;
    private const string DuplicateStripeUri = "avares://BohemiX.App/Assets/striped-draft-card-surface.png";
    private const string CanonicalStripeUri = "avares://BohemiX.App/Assets/striped-background-page.png";

    private readonly object gate = new();
    private readonly Dictionary<CacheKey, Entry> entries = [];
    private readonly Func<string, int, int, Bitmap?> loader;
    private readonly Func<Bitmap, long> sizeProvider;
    private readonly Action<Bitmap> bitmapDisposer;
    private readonly long pixelBudget;
    private long totalPixelBytes;
    private long accessSequence;
    private bool disposed;

    public BitmapLeaseCache(long pixelBudget = DefaultPixelBudget)
        : this(
            pixelBudget,
            LoadBitmap,
            bitmap => checked((long)bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4),
            bitmap => bitmap.Dispose())
    {
    }

    internal BitmapLeaseCache(
        long pixelBudget,
        Func<string, int, int, Bitmap?> loader,
        Func<Bitmap, long>? sizeProvider = null,
        Action<Bitmap>? bitmapDisposer = null)
    {
        this.pixelBudget = Math.Max(0, pixelBudget);
        this.loader = loader ?? throw new ArgumentNullException(nameof(loader));
        this.sizeProvider = sizeProvider ?? (bitmap => checked((long)bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4));
        this.bitmapDisposer = bitmapDisposer ?? (bitmap => bitmap.Dispose());
    }

    internal long TotalPixelBytes
    {
        get
        {
            lock (gate)
            {
                return totalPixelBytes;
            }
        }
    }

    internal int EntryCount
    {
        get
        {
            lock (gate)
            {
                return entries.Count;
            }
        }
    }

    public BitmapLease? Acquire(string source, int decodePixelWidth = 0, int decodePixelHeight = 0)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        var key = new CacheKey(
            NormalizeSource(source),
            Math.Max(0, decodePixelWidth),
            Math.Max(0, decodePixelHeight));

        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            if (!entries.TryGetValue(key, out var entry))
            {
                var bitmap = loader(key.Source, key.DecodePixelWidth, key.DecodePixelHeight);
                if (bitmap is null)
                {
                    return null;
                }

                entry = new Entry(bitmap, Math.Max(0, this.sizeProvider(bitmap)));
                entries.Add(key, entry);
                totalPixelBytes += entry.PixelBytes;
            }

            entry.ReferenceCount++;
            entry.LastAccess = ++accessSequence;
            TrimUnlocked();
            return new BitmapLease(entry.Bitmap, () => Release(key, entry));
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            foreach (var entry in entries.Values)
            {
                bitmapDisposer(entry.Bitmap);
            }

            entries.Clear();
            totalPixelBytes = 0;
        }
    }

    public void TrimUnused(long targetPixelBytes = 0)
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            TrimUnlocked(Math.Max(0, targetPixelBytes));
        }
    }

    internal static string NormalizeSource(string source)
    {
        var normalized = source.Trim().Replace('\\', '/');
        if (normalized.StartsWith("/Assets/", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "avares://BohemiX.App" + normalized;
        }

        return string.Equals(normalized, DuplicateStripeUri, StringComparison.OrdinalIgnoreCase)
            ? CanonicalStripeUri
            : normalized;
    }

    private void Release(CacheKey key, Entry leasedEntry)
    {
        lock (gate)
        {
            if (disposed || !entries.TryGetValue(key, out var entry) || !ReferenceEquals(entry, leasedEntry))
            {
                return;
            }

            entry.ReferenceCount = Math.Max(0, entry.ReferenceCount - 1);
            entry.LastAccess = ++accessSequence;
            TrimUnlocked();
        }
    }

    private void TrimUnlocked() => TrimUnlocked(pixelBudget);

    private void TrimUnlocked(long targetPixelBytes)
    {
        while (totalPixelBytes > targetPixelBytes)
        {
            CacheKey? oldestKey = null;
            Entry? oldestEntry = null;
            foreach (var pair in entries)
            {
                if (pair.Value.ReferenceCount != 0 || oldestEntry is not null && pair.Value.LastAccess >= oldestEntry.LastAccess)
                {
                    continue;
                }

                oldestKey = pair.Key;
                oldestEntry = pair.Value;
            }

            if (oldestKey is null || oldestEntry is null)
            {
                return;
            }

            entries.Remove(oldestKey.Value);
            totalPixelBytes -= oldestEntry.PixelBytes;
            bitmapDisposer(oldestEntry.Bitmap);
        }
    }

    private static Bitmap? LoadBitmap(string source, int decodePixelWidth, int decodePixelHeight)
    {
        try
        {
            using var stream = OpenSource(source);
            if (decodePixelWidth > 0)
            {
                return Bitmap.DecodeToWidth(stream, decodePixelWidth, Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
            }

            if (decodePixelHeight > 0)
            {
                return Bitmap.DecodeToHeight(stream, decodePixelHeight, Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
            }

            return new Bitmap(stream);
        }
        catch
        {
            return null;
        }
    }

    private static Stream OpenSource(string source)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme.Equals("avares", StringComparison.OrdinalIgnoreCase))
        {
            return AssetLoader.Open(uri);
        }

        return File.OpenRead(source);
    }

    private readonly record struct CacheKey(string Source, int DecodePixelWidth, int DecodePixelHeight);

    private sealed class Entry(Bitmap bitmap, long pixelBytes)
    {
        public Bitmap Bitmap { get; } = bitmap;
        public long PixelBytes { get; } = pixelBytes;
        public int ReferenceCount { get; set; }
        public long LastAccess { get; set; }
    }
}

internal static class SharedBitmapLeaseCache
{
    private static readonly BitmapLeaseCache Cache = new();

    public static IBitmapLeaseCache Instance => Cache;

    public static void TrimUnused(long targetPixelBytes = 0) => Cache.TrimUnused(targetPixelBytes);

    public static void Shutdown() => Cache.Dispose();
}
