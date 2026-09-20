using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using CutMaker.App;
using CutMaker.Core;

internal static class AudioPreviewCacheChecks
{
    internal static async Task Run(string folder, Action<bool, string> check)
    {
        Directory.CreateDirectory(folder);
        var ffmpeg = MediaRenderService.FindTool("ffmpeg.exe");
        var ffprobe = MediaRenderService.FindTool("ffprobe.exe");
        Task<string> RunTool(params string[] arguments) => MediaRenderService.RunToolAsync(ffmpeg,
            new[] { "-hide_banner", "-nostdin", "-y", "-v", "error" }.Concat(arguments), CancellationToken.None);
        async Task<string> Signal(string name, int frequency, double duration = 3.1)
        {
            var path = Path.Combine(folder, name + ".wav");
            await RunTool("-f", "lavfi", "-i", $"sine=frequency={frequency}:sample_rate=44100:duration={duration}", "-c:a", "pcm_s16le", path);
            return path;
        }
        var source = await Signal("source", 419);
        var sourceHash = SHA256.HashData(File.ReadAllBytes(source));
        var cacheFolder = Path.Combine(folder, "decoded-" + Guid.NewGuid().ToString("N"));
        var cache = new AudioPreviewCache(cacheFolder);
        string cachedPath;
        using (var initial = await cache.AcquireAsync(source, 3.1, ffmpeg, null, CancellationToken.None))
        {
            check(initial is { Reused: false }, "first source preview decodes a complete PCM cache");
            cachedPath = initial!.Path;
            using var repeated = await cache.AcquireAsync(source, 3.1, ffmpeg, null, CancellationToken.None);
            check(repeated is { Reused: true } && repeated.Path == initial.Path, "repeated source preview reuses the same PCM file while another render holds it");
            var expected = Path.Combine(folder, "source-reference.f64");
            await RunTool("-i", source, "-map", "0:a:0", "-af", "aresample=48000,aformat=sample_fmts=dblp:channel_layouts=stereo,asetpts=N/SR/TB",
                "-c:a", "pcm_f64le", "-f", "f64le", expected);
            check(File.ReadAllBytes(initial.Path).SequenceEqual(File.ReadAllBytes(expected)), "cached 44.1 kHz source preserves every full-length 48 kHz stereo double PCM sample");
        }
        await Signal("source", 871);
        // Guarantee a distinct metadata identity even on file systems with coarse timestamps.
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddSeconds(2));
        using (var changed = await cache.AcquireAsync(source, 3.1, ffmpeg, null, CancellationToken.None))
            check(changed is { Reused: false } && changed.Path != cachedPath &&
                !File.ReadAllBytes(changed.Path).SequenceEqual(File.ReadAllBytes(cachedPath)),
                "same-path source replacement invalidates PCM cache and decodes the new audio");

        var cancellationFolder = Path.Combine(folder, "cancel-" + Guid.NewGuid().ToString("N"));
        using (var cancellation = new CancellationTokenSource())
        {
            var canceledDuringDecode = false;
            try
            {
                using var unexpected = await new AudioPreviewCache(cancellationFolder).AcquireAsync(source, 3.1, ffmpeg,
                    new ImmediateProgress(value => { if (value.Fraction > 0) { canceledDuringDecode = true; cancellation.Cancel(); } }), cancellation.Token);
                throw new InvalidOperationException("Source decoding should have been canceled");
            }
            catch (OperationCanceledException) { }
            check(canceledDuringDecode && !Directory.EnumerateFiles(cancellationFolder).Any(),
                "canceling a running source decode publishes no partial PCM and removes its temporary file");
        }

        var smallFolder = Path.Combine(folder, "bounded-" + Guid.NewGuid().ToString("N"));
        var bounded = new AudioPreviewCache(smallFolder, maximumBytes: 3_000_000, maximumEntryBytes: 2_000_000);
        var shortOne = await Signal("short-one", 200, 1);
        var shortTwo = await Signal("short-two", 400, 1);
        var shortThree = await Signal("short-three", 600, 1);
        using (var one = await bounded.AcquireAsync(shortOne, 1, ffmpeg, null, CancellationToken.None))
        using (var two = await bounded.AcquireAsync(shortTwo, 1, ffmpeg, null, CancellationToken.None))
        using (var three = await bounded.AcquireAsync(shortThree, 1, ffmpeg, null, CancellationToken.None))
            check(one is not null && two is not null && three is null && File.Exists(one.Path) && File.Exists(two.Path),
                "cache budget falls back instead of evicting PCM held by active preview renders");
        using (var three = await bounded.AcquireAsync(shortThree, 1, ffmpeg, null, CancellationToken.None))
            check(three is not null && Directory.EnumerateFiles(smallFolder, "*.f64").Sum(path => new FileInfo(path).Length) <= 3_000_000,
                "unused PCM is evicted in least-recently-used order to respect the disk budget");
        using (var oversized = await bounded.AcquireAsync(source, 3.1, ffmpeg, null, CancellationToken.None))
            check(oversized is null, "oversized sources use direct rendering without exceeding the per-source cache limit");

