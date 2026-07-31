using System.Diagnostics;
using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BohemiX.Core.Models;
using BohemiX.Core.Services;
using Microsoft.Data.Sqlite;
using Serilog;

namespace BohemiX.Infrastructure.Services;

public sealed class NexusCookieAuthService : INexusCookieAuthService
{
    private const string NexusCookieHost = "nexusmods.com";
    private const string ChromiumCookieQuery = """
        SELECT host_key, name, encrypted_value, value, expires_utc, is_secure
        FROM cookies
        WHERE host_key = 'nexusmods.com'
           OR host_key = '.nexusmods.com'
           OR host_key LIKE '%.nexusmods.com'
        ORDER BY host_key, name
        """;

    private const string FirefoxCookieQuery = """
        SELECT host, name, value, expiry, isSecure
        FROM moz_cookies
        WHERE host = 'nexusmods.com'
           OR host = '.nexusmods.com'
           OR host LIKE '%.nexusmods.com'
        ORDER BY host, name
        """;

    private static readonly TimeSpan ProbeCopyFreshness = TimeSpan.FromMinutes(2);

    private readonly ILogger logger;
    private readonly IAppSettingsService appSettingsService;
    private readonly Func<DateTimeOffset> utcNow;

    public NexusCookieAuthService(ILogger logger, IAppSettingsService appSettingsService)
        : this(logger, appSettingsService, () => DateTimeOffset.UtcNow)
    {
    }

    internal NexusCookieAuthService(ILogger logger, IAppSettingsService appSettingsService, Func<DateTimeOffset> utcNow)
    {
        this.logger = logger.ForContext<NexusCookieAuthService>();
        this.appSettingsService = appSettingsService;
        this.utcNow = utcNow;
    }

    public async Task<NexusCookieAuthProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var settings = await appSettingsService.LoadAsync(cancellationToken);
        if (!settings.AllowNexusBrowserCookieAuth)
        {
            return new NexusCookieAuthProbeResult(
                false,
                null,
                "NEXUS_COOKIE_AUTH_DISABLED",
                "Nexus browser Cookie auth is disabled in settings.",
                []);
        }

        var attempts = new List<NexusCookieAuthProbeAttempt>();
        string? lastErrorCode = null;
        foreach (var browser in BrowserCookieSource.CreateProbeOrder())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var probe = await TryProbeBrowserAsync(browser, cancellationToken);
            if (probe.Lease is not null)
            {
                probe.Lease.Dispose();
                logger.Information("Nexus browser Cookie auth probe succeeded from {BrowserName}", browser.Name);
                return new NexusCookieAuthProbeResult(
                    true,
                    browser.Name,
                    null,
                    $"Nexus browser sign-in detected from {browser.Name}.",
                    attempts);
            }

