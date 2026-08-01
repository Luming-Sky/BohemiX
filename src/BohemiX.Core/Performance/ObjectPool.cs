using System;
using System.Collections.Concurrent;

namespace BohemiX.Core.Performance;

/// <summary>
/// 通用对象池，用于减少对象分配和GC压力
/// </summary>
public sealed class ObjectPool<T> where T : class
{
    private readonly ConcurrentBag<T> objects = new();
    private readonly Func<T> objectGenerator;
    private readonly Action<T>? resetAction;
    private readonly int maxSize;
    private int currentSize;

    public ObjectPool(Func<T> objectGenerator, Action<T>? resetAction = null, int maxSize = 100)
    {
        this.objectGenerator = objectGenerator ?? throw new ArgumentNullException(nameof(objectGenerator));
        this.resetAction = resetAction;
        this.maxSize = maxSize;
    }

    /// <summary>
    /// 从池中获取对象
    /// </summary>
    public T Get()
    {
        if (objects.TryTake(out var item))
        {
            System.Threading.Interlocked.Decrement(ref currentSize);
            return item;
        }

        return objectGenerator();
    }

    /// <summary>
    /// 将对象归还到池中
    /// </summary>
    public void Return(T item)
    {
        if (item is null)
        {
            return;
        }

        // 如果池已满，不再保留对象
        if (currentSize >= maxSize)
        {
            return;
        }

        resetAction?.Invoke(item);
        objects.Add(item);
        System.Threading.Interlocked.Increment(ref currentSize);
    }

    /// <summary>
    /// 清空对象池
    /// </summary>
    public void Clear()
    {
        while (objects.TryTake(out _))
        {
            System.Threading.Interlocked.Decrement(ref currentSize);
        }
    }

    /// <summary>
    /// 获取池中的对象数量
    /// </summary>
    public int Count => currentSize;
}

/// <summary>
/// 对象池租约，使用完毕后自动归还
/// </summary>
public readonly struct PooledObject<T> : IDisposable where T : class
{
    private readonly ObjectPool<T>? pool;
    private readonly T? obj;

    internal PooledObject(ObjectPool<T> pool, T obj)
    {
        this.pool = pool;
        this.obj = obj;
    }

    public T Object => obj ?? throw new InvalidOperationException("Object is null");

    public void Dispose()
    {
        if (pool is not null && obj is not null)
        {
            pool.Return(obj);
        }
    }
}

/// <summary>
/// 对象池扩展方法
/// </summary>
public static class ObjectPoolExtensions
{
    /// <summary>
    /// 租用对象，使用完毕后自动归还
    /// </summary>
    public static PooledObject<T> Rent<T>(this ObjectPool<T> pool) where T : class
    {
        var obj = pool.Get();
        return new PooledObject<T>(pool, obj);
    }
}
