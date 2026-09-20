using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using CutMaker.App;
using CutMaker.Core;

try
{
var folder = Path.GetFullPath(args.FirstOrDefault() ?? Path.Combine("runtime", "render-checks"));
Directory.CreateDirectory(folder);
if (args.Contains("--audio-only"))
{
    await AudioContinuityChecks.Run(Path.Combine(folder, "audio-continuity"), (condition, message) =>
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS: " + message);
    });
    await AudioPreviewCacheChecks.Run(Path.Combine(folder, "audio-preview-cache"), (condition, message) =>
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS: " + message);
    });
    return;
}
var ffmpeg = MediaRenderService.FindTool("ffmpeg.exe");
var ffprobe = MediaRenderService.FindTool("ffprobe.exe");
var notes = new List<string>();
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    notes.Add("PASS: " + message);
    Console.WriteLine(notes[^1]);
}
Task<string> Run(params string[] values) => MediaRenderService.RunToolAsync(ffmpeg,
    new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-y" }.Concat(values), CancellationToken.None);
PreviewRenderCacheChecks.Run(Path.Combine(folder, "preview-cache"), Check);
var colorPath = Path.Combine(folder, "原片 [保留]; ' 雙色與音訊.mp4");
var bluePath = Path.Combine(folder, "blue-no-audio.mp4");
var tonePath = Path.Combine(folder, "tone-880.wav");
await Run("-f", "lavfi", "-i", "color=red:s=160x90:r=30:d=1", "-f", "lavfi", "-i", "color=green:s=160x90:r=30:d=2",
    "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=3", "-filter_complex", "[0:v][1:v]concat=n=2:v=1:a=0[v]",
    "-map", "[v]", "-map", "2:a", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k", colorPath);
await Run("-f", "lavfi", "-i", "color=blue:s=160x90:r=30:d=2", "-c:v", "libx264", "-pix_fmt", "yuv420p", bluePath);
WriteWave(tonePath, 2, 880, 0.3);
var hashes = new[] { colorPath, bluePath, tonePath }.ToDictionary(path => path, path => SHA256.HashData(File.ReadAllBytes(path)));
var project = new CutProject
{
    Title = "Export integration", Video = new(160, 90, 30),
    MediaAssets = [new("color", colorPath, MediaKind.Video, 3), new("blue", bluePath, MediaKind.Video, 2), new("tone", tonePath, MediaKind.Audio, 2)],
    Tracks = [new("upper", "Upper", TrackKind.Video, Volume: 0.8), new("lower", "Lower", TrackKind.Video),
        new("tone", "Tone", TrackKind.Audio, Volume: 0.6), new("muted", "Muted", TrackKind.Video, Muted: true)],
    Clips = [new("upper", "color", "upper", 0.4, 1, 1.4, Gain: 0.8, FadeIn: new(0.4, FadeCurve.EaseIn), FadeOut: new(0.4, FadeCurve.EqualPower)),
        new("lower", "blue", "lower", 0, 0, 2), new("tone", "tone", "tone", 0.2, 0.2, 1.5, Gain: 0.5),
        new("muted", "color", "muted", 2.2, 0, 0.4)]
};
var projectPath = Path.Combine(folder, "export-roundtrip.cutmaker");
ProjectStore.Save(projectPath, project with { MediaAssets = project.MediaAssets.Select(asset => asset with { Path = Path.GetFileName(asset.Path) }).ToList() });
project = ProjectStore.Load(projectPath);
var mp4 = Path.Combine(folder, "integration.mp4");
var mp3 = Path.Combine(folder, "integration.mp3");
var progressValues = new List<double>();
await MediaRenderService.RenderAsync(project, projectPath, mp4, progress: new ImmediateProgress(value => progressValues.Add(value.Fraction)));
await MediaRenderService.RenderAsync(project, projectPath, mp3);
Check(progressValues.Count > 1 && progressValues[^1] == 1 && progressValues.All(value => value is >= 0 and <= 1), "render reports bounded progress and completion");
using (var meta = JsonDocument.Parse(await MediaRenderService.RunToolAsync(ffprobe, ["-v", "error", "-show_streams", "-show_format", "-of", "json", mp4], CancellationToken.None)))
{
    var streams = meta.RootElement.GetProperty("streams").EnumerateArray().ToArray();
    Check(streams.Any(stream => stream.GetProperty("codec_name").GetString() == "h264" && stream.GetProperty("width").GetInt32() == 160 && stream.GetProperty("height").GetInt32() == 90), "MP4 contains H.264 at project size");
    Check(streams.Any(stream => stream.GetProperty("codec_name").GetString() == "aac" && stream.GetProperty("channels").GetInt32() == 2), "MP4 contains stereo AAC");
    Check(Math.Abs(double.Parse(meta.RootElement.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture) - 2.6) < 0.1, "MP4 keeps timeline duration including muted tail and gaps");
}
using (var meta = JsonDocument.Parse(await MediaRenderService.RunToolAsync(ffprobe, ["-v", "error", "-show_streams", "-show_format", "-of", "json", mp3], CancellationToken.None)))
{
    var streams = meta.RootElement.GetProperty("streams").EnumerateArray().ToArray();
    Check(streams.Length == 1 && streams[0].GetProperty("codec_name").GetString() == "mp3", "MP3 output contains audio only");
    Check(Math.Abs(double.Parse(meta.RootElement.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture) - 2.6) < 0.1, "MP3 duration matches timeline within encoder padding");
}
async Task<byte[]> Pixel(double time, string? source = null)
{
    var raw = Path.Combine(folder, "pixel.rgb");
    await Run("-ss", time.ToString(CultureInfo.InvariantCulture), "-i", source ?? mp4, "-frames:v", "1", "-vf", "crop=2:2:iw/2:ih/2", "-pix_fmt", "rgb24", "-f", "rawvideo", raw);
    return File.ReadAllBytes(raw);
}
var blue = await Pixel(0.1);
Check(blue[2] > 220 && blue[0] < 20 && blue[1] < 20, "lower video appears before upper clip");
var green = await Pixel(1);
Check(green[1] > 100 && green[0] < 20 && green[2] < 20, "source trim skips red section and first track composites on top");
var fade = await Pixel(0.6);
Check(fade[1] > 15 && fade[1] < 55 && fade[2] > 140 && fade[2] < 225, "ease-in visual alpha reveals lower track at expected fraction");
var afterUpper = await Pixel(1.9);
Check(afterUpper[2] > 220 && afterUpper[1] < 20, "upper video does not freeze past its clip end");
var gap = await Pixel(2.4);
Check(gap.Take(3).All(channel => channel < 8), "muted video and timeline gap render black");
async Task<float[]> DecodeAudio(string path)
{
    var pcm = path + ".f32";
    await Run("-i", path, "-vn", "-ac", "1", "-ar", "48000", "-f", "f32le", pcm);
    var bytes = File.ReadAllBytes(pcm);
    var result = new float[bytes.Length / 4];
    Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length);
    return result;
}
foreach (var path in new[] { mp4, mp3 })
{
    var pcm = await DecodeAudio(path);
    var videoTone = Amplitude(pcm, 1, 0.1, 440);
    var audioTone = Amplitude(pcm, 1, 0.1, 880);
    Check(videoTone is > 0.055 and < 0.10 && audioTone is > 0.065 and < 0.115, $"{Path.GetExtension(path)} mixes native video audio and audio track with clip/track gain");
    Check(Amplitude(pcm, 0.5, 0.05, 440) < videoTone * 0.3, $"{Path.GetExtension(path)} fade-in changes audio envelope");
    Check(Rms(pcm, 2.3, 0.1) < 0.003, $"{Path.GetExtension(path)} muted track and end gap are silent");
}
var preview = Path.Combine(folder, "preview.mp4");
await MediaRenderService.RenderAsync(project, projectPath, preview, new(Preview: true, Width: 320, Height: 180));
Check(File.Exists(preview) && new FileInfo(preview).Length > 1000, "preview uses the same renderer with smaller encoding options");
var stillPath = Path.Combine(folder, "still.png");
await Run("-f", "lavfi", "-i", "color=yellow:s=160x90", "-frames:v", "1", stillPath);
var stillProject = new CutProject
{
    Title = "Still image", Video = new(160, 90, 30), MediaAssets = [new("still", stillPath, MediaKind.Image, 5)],
    Tracks = [new("video", "Video", TrackKind.Video)], Clips = [new("still", "still", "video", 0, 0, 0.5)]
};
var stillOutput = Path.Combine(folder, "still.mp4");
await MediaRenderService.RenderAsync(stillProject, null, stillOutput);
Check((await DecodeAudio(stillOutput)).All(sample => Math.Abs(sample) < 0.003), "still image loops across its clip with a valid silent audio stream");
var curves = Enum.GetValues<FadeCurve>();
var curveProject = new CutProject
{
    Title = "Curve checks", MediaAssets = [new("tone", tonePath, MediaKind.Audio, 2)], Tracks = [new("audio", "Audio", TrackKind.Audio)],
    Clips = curves.Select((curve, index) => new Clip($"curve-{index}", "tone", "audio", index, 0, 1,
        FadeIn: new(0.4, curve), FadeOut: new(0.4, curve))).ToList()
};
var curvePath = Path.Combine(folder, "curves.mp3");
await MediaRenderService.RenderAsync(curveProject, null, curvePath);
var curvePcm = await DecodeAudio(curvePath);
for (var i = 0; i < curves.Length; i++)
{
    var midpoint = curves[i] switch { FadeCurve.EaseIn => 0.25, FadeCurve.EaseOut => 0.75, FadeCurve.EqualPower => Math.Sqrt(0.5), _ => 0.5 };
    Check(Math.Abs(Amplitude(curvePcm, i + 0.19, 0.02, 880) - 0.3 * midpoint) < 0.03 &&
        Math.Abs(Amplitude(curvePcm, i + 0.79, 0.02, 880) - 0.3 * midpoint) < 0.03,
        $"{curves[i]} fade-in and fade-out use the configured curve");
}
var oldOutput = Path.Combine(folder, "existing.mp4");
var sentinel = "existing output must survive"u8.ToArray();
File.WriteAllBytes(oldOutput, sentinel);
var rangePath = Path.Combine(folder, "range.mp4");
var rangeResult = await MediaRenderService.RenderAsync(project, projectPath, rangePath,
    new(Width: 108, Height: 192, Fps: 24, VideoQuality: ExportVideoQuality.High, AudioBitrateKbps: 256, OutputStart: 0.6, OutputEnd: 1.6));
Check(Math.Abs(rangeResult.Duration - 1) < 1e-9 && project.Video == new VideoSettings(160, 90, 30), "range export reports exact selected duration without changing the source project");
using (var meta = JsonDocument.Parse(await MediaRenderService.RunToolAsync(ffprobe, ["-v", "error", "-show_streams", "-show_format", "-of", "json", rangePath], CancellationToken.None)))
{
    var video = meta.RootElement.GetProperty("streams").EnumerateArray().First(stream => stream.GetProperty("codec_type").GetString() == "video");
    Check(video.GetProperty("width").GetInt32() == 108 && video.GetProperty("height").GetInt32() == 192 && video.GetProperty("r_frame_rate").GetString() == "24/1", "portrait dimensions and selected frame rate reach the MP4 encoder");
    Check(Math.Abs(double.Parse(meta.RootElement.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture) - 1) < .05, "range output contains one second instead of the full timeline");
}
var rangeFirst = await Pixel(0, rangePath);
Check(Math.Abs(rangeFirst[1] - fade[1]) < 15 && Math.Abs(rangeFirst[2] - fade[2]) < 15, "range start preserves source trim, layer order and in-progress fade instead of restarting it");
var rangeAudio = await DecodeAudio(rangePath);
Check(Amplitude(rangeAudio, .4, .1, 880) is > .06 and < .12, "range audio is shifted to local output time with its original gain");
var highBitratePath = Path.Combine(folder, "range-320.mp3");
await MediaRenderService.RenderAsync(project, projectPath, highBitratePath, new(AudioBitrateKbps: 320, OutputStart: .4, OutputEnd: 1.4));
using (var meta = JsonDocument.Parse(await MediaRenderService.RunToolAsync(ffprobe, ["-v", "error", "-show_streams", "-of", "json", highBitratePath], CancellationToken.None)))
    Check(meta.RootElement.GetProperty("streams")[0].GetProperty("bit_rate").GetString() == "320000", "MP3 uses the chosen 320 kbps bitrate");
var framePath = Path.Combine(folder, "seek-frame.png");
await MediaRenderService.RenderAsync(project, projectPath, framePath, new(Preview: true, Width: 160, Height: 90, OutputStart: .6, OutputEnd: .7, FrameOnly: true));
var framePixel = await Pixel(0, framePath);
Check(Math.Abs(framePixel[1] - fade[1]) < 10 && Math.Abs(framePixel[2] - fade[2]) < 10, "single-frame seek renders current multitrack composition and fade without encoding the whole project");
var blackPath = Path.Combine(folder, "fade-black.mp4");
var blackProject = project with { Clips = project.Clips.Select(clip => clip.Id == "upper" ? clip with
    { VideoFadeMode = VideoFadeMode.Black, SeparateAudioFades = true, AudioFadeIn = null, AudioFadeOut = null } : clip).ToList() };
await MediaRenderService.RenderAsync(blackProject, projectPath, blackPath);
var blackFade = await Pixel(.6, blackPath);
Check(blackFade[1] is > 15 and < 55 && blackFade[2] < 15, "independent fade-to-black darkens foreground without revealing the lower blue track");
var blackAudio = await DecodeAudio(blackPath);
Check(Amplitude(blackAudio, .5, .05, 440) > .05, "separate audio envelope can keep sound unchanged during the visual fade");
var detachedPath = Path.Combine(folder, "detached-audio.mp3");
await MediaRenderService.RenderAsync(project with { Clips = project.Clips.Select(clip => clip.Id == "upper" ? clip with { SourceAudioMuted = true, Gain = 4 } : clip).ToList() }, projectPath, detachedPath);
var detachedAudio = await DecodeAudio(detachedPath);
Check(Amplitude(detachedAudio, 1, .1, 440) < .003 && Amplitude(detachedAudio, 1, .1, 880) > .065,
    "muted source audio stays silent after video gain changes while independent audio remains audible");
var customClip = new Clip("custom", "tone", "audio", 0, 0, 2, FadeIn: new(1, FadeCurve.Custom, .1, .4), FadeOut: new(.5, FadeCurve.Custom, .8, .9));
var (customLeft, customRight) = ClipEditor.Split(customClip, .3, "custom-right");
var customProject = new CutProject { MediaAssets = [new("tone", tonePath, MediaKind.Audio, 2)], Tracks = [new("audio", "Audio", TrackKind.Audio)], Clips = [customLeft, customRight] };
var customPath = Path.Combine(folder, "custom-split.mp3");
await MediaRenderService.RenderAsync(customProject, null, customPath);
var customPcm = await DecodeAudio(customPath);
Check(Math.Abs(Amplitude(customPcm, .54, .02, 880) - .3 * FadeEnvelope.Gain(customClip, .55, true)) < .015,
    "custom Bezier envelope survives a razor cut inside the fade and renders the preserved curve interval");
foreach (var invalid in new[] { new MediaRenderOptions(OutputStart: 1, OutputEnd: 1), new MediaRenderOptions(OutputStart: -.1), new MediaRenderOptions(OutputEnd: 3), new MediaRenderOptions(AudioBitrateKbps: 17) })
{
    try { await MediaRenderService.RenderAsync(project, projectPath, oldOutput, invalid); throw new Exception("Expected invalid output settings rejection"); }
    catch (InvalidOperationException error) when (error.Message.Contains("區間") || error.Message.Contains("位元率")) { }
}
Check(File.ReadAllBytes(oldOutput).SequenceEqual(sentinel), "invalid ranges and bitrate fail before replacing any existing destination");
var missing = project with { MediaAssets = project.MediaAssets.Select(asset => asset.Id == "blue" ? asset with { Path = "missing.mp4" } : asset).ToList() };
try { await MediaRenderService.RenderAsync(missing, projectPath, oldOutput); throw new Exception("Expected missing input rejection"); }
catch (FileNotFoundException) { }
Check(File.ReadAllBytes(oldOutput).SequenceEqual(sentinel), "missing input preserves existing destination");
try { await MediaRenderService.RenderAsync(project, projectPath, colorPath); throw new Exception("Expected source overwrite rejection"); }
catch (InvalidOperationException error) when (error.Message.Contains("覆蓋")) { }
Check(hashes.All(pair => SHA256.HashData(File.ReadAllBytes(pair.Key)).SequenceEqual(pair.Value)), "source overwrite blocked and all original hashes unchanged");
using (var cancellation = new CancellationTokenSource())
{
    var longProject = project with
    {
        MediaAssets = [.. project.MediaAssets, new("long-muted", "unused.missing.mp4", MediaKind.Video, 60)],
        Clips = [.. project.Clips, new("long-muted", "long-muted", "muted", 0, 0, 60)]
    };
    var canceledDuringRender = false;
    try
    {
        await MediaRenderService.RenderAsync(longProject, projectPath, oldOutput, new(Width: 1920, Height: 1080),
            new ImmediateProgress(value => { if (value.Fraction > 0 && value.Fraction < 1) { canceledDuringRender = true; cancellation.Cancel(); } }), cancellation.Token);
        throw new Exception("Expected cancellation");
    }
    catch (OperationCanceledException) { }
    Check(canceledDuringRender && File.ReadAllBytes(oldOutput).SequenceEqual(sentinel), "in-progress cancellation stops encoder and preserves existing output");
    Check(!Directory.EnumerateFiles(folder, ".cutmaker-*").Any(), "render temporary media and filter files cleaned after cancellation");
}
Check(hashes.All(pair => SHA256.HashData(File.ReadAllBytes(pair.Key)).SequenceEqual(pair.Value)), "source hashes remain unchanged after canceled render");
await AudioContinuityChecks.Run(Path.Combine(folder, "audio-continuity"), Check);
await AudioPreviewCacheChecks.Run(Path.Combine(folder, "audio-preview-cache"), Check);
await File.WriteAllLinesAsync(Path.Combine(folder, "render-result.txt"), notes);
Console.WriteLine($"{notes.Count}/{notes.Count} render integration checks passed. Artifacts: {folder}");

static void WriteWave(string path, double duration, double frequency, double amplitude)
{
    var samples = (int)(duration * 48000);
    using var writer = new BinaryWriter(File.Create(path));
    writer.Write("RIFF"u8.ToArray()); writer.Write(36 + samples * 2); writer.Write("WAVEfmt "u8.ToArray());
    writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(48000); writer.Write(96000);
    writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8.ToArray()); writer.Write(samples * 2);
    for (var i = 0; i < samples; i++) writer.Write((short)(Math.Sin(i * 2 * Math.PI * frequency / 48000) * amplitude * short.MaxValue));
}
static double Amplitude(float[] samples, double start, double duration, double frequency)
{
    var offset = (int)(start * 48000);
    var count = Math.Min((int)(duration * 48000), samples.Length - offset);
    double real = 0, imaginary = 0;
    for (var i = 0; i < count; i++) { real += samples[offset + i] * Math.Cos(i * 2 * Math.PI * frequency / 48000); imaginary += samples[offset + i] * Math.Sin(i * 2 * Math.PI * frequency / 48000); }
    return Math.Sqrt(real * real + imaginary * imaginary) * 2 / count;
}
static double Rms(float[] samples, double start, double duration)
{
    var offset = (int)(start * 48000);
    var count = Math.Min((int)(duration * 48000), samples.Length - offset);
    return Math.Sqrt(samples.Skip(offset).Take(count).Sum(value => value * value) / count);
}
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    Environment.ExitCode = 1;
}
sealed class ImmediateProgress(Action<MediaRenderProgress> report) : IProgress<MediaRenderProgress>
{
    public void Report(MediaRenderProgress value) => report(value);
}
