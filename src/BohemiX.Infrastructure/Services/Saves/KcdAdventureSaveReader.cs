using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using BohemiX.Core.Models.Saves;
using BohemiX.Core.Services.Saves;

namespace BohemiX.Infrastructure.Services.Saves;

public sealed class KcdAdventureSaveReader : IKcdAdventureSaveReader
{
    private const int MaximumDescriptionBytes = 4 * 1024 * 1024;
    private const int MaximumInflatedBytes = 512 * 1024 * 1024;
    private static readonly Regex RootAttributeRegex = new(
        "(?<name>[A-Za-z_][A-Za-z0-9_]*)=\"(?<value>[^\"]*)\"",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex QuestTagRegex = new(
        "<Quest\\b(?<attributes>[^>]*)>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex StateTagRegex = new(
        "<State\\b(?<attributes>[^>]*)>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex AttributeRegex = new(
        "(?:^|\\s)(?<name>[A-Za-z_][A-Za-z0-9_]*)=\"(?<value>[^\"]*)\"",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex MainProductionCodeRegex = new(
        "^M\\d+[A-Za-z]?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private const long MillisecondsPerGameDay = 24L * 60 * 60 * 1000;

    private readonly ConcurrentDictionary<SaveCacheKey, KcdAdventureSaveData> saveCache = new();
    private readonly object saveReadGate = new();
    private readonly object catalogGate = new();
    private GameCatalogCache? catalogCache;

    public Task<KcdAdventureSaveData> ReadAsync(
        string saveDirectory,
        string? gameInstallDirectory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(saveDirectory) || !Directory.Exists(saveDirectory))
        {
            return Task.FromResult(KcdAdventureSaveData.Empty);
        }

        return Task.Run(
            () => ReadCore(saveDirectory, gameInstallDirectory, cancellationToken),
            cancellationToken);
    }

    private KcdAdventureSaveData ReadCore(
        string saveDirectory,
        string? gameInstallDirectory,
        CancellationToken cancellationToken)
    {
        lock (saveReadGate)
        {
            return ReadCoreLocked(saveDirectory, gameInstallDirectory, cancellationToken);
        }
    }

    private KcdAdventureSaveData ReadCoreLocked(
        string saveDirectory,
        string? gameInstallDirectory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var saves = EnumerateSaves(saveDirectory, cancellationToken);
        if (saves.Count == 0)
        {
            return KcdAdventureSaveData.Empty;
        }

        var latest = saves
            .OrderByDescending(item => item.SaveTime ?? DateTimeOffset.MinValue)
            .ThenByDescending(item => item.LastWriteTimeUtc)
            .First();
        var catalog = LoadCatalog(gameInstallDirectory, cancellationToken);
        var key = new SaveCacheKey(
            latest.Path,
            latest.Length,
            latest.LastWriteTimeUtc.UtcTicks,
            latest.SaveTime?.UtcTicks ?? 0,
            saves.Count,
            catalog.Fingerprint);

        if (saveCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var progress = ScanWorldState(latest.Path, catalog, cancellationToken);

        var result = new KcdAdventureSaveData(
            FormatDifficulty(latest.GameMode),
            progress.InGameDay,
            progress.MainCompleted,
            progress.MainTotal,
            null,
            catalog.AchievementTotal,
            progress.LocationsDiscovered,
            progress.LocationsTotal,
            saves.Count);

        if (saveCache.Count >= 12)
        {
            saveCache.Clear();
        }

        saveCache[key] = result;
        return result;
    }

    private static IReadOnlyList<WhsSaveInfo> EnumerateSaves(
        string saveDirectory,
        CancellationToken cancellationToken)
    {
        List<WhsSaveInfo> saves = [];
        foreach (var path in Directory.EnumerateFiles(saveDirectory, "*.whs", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var file = new FileInfo(path);
                var header = ReadHeader(path);
                saves.Add(new WhsSaveInfo(
                    path,
                    file.Length,
                    new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero),
                    header.SaveTime,
                    header.GameMode));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // A single incomplete autosave must not hide the rest of the playline.
            }
        }

        return saves;
    }

    private static WhsHeader ReadHeader(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        Span<byte> prefix = stackalloc byte[8];
        stream.ReadExactly(prefix);
        if (BinaryPrimitives.ReadInt32LittleEndian(prefix) != -1)
        {
            throw new InvalidDataException("Unsupported KCD2 save signature.");
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix[4..]);
        if (length <= 0 || length > MaximumDescriptionBytes || length > stream.Length - 8)
        {
            throw new InvalidDataException("Invalid KCD2 save description length.");
        }

        var description = new byte[length];
        stream.ReadExactly(description);
        var text = Encoding.UTF8.GetString(description);
        var openingTagEnd = text.IndexOf('>');
        if (openingTagEnd < 0)
        {
            throw new InvalidDataException("KCD2 save description has no root tag.");
        }

        var attributes = ParseAttributes(text[..(openingTagEnd + 1)]);
        DateTimeOffset? saveTime = null;
        if (attributes.TryGetValue("SaveTime", out var saveTimeValue)
            && long.TryParse(saveTimeValue, out var unixSeconds))
        {
            try
            {
                saveTime = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
            }
            catch (ArgumentOutOfRangeException)
            {
                saveTime = null;
            }
        }

        attributes.TryGetValue("GameMode", out var gameMode);
        return new WhsHeader(saveTime, gameMode);
    }

    private static WorldStateProgress ScanWorldState(
        string path,
        GameCatalog catalog,
        CancellationToken cancellationToken)
    {
        using var file = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        Span<byte> header = stackalloc byte[8];
        file.ReadExactly(header);
        if (BinaryPrimitives.ReadInt32LittleEndian(header) != -1)
        {
            throw new InvalidDataException("Unsupported KCD2 save signature.");
        }

        var descriptionLength = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
        if (descriptionLength <= 0
            || descriptionLength > MaximumDescriptionBytes
            || descriptionLength > file.Length - file.Position)
        {
            throw new InvalidDataException("Invalid KCD2 save description length.");
        }

        file.Position += descriptionLength;
        using var scanner = new WorldStateScanner(catalog.MainQuests, catalog.LocationIds);
        while (file.Length - file.Position >= header.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            file.ReadExactly(header);
            var compressedLength = BinaryPrimitives.ReadInt32LittleEndian(header);
            var uncompressedLength = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
            if (compressedLength <= 6
                || uncompressedLength <= 0
                || compressedLength > file.Length - file.Position)
            {
                break;
            }

            if (scanner.Length + uncompressedLength > MaximumInflatedBytes)
            {
                throw new InvalidDataException("KCD2 save world state is too large.");
            }

            var nextBlockPosition = file.Position + compressedLength;
            using var compressed = new BoundedReadStream(file, compressedLength);
            using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
            var blockStart = scanner.Length;
            zlib.CopyTo(scanner);
            if (scanner.Length - blockStart != uncompressedLength)
            {
                throw new InvalidDataException("KCD2 save block length does not match its header.");
            }

            file.Position = nextBlockPosition;
        }

        if (scanner.Length == 0)
        {
            throw new InvalidDataException("KCD2 save contains no readable world-state blocks.");
        }

        return scanner.BuildResult();
    }

    private GameCatalog LoadCatalog(string? gameInstallDirectory, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(gameInstallDirectory))
        {
            return GameCatalog.Empty;
        }

        var scriptsPath = Path.Combine(gameInstallDirectory, "Data", "Scripts.pak");
        var tablesPath = Path.Combine(gameInstallDirectory, "Data", "Tables.pak");
        if (!File.Exists(scriptsPath) || !File.Exists(tablesPath))
        {
            return GameCatalog.Empty;
        }

        var scriptsInfo = new FileInfo(scriptsPath);
        var tablesInfo = new FileInfo(tablesPath);
        var fingerprint = HashCode.Combine(
            scriptsInfo.Length,
            scriptsInfo.LastWriteTimeUtc.Ticks,
            tablesInfo.Length,
            tablesInfo.LastWriteTimeUtc.Ticks);

        lock (catalogGate)
        {
            if (catalogCache is not null
                && string.Equals(catalogCache.InstallDirectory, gameInstallDirectory, StringComparison.OrdinalIgnoreCase)
                && catalogCache.Catalog.Fingerprint == fingerprint)
            {
                return catalogCache.Catalog;
            }

            var catalog = new GameCatalog(
                LoadMainQuestDefinitions(scriptsPath, cancellationToken),
                LoadLocationIds(tablesPath),
                LoadAchievementTotal(tablesPath),
                fingerprint);
            catalogCache = new GameCatalogCache(gameInstallDirectory, catalog);
            return catalog;
        }
    }

    private static IReadOnlyList<MainQuestDefinition> LoadMainQuestDefinitions(
        string scriptsPath,
        CancellationToken cancellationToken)
    {
        List<MainQuestDefinition> quests = [];
        using var archive = ZipFile.OpenRead(scriptsPath);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!entry.FullName.StartsWith("Quests/Final/Barbora/", StringComparison.OrdinalIgnoreCase)
                || !entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                || entry.Length <= 0
                || entry.Length > 20 * 1024 * 1024)
            {
                continue;
            }

            using var reader = new StreamReader(entry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var text = reader.ReadToEnd();
            var questMatch = QuestTagRegex.Match(text);
            if (!questMatch.Success)
            {
                continue;
            }

            var questAttributes = ParseAttributes(questMatch.Groups["attributes"].Value);
            if (!questAttributes.TryGetValue("Name", out var name)
                || !questAttributes.TryGetValue("ProductionCode", out var productionCode)
                || !MainProductionCodeRegex.IsMatch(productionCode))
            {
                continue;
            }

            var stateNames = StateTagRegex
                .Matches(text)
                .Select(match => ParseAttributes(match.Groups["attributes"].Value))
                .Where(attributes => attributes.TryGetValue("TypeT", out var type)
                    && string.Equals(type, "wh::questmodule::QuestProgress", StringComparison.OrdinalIgnoreCase))
                .Select(attributes => attributes.TryGetValue("Name", out var stateName) ? stateName : null)
                .Where(stateName => !string.IsNullOrWhiteSpace(stateName))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            quests.Add(new MainQuestDefinition(name, stateNames));
        }

        return quests;
    }

