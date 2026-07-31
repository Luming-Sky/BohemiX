using BohemiX.Infrastructure.Services;

namespace BohemiX.Core.Tests;

public sealed class SteamLoginAccountReaderTests
{
    [Fact]
    public void ParseActiveAccount_SelectsRegistryActiveUserInsteadOfMostRecentFlag()
    {
        const ulong inactiveSteamId = 76561198000000001;
        const ulong activeSteamId = 76561198000000002;
        const string content = """
            "users"
            {
                "76561198000000001"
                {
                    "AccountName" "inactive"
                    "PersonaName" "Inactive User"
                    "MostRecent" "1"
                }
                "76561198000000002"
                {
                    "AccountName" "active"
                    "PersonaName" "Henry \"The Bold\""
                    "MostRecent" "0"
                }
            }
            """;

        var account = SteamLoginAccountReader.ParseActiveAccount(content, unchecked((uint)activeSteamId));

        Assert.NotNull(account);
        Assert.Equal(activeSteamId, account.SteamId);
        Assert.Equal("Henry \"The Bold\"", account.PersonaName);
        Assert.NotEqual(inactiveSteamId, account.SteamId);
    }

    [Fact]
    public void ParseActiveAccount_ReturnsNullWhenActiveUserIsNotPresent()
    {
        const string content = """
            "users"
            {
                "76561198000000001"
                {
                    "AccountName" "henry"
                    "PersonaName" "Henry"
                }
            }
            """;

        var account = SteamLoginAccountReader.ParseActiveAccount(content, 42);

        Assert.Null(account);
    }

    [Fact]
    public void ParseActiveAccount_FallsBackToAccountName()
    {
        const ulong steamId = 76561198000000003;
        const string content = """
            "users"
            {
                "76561198000000003"
                {
                    "AccountName" "theresa"
                    "PersonaName" ""
                }
            }
            """;

        var account = SteamLoginAccountReader.ParseActiveAccount(content, unchecked((uint)steamId));

        Assert.NotNull(account);
        Assert.Equal("theresa", account.PersonaName);
    }

    [Fact]
    public void SelectSteamExecutablePath_PrefersFirstExistingCandidate()
    {
        var first = Path.GetFullPath(@"D:\Steam\steam.exe");
        var second = Path.GetFullPath(@"C:\Program Files (x86)\Steam\steam.exe");

        var result = SteamLoginAccountReader.SelectSteamExecutablePath(
            [@"D:\Steam\steam.exe", @"C:\Program Files (x86)\Steam\steam.exe"],
            path => path == first || path == second);

        Assert.Equal(first, result);
    }

    [Fact]
    public void SelectSteamExecutablePath_SkipsMissingAndQuotedCandidates()
    {
        var fallback = Path.GetFullPath(@"D:\Steam\steam.exe");

        var result = SteamLoginAccountReader.SelectSteamExecutablePath(
            [@"C:\Missing\steam.exe", "  \"D:\\Steam\\steam.exe\"  "],
            path => path == fallback);

        Assert.Equal(fallback, result);
    }
}
