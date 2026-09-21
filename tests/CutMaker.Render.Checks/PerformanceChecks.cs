using System.Diagnostics;
using System.Runtime.InteropServices;
using CutMaker.App;
using CutMaker.Core;

internal static class PerformanceChecks
{
    internal static async Task Run(string folder, Action<bool, string> check)
    {
        Directory.CreateDirectory(folder);
        var ffmpeg = MediaRenderService.FindTool("ffmpeg.exe");
        Task<string> Tool(params string[] args) => MediaRenderService.RunToolAsync(ffmpeg,
            new[] { "-hide_banner", "-nostdin", "-y", "-v", "error" }.Concat(args), CancellationToken.None);
        var video = Path.Combine(folder, "source.mp4"); var audio = Path.Combine(folder, "source.wav");
        await Tool("-f", "lavfi", "-i", "testsrc2=size=160x90:rate=30000/1001:duration=22.4", "-an", "-c:v", "libx264", "-threads", "2", video);
        await Tool("-f", "lavfi", "-i", "sine=frequency=419:sample_rate=44100:duration=22.4", "-c:a", "pcm_s16le", audio);
        var project = new CutProject
        {
            Video = new(160, 90, 30000.0 / 1001),
            MediaAssets = [new("v", video, MediaKind.Video, 22.4), new("a", audio, MediaKind.Audio, 22.4)],
            Tracks = [new("v", "V", TrackKind.Video), new("a", "A", TrackKind.Audio)],
            Clips = [new("v", "v", "v", 0, 0, 22.4, FadeOut: new(.5), SourceAudioMuted: true), new("a", "a", "a", 0, 0, 22.4)]
        };
        var clock = Stopwatch.StartNew();
        var first = Path.Combine(folder, "first.mp4");
        var cold = await VideoRegionPreview.RenderAsync(project, first, 160, false, null, CancellationToken.None);
        var coldMs = clock.ElapsedMilliseconds;
        check(cold.RenderedRegions == 3 && cold.ReusedRegions == 0, "video preview builds three frame-aligned regions at 29.97 fps");
        var edited = project with { Clips = [project.Clips[0] with { FadeOut = new(1) }, project.Clips[1]] };
        var changed = Path.Combine(folder, "changed.mp4"); clock.Restart();
        var update = await VideoRegionPreview.RenderAsync(edited, changed, 160, false, null, CancellationToken.None);
        check(update.RenderedRegions == 1 && update.ReusedRegions == 2,
            $"tail Fade edit reuses two untouched video regions; cold={coldMs}ms, edit={clock.ElapsedMilliseconds}ms");
        var repeated = await VideoRegionPreview.RenderAsync(edited, Path.Combine(folder, "repeated.mp4"), 160, false, null, CancellationToken.None);
        check(repeated.ReusedRegions == 3 && repeated.RenderedRegions == 0, "new preview invocation reuses persistent completed regions");
        var direct = Path.Combine(folder, "direct.mp4");
        await MediaRenderService.RenderAsync(edited, null, direct, new(Preview: true, Width: 160));
        async Task<byte[]> Frames(string path, string name)
        {
            var raw = Path.Combine(folder, name + ".rgb");
            await Tool("-i", path, "-an", "-vf", "select='eq(n,298)+eq(n,299)+eq(n,300)+eq(n,301)+eq(n,599)+eq(n,600)+eq(n,601)'",
                "-fps_mode", "passthrough", "-pix_fmt", "rgb24", "-f", "rawvideo", raw);
            return File.ReadAllBytes(raw);
        }
        var expectedFrames = await Frames(direct, "expected"); var actualFrames = await Frames(changed, "actual");
        var frameError = expectedFrames.Zip(actualFrames, (a, b) => Math.Abs(a - b)).Average();
        check(expectedFrames.Length == actualFrames.Length && frameError < 5,
            $"video samples on both sides of region boundaries match the continuous renderer; mean pixel difference={frameError:0.###}");
        async Task<float[]> Samples(string path, string name)
        {
            var raw = Path.Combine(folder, name + ".f32");
            await Tool("-i", path, "-vn", "-ac", "2", "-ar", "48000", "-f", "f32le", raw);
            return MemoryMarshal.Cast<byte, float>(File.ReadAllBytes(raw)).ToArray();
        }
        var expected = await Samples(direct, "expected-audio"); var actual = await Samples(changed, "actual-audio");
        var rms = Math.Sqrt(expected.Zip(actual, (a, b) => (double)(a - b) * (a - b)).Average());
        check(expected.Length == actual.Length && rms < .001, $"one continuous audio encode retains all samples across video regions; RMS difference={rms:G4}");

        var asset = project.MediaAssets[1];
        using (var progressive = await ProgressiveAudioSource.StartAsync(audio, ffmpeg, .25, 64L * 1024 * 1024, null, CancellationToken.None))
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!progressive.Complete && DateTime.UtcNow < deadline) await Task.Delay(10);
            check(progressive.Complete && progressive.ReadableFrames(0, 4096) == 4096, "progressive audio finishes with readable PCM and no partial-cache publication");
            using var cached = await AudioPreviewCache.Shared.AcquireAsync(audio, asset.Duration, ffmpeg, null, CancellationToken.None);
            check(cached is not null, "completed progressive decode is reusable by the ordinary source cache");
            var raw = Path.Combine(folder, "reference.f64");
            await Tool("-i", audio, "-af", "aresample=48000,aformat=sample_fmts=dblp:channel_layouts=stereo,asetpts=N/SR/TB", "-c:a", "pcm_f64le", "-f", "f64le", raw);
            check(File.ReadAllBytes(raw).SequenceEqual(File.ReadAllBytes(cached!.Path)), "progressive source PCM equals one uninterrupted decode byte for byte");
        }
        var audioProject = project with { MediaAssets = [asset], Tracks = [project.Tracks[1]], Clips = [project.Clips[1]] };
        using (var mixer = await AudioTimelinePreview.PrepareAsync(audioProject))
        {
            var before = new short[2048]; mixer.ReadFrames(48000, before, 1024);
            var muted = audioProject with { Tracks = [audioProject.Tracks[0] with { Muted = true }] };
            check(mixer.TryUpdateControls(muted, 48000), "live mute updates the prepared mixer without rebuilding sources");
            var after = new short[2048]; mixer.ReadFrames(48000, after, 1024);
            check(after[0] == before[0] && after.Skip(480).All(sample => sample == 0), "live mute ramps over 240 frames, then becomes exact silence");
            check(mixer.TryUpdateControls(audioProject, 50000), "live unmute reuses the same source handles");
            mixer.ReadFrames(51000, after, 1024);
            check(after.Any(sample => sample != 0), "unmuted source resumes after the short gain ramp");
            var sourceTime = File.GetLastWriteTimeUtc(audio);
            try
            {
                File.SetLastWriteTimeUtc(audio, sourceTime.AddSeconds(2));
                check(!mixer.TryUpdateControls(muted, 52000), "live controls reject a source changed since preparation");
            }
            finally { File.SetLastWriteTimeUtc(audio, sourceTime); }
        }
        var key = ManagedMediaCache.Hash("lease-check-" + Guid.NewGuid());
        var longAudio = Path.Combine(folder, "long-progressive.mp3");
        await Tool("-f", "lavfi", "-i", "sine=frequency=311:sample_rate=44100:duration=330", "-c:a", "libmp3lame", "-b:a", "128k", longAudio);
        var longProject = new CutProject { MediaAssets = [new("long", longAudio, MediaKind.Audio, 330)],
            Tracks = [new("a", "A", TrackKind.Audio)], Clips = [new("long", "long", "a", 0, 0, 330)] };
        clock.Restart();
        using (var longMixer = await AudioTimelinePreview.PrepareAsync(longProject))
        {
            var startup = clock.ElapsedMilliseconds;
            var buffer = new short[8192];
            check(longMixer.ReadFrames(0, buffer, 4096) == 4096 && buffer.Any(value => value != 0),
                $"330-second source begins through the progressive preparation path; startup={startup}ms");
            var deadline = DateTime.UtcNow.AddSeconds(20); var read = false;
            while (!read && DateTime.UtcNow < deadline)
            {
                try { read = longMixer.ReadFrames(329 * 48000L, buffer, 4096) == 4096; }
                catch (AudioBufferPendingException) { await Task.Delay(10); }
            }
            check(read && buffer.Any(value => value != 0), "late seek waits for real source data instead of substituting undecoded silence");
        }
        var proxySource = Path.Combine(folder, "large-proxy-source.mp4");
        File.Copy(video, proxySource, true);
        using (var padded = new FileStream(proxySource, FileMode.Open, FileAccess.Write)) padded.SetLength(129L * 1024 * 1024);
        var proxyProject = project with { MediaAssets = [project.MediaAssets[0] with { Path = proxySource }, asset] };
        var proxied = await VideoRegionPreview.RenderAsync(proxyProject, Path.Combine(folder, "proxy-preview.mp4"), 160, true, null, CancellationToken.None);
        check(proxied.ProxySources == 1, "opt-in proxy path creates a reusable visual proxy for a large source while keeping original audio");
        using (var hold = await ManagedMediaCache.GetAsync("video-regions", key, ".mp4", (path, token) => File.WriteAllBytesAsync(path, [1, 2, 3], token), CancellationToken.None))
        {
            ManagedMediaCache.Prune(0);
            check(File.Exists(hold.Path), "cache cleanup preserves a leased file while removing unleased generated media");
        }
        check(File.Exists(video) && File.Exists(audio), "cache cleanup leaves source media untouched");
        using (ManagedMediaCache.Reserve(EditorPreferences.Current.CacheBytes))
        {
            var blocked = false;
            try { using var excess = ManagedMediaCache.Reserve(1); }
            catch (IOException) { blocked = true; }
            check(blocked, "concurrent progressive reservations cannot exceed the cache budget");
        }
        using (ManagedMediaCache.Reserve(1)) check(true, "finished progressive reservations release their budget");
        using (MediaWorkScheduler.Foreground())
        {
            using var cancel = new CancellationTokenSource(100);
            var canceled = false;
            try { using var background = await MediaWorkScheduler.BackgroundAsync(cancel.Token); }
            catch (OperationCanceledException) { canceled = true; }
            check(canceled, "pending background overviews yield to foreground work and remain cancellable");
        }
    }
}
