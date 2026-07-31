using System.Text.Json;
using BohemiX.Core.Models;
using BohemiX.Infrastructure.Services;

namespace BohemiX.Core.Tests;

public sealed class WorkshopServiceUtilitiesTests : IDisposable
{
    private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "BohemiX.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void CreateSummary_CompactsWhitespace_AndTruncatesLongDescriptions()
    {
        var input = "  one   two\r\nthree\tfour  " + new string('x', 260);

        var summary = WorkshopServiceUtilities.CreateSummary(input);

        Assert.DoesNotContain("  ", summary);
        Assert.StartsWith("one two three four", summary);
        Assert.EndsWith("...", summary);
        Assert.Equal(223, summary.Length);
    }

    [Fact]
    public void SortSearchResults_UpdatedOrdersByUpdatedAtDescending()
    {
        var older = CreateWorkshopMod(3, updatedAt: new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var newer = CreateWorkshopMod(1, updatedAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var missingUpdated = CreateWorkshopMod(2, updatedAt: null);

        var sorted = WorkshopServiceUtilities.SortSearchResults(
            [older, missingUpdated, newer],
            WorkshopSortOrder.Updated);

        Assert.Equal([1UL, 3UL, 2UL], sorted.Select(mod => mod.PublishedFileId));
    }

    [Fact]
    public void SortSearchResults_HotPreservesSteamOrder()
    {
        var first = CreateWorkshopMod(30, updatedAt: new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var second = CreateWorkshopMod(10, updatedAt: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var sorted = WorkshopServiceUtilities.SortSearchResults(
            [first, second],
            WorkshopSortOrder.Hot);

        Assert.Equal([30UL, 10UL], sorted.Select(mod => mod.PublishedFileId));
    }

    [Fact]
    public void SanitizePathSegment_ReplacesInvalidCharacters_AndFallsBackForBlankInput()
    {
        var invalidPrefix = new string(Path.GetInvalidFileNameChars().Take(3).ToArray());

        var sanitized = WorkshopServiceUtilities.SanitizePathSegment($"  {invalidPrefix}Steam:Workshop?  ");
        var fallback = WorkshopServiceUtilities.SanitizePathSegment("   ");

        Assert.All(Path.GetInvalidFileNameChars(), invalid => Assert.DoesNotContain(invalid, sanitized));
        Assert.False(string.IsNullOrWhiteSpace(sanitized));
        Assert.Equal("workshop-mod", fallback);
    }

    [Fact]
    public void EnsureUniqueDirectory_AppendsSuffix_WhenDirectoryAlreadyExists()
    {
        Directory.CreateDirectory(tempRoot);
        var existing = Path.Combine(tempRoot, "steam-123-sample");
        Directory.CreateDirectory(existing);

        var unique = WorkshopServiceUtilities.EnsureUniqueDirectory(existing);

        Assert.NotEqual(existing, unique);
        Assert.Equal(existing + "-2", unique);
    }

    [Fact]
    public void IsPathInsideDirectory_RejectsPrefixSimilarSibling()
    {
        Directory.CreateDirectory(tempRoot);
        var modsRoot = Path.Combine(tempRoot, "Mods");
        var inside = Path.Combine(modsRoot, "steam-1-test");
        var sibling = Path.Combine(tempRoot, "Mods-archive", "steam-1-test");

        Assert.True(WorkshopServiceUtilities.IsPathInsideDirectory(modsRoot, inside));
        Assert.False(WorkshopServiceUtilities.IsPathInsideDirectory(modsRoot, sibling));
    }

    [Fact]
    public async Task WriteJsonAtomicallyAsync_WritesReadableJsonFile()
    {
        Directory.CreateDirectory(tempRoot);
        var path = Path.Combine(tempRoot, "workshop_mapping.json");
        var payload = new Dictionary<string, object?>
        {
            ["publishedFileId"] = 123456UL,
            ["title"] = "Test Mod"
        };

        await WorkshopServiceUtilities.WriteJsonAtomicallyAsync(
            path,
            payload,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true },
            CancellationToken.None);

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.Equal(123456UL, document.RootElement.GetProperty("publishedFileId").GetUInt64());
        Assert.Equal("Test Mod", document.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public void CreateSteamInitializationException_ClassifiesOwnershipFailures()
    {
        var exception = new Exception("SteamApi_Init returned false. Steam isn't running, couldn't find Steam, AppId is ureleased, Don't own AppId.");

        var workshopException = WorkshopServiceUtilities.CreateSteamInitializationException(exception, 1771300);

        Assert.Equal(WorkshopFailureKind.GameNotOwned, workshopException.FailureKind);
        Assert.Contains("1771300", workshopException.Message);
        Assert.Same(exception, workshopException.InnerException);
    }

    [Fact]
    public void CreateSteamInitializationException_ClassifiesLoginFailures()
    {
        var exception = new Exception("Steam user is not logged in.");

        var workshopException = WorkshopServiceUtilities.CreateSteamInitializationException(exception, 1771300);

        Assert.Equal(WorkshopFailureKind.NotLoggedIn, workshopException.FailureKind);
        Assert.Contains("not logged in", workshopException.Message, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(tempRoot))
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static WorkshopModInfo CreateWorkshopMod(ulong publishedFileId, DateTimeOffset? updatedAt)
    {
        return new WorkshopModInfo(
            publishedFileId,
            $"Mod {publishedFileId}",
            string.Empty,
            string.Empty,
            "Steam user",
            null,
            null,
            new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero),
            updatedAt,
            0,
            0,
            0,
            0,
            []);
    }
}
