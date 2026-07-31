using Avalonia.Media.Imaging;
using BohemiX.App.Services;

namespace BohemiX.App.Tests;

public sealed class BitmapLeaseCacheTests
{
    [Fact]
    public void NormalizeSource_MergesDuplicateStripeUris()
    {
        var draft = BitmapLeaseCache.NormalizeSource("avares://BohemiX.App/Assets/striped-draft-card-surface.png");
        var page = BitmapLeaseCache.NormalizeSource("/Assets/striped-background-page.png");

        Assert.Equal(page, draft, ignoreCase: true);
    }

    [Fact]
    public void Acquire_SharesBitmapUntilLastLeaseIsReleased()
    {
        var loads = 0;
        using var cache = new BitmapLeaseCache(4, (_, _, _) =>
        {
            loads++;
            return CreateBitmap();
        }, _ => 4, _ => { });

        using var first = cache.Acquire("first.png");
        using var second = cache.Acquire("first.png");

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Same(first!.Bitmap, second!.Bitmap);
        Assert.Equal(1, loads);
        Assert.Equal(1, cache.EntryCount);
    }

    [Fact]
    public void Release_EvictsLeastRecentlyUsedUnleasedBitmapOverBudget()
    {
        var loads = 0;
        using var cache = new BitmapLeaseCache(4, (_, _, _) =>
        {
            loads++;
            return CreateBitmap();
        }, _ => 4, _ => { });

        var first = cache.Acquire("first.png");
        var second = cache.Acquire("second.png");
        Assert.Equal(2, cache.EntryCount);

        first!.Dispose();

        Assert.Equal(1, cache.EntryCount);
        Assert.Equal(4, cache.TotalPixelBytes);
        second!.Dispose();
        Assert.Equal(2, loads);
    }

    [Fact]
    public void TrimUnused_PreservesLeasedEntriesAndRemovesReleasedEntries()
    {
        var disposed = 0;
        using var cache = new BitmapLeaseCache(16, (_, _, _) => CreateBitmap(), _ => 4, _ => disposed++);
        var leased = cache.Acquire("leased.png");
        var released = cache.Acquire("released.png");
        released!.Dispose();

        cache.TrimUnused();

        Assert.Equal(1, cache.EntryCount);
        Assert.Equal(4, cache.TotalPixelBytes);
        Assert.Equal(1, disposed);
        Assert.Same(leased!.Bitmap, cache.Acquire("leased.png")!.Bitmap);
        leased.Dispose();
    }

    [Fact]
    public void TrimUnused_IsIdempotent()
    {
        var disposed = 0;
        using var cache = new BitmapLeaseCache(16, (_, _, _) => CreateBitmap(), _ => 4, _ => disposed++);
        cache.Acquire("released.png")!.Dispose();

        cache.TrimUnused();
        cache.TrimUnused();

        Assert.Equal(0, cache.EntryCount);
        Assert.Equal(1, disposed);
    }

    [Fact]
    public void Dispose_ReleasesLeasedAndUnleasedEntriesOnce()
    {
        var disposed = 0;
        var cache = new BitmapLeaseCache(16, (_, _, _) => CreateBitmap(), _ => 4, _ => disposed++);
        var lease = cache.Acquire("leased.png");
        cache.Acquire("released.png")!.Dispose();

        cache.Dispose();
        cache.Dispose();
        lease!.Dispose();

        Assert.Equal(2, disposed);
        Assert.Equal(0, cache.EntryCount);
    }

    private static Bitmap CreateBitmap() =>
        (Bitmap)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Bitmap));
}
