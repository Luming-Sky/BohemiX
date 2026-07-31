using BohemiX.App.Services;
using BohemiX.Core.Models;

namespace BohemiX.App.Tests;

public sealed class NexusDownloadAuthorizationTests
{
    private static readonly NexusDownloadAuthorizationRequest Request = new(
        "kingdomcomedeliverance2",
        12,
        34,
        "Test Mod",
        "test.zip",
        new Uri("https://www.nexusmods.com/kingdomcomedeliverance2/mods/12?tab=files&file_id=34"));

    [Fact]
    public void ParserAcceptsMatchingUnexpiredNxmAuthorization()
    {
        var expires = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds();

        var parsed = NexusEmbeddedBrowserAuthService.TryParseNxmAuthorization(
            $"nxm://kingdomcomedeliverance2/mods/12/files/34?key=test-key&expires={expires}",
            Request,
            out var authorization,
            out var error);

        Assert.True(parsed, error);
        Assert.NotNull(authorization);
        Assert.Equal("test-key", authorization.Key);
        Assert.Equal(expires, authorization.Expires);
    }

    [Theory]
    [InlineData("nxm://skyrim/mods/12/files/34?key=test&expires=9999999999")]
    [InlineData("nxm://kingdomcomedeliverance2/mods/13/files/34?key=test&expires=9999999999")]
    [InlineData("nxm://kingdomcomedeliverance2/mods/12/files/35?key=test&expires=9999999999")]
    [InlineData("https://www.nexusmods.com/kingdomcomedeliverance2/mods/12")]
    public void ParserRejectsWrongGameModFileOrScheme(string value)
    {
        Assert.False(NexusEmbeddedBrowserAuthService.TryParseNxmAuthorization(
            value,
            Request,
            out _,
            out _));
    }

    [Fact]
    public void ParserRejectsExpiredAuthorization()
    {
        var expires = DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeSeconds();

        Assert.False(NexusEmbeddedBrowserAuthService.TryParseNxmAuthorization(
            $"nxm://kingdomcomedeliverance2/mods/12/files/34?key=test&expires={expires}",
            Request,
            out _,
            out var error));
        Assert.Contains("过期", error);
    }

    [Theory]
    [InlineData("https://users.nexusmods.com/account/security", "<html><body>Account security</body></html>", true)]
    [InlineData("https://users.nexusmods.com/account/preferences", "<html><body>Account preferences</body></html>", true)]
    [InlineData("https://users.nexusmods.com/auth/sign_in", "<html><body>Sign in to Nexus Mods</body></html>", false)]
    [InlineData("https://users.nexusmods.com/account/security", "<html><a href='/auth/sign_in'>Sign in</a></html>", false)]
    [InlineData("https://www.nexusmods.com/users/myaccount", "<html><body>Account settings</body></html>", true)]
    [InlineData("https://www.nexusmods.com/users/login", "<html><body>Log in to Nexus Mods</body></html>", false)]
    [InlineData("https://www.nexusmods.com/users/myaccount", "<html><a href='/users/login'>Log in</a></html>", false)]
    [InlineData("https://example.com/users/myaccount", "<html><body>Account settings</body></html>", false)]
    public void AccountPageValidatorRequiresAProtectedAuthenticatedPage(string finalUrl, string html, bool expected)
    {
        Assert.Equal(expected, NexusEmbeddedBrowserAuthService.IsAuthenticatedAccountPage(finalUrl, html));
    }
}
