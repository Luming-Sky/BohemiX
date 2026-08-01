using System;
using System.Collections.Concurrent;
using System.Net.Http;

namespace BohemiX.Infrastructure.Http;

/// <summary>
/// 简单的HTTP客户端工厂，用于复用HttpClient实例，避免端口耗尽
/// </summary>
public sealed class HttpClientFactory : IDisposable
{
    private readonly ConcurrentDictionary<string, HttpClient> clients = new();
    private bool disposed;

    /// <summary>
    /// 创建或获取指定名称的HttpClient
    /// </summary>
    public HttpClient CreateClient(string name, Action<HttpClient>? configure = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        return clients.GetOrAdd(name, key =>
        {
            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                MaxConnectionsPerServer = 10
            };

            var client = new HttpClient(handler, disposeHandler: true)
            {
                Timeout = TimeSpan.FromSeconds(30)
            };

            configure?.Invoke(client);
            return client;
        });
    }

    /// <summary>
    /// 创建或获取默认HttpClient
    /// </summary>
    public HttpClient CreateClient(Action<HttpClient>? configure = null)
    {
        return CreateClient("default", configure);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        foreach (var client in clients.Values)
        {
            try
            {
                client.Dispose();
            }
            catch
            {
                // 忽略释放错误
            }
        }

        clients.Clear();
    }
}

/// <summary>
/// HTTP客户端配置帮助类
/// </summary>
public static class HttpClientConfigurations
{
    /// <summary>
    /// 配置用于GitHub API的客户端
    /// </summary>
    public static void ConfigureForGitHub(HttpClient client)
    {
        client.Timeout = TimeSpan.FromSeconds(15);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("BohemiX/0.9.1");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    /// <summary>
    /// 配置用于Mod目录的客户端
    /// </summary>
    public static void ConfigureForModCatalog(HttpClient client)
    {
        client.Timeout = TimeSpan.FromSeconds(20);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("BohemiX/0.9.1");
    }

    /// <summary>
    /// 配置用于下载的客户端
    /// </summary>
    public static void ConfigureForDownload(HttpClient client)
    {
        client.Timeout = TimeSpan.FromMinutes(10);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("BohemiX/0.9.1");
    }
}