            lastErrorCode = probe.ErrorCode;
            var errorCode = probe.ErrorCode ?? "NEXUS_COOKIE_NOT_FOUND_OR_EXPIRED";
            attempts.Add(new NexusCookieAuthProbeAttempt(
                browser.Name,
                errorCode,
                BuildProbeFailureMessage(errorCode)));
            logger.Warning(
                "Nexus browser Cookie auth probe skipped {BrowserName}: {ErrorCode} - {Message}",
                browser.Name,
                errorCode,
                BuildProbeFailureMessage(errorCode));
        }

        var primaryErrorCode = SelectPrimaryErrorCode(attempts) ?? lastErrorCode ?? "NEXUS_COOKIE_NOT_FOUND_OR_EXPIRED";
        return new NexusCookieAuthProbeResult(
            false,
            null,
            primaryErrorCode,
            BuildProbeFailureMessage(primaryErrorCode),
            attempts);
    }

    public async Task<NexusCookieAuthLease?> TryCreateCookieHeaderLeaseAsync(
        Uri requestUri,
        CancellationToken cancellationToken = default)
    {
        AssertNexusModsUri(requestUri);
        var settings = await appSettingsService.LoadAsync(cancellationToken);
        if (!settings.AllowNexusBrowserCookieAuth)
        {
            return null;
        }

        foreach (var browser in BrowserCookieSource.CreateProbeOrder())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var probe = await TryProbeBrowserAsync(browser, cancellationToken);
            if (probe.Lease is not null)
            {
                logger.Information("Nexus browser Cookie auth session detected from {BrowserName}", browser.Name);
                return probe.Lease;
            }

            logger.Warning(
                "Nexus browser Cookie auth probe skipped {BrowserName}: {ErrorCode} - {Message}",
                browser.Name,
                probe.ErrorCode,
                BuildProbeFailureMessage(probe.ErrorCode));
        }

        return null;
    }

    public void ClearSession()
    {
    }

    public static void AssertNexusModsUri(Uri requestUri)
    {
        if (!IsNexusModsHost(requestUri.Host))
        {
            throw new InvalidOperationException("Refusing to attach Nexus browser Cookie auth to a non-Nexus Mods domain.");
        }
    }

    public static bool IsNexusModsHost(string host)
    {
        return string.Equals(host, NexusCookieHost, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("." + NexusCookieHost, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNexusCookieDomain(string host)
    {
        return IsNexusModsHost(host.TrimStart('.'));
    }

    private async Task<ProbeAttempt> TryProbeBrowserAsync(
        BrowserCookieSource browser,
        CancellationToken cancellationToken)
    {
        try
        {
            return browser.Kind == BrowserCookieKind.Firefox
                ? await TryProbeFirefoxAsync(browser, cancellationToken)
                : await TryProbeChromiumAsync(browser, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or InvalidOperationException or SqliteException)
        {
            logger.Warning(
                "Nexus browser Cookie auth probe failed for {BrowserName}: {ErrorCode}",
                browser.Name,
                SanitizeErrorCode(ex));
            return ProbeAttempt.Failed(SanitizeErrorCode(ex));
        }
    }

    private async Task<ProbeAttempt> TryProbeChromiumAsync(
        BrowserCookieSource browser,
        CancellationToken cancellationToken)
    {
        var cookieDatabasePath = browser.ResolveCookieDatabasePath();
        if (cookieDatabasePath is null || !File.Exists(cookieDatabasePath))
        {
            return ProbeAttempt.Failed("BROWSER_COOKIE_DB_NOT_FOUND");
        }

        var localStatePath = browser.ResolveLocalStatePath();
        var masterKey = localStatePath is null ? null : await TryReadChromiumMasterKeyAsync(localStatePath, browser, cancellationToken);
        using var copiedDb = CopyDatabaseToTemporaryFile(cookieDatabasePath);
        var cookies = new List<BrowserCookie>();

        try
        {
            await using var connection = CreateReadOnlyConnection(copiedDb.Path);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = ChromiumCookieQuery;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var host = reader.GetString(0);
                var name = reader.GetString(1);
                var encryptedValue = reader.IsDBNull(2) ? [] : (byte[])reader["encrypted_value"];
                try
                {
                    if (!IsNexusCookieDomain(host))
                    {
                        continue;
                    }

                    var plainValue = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
                    var expiresUtc = reader.IsDBNull(4) ? 0 : reader.GetInt64(4);
                    var isSecure = !reader.IsDBNull(5) && reader.GetInt32(5) != 0;

                    if (IsExpiredChromiumCookie(expiresUtc))
                    {
                        continue;
                    }

                    string? decryptErrorCode = null;
                    string? value;
                    if (!string.IsNullOrEmpty(plainValue))
                    {
                        value = plainValue;
                    }
                    else
                    {
                        value = TryDecryptChromiumCookie(browser, encryptedValue, masterKey, out decryptErrorCode);
                    }

                    if (decryptErrorCode is not null)
                    {
                        return ProbeAttempt.Failed(decryptErrorCode);
                    }

                    if (!string.IsNullOrEmpty(value))
                    {
                        cookies.Add(new BrowserCookie(host, name, value, isSecure));
                    }
                }
                finally
                {
                    ZeroBytes(encryptedValue);
                }
            }

            ZeroBytes(masterKey);
            return CreateLeaseFromCookies(browser.Name, cookies);
        }
        catch
        {
            DisposeCookies(cookies);
            throw;
        }
        finally
        {
            ZeroBytes(masterKey);
        }
    }

    private async Task<ProbeAttempt> TryProbeFirefoxAsync(
        BrowserCookieSource browser,
        CancellationToken cancellationToken)
    {
        var cookieDatabasePath = browser.ResolveCookieDatabasePath();
        if (cookieDatabasePath is null || !File.Exists(cookieDatabasePath))
        {
            return ProbeAttempt.Failed("BROWSER_COOKIE_DB_NOT_FOUND");
        }

        using var nssSession = FirefoxNssRuntime.TryInitialize(browser.ResolveFirefoxProfileDirectory());
        if (nssSession is null)
        {
            return ProbeAttempt.Failed("FIREFOX_NSS_UNAVAILABLE");
        }

        using var copiedDb = CopyDatabaseToTemporaryFile(cookieDatabasePath);
        var cookies = new List<BrowserCookie>();

        try
        {
            await using var connection = CreateReadOnlyConnection(copiedDb.Path);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = FirefoxCookieQuery;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var expiry = reader.IsDBNull(3) ? 0 : reader.GetInt64(3);
                var host = reader.GetString(0);
                if (!IsNexusCookieDomain(host))
                {
                    continue;
                }

                if (expiry > 0 && DateTimeOffset.FromUnixTimeSeconds(expiry) <= utcNow())
                {
                    continue;
                }

                var value = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                if (!string.IsNullOrEmpty(value))
                {
                    cookies.Add(new BrowserCookie(
                        host,
                        reader.GetString(1),
                        value,
                        !reader.IsDBNull(4) && reader.GetInt32(4) != 0));
                }
            }

            return CreateLeaseFromCookies(browser.Name, cookies);
        }
        catch
        {
            DisposeCookies(cookies);
            throw;
        }
    }

    private ProbeAttempt CreateLeaseFromCookies(string browserName, IReadOnlyList<BrowserCookie> cookies)
    {
        if (cookies.Count == 0)
        {
            return ProbeAttempt.Failed("NEXUS_COOKIE_NOT_FOUND_OR_EXPIRED");
        }

        try
        {
            var header = string.Join("; ", cookies.Select(cookie => $"{cookie.Name}={cookie.Value.RevealAsString()}"));
            return ProbeAttempt.Succeeded(new NexusCookieAuthLease(browserName, SecureCookieHeader.FromPlainText(header)));
        }
        finally
        {
            DisposeCookies(cookies);
        }
    }

    private static void DisposeCookies(IEnumerable<BrowserCookie> cookies)
    {
        foreach (var cookie in cookies)
        {
            cookie.Dispose();
        }
    }

    private static SqliteConnection CreateReadOnlyConnection(string databasePath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        };

        return new SqliteConnection(builder.ToString());
    }

    private static TemporaryDatabaseCopy CopyDatabaseToTemporaryFile(string databasePath)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"bohemix-nexus-cookie-{Guid.NewGuid():N}.sqlite");
        File.Copy(databasePath, tempPath, overwrite: true);
        File.SetLastWriteTimeUtc(tempPath, DateTime.UtcNow.Subtract(ProbeCopyFreshness));
        return new TemporaryDatabaseCopy(tempPath);
    }

    private static async Task<byte[]?> TryReadChromiumMasterKeyAsync(
        string localStatePath,
        BrowserCookieSource browser,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(localStatePath))
        {
            return null;
        }

        await using var stream = File.OpenRead(localStatePath);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("os_crypt", out var osCrypt)
            || !osCrypt.TryGetProperty("encrypted_key", out var encryptedKeyElement))
        {
            return null;
        }

        var encryptedKey = Convert.FromBase64String(encryptedKeyElement.GetString() ?? string.Empty);
        try
        {
            var protectedKey = encryptedKey;
            if (encryptedKey.Length > 5 && Encoding.ASCII.GetString(encryptedKey, 0, 5) == "DPAPI")
            {
                protectedKey = encryptedKey[5..];
            }

            if (OperatingSystem.IsWindows())
            {
                return WindowsDpapi.Unprotect(protectedKey);
            }

            var safeStorageKey = await browser.TryReadSafeStorageKeyAsync(cancellationToken);
            if (safeStorageKey is null)
            {
                return null;
            }

            return DeriveChromiumSafeStorageKey(safeStorageKey);
        }
        finally
        {
            ZeroBytes(encryptedKey);
        }
    }

    private static string? TryDecryptChromiumCookie(
        BrowserCookieSource browser,
        byte[] encryptedValue,
        byte[]? masterKey,
        out string? errorCode)
    {
        errorCode = null;
        if (encryptedValue.Length == 0)
        {
            return null;
        }

        if (IsChromiumAppBoundCookie(encryptedValue))
        {
            errorCode = "CHROMIUM_APP_BOUND_COOKIE_ENCRYPTION";
            return null;
        }

        if (OperatingSystem.IsWindows() && !IsChromiumAeadCookie(encryptedValue))
        {
            return DecodeUtf8(WindowsDpapi.Unprotect(encryptedValue));
        }

        if (IsChromiumAeadCookie(encryptedValue) && masterKey is not null)
        {
            var value = DecryptChromiumAeadCookie(encryptedValue, masterKey);
            if (value is null)
            {
                errorCode = "BROWSER_COOKIE_DECRYPT_FAILED";
            }

            return value;
        }

        if ((OperatingSystem.IsMacOS() || OperatingSystem.IsLinux()) && masterKey is not null)
        {
            return DecryptChromiumCbcCookie(encryptedValue, masterKey);
        }

        errorCode = "BROWSER_COOKIE_DECRYPT_FAILED";
        return null;
    }

    private static bool IsChromiumAeadCookie(byte[] encryptedValue)
    {
        return encryptedValue.Length > 3
            && encryptedValue[0] == (byte)'v'
            && char.IsAsciiDigit((char)encryptedValue[1])
            && char.IsAsciiDigit((char)encryptedValue[2]);
    }

    private static bool IsChromiumAppBoundCookie(byte[] encryptedValue)
    {
        return encryptedValue.Length > 3
            && encryptedValue[0] == (byte)'v'
            && encryptedValue[1] == (byte)'2'
            && encryptedValue[2] == (byte)'0';
    }

    private static string? DecryptChromiumAeadCookie(byte[] encryptedValue, byte[] key)
    {
        if (encryptedValue.Length < 3 + 12 + 16)
        {
            return null;
        }

        var nonce = encryptedValue.AsSpan(3, 12);
        var cipherText = encryptedValue.AsSpan(15, encryptedValue.Length - 15 - 16);
        var tag = encryptedValue.AsSpan(encryptedValue.Length - 16, 16);
        var plainText = new byte[cipherText.Length];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(nonce, cipherText, tag, plainText);
            return DecodeUtf8(plainText);
        }
        finally
        {
            ZeroBytes(plainText);
        }
    }

    private static string? DecryptChromiumCbcCookie(byte[] encryptedValue, byte[] key)
    {
        var payload = IsChromiumAeadCookie(encryptedValue) ? encryptedValue[3..] : encryptedValue;
        using var aes = Aes.Create();
        aes.Key = key[..Math.Min(16, key.Length)];
        aes.IV = Encoding.ASCII.GetBytes("                ");
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        try
        {
            using var decryptor = aes.CreateDecryptor();
            var plain = decryptor.TransformFinalBlock(payload, 0, payload.Length);
            try
            {
                return DecodeUtf8(plain);
            }
            finally
            {
                ZeroBytes(plain);
            }
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static byte[] DeriveChromiumSafeStorageKey(string safeStorageKey)
    {
        var salt = Encoding.ASCII.GetBytes("saltysalt");
        using var deriveBytes = new Rfc2898DeriveBytes(
            safeStorageKey,
            salt,
            OperatingSystem.IsMacOS() ? 1003 : 1,
            HashAlgorithmName.SHA1);
        return deriveBytes.GetBytes(16);
    }

    private bool IsExpiredChromiumCookie(long expiresUtc)
    {
        if (expiresUtc <= 0)
        {
            return false;
        }

        try
        {
            var unixMicroseconds = expiresUtc - 11644473600000000L;
            return DateTimeOffset.FromUnixTimeMilliseconds(unixMicroseconds / 1000) <= utcNow();
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static string DecodeUtf8(byte[] bytes)
    {
        return Encoding.UTF8.GetString(bytes);
    }

    private static void ZeroBytes(byte[]? bytes)
    {
        if (bytes is not null)
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static string SanitizeErrorCode(Exception exception)
    {
        return exception switch
        {
            UnauthorizedAccessException => "BROWSER_COOKIE_PERMISSION_DENIED",
            IOException => "BROWSER_COOKIE_IO_UNAVAILABLE",
            CryptographicException => "BROWSER_COOKIE_DECRYPT_FAILED",
            SqliteException => "BROWSER_COOKIE_SQLITE_FAILED",
            _ => "BROWSER_COOKIE_PROBE_FAILED"
        };
    }

    private static string BuildProbeFailureMessage(string? errorCode)
    {
        return errorCode switch
        {
            "BROWSER_COOKIE_DB_NOT_FOUND" => "No supported browser Cookie database was found. Sign in to Nexus Mods in Edge, Chrome, Firefox, or Brave.",
            "NEXUS_COOKIE_NOT_FOUND_OR_EXPIRED" => "No active Nexus Mods browser session Cookie was found. Open nexusmods.com in the browser and confirm you are signed in.",
            "BROWSER_COOKIE_DECRYPT_FAILED" => "BohemiX found browser cookies but could not decrypt them for this Windows profile.",
            "CHROMIUM_APP_BOUND_COOKIE_ENCRYPTION" => "This Chromium browser stores Nexus cookies with app-bound encryption. Try Firefox browser sign-in or use a Nexus API key.",
            "BROWSER_COOKIE_PERMISSION_DENIED" => "BohemiX could not read the browser Cookie database. Close the browser or check file permissions, then retry.",
            "FIREFOX_NSS_UNAVAILABLE" => "Firefox Cookie support is unavailable because the NSS runtime could not be loaded.",
            _ => "BohemiX could not detect a usable Nexus Mods browser sign-in."
        };
    }

    private static string? SelectPrimaryErrorCode(IReadOnlyList<NexusCookieAuthProbeAttempt> attempts)
    {
        foreach (var preferred in new[]
                 {
                     "CHROMIUM_APP_BOUND_COOKIE_ENCRYPTION",
                     "BROWSER_COOKIE_DECRYPT_FAILED",
                     "BROWSER_COOKIE_PERMISSION_DENIED",
                     "NEXUS_COOKIE_NOT_FOUND_OR_EXPIRED",
                     "FIREFOX_NSS_UNAVAILABLE"
                 })
        {
            if (attempts.Any(attempt => string.Equals(attempt.ErrorCode, preferred, StringComparison.Ordinal)))
            {
                return preferred;
            }
        }

        return attempts.LastOrDefault()?.ErrorCode;
    }

    private sealed record ProbeAttempt(NexusCookieAuthLease? Lease, string? ErrorCode)
    {
        public static ProbeAttempt Succeeded(NexusCookieAuthLease lease)
        {
            return new ProbeAttempt(lease, null);
        }

        public static ProbeAttempt Failed(string errorCode)
        {
            return new ProbeAttempt(null, errorCode);
        }
    }

    private sealed class BrowserCookie : IDisposable
    {
        public BrowserCookie(string host, string name, string value, bool isSecure)
        {
            Host = host;
            Name = name;
            Value = SecureCookieHeader.FromPlainText(value);
            IsSecure = isSecure;
        }

        public string Host { get; }

        public string Name { get; }

        public SecureCookieHeader Value { get; }

        public bool IsSecure { get; }

        public void Dispose()
        {
            Value.Dispose();
        }
    }

    private sealed class TemporaryDatabaseCopy : IDisposable
    {
        public TemporaryDatabaseCopy(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                File.Delete(Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private enum BrowserCookieKind
    {
        Chromium,
        Firefox
    }

    private sealed record BrowserCookieSource(
        string Name,
        BrowserCookieKind Kind,
        string? WindowsUserDataPath,
        string? MacProfileRoot,
        string? LinuxProfileRoot,
        string? SafeStorageServiceName)
    {
        public static IReadOnlyList<BrowserCookieSource> CreateProbeOrder()
        {
            return
            [
                new("Chrome", BrowserCookieKind.Chromium, @"Google\Chrome\User Data", "Google/Chrome", "google-chrome", "Chrome Safe Storage"),
                new("Edge", BrowserCookieKind.Chromium, @"Microsoft\Edge\User Data", "Microsoft Edge", "microsoft-edge", "Microsoft Edge Safe Storage"),
                new("Firefox", BrowserCookieKind.Firefox, null, "Firefox", "firefox", null),
                new("Brave", BrowserCookieKind.Chromium, @"BraveSoftware\Brave-Browser\User Data", "BraveSoftware/Brave-Browser", "BraveSoftware/Brave-Browser", "Brave Safe Storage")
            ];
        }

        public string? ResolveCookieDatabasePath()
        {
            if (Kind == BrowserCookieKind.Firefox)
            {
                return ResolveFirefoxCookieDatabasePath();
            }

            var profileRoot = ResolveChromiumUserDataRoot();
            if (profileRoot is null)
            {
                return null;
            }

            foreach (var profile in EnumerateChromiumProfiles(profileRoot))
            {
                var candidate = System.IO.Path.Combine(profile, "Network", "Cookies");
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                candidate = System.IO.Path.Combine(profile, "Cookies");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        public string? ResolveLocalStatePath()
        {
            var profileRoot = ResolveChromiumUserDataRoot();
            return profileRoot is null ? null : System.IO.Path.Combine(profileRoot, "Local State");
        }

        public async Task<string?> TryReadSafeStorageKeyAsync(CancellationToken cancellationToken)
        {
            if (OperatingSystem.IsMacOS())
            {
                return await RunProcessForSingleLineAsync(
                    "security",
                    ["find-generic-password", "-w", "-s", SafeStorageServiceName ?? string.Empty],
                    cancellationToken);
            }

            if (OperatingSystem.IsLinux())
            {
                var secretToolValue = await RunProcessForSingleLineAsync(
                    "secret-tool",
                    ["lookup", "application", SafeStorageServiceName ?? Name],
                    cancellationToken);
                if (!string.IsNullOrWhiteSpace(secretToolValue))
                {
                    return secretToolValue;
                }

                return await RunProcessForSingleLineAsync(
                    "kwallet-query",
                    ["-r", SafeStorageServiceName ?? Name, "kdewallet"],
                    cancellationToken);
            }

            return null;
        }

        private string? ResolveChromiumUserDataRoot()
        {
            if (OperatingSystem.IsWindows())
            {
                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return WindowsUserDataPath is null ? null : System.IO.Path.Combine(localAppData, WindowsUserDataPath);
            }

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (OperatingSystem.IsMacOS())
            {
                return MacProfileRoot is null
                    ? null
                    : System.IO.Path.Combine(home, "Library", "Application Support", MacProfileRoot);
            }

            return LinuxProfileRoot is null
                ? null
                : System.IO.Path.Combine(home, ".config", LinuxProfileRoot);
        }

        public string? ResolveFirefoxProfileDirectory()
        {
            var databasePath = ResolveFirefoxCookieDatabasePath();
            return databasePath is null ? null : System.IO.Path.GetDirectoryName(databasePath);
        }

        private string? ResolveFirefoxCookieDatabasePath()
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string root;
            if (OperatingSystem.IsWindows())
            {
                root = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Mozilla",
                    "Firefox",
                    "Profiles");
            }
            else if (OperatingSystem.IsMacOS())
            {
                root = System.IO.Path.Combine(home, "Library", "Application Support", "Firefox", "Profiles");
            }
            else
            {
                root = System.IO.Path.Combine(home, ".mozilla", "firefox");
            }

            if (!Directory.Exists(root))
            {
                return null;
            }

            return Directory.EnumerateFiles(root, "cookies.sqlite", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }

        private static IEnumerable<string> EnumerateChromiumProfiles(string profileRoot)
        {
            if (!Directory.Exists(profileRoot))
            {
                yield break;
            }

            var preferredProfiles = new[] { "Default", "Profile 1", "Profile 2", "Profile 3" };
            foreach (var profile in preferredProfiles.Select(profile => System.IO.Path.Combine(profileRoot, profile)))
            {
                if (Directory.Exists(profile))
                {
                    yield return profile;
                }
            }

            foreach (var profile in Directory.EnumerateDirectories(profileRoot, "Profile *")
                         .Where(profile => !preferredProfiles.Contains(System.IO.Path.GetFileName(profile), StringComparer.OrdinalIgnoreCase)))
            {
                yield return profile;
            }
        }

        private static async Task<string?> RunProcessForSingleLineAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                };

                foreach (var argument in arguments)
                {
                    startInfo.ArgumentList.Add(argument);
                }

                using var process = Process.Start(startInfo);
                if (process is null)
                {
                    return null;
                }

                var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
                await process.WaitForExitAsync(cancellationToken);
                return process.ExitCode == 0 ? output.Trim() : null;
            }
            catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
            {
                return null;
            }
        }
    }

    private static class FirefoxNssRuntime
    {
        public static IDisposable? TryInitialize(string? profileDirectory)
        {
            foreach (var libraryName in ResolveCandidateNames())
            {
                if (NativeLibrary.TryLoad(libraryName, out var handle))
                {
                    try
                    {
                        if (NativeLibrary.TryGetExport(handle, "NSS_Init", out var initPtr)
                            && NativeLibrary.TryGetExport(handle, "NSS_Shutdown", out var shutdownPtr)
                            && !string.IsNullOrWhiteSpace(profileDirectory))
                        {
                            var init = Marshal.GetDelegateForFunctionPointer<NssInit>(initPtr);
                            var result = init(profileDirectory);
                            if (result == 0)
                            {
                                return new NssSession(handle, Marshal.GetDelegateForFunctionPointer<NssShutdown>(shutdownPtr));
                            }
                        }

                        if (NativeLibrary.TryGetExport(handle, "NSS_NoDB_Init", out var noDbInitPtr)
                            && NativeLibrary.TryGetExport(handle, "NSS_Shutdown", out shutdownPtr))
                        {
                            var noDbInit = Marshal.GetDelegateForFunctionPointer<NssInit>(noDbInitPtr);
                            var result = noDbInit(null);
                            if (result == 0)
                            {
                                return new NssSession(handle, Marshal.GetDelegateForFunctionPointer<NssShutdown>(shutdownPtr));
                            }
                        }
                    }
                    catch (Exception ex) when (ex is EntryPointNotFoundException or MarshalDirectiveException or SEHException)
                    {
                    }

                    NativeLibrary.Free(handle);
                }
            }

            return null;
        }

        private static IEnumerable<string> ResolveCandidateNames()
        {
            if (OperatingSystem.IsWindows())
            {
                yield return "nss3.dll";
            }
            else if (OperatingSystem.IsMacOS())
            {
                yield return "libnss3.dylib";
            }
            else
            {
                yield return "libnss3.so";
            }
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private delegate int NssInit(string? configDir);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NssShutdown();

        private sealed class NssSession : IDisposable
        {
            private readonly NssShutdown shutdown;
            private IntPtr handle;

            public NssSession(IntPtr handle, NssShutdown shutdown)
            {
                this.handle = handle;
                this.shutdown = shutdown;
            }

            public void Dispose()
            {
                if (handle == IntPtr.Zero)
                {
                    return;
                }

                shutdown();
                NativeLibrary.Free(handle);
                handle = IntPtr.Zero;
            }
        }
    }

    private static class WindowsDpapi
    {
        public static byte[] Unprotect(byte[] encrypted)
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("DPAPI is only available on Windows.");
            }

            var input = new DataBlob(encrypted);
            var output = default(DATA_BLOB);
            try
            {
                if (!CryptUnprotectData(ref input.Blob, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref output))
                {
                    throw new CryptographicException(Marshal.GetLastWin32Error());
                }

                var plain = new byte[output.cbData];
                if (output.cbData > 0)
                {
                    Marshal.Copy(output.pbData, plain, 0, output.cbData);
                }

                return plain;
            }
            finally
            {
                input.Dispose();
                if (output.pbData != IntPtr.Zero)
                {
                    LocalFree(output.pbData);
                }
            }
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptUnprotectData(
            ref DATA_BLOB pDataIn,
            string? ppszDataDescr,
            IntPtr pOptionalEntropy,
            IntPtr pvReserved,
            IntPtr pPromptStruct,
            int dwFlags,
            ref DATA_BLOB pDataOut);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr hMem);

        [StructLayout(LayoutKind.Sequential)]
        private struct DATA_BLOB
        {
            public int cbData;
            public IntPtr pbData;
        }

        private sealed class DataBlob : IDisposable
        {
            public DataBlob(byte[] data)
            {
                Blob.cbData = data.Length;
                Blob.pbData = Marshal.AllocHGlobal(data.Length);
                Marshal.Copy(data, 0, Blob.pbData, data.Length);
            }

            public DATA_BLOB Blob;

            public void Dispose()
            {
                if (Blob.pbData == IntPtr.Zero)
                {
                    return;
                }

                Span<byte> zero = stackalloc byte[Math.Min(Blob.cbData, 1024)];
                var remaining = Blob.cbData;
                var offset = 0;
                while (remaining > 0)
                {
                    var count = Math.Min(remaining, zero.Length);
                    Marshal.Copy(zero[..count].ToArray(), 0, Blob.pbData + offset, count);
                    remaining -= count;
                    offset += count;
                }

                Marshal.FreeHGlobal(Blob.pbData);
                Blob.pbData = IntPtr.Zero;
                Blob.cbData = 0;
            }
        }
    }
}
