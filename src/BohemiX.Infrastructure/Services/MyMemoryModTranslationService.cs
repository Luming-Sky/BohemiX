using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BohemiX.Core.Services;

namespace BohemiX.Infrastructure.Services;

public sealed class MyMemoryModTranslationService : IModTranslationService, IDisposable
{
    internal const int MaximumQueryUtf8Bytes = 450;
    private static readonly Uri Endpoint = new("https://api.mymemory.translated.net/get");
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private readonly ConcurrentDictionary<string, string> cache = new(StringComparer.Ordinal);
    private readonly HttpClient httpClient;

    public MyMemoryModTranslationService()
        : this(new HttpClientHandler { AllowAutoRedirect = false })
    {
    }

    internal MyMemoryModTranslationService(HttpMessageHandler handler, TimeSpan? requestTimeout = null)
    {
        httpClient = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = requestTimeout ?? RequestTimeout
        };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("BohemiX/0.9.1");
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<string> TranslateAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceLanguage);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetLanguage);

        var normalizedText = WebUtility.HtmlDecode(text).Trim();
        var normalizedSource = NormalizeLanguage(sourceLanguage);
        var normalizedTarget = NormalizeLanguage(targetLanguage);
        if (string.Equals(normalizedSource, normalizedTarget, StringComparison.OrdinalIgnoreCase))
        {
            return normalizedText;
        }

        var cacheKey = string.Join('\n', normalizedSource, normalizedTarget, normalizedText);
        if (cache.TryGetValue(cacheKey, out var cachedTranslation))
        {
            return cachedTranslation;
        }

        var translatedChunks = new List<string>();
        foreach (var chunk in SplitText(normalizedText))
        {
            translatedChunks.Add(await TranslateChunkAsync(
                chunk,
                normalizedSource,
                normalizedTarget,
                cancellationToken).ConfigureAwait(false));
        }

        var translation = string.Join(' ', translatedChunks.Where(chunk => !string.IsNullOrWhiteSpace(chunk))).Trim();
        if (string.IsNullOrWhiteSpace(translation))
        {
            throw new ModTranslationException(
                ModTranslationFailureKind.InvalidResponse,
                "The translation service returned an empty translation.");
        }

        cache.TryAdd(cacheKey, translation);
        return translation;
    }

    internal static IReadOnlyList<string> SplitText(string text, int maximumUtf8Bytes = MaximumQueryUtf8Bytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumUtf8Bytes, 4);

        var normalized = WebUtility.HtmlDecode(text).Trim();
        var chunks = new List<string>();
        var offset = 0;
        while (offset < normalized.Length)
        {
            while (offset < normalized.Length && char.IsWhiteSpace(normalized[offset]))
            {
                offset++;
            }

            if (offset >= normalized.Length)
            {
                break;
            }

            var maximumEnd = FindMaximumEnd(normalized, offset, maximumUtf8Bytes);
            var end = maximumEnd;
            if (maximumEnd < normalized.Length)
            {
                var preferredBoundary = FindPreferredBoundary(normalized, offset, maximumEnd);
                if (preferredBoundary > offset)
                {
                    end = preferredBoundary;
                }
            }

            var chunk = normalized[offset..end].Trim();
            if (chunk.Length > 0)
            {
                chunks.Add(chunk);
            }

            offset = end;
        }

        return chunks;
    }

    private async Task<string> TranslateChunkAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        var requestUri = new UriBuilder(Endpoint)
        {
            Query = $"q={Uri.EscapeDataString(text)}&langpair={Uri.EscapeDataString($"{sourceLanguage}|{targetLanguage}")}"
        }.Uri;

        try
        {
            using var response = await httpClient.GetAsync(
                requestUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new ModTranslationException(
                    response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
                        ? ModTranslationFailureKind.QuotaExceeded
                        : ModTranslationFailureKind.Service,
                    $"The translation service returned HTTP {(int)response.StatusCode}.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;

            if (root.TryGetProperty("quotaFinished", out var quotaFinished)
                && quotaFinished.ValueKind == JsonValueKind.True)
            {
                throw new ModTranslationException(
                    ModTranslationFailureKind.QuotaExceeded,
                    "The public translation quota has been exhausted.");
            }

            if (!TryReadResponseStatus(root, out var responseStatus))
            {
                throw new ModTranslationException(
                    ModTranslationFailureKind.InvalidResponse,
                    "The translation response did not contain a valid status.");
            }

            if (responseStatus != 200)
            {
                throw new ModTranslationException(
                    responseStatus == 403 || responseStatus == 429
                        ? ModTranslationFailureKind.QuotaExceeded
                        : ModTranslationFailureKind.Service,
                    $"The translation service returned status {responseStatus}.");
            }

            if (!root.TryGetProperty("responseData", out var responseData)
                || !responseData.TryGetProperty("translatedText", out var translatedTextElement)
                || translatedTextElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(translatedTextElement.GetString()))
            {
                throw new ModTranslationException(
                    ModTranslationFailureKind.InvalidResponse,
                    "The translation response did not contain translated text.");
            }

            return WebUtility.HtmlDecode(translatedTextElement.GetString()!).Trim();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ModTranslationException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            throw new ModTranslationException(
                ModTranslationFailureKind.Network,
                "Unable to reach the translation service.",
                ex);
        }
        catch (JsonException ex)
        {
            throw new ModTranslationException(
                ModTranslationFailureKind.InvalidResponse,
                "The translation service returned invalid JSON.",
                ex);
        }
    }

    private static bool TryReadResponseStatus(JsonElement root, out int status)
    {
        status = 0;
        if (!root.TryGetProperty("responseStatus", out var value))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetInt32(out status),
            JsonValueKind.String => int.TryParse(value.GetString(), out status),
            _ => false
        };
    }

    private static int FindMaximumEnd(string text, int offset, int maximumUtf8Bytes)
    {
        var bytes = 0;
        var index = offset;
        while (index < text.Length)
        {
            var characterLength = index + 1 < text.Length && char.IsSurrogatePair(text[index], text[index + 1]) ? 2 : 1;
            var characterBytes = Encoding.UTF8.GetByteCount(text.AsSpan(index, characterLength));
            if (bytes + characterBytes > maximumUtf8Bytes)
            {
                break;
            }

            bytes += characterBytes;
            index += characterLength;
        }

        return index == offset ? Math.Min(text.Length, offset + 1) : index;
    }

    private static int FindPreferredBoundary(string text, int offset, int maximumEnd)
    {
        var minimumBoundary = offset + ((maximumEnd - offset) / 2);
        for (var index = maximumEnd - 1; index >= minimumBoundary; index--)
        {
            if (char.IsWhiteSpace(text[index]) || IsSentenceBoundary(text[index]))
            {
                return index + 1;
            }
        }

        return maximumEnd;
    }

    private static bool IsSentenceBoundary(char value) => value is '.' or '!' or '?' or ';' or '\n'
        or '\u3002' or '\uff01' or '\uff1f' or '\uff1b';

    private static string NormalizeLanguage(string language)
    {
        var normalized = language.Trim();
        return normalized.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-CN" : "en";
    }

    public void Dispose() => httpClient.Dispose();
}
