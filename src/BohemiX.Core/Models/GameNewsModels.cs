namespace BohemiX.Core.Models;

public sealed record GameNewsItem(
    string Id,
    string Title,
    string Summary,
    string Url,
    string Author,
    string Source,
    DateTimeOffset PublishedAtUtc,
    string? ImageUrl = null,
    string? ImagePath = null);

public sealed record GameNewsFeed(
    IReadOnlyList<GameNewsItem> Items,
    DateTimeOffset UpdatedAtUtc,
    bool IsFromCache = false,
    bool IsStale = false);
