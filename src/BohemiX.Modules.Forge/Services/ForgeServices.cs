using System.Text.Json;
using BohemiX.Core.Services;
using BohemiX.Modules.Forge.Models;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace BohemiX.Modules.Forge.Services;

public enum ForgeSoundCue
{
    Bellows,
    Hammer,
    Rotate,
    Quench,
    Grind
}

public interface IForgeAudioService : IDisposable
{
    void Play(ForgeSoundCue cue, double intensity = 1);
    void StopAll();
}

public sealed class ProceduralForgeAudioService : IForgeAudioService
{
    private readonly WaveOutEvent? output;
    private readonly MixingSampleProvider? mixer;
    private bool disposed;

    public ProceduralForgeAudioService()
    {
        try
        {
            mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(44100, 2)) { ReadFully = true };
            output = new WaveOutEvent { DesiredLatency = 90 };
            output.Init(mixer);
            output.Play();
        }
        catch
        {
            output?.Dispose();
            output = null;
            mixer = null;
        }
    }

    public void Play(ForgeSoundCue cue, double intensity = 1)
    {
        if (disposed || mixer is null)
        {
            return;
        }

        mixer.AddMixerInput(new ForgeSynthProvider(cue, Math.Clamp(intensity, .1, 1)));
    }

    public void StopAll() => mixer?.RemoveAllMixerInputs();

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        output?.Stop();
        output?.Dispose();
    }

    private sealed class ForgeSynthProvider : ISampleProvider
    {
        private readonly ForgeSoundCue cue;
        private readonly double intensity;
        private readonly int totalFrames;
        private readonly Random random;
        private int frame;

        public ForgeSynthProvider(ForgeSoundCue cue, double intensity)
        {
            this.cue = cue;
            this.intensity = intensity;
            var duration = cue switch
            {
                ForgeSoundCue.Hammer => .42,
                ForgeSoundCue.Quench => .85,
                ForgeSoundCue.Grind => .22,
                _ => .32
            };
            totalFrames = (int)(44100 * duration);
            random = new Random(HashCode.Combine(cue, totalFrames, Random.Shared.Next()));
        }

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);

        public int Read(float[] buffer, int offset, int count)
        {
            var requestedFrames = count / 2;
            var frames = Math.Min(requestedFrames, totalFrames - frame);
            for (var i = 0; i < frames; i++)
            {
                var t = (frame + i) / 44100d;
                var progress = (frame + i) / (double)Math.Max(1, totalFrames);
                var envelope = cue switch
                {
                    ForgeSoundCue.Quench => Math.Pow(1 - progress, .65),
                    // Short overlapping grains form one interruptible contact sound.
                    // A soft attack removes clicks when pointer samples arrive quickly.
                    ForgeSoundCue.Grind => Math.Min(1, progress / .055) * Math.Pow(1 - progress, .72),
                    _ => Math.Pow(1 - progress, 2.3)
                };
                var noise = random.NextDouble() * 2 - 1;
                var sample = cue switch
                {
                    ForgeSoundCue.Hammer => Math.Sin(2 * Math.PI * 820 * t) * .58 + Math.Sin(2 * Math.PI * 1510 * t) * .23 + noise * .08,
                    ForgeSoundCue.Bellows => noise * .55 + Math.Sin(2 * Math.PI * 92 * t) * .12,
                    ForgeSoundCue.Rotate => Math.Sin(2 * Math.PI * 165 * t) * .30 + noise * .16,
                    ForgeSoundCue.Quench => noise * .62 + Math.Sin(2 * Math.PI * 410 * t) * .08,
                    ForgeSoundCue.Grind => noise * (.34 + .10 * Math.Sin(2 * Math.PI * 94 * t)) +
                                           Math.Sin(2 * Math.PI * 1260 * t) * .13 +
                                           Math.Sin(2 * Math.PI * 2380 * t) * .055,
                    _ => 0
                };
                var value = (float)(sample * envelope * intensity * .34);
                buffer[offset + i * 2] = value;
                buffer[offset + i * 2 + 1] = value;
            }

            frame += frames;
            return frames * 2;
        }
    }
}

