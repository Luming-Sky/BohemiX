using BohemiX.Core.Models;
using BohemiX.Core.PlayerProfiles;
using BohemiX.Core.Services;

namespace BohemiX.ForgeHost;

internal sealed class ForgeHostApplicationPathService : IApplicationPathService
{
    public ApplicationPaths GetPaths()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BohemiX");
        var tracker = Path.Combine(root, "tracker");
        return new ApplicationPaths(
            root,
            Path.Combine(root, "bohemix.db"),
            Path.Combine(root, "logs"),
            Path.Combine(root, "mods"),
            Path.Combine(root, "native"),
            tracker,
            Path.Combine(tracker, "bridge_events.jsonl"),
            Path.Combine(tracker, "tracker_achievement_rules.json"),
            SettingsDirectory: Path.Combine(root, "Settings"));
    }

    public GlobalApplicationPaths GetGlobalPaths() =>
        throw new NotSupportedException("ForgeHost only resolves its progress storage path.");
}
