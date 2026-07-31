using System.Text.Json;
using BohemiX.Core.Models.Saves;
using BohemiX.Core.Services.Saves;

namespace BohemiX.Core.Tests;

public sealed class SaveManagerCoreLogicTests
{
    [Fact]
    public async Task DiscoverSavesAsync_SkipsCorruptMetaAndSortsByTimestampDescending()
    {
        var root = CreateTempRoot();

        try
        {
            await WriteMeta(root, "playline0", "alpha", 10, SaveType.Bed);
            await WriteMeta(root, "playline0", "bravo", 30, SaveType.Auto);

            var corruptDirectory = Path.Combine(root, "playline1", "save_corrupt");
            Directory.CreateDirectory(corruptDirectory);
            await File.WriteAllTextAsync(Path.Combine(corruptDirectory, "meta.json"), "{ broken json");

            await File.WriteAllBytesAsync(Path.Combine(root, "playline0", "save_alpha", "core_data.whs"), [1, 2, 3]);

            var discovered = await SaveManager.DiscoverSavesAsync(root);

            Assert.Equal(["bravo", "alpha"], discovered.Select(meta => meta.SaveID).ToArray());
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task WriteSaveSafeAsync_WritesPayloadThenMetadataAndRemovesBackup()
    {
        var root = CreateTempRoot();
        var meta = CreateMeta("rattay-before-rain", SaveType.Potion, 100);
        var core = new byte[] { 7, 8, 9, 10 };

        try
        {
            var written = await SaveManager.WriteSaveSafeAsync(root, "playline0", meta, core);
            var saveDirectory = Path.Combine(root, "playline0", "save_rattay-before-rain");

            Assert.True(written);
            Assert.Equal(core, await File.ReadAllBytesAsync(Path.Combine(saveDirectory, "core_data.whs")));
            Assert.False(File.Exists(Path.Combine(saveDirectory, "core_data.whs.tmp")));
            Assert.False(File.Exists(Path.Combine(saveDirectory, "core_data.whs.bak")));

            var metaJson = await File.ReadAllTextAsync(Path.Combine(saveDirectory, "meta.json"));
            var persistedMeta = JsonSerializer.Deserialize<SaveMeta>(metaJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));

            Assert.NotNull(persistedMeta);
            Assert.Equal("playline0", persistedMeta.PlaylineID);
            Assert.Equal(SaveType.Potion, persistedMeta.Type);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task LoadCoreDataAsync_ThrowsCustomExceptionWhenPayloadIsMissing()
    {
        var root = CreateTempRoot();

        try
        {
            await WriteMeta(root, "playline0", "missing-core", 10, SaveType.Auto);

            await Assert.ThrowsAsync<SaveFileNotFoundException>(
                () => SaveManager.LoadCoreDataAsync(root, "playline0", "missing-core"));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task ConvertExitSaveToPermanentAsync_RenamesExitFolderAndPersistsPotionType()
    {
        var root = CreateTempRoot();
        var exitDirectory = Path.Combine(root, "playline0", "exit_quit-bridge");
        Directory.CreateDirectory(exitDirectory);
        await File.WriteAllBytesAsync(Path.Combine(exitDirectory, "core_data.whs"), [4, 5, 6]);
        await File.WriteAllTextAsync(
            Path.Combine(exitDirectory, "meta.json"),
            JsonSerializer.Serialize(CreateMeta("quit-bridge", SaveType.Exit, 200), new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        try
        {
            await SaveManager.ConvertExitSaveToPermanentAsync(root, "playline0", "quit-bridge");

            var permanentDirectory = Path.Combine(root, "playline0", "save_quit-bridge");
            Assert.False(Directory.Exists(exitDirectory));
            Assert.True(Directory.Exists(permanentDirectory));
            Assert.Equal([4, 5, 6], await File.ReadAllBytesAsync(Path.Combine(permanentDirectory, "core_data.whs")));

            var convertedJson = await File.ReadAllTextAsync(Path.Combine(permanentDirectory, "meta.json"));
            var convertedMeta = JsonSerializer.Deserialize<SaveMeta>(convertedJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));

            Assert.NotNull(convertedMeta);
            Assert.Equal(SaveType.Potion, convertedMeta.Type);
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    private static async Task WriteMeta(string root, string playlineId, string saveId, long timestamp, SaveType type)
    {
        var saveDirectory = Path.Combine(root, playlineId, $"save_{saveId}");
        Directory.CreateDirectory(saveDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(saveDirectory, "meta.json"),
            JsonSerializer.Serialize(CreateMeta(saveId, type, timestamp), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    private static SaveMeta CreateMeta(string saveId, SaveType type, long timestamp) =>
        new()
        {
            SaveID = saveId,
            PlaylineID = "playline0",
            Type = type,
            GameVersion = "1.0.0",
            Timestamp = timestamp,
            PlayTimeTotal = 3600,
            ThumbnailPath = "thumb.png",
            DisplayData = new DisplayInfo
            {
                HenryLevel = 12,
                CurrentLocation = "Rattay",
                ActiveQuest = "For Whom the Bell Tolls",
                GroschenCount = 42,
                PlayerStatusEffects = ["Well Rested"]
            }
        };

    private static string CreateTempRoot() =>
        Path.Combine(Path.GetTempPath(), "BohemiX.Tests", Guid.NewGuid().ToString("N"));

    private static void DeleteTempRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