    private static IReadOnlyList<Guid> LoadLocationIds(string tablesPath)
    {
        using var archive = ZipFile.OpenRead(tablesPath);
        var entry = archive.GetEntry("Libs/Tables/rpg/location.xml");
        if (entry is null)
        {
            return [];
        }

        using var stream = entry.Open();
        var document = XDocument.Load(stream, LoadOptions.None);
        return document
            .Descendants("location")
            .Select(element => Guid.TryParse(element.Attribute("location_id")?.Value, out var id) ? id : (Guid?)null)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToArray();
    }

    private static int? LoadAchievementTotal(string tablesPath)
    {
        using var archive = ZipFile.OpenRead(tablesPath);
        var entry = archive.GetEntry("Libs/Tables/rpg/achievement.xml");
        if (entry is null)
        {
            return null;
        }

        using var stream = entry.Open();
        var document = XDocument.Load(stream, LoadOptions.None);
        var total = document.Descendants("achievement").Count();
        return total > 0 ? total : null;
    }

    private static (int? Completed, int? Total) ReadMainStoryProgress(
        ReadOnlySpan<byte> worldState,
        IReadOnlyList<MainQuestDefinition> quests)
    {
        if (quests.Count == 0)
        {
            return (null, null);
        }

        var completed = 0;
        foreach (var quest in quests)
        {
            var section = FindModuleSection(worldState, quest.Name);
            if (section.Length == 0)
            {
                continue;
            }

            var isCompleted = false;
            foreach (var stateName in quest.StateNames)
            {
                if (NodeHasValue(section, stateName, "Done"))
                {
                    isCompleted = true;
                    break;
                }
            }

            if (isCompleted)
            {
                completed++;
            }
        }

        return (completed, quests.Count);
    }

