using System.Net;
using System.Text;
using BohemiX.Core.Services;
using BohemiX.Infrastructure.Services;

namespace BohemiX.Core.Tests;

public sealed class MyMemoryModTranslationServiceTests
{
    [Fact]
    public async Task TranslateAsync_SendsExpectedParametersAndDecodesHtml()
    {
        var handler = new RecordingHandler(_ => JsonResponse("""
            {
              "responseData": { "translatedText": "Sword &amp; shield" },
              "responseStatus": 200
            }
            """));
        using var service = new MyMemoryModTranslationService(handler);

        var result = await service.TranslateAsync("\u5251 &amp; \u76fe", "zh", "en");

        Assert.Equal("Sword & shield", result);
        var requestUri = Assert.Single(handler.RequestUris);
        Assert.Equal("https", requestUri.Scheme);
        Assert.Equal("api.mymemory.translated.net", requestUri.Host);
        Assert.Equal("\u5251 & \u76fe", ReadQueryValue(requestUri, "q"));
        Assert.Equal("zh-CN|en", ReadQueryValue(requestUri, "langpair"));
    }

    [Fact]
    public async Task TranslateAsync_CachesCompletedTranslation()
    {
        var handler = new RecordingHandler(_ => JsonResponse("""
            {
              "responseData": { "translatedText": "\u7ffb\u8bd1" },
              "responseStatus": "200"
            }
            """));
        using var service = new MyMemoryModTranslationService(handler);

        var first = await service.TranslateAsync("Translate me", "en", "zh-CN");
        var second = await service.TranslateAsync("Translate me", "en", "zh-CN");

        Assert.Equal("\u7ffb\u8bd1", first);
        Assert.Equal(first, second);
        Assert.Single(handler.RequestUris);
    }

    [Fact]
    public async Task TranslateAsync_SplitsLongTextWithinUtf8LimitAndMergesInOrder()
    {
        var responseIndex = 0;
        var handler = new RecordingHandler(_ =>
        {
            responseIndex++;
            return JsonResponse($$"""
                {
                  "responseData": { "translatedText": "part{{responseIndex}}" },
                  "responseStatus": 200
                }
                """);
        });
        using var service = new MyMemoryModTranslationService(handler);
        var source = string.Join(' ', Enumerable.Repeat("\u6c49\u5b57\u6d4b\u8bd5", 80));

        var result = await service.TranslateAsync(source, "zh-CN", "en");

        Assert.True(handler.RequestUris.Count > 1);
        Assert.All(
            handler.RequestUris,
            uri => Assert.InRange(
                Encoding.UTF8.GetByteCount(ReadQueryValue(uri, "q")),
                1,
                MyMemoryModTranslationService.MaximumQueryUtf8Bytes));
        Assert.Equal(
            string.Join(' ', Enumerable.Range(1, handler.RequestUris.Count).Select(index => $"part{index}")),
            result);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway, ModTranslationFailureKind.Service)]
    [InlineData(HttpStatusCode.TooManyRequests, ModTranslationFailureKind.QuotaExceeded)]
    public async Task TranslateAsync_MapsHttpErrors(
        HttpStatusCode statusCode,
        ModTranslationFailureKind expectedFailure)
    {
        using var service = new MyMemoryModTranslationService(
            new RecordingHandler(_ => new HttpResponseMessage(statusCode)));

        var error = await Assert.ThrowsAsync<ModTranslationException>(
            () => service.TranslateAsync("Translate me", "en", "zh-CN"));

        Assert.Equal(expectedFailure, error.FailureKind);
    }

    [Fact]
    public async Task TranslateAsync_MapsQuotaResponse()
    {
        using var service = new MyMemoryModTranslationService(
            new RecordingHandler(_ => JsonResponse("""
                {
                  "responseData": { "translatedText": "" },
                  "quotaFinished": true,
                  "responseStatus": 429
                }
                """)));

        var error = await Assert.ThrowsAsync<ModTranslationException>(
            () => service.TranslateAsync("Translate me", "en", "zh-CN"));

        Assert.Equal(ModTranslationFailureKind.QuotaExceeded, error.FailureKind);
    }

    [Fact]
    public async Task TranslateAsync_MapsInvalidJson()
    {
        using var service = new MyMemoryModTranslationService(
            new RecordingHandler(_ => JsonResponse("not json")));

        var error = await Assert.ThrowsAsync<ModTranslationException>(
            () => service.TranslateAsync("Translate me", "en", "zh-CN"));

        Assert.Equal(ModTranslationFailureKind.InvalidResponse, error.FailureKind);
    }

    [Fact]
    public async Task TranslateAsync_MapsMissingStatusToInvalidResponse()
    {
        using var service = new MyMemoryModTranslationService(
            new RecordingHandler(_ => JsonResponse("""
                {
                  "responseData": { "translatedText": "Translation" }
                }
                """)));

        var error = await Assert.ThrowsAsync<ModTranslationException>(
            () => service.TranslateAsync("Translate me", "en", "zh-CN"));

        Assert.Equal(ModTranslationFailureKind.InvalidResponse, error.FailureKind);
    }

    [Fact]
    public async Task TranslateAsync_MapsNetworkFailure()
    {
        using var service = new MyMemoryModTranslationService(
            new RecordingHandler(_ => throw new HttpRequestException("Offline")));

        var error = await Assert.ThrowsAsync<ModTranslationException>(
            () => service.TranslateAsync("Translate me", "en", "zh-CN"));

        Assert.Equal(ModTranslationFailureKind.Network, error.FailureKind);
    }

    [Fact]
    public async Task TranslateAsync_MapsTimeoutToNetworkFailure()
    {
        using var service = new MyMemoryModTranslationService(
            new DelayingHandler(),
            TimeSpan.FromMilliseconds(20));

        var error = await Assert.ThrowsAsync<ModTranslationException>(
            () => service.TranslateAsync("Translate me", "en", "zh-CN"));

        Assert.Equal(ModTranslationFailureKind.Network, error.FailureKind);
    }

    [Fact]
    public async Task TranslateAsync_PreservesCallerCancellation()
    {
        using var service = new MyMemoryModTranslationService(new DelayingHandler());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.TranslateAsync("Translate me", "en", "zh-CN", cancellation.Token));
    }

    [Fact]
    public async Task TranslateAsync_SkipsRequestWhenLanguagesMatch()
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("No request expected."));
        using var service = new MyMemoryModTranslationService(handler);

        var result = await service.TranslateAsync(" Already translated ", "zh", "zh-CN");

        Assert.Equal("Already translated", result);
        Assert.Empty(handler.RequestUris);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static string ReadQueryValue(Uri uri, string key)
    {
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var pairKey = separator < 0 ? pair : pair[..separator];
            if (string.Equals(Uri.UnescapeDataString(pairKey), key, StringComparison.Ordinal))
            {
                return separator < 0 ? string.Empty : Uri.UnescapeDataString(pair[(separator + 1)..]);
            }
        }

        throw new InvalidOperationException($"Query parameter '{key}' was not found.");
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public List<Uri> RequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!);
            return Task.FromResult(responseFactory(request));
        }
    }

    private sealed class DelayingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The delay should always be cancelled.");
        }
    }
}
