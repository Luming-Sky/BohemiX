using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using BohemiX.Core.Models;
using BohemiX.Core.Services;
using BohemiX.Infrastructure.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Serilog;

namespace BohemiX.App.Services;

public sealed class NexusEmbeddedBrowserAuthService : INexusEmbeddedBrowserAuthService, INexusWebDownloadLinkResolver, INexusWebPageResolver, INexusDownloadAuthorizationService
{
    private const string NexusModsUrl = "https://www.nexusmods.com/";
    private const string NexusAccountUrl = "https://users.nexusmods.com/account/security";
    private const string NexusCookieHost = "nexusmods.com";
    private const string DownloadUrlMessageType = "bohemix:nexus-download-url";
    private static readonly TimeSpan CookieReadCacheDuration = TimeSpan.FromSeconds(30);

    private readonly IApplicationPathService applicationPathService;
    private readonly ILogger logger;
    private readonly object cookieReadGate = new();
    private Task<IReadOnlyList<NexusCookieValue>>? activeCookieRead;
    private IReadOnlyList<NexusCookieValue> cachedCookies = [];
    private DateTimeOffset cachedCookiesExpiresAt;

    public NexusEmbeddedBrowserAuthService(
        IApplicationPathService applicationPathService,
        ILogger logger)
    {
        this.applicationPathService = applicationPathService;
        this.logger = logger.ForContext<NexusEmbeddedBrowserAuthService>();
    }

    public async Task<bool> SignInAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var result = await RunOnStaThreadAsync(() =>
        {
            using var window = new NexusWebViewSignInForm(GetUserDataFolder());
            return Task.FromResult(window.ShowSignIn(cancellationToken));
        }, cancellationToken);

        logger.Information("Embedded Edge WebView2 Nexus sign-in completed: {Result}", result);
        if (result)
        {
            InvalidateCookieReadCache();
        }