    private static ReadOnlySpan<byte> FindModuleSection(ReadOnlySpan<byte> worldState, string moduleName)
    {
        var start = FindOpeningTag(worldState, moduleName);
        if (start < 0)
        {
            return ReadOnlySpan<byte>.Empty;
        }

        var openingEndRelative = worldState[start..].IndexOf((byte)'>');
        if (openingEndRelative < 0)
        {
            return ReadOnlySpan<byte>.Empty;
        }

        var openingEnd = start + openingEndRelative;
        var endToken = "</_" + moduleName + ">";
        var endRelative = IndexOfAsciiIgnoreCase(worldState[openingEnd..], endToken);
        var end = endRelative < 0 ? -1 : openingEnd + endRelative;
        return end > start
            ? worldState.Slice(start, end - start)
            : ReadOnlySpan<byte>.Empty;
    }

    private static bool NodeHasValue(
        ReadOnlySpan<byte> moduleSection,
        string stateName,
        string expectedValue)
    {
        var searchOffset = 0;
        while (searchOffset < moduleSection.Length)
        {
            var relativeIndex = FindOpeningTag(moduleSection[searchOffset..], stateName);
            if (relativeIndex < 0)
            {
                return false;
            }

            var index = searchOffset + relativeIndex;
            var openingEnd = moduleSection[index..].IndexOf((byte)'>');
            if (openingEnd < 0)
            {
                return false;
            }

            var openingTag = moduleSection.Slice(index, openingEnd + 1);
            const string valueToken = "value=\"";
            var valueIndex = IndexOfAsciiIgnoreCase(openingTag, valueToken);
            if (valueIndex >= 0)
            {
                var valueStart = valueIndex + valueToken.Length;
                var valueEnd = openingTag[valueStart..].IndexOf((byte)'"');
                if (valueEnd >= 0
                    && EqualsAsciiIgnoreCase(openingTag.Slice(valueStart, valueEnd), expectedValue))
                {
                    return true;
                }
            }

            searchOffset = index + openingEnd + 1;
        }

        return false;
    }

