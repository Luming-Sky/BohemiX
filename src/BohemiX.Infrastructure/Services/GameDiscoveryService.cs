using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.Versioning;
using BohemiX.Core.Models;
using BohemiX.Core.Services;
using Microsoft.Win32;
using Serilog;

namespace BohemiX.Infrastructure.Services;

public sealed partial class GameDiscoveryService : IGameDiscoveryService, ILocalGameInstallationDetector
{
    private const string Kcd2SteamAppId = "1771300";
    private const string GameDisplayName = "Kingdom Come: Deliverance II";

    private static readonly string[] CandidateExecutablePaths =
    [
        Path.Combine("Bin", "Win64MasterMasterSteamPGO", "KingdomCome.exe"),
        Path.Combine("Bin", "Win64MasterAssertSteamPGO", "KingdomCome.exe"),
        Path.Combine("Bin", "Win64MasterMasterEpicPGO", "KingdomCome.exe"),
        Path.Combine("Bin", "Win64MasterAssertEpicPGO", "KingdomCome.exe"),
        Path.Combine("Bin", "Win64MasterMasterGogPGO", "KingdomCome.exe"),
        Path.Combine("Bin", "Win64Shared", "KingdomCome.exe"),
        Path.Combine("Content", "KingdomCome.exe"),
        Path.Combine("Content", "Bin", "Win64", "KingdomCome.exe"),
        "KingdomCome.exe"
    ];

    private static readonly string[] CandidateInstallDirectoryNames =
    [
        "KingdomComeDeliverance2",
        "KingdomComeDeliveranceII",
        "KingdomCome2",
        "KingdomComeII",
        "Kingdom Come Deliverance II",
        "Kingdom Come Deliverance 2",
        "Kingdom Come II",
        "Kingdom Come 2",
        "Kingdom Come- Deliverance II",
        "Kingdom Come- Deliverance 2",
        "KCD2"
    ];

    private readonly ILogger logger;
    private readonly IGameInstallationStore gameInstallationStore;
    private readonly IGameDiscoveryPathSource pathSource;

    public GameDiscoveryService(
        ILogger logger,
        IGameInstallationStore gameInstallationStore,
        IGameDiscoveryPathSource? pathSource = null)
    {
        this.logger = logger.ForContext<GameDiscoveryService>();
        this.gameInstallationStore = gameInstallationStore;
        this.pathSource = pathSource ?? new WindowsGameDiscoveryPathSource(this.logger);
    }

