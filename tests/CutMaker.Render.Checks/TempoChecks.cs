using System.Runtime.InteropServices;
using System.Security.Cryptography;
using CutMaker.App;
using CutMaker.Core;

internal static class TempoChecks
{
    internal static async Task Run(string folder, Action<bool,string> check)
    {
        Directory.CreateDirectory(folder);
        var source = Path.GetFullPath(Path.Combine(folder, "tone.wav"));
        var ffmpeg = MediaRenderService.FindTool("ffmpeg.exe");
        Task<string> Tool(params string[] args) => MediaRenderService.RunToolAsync(ffmpeg,
            new[] { "-hide_banner", "-nostdin", "-y", "-v", "error" }.Concat(args), CancellationToken.None);
        await Tool("-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=10", "-c:a", "pcm_s16le", source);
        var hash = SHA256.HashData(File.ReadAllBytes(source));
        var project = new CutProject { MediaAssets = [new("a", source, MediaKind.Audio, 10)], Tracks = [new("t", "T", TrackKind.Audio)],
            Clips = [new("c", "a", "t", 0, 0, 10)] };
        foreach (var ratio in new[] { .8, 1.2 })
        {
            var clip = ClipEditor.ChangeTimeRatio(project.Clips[0], ratio);
            var edited = project with { Clips = [clip], PlaybackRange = new(.5, 1) };
            using var mixer = await AudioTimelinePreview.PrepareAsync(edited);
            var samples = new short[checked((int)mixer.DurationFrames * 2)];
            mixer.ReadFrames(0, samples, samples.Length / 2);
            check(samples.Length / 2 == AudioSampleClock.At(10 * ratio), $"{ratio * 100}% preview has exact requested duration");
            var crossings = 0;
            for (var i = 48000; i < 96000; i++) if (samples[(i-1)*2] <= 0 && samples[i*2] > 0) crossings++;
            check(Math.Abs(crossings - 440) <= 2, $"{ratio * 100}% preserves 440 Hz pitch");
            var (left, right) = ClipEditor.Split(clip, 3.123456789, "r");
            using var split = await AudioTimelinePreview.PrepareAsync(edited with { Clips = [left, right] });
            var splitSamples = new short[samples.Length]; split.ReadFrames(0, splitSamples, splitSamples.Length/2);
            check(samples.SequenceEqual(splitSamples), $"{ratio * 100}% razor cut preserves every preview sample");
            var trimmed = ClipEditor.TrimStart(clip, 1, 10);
            using var trim = await AudioTimelinePreview.PrepareAsync(edited with { Clips = [trimmed] });
            var seek = new short[9600]; trim.ReadFrames(48000, seek, 4800);
            check(seek.SequenceEqual(samples.Skip(96000).Take(9600)), $"{ratio * 100}% trim maps to the original stretched source samples");
            var wave = Path.GetFullPath(Path.Combine(folder, $"export-{ratio}.wav"));
            await MediaRenderService.RenderAsync(edited, null, wave);
            var raw = wave + ".pcm"; await Tool("-i", wave, "-f", "s16le", "-c:a", "pcm_s16le", raw);
            var exported = MemoryMarshal.Cast<byte, short>(File.ReadAllBytes(raw)).ToArray();
            check(exported.Length == samples.Length && exported.Zip(samples).All(pair => Math.Abs(pair.First - pair.Second) <= 2),
                $"{ratio * 100}% export matches playback; playback range does not crop export");
        }
        check(SHA256.HashData(File.ReadAllBytes(source)).SequenceEqual(hash), "time stretching preserves source bytes");
        var fast = project with { Clips = [ClipEditor.ChangeTimeRatio(project.Clips[0], .8)] };
        using var prepared = await TempoPreparedProject.PrepareAsync(fast, null, null, CancellationToken.None);
        using var reused = await TempoPreparedProject.PrepareAsync(fast, null, null, CancellationToken.None);
        check(prepared.Project.MediaAssets.Last().Path == reused.Project.MediaAssets.Last().Path, "identical time ratio reuses derived audio cache");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        try { using var ignored = await TempoPreparedProject.PrepareAsync(fast, null, null, cancel.Token); check(false, "cancellation must throw"); }
        catch (OperationCanceledException) { check(true, "canceled stretch request never publishes a project"); }
    }
}