    private static int FindOpeningTag(ReadOnlySpan<byte> source, string nodeName)
    {
        var tokenWithAttributes = "<_" + nodeName + " ";
        var withAttributes = IndexOfAsciiIgnoreCase(source, tokenWithAttributes);
        var tokenWithoutAttributes = "<_" + nodeName + ">";
        var withoutAttributes = IndexOfAsciiIgnoreCase(source, tokenWithoutAttributes);
        if (withAttributes < 0)
        {
            return withoutAttributes;
        }

        return withoutAttributes < 0
            ? withAttributes
            : Math.Min(withAttributes, withoutAttributes);
    }

    private static (int? Discovered, int? Total) ReadLocationProgress(
        ReadOnlySpan<byte> worldState,
        IReadOnlyList<Guid> locationIds)
    {
        if (locationIds.Count == 0)
        {
            return (null, null);
        }

        var parsed = 0;
        var discovered = 0;
        foreach (var locationId in locationIds)
        {
            var guidBytes = locationId.ToByteArray();
            var searchOffset = 0;
            while (searchOffset <= worldState.Length - guidBytes.Length - sizeof(int))
            {
                var relative = worldState[searchOffset..].IndexOf(guidBytes);
                if (relative < 0)
                {
                    break;
                }

                var index = searchOffset + relative;
                var state = BinaryPrimitives.ReadInt32LittleEndian(worldState.Slice(index + guidBytes.Length, sizeof(int)));
                if (state is >= 0 and <= 4)
                {
                    parsed++;
                    if (state > 0)
                    {
                        discovered++;
                    }

                    break;
                }

                searchOffset = index + 1;
            }
        }

        return parsed == locationIds.Count
            ? (discovered, locationIds.Count)
            : (null, null);
    }

    private static int? ReadInGameDay(ReadOnlySpan<byte> worldState)
    {
        const string watchToken = "<_timeofdaywatch_activateNewDay";
        const string timestampToken = "NextStart=\"";
        HashSet<long> uniqueTimestamps = [];
        var searchOffset = 0;
        while (searchOffset < worldState.Length)
        {
            var relativeWatch = IndexOfAsciiIgnoreCase(worldState[searchOffset..], watchToken);
            if (relativeWatch < 0)
            {
                break;
            }

            var watchStart = searchOffset + relativeWatch;
            var suffixIndex = watchStart + watchToken.Length;
            if (suffixIndex >= worldState.Length
                || (worldState[suffixIndex] != (byte)'_' && !IsAsciiWhitespace(worldState[suffixIndex])))
            {
                searchOffset = watchStart + watchToken.Length;
                continue;
            }

            var openingEnd = worldState[watchStart..].IndexOf((byte)'>');
            if (openingEnd < 0)
            {
                break;
            }

            var openingTag = worldState.Slice(watchStart, openingEnd + 1);
            var relativeTimestamp = IndexOfAsciiIgnoreCase(openingTag, timestampToken);
            if (relativeTimestamp >= 0)
            {
                var digits = openingTag[(relativeTimestamp + timestampToken.Length)..];
                var quote = digits.IndexOf((byte)'"');
                if (quote > 0 && TryParsePositiveInt64(digits[..quote], out var timestamp))
                {
                    uniqueTimestamps.Add(timestamp);
                }
            }

            searchOffset = watchStart + openingEnd + 1;
        }

        return CalculateInGameDay(uniqueTimestamps);
    }

