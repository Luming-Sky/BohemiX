using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;

namespace BohemiX.Infrastructure.Services;

internal sealed record SteamLoginAccount(ulong SteamId, string PersonaName);

internal sealed record SteamClientLaunchResult(bool Started, string? ErrorMessage)
{
    public static SteamClientLaunchResult Success { get; } = new(true, null);

    public static SteamClientLaunchResult Failure(string message) => new(false, message);
}

internal static class SteamLoginAccountReader
{
    public static bool IsSteamClientRunning()
    {
        try
        {
            var processes = Process.GetProcessesByName("steam");
            foreach (var process in processes)
            {
                process.Dispose();
            }

            return processes.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryReadActiveAccount(out SteamLoginAccount? account)
    {
        account = null;
        if (!OperatingSystem.IsWindows()
            || !TryReadActiveUserId(out var activeUserId)
            || activeUserId == 0)
        {
            return false;
        }

        var loginUsersPath = FindLoginUsersPath();
        if (loginUsersPath is null)
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(
                loginUsersPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            account = ParseActiveAccount(reader.ReadToEnd(), activeUserId);
            return account is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static SteamClientLaunchResult TryStartSteamClient()
    {
        if (!OperatingSystem.IsWindows())
        {
            return SteamClientLaunchResult.Failure("Automatic Steam startup is only supported on Windows.");
        }

        var executablePath = FindSteamExecutablePath();
        if (executablePath is null)
        {
            return SteamClientLaunchResult.Failure("Steam is installed, but steam.exe could not be located.");
        }

        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = true
            });
            process?.Dispose();
            return SteamClientLaunchResult.Success;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return SteamClientLaunchResult.Failure($"Steam could not be started: {ex.Message}");
        }
    }

    internal static SteamLoginAccount? ParseActiveAccount(string content, uint activeUserId)
    {
        var tokens = Tokenize(content);
        var index = 0;
        var root = ParseObject(tokens, ref index, stopAtClosingBrace: false);
        if (!root.TryGetValue("users", out var usersValue)
            || usersValue is not Dictionary<string, object> users)
        {
            return null;
        }

        foreach (var (steamIdText, value) in users)
        {
            if (value is not Dictionary<string, object> properties
                || !ulong.TryParse(steamIdText, NumberStyles.None, CultureInfo.InvariantCulture, out var steamId)
                || unchecked((uint)steamId) != activeUserId)
            {
                continue;
            }

            var personaName = GetString(properties, "PersonaName");
            if (string.IsNullOrWhiteSpace(personaName))
            {
                personaName = GetString(properties, "AccountName");
            }

            return new SteamLoginAccount(
                steamId,
                string.IsNullOrWhiteSpace(personaName) ? steamIdText : personaName.Trim());
        }

        return null;
    }

    [SupportedOSPlatform("windows")]
    private static bool TryReadActiveUserId(out uint activeUserId)
    {
        activeUserId = 0;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess");
            var value = key?.GetValue("ActiveUser");
            return value switch
            {
                int number when number > 0 => Assign(unchecked((uint)number), out activeUserId),
                long number when number > 0 && number <= uint.MaxValue => Assign((uint)number, out activeUserId),
                string text when uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number > 0 =>
                    Assign(number, out activeUserId),
                _ => false
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? FindLoginUsersPath()
    {
        var steamRoots = new List<string?>();
        TryAddRegistryValue(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath", steamRoots);
        TryAddRegistryValue(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", steamRoots);
        TryAddRegistryValue(Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath", steamRoots);

        return steamRoots
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.Combine(path!, "config", "loginusers.vdf"))
            .FirstOrDefault(File.Exists);
    }

    [SupportedOSPlatform("windows")]
    internal static string? FindSteamExecutablePath()
    {
        var candidates = new List<string?>();
        TryAddRegistryValue(Registry.CurrentUser, @"Software\Valve\Steam", "SteamExe", candidates);

        var steamRoots = new List<string?>();
        TryAddRegistryValue(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath", steamRoots);
        TryAddRegistryValue(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", steamRoots);
        TryAddRegistryValue(Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath", steamRoots);
        candidates.AddRange(steamRoots
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.Combine(path!, "steam.exe")));

        return SelectSteamExecutablePath(candidates, File.Exists);
    }

    internal static string? SelectSteamExecutablePath(
        IEnumerable<string?> candidates,
        Func<string, bool> fileExists)
    {
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            try
            {
                var fullPath = Path.GetFullPath(candidate.Trim().Trim('"'));
                if (fileExists(fullPath))
                {
                    return fullPath;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
            }
        }

        return null;
    }

    [SupportedOSPlatform("windows")]
    private static void TryAddRegistryValue(
        RegistryKey root,
        string subKeyPath,
        string valueName,
        ICollection<string?> values)
    {
        try
        {
            using var key = root.OpenSubKey(subKeyPath);
            values.Add(key?.GetValue(valueName)?.ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static Dictionary<string, object> ParseObject(
        IReadOnlyList<string> tokens,
        ref int index,
        bool stopAtClosingBrace)
    {
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        while (index < tokens.Count)
        {
            if (tokens[index] == "}")
            {
                index++;
                if (stopAtClosingBrace)
                {
                    break;
                }

                continue;
            }

            var key = tokens[index++];
            if (index >= tokens.Count)
            {
                break;
            }

            if (tokens[index] == "{")
            {
                index++;
                result[key] = ParseObject(tokens, ref index, stopAtClosingBrace: true);
            }
            else
            {
                result[key] = tokens[index++];
            }
        }

        return result;
    }

    private static IReadOnlyList<string> Tokenize(string content)
    {
        var tokens = new List<string>();
        var index = 0;
        while (index < content.Length)
        {
            if (char.IsWhiteSpace(content[index]))
            {
                index++;
                continue;
            }

            if (content[index] == '/' && index + 1 < content.Length && content[index + 1] == '/')
            {
                index += 2;
                while (index < content.Length && content[index] is not '\r' and not '\n')
                {
                    index++;
                }

                continue;
            }

            if (content[index] is '{' or '}')
            {
                tokens.Add(content[index++].ToString());
                continue;
            }

            if (content[index] == '"')
            {
                index++;
                var value = new StringBuilder();
                while (index < content.Length)
                {
                    var ch = content[index++];
                    if (ch == '"')
                    {
                        break;
                    }

                    if (ch == '\\' && index < content.Length)
                    {
                        var escaped = content[index++];
                        value.Append(escaped switch
                        {
                            'n' => '\n',
                            'r' => '\r',
                            't' => '\t',
                            _ => escaped
                        });
                        continue;
                    }

                    value.Append(ch);
                }

                tokens.Add(value.ToString());
                continue;
            }

            var start = index;
            while (index < content.Length
                   && !char.IsWhiteSpace(content[index])
                   && content[index] is not '{' and not '}')
            {
                index++;
            }

            tokens.Add(content[start..index]);
        }

        return tokens;
    }

    private static string? GetString(IReadOnlyDictionary<string, object> values, string key) =>
        values.TryGetValue(key, out var value) ? value as string : null;

    private static bool Assign(uint value, out uint target)
    {
        target = value;
        return true;
    }
}
