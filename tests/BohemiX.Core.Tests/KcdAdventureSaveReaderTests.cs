using System.IO.Compression;
using System.Text;
using BohemiX.Infrastructure.Services.Saves;

namespace BohemiX.Core.Tests;

public sealed class KcdAdventureSaveReaderTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(64)]
    public async Task ReadAsync_ProjectsSpoilerFreeProgressFromCurrentPlayline(int blockSize)
    {
        var root = Path.Combine(Path.GetTempPath(), "BohemiX.Tests", Guid.NewGuid().ToString("N"));
        var saveDirectory = Path.Combine(root, "saves");
        var gameDirectory = Path.Combine(root, "game");
        var dataDirectory = Path.Combine(gameDirectory, "Data");
        Directory.CreateDirectory(saveDirectory);
        Directory.CreateDirectory(dataDirectory);

        try
        {
            var firstLocation = Guid.Parse("0855c91f-2ce2-413f-b16a-113754d2cebe");
            var secondLocation = Guid.Parse("0d231c01-2911-49ec-9af9-9edd5fd3f7e1");
            CreateScriptsPackage(Path.Combine(dataDirectory, "Scripts.pak"));
            CreateTablesPackage(Path.Combine(dataDirectory, "Tables.pak"), firstLocation, secondLocation);

            var worldState = CreateWorldState(firstLocation, secondLocation);
            CreateSave(
                Path.Combine(saveDirectory, "save001.whs"),
                DateTimeOffset.Parse("2026-07-20T12:00:00Z"),
                "normal",
                worldState,
                blockSize);
            CreateSave(
                Path.Combine(saveDirectory, "exit.whs"),
                DateTimeOffset.Parse("2026-07-22T12:00:00Z"),
                "hardcore",
                worldState,
                blockSize);

            var result = await new KcdAdventureSaveReader().ReadAsync(saveDirectory, gameDirectory);

            Assert.Equal("Hardcore", result.Difficulty);
            Assert.Equal(3, result.InGameDay);
            Assert.Equal(1, result.MainStoryCompleted);
            Assert.Equal(3, result.MainStoryTotal);
            Assert.Null(result.AchievementsUnlocked);
            Assert.Equal(3, result.AchievementsTotal);
            Assert.Equal(1, result.LocationsDiscovered);
            Assert.Equal(2, result.LocationsTotal);
            Assert.Equal(2, result.SaveFileCount);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static byte[] CreateWorldState(Guid firstLocation, Guid secondLocation)
    {
        using var stream = new MemoryStream();
        var xml = Encoding.Latin1.GetBytes(
            "<Root>" +
            "<_mainOne Version=\"1\"><Nodes><_progress><_progress value=\"Done\" /></_progress></Nodes></_mainOne>" +
            "<_mainTwo Version=\"1\"><Nodes><_progress value=\"Active\" /></Nodes></_mainTwo>" +
            "<_timeofdaywatch_activateNewDay value=\"Running\" NextStart=\"172800000\" />" +
            "<_timeofdaywatch_activateNewDay_1 value=\"Running\" NextStart=\"219600000\" />" +
            "</Root>");
        stream.Write(xml);
        stream.Write(firstLocation.ToByteArray());
        stream.Write(BitConverter.GetBytes(2));
        stream.Write(secondLocation.ToByteArray());
        stream.Write(BitConverter.GetBytes(0));
        return stream.ToArray();
    }

    private static void CreateScriptsPackage(string path)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        AddTextEntry(archive, "Quests/Final/Barbora/region/mainOne.xml", CreateQuest("mainOne", "M01"));
        AddTextEntry(archive, "Quests/Final/Barbora/region/mainTwo.xml", CreateQuest("mainTwo", "M02"));
        AddTextEntry(archive, "Quests/Final/Barbora/region/mainThree.xml", CreateQuest("mainThree", "M03"));
        AddTextEntry(archive, "Quests/Final/Barbora/region/sideOne.xml", CreateQuest("sideOne", "S01"));
    }

    private static string CreateQuest(string name, string productionCode) =>
        $"<Database><Skald><Quest Name=\"{name}\" ProductionCode=\"{productionCode}\">" +
        "<Nodes><State Name=\"progress\" TypeT=\"wh::questmodule::QuestProgress\" /></Nodes>" +
        "</Quest></Skald></Database>";

    private static void CreateTablesPackage(string path, Guid firstLocation, Guid secondLocation)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        AddTextEntry(
            archive,
            "Libs/Tables/rpg/location.xml",
            $"<database><locations><location location_id=\"{firstLocation:D}\" />" +
            $"<location location_id=\"{secondLocation:D}\" /></locations></database>");
        AddTextEntry(
            archive,
            "Libs/Tables/rpg/achievement.xml",
            "<database><achievements><achievement achievement_id=\"1\" />" +
            "<achievement achievement_id=\"2\" /><achievement achievement_id=\"3\" />" +
            "</achievements></database>");
    }

    private static void AddTextEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(content);
    }

    private static void CreateSave(
        string path,
        DateTimeOffset saveTime,
        string gameMode,
        byte[] worldState,
        int blockSize)
    {
        var description = Encoding.UTF8.GetBytes(
            $"<C_SaveGameDescription SaveTime=\"{saveTime.ToUnixTimeSeconds()}\" GameMode=\"{gameMode}\"></C_SaveGameDescription>\n\0");
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(-1);
        writer.Write(description.Length);
        writer.Write(description);

        for (var offset = 0; offset < worldState.Length; offset += blockSize)
        {
            var length = Math.Min(blockSize, worldState.Length - offset);
            using var compressed = new MemoryStream();
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            {
                zlib.Write(worldState, offset, length);
            }

            writer.Write(checked((int)compressed.Length));
            writer.Write(length);
            writer.Write(compressed.ToArray());
        }
    }
}