    public async Task<IReadOnlyList<DiscoveredGame>> DiscoverInstalledGamesAsync(
        GameDiscoverySearchMode searchMode = GameDiscoverySearchMode.Fast,
        CancellationToken cancellationToken = default)
    {
        var uniqueGames = await DetectLocalInstallationsAsync(searchMode, cancellationToken);
        var cachedManualGames = (await gameInstallationStore.LoadAsync(cancellationToken))
            .Where(game => game.Source == GameInstallSource.Manual);
        var mergedGames = uniqueGames
            .Concat(cachedManualGames)
            .GroupBy(game => game.InstallPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(game => game.IsVerified).First())
            .OrderBy(game => game.Source)
            .ThenBy(game => game.InstallPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        await gameInstallationStore.SaveAsync(mergedGames, cancellationToken);

        logger.Information(
            "{SearchMode} game discovery completed with {GameCount} candidate installations",
            searchMode,
            mergedGames.Length);
        return mergedGames;
    }

    public async Task<IReadOnlyList<DiscoveredGame>> DetectLocalInstallationsAsync(
        CancellationToken cancellationToken = default)
    {
        return await DetectLocalInstallationsAsync(GameDiscoverySearchMode.Fast, cancellationToken);
    }

    private async Task<IReadOnlyList<DiscoveredGame>> DetectLocalInstallationsAsync(
        GameDiscoverySearchMode searchMode,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var games = await Task.Run(
            () =>
            {
                var discoveredGames = new List<DiscoveredGame>();
                discoveredGames.AddRange(DiscoverSteamInstallations(cancellationToken));
                discoveredGames.AddRange(DiscoverEpicInstallations(cancellationToken));
                discoveredGames.AddRange(DiscoverKnownDirectoryInstallations(cancellationToken));
                if (searchMode == GameDiscoverySearchMode.Deep)
                {
                    discoveredGames.AddRange(DiscoverDeepInstallations(cancellationToken));
                }
                return discoveredGames;
            },
            cancellationToken);

        return games
            .GroupBy(game => game.InstallPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(game => game.IsVerified).First())
            .OrderBy(game => game.Source)
            .ThenBy(game => game.InstallPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<DiscoveredGame?> VerifyManualPathAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedPath = path.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return null;
        }

        string? installPath = null;
        string? executablePath = null;

        if (File.Exists(normalizedPath))
        {
            executablePath = normalizedPath;
            installPath = ResolveInstallPathFromExecutable(normalizedPath);
        }
        else if (Directory.Exists(normalizedPath))
        {
            installPath = normalizedPath;
            executablePath = ResolveExecutablePath(normalizedPath);
        }

        if (installPath is null || executablePath is null || !IsKnownGameExecutable(executablePath))
        {
            logger.Warning("Manual KCD2 path verification failed for {Path}", normalizedPath);
            return null;
        }

        var game = new DiscoveredGame(
            GameDisplayName,
            installPath,
            executablePath,
            GameInstallSource.Manual,
            true);

        var cachedGames = await gameInstallationStore.LoadAsync(cancellationToken);
        var mergedGames = cachedGames
            .Where(existing => !string.Equals(existing.InstallPath, game.InstallPath, StringComparison.OrdinalIgnoreCase))
            .Append(game)
            .OrderBy(existing => existing.Source)
            .ThenBy(existing => existing.InstallPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        await gameInstallationStore.SaveAsync(mergedGames, cancellationToken);
        logger.Information("Manual KCD2 installation verified at {ExecutablePath}", executablePath);
        return game;
    }

    private IEnumerable<DiscoveredGame> DiscoverSteamInstallations(CancellationToken cancellationToken)
    {
        var seenManifests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var steamRoot in GetSteamRoots())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var steamAppsPath = Path.Combine(steamRoot, "steamapps");
            foreach (var libraryPath in GetSteamLibraryPaths(steamAppsPath))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var manifestPath = Path.Combine(libraryPath, "steamapps", $"appmanifest_{Kcd2SteamAppId}.acf");
                if (!File.Exists(manifestPath) || !seenManifests.Add(manifestPath))
                {
                    continue;
                }

                var game = TryCreateSteamGameFromManifest(manifestPath);
                if (game is not null)
                {
                    yield return game;
                }
            }
        }

    }

    private IEnumerable<DiscoveredGame> DiscoverEpicInstallations(CancellationToken cancellationToken)
    {
        var manifestRoot = pathSource.EpicManifestRoot;

        if (!Directory.Exists(manifestRoot))
        {
            yield break;
        }

        string[] manifests;
        try
        {
            manifests = Directory.GetFiles(manifestRoot, "*.item", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.Warning(ex, "Unable to enumerate Epic manifests from {ManifestRoot}", manifestRoot);
            yield break;
        }

        foreach (var manifestPath in manifests)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var game = TryReadEpicManifest(manifestPath);
            if (game is not null)
            {
                yield return game;
            }
        }
    }

    private IEnumerable<DiscoveredGame> DiscoverKnownDirectoryInstallations(CancellationToken cancellationToken)
    {
        var candidates = new Dictionary<string, GameInstallSource>(StringComparer.OrdinalIgnoreCase);

        foreach (var steamCommonPath in GetSteamCommonPaths())
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddKnownGameDirectories(candidates, steamCommonPath, GameInstallSource.Steam);
        }

        foreach (var epicInstallRoot in GetEpicInstallRoots())
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddKnownGameDirectories(candidates, epicInstallRoot, GameInstallSource.Epic);
        }

        foreach (var gameInstallRoot in GetCommonGameInstallRoots())
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddKnownGameDirectories(candidates, gameInstallRoot, GameInstallSource.Unknown);
        }

        foreach (var gameRoot in GetDirectGameRootCandidates())
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddCandidateGameDirectory(candidates, gameRoot, GameInstallSource.Unknown);
        }

        foreach (var (installPath, source) in candidates.OrderBy(candidate => candidate.Value).ThenBy(candidate => candidate.Key, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return CreateDiscoveredGame(GameDisplayName, installPath, source);
        }
    }

    private IEnumerable<DiscoveredGame> DiscoverDeepInstallations(CancellationToken cancellationToken)
    {
        var games = new Dictionary<string, DiscoveredGame>(StringComparer.OrdinalIgnoreCase);
        var searchedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var searchRoot in GetWindowsDriveRoots())
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var directory in EnumerateDirectories(searchRoot, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!searchedDirectories.Add(directory))
                {
                    continue;
                }

                if (string.Equals(Path.GetFileName(directory), "steamapps", StringComparison.OrdinalIgnoreCase))
                {
                    var manifestPath = Path.Combine(directory, $"appmanifest_{Kcd2SteamAppId}.acf");
                    var steamGame = File.Exists(manifestPath) ? TryCreateSteamGameFromManifest(manifestPath) : null;
                    if (steamGame is not null)
                    {
                        games[steamGame.InstallPath] = steamGame;
                    }
                }

                var executablePath = Path.Combine(directory, "KingdomCome.exe");
                if (!File.Exists(executablePath))
                {
                    continue;
                }

                var installPath = ResolveInstallPathFromExecutable(executablePath);
                if (IsLikelyKcd2Executable(executablePath, installPath))
                {
                    games[installPath] = new DiscoveredGame(
                        GameDisplayName,
                        installPath,
                        executablePath,
                        GameInstallSource.Unknown,
                        true);
                }
            }
        }

        foreach (var game in games.Values.OrderBy(game => game.Source).ThenBy(game => game.InstallPath, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return game;
        }
    }

    private IEnumerable<string> EnumerateDirectories(string searchRoot, CancellationToken cancellationToken)
    {
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((searchRoot, 0));

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = FileAttributes.System | FileAttributes.Temporary | FileAttributes.ReparsePoint
        };

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (directory, depth) = pending.Pop();
            yield return directory;

            if (depth >= 10)
            {
                continue;
            }

            string[] children;
            try
            {
                children = Directory.GetDirectories(directory, "*", options);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.Debug(ex, "Unable to enumerate game discovery directory {Directory}", directory);
                continue;
            }

            foreach (var child in children)
            {
                pending.Push((child, depth + 1));
            }
        }
    }

    private void AddKnownGameDirectories(IDictionary<string, GameInstallSource> candidates, string installContainer, GameInstallSource source)
    {
        if (!Directory.Exists(installContainer))
        {
            return;
        }

        foreach (var directoryName in CandidateInstallDirectoryNames)
        {
            AddCandidateGameDirectory(candidates, Path.Combine(installContainer, directoryName), source);
        }

        string[] directories;
        try
        {
            directories = Directory.GetDirectories(installContainer, "*", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.Warning(ex, "Unable to enumerate install container {InstallContainer}", installContainer);
            return;
        }

        foreach (var directory in directories)
        {
            if (IsKcd2InstallDirectoryName(Path.GetFileName(directory)) || ResolveExecutablePath(directory, includeRecursiveSearch: false) is not null)
            {
                AddCandidateGameDirectory(candidates, directory, source);
            }
        }
    }

    private void AddCandidateGameDirectory(IDictionary<string, GameInstallSource> candidates, string installPath, GameInstallSource source)
    {
        if (!Directory.Exists(installPath))
        {
            return;
        }

        var normalizedPath = NormalizeDirectoryPath(installPath);
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return;
        }

        if (!candidates.TryGetValue(normalizedPath, out var existingSource) || existingSource == GameInstallSource.Unknown)
        {
            candidates[normalizedPath] = source;
        }
    }

    private IEnumerable<string> GetSteamRoots() => pathSource.GetSteamRoots();

    private IEnumerable<string> GetSteamCommonPaths()
    {
        var commonPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var steamRoot in GetSteamRoots())
        {
            var steamAppsPath = Path.Combine(steamRoot, "steamapps");
            foreach (var libraryPath in GetSteamLibraryPaths(steamAppsPath))
            {
                AddExistingDirectory(commonPaths, Path.Combine(libraryPath, "steamapps", "common"));
            }

            AddExistingDirectory(commonPaths, Path.Combine(steamRoot, "steamapps", "common"));
        }

        return commonPaths.ToArray();
    }

    private IEnumerable<string> GetEpicInstallRoots() => pathSource.GetEpicInstallRoots();

    private IEnumerable<string> GetCommonGameInstallRoots() => pathSource.GetCommonGameInstallRoots();

    private IEnumerable<string> GetDirectGameRootCandidates()
    {
        foreach (var root in GetWindowsDriveRoots())
        {
            foreach (var directoryName in CandidateInstallDirectoryNames)
            {
                yield return Path.Combine(root, directoryName);
                yield return Path.Combine(root, "Games", directoryName);
                yield return Path.Combine(root, "Program Files", directoryName);
                yield return Path.Combine(root, "Program Files (x86)", directoryName);
                yield return Path.Combine(root, "GOG Games", directoryName);
                yield return Path.Combine(root, "GOG Galaxy", "Games", directoryName);
                yield return Path.Combine(root, "XboxGames", directoryName);
                yield return Path.Combine(root, "Xbox Games", directoryName);
            }
        }
    }

    private IEnumerable<string> GetWindowsDriveRoots() => pathSource.GetDriveRoots();

    private IEnumerable<string> GetSteamLibraryPaths(string steamAppsPath)
    {
        var libraryRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var steamRoot = Directory.GetParent(steamAppsPath)?.FullName;

        if (!string.IsNullOrWhiteSpace(steamRoot) && Directory.Exists(steamRoot))
        {
            libraryRoots.Add(steamRoot);
        }

        var libraryFoldersPath = Path.Combine(steamAppsPath, "libraryfolders.vdf");
        if (!File.Exists(libraryFoldersPath))
        {
            return libraryRoots;
        }

        try
        {
            foreach (var line in File.ReadLines(libraryFoldersPath))
            {
                var match = SteamPathLineRegex().Match(line);
                if (match.Success)
                {
                    libraryRoots.Add(match.Groups["path"].Value.Replace(@"\\", @"\"));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.Warning(ex, "Unable to read Steam library folders from {LibraryFoldersPath}", libraryFoldersPath);
        }

        return libraryRoots.Where(Directory.Exists).ToArray();
    }

    private string? TryReadSteamInstallDir(string manifestPath)
    {
        try
        {
            foreach (var line in File.ReadLines(manifestPath))
            {
                var match = SteamInstallDirRegex().Match(line);
                if (match.Success)
                {
                    return match.Groups["dir"].Value;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.Warning(ex, "Unable to read Steam app manifest {ManifestPath}", manifestPath);
        }

        return null;
    }

    private DiscoveredGame? TryCreateSteamGameFromManifest(string manifestPath)
    {
        var installDir = TryReadSteamInstallDir(manifestPath);
        if (string.IsNullOrWhiteSpace(installDir))
        {
            logger.Warning("Steam manifest did not contain installdir: {ManifestPath}", manifestPath);
            return null;
        }

        var steamAppsDirectory = Path.GetDirectoryName(manifestPath);
        if (string.IsNullOrWhiteSpace(steamAppsDirectory))
        {
            logger.Warning("Steam manifest was not under a steamapps directory: {ManifestPath}", manifestPath);
            return null;
        }

        var libraryPath = Directory.GetParent(steamAppsDirectory)?.FullName;
        if (string.IsNullOrWhiteSpace(libraryPath))
        {
            logger.Warning("Steam manifest was not under a steamapps directory: {ManifestPath}", manifestPath);
            return null;
        }

        var installPath = Path.Combine(libraryPath, "steamapps", "common", installDir);
        return CreateDiscoveredGame(GameDisplayName, installPath, GameInstallSource.Steam);
    }


    private DiscoveredGame? TryReadEpicManifest(string manifestPath)
    {
        try
        {
            using var stream = File.OpenRead(manifestPath);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;

            var displayName = TryGetString(root, "DisplayName");
            if (!IsKcd2DisplayName(displayName))
            {
                return null;
            }

            var installLocation = TryGetString(root, "InstallLocation");
            if (string.IsNullOrWhiteSpace(installLocation))
            {
                logger.Warning("Epic manifest did not contain InstallLocation: {ManifestPath}", manifestPath);
                return null;
            }

            return CreateDiscoveredGame(displayName ?? GameDisplayName, installLocation, GameInstallSource.Epic);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.Warning(ex, "Unable to read Epic manifest {ManifestPath}", manifestPath);
            return null;
        }
    }

    private DiscoveredGame CreateDiscoveredGame(string name, string installPath, GameInstallSource source)
    {
        var executablePath = ResolveExecutablePath(installPath, includeRecursiveSearch: false);
        return new DiscoveredGame(
            name,
            installPath,
            executablePath ?? string.Empty,
            source,
            executablePath is not null);
    }

    private string? ResolveExecutablePath(string installPath)
    {
        return ResolveExecutablePath(installPath, includeRecursiveSearch: true);
    }

    private string? ResolveExecutablePath(string installPath, bool includeRecursiveSearch)
    {
        foreach (var candidate in CandidateExecutablePaths)
        {
            var executablePath = Path.Combine(installPath, candidate);
            if (File.Exists(executablePath))
            {
                return executablePath;
            }
        }

        if (!includeRecursiveSearch)
        {
            return null;
        }

        try
        {
            return Directory.Exists(installPath)
                ? Directory.EnumerateFiles(installPath, "KingdomCome.exe", SearchOption.AllDirectories).FirstOrDefault()
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.Warning(ex, "Unable to search for executable under {InstallPath}", installPath);
            return null;
        }
    }

    private static string ResolveInstallPathFromExecutable(string executablePath)
    {
        var directory = Path.GetDirectoryName(executablePath);
        if (directory is null)
        {
            return executablePath;
        }

        var current = new DirectoryInfo(directory);
        while (current.Parent is not null)
        {
            if (string.Equals(current.Name, "Bin", StringComparison.OrdinalIgnoreCase))
            {
                return current.Parent.FullName;
            }

            current = current.Parent;
        }

        return directory;
    }

    private static bool IsKnownGameExecutable(string executablePath)
    {
        return string.Equals(Path.GetFileName(executablePath), "KingdomCome.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLikelyKcd2Executable(string executablePath, string installPath)
    {
        if (!IsKnownGameExecutable(executablePath))
        {
            return false;
        }

        if (IsKcd2InstallDirectoryName(Path.GetFileName(installPath)))
        {
            return true;
        }

        var normalizedExecutablePath = NormalizeDirectoryPath(executablePath);
        return CandidateExecutablePaths
            .Select(candidate => NormalizeDirectoryPath(Path.Combine(installPath, candidate)))
            .Any(candidatePath => string.Equals(candidatePath, normalizedExecutablePath, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsKcd2InstallDirectoryName(string directoryName)
    {
        var compactName = new string(directoryName.Where(char.IsLetterOrDigit).ToArray());
        return compactName.Equals("KCD2", StringComparison.OrdinalIgnoreCase)
            || compactName.Equals("KingdomCome2", StringComparison.OrdinalIgnoreCase)
            || compactName.Equals("KingdomComeII", StringComparison.OrdinalIgnoreCase)
            || (compactName.Contains("KingdomCome", StringComparison.OrdinalIgnoreCase)
                && compactName.Contains("Deliverance", StringComparison.OrdinalIgnoreCase)
                && (compactName.Contains("2", StringComparison.OrdinalIgnoreCase)
                    || compactName.Contains("II", StringComparison.OrdinalIgnoreCase)));
    }

    private static void AddExistingDirectory(ISet<string> directories, string path)
    {
        var normalizedPath = NormalizeDirectoryPath(path);
        if (!string.IsNullOrWhiteSpace(normalizedPath) && Directory.Exists(normalizedPath))
        {
            directories.Add(normalizedPath);
        }
    }

    private static string NormalizeDirectoryPath(string path)
    {
        try
        {
            return Path.GetFullPath(path.Trim().Trim('"')).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Empty;
        }
    }

    private static string? TryGetString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool IsKcd2DisplayName(string? displayName)
    {
        return displayName is not null
            && displayName.Contains("Kingdom Come", StringComparison.OrdinalIgnoreCase)
            && displayName.Contains("Deliverance", StringComparison.OrdinalIgnoreCase)
            && (displayName.Contains("II", StringComparison.OrdinalIgnoreCase) || displayName.Contains("2", StringComparison.OrdinalIgnoreCase));
    }

    [GeneratedRegex("""^\s*"path"\s+"(?<path>.+)"\s*$""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SteamPathLineRegex();

    [GeneratedRegex("""^\s*"installdir"\s+"(?<dir>.+)"\s*$""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SteamInstallDirRegex();
}

internal sealed class WindowsGameDiscoveryPathSource : IGameDiscoveryPathSource
{
    private readonly ILogger logger;

    public WindowsGameDiscoveryPathSource(ILogger logger)
    {
        this.logger = logger;
    }

    public string EpicManifestRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Epic",
        "EpicGamesLauncher",
        "Data",
        "Manifests");

    public IEnumerable<string> GetSteamRoots()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        TryAddRegistryValue(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath", roots);
        TryAddRegistryValue(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", roots);
        TryAddRegistryValue(Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath", roots);

        foreach (var root in GetDriveRoots())
        {
            AddExistingDirectory(roots, Path.Combine(root, "Steam"));
            AddExistingDirectory(roots, Path.Combine(root, "SteamLibrary"));
            AddExistingDirectory(roots, Path.Combine(root, "Games", "Steam"));
            AddExistingDirectory(roots, Path.Combine(root, "Games", "SteamLibrary"));
        }

        AddExistingDirectory(roots, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
        AddExistingDirectory(roots, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"));
        return roots.Where(Directory.Exists).ToArray();
    }

    public IEnumerable<string> GetEpicInstallRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddExistingDirectory(roots, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Epic Games"));
        AddExistingDirectory(roots, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Epic Games"));

        foreach (var root in GetDriveRoots())
        {
            AddExistingDirectory(roots, Path.Combine(root, "Epic Games"));
            AddExistingDirectory(roots, Path.Combine(root, "Games", "Epic Games"));
        }

        return roots.ToArray();
    }

    public IEnumerable<string> GetCommonGameInstallRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in GetDriveRoots())
        {
            AddExistingDirectory(roots, Path.Combine(root, "Games"));
            AddExistingDirectory(roots, Path.Combine(root, "Games", "SteamLibrary", "steamapps", "common"));
            AddExistingDirectory(roots, Path.Combine(root, "SteamLibrary", "steamapps", "common"));
            AddExistingDirectory(roots, Path.Combine(root, "Steam", "steamapps", "common"));
            AddExistingDirectory(roots, Path.Combine(root, "Epic Games"));
            AddExistingDirectory(roots, Path.Combine(root, "GOG Games"));
            AddExistingDirectory(roots, Path.Combine(root, "GOG Galaxy", "Games"));
            AddExistingDirectory(roots, Path.Combine(root, "XboxGames"));
            AddExistingDirectory(roots, Path.Combine(root, "Xbox Games"));
        }

        return roots.ToArray();
    }

    public IEnumerable<string> GetDriveRoots()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        try
        {
            return DriveInfo.GetDrives()
                .Where(drive => drive.IsReady && drive.DriveType is DriveType.Fixed or DriveType.Removable)
                .Select(drive => drive.RootDirectory.FullName)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.Warning(ex, "Unable to enumerate local drives for game discovery");
            return [];
        }
    }

    [SupportedOSPlatform("windows")]
    private void TryAddRegistryValue(RegistryKey root, string subKeyPath, string valueName, ISet<string> values)
    {
        try
        {
            using var key = root.OpenSubKey(subKeyPath);
            if (key?.GetValue(valueName) is string value && !string.IsNullOrWhiteSpace(value))
            {
                values.Add(value);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            logger.Warning(ex, "Unable to read registry value {SubKeyPath}/{ValueName}", subKeyPath, valueName);
        }
    }

    private static void AddExistingDirectory(ISet<string> directories, string path)
    {
        if (Directory.Exists(path))
        {
            directories.Add(path);
        }
    }
}
