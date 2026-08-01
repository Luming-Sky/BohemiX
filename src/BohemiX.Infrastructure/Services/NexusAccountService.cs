using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BohemiX.Core.Models;
using BohemiX.Core.Services;
using Serilog;

namespace BohemiX.Infrastructure.Services;

public sealed class NexusAccountService : INexusAccountService, IDisposable
{
    private const int MaxSsoMessageBytes = 64 * 1024;
    private readonly SemaphoreSlim bindingGate = new(1, 1);
    private readonly NexusAccountCredentialStore credentialStore;
    private readonly NexusSsoOptions options;
    private readonly HttpClient httpClient;
    private readonly ILogger logger;
    private readonly Func<Uri, CancellationToken, Task> openBrowser;
    private readonly INexusEmbeddedBrowserAuthService? embeddedBrowserAuthService;

    public NexusAccountService(
        NexusAccountCredentialStore credentialStore,
        NexusSsoOptions options,
        IEnumerable<INexusEmbeddedBrowserAuthService> embeddedBrowserAuthServices,
        ILogger logger)
        : this(credentialStore, options, logger, new HttpClient(), OpenBrowserAsync)
    {
        embeddedBrowserAuthService = embeddedBrowserAuthServices.FirstOrDefault();
    }

    internal NexusAccountService(
        NexusAccountCredentialStore credentialStore,
        NexusSsoOptions options,
        ILogger logger,
        HttpClient httpClient,
        Func<Uri, CancellationToken, Task> openBrowser,
        INexusEmbeddedBrowserAuthService? embeddedBrowserAuthService = null)
    {
        this.credentialStore = credentialStore;
        this.options = options;
        this.logger = logger.ForContext<NexusAccountService>();
        this.httpClient = httpClient;
        this.openBrowser = openBrowser;
        this.embeddedBrowserAuthService = embeddedBrowserAuthService;
    }

    public async Task<NexusAccountBinding?> GetBoundAccountAsync(CancellationToken cancellationToken = default)
    {
        var credential = await credentialStore.LoadAsync(cancellationToken);
        if (credential is not null)
        {
            return credential.ToBinding();
        }

        if (embeddedBrowserAuthService is null)
        {
            return null;
        }

        var probe = await embeddedBrowserAuthService.ProbeAsync(cancellationToken);
        return probe.Success ? CreateBrowserAccountBinding() : null;
    }

