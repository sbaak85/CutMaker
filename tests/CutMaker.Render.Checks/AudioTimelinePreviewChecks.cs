using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using CutMaker.App;
using CutMaker.Core;

internal static class AudioTimelinePreviewChecks
{
    internal static async Task Run(string folder, Action<bool, string> check)
    {
        Directory.CreateDirectory(folder);
        var ffmpeg = MediaRenderService.FindTool("ffmpeg.exe");
        Task<string> Tool(params string[] arguments) => MediaRenderService.RunToolAsync(ffmpeg,
            new[] { "-hide_banner", "-nostdin", "-y", "-v", "error" }.Concat(arguments), CancellationToken.None);
        var source = Path.Combine(folder, "source.mp3");
        var other = Path.Combine(folder, "other.wav");
        await Tool("-f", "lavfi", "-i", "aevalsrc=0.11*sin(2*PI*419*t)|0.09*sin(2*PI*631*t):s=44100:d=3.5",
            "-c:a", "libmp3lame", "-b:a", "192k", source);
        await Tool("-f", "lavfi", "-i", "sine=frequency=211:sample_rate=48000:duration=3.5", "-c:a", "pcm_s16le", other);
        var sourceHash = SHA256.HashData(File.ReadAllBytes(source));
        var cache = new AudioPreviewCache(Path.Combine(folder, "source-cache-" + Guid.NewGuid().ToString("N")));
        var clip = new Clip("clip", "asset", "audio", .071013, .123457, 2.83, Gain: .78,
            FadeIn: new(.912341, FadeCurve.Custom, .13, .61), FadeOut: new(.634567, FadeCurve.EqualPower));
        var project = new CutProject
        {
            MediaAssets = [new("asset", source, MediaKind.Audio, 3.5)],
            Tracks = [new("audio", "Audio", TrackKind.Audio, Volume: .91)], Clips = [clip]
        };

        async Task<short[]> Reference(CutProject value, string name)
        {
            var wave = Path.Combine(folder, name + ".wav");
            await MediaRenderService.RenderAsync(value, null, wave);
            var pcm = Path.Combine(folder, name + ".s16");
            await Tool("-i", wave, "-c:a", "pcm_s16le", "-f", "s16le", pcm);
            return MemoryMarshal.Cast<byte, short>(File.ReadAllBytes(pcm)).ToArray();
        }

        var coldLoads = 0;
        short[] uncut;
        using (var mixer = await AudioTimelinePreview.PrepareAsync(project,
            new ImmediateProgress(value => { if (value.Message.StartsWith("首次載入音訊")) coldLoads++; }), cache: cache))
        {
            uncut = ReadAll(mixer);
            CheckNear(await Reference(project, "reference"), uncut,
                "realtime 44.1 kHz stereo MP3, gain and custom/equal-power fades match export", check);
            var random = new short[1234 * 2];
            var seek = AudioSampleClock.At(1.123456789);
            var returned = mixer.ReadFrames(seek, random, 1234);
            check(returned == 1234 && random.SequenceEqual(uncut.Skip((int)seek * 2).Take(2468)),
                "audio random seek reads exact cached source samples without scanning or remixing the timeline");
            var tail = Enumerable.Repeat((short)12345, 60).ToArray();
            check(mixer.ReadFrames(mixer.DurationFrames - 7, tail, 30) == 7 &&
                tail.Take(14).SequenceEqual(uncut.TakeLast(14)) && tail.Skip(14).All(sample => sample == 0) &&
                mixer.ReadFrames(mixer.DurationFrames, tail, 30) == 0 && tail.All(sample => sample == 0),
                "audio read reports precise EOF and clears unfilled playback frames");
            check(mixer.DurationFrames == AudioSampleClock.At(clip.End) &&
                uncut.Take((int)AudioSampleClock.At(clip.Start) * 2).All(sample => sample == 0),
                "audio sample clock retains leading timeline silence and fractional clip boundaries");
        }

        var pieces = new List<Clip>();
        var remainder = clip;
        foreach (var time in new[] { .2, .7, 1.123456789, 1.9, 2.713497 })
        {
            var cut = ClipEditor.Split(remainder, time, "cut-" + pieces.Count);
            pieces.Add(cut.Left);
            remainder = cut.Right;
        }
        pieces.Add(remainder);
        var warmLoads = 0;
        using (var split = await AudioTimelinePreview.PrepareAsync(project with { Clips = pieces },
            new ImmediateProgress(value => { if (value.Message.StartsWith("首次載入音訊")) warmLoads++; }), cache: cache))
        {
            check(uncut.SequenceEqual(ReadAll(split)),
                "realtime razor fragments preserve every PCM16 sample through six non-frame cut boundaries and existing fades");
            var partitioned = new short[uncut.Length];
            var buffer = new short[257 * 2];
            for (long position = 0; position < split.DurationFrames; position += 257)
            {
                var count = split.ReadFrames(position, buffer, 257);
                buffer.AsSpan(0, count * 2).CopyTo(partitioned.AsSpan((int)position * 2));
            }
            check(uncut.SequenceEqual(partitioned), "realtime mixing is independent of device buffer size and cut position");
        }
        check(coldLoads > 0 && warmLoads == 0, "editing cut structure reuses source PCM and starts without full mixed-file preparation");

        var mixed = project with
        {
            MediaAssets = [.. project.MediaAssets, new("other", other, MediaKind.Audio, 3.5)],
            Tracks = [.. project.Tracks, new("second", "Second", TrackKind.Audio, Volume: .64),
                new("mute", "Muted", TrackKind.Audio, Muted: true)],
            Clips = [clip, new("mix", "other", "second", .431113, .371457, 1.7843, Gain: .45,
                AudioFadeIn: new(.65432, FadeCurve.SmoothStep), AudioFadeOut: new(.43213, FadeCurve.EaseOut), SeparateAudioFades: true),
                new("muted", "other", "mute", 3.41, 0, .7),
                new("source-muted", "other", "second", 3.31, 0, .1, SourceAudioMuted: true)]
        };
        using (var mixer = await AudioTimelinePreview.PrepareAsync(mixed, cache: cache))
        {
            var samples = ReadAll(mixer);
            CheckNear(await Reference(mixed, "mixed-reference"), samples,
                "realtime multitrack gains, independent audio fades, gaps and mute match export", check);
            check(samples.Skip((int)AudioSampleClock.At(clip.End) * 2).All(value => value == 0) &&
                mixer.DurationFrames == AudioSampleClock.At(4.11), "muted tail retains project duration but emits no sound");
        }

        // Force cache refusal without large fixtures: fallback must preserve source phase,
        // own its files and remain bounded instead of dropping a quiet or oversized source.
        var refusedCache = new AudioPreviewCache(Path.Combine(folder, "refused-cache"), maximumBytes: 1, maximumEntryBytes: 1);
        var workFolder = Path.Combine(Environment.GetEnvironmentVariable("CUTMAKER_DATA_DIR") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CutMaker"), "audio-preview-work");
        var priorFiles = Directory.Exists(workFolder) ? Directory.EnumerateFiles(workFolder).ToHashSet() : [];
        using (var fallback = await AudioTimelinePreview.PrepareAsync(project, cache: refusedCache))
        {
            check(uncut.SequenceEqual(ReadAll(fallback)), "full or oversized source-cache fallback preserves exact audio instead of skipping tracks");
            check(Directory.EnumerateFiles(workFolder).Except(priorFiles).Count() == 1,
                "source-cache fallback owns only one bounded used-range temporary PCM");
        }
        check(!Directory.EnumerateFiles(workFolder).Except(priorFiles).Any(), "disposing fallback mixer removes its own temporary PCM");

        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            try
            {
                using var unexpected = await AudioTimelinePreview.PrepareAsync(project, cancellationToken: cancellation.Token, cache: cache);
                throw new InvalidOperationException("Canceled realtime source preparation unexpectedly completed.");
            }
            catch (OperationCanceledException) { }
            check(true, "canceled realtime preparation creates no playable stale timeline");
        }

        var disposed = await AudioTimelinePreview.PrepareAsync(project, cache: cache);
        disposed.Dispose();
        disposed.Dispose();
        try
        {
            disposed.ReadFrames(0, new short[2], 1);
            throw new InvalidOperationException("Disposed mixer unexpectedly read source audio.");
        }
        catch (ObjectDisposedException) { }
        check(true, "audio mixer disposal is idempotent and blocks further reads");
        check(sourceHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(source))), "realtime audio preparation leaves source media unchanged");

        var loud = project with
        {
            Tracks = [new("audio", "Loud", TrackKind.Audio, Volume: 4)],
            Clips = [clip with { Gain = 4, FadeIn = null, FadeOut = null }]
        };
        using (var ceiling = await AudioTimelinePreview.PrepareAsync(loud, cache: cache))
        {
            var samples = ReadAll(ceiling);
            var peak = samples.Max(value => Math.Abs((int)value));
            check(peak == 31130 && samples.Any(value => value == 31130) && samples.Any(value => value == -31130),
                "overloaded realtime mix applies deterministic .95 peak protection without PCM integer wraparound");
        }

        var busy = project with
        {
            Tracks = Enumerable.Range(0, 4).Select(index => new Track("track-" + index, "Track " + index, TrackKind.Audio)).ToList(),
            Clips = Enumerable.Range(0, 100).Select(index => new Clip("busy-" + index, "asset", "track-" + index % 4,
                index / 4 * .1, .01 + index / 4 * .123, .1, Gain: .38,
                FadeIn: new(.047, FadeCurve.Custom, .13, .61), FadeOut: new(.038, FadeCurve.EqualPower))).ToList()
        };
        using (var mixer = await AudioTimelinePreview.PrepareAsync(busy, cache: cache))
        {
            var block = new short[1920 * 2];
            mixer.ReadFrames(0, block, 1920); // Exclude the first JIT/array-pool initialization.
            var milliseconds = new List<double>();
            var nonzero = 0;
            for (var index = 0; index < 100; index++)
            {
                var first = index * 1920L % (mixer.DurationFrames - 1920);
                var start = Stopwatch.GetTimestamp();
                mixer.ReadFrames(first, block, 1920);
                milliseconds.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                if (block.Any(value => value != 0)) nonzero++;
            }
            milliseconds.Sort();
            check(nonzero == 100 && milliseconds[94] < 40,
                $"100 clips/four tracks mix 40ms device buffers within realtime budget; p95={milliseconds[94]:0.###}ms, max={milliseconds[^1]:0.###}ms");
        }

        var song = Path.Combine(folder, "four-minute-song.mp3");
        await Tool("-f", "lavfi", "-i", "sine=frequency=443:sample_rate=44100:duration=240", "-c:a", "libmp3lame", "-b:a", "192k", song);
        var songProject = project with
        {
            MediaAssets = [new("asset", song, MediaKind.Audio, 240)],
            Clips = [new("song", "asset", "audio", 0, 0, 240)]
        };
        var timer = Stopwatch.StartNew();
        using (var first = await AudioTimelinePreview.PrepareAsync(songProject, cache: cache))
            first.ReadFrames(0, new short[1920], 960);
        var firstMs = timer.ElapsedMilliseconds;
        timer.Restart();
        var editLoads = 0;
        using (var edited = await AudioTimelinePreview.PrepareAsync(songProject with
        {
            Clips = [songProject.Clips[0] with { SourceIn = 224.234568, Duration = 3.31, Gain = .47 }]
        }, new ImmediateProgress(value => { if (value.Message.StartsWith("首次載入音訊")) editLoads++; }), cache: cache))
            edited.ReadFrames(0, new short[1920], 960);
        check(editLoads == 0, $"four-minute audio uses source-only preparation; first={firstMs}ms, edited playback={timer.ElapsedMilliseconds}ms without mixed WAV");
    }

    private static short[] ReadAll(AudioTimelinePreview mixer)
    {
        var samples = new short[checked((int)mixer.DurationFrames * 2)];
        if (mixer.ReadFrames(0, samples, samples.Length / 2) != mixer.DurationFrames)
            throw new InvalidOperationException("Realtime timeline ended before its declared duration.");
        return samples;
    }

    private static void CheckNear(short[] reference, short[] actual, string message, Action<bool, string> check)
    {
        var maximum = 0;
        for (var index = 0; index < Math.Min(reference.Length, actual.Length); index++)
            maximum = Math.Max(maximum, Math.Abs(reference[index] - actual[index]));
        // Export stores float32 before this PCM16 decode, unlike direct double mixing.
        check(reference.Length == actual.Length && maximum <= 1,
            $"{message}; PCM16 difference={maximum}, samples={reference.Length}/{actual.Length}");
    }
}