    private static int? CalculateInGameDay(IEnumerable<long> uniqueTimestamps)
    {
        var timestamps = uniqueTimestamps.ToArray();
        Array.Sort(timestamps);

        // The calendar's recurring new-day watchers serialize their absolute WorldTime in milliseconds.
        // Require consecutive clock records so unrelated quest timers cannot be mistaken for the game date.
        var hasConsecutiveClockRecords = false;
        for (var index = 1; index < timestamps.Length; index++)
        {
            var delta = timestamps[index] - timestamps[index - 1];
            if (delta > 0 && delta <= MillisecondsPerGameDay)
            {
                hasConsecutiveClockRecords = true;
                break;
            }
        }

        if (!hasConsecutiveClockRecords)
        {
            return null;
        }

        var firstTimestamp = timestamps[0];
        var day = (firstTimestamp / MillisecondsPerGameDay) + 1;
        return day is > 0 and <= int.MaxValue ? (int)day : null;
    }

    private static int IndexOfAsciiIgnoreCase(ReadOnlySpan<byte> source, string value)
    {
        if (value.Length == 0)
        {
            return 0;
        }

        for (var index = 0; index <= source.Length - value.Length; index++)
        {
            if (EqualsAsciiIgnoreCase(source.Slice(index, value.Length), value))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool EqualsAsciiIgnoreCase(ReadOnlySpan<byte> source, string value)
    {
        if (source.Length != value.Length)
        {
            return false;
        }

        for (var index = 0; index < source.Length; index++)
        {
            var left = source[index];
            var right = (byte)value[index];
            if (left == right)
            {
                continue;
            }

            if (left is >= (byte)'A' and <= (byte)'Z')
            {
                left = (byte)(left + ('a' - 'A'));
            }

            if (right is >= (byte)'A' and <= (byte)'Z')
            {
                right = (byte)(right + ('a' - 'A'));
            }

            if (left != right)
            {
                return false;
            }
        }

        return true;
    }

    private static bool EqualsAsciiIgnoreCase(ReadOnlySpan<byte> source, ReadOnlySpan<byte> value)
    {
        if (source.Length != value.Length)
        {
            return false;
        }

        for (var index = 0; index < source.Length; index++)
        {
            var left = source[index];
            var right = value[index];
            if (left == right)
            {
                continue;
            }

            if (left is >= (byte)'A' and <= (byte)'Z')
            {
                left = (byte)(left + ('a' - 'A'));
            }

            if (right is >= (byte)'A' and <= (byte)'Z')
            {
                right = (byte)(right + ('a' - 'A'));
            }

            if (left != right)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAsciiWhitespace(byte value) =>
        value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';

    private static bool TryParsePositiveInt64(ReadOnlySpan<byte> digits, out long value)
    {
        value = 0;
        foreach (var digit in digits)
        {
            if (digit is < (byte)'0' or > (byte)'9')
            {
                value = 0;
                return false;
            }

            var number = digit - (byte)'0';
            if (value > (long.MaxValue - number) / 10)
            {
                value = 0;
                return false;
            }

            value = (value * 10) + number;
        }

        return value > 0;
    }

    private static bool TryReadIntegerStatistic(byte[] worldState, string name, out int value)
    {
        value = 0;
        var nameBytes = Encoding.ASCII.GetBytes(name);
        var nameIndex = worldState.AsSpan().IndexOf(nameBytes);
        if (nameIndex < 0)
        {
            return false;
        }

        var payload = nameIndex + nameBytes.Length + 1;
        if (payload + 10 > worldState.Length
            || worldState[payload] != 0x8F
            || worldState[payload + 1] != 0x37
            || BinaryPrimitives.ReadInt32LittleEndian(worldState.AsSpan(payload + 2, 4)) != sizeof(int))
        {
            return false;
        }

        value = BinaryPrimitives.ReadInt32LittleEndian(worldState.AsSpan(payload + 6, 4));
        return value >= 0;
    }

    private static int CountPlayedDays(IReadOnlyList<WhsSaveInfo> saves) =>
        saves
            .Where(save => save.SaveTime.HasValue)
            .Select(save => DateOnly.FromDateTime(save.SaveTime!.Value.LocalDateTime))
            .Distinct()
            .Count();

    private static string FormatDifficulty(string? gameMode)
    {
        if (string.IsNullOrWhiteSpace(gameMode))
        {
            return "--";
        }

        return gameMode.Contains("hardcore", StringComparison.OrdinalIgnoreCase)
            ? "Hardcore"
            : gameMode.Contains("normal", StringComparison.OrdinalIgnoreCase)
                ? "Normal"
                : char.ToUpperInvariant(gameMode[0]) + gameMode[1..].ToLowerInvariant();
    }

    private static Dictionary<string, string> ParseAttributes(string text)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in AttributeRegex.Matches(text).Cast<Match>().Concat(RootAttributeRegex.Matches(text).Cast<Match>()))
        {
            attributes[match.Groups["name"].Value] = match.Groups["value"].Value;
        }

        return attributes;
    }

    private sealed record WhsHeader(DateTimeOffset? SaveTime, string? GameMode);

    private sealed record WhsSaveInfo(
        string Path,
        long Length,
        DateTimeOffset LastWriteTimeUtc,
        DateTimeOffset? SaveTime,
        string? GameMode);

    private sealed record MainQuestDefinition(string Name, IReadOnlyList<string> StateNames);

    private sealed record GameCatalog(
        IReadOnlyList<MainQuestDefinition> MainQuests,
        IReadOnlyList<Guid> LocationIds,
        int? AchievementTotal,
        int Fingerprint)
    {
        public static GameCatalog Empty { get; } = new([], [], null, 0);
    }

    private sealed record GameCatalogCache(string InstallDirectory, GameCatalog Catalog);

    private sealed record WorldStateProgress(
        int? InGameDay,
        int? MainCompleted,
        int? MainTotal,
        int? LocationsDiscovered,
        int? LocationsTotal);

    private sealed class WorldStateScanner : Stream
    {
        private const int MaximumTagBytes = 16 * 1024;
        private const string WatchTagName = "_timeofdaywatch_activateNewDay";
        private const string TimestampToken = "NextStart=\"";
        private readonly QuestScanDefinition[] quests;
        private readonly Dictionary<int, List<QuestScanDefinition>> questsByTagHash = [];
        private readonly LocationScanDefinition[] locations;
        private readonly Dictionary<uint, List<LocationScanDefinition>> locationsByPrefix = [];
        private readonly HashSet<long> timestamps = [];
        private readonly byte[] tagBuffer = new byte[MaximumTagBytes];
        private readonly byte[] locationWindow = new byte[20];
        private QuestScanDefinition? activeQuest;
        private int tagLength;
        private bool capturingTag;
        private int locationWindowCount;
        private int locationWindowStart;
        private long bytesWritten;

        public WorldStateScanner(
            IReadOnlyList<MainQuestDefinition> questDefinitions,
            IReadOnlyList<Guid> locationIds)
        {
            quests = questDefinitions.Select(definition => new QuestScanDefinition(definition)).ToArray();
            foreach (var quest in quests)
            {
                AddByHash(questsByTagHash, HashAsciiIgnoreCase(quest.TagName), quest);
            }

            locations = locationIds.Select(id => new LocationScanDefinition(id.ToByteArray())).ToArray();
            foreach (var location in locations)
            {
                AddByHash(locationsByPrefix, ReadUInt32(location.Bytes), location);
            }
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => bytesWritten;
        public override long Position
        {
            get => bytesWritten;
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            foreach (var value in buffer)
            {
                ScanTagByte(value);
                ScanLocationByte(value);
            }

            bytesWritten += buffer.Length;
        }

        public WorldStateProgress BuildResult()
        {
            var parsedLocations = locations.Count(location => location.Found);
            return new WorldStateProgress(
                CalculateInGameDay(timestamps),
                quests.Length == 0 ? null : quests.Count(quest => quest.Completed),
                quests.Length == 0 ? null : quests.Length,
                parsedLocations == locations.Length && locations.Length > 0
                    ? locations.Count(location => location.State > 0)
                    : null,
                locations.Length == 0 ? null : locations.Length);
        }

        private void ScanTagByte(byte value)
        {
            if (!capturingTag)
            {
                if (value == (byte)'<')
                {
                    capturingTag = true;
                    tagLength = 1;
                    tagBuffer[0] = value;
                }
                return;
            }

            if (value == (byte)'<' && tagLength > 1)
            {
                tagLength = 1;
                tagBuffer[0] = value;
                return;
            }

            if (tagLength == tagBuffer.Length)
            {
                capturingTag = false;
                tagLength = 0;
                return;
            }

            tagBuffer[tagLength++] = value;
            if (value != (byte)'>')
            {
                return;
            }

            ProcessTag(tagBuffer.AsSpan(0, tagLength));
            capturingTag = false;
            tagLength = 0;
        }

        private void ProcessTag(ReadOnlySpan<byte> tag)
        {
            var index = 1;
            var closing = index < tag.Length && tag[index] == (byte)'/';
            if (closing)
            {
                index++;
            }

            while (index < tag.Length && IsAsciiWhitespace(tag[index]))
            {
                index++;
            }

            if (index >= tag.Length || tag[index] is (byte)'!' or (byte)'?')
            {
                return;
            }

            var nameStart = index;
            while (index < tag.Length
                   && !IsAsciiWhitespace(tag[index])
                   && tag[index] is not (byte)'>' and not (byte)'/')
            {
                index++;
            }

            var name = tag[nameStart..index];
            if (name.Length == 0)
            {
                return;
            }

            if (!closing && IsWatchTag(name))
            {
                ReadTimestamp(tag);
            }

            if (activeQuest is { } active)
            {
                if (closing && EqualsAsciiIgnoreCase(name, active.TagName))
                {
                    activeQuest = null;
                    return;
                }

                if (!closing
                    && !active.Completed
                    && active.StatesByTagHash.TryGetValue(HashAsciiIgnoreCase(name), out var states)
                    && ContainsName(states, name)
                    && TagHasValue(tag, "Done"))
                {
                    active.Completed = true;
                }
                return;
            }

            if (closing
                || !questsByTagHash.TryGetValue(HashAsciiIgnoreCase(name), out var matchingQuests))
            {
                return;
            }

            activeQuest = FindQuest(matchingQuests, name);
        }

        private void ReadTimestamp(ReadOnlySpan<byte> tag)
        {
            var relativeTimestamp = IndexOfAsciiIgnoreCase(tag, TimestampToken);
            if (relativeTimestamp < 0)
            {
                return;
            }

            var digits = tag[(relativeTimestamp + TimestampToken.Length)..];
            var quote = digits.IndexOf((byte)'\"');
            if (quote > 0 && TryParsePositiveInt64(digits[..quote], out var timestamp))
            {
                timestamps.Add(timestamp);
            }
        }

        private static bool IsWatchTag(ReadOnlySpan<byte> name) =>
            EqualsAsciiIgnoreCase(name, WatchTagName)
            || (name.Length > WatchTagName.Length
                && name[WatchTagName.Length] == (byte)'_'
                && EqualsAsciiIgnoreCase(name[..WatchTagName.Length], WatchTagName));

        private static bool TagHasValue(ReadOnlySpan<byte> tag, string expectedValue)
        {
            const string valueToken = "value=\"";
            var valueIndex = IndexOfAsciiIgnoreCase(tag, valueToken);
            if (valueIndex < 0)
            {
                return false;
            }

            var value = tag[(valueIndex + valueToken.Length)..];
            var quote = value.IndexOf((byte)'\"');
            return quote >= 0 && EqualsAsciiIgnoreCase(value[..quote], expectedValue);
        }

        private void ScanLocationByte(byte value)
        {
            if (locationWindowCount < locationWindow.Length)
            {
                locationWindow[(locationWindowStart + locationWindowCount) % locationWindow.Length] = value;
                locationWindowCount++;
            }
            else
            {
                locationWindow[locationWindowStart] = value;
                locationWindowStart = (locationWindowStart + 1) % locationWindow.Length;
            }

            if (locationWindowCount == locationWindow.Length)
            {
                InspectLocationWindow();
            }
        }

        private void InspectLocationWindow()
        {
            var prefix = (uint)(WindowByte(0)
                | (WindowByte(1) << 8)
                | (WindowByte(2) << 16)
                | (WindowByte(3) << 24));
            if (!locationsByPrefix.TryGetValue(prefix, out var candidates))
            {
                return;
            }

            foreach (var candidate in candidates)
            {
                if (candidate.Found || !LocationBytesMatch(candidate.Bytes))
                {
                    continue;
                }

                var state = WindowByte(16)
                    | (WindowByte(17) << 8)
                    | (WindowByte(18) << 16)
                    | (WindowByte(19) << 24);
                if (state is >= 0 and <= 4)
                {
                    candidate.State = state;
                    candidate.Found = true;
                }
            }
        }

        private bool LocationBytesMatch(ReadOnlySpan<byte> expected)
        {
            for (var index = 0; index < expected.Length; index++)
            {
                if (WindowByte(index) != expected[index])
                {
                    return false;
                }
            }

            return true;
        }

        private int WindowByte(int offset) =>
            locationWindow[(locationWindowStart + offset) % locationWindow.Length];

        private static uint ReadUInt32(ReadOnlySpan<byte> bytes) =>
            BinaryPrimitives.ReadUInt32LittleEndian(bytes);

        private static int HashAsciiIgnoreCase(ReadOnlySpan<byte> value)
        {
            var hash = 17;
            foreach (var character in value)
            {
                var normalized = character is >= (byte)'A' and <= (byte)'Z'
                    ? character + ('a' - 'A')
                    : character;
                hash = unchecked((hash * 31) + normalized);
            }

            return hash;
        }

        private static bool ContainsName(IEnumerable<byte[]> names, ReadOnlySpan<byte> value)
        {
            foreach (var name in names)
            {
                if (EqualsAsciiIgnoreCase(value, name))
                {
                    return true;
                }
            }

            return false;
        }

        private static QuestScanDefinition? FindQuest(
            IEnumerable<QuestScanDefinition> candidates,
            ReadOnlySpan<byte> name)
        {
            foreach (var candidate in candidates)
            {
                if (EqualsAsciiIgnoreCase(name, candidate.TagName))
                {
                    return candidate;
                }
            }

            return null;
        }

        private static void AddByHash<TKey, TValue>(
            Dictionary<TKey, List<TValue>> dictionary,
            TKey key,
            TValue value) where TKey : notnull
        {
            if (!dictionary.TryGetValue(key, out var values))
            {
                values = [];
                dictionary.Add(key, values);
            }

            values.Add(value);
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        private sealed class QuestScanDefinition
        {
            public QuestScanDefinition(MainQuestDefinition definition)
            {
                TagName = Encoding.ASCII.GetBytes("_" + definition.Name);
                foreach (var stateName in definition.StateNames)
                {
                    var tagName = Encoding.ASCII.GetBytes("_" + stateName);
                    AddByHash(StatesByTagHash, HashAsciiIgnoreCase(tagName), tagName);
                }
            }

            public byte[] TagName { get; }
            public Dictionary<int, List<byte[]>> StatesByTagHash { get; } = [];
            public bool Completed { get; set; }
        }

        private sealed class LocationScanDefinition(byte[] bytes)
        {
            public byte[] Bytes { get; } = bytes;
            public bool Found { get; set; }
            public int State { get; set; }
        }
    }

    private sealed class BoundedReadStream : Stream
    {
        private readonly Stream source;
        private readonly long length;
        private long remaining;

        public BoundedReadStream(Stream source, long length)
        {
            this.source = source ?? throw new ArgumentNullException(nameof(source));
            if (length < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(length));
            }

            this.length = length;
            remaining = length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position
        {
            get => length - remaining;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (remaining == 0)
            {
                return 0;
            }

            var read = source.Read(buffer[..(int)Math.Min(buffer.Length, remaining)]);
            remaining -= read;
            return read;
        }

        public override int ReadByte()
        {
            if (remaining == 0)
            {
                return -1;
            }

            var value = source.ReadByte();
            if (value >= 0)
            {
                remaining--;
            }

            return value;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private readonly record struct SaveCacheKey(
        string Path,
        long Length,
        long LastWriteTicks,
        long SaveTimeTicks,
        int SaveCount,
        int CatalogFingerprint);
}
