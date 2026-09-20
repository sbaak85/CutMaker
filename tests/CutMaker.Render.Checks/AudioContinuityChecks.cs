using System.Globalization;
using System.Security.Cryptography;
using CutMaker.App;
using CutMaker.Core;

internal static class AudioContinuityChecks
{
    internal static async Task Run(string folder, Action<bool, string> check)
    {
        Directory.CreateDirectory(folder);
        var ffmpeg = MediaRenderService.FindTool("ffmpeg.exe");
        Task<string> RunTool(params string[] arguments) => MediaRenderService.RunToolAsync(ffmpeg,
            new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-y" }.Concat(arguments), CancellationToken.None);
        async Task<float[]> Decode(string path)
        {
            var raw = path + ".f32";
            await RunTool("-i", path, "-vn", "-ac", "2", "-ar", "48000", "-f", "f32le", raw);
            var bytes = File.ReadAllBytes(raw);
            var result = new float[bytes.Length / sizeof(float)];
            Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length);
            return result;
        }

        foreach (var rate in new[] { 44100, 48000 })
        {
            var wave = Path.Combine(folder, $"continuity-{rate}.wav");
            WriteSignal(wave, rate);
            var mp3 = Path.Combine(folder, $"continuity-{rate}.mp3");
            await RunTool("-i", wave, "-c:a", "libmp3lame", "-b:a", "320k", mp3);
            foreach (var source in new[] { wave, mp3 })
            {
                var hash = SHA256.HashData(File.ReadAllBytes(source));
                var original = new Clip("whole", "source", "audio", .071013, .123457, 2.43, Gain: .78,
                    FadeIn: new(.912341, FadeCurve.Custom, .13, .61), FadeOut: new(.634567, FadeCurve.EqualPower));
                var project = new CutProject
                {
                    Video = new(96, 54, 30), MediaAssets = [new("source", source, MediaKind.Audio, 3.1)],
                    Tracks = [new("audio", "音訊", TrackKind.Audio, Volume: .91)], Clips = [original]
                };
                var tail = original;
                var sliced = new List<Clip>();
                foreach (var offset in new[] { .037001, .347013, .777777, 1.231223, 1.910013, 2.10101 })
                {
                    var pair = ClipEditor.Split(tail, original.Start + offset, $"part-{sliced.Count}");
                    sliced.Add(pair.Left);
                    tail = pair.Right;
                }
                sliced.Add(tail);
                var split = project with { Clips = sliced };
                var sourceName = $"{rate}-{Path.GetExtension(source)[1..]}";
                foreach (var outputKind in new[] { "wav", "mp3", "mp4" })
                {
                    var options = new MediaRenderOptions(Preview: outputKind == "mp4", Width: 96, Height: 54, AudioBitrateKbps: 320);
                    var before = Path.Combine(folder, $"{sourceName}-before.{outputKind}");
                    var after = Path.Combine(folder, $"{sourceName}-after.{outputKind}");
                    await MediaRenderService.RenderAsync(project, null, before, options);
                    await MediaRenderService.RenderAsync(split, null, after, options);
                    Compare(await Decode(before), await Decode(after), $"{sourceName} → {outputKind}: six non-frame cuts preserve every decoded sample including custom fades", check);
                }
                {
                    var options = new MediaRenderOptions(OutputStart: .284619, OutputEnd: 2.291337);
                    var before = Path.Combine(folder, $"{sourceName}-range-before.wav");
                    var after = Path.Combine(folder, $"{sourceName}-range-after.wav");
                    await MediaRenderService.RenderAsync(project, null, before, options);
                    await MediaRenderService.RenderAsync(split, null, after, options);
                    Compare(await Decode(before), await Decode(after), $"{sourceName}: range beginning/ending inside fades retains all samples across cuts", check);
                }
                check(hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(source))), $"{sourceName}: continuity render preserves source bytes");
            }
        }
        var edgeSource = Path.Combine(folder, "continuity-44100.wav");
        foreach (var separateFades in new[] { false, true })
        {
            // Very short fragments straddle exact samples, half samples, and boundaries that
            // quantize to the same sample. All variants must partition the SAME source sequence.
            var original = new Clip("edge", "edge-source", "edge-track", .05001, .07132, 1.8,
                AudioFadeIn: separateFades ? new(1.3, FadeCurve.Custom, .72, .89) : null,
                AudioFadeOut: separateFades ? new(1.4, FadeCurve.SmoothStep) : null,
                SeparateAudioFades: separateFades);
            var project = new CutProject
            {
                Video = new(96, 54, 59.94), MediaAssets = [new("edge-source", edgeSource, MediaKind.Audio, 3.1)],
                Tracks = [new("edge-track", "Edge", TrackKind.Audio)], Clips = [original]
            };
            var pieces = new List<Clip>();
            var tail = original;
            foreach (var cut in new[] { .2, .2 + 1d / 48000, .2 + 1.5 / 48000, .2 + 1.6 / 48000, .7, 1.123456789, 1.83 })
            {
                var pair = ClipEditor.Split(tail, cut, $"edge-{pieces.Count}");
                pieces.Add(pair.Left);
                tail = pair.Right;
            }
            pieces.Add(tail);
            var split = project with { Clips = pieces };
            var before = Path.Combine(folder, $"sample-boundary-{separateFades}-before.wav");
            var after = Path.Combine(folder, $"sample-boundary-{separateFades}-after.wav");
            await MediaRenderService.RenderAsync(project, null, before);
            await MediaRenderService.RenderAsync(split, null, after);
            var full = await Decode(before);
            Compare(full, await Decode(after), $"59.94 fps / exact, half and sub-sample cuts: {(separateFades ? "overlapping independent audio fades" : "unedited waveform")}", check);

            var rangeStart = .1234567;
            var rangeEnd = 1.692468;
            var range = Path.Combine(folder, $"sample-boundary-{separateFades}-range.wav");
            await MediaRenderService.RenderAsync(split, null, range, new(OutputStart: rangeStart, OutputEnd: rangeEnd));
            var first = (int)AudioSampleClock.At(rangeStart) * 2;
            var count = (int)(AudioSampleClock.At(rangeEnd) - AudioSampleClock.At(rangeStart)) * 2;
            Compare(full.Skip(first).Take(count).ToArray(), await Decode(range), "range output matches the corresponding full-timeline samples", check);

            var muted = Path.Combine(folder, $"sample-boundary-{separateFades}-muted.wav");
            await MediaRenderService.RenderAsync(split with { Tracks = [split.Tracks[0] with { Muted = true }] }, null, muted);
            check((await Decode(muted)).All(sample => sample == 0), "muted audio track is silent across all split fragments");
        }
    }

    private static void Compare(float[] before, float[] after, string message, Action<bool, string> check)
    {
        var maximum = 0d;
        var maxIndex = 0;
        double squareSum = 0;
        for (var i = 0; i < Math.Min(before.Length, after.Length); i++)
        {
            var error = Math.Abs((double)before[i] - after[i]);
            squareSum += error * error;
            if (error > maximum) { maximum = error; maxIndex = i; }
        }
        var detail = string.Create(CultureInfo.InvariantCulture,
            $"{message}; max={maximum:G6}, rms={Math.Sqrt(squareSum / Math.Max(1, before.Length)):G6}, peakTime={maxIndex / 96000d:F6}s, samples={before.Length}/{after.Length}");
        check(before.Length == after.Length && maximum <= 2e-7, detail);
    }

    private static void WriteSignal(string path, int rate)
    {
        var frames = (int)(3.1 * rate);
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8.ToArray()); writer.Write(36 + frames * 4); writer.Write("WAVEfmt "u8.ToArray());
        writer.Write(16); writer.Write((short)1); writer.Write((short)2); writer.Write(rate); writer.Write(rate * 4);
        writer.Write((short)4); writer.Write((short)16); writer.Write("data"u8.ToArray()); writer.Write(frames * 4);
        uint state = 18231;
        for (var frame = 0; frame < frames; frame++)
        {
            var time = (double)frame / rate;
            state = unchecked(state * 1664525 + 1013904223);
            var noise = ((state >> 8) / 16777216d - .5) * .06;
            var left = .19 * Math.Sin(2 * Math.PI * (431.7 * time + 29.3 * time * time)) + noise + .027;
            var right = .17 * Math.Sin(2 * Math.PI * (917.1 * time + 13.7 * time * time)) - noise - .013;
            writer.Write((short)(left * short.MaxValue));
            writer.Write((short)(right * short.MaxValue));
        }
    }
}
