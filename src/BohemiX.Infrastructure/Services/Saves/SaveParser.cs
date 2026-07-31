using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using BohemiX.Core.Models.Saves;
using BohemiX.Core.Services.Saves;

namespace BohemiX.Infrastructure.Services.Saves;

public sealed class SaveParser : IKcdSaveParser
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Reads metadata.xml from a Vault slot and extracts UI-facing save metadata.
    /// </summary>
    public KcdSaveMetadata ParseMetadata(string slotPath)
    {
        var metaJsonPath = FindMetadataFile(slotPath, "meta.json");
        if (metaJsonPath is not null)
        {
            var metadata = TryParseJsonMetadata(metaJsonPath);
            if (metadata is not null)
            {
                return metadata;
            }
        }

        var metadataXmlPath = FindMetadataFile(slotPath, "metadata.xml");
        if (metadataXmlPath is null)
        {
            var whsMetadata = TryParseWhsMetadata(slotPath);
            return whsMetadata ?? new KcdSaveMetadata(null, null, null);
        }

        // [防御性编程] 只读打开且允许共享，解析器绝不修改 metadata.xml、.pak 或 .whs 原名/内容。
        using var stream = new FileStream(metadataXmlPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var xmlReader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = true
        });

        var document = XDocument.Load(xmlReader);
        var gameSaveName = FirstValue(document, "SaveName", "save_name", "Name", "Title", "Description");
        var playTime = TryParsePlayTime(FirstValue(document, "PlayTime", "play_time", "PlayedTime", "TimePlayed", "GameplayTime"));
        var lastSavedAt = TryParseDateTime(FirstValue(document, "SaveTime", "save_time", "LastSaved", "Timestamp", "SystemTime"));
        var displayData = new DisplayInfo
        {
            HenryLevel = TryParseInt(FirstValue(document, "HenryLevel", "Level", "PlayerLevel", "player_level")) ?? 0,
            CurrentLocation = FirstValue(document, "CurrentLocation", "Location", "MapLocation", "Area") ?? string.Empty,
            ActiveQuest = FirstValue(document, "ActiveQuest", "Quest", "QuestName", "ActiveObjective") ?? string.Empty,
            GroschenCount = TryParseInt(FirstValue(document, "GroschenCount", "Groschen", "Money", "Coins")) ?? 0,
            PlayerStatusEffects = SplitStatusEffects(FirstValue(document, "PlayerStatusEffects", "StatusEffects", "Buffs", "Effects"))
        };

        return new KcdSaveMetadata(playTime, lastSavedAt, gameSaveName, HasDisplayData(displayData) ? displayData : null);
    }

    /// <summary>
    /// Opens a candidate binary save file and returns a stable skeleton for future reverse-engineered world-state parsing.
    /// </summary>
    public KcdWorldState ExtractWorldState(string slotPath)
    {
        var candidate = Directory.EnumerateFiles(slotPath, "*.whs", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(slotPath, "*.pak", SearchOption.AllDirectories))
            .FirstOrDefault();

        if (candidate is null)
        {
            return new KcdWorldState(null, [], [], []);
        }

        // [防御性编程] BinaryReader 当前只读文件签名，不移动、不复制、不重命名任何核心存档文件。
        using var stream = new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
        var signature = reader.ReadBytes((int)Math.Min(32, stream.Length));

        return new KcdWorldState(candidate, signature, [], []);
    }

    private static string? FindMetadataFile(string slotPath, string fileName)
    {
        var direct = Path.Combine(slotPath, fileName);
        if (File.Exists(direct))
        {
            return direct;
        }

        if (!Directory.Exists(slotPath))
        {
            return null;
        }

        return Directory.EnumerateFiles(slotPath, "*", SearchOption.AllDirectories)
            .FirstOrDefault(path => string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase));
    }

    private static KcdSaveMetadata? TryParseJsonMetadata(string metadataPath)
    {
        try
        {
            using var stream = new FileStream(metadataPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var meta = JsonSerializer.Deserialize<SaveMeta>(stream, JsonOptions);
            if (meta is null)
            {
                return null;
            }

            return new KcdSaveMetadata(
                TimeSpan.FromSeconds(Math.Max(0d, meta.PlayTimeTotal)),
                meta.Timestamp > 0 ? DateTimeOffset.FromUnixTimeSeconds(meta.Timestamp) : null,
                CreateJsonDisplayName(meta),
                meta.DisplayData,
                meta.Type,
                meta.GameVersion,
                meta.ThumbnailPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    private static KcdSaveMetadata? TryParseWhsMetadata(string slotPath)
    {
        var candidate = FindLatestWhsFile(slotPath);
        if (candidate is null)
        {
            return null;
        }

        try
        {
            var bytes = File.ReadAllBytes(candidate);
            var text = Encoding.UTF8.GetString(bytes);
            const string startMarker = "<C_SaveGameDescription";
            const string endMarker = "</C_SaveGameDescription>";
            var start = text.IndexOf(startMarker, StringComparison.Ordinal);
            if (start < 0)
            {
                return CreateFileFallbackMetadata(candidate);
            }

            var end = text.IndexOf(endMarker, start, StringComparison.Ordinal);
            if (end < 0)
            {
                return CreateFileFallbackMetadata(candidate);
            }

            var openingTagEnd = text.IndexOf('>', start);
            if (openingTagEnd < 0 || openingTagEnd > end)
            {
                return CreateFileFallbackMetadata(candidate);
            }

            // KCD2 .whs embeds a C_SaveGameDescription block, but nested tags can use
            // engine names such as structwh::rpgmodule::S_LocationId, which are not
            // valid XML element names. Parse only the root tag attributes here.
            var attributes = ParseRootAttributes(text.Substring(start, openingTagEnd - start + 1));

            var saveTime = TryParseDateTime(GetAttribute(attributes, "SaveTime"))
                ?? new DateTimeOffset(new FileInfo(candidate).LastWriteTimeUtc, TimeSpan.Zero);
            var saveType = TryParseSaveType(GetAttribute(attributes, "SaveType"));
            var uiParts = (GetAttribute(attributes, "UIDescription") ?? string.Empty).Split('|');
            var questKey = uiParts.Length > 2 ? uiParts[2] : null;
            var locationKey = uiParts.Length > 4 ? uiParts[4] : null;
            TimeSpan? playTime = uiParts.Length > 7 && double.TryParse(uiParts[7], NumberStyles.Float, CultureInfo.InvariantCulture, out var hours)
                ? TimeSpan.FromHours(hours)
                : null;
            var location = HumanizeKcdKey(locationKey) ?? HumanizeKcdKey(GetAttribute(attributes, "LevelName")) ?? Path.GetFileNameWithoutExtension(candidate);
            var quest = HumanizeKcdKey(questKey) ?? Path.GetFileNameWithoutExtension(candidate);

            return new KcdSaveMetadata(
                playTime,
                saveTime,
                quest,
                new DisplayInfo
                {
                    CurrentLocation = location,
                    ActiveQuest = quest
                },
                saveType,
                GetAttribute(attributes, "BuildInfo"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException or ArgumentException)
        {
            return CreateFileFallbackMetadata(candidate);
        }
    }

    private static Dictionary<string, string> ParseRootAttributes(string openingTag)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(openingTag, "(?<name>[A-Za-z_][A-Za-z0-9_]*)=\"(?<value>[^\"]*)\""))
        {
            result[match.Groups["name"].Value] = match.Groups["value"].Value;
        }

        return result;
    }

    private static string? GetAttribute(IReadOnlyDictionary<string, string> attributes, string name) =>
        attributes.TryGetValue(name, out var value) ? value : null;

    private static string? FindLatestWhsFile(string slotPath)
    {
        if (File.Exists(slotPath) && string.Equals(Path.GetExtension(slotPath), ".whs", StringComparison.OrdinalIgnoreCase))
        {
            return slotPath;
        }

        if (!Directory.Exists(slotPath))
        {
            return null;
        }

        return Directory
            .EnumerateFiles(slotPath, "*.whs", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .FirstOrDefault()
            ?.FullName;
    }

    private static KcdSaveMetadata CreateFileFallbackMetadata(string filePath)
    {
        var info = new FileInfo(filePath);
        var name = Path.GetFileNameWithoutExtension(filePath);
        var saveType = InferSaveTypeFromFileName(name);

        return new KcdSaveMetadata(
            null,
            new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
            HumanizeSaveFileName(name),
            new DisplayInfo
            {
                CurrentLocation = HumanizeSaveFileName(name),
                ActiveQuest = HumanizeSaveFileName(name)
            },
            saveType);
    }

    private static SaveType? TryParseSaveType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.ToLowerInvariant() switch
        {
            "exitsave" => SaveType.Exit,
            "autosave" => SaveType.Auto,
            "bedsave" => SaveType.Bed,
            "saviourschnappssave" or "permanentsave" or "manualsave" => SaveType.Potion,
            _ => null
        };
    }

    private static SaveType? InferSaveTypeFromFileName(string fileName)
    {
        if (fileName.StartsWith("auto", StringComparison.OrdinalIgnoreCase))
        {
            return SaveType.Auto;
        }

        if (fileName.StartsWith("exit", StringComparison.OrdinalIgnoreCase))
        {
            return SaveType.Exit;
        }

        return fileName.StartsWith("permanent", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("save", StringComparison.OrdinalIgnoreCase)
                ? SaveType.Potion
                : null;
    }

    private static string? HumanizeKcdKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var cleaned = value.Trim().Trim('@');
        foreach (var prefix in new[] { "qname_", "location_", "loc_", "ui_" })
        {
            if (cleaned.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                cleaned = cleaned[prefix.Length..];
                break;
            }
        }

        return HumanizeSaveFileName(cleaned);
    }

    private static string HumanizeSaveFileName(string value)
    {
        var builder = new StringBuilder(value.Length + 8);
        char previous = '\0';

        foreach (var current in value)
        {
            if (current is '_' or '-' or '.')
            {
                builder.Append(' ');
            }
            else
            {
                if (char.IsUpper(current) && builder.Length > 0 && previous != ' ' && !char.IsUpper(previous))
                {
                    builder.Append(' ');
                }

                builder.Append(current);
            }

            previous = builder.Length > 0 ? builder[^1] : current;
        }

        var words = builder
            .ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return words.Length == 0
            ? value
            : string.Join(' ', words.Select(CapitalizeWord));
    }

    private static string CapitalizeWord(string value)
    {
        if (value.Length == 0 || value.All(char.IsUpper))
        {
            return value;
        }

        return value.Length == 1
            ? value.ToUpperInvariant()
            : char.ToUpperInvariant(value[0]) + value[1..];
    }

    private static string? CreateJsonDisplayName(SaveMeta meta)
    {
        if (!string.IsNullOrWhiteSpace(meta.DisplayData.ActiveQuest))
        {
            return meta.DisplayData.ActiveQuest;
        }

        if (!string.IsNullOrWhiteSpace(meta.DisplayData.CurrentLocation))
        {
            return meta.DisplayData.CurrentLocation;
        }

        return string.IsNullOrWhiteSpace(meta.SaveID) ? null : meta.SaveID;
    }

    private static string? FirstValue(XDocument document, params string[] names)
    {
        var nameSet = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var elementValue = document.Descendants()
            .FirstOrDefault(element => nameSet.Contains(element.Name.LocalName))
            ?.Value
            .Trim();

        if (!string.IsNullOrWhiteSpace(elementValue))
        {
            return elementValue;
        }

        return document.Descendants()
            .Attributes()
            .FirstOrDefault(attribute => nameSet.Contains(attribute.Name.LocalName))
            ?.Value
            .Trim();
    }

    private static TimeSpan? TryParsePlayTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
        {
            return TimeSpan.FromSeconds(seconds);
        }

        return TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    private static int? TryParseInt(string? value)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static List<string> SplitStatusEffects(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return value
            .Split([';', ',', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    private static bool HasDisplayData(DisplayInfo displayData) =>
        displayData.HenryLevel > 0
        || displayData.GroschenCount > 0
        || !string.IsNullOrWhiteSpace(displayData.CurrentLocation)
        || !string.IsNullOrWhiteSpace(displayData.ActiveQuest)
        || displayData.PlayerStatusEffects.Count > 0;

    private static DateTimeOffset? TryParseDateTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixSeconds))
        {
            return DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        }

        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal,
            out var parsed)
            ? parsed.ToUniversalTime()
            : null;
    }
}
