using NAudio.CoreAudioApi;
using NAudio.Vorbis;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace BohemiX.Modules.Alchemy.Services;

public enum AlchemySoundCue
{
    Pour,
    HerbToMortar,
    Grind,
    PowderPour,
    HerbDrop,
    CauldronSplash,
    CauldronLower,
    CauldronLift,
    Bellows,
    HourglassStart,
    HourglassSand,
    HourglassComplete,
    Fire,
    Distill,
    Bottle,
    BrewComplete,
    BrewDiluted,
    Mistake,
    UiClick,
    BookOpen,
    BookClose,
    BookPage
}

public interface IAlchemyAudioService : IDisposable
{
    void Play(AlchemySoundCue cue, double intensity = 1);
    void StartLoop(AlchemySoundCue cue, double intensity = 1);
    void StopLoop(AlchemySoundCue cue);
    void StopAll();
}

public sealed class SampledAlchemyAudioService : IAlchemyAudioService
{
    private const int SampleRate = 44100;
    private const int Channels = 2;
    private static readonly WaveFormat OutputFormat = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);
    private static readonly IReadOnlyDictionary<AlchemySoundCue, SoundProfile> Profiles =
        new Dictionary<AlchemySoundCue, SoundProfile>
        {
            [AlchemySoundCue.Pour] = new(
                "pour_water.ogg", 1,
                LoopStartSeconds: .18,
                LoopEndTrimSeconds: .18,
                LoopCrossfadeSeconds: .08),
            [AlchemySoundCue.HerbToMortar] = new("herb_to_mortar.ogg", .68),
            [AlchemySoundCue.Grind] = new("mortar_grind.ogg", .76, .24, true),
            [AlchemySoundCue.PowderPour] = new("powder_pour.ogg", .82),
            [AlchemySoundCue.HerbDrop] = new("herb_drop.ogg", .86),
            [AlchemySoundCue.CauldronSplash] = new("water_drop.ogg", .7),
            [AlchemySoundCue.CauldronLower] = new("cauldron_lower.ogg", .82),
            [AlchemySoundCue.CauldronLift] = new("cauldron_lift.ogg", .76),
            [AlchemySoundCue.Bellows] = new("bellows.ogg", 1),
            [AlchemySoundCue.HourglassStart] = new("hourglass_flip.ogg", .7),
            [AlchemySoundCue.HourglassSand] = new(
                "hourglass_sand.ogg", .42,
                LoopStartSeconds: .35,
                LoopEndTrimSeconds: .35,
                LoopCrossfadeSeconds: .12),
            [AlchemySoundCue.HourglassComplete] = new("glass_settle.ogg", .62),
            [AlchemySoundCue.Fire] = new(
                "fire_loop.ogg", .38,
                LoopStartSeconds: .4,
                LoopEndTrimSeconds: .4,
                LoopCrossfadeSeconds: .22),
            [AlchemySoundCue.Distill] = new("water_drop.ogg", .86),
            [AlchemySoundCue.Bottle] = new("glass_settle.ogg", .82),
            [AlchemySoundCue.BrewComplete] = new("brew_complete.ogg", .7),
            [AlchemySoundCue.BrewDiluted] = new("brew_diluted.ogg", .66),
            [AlchemySoundCue.Mistake] = new("brew_diluted.ogg", .38),
            [AlchemySoundCue.UiClick] = new("ui_click.ogg", .62),
            [AlchemySoundCue.BookOpen] = new("book_open.ogg", .64),
            [AlchemySoundCue.BookClose] = new("book_close.ogg", .6),
            [AlchemySoundCue.BookPage] = new("book_page.ogg", .64)
        };

    private readonly IWavePlayer? output;
    private readonly MixingSampleProvider? mixer;
    private readonly Dictionary<AlchemySoundCue, LoadedSound> sounds = [];
    private readonly Dictionary<AlchemySoundCue, LoopingCachedSoundProvider> loops = [];
    private readonly object loopGate = new();
    private bool disposed;

    public SampledAlchemyAudioService()
    {
        var audioDirectory = Path.Combine(AppContext.BaseDirectory, "Assets", "Alchemy", "Audio");
        foreach (var (cue, profile) in Profiles)
        {
            try
            {
                var path = Path.Combine(audioDirectory, profile.FileName);
                sounds[cue] = new LoadedSound(CachedSound.Load(path), profile);
            }
            catch
            {
                // A missing or damaged optional cue must not prevent the workshop from opening.
            }
        }

        try
        {
            mixer = new MixingSampleProvider(OutputFormat) { ReadFully = true };
            output = CreateOutput();
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

    public bool IsAvailable => !disposed && output is not null && mixer is not null && sounds.Count > 0;

    public void Play(AlchemySoundCue cue, double intensity = 1)
    {
        if (disposed || mixer is null || !sounds.TryGetValue(cue, out var loaded))
        {
            return;
        }

        var volume = loaded.Profile.Gain * Math.Clamp(intensity, .12, 1);
        mixer.AddMixerInput(loaded.Sound.CreateProvider(
            volume,
            loaded.Profile.ClipSeconds,
            loaded.Profile.RandomOffset));
    }

    public void StartLoop(AlchemySoundCue cue, double intensity = 1)
    {
        if (disposed || mixer is null || !sounds.TryGetValue(cue, out var loaded))
        {
            return;
        }

        var volume = loaded.Profile.Gain * Math.Clamp(intensity, .05, 1);
        lock (loopGate)
        {
            if (loops.TryGetValue(cue, out var existing))
            {
                existing.SetVolume(volume);
                return;
            }

            var provider = loaded.Sound.CreateLoopingProvider(
                volume,
                loaded.Profile.LoopStartSeconds,
                loaded.Profile.LoopEndTrimSeconds,
                loaded.Profile.LoopCrossfadeSeconds);
            loops[cue] = provider;
            mixer.AddMixerInput(provider);
        }
    }

    public void StopLoop(AlchemySoundCue cue)
    {
        if (mixer is null)
        {
            return;
        }

        lock (loopGate)
        {
            if (loops.Remove(cue, out var provider))
            {
                mixer.RemoveMixerInput(provider);
            }
        }
    }

    public void StopAll()
    {
        lock (loopGate)
        {
            mixer?.RemoveAllMixerInputs();
            loops.Clear();
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        StopAll();
        disposed = true;
        output?.Stop();
        output?.Dispose();
    }

    private static IWavePlayer CreateOutput()
    {
        try
        {
            return new WasapiOut(AudioClientShareMode.Shared, 80);
        }
        catch
        {
            return new WaveOutEvent { DesiredLatency = 90 };
        }
    }

    private sealed record SoundProfile(
        string FileName,
        double Gain,
        double ClipSeconds = 0,
        bool RandomOffset = false,
        double LoopStartSeconds = 0,
        double LoopEndTrimSeconds = 0,
        double LoopCrossfadeSeconds = .08);

    private sealed record LoadedSound(CachedSound Sound, SoundProfile Profile);

    private sealed class CachedSound
    {
        private readonly float[] samples;

        private CachedSound(float[] samples)
        {
            this.samples = samples;
        }

        public static CachedSound Load(string path)
        {
            using var reader = new VorbisWaveReader(path);
            ISampleProvider source = reader.ToSampleProvider();
            if (source.WaveFormat.SampleRate != SampleRate)
            {
                source = new WdlResamplingSampleProvider(source, SampleRate);
            }

            source = source.WaveFormat.Channels switch
            {
                1 => new MonoToStereoSampleProvider(source),
                2 => source,
                _ => throw new InvalidDataException($"Unsupported channel count in '{path}'.")
            };

            var samples = new List<float>();
            var buffer = new float[SampleRate * Channels];
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                samples.AddRange(buffer.AsSpan(0, read).ToArray());
            }

            return new CachedSound(samples.ToArray());
        }

        public ISampleProvider CreateProvider(double volume, double clipSeconds, bool randomOffset) =>
            new CachedSoundProvider(samples, volume, clipSeconds, randomOffset);

        public LoopingCachedSoundProvider CreateLoopingProvider(
            double volume,
            double startSeconds,
            double endTrimSeconds,
            double crossfadeSeconds) =>
            new(samples, volume, startSeconds, endTrimSeconds, crossfadeSeconds);
    }

    private sealed class CachedSoundProvider : ISampleProvider
    {
        private const int FadeFrames = 320;
        private readonly float[] samples;
        private readonly double volume;
        private readonly int start;
        private readonly int end;
        private int position;

        public CachedSoundProvider(float[] samples, double volume, double clipSeconds, bool randomOffset)
        {
            this.samples = samples;
            this.volume = volume;
            var totalFrames = samples.Length / Channels;
            var requestedFrames = clipSeconds > 0
                ? Math.Min(totalFrames, (int)(clipSeconds * SampleRate))
                : totalFrames;
            var startFrame = randomOffset && totalFrames > requestedFrames
                ? Random.Shared.Next(0, totalFrames - requestedFrames)
                : 0;
            start = startFrame * Channels;
            position = start;
            end = Math.Min(samples.Length, start + (requestedFrames * Channels));
        }

        public WaveFormat WaveFormat => OutputFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            var available = Math.Min(count, end - position);
            available -= available % Channels;
            var totalFrames = Math.Max(1, (end - start) / Channels);
            for (var sampleIndex = 0; sampleIndex < available; sampleIndex += Channels)
            {
                var frame = (position + sampleIndex - start) / Channels;
                var fadeIn = Math.Min(1, frame / (double)FadeFrames);
                var fadeOut = Math.Min(1, (totalFrames - frame - 1) / (double)FadeFrames);
                var gain = (float)(volume * Math.Min(fadeIn, fadeOut));
                buffer[offset + sampleIndex] = samples[position + sampleIndex] * gain;
                buffer[offset + sampleIndex + 1] = samples[position + sampleIndex + 1] * gain;
            }

            position += available;
            return available;
        }
    }

    private sealed class LoopingCachedSoundProvider : ISampleProvider
    {
        private readonly float[] samples;
        private readonly int startFrame;
        private readonly int endFrame;
        private readonly int crossfadeFrames;
        private float currentVolume;
        private volatile float targetVolume;
        private int positionFrame;

        public LoopingCachedSoundProvider(
            float[] samples,
            double volume,
            double startSeconds,
            double endTrimSeconds,
            double crossfadeSeconds)
        {
            this.samples = samples;
            var totalFrames = samples.Length / Channels;
            startFrame = Math.Clamp((int)(startSeconds * SampleRate), 0, Math.Max(0, totalFrames - 2));
            endFrame = Math.Clamp(totalFrames - (int)(endTrimSeconds * SampleRate), startFrame + 2, totalFrames);
            crossfadeFrames = Math.Clamp(
                (int)(crossfadeSeconds * SampleRate),
                1,
                Math.Max(1, (endFrame - startFrame) / 4));
            positionFrame = startFrame;
            currentVolume = (float)volume;
            targetVolume = (float)volume;
        }

        public WaveFormat WaveFormat => OutputFormat;

        public void SetVolume(double volume) => targetVolume = (float)Math.Clamp(volume, 0, 1);

        public int Read(float[] buffer, int offset, int count)
        {
            var frames = count / Channels;
            for (var frameIndex = 0; frameIndex < frames; frameIndex++)
            {
                currentVolume += (targetVolume - currentVolume) * .004f;
                var outputIndex = offset + (frameIndex * Channels);
                var crossfadeStart = endFrame - crossfadeFrames;
                if (positionFrame >= crossfadeStart)
                {
                    var crossfadeFrame = positionFrame - crossfadeStart;
                    var mix = crossfadeFrame / (float)crossfadeFrames;
                    var incomingFrame = startFrame + crossfadeFrame;
                    var outgoingIndex = positionFrame * Channels;
                    var incomingIndex = incomingFrame * Channels;
                    buffer[outputIndex] = ((samples[outgoingIndex] * (1 - mix)) + (samples[incomingIndex] * mix)) * currentVolume;
                    buffer[outputIndex + 1] = ((samples[outgoingIndex + 1] * (1 - mix)) + (samples[incomingIndex + 1] * mix)) * currentVolume;
                }
                else
                {
                    var sampleIndex = positionFrame * Channels;
                    buffer[outputIndex] = samples[sampleIndex] * currentVolume;
                    buffer[outputIndex + 1] = samples[sampleIndex + 1] * currentVolume;
                }

                positionFrame++;
                if (positionFrame >= endFrame)
                {
                    positionFrame = startFrame + crossfadeFrames;
                }
            }

            return frames * Channels;
        }
    }
}