        var mp3Source = Path.Combine(folder, "mono-source.mp3");
        await RunTool("-i", source, "-c:a", "libmp3lame", "-b:a", "192k", mp3Source);
        var changedSourceHash = SHA256.HashData(File.ReadAllBytes(source));
        var mp3Hash = SHA256.HashData(File.ReadAllBytes(mp3Source));
        var clip = new Clip("whole", "source", "audio", .071013, .123457, 2.43, Gain: .78,
            FadeIn: new(.912341, FadeCurve.Custom, .13, .61), FadeOut: new(.634567, FadeCurve.EqualPower));
        var project = new CutProject
        {
            MediaAssets = [new("source", mp3Source, MediaKind.Audio, 3.1)],
            Tracks = [new("audio", "Audio", TrackKind.Audio, Volume: .91)], Clips = [clip]
        };
        var reference = Path.Combine(folder, "reference.wav");
        var preview = Path.Combine(folder, "preview.wav");
        await MediaRenderService.RenderAsync(project, null, reference);
        await MediaRenderService.RenderAsync(project, null, preview, new(Preview: true));
        async Task<byte[]> Decode16(string path)
        {
            var pcm = path + ".s16";
            await RunTool("-i", path, "-c:a", "pcm_s16le", "-f", "s16le", pcm);
            return File.ReadAllBytes(pcm);
        }
        var referencePcm = await Decode16(reference);
        var previewPcm = await Decode16(preview);
        CheckPcm16(referencePcm, previewPcm, "cached mono MP3 preview matches direct source render", check);
        using (var metadata = JsonDocument.Parse(await MediaRenderService.RunToolAsync(ffprobe,
            ["-v", "error", "-show_entries", "stream=codec_name,sample_rate,channels", "-of", "json", preview], CancellationToken.None)))
        {
            var stream = metadata.RootElement.GetProperty("streams")[0];
            check(stream.GetProperty("codec_name").GetString() == "pcm_s16le" && stream.GetProperty("sample_rate").GetString() == "48000" &&
                stream.GetProperty("channels").GetInt32() == 2, "audio-only preview is Windows-compatible 48 kHz stereo PCM without a video encoder");
        }
        var tail = clip;
        var pieces = new List<Clip>();
        foreach (var time in new[] { .2, .7, 1.123456789, 1.9 })
        {
            var split = ClipEditor.Split(tail, time, "cut-" + pieces.Count);
            pieces.Add(split.Left);
            tail = split.Right;
        }
        pieces.Add(tail);
        var splitPreview = Path.Combine(folder, "preview-split.wav");
        await MediaRenderService.RenderAsync(project with { Clips = pieces }, null, splitPreview, new(Preview: true));
        var splitPcm = await Decode16(splitPreview);
        check(previewPcm.SequenceEqual(splitPcm),
            "reused source PCM retains exact audio continuity through cuts inside fades");
        check(!sourceHash.SequenceEqual(changedSourceHash) && changedSourceHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(source))) &&
            mp3Hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(mp3Source))) && Directory.EnumerateFiles(cacheFolder, "*.f64").Count() == 2,
            "source version caches coexist safely without overwriting original media");

        var song = Path.Combine(folder, "four-minute-song.mp3");
        await RunTool("-f", "lavfi", "-i", "sine=frequency=443:sample_rate=44100:duration=240", "-c:a", "libmp3lame", "-b:a", "192k", song);
        var songProject = project with
        {
            MediaAssets = [new("source", song, MediaKind.Audio, 240)],
            Clips = [clip with { Start = 0, SourceIn = 220.123457, Duration = 7.77 }]
        };
        var initialLoads = 0;
        var repeatedLoads = 0;
        var timer = Stopwatch.StartNew();
        await MediaRenderService.RenderAsync(songProject, null, Path.Combine(folder, "song-preview.wav"), new(Preview: true),
            new ImmediateProgress(value => { if (value.Message.StartsWith("首次載入音訊")) initialLoads++; }));
        var firstMilliseconds = timer.ElapsedMilliseconds;
        timer.Restart();
        var editedSong = songProject with { Clips = [songProject.Clips[0] with { SourceIn = 224.234568, Duration = 3.31, Gain = .47 }] };
        var songEdited = Path.Combine(folder, "song-edited.wav");
        await MediaRenderService.RenderAsync(editedSong, null, songEdited, new(Preview: true),
            new ImmediateProgress(value => { if (value.Message.StartsWith("首次載入音訊")) repeatedLoads++; }));
        var repeatedMilliseconds = timer.ElapsedMilliseconds;
        check(initialLoads > 0 && repeatedLoads == 0,
            $"four-minute MP3 is decoded once and a late trim/gain edit reuses PCM; first={firstMilliseconds}ms, edit={repeatedMilliseconds}ms");
        var songDirect = Path.Combine(folder, "song-direct.wav");
        await MediaRenderService.RenderAsync(editedSong, null, songDirect);
        var songPcm = await Decode16(songEdited);
        var songDirectPcm = await Decode16(songDirect);
        CheckPcm16(songPcm, songDirectPcm, "exact PCM byte seeking near the end of a four-minute song preserves source sample phase", check);
    }

    private static void CheckPcm16(byte[] reference, byte[] actual, string message, Action<bool, string> check)
    {
        var maximum = 0;
        for (var offset = 0; offset < Math.Min(reference.Length, actual.Length); offset += sizeof(short))
            maximum = Math.Max(maximum, Math.Abs((int)BitConverter.ToInt16(reference, offset) - BitConverter.ToInt16(actual, offset)));
        // The reference is first stored as float32, then quantized to PCM16; a rounding
        // boundary can move by one integer step versus direct double → PCM16 preview.
        check(reference.Length == actual.Length && maximum <= 1,
            $"{message}; PCM16 difference={maximum}, bytes={reference.Length}/{actual.Length}");
    }
}
