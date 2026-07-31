#pragma warning disable CS0618 // The fake path service implements the retained legacy interface member.

using BohemiX.Core.Models;
using BohemiX.Core.PlayerProfiles;
using BohemiX.Core.Services;
using BohemiX.Modules.Forge.Models;
using BohemiX.Modules.Forge.Services;

namespace BohemiX.Modules.Forge.Tests;

public sealed class ForgeProgressStoreTests
{
    [Fact]
    public async Task ProgressStore_ResolvesTheCurrentPlayerPathForEveryOperation()
    {
        var root = Path.Combine(Path.GetTempPath(), "BohemiX.ForgeProfiles.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new MutablePathService(CreatePaths(Path.Combine(root, "Henry")));
            var store = new JsonForgeProgressStore(paths);
            var empty = new ForgeProfile(1, [], new Dictionary<string, int>());

            await store.RecordAsync(empty, new ForgeHistoryEntry(
                DateTimeOffset.UtcNow,
                "duelling-longsword",
                "balanced-steel",
                ForgeQuality.Fine,
                81));

            paths.Current = CreatePaths(Path.Combine(root, "Theresa"));
            Assert.Empty((await store.LoadAsync()).History);
            await store.RecordAsync(empty, new ForgeHistoryEntry(
                DateTimeOffset.UtcNow,
                "basilard",
                "soft-steel",
                ForgeQuality.Normal,
                63));

            paths.Current = CreatePaths(Path.Combine(root, "Henry"));
            var henry = await store.LoadAsync();
            Assert.Single(henry.History);
            Assert.Equal("duelling-longsword", henry.History[0].RecipeId);
            Assert.Equal(81, henry.History[0].Score);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static ApplicationPaths CreatePaths(string root) =>
        new(
            root,
            Path.Combine(root, "bohemix.db"),
            Path.Combine(root, "Logs"),
            Path.Combine(root, "Mods"),
            Path.Combine(root, "Native"),
            Path.Combine(root, "Tracker"),
            Path.Combine(root, "Tracker", "bridge.jsonl"),
            Path.Combine(root, "Tracker", "rules.json"),
            SettingsDirectory: Path.Combine(root, "Settings"));

    private sealed class MutablePathService : IApplicationPathService
    {
        public MutablePathService(ApplicationPaths current)
        {
            Current = current;
        }

        public ApplicationPaths Current { get; set; }

        public ApplicationPaths GetPaths() => Current;

        public GlobalApplicationPaths GetGlobalPaths() =>
            throw new NotSupportedException();

        public PlayerProfilePaths GetProfilePaths(Guid playerId) =>
            throw new NotSupportedException();
    }
}

#pragma warning restore CS0618