        return result;
    }

    public async Task<NexusCookieAuthProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Failed("EMBEDDED_EDGE_UNAVAILABLE", "Embedded Edge WebView2 sign-in is only available on Windows.");
        }

        try
        {
            using var lease = await TryCreateCookieHeaderLeaseAsync(new Uri(NexusModsUrl), cancellationToken);
            if (lease is null)
            {
                return Failed("EMBEDDED_EDGE_COOKIE_NOT_FOUND", "No Nexus sign-in was found in BohemiX embedded Edge.");
            }

            var authenticated = await RunOnStaThreadAsync(() =>
            {
                using var form = new NexusSessionProbeForm(GetUserDataFolder());
                return Task.FromResult(form.Probe(cancellationToken));
            }, cancellationToken);
            return authenticated
                ? new NexusCookieAuthProbeResult(
                    true,
                    "BohemiX embedded Edge",
                    null,
                    "Nexus accepted the BohemiX embedded Edge sign-in.",
                    [])
                : Failed(
                    "EMBEDDED_EDGE_SESSION_REJECTED",
                    "Nexus rejected the browser sign-in. Sign in again or use a personal key.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or COMException)
        {
            logger.Warning(ex, "Embedded Edge WebView2 Nexus Cookie probe failed.");
            return Failed("EMBEDDED_EDGE_PROBE_FAILED", "BohemiX embedded Edge sign-in could not be inspected.");
        }
    }

    public async Task<NexusCookieAuthLease?> TryCreateCookieHeaderLeaseAsync(
        Uri requestUri,
        CancellationToken cancellationToken = default)
    {
        NexusCookieAuthService.AssertNexusModsUri(requestUri);
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var cookies = await ReadNexusCookiesAsync(cancellationToken);
        if (cookies.Count == 0)
        {
            return null;
        }

        var header = string.Join("; ", cookies.Select(cookie => $"{cookie.Name}={cookie.Value}"));
        return new NexusCookieAuthLease("BohemiX embedded Edge", SecureCookieHeader.FromPlainText(header));
    }

    public void ClearSession()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        InvalidateCookieReadCache();
        _ = RunOnStaThreadAsync(() =>
        {
            using var form = new NexusCookieReaderForm(GetUserDataFolder(), deleteCookies: true);
            _ = form.ReadCookies(CancellationToken.None);
            InvalidateCookieReadCache();
            return Task.FromResult(true);
        }, CancellationToken.None);
    }

    public async Task<string?> TryGenerateDownloadUrlAsync(
        string gameDomainName,
        int modId,
        int fileId,
        int gameId,
        Uri referrer,
        CancellationToken cancellationToken = default)
    {
        NexusCookieAuthService.AssertNexusModsUri(referrer);
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            var result = await RunOnStaThreadAsync(() =>
            {
                using var form = new NexusDownloadUrlResolverForm(GetUserDataFolder(), fileId, gameId, referrer);
                return Task.FromResult(form.GenerateDownloadUrl(cancellationToken));
            }, cancellationToken);

            logger.Information(
                "Embedded Edge WebView2 Nexus download URL request completed for mod {ModId}, file {FileId}: HTTP {StatusCode}, HasBody={HasBody}.",
                modId,
                fileId,
                result?.StatusCode,
                !string.IsNullOrWhiteSpace(result?.Body));
            return result?.Body;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or COMException or JsonException)
        {
            logger.Warning(
                ex,
                "Embedded Edge WebView2 Nexus download URL request failed for mod {ModId}, file {FileId}.",
                modId,
                fileId);
            return null;
        }
    }

    private Task<IReadOnlyList<NexusCookieValue>> ReadNexusCookiesAsync(CancellationToken cancellationToken)
    {
        Task<IReadOnlyList<NexusCookieValue>> readTask;
        lock (cookieReadGate)
        {
            if (DateTimeOffset.UtcNow < cachedCookiesExpiresAt)
            {
                return Task.FromResult(cachedCookies);
            }

            readTask = activeCookieRead ??= ReadNexusCookiesCoreAsync();
        }

        return readTask.WaitAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<NexusCookieValue>> ReadNexusCookiesCoreAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var cookies = await RunOnStaThreadAsync(() =>
            {
                using var form = new NexusCookieReaderForm(GetUserDataFolder(), deleteCookies: false);
                return Task.FromResult<IReadOnlyList<NexusCookieValue>>(form.ReadCookies(timeout.Token));
            }, timeout.Token);

            lock (cookieReadGate)
            {
                cachedCookies = cookies;
                cachedCookiesExpiresAt = DateTimeOffset.UtcNow + CookieReadCacheDuration;
                activeCookieRead = null;
            }

            return cookies;
        }
        catch
        {
            lock (cookieReadGate)
            {
                activeCookieRead = null;
            }

            throw;
        }
    }

    private void InvalidateCookieReadCache()
    {
        lock (cookieReadGate)
        {
            cachedCookies = [];
            cachedCookiesExpiresAt = default;
        }
    }

    private string GetUserDataFolder()
    {
        var paths = applicationPathService.GetPaths();
        var folder = Path.Combine(paths.DataDirectory, "nexus-webview2");
        Directory.CreateDirectory(folder);
        logger.Debug("Using embedded Edge WebView2 Nexus user data folder {UserDataFolder}.", folder);
        return folder;
    }

    private static bool IsNexusCookieDomain(string domain)
    {
        var host = domain.TrimStart('.');
        return string.Equals(host, NexusCookieHost, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("." + NexusCookieHost, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<string?> TryLoadPageHtmlAsync(
        Uri uri,
        CancellationToken cancellationToken = default)
    {
        NexusCookieAuthService.AssertNexusModsUri(uri);
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            return await RunOnStaThreadAsync(() =>
            {
                using var form = new NexusWebPageResolverForm(GetUserDataFolder(), uri);
                return Task.FromResult(form.LoadPageHtml(cancellationToken));
            }, cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or COMException or JsonException)
        {
            logger.Warning(ex, "Embedded Edge WebView2 Nexus page load failed for {Uri}.", uri);
            return null;
        }
    }

    public async Task<NexusDownloadAuthorizationResult> AuthorizeAsync(
        NexusDownloadAuthorizationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!OperatingSystem.IsWindows())
        {
            return new NexusDownloadAuthorizationResult(false, null, "Nexus 网页授权目前仅支持 Windows WebView2。");
        }

        AssertOfficialFilePage(request);
        try
        {
            var result = await RunOnStaThreadAsync(() =>
            {
                using var form = new NexusDownloadAuthorizationForm(GetUserDataFolder(), request);
                return Task.FromResult(form.Authorize(cancellationToken));
            }, cancellationToken);
            logger.Information(
                "Visible Nexus authorization completed for mod {ModId}, file {FileId}: {Authorized}.",
                request.ModId,
                request.FileId,
                result.Authorized);
            return result;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or COMException)
        {
            logger.Warning(ex, "Visible Nexus authorization failed for mod {ModId}, file {FileId}.", request.ModId, request.FileId);
            return new NexusDownloadAuthorizationResult(false, null, ex.Message);
        }
    }

    private static bool IsNexusAuthenticationCookie(CoreWebView2Cookie cookie)
    {
        return IsNexusCookieDomain(cookie.Domain)
            && !string.IsNullOrEmpty(cookie.Value)
            && !IsExpired(cookie)
            && (string.Equals(cookie.Name, "nexusmods_session", StringComparison.OrdinalIgnoreCase)
                || string.Equals(cookie.Name, "member_id", StringComparison.OrdinalIgnoreCase)
                || string.Equals(cookie.Name, "pass_hash", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsExpired(CoreWebView2Cookie cookie)
    {
        return cookie.Expires != DateTime.MinValue
            && new DateTimeOffset(cookie.Expires, TimeSpan.Zero) <= DateTimeOffset.UtcNow;
    }

    private static NexusCookieAuthProbeResult Failed(string errorCode, string message)
    {
        return new NexusCookieAuthProbeResult(
            false,
            null,
            errorCode,
            message,
            [new NexusCookieAuthProbeAttempt("BohemiX embedded Edge", errorCode, message)]);
    }

    internal static bool IsAuthenticatedAccountPage(string? finalUrl, string? html)
    {
        if (!Uri.TryCreate(finalUrl, UriKind.Absolute, out var uri)
            || !IsNexusCookieDomain(uri.Host)
            || !IsNexusAccountPath(uri)
            || string.IsNullOrWhiteSpace(html))
        {
            return false;
        }

        return !html.Contains("/users/login", StringComparison.OrdinalIgnoreCase)
            && !html.Contains("/auth/sign_in", StringComparison.OrdinalIgnoreCase)
            && !html.Contains("Log in to Nexus Mods", StringComparison.OrdinalIgnoreCase)
            && !html.Contains("Sign in to Nexus Mods", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNexusAccountPath(Uri uri)
    {
        return uri.Host.Equals("users.nexusmods.com", StringComparison.OrdinalIgnoreCase)
            ? uri.AbsolutePath.StartsWith("/account", StringComparison.OrdinalIgnoreCase)
            : uri.AbsolutePath.StartsWith("/users/myaccount", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool TryParseNxmAuthorization(
        string? value,
        NexusDownloadAuthorizationRequest request,
        out NexusDownloadAuthorization? authorization,
        out string error)
    {
        authorization = null;
        error = "该链接不是有效的 Nexus Mod Manager 下载授权。";
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals("nxm", StringComparison.OrdinalIgnoreCase)
            || !uri.Host.Equals(request.GameDomainName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length != 4
            || !segments[0].Equals("mods", StringComparison.OrdinalIgnoreCase)
            || !segments[2].Equals("files", StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(segments[1], out var modId)
            || !int.TryParse(segments[3], out var fileId)
            || modId != request.ModId
            || fileId != request.FileId)
        {
            error = "Nexus 授权链接与当前等待下载的模组文件不匹配。";
            return false;
        }

        var query = ParseQuery(uri.Query);
        if (!query.TryGetValue("key", out var key)
            || string.IsNullOrWhiteSpace(key)
            || key.Length is < 4 or > 1024
            || key.Any(char.IsControl)
            || !query.TryGetValue("expires", out var expiresText)
            || !long.TryParse(expiresText, out var expires))
        {
            error = "Nexus 授权链接不完整，请重新登录。";
            return false;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (expires <= now || expires > now + (long)TimeSpan.FromDays(1).TotalSeconds)
        {
            error = "Nexus 下载授权已过期或有效期异常，请重新点击 Mod Manager Download。";
            return false;
        }

        authorization = new NexusDownloadAuthorization(key, expires);
        error = string.Empty;
        return true;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            var name = separator < 0 ? part : part[..separator];
            var value = separator < 0 ? string.Empty : part[(separator + 1)..];
            name = Uri.UnescapeDataString(name.Replace('+', ' '));
            value = Uri.UnescapeDataString(value.Replace('+', ' '));
            if (!values.TryAdd(name, value))
            {
                values[name] = string.Empty;
            }
        }

        return values;
    }

    private static void AssertOfficialFilePage(NexusDownloadAuthorizationRequest request)
    {
        var uri = request.OfficialFilePageUri;
        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !(uri.Host.Equals("www.nexusmods.com", StringComparison.OrdinalIgnoreCase)
                 || uri.Host.Equals("nexusmods.com", StringComparison.OrdinalIgnoreCase))
            || !uri.AbsolutePath.Equals($"/{request.GameDomainName}/mods/{request.ModId}", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refusing to open a non-official Nexus file authorization page.");
        }
    }

    private static async Task<T> RunOnStaThreadAsync<T>(
        Func<Task<T>> action,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(async () =>
        {
            try
            {
                completion.TrySetResult(await action());
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        using var registration = cancellationToken.Register(
            () => completion.TrySetCanceled(cancellationToken));
        return await completion.Task.ConfigureAwait(false);
    }

    private static void CloseFormOnUiThread(Form form)
    {
        if (form.IsDisposed)
        {
            return;
        }

        try
        {
            if (form.IsHandleCreated)
            {
                form.BeginInvoke(form.Close);
            }
            else
            {
                form.Close();
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private sealed record NexusCookieValue(string Name, string Value);

    private sealed record NexusDownloadUrlWebResult(int StatusCode, string? Body);

    private sealed class NexusSessionProbeForm : Form
    {
        private readonly string userDataFolder;
        private readonly WebView2 webView = new();
        private TaskCompletionSource<bool>? completion;

        public NexusSessionProbeForm(string userDataFolder)
        {
            this.userDataFolder = userDataFolder;
            ShowInTaskbar = false;
            WindowState = FormWindowState.Minimized;
            Opacity = 0;
            Width = 1;
            Height = 1;
            Controls.Add(webView);
        }

        public bool Probe(CancellationToken cancellationToken)
        {
            completion = new TaskCompletionSource<bool>();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var registration = timeout.Token.Register(() =>
            {
                completion.TrySetResult(false);
                CloseFormOnUiThread(this);
            });
            completion.Task.ContinueWith(_ => CloseFormOnUiThread(this));
            FormClosed += (_, _) => completion.TrySetResult(false);
            Application.Run(this);
            return completion.Task.Status == TaskStatus.RanToCompletion && completion.Task.Result;
        }

        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try
            {
                var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                await webView.EnsureCoreWebView2Async(environment);
                webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
                webView.CoreWebView2.Navigate(NexusAccountUrl);
            }
            catch (Exception ex) when (ex is InvalidOperationException or COMException)
            {
                completion?.TrySetResult(false);
            }
        }

        private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (completion?.Task.IsCompleted != false || webView.CoreWebView2 is null)
            {
                return;
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(750));
                var result = await webView.CoreWebView2.ExecuteScriptAsync(
                    "document.documentElement ? document.documentElement.outerHTML : ''");
                var html = JsonSerializer.Deserialize<string>(result);
                completion.TrySetResult(e.IsSuccess && IsAuthenticatedAccountPage(webView.CoreWebView2.Source, html));
            }
            catch (Exception ex) when (ex is InvalidOperationException or COMException or JsonException)
            {
                completion?.TrySetResult(false);
            }
        }
    }

    private sealed class NexusWebPageResolverForm : Form
    {
        private readonly string userDataFolder;
        private readonly Uri uri;
        private readonly WebView2 webView = new();
        private TaskCompletionSource<string?>? completion;

        public NexusWebPageResolverForm(string userDataFolder, Uri uri)
        {
            this.userDataFolder = userDataFolder;
            this.uri = uri;
            ShowInTaskbar = false;
            WindowState = FormWindowState.Minimized;
            Opacity = 0;
            Width = 1;
            Height = 1;
            Controls.Add(webView);
        }

        public string? LoadPageHtml(CancellationToken cancellationToken)
        {
            completion = new TaskCompletionSource<string?>();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var registration = timeout.Token.Register(() =>
            {
                completion.TrySetResult(null);
                CloseFormOnUiThread(this);
            });

            completion.Task.ContinueWith(_ => CloseFormOnUiThread(this));
            FormClosed += (_, _) => completion.TrySetResult(null);
            Application.Run(this);
            return completion.Task.Status == TaskStatus.RanToCompletion ? completion.Task.Result : null;
        }

        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try
            {
                var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                await webView.EnsureCoreWebView2Async(environment);
                webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
                webView.CoreWebView2.Navigate(uri.AbsoluteUri);
            }
            catch (Exception ex) when (ex is InvalidOperationException or COMException)
            {
                completion?.TrySetResult(null);
            }
        }

        private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (completion?.Task.IsCompleted != false || webView.CoreWebView2 is null)
            {
                return;
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(750));
                var result = await webView.CoreWebView2.ExecuteScriptAsync(
                    "document.documentElement ? document.documentElement.outerHTML : ''");
                completion.TrySetResult(JsonSerializer.Deserialize<string>(result));
            }
            catch (Exception ex) when (ex is InvalidOperationException or COMException or JsonException)
            {
                completion?.TrySetResult(null);
            }
        }
    }

    private sealed class NexusDownloadAuthorizationForm : Form
    {
        private readonly string userDataFolder;
        private readonly NexusDownloadAuthorizationRequest request;
        private readonly WebView2 webView = new();
        private readonly Label statusLabel = new();
        private TaskCompletionSource<NexusDownloadAuthorizationResult>? completion;

        public NexusDownloadAuthorizationForm(string userDataFolder, NexusDownloadAuthorizationRequest request)
        {
            this.userDataFolder = userDataFolder;
            this.request = request;
            Text = $"BohemiX - Nexus 下载授权 - {request.ModName}";
            Width = 1180;
            Height = 820;
            MinimumSize = new System.Drawing.Size(760, 560);
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = true;

            statusLabel.Dock = DockStyle.Top;
            statusLabel.Height = 46;
            statusLabel.Text = $"请在官方页面点击 Mod Manager Download：{request.FileName}";
            statusLabel.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            statusLabel.Padding = new Padding(12, 0, 12, 0);

            webView.Dock = DockStyle.Fill;
            Controls.Add(webView);
            Controls.Add(statusLabel);
        }

        public NexusDownloadAuthorizationResult Authorize(CancellationToken cancellationToken)
        {
            var result = new NexusDownloadAuthorizationResult(false, null, "授权窗口已关闭，整合包安装已暂停。");
            completion = new TaskCompletionSource<NexusDownloadAuthorizationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellationToken.Register(() =>
            {
                completion.TrySetCanceled(cancellationToken);
                CloseFormOnUiThread(this);
            });
            completion.Task.ContinueWith(task =>
            {
                if (task.Status == TaskStatus.RanToCompletion)
                {
                    result = task.Result;
                }

                CloseFormOnUiThread(this);
            }, TaskScheduler.Default);
            FormClosed += (_, _) => completion.TrySetResult(result);
            Application.Run(this);
            return result;
        }

        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try
            {
                var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                await webView.EnsureCoreWebView2Async(environment);
                webView.CoreWebView2.NavigationStarting += OnNavigationStarting;
                webView.CoreWebView2.NewWindowRequested += OnNewWindowRequested;
                webView.CoreWebView2.Navigate(request.OfficialFilePageUri.AbsoluteUri);
                webView.Focus();
            }
            catch (Exception ex) when (ex is InvalidOperationException or COMException)
            {
                completion?.TrySetResult(new NexusDownloadAuthorizationResult(false, null, $"无法启动 WebView2：{ex.Message}"));
            }
        }

        private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
        {
            if (!e.Uri.StartsWith("nxm:", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            e.Cancel = true;
            CompleteFromNxmUri(e.Uri);
        }

        private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            if (!e.Uri.StartsWith("nxm:", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            e.Handled = true;
            CompleteFromNxmUri(e.Uri);
        }

        private void CompleteFromNxmUri(string value)
        {
            if (TryParseNxmAuthorization(value, request, out var authorization, out var error))
            {
                completion?.TrySetResult(new NexusDownloadAuthorizationResult(true, authorization));
                return;
            }

            statusLabel.Text = error;
        }
    }

    private sealed class NexusDownloadUrlResolverForm : Form
    {
        private readonly string userDataFolder;
        private readonly int fileId;
        private readonly int gameId;
        private readonly Uri referrer;
        private readonly WebView2 webView = new();
        private TaskCompletionSource<NexusDownloadUrlWebResult?>? completion;
        private int requestStarted;
        private int scheduledAttempts;

        public NexusDownloadUrlResolverForm(
            string userDataFolder,
            int fileId,
            int gameId,
            Uri referrer)
        {
            this.userDataFolder = userDataFolder;
            this.fileId = fileId;
            this.gameId = gameId;
            this.referrer = referrer;
            ShowInTaskbar = false;
            WindowState = FormWindowState.Minimized;
            Opacity = 0;
            Width = 1;
            Height = 1;
            Controls.Add(webView);
        }

        public NexusDownloadUrlWebResult? GenerateDownloadUrl(CancellationToken cancellationToken)
        {
            completion = new TaskCompletionSource<NexusDownloadUrlWebResult?>();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            using var registration = timeout.Token.Register(() =>
            {
                completion.TrySetResult(null);
                CloseFormOnUiThread(this);
            });

            completion.Task.ContinueWith(_ =>
            {
                CloseFormOnUiThread(this);
            });

            FormClosed += (_, _) => completion.TrySetResult(null);
            Application.Run(this);
            return completion.Task.Status == TaskStatus.RanToCompletion ? completion.Task.Result : null;
        }

        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try
            {
                var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                await webView.EnsureCoreWebView2Async(environment);
                webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
                webView.CoreWebView2.AddWebResourceRequestedFilter(
                    "*Core/Libs/Common/Managers/Downloads?GenerateDownloadUrl*",
                    CoreWebView2WebResourceContext.XmlHttpRequest);
                webView.CoreWebView2.WebResourceResponseReceived += OnWebResourceResponseReceived;
                AddDownloadFlowCookies();
                webView.CoreWebView2.Navigate(referrer.AbsoluteUri);
                _ = RunGenerateDownloadUrlAfterDelayAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex) when (ex is InvalidOperationException or COMException)
            {
                completion?.TrySetResult(null);
            }
        }

        private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (webView.CoreWebView2 is null)
            {
                completion?.TrySetResult(null);
                return;
            }

            webView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
            if (!IsCurrentPageNexusMods())
            {
                _ = RunGenerateDownloadUrlAfterDelayAsync(TimeSpan.FromSeconds(1));
                return;
            }

            if (Interlocked.Exchange(ref requestStarted, 1) != 0)
            {
                return;
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(750));
                await StartGenerateDownloadUrlRequestAsync();
            }
            catch (Exception ex) when (ex is InvalidOperationException or COMException)
            {
                completion?.TrySetResult(null);
            }
        }

        private async Task RunGenerateDownloadUrlAfterDelayAsync(TimeSpan delay)
        {
            try
            {
                await Task.Delay(delay);
                if (Interlocked.Increment(ref scheduledAttempts) > 12)
                {
                    completion?.TrySetResult(null);
                    return;
                }

                if (!IsCurrentPageNexusMods())
                {
                    _ = RunGenerateDownloadUrlAfterDelayAsync(TimeSpan.FromSeconds(1));
                    return;
                }

                if (Interlocked.Exchange(ref requestStarted, 1) != 0)
                {
                    return;
                }

                await StartGenerateDownloadUrlRequestAsync();
            }
            catch (Exception ex) when (ex is InvalidOperationException or COMException)
            {
                completion?.TrySetResult(null);
            }
        }

        private bool IsCurrentPageNexusMods()
        {
            if (webView.CoreWebView2?.Source is not { } source
                || string.IsNullOrWhiteSpace(source)
                || !Uri.TryCreate(source, UriKind.Absolute, out var uri))
            {
                return false;
            }

            return uri.Host.Equals("www.nexusmods.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Equals("nexusmods.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.EndsWith(".nexusmods.com", StringComparison.OrdinalIgnoreCase);
        }

        private void AddDownloadFlowCookies()
        {
            if (webView.CoreWebView2 is null)
            {
                return;
            }

            var cookie = webView.CoreWebView2.CookieManager.CreateCookie("ab", "1", ".nexusmods.com", "/");
            webView.CoreWebView2.CookieManager.AddOrUpdateCookie(cookie);
        }

        private async Task StartGenerateDownloadUrlRequestAsync()
        {
            if (webView.CoreWebView2 is null)
            {
                completion?.TrySetResult(null);
                return;
            }

            try
            {
                var script = BuildGenerateDownloadUrlScript();
                webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
                _ = await webView.CoreWebView2.ExecuteScriptAsync(script);
            }
            catch (Exception ex) when (ex is InvalidOperationException or COMException or JsonException)
            {
                completion?.TrySetResult(null);
            }
        }

        private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            if (completion?.Task.IsCompleted != false)
            {
                return;
            }

            try
            {
                var message = TryReadWebMessage(e);
                if (!TryParseDownloadUrlMessage(message, out var result))
                {
                    return;
                }

                completion?.TrySetResult(result);
                if (webView.CoreWebView2 is not null)
                {
                    webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
                }
            }
            catch (Exception ex) when (ex is JsonException)
            {
                completion?.TrySetResult(null);
                if (webView.CoreWebView2 is not null)
                {
                    webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
                }
            }
        }

        private async void OnWebResourceResponseReceived(
            object? sender,
            CoreWebView2WebResourceResponseReceivedEventArgs e)
        {
            if (completion?.Task.IsCompleted != false
                || !IsGenerateDownloadUrlRequest(e.Request.Uri))
            {
                return;
            }

            try
            {
                using var stream = await e.Response.GetContentAsync();
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var body = await reader.ReadToEndAsync();
                if (string.IsNullOrWhiteSpace(body) || string.Equals(body.Trim(), "[]", StringComparison.Ordinal))
                {
                    return;
                }

                completion?.TrySetResult(new NexusDownloadUrlWebResult(e.Response.StatusCode, body));
                if (webView.CoreWebView2 is not null)
                {
                    webView.CoreWebView2.WebResourceResponseReceived -= OnWebResourceResponseReceived;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or COMException or IOException)
            {
            }
        }

        private static bool IsGenerateDownloadUrlRequest(string? uri)
        {
            return !string.IsNullOrWhiteSpace(uri)
                && uri.Contains("/Core/Libs/Common/Managers/Downloads", StringComparison.OrdinalIgnoreCase)
                && uri.Contains("GenerateDownloadUrl", StringComparison.OrdinalIgnoreCase);
        }

        private static string TryReadWebMessage(CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                return e.TryGetWebMessageAsString();
            }
            catch (ArgumentException)
            {
                return e.WebMessageAsJson;
            }
        }

        private string BuildGenerateDownloadUrlScript()
        {
            var fid = fileId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var gid = gameId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var referrerJson = JsonSerializer.Serialize(referrer.AbsoluteUri);
            return $$"""
                (async () => {
                    const send = (value) => chrome.webview.postMessage(JSON.stringify({
                        type: "{{DownloadUrlMessageType}}",
                        ...value
                    }));
                    const endpoint = "/Core/Libs/Common/Managers/Downloads?GenerateDownloadUrl";
                    const referrer = {{referrerJson}};
                    const findDownloadUrl = (text) => {
                        if (!text) {
                            return null;
                        }

                        const patterns = [
                            /const\s+downloadUrl\s*=\s*['"]([^'"]+)['"]/i,
                            /id=["']slowDownloadButton["'][\s\S]*?data-download-url=["']([^"']+)["']/i,
                            /data-download-url=["']([^"']+)["']/i,
                            /href=["']([^"']*\/Core\/Libs\/Common\/Managers\/Downloads[^"']*)["']/i,
                            /https?:\\?\/\\?\/[^"'\s<>]+/i
                        ];
                        for (const pattern of patterns) {
                            const match = String(text).match(pattern);
                            const value = match && (match[1] || match[0]);
                            if (value) {
                                return value.replaceAll("\\/", "/").replaceAll("&amp;", "&").replaceAll(";", "&");
                            }
                        }

                        return null;
                    };
                    const readGameIds = () => {
                        const ids = [
                            document.getElementById("section")?.dataset?.gameId,
                            document.querySelector("#section.modpage")?.getAttribute("data-game-id"),
                            document.querySelector("[data-game-id]")?.getAttribute("data-game-id"),
                            window.PAGE_WINDOW?.current_game_id,
                            window.current_game_id,
                            "{{gid}}"
                        ];
                        return [...new Set(ids.filter(Boolean).map(String))];
                    };
                    let last = { status: 0, body: "" };
                    for (const gameId of readGameIds()) {
                        for (const nmm of ["1", "0"]) {
                            const form = { fid: "{{fid}}", game_id: gameId };
                            if (nmm === "1") {
                                form.nmm = "1";
                            }

                            const body = new URLSearchParams(form);
                            const response = await fetch(endpoint, {
                                method: "POST",
                                credentials: "include",
                                headers: {
                                    "Accept": "application/json, text/javascript, */*; q=0.01",
                                    "Content-Type": "application/x-www-form-urlencoded; charset=UTF-8",
                                    "X-Requested-With": "XMLHttpRequest"
                                },
                                body: body.toString()
                            });
                            const text = await response.text();
                            last = { status: response.status, body: text };
                            if (response.ok && text && text.trim() && text.trim() !== "[]") {
                                send(last);
                                return;
                            }
                        }
                    }

                    for (const href of [referrer, referrer + (referrer.includes("?") ? "&" : "?") + "nmm=1"]) {
                        const response = await fetch(href, {
                            method: "GET",
                            credentials: "include",
                            headers: {
                                "Accept": "text/html, */*; q=0.01",
                                "X-Requested-With": "XMLHttpRequest"
                            }
                        });
                        const text = await response.text();
                        const url = findDownloadUrl(text);
                        last = { status: response.status, body: text };
                        if (response.ok && url) {
                            send({ status: response.status, body: JSON.stringify({ url }) });
                            return;
                        }
                    }

                    send(last);
                })().catch(error => chrome.webview.postMessage(JSON.stringify({
                    type: "{{DownloadUrlMessageType}}",
                    status: 0,
                    body: String(error && (error.stack || error.message || error))
                })));
                """;
        }

        private static bool TryParseDownloadUrlMessage(
            string message,
            out NexusDownloadUrlWebResult? result)
        {
            result = null;
            if (string.IsNullOrWhiteSpace(message) || message == "null")
            {
                return false;
            }

            using var inner = JsonDocument.Parse(message);
            if (!inner.RootElement.TryGetProperty("type", out var typeElement)
                || !string.Equals(typeElement.GetString(), DownloadUrlMessageType, StringComparison.Ordinal))
            {
                return false;
            }

            var status = inner.RootElement.TryGetProperty("status", out var statusElement)
                && statusElement.TryGetInt32(out var statusCode)
                    ? statusCode
                    : 0;
            var body = inner.RootElement.TryGetProperty("body", out var bodyElement)
                ? bodyElement.GetString()
                : null;
            result = new NexusDownloadUrlWebResult(status, body);
            return true;
        }
    }

    private sealed class NexusCookieReaderForm : Form
    {
        private readonly string userDataFolder;
        private readonly bool deleteCookies;
        private readonly WebView2 webView = new();
        private TaskCompletionSource<IReadOnlyList<NexusCookieValue>>? completion;

        public NexusCookieReaderForm(string userDataFolder, bool deleteCookies)
        {
            this.userDataFolder = userDataFolder;
            this.deleteCookies = deleteCookies;
            ShowInTaskbar = false;
            WindowState = FormWindowState.Minimized;
            Opacity = 0;
            Width = 1;
            Height = 1;
            Controls.Add(webView);
        }

        public IReadOnlyList<NexusCookieValue> ReadCookies(CancellationToken cancellationToken)
        {
            completion = new TaskCompletionSource<IReadOnlyList<NexusCookieValue>>();
            using var registration = cancellationToken.Register(() =>
            {
                completion.TrySetCanceled(cancellationToken);
                CloseFormOnUiThread(this);
            });

            completion.Task.ContinueWith(_ =>
            {
                CloseFormOnUiThread(this);
            });

            FormClosed += (_, _) => completion.TrySetResult([]);
            Application.Run(this);
            return completion.Task.Status == TaskStatus.RanToCompletion ? completion.Task.Result : [];
        }

        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try
            {
                var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                await webView.EnsureCoreWebView2Async(environment);
                var cookies = await webView.CoreWebView2.CookieManager.GetCookiesAsync(NexusModsUrl);
                var nexusCookies = cookies
                    .Where(cookie => IsNexusCookieDomain(cookie.Domain)
                        && !string.IsNullOrWhiteSpace(cookie.Name)
                        && !string.IsNullOrEmpty(cookie.Value)
                        && !IsExpired(cookie))
                    .ToArray();

                if (deleteCookies)
                {
                    foreach (var cookie in nexusCookies)
                    {
                        webView.CoreWebView2.CookieManager.DeleteCookie(cookie);
                    }

                    completion?.TrySetResult([]);
                    return;
                }

                if (!nexusCookies.Any(IsNexusAuthenticationCookie))
                {
                    completion?.TrySetResult([]);
                    return;
                }

                completion?.TrySetResult(nexusCookies.Select(cookie => new NexusCookieValue(cookie.Name, cookie.Value)).ToArray());
            }
            catch (Exception ex) when (ex is InvalidOperationException or COMException)
            {
                completion?.TrySetResult([]);
            }
        }
    }

    private sealed class NexusWebViewSignInForm : Form
    {
        private readonly string userDataFolder;
        private readonly WebView2 webView = new();
        private readonly Button doneButton = new();
        private readonly Label statusLabel = new();
        private TaskCompletionSource<bool>? completion;
        private int sessionValidationStarted;

        public NexusWebViewSignInForm(string userDataFolder)
        {
            this.userDataFolder = userDataFolder;
            Text = "BohemiX - 登录 Nexus Mods";
            Width = 1180;
            Height = 820;
            StartPosition = FormStartPosition.CenterScreen;

            statusLabel.Dock = DockStyle.Top;
            statusLabel.Height = 34;
            statusLabel.Text = "请在下方登录 Nexus Mods，授权成功后窗口会自动关闭。";
            statusLabel.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            statusLabel.Padding = new Padding(12, 0, 0, 0);

            doneButton.Dock = DockStyle.Bottom;
            doneButton.Height = 42;
            doneButton.Text = "我已完成登录";
            doneButton.Click += async (_, _) => await CompleteIfSignedInAsync();

            webView.Dock = DockStyle.Fill;
            Controls.Add(webView);
            Controls.Add(doneButton);
            Controls.Add(statusLabel);
        }

        public bool ShowSignIn(CancellationToken cancellationToken)
        {
            var result = false;
            completion = new TaskCompletionSource<bool>();
            using var registration = cancellationToken.Register(() =>
            {
                completion.TrySetCanceled(cancellationToken);
                CloseFormOnUiThread(this);
            });

            completion.Task.ContinueWith(task =>
            {
                if (task.Status == TaskStatus.RanToCompletion)
                {
                    result = task.Result;
                }

                CloseFormOnUiThread(this);
            });

            FormClosed += (_, _) => completion.TrySetResult(false);
            Application.Run(this);
            return result;
        }

        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try
            {
                var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                await webView.EnsureCoreWebView2Async(environment);
                webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
                webView.CoreWebView2.Navigate(NexusModsUrl);
                webView.Focus();
            }
            catch (Exception ex) when (ex is InvalidOperationException or COMException)
            {
                statusLabel.Text = "无法启动 Edge WebView2，请确认系统已安装 WebView2 Runtime。";
                completion?.TrySetResult(false);
            }
        }

        private async Task CompleteIfSignedInAsync()
        {
            if (completion?.Task.IsCompleted != false)
            {
                return;
            }

            if (webView.CoreWebView2 is null)
            {
                statusLabel.Text = "Nexus 登录页面仍在加载，请稍候。";
                return;
            }

            var cookies = await webView.CoreWebView2.CookieManager.GetCookiesAsync(NexusModsUrl);
            if (!cookies.Any(IsNexusAuthenticationCookie))
            {
                statusLabel.Text = "尚未发现已登录的 Nexus 账号，请先完成登录。";
                return;
            }

            if (Volatile.Read(ref sessionValidationStarted) != 0)
            {
                statusLabel.Text = "正在确认 Nexus 登录状态...";
                return;
            }

            Interlocked.Exchange(ref sessionValidationStarted, 1);
            statusLabel.Text = "正在确认 Nexus 登录状态...";
            webView.CoreWebView2.Navigate(NexusAccountUrl);
        }

        private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (completion?.Task.IsCompleted != false)
            {
                return;
            }

            if (Volatile.Read(ref sessionValidationStarted) == 0)
            {
                await CompleteIfSignedInAsync();
                return;
            }

            if (e.IsSuccess && await IsCurrentAccountPageAuthenticatedAsync())
            {
                completion?.TrySetResult(true);
                return;
            }

            Interlocked.Exchange(ref sessionValidationStarted, 0);
            statusLabel.Text = "Nexus 未接受当前登录，请重新登录或使用个人密钥。";
        }

        private async Task<bool> IsCurrentAccountPageAuthenticatedAsync()
        {
            if (webView.CoreWebView2 is null)
            {
                return false;
            }

            try
            {
                var result = await webView.CoreWebView2.ExecuteScriptAsync(
                    "document.documentElement ? document.documentElement.outerHTML : ''");
                var html = JsonSerializer.Deserialize<string>(result);
                return IsAuthenticatedAccountPage(webView.CoreWebView2.Source, html);
            }
            catch (Exception ex) when (ex is InvalidOperationException or COMException or JsonException)
            {
                return false;
            }
        }
    }
}