    public async Task<NexusAccountBinding> BindAsync(CancellationToken cancellationToken = default)
    {
        await bindingGate.WaitAsync(cancellationToken);
        try
        {
            if (embeddedBrowserAuthService is not null)
            {
                return await BindWithEmbeddedBrowserAsync(cancellationToken);
            }

            if (string.IsNullOrWhiteSpace(options.ApplicationSlug))
            {
                throw new InvalidOperationException("Nexus browser sign-in is unavailable and the SSO application slug is not configured.");
            }

            var requestId = Guid.NewGuid();
            var deadline = DateTimeOffset.UtcNow + options.EffectiveAuthorizationTimeout;
            string? connectionToken = null;
            var browserOpened = false;

            logger.Information("Starting Nexus SSO account binding request {RequestId}.", requestId);
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var socket = new ClientWebSocket();
                try
                {
                    using var operationTimeout = CreateOperationTimeout(deadline, cancellationToken);
                    await socket.ConnectAsync(new Uri(options.WebSocketEndpoint), operationTimeout.Token);
                    await SendHandshakeAsync(socket, requestId, connectionToken, operationTimeout.Token);

                    while (socket.State == WebSocketState.Open && DateTimeOffset.UtcNow < deadline)
                    {
                        using var receiveTimeout = CreateOperationTimeout(deadline, cancellationToken);
                        var message = await ReceiveMessageAsync(socket, receiveTimeout.Token);
                        if (message is null)
                        {
                            break;
                        }

                        var frame = ParseSsoMessage(message);
                        if (!frame.Success)
                        {
                            throw new InvalidOperationException(frame.Error ?? "Nexus SSO rejected the account binding request.");
                        }

                        if (!string.IsNullOrWhiteSpace(frame.ConnectionToken))
                        {
                            connectionToken = frame.ConnectionToken;
                        }

                        if (!browserOpened && !string.IsNullOrWhiteSpace(connectionToken))
                        {
                            await openBrowser(BuildAuthorizationUri(options, requestId), cancellationToken);
                            browserOpened = true;
                        }

                        if (string.IsNullOrWhiteSpace(frame.ApiKey))
                        {
                            continue;
                        }

                        var account = await ValidateAccountAsync(frame.ApiKey, cancellationToken);
                        await credentialStore.SaveAsync(frame.ApiKey, account, cancellationToken);
                        logger.Information(
                            "Nexus account {NexusUserId} ({NexusUserName}) was bound successfully.",
                            account.UserId,
                            account.Name);
                        return account;
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (WebSocketException ex)
                {
                    logger.Warning(ex, "Nexus SSO connection was interrupted; reconnecting the pending request.");
                }

                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining > TimeSpan.Zero)
                {
                    await Task.Delay(remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1), cancellationToken);
                }
            }

            throw new TimeoutException("Nexus account binding timed out. Approve the request in the Nexus Mods page and try again.");
        }
        finally
        {
            bindingGate.Release();
        }
    }

    public async Task UnbindAsync(CancellationToken cancellationToken = default)
    {
        logger.Information("Removing the local Nexus account binding.");
        embeddedBrowserAuthService?.ClearSession();
        await credentialStore.ClearAsync(cancellationToken);
    }

    public async Task<NexusAccountBinding> BindWithApiKeyAsync(
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException("A Nexus API key is required.", nameof(apiKey));
        }

        await bindingGate.WaitAsync(cancellationToken);
        try
        {
            var account = await ValidateAccountAsync(apiKey, cancellationToken);
            await credentialStore.SaveAsync(apiKey, account, cancellationToken);
            logger.Information(
                "Nexus account {NexusUserId} ({NexusUserName}) was bound with a personal API key.",
                account.UserId,
                account.Name);
            return account;
        }
        finally
        {
            bindingGate.Release();
        }
    }

    internal async Task<NexusAccountBinding> ValidateAccountAsync(
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.nexusmods.com/v1/users/validate.json");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("apikey", apiKey);
        request.Headers.TryAddWithoutValidation("Application-Name", "BohemiX");
        request.Headers.TryAddWithoutValidation("Application-Version", "0.9.1");

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new NexusModsException(
                "Nexus rejected the account authorization. Revoke the BohemiX key on Nexus Mods and bind again.",
                response.StatusCode,
                responseBody: body);
        }

        return ParseValidatedAccount(body);
    }

    private async Task<NexusAccountBinding> BindWithEmbeddedBrowserAsync(CancellationToken cancellationToken)
    {
        logger.Information("Starting embedded Nexus Mods browser sign-in.");
        if (!await embeddedBrowserAuthService!.SignInAsync(cancellationToken))
        {
            throw new OperationCanceledException("Nexus browser sign-in was cancelled.");
        }

        logger.Information("Nexus account was bound through the embedded browser session.");
        return CreateBrowserAccountBinding();
    }

    private static NexusAccountBinding CreateBrowserAccountBinding() =>
        new(0, "Nexus Mods 账号", false, false);

    internal static Uri BuildAuthorizationUri(NexusSsoOptions options, Guid requestId)
    {
        var separator = options.AuthorizationEndpoint.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return new Uri(
            $"{options.AuthorizationEndpoint}{separator}id={Uri.EscapeDataString(requestId.ToString("D"))}&application={Uri.EscapeDataString(options.ApplicationSlug)}");
    }

    internal static NexusSsoFrame ParseSsoMessage(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var success = root.TryGetProperty("success", out var successElement) && successElement.ValueKind == JsonValueKind.True;
        var error = ReadError(root);
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            return new NexusSsoFrame(success, null, null, error);
        }

        return new NexusSsoFrame(
            success,
            ReadString(data, "connection_token"),
            ReadString(data, "api_key"),
            error);
    }

    internal static NexusAccountBinding ParseValidatedAccount(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var userId = ReadLong(root, "user_id")
            ?? throw new JsonException("Nexus account validation did not include a user id.");
        var name = ReadString(root, "name")
            ?? throw new JsonException("Nexus account validation did not include an account name.");

        return new NexusAccountBinding(
            userId,
            name,
            ReadBool(root, "is_premium"),
            ReadBool(root, "is_supporter"));
    }

    public void Dispose()
    {
        httpClient.Dispose();
        bindingGate.Dispose();
    }

    private static async Task SendHandshakeAsync(
        ClientWebSocket socket,
        Guid requestId,
        string? connectionToken,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new NexusSsoHandshake(
            requestId.ToString("D"),
            connectionToken,
            2));
        await socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken);
    }

    private static async Task<string?> ReceiveMessageAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        using var content = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                throw new InvalidOperationException("Nexus SSO returned an unsupported WebSocket message type.");
            }

            content.Write(buffer, 0, result.Count);
            if (content.Length > MaxSsoMessageBytes)
            {
                throw new InvalidOperationException("Nexus SSO returned an unexpectedly large response.");
            }

            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(content.GetBuffer(), 0, checked((int)content.Length));
            }
        }
    }

    private static CancellationTokenSource CreateOperationTimeout(
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var remaining = deadline - DateTimeOffset.UtcNow;
        timeout.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(1));
        return timeout;
    }

    private static Task OpenBrowserAsync(Uri uri, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = uri.AbsoluteUri,
                UseShellExecute = true
            });
            return Task.CompletedTask;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            throw new InvalidOperationException("Unable to open the Nexus Mods account authorization page.", ex);
        }
    }

    private static string? ReadError(JsonElement root)
    {
        if (!root.TryGetProperty("error", out var error) || error.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return error.ValueKind == JsonValueKind.String ? error.GetString() : error.GetRawText();
    }

    private static string? ReadString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static long? ReadLong(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), out var number) => number,
            _ => null
        };
    }

    private static bool ReadBool(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when value.TryGetInt32(out var number) => number != 0,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var boolean) => boolean,
            _ => false
        };
    }

    private sealed record NexusSsoHandshake(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("token")] string? Token,
        [property: JsonPropertyName("protocol")] int Protocol);
}

internal sealed record NexusSsoFrame(
    bool Success,
    string? ConnectionToken,
    string? ApiKey,
    string? Error);
