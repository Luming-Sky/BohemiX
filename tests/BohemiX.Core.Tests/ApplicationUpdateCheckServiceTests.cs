using System.Net;
using System.Text;
using BohemiX.Core.Services;
using BohemiX.Infrastructure.Services;

namespace BohemiX.Core.Tests;

public sealed class ApplicationUpdateCheckServiceTests
{
    [Fact]
    public async Task CheckAsync_ReturnsUpdateAvailableForNewerRelease()
    {
        var service = CreateService(_ => JsonResponse("""
            { "tag_name": "v0.8.0", "html_url": "https://github.com/Luming-Sky/BohemiX/releases/tag/v0.8.0" }
            """));

        var result = await service.CheckAsync("v0.7.0");

        Assert.Equal(ApplicationUpdateAvailability.UpdateAvailable, result.Availability);
        Assert.Equal("v0.8.0", result.LatestVersion);
    }

    [Fact]
    public async Task CheckAsync_ReturnsUpToDateForMatchingRelease()
    {
        var service = CreateService(_ => JsonResponse("""
            { "tag_name": "0.7.0", "html_url": "https://github.com/Luming-Sky/BohemiX/releases/tag/v0.7.0" }
            """));

        var result = await service.CheckAsync("v0.7.0");

        Assert.Equal(ApplicationUpdateAvailability.UpToDate, result.Availability);
        Assert.Equal("v0.7.0", result.LatestVersion);
    }

    [Fact]
    public async Task CheckAsync_RejectsMalformedReleaseTag()
    {
        var service = CreateService(_ => JsonResponse("""
            { "tag_name": "latest", "html_url": "https://github.com/Luming-Sky/BohemiX/releases/latest" }
            """));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CheckAsync("v0.7.0"));
    }

    [Fact]
    public async Task CheckAsync_PropagatesNetworkFailures()
    {
        var service = CreateService(_ => throw new HttpRequestException("Network unavailable"));

        await Assert.ThrowsAsync<HttpRequestException>(() => service.CheckAsync("v0.7.0"));
    }

    [Fact]
    public async Task CheckAsync_ReturnsNoPublishedVersionWhenRepositoryHasNoReleaseOrTag()
    {
        var requestCount = 0;
        var service = CreateService(_ =>
        {
            requestCount++;
            return requestCount == 1
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : JsonResponse("[]");
        });

        var result = await service.CheckAsync("v0.7.0");

        Assert.Equal(ApplicationUpdateAvailability.NoPublishedVersion, result.Availability);
        Assert.Null(result.LatestVersion);
    }

    private static ApplicationUpdateCheckService CreateService(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) =>
        new(new HttpClient(new StubHttpMessageHandler(responseFactory)));

    private static HttpResponseMessage JsonResponse(string content) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json")
        };

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