public sealed record ForgeHistoryEntry(
    DateTimeOffset CreatedAt,
    string RecipeId,
    string MaterialId,
    ForgeQuality Quality,
    int Score);

public sealed record ForgeProfile(
    int SchemaVersion,
    IReadOnlyList<ForgeHistoryEntry> History,
    IReadOnlyDictionary<string, int> BestScores,
    bool ApprenticeHints = true,
    double MasterVolume = .75,
    double InputSensitivity = 1);

public interface IForgeProgressStore
{
    Task<ForgeProfile> LoadAsync(CancellationToken cancellationToken = default);
    Task<ForgeProfile> RecordAsync(ForgeProfile current, ForgeHistoryEntry entry, CancellationToken cancellationToken = default);
}

public sealed class JsonForgeProgressStore : IForgeProgressStore
{
    private readonly IApplicationPathService applicationPathService;
    private readonly JsonSerializerOptions options = new() { WriteIndented = true };

    public JsonForgeProgressStore(IApplicationPathService applicationPathService)
    {
        this.applicationPathService = applicationPathService;
    }

    public async Task<ForgeProfile> LoadAsync(CancellationToken cancellationToken = default)
    {
        var path = GetCurrentPath();
        if (!File.Exists(path))
        {
            return Empty();
        }

        try
        {
            await using var source = File.OpenRead(path);
            var profile = await JsonSerializer.DeserializeAsync<ForgeProfile>(source, options, cancellationToken);
            return profile is { SchemaVersion: 1 } ? MigrateRecipeIds(profile) : Empty();
        }
        catch
        {
            var backup = path + ".corrupt-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss");
            try
            {
                File.Move(path, backup, overwrite: true);
            }
            catch
            {
                // A locked profile should not prevent the simulator from starting.
            }
            return Empty();
        }
    }

    public async Task<ForgeProfile> RecordAsync(ForgeProfile current, ForgeHistoryEntry entry, CancellationToken cancellationToken = default)
    {
        var path = GetCurrentPath();
        var history = current.History.Prepend(entry).Take(20).ToArray();
        var best = new Dictionary<string, int>(current.BestScores, StringComparer.OrdinalIgnoreCase);
        var key = $"{entry.RecipeId}:{entry.MaterialId}";
        best[key] = Math.Max(best.GetValueOrDefault(key), entry.Score);
        var updated = current with { History = history, BestScores = best };
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temp = path + ".tmp";
        await using (var target = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(target, updated, options, cancellationToken);
        }
        File.Move(temp, path, overwrite: true);
        return updated;
    }

    private string GetCurrentPath()
    {
        var paths = applicationPathService.GetPaths();
        var settingsDirectory = paths.SettingsDirectory
            ?? Path.Combine(paths.DataDirectory, "Settings");
        return Path.Combine(settingsDirectory, "Forge", "profile-v1.json");
    }

    private static ForgeProfile Empty() => new(1, [], new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));

    internal static ForgeProfile MigrateRecipeIds(ForgeProfile profile)
    {
        var history = profile.History
            .Select(entry => entry with { RecipeId = MapRecipeId(entry.RecipeId) })
            .ToArray();
        var best = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, score) in profile.BestScores)
        {
            var separator = key.IndexOf(':');
            var migratedKey = separator < 0
                ? MapRecipeId(key)
                : $"{MapRecipeId(key[..separator])}{key[separator..]}";
            best[migratedKey] = Math.Max(best.GetValueOrDefault(migratedKey), score);
        }
        return profile with { History = history, BestScores = best };
    }

    private static string MapRecipeId(string recipeId) => recipeId.ToLowerInvariant() switch
    {
        "longsword" => "duelling-longsword",
        "shortsword" => "basilard",
        "axe" => "bearded-axe",
        _ => recipeId
    };
}
