using System;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace BohemiX.Core.Performance;

/// <summary>
/// 性能监控工具，用于跟踪关键操作的执行时间和性能指标
/// </summary>
public sealed class PerformanceMonitor
{
    private static readonly Lazy<PerformanceMonitor> LazyInstance =
        new(() => new PerformanceMonitor(), true);

    public static PerformanceMonitor Instance => LazyInstance.Value;

    private readonly ConcurrentDictionary<string, PerformanceMetric> metrics = new();
    private Action<string, double>? slowOperationCallback;
    private bool isEnabled = true;

    private PerformanceMonitor() { }

    /// <summary>
    /// 注册慢操作回调（由上层注入日志器）
    /// </summary>
    public void SetSlowOperationCallback(Action<string, double> callback)
    {
        slowOperationCallback = callback;
    }

    public void Enable() => isEnabled = true;
    public void Disable() => isEnabled = false;

    /// <summary>
    /// 开始跟踪操作
    /// </summary>
    public PerformanceTracker Track(string operationName)
    {
        if (!isEnabled)
        {
            return PerformanceTracker.Disabled;
        }

        return new PerformanceTracker(operationName, this);
    }

    /// <summary>
    /// 记录操作完成
    /// </summary>
    internal void RecordOperation(string operationName, TimeSpan duration)
    {
        var metric = metrics.GetOrAdd(operationName, _ => new PerformanceMetric());
        metric.Record(duration);

        if (duration.TotalMilliseconds > 500)
        {
            slowOperationCallback?.Invoke(operationName, duration.TotalMilliseconds);
        }
    }

    /// <summary>
    /// 获取性能统计信息
    /// </summary>
    public PerformanceStatistics GetStatistics(string operationName)
    {
        if (metrics.TryGetValue(operationName, out var metric))
        {
            return metric.GetStatistics();
        }

        return PerformanceStatistics.Empty;
    }

    /// <summary>
    /// 获取所有性能统计
    /// </summary>
    public System.Collections.Generic.Dictionary<string, PerformanceStatistics> GetAllStatistics()
    {
        var result = new System.Collections.Generic.Dictionary<string, PerformanceStatistics>();
        foreach (var kvp in metrics)
        {
            result[kvp.Key] = kvp.Value.GetStatistics();
        }

        return result;
    }

    /// <summary>
    /// 重置统计信息
    /// </summary>
    public void Reset()
    {
        metrics.Clear();
    }
}

/// <summary>
/// 性能跟踪器，using 块结束时自动记录耗时
/// </summary>
public readonly struct PerformanceTracker : IDisposable
{
    private readonly string? operationName;
    private readonly PerformanceMonitor? monitor;
    private readonly Stopwatch? stopwatch;

    internal static readonly PerformanceTracker Disabled = default;

    internal PerformanceTracker(string operationName, PerformanceMonitor monitor)
    {
        this.operationName = operationName;
        this.monitor = monitor;
        stopwatch = Stopwatch.StartNew();
    }

    public void Dispose()
    {
        if (stopwatch is not null && monitor is not null && operationName is not null)
        {
            stopwatch.Stop();
            monitor.RecordOperation(operationName, stopwatch.Elapsed);
        }
    }
}

/// <summary>
/// 性能指标（线程安全）
/// </summary>
internal sealed class PerformanceMetric
{
    private readonly object lockObj = new();
    private long totalCount;
    private long totalMilliseconds;
    private long minMilliseconds = long.MaxValue;
    private long maxMilliseconds;

    public void Record(TimeSpan duration)
    {
        var ms = (long)duration.TotalMilliseconds;

        lock (lockObj)
        {
            totalCount++;
            totalMilliseconds += ms;
            if (ms < minMilliseconds) minMilliseconds = ms;
            if (ms > maxMilliseconds) maxMilliseconds = ms;
        }
    }

    public PerformanceStatistics GetStatistics()
    {
        lock (lockObj)
        {
            return new PerformanceStatistics(
                totalCount,
                totalCount > 0 ? totalMilliseconds / totalCount : 0,
                minMilliseconds == long.MaxValue ? 0 : minMilliseconds,
                maxMilliseconds);
        }
    }
}

/// <summary>
/// 性能统计信息（值类型，零分配读取）
/// </summary>
public readonly record struct PerformanceStatistics(
    long Count,
    long AverageMilliseconds,
    long MinMilliseconds,
    long MaxMilliseconds)
{
    public static readonly PerformanceStatistics Empty = new(0, 0, 0, 0);
}