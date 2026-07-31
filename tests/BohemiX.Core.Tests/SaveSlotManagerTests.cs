using BohemiX.Core.Models.Saves;
using BohemiX.Core.Services.Saves;
using BohemiX.Infrastructure.Services.Saves;
using System.Text.Json;
using Serilog;

namespace BohemiX.Core.Tests;

public sealed class SaveSlotManagerTests
{
    [Fact]
    public void SaveParser_ParseMetadata_ReadsCommonXmlFields()
    {
        var root = CreateTempRoot();

        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(
                Path.Combine(root, "metadata.xml"),
                """
                <Save>
                  <SaveName>Rattay before rain</SaveName>
                  <PlayTime>3661</PlayTime>
                  <SaveTime>2026-06-18T10:20:30Z</SaveTime>
                </Save>
                """);

            var parser = new SaveParser();
            var metadata = parser.ParseMetadata(root);

            Assert.Equal("Rattay before rain", metadata.GameSaveName);
            Assert.Equal(TimeSpan.FromSeconds(3661), metadata.PlayTime);
            Assert.Equal(DateTimeOffset.Parse("2026-06-18T10:20:30Z").ToUniversalTime(), metadata.LastSavedAtUtc);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task SaveParser_ParseMetadata_ReadsJsonMetaDisplayFields()
    {
        var root = CreateTempRoot();

        try
        {
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(
                Path.Combine(root, "meta.json"),
                JsonSerializer.Serialize(new SaveMeta
                {
                    SaveID = "save_rattay",
                    PlaylineID = "playline0",
                    Type = SaveType.Potion,
                    GameVersion = "1.0.0",
                    Timestamp = DateTimeOffset.Parse("2026-06-18T10:20:30Z").ToUnixTimeSeconds(),
                    PlayTimeTotal = 3661,
                    ThumbnailPath = "thumb.png",
                    DisplayData = new DisplayInfo
                    {
                        HenryLevel = 14,
                        CurrentLocation = "Rattay",
                        ActiveQuest = "The King's Gambit",
                        GroschenCount = 128,
                        PlayerStatusEffects = ["Well Rested", "Nourished"]
                    }
                }));

            var parser = new SaveParser();
            var metadata = parser.ParseMetadata(root);

            Assert.Equal(TimeSpan.FromSeconds(3661), metadata.PlayTime);
            Assert.Equal(DateTimeOffset.Parse("2026-06-18T10:20:30Z").ToUniversalTime(), metadata.LastSavedAtUtc);
            Assert.Equal("The King's Gambit", metadata.GameSaveName);
            Assert.Equal(SaveType.Potion, metadata.Type);
            Assert.Equal(14, metadata.DisplayData!.HenryLevel);
            Assert.Equal("Rattay", metadata.DisplayData.CurrentLocation);
            Assert.Equal(128, metadata.DisplayData.GroschenCount);
            Assert.Equal(["Well Rested", "Nourished"], metadata.DisplayData.PlayerStatusEffects);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task SaveParser_ParseMetadata_ReadsEmbeddedWhsDescription()
    {
        var root = CreateTempRoot();

        try
        {
            Directory.CreateDirectory(root);
            await File.WriteAllBytesAsync(
                Path.Combine(root, "exit.whs"),
                [
                    0xFF, 0xFF, 0xFF, 0xFF,
                    ..System.Text.Encoding.UTF8.GetBytes("""
                    <C_SaveGameDescription FormatVersion="0" SaveType="ExitSave" SaveId="450" SaveTime="1780218593" LevelName="kutnohorsko" PlayerId="0" UIDescription="5|450|@qname_u201_rodinna_chlouba_v2Kr||location_kutnaHora|1780218593|31/05/2026 17:09|49.080746|" BuildInfo="1.5.2-14493-release_1_5" AssemblyDate="2025-11-25" GameReleaseVersion="10502" NewGameReleaseVersion="0" GameMode="normal">
                      <DLCs />
                      <Locations>
                        <structwh::rpgmodule::S_LocationId>98d121c8-f500-425f-87f6-bef4a97f26d5</structwh::rpgmodule::S_LocationId>
                      </Locations>
                    </C_SaveGameDescription>
                    """)
                ]);

            var parser = new SaveParser();
            var metadata = parser.ParseMetadata(root);

            Assert.Equal(SaveType.Exit, metadata.Type);
            Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1780218593), metadata.LastSavedAtUtc);
            Assert.Equal(TimeSpan.FromHours(49.080746), metadata.PlayTime);
            Assert.Equal("U201 Rodinna Chlouba V2 Kr", metadata.GameSaveName);
            Assert.Equal("Kutna Hora", metadata.DisplayData!.CurrentLocation);
            Assert.Equal("U201 Rodinna Chlouba V2 Kr", metadata.DisplayData.ActiveQuest);
            Assert.Equal("1.5.2-14493-release_1_5", metadata.GameVersion);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }


    [Fact]
    public async Task ImportSave_CreatesGeneratedPhysicalSlotAndMovesOriginalFiles()
    {
        var root = CreateTempRoot();

        try
        {
            var official = Path.Combine(root, "official");
            var vault = Path.Combine(root, "vault");
            var database = Path.Combine(root, "bohemix.db");
            Directory.CreateDirectory(official);
            await File.WriteAllTextAsync(Path.Combine(official, "metadata.xml"), "<Save><SaveName>Original Core Name</SaveName><PlayTime>42</PlayTime></Save>");
            await File.WriteAllBytesAsync(Path.Combine(official, "save.whs"), [1, 2, 3, 4]);

            var manager = new SlotManager(
                official,
                vault,
                database,
                new SaveParser(),
                new AlwaysSafeCoordinator(),
                new LoggerConfiguration().CreateLogger());

            var slot = await manager.ImportSave("My Display Slot");

            Assert.Matches(@"^Slot_\d{8}_\d{6}_[0-9a-f]{8}$", slot.PhysicalName);
            Assert.Equal("My Display Slot", slot.DisplayName);
            Assert.True(File.Exists(Path.Combine(slot.PhysicalPath, "metadata.xml")));
            Assert.True(File.Exists(Path.Combine(slot.PhysicalPath, "save.whs")));
            Assert.False(File.Exists(Path.Combine(official, "metadata.xml")));
            Assert.False(File.Exists(Path.Combine(official, "save.whs")));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task RenameSlot_ChangesOnlyLogicalDisplayName()
    {
        var root = CreateTempRoot();

        try
        {
            var official = Path.Combine(root, "official");
            var vault = Path.Combine(root, "vault");
            var database = Path.Combine(root, "bohemix.db");
            Directory.CreateDirectory(official);
            await File.WriteAllTextAsync(Path.Combine(official, "metadata.xml"), "<Save><SaveName>Game Name</SaveName></Save>");

            var manager = new SlotManager(
                official,
                vault,
                database,
                new SaveParser(),
                new AlwaysSafeCoordinator(),
                new LoggerConfiguration().CreateLogger());

            var imported = await manager.ImportSave("First Name");
            await manager.RenameSlot(imported.Id, "Logical Only");
            var renamed = (await manager.GetAllSlots()).Single();

            Assert.Equal(imported.PhysicalName, renamed.PhysicalName);
            Assert.Equal(imported.PhysicalPath, renamed.PhysicalPath);
            Assert.Equal("Logical Only", renamed.DisplayName);
            Assert.True(Directory.Exists(imported.PhysicalPath));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task RenameSlot_RejectsInvalidDisplayNameCharacters()
    {
        var root = CreateTempRoot();

        try
        {
            var official = Path.Combine(root, "official");
            var vault = Path.Combine(root, "vault");
            var database = Path.Combine(root, "bohemix.db");
            Directory.CreateDirectory(official);
            await File.WriteAllTextAsync(Path.Combine(official, "metadata.xml"), "<Save><SaveName>Game Name</SaveName></Save>");

            var manager = new SlotManager(
                official,
                vault,
                database,
                new SaveParser(),
                new AlwaysSafeCoordinator(),
                new LoggerConfiguration().CreateLogger());

            var imported = await manager.ImportSave("First Name");
            await Assert.ThrowsAsync<ArgumentException>(() => manager.RenameSlot(imported.Id, "bad\u0001name"));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task GetAllSlots_BackfillsDisplayMetadataForExistingRows()
    {
        var root = CreateTempRoot();

        try
        {
            var official = Path.Combine(root, "official");
            var vault = Path.Combine(root, "vault");
            var database = Path.Combine(root, "bohemix.db");
            Directory.CreateDirectory(official);
            await File.WriteAllTextAsync(
                Path.Combine(official, "meta.json"),
                JsonSerializer.Serialize(new SaveMeta
                {
                    SaveID = "old_row",
                    PlaylineID = "playline0",
                    Type = SaveType.Bed,
                    GameVersion = "1.0.1",
                    Timestamp = 42,
                    PlayTimeTotal = 120,
                    DisplayData = new DisplayInfo
                    {
                        HenryLevel = 8,
                        CurrentLocation = "Trosky",
                        ActiveQuest = "Back in the Saddle",
                        GroschenCount = 30
                    }
                }));

            var manager = new SlotManager(
                official,
                vault,
                database,
                new SaveParser(),
                new AlwaysSafeCoordinator(),
                new LoggerConfiguration().CreateLogger());

            var imported = await manager.ImportSave("Old Row");

            await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={database};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    UPDATE SaveSlots
                    SET GameSaveName = NULL,
                        PlayTimeSeconds = NULL,
                        LastSavedAtUtc = NULL,
                        HenryLevel = NULL,
                        CurrentLocation = NULL,
                        ActiveQuest = NULL,
                        GroschenCount = NULL
                    WHERE Id = $id;
                    """;
                command.Parameters.AddWithValue("$id", imported.Id.ToString("D"));
                await command.ExecuteNonQueryAsync();
            }

            var refreshed = (await manager.GetAllSlots()).Single(slot => slot.Id == imported.Id);

            Assert.Equal("Back in the Saddle", refreshed.GameSaveName);
            Assert.Equal(TimeSpan.FromSeconds(120), refreshed.PlayTime);
            Assert.Equal("Trosky", refreshed.DisplayData!.CurrentLocation);
            Assert.Equal(8, refreshed.DisplayData.HenryLevel);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    private static string CreateTempRoot() =>
        Path.Combine(Path.GetTempPath(), "BohemiX.Tests", Guid.NewGuid().ToString("N"));

    private static void DeleteTempRoot(string root)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }

                return;
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(100);
            }
        }
    }

    private sealed class AlwaysSafeCoordinator : IProcessCoordinator
    {
        public Task<bool> IsSafeToSwitch(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }
}
