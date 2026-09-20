using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using CutMaker.Core;

namespace CutMaker.App;

public enum ExportVideoQuality { Compact, Balanced, High }
public sealed record MediaRenderOptions(bool Preview = false, int? Width = null, int? Height = null, double? Fps = null,
    ExportVideoQuality VideoQuality = ExportVideoQuality.Balanced, int AudioBitrateKbps = 192,
    double? OutputStart = null, double? OutputEnd = null, bool FrameOnly = false);
public sealed record MediaRenderProgress(double Fraction, string Message);
public sealed record MediaRenderResult(string OutputPath, double Duration, int Width, int Height);

/// <summary>One render graph for preview and export; inputs are opened read-only and output is replaced only on success.</summary>
public static partial class MediaRenderService
{
    private static string N(double value) => value.ToString("0.#########", CultureInfo.InvariantCulture);

    public static async Task<MediaRenderResult> RenderAsync(CutProject project, string? projectPath, string outputPath,
        MediaRenderOptions? options = null, IProgress<MediaRenderProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // The UI may continue editing while this immutable record/list snapshot is rendered.
        project = project with { MediaAssets = [.. project.MediaAssets], Tracks = [.. project.Tracks], Clips = [.. project.Clips] };
        ProjectValidator.Validate(project);
        options ??= new();
        if (project.Clips.Count == 0) throw new InvalidOperationException("時間軸沒有片段可匯出，請先將素材放入軌道。");
        var output = Path.GetFullPath(outputPath);
        var mp3 = string.Equals(Path.GetExtension(output), ".mp3", StringComparison.OrdinalIgnoreCase);
        // Lossless PCM is also used by sample-accurate audio verification; the export dialog
        // continues to offer the ordinary MP4/MP3 delivery formats.
        var wave = string.Equals(Path.GetExtension(output), ".wav", StringComparison.OrdinalIgnoreCase);
        var audioOnly = mp3 || wave;
        if (options.FrameOnly ? !string.Equals(Path.GetExtension(output), ".png", StringComparison.OrdinalIgnoreCase) :
            !audioOnly && !string.Equals(Path.GetExtension(output), ".mp4", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("請選擇 .mp4、.mp3 或 .wav 輸出檔名。");
        var assets = project.MediaAssets.ToDictionary(asset => asset.Id);
        var paths = assets.ToDictionary(pair => pair.Key, pair => projectPath is null
            ? Path.GetFullPath(pair.Value.Path) : ProjectStore.ResolveAssetPath(projectPath, pair.Value));
        if (paths.Values.Any(path => string.Equals(output, path, StringComparison.OrdinalIgnoreCase)) ||
            (projectPath is not null && string.Equals(output, Path.GetFullPath(projectPath), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("匯出檔案不能覆蓋來源素材或目前專案，請另選檔名。");
        var timelineDuration = project.Clips.Max(clip => clip.End);
        var rangeStart = options.OutputStart ?? 0;
        var rangeEnd = options.OutputEnd ?? timelineDuration;
        if (!double.IsFinite(rangeStart) || !double.IsFinite(rangeEnd) || rangeStart < 0 || rangeEnd > timelineDuration + 1e-7 || rangeEnd <= rangeStart)
            throw new InvalidOperationException("匯出區間必須在時間軸內，且結束秒數必須大於起點。");
        if (!Enum.IsDefined(options.VideoQuality) || options.AudioBitrateKbps is not (128 or 192 or 256 or 320))
            throw new InvalidOperationException("請選擇有效的畫質與音訊位元率。");
        var duration = rangeEnd - rangeStart;
        var tracks = project.Tracks.ToDictionary(track => track.Id);
        // Decode only intersecting sources. Local fade time still includes the trimmed portion.
        var clips = project.Clips.Where(clip => !tracks[clip.TrackId].Muted && clip.Start < rangeEnd && clip.End > rangeStart).ToList();
        var ffmpeg = FindTool("ffmpeg.exe");
        var ffprobe = FindTool("ffprobe.exe");
        var streams = new Dictionary<string, (bool Video, bool Audio)>();
        progress?.Report(new(0, "檢查素材…"));
        foreach (var assetId in clips.Select(clip => clip.AssetId).Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = paths[assetId];
            if (!File.Exists(path)) throw new FileNotFoundException($"找不到素材：{path}", path);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            string json;
            try
            {
                json = await RunToolAsync(ffprobe, ["-v", "error", "-show_entries", "stream=codec_type", "-of", "json", path], timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { throw new IOException($"讀取素材逾時：{Path.GetFileName(path)}"); }
            using var document = JsonDocument.Parse(json);
            var types = document.RootElement.GetProperty("streams").EnumerateArray()
                .Select(stream => stream.GetProperty("codec_type").GetString()).ToArray();
            streams[assetId] = (types.Contains("video"), types.Contains("audio"));
            var kind = assets[assetId].Kind;
            if ((kind == MediaKind.Audio && !streams[assetId].Audio) || (kind != MediaKind.Audio && !streams[assetId].Video))
                throw new IOException($"素材沒有可讀取的{(kind == MediaKind.Audio ? "音訊" : "畫面")}：{Path.GetFileName(path)}");
        }

        var width = options.Width ?? (options.Preview ? Math.Min(960, project.Video.Width) : project.Video.Width);
        var height = options.Height ?? (options.Preview ? (int)Math.Round(project.Video.Height * (double)width / project.Video.Width) : project.Video.Height);
        if (width < 2 || height < 2) throw new InvalidOperationException("輸出畫面尺寸必須至少為 2 × 2。");
        width = Math.Max(2, width / 2 * 2);
        height = Math.Max(2, height / 2 * 2);
        var fps = options.Fps ?? (options.Preview ? Math.Min(30, project.Video.Fps) : project.Video.Fps);
        if (width > 16384 || height > 16384 || !double.IsFinite(fps) || fps <= 0 || fps > 240)
            throw new InvalidOperationException("輸出畫面尺寸或影格率無效。");

        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var id = Guid.NewGuid().ToString("N");
        var temporary = Path.Combine(Path.GetDirectoryName(output)!, $".cutmaker-{id}{Path.GetExtension(output)}");
        var filterPath = Path.Combine(Path.GetDirectoryName(output)!, $".cutmaker-{id}.filters.txt");
        var cachedAudio = new Dictionary<string, AudioPreviewCache.Lease>();
        try
        {
            if (options.Preview && !options.FrameOnly)
            {
                foreach (var assetId in clips.Where(clip => !clip.SourceAudioMuted && streams[clip.AssetId].Audio &&
                    assets[clip.AssetId].Kind != MediaKind.Image).Select(clip => clip.AssetId).Distinct())
                {
                    var cached = await AudioPreviewCache.Shared.AcquireAsync(paths[assetId], assets[assetId].Duration,
                        ffmpeg, progress, cancellationToken).ConfigureAwait(false);
                    if (cached is not null) cachedAudio.Add(assetId, cached);
                }
            }
            var arguments = new List<string> { "-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-filter_complex_threads", "2" };
            var graph = new List<string>();
            var visualLabels = new List<(Clip Clip, int TrackIndex, string Label)>();
            var inputCount = 0;
            // A finite black/silence canvas retains timeline gaps and the tails of muted tracks.
            if (!audioOnly) graph.Add($"color=c=black:s={width}x{height}:r={N(fps)}:d={N(duration)},format=yuv420p[canvas]");
            for (var i = 0; i < clips.Count; i++)
            {
                var clip = clips[i];
                var asset = assets[clip.AssetId];
                if (audioOnly || tracks[clip.TrackId].Kind != TrackKind.Video || !streams[clip.AssetId].Video) continue;
                var clipOffset = Math.Max(rangeStart - clip.Start, 0);
                var segment = clip with { Start = Math.Max(clip.Start - rangeStart, 0), SourceIn = clip.SourceIn + clipOffset,
                    Duration = Math.Min(clip.End, rangeEnd) - Math.Max(clip.Start, rangeStart) };
                // Bound decoder concurrency per input instead of multiplying CPU count by every clip.
                arguments.AddRange(["-threads", "2"]);
                if (asset.Kind == MediaKind.Image)
                    arguments.AddRange(["-loop", "1", "-framerate", N(fps), "-t", N(segment.Duration), "-i", paths[clip.AssetId]]);
                else
                    arguments.AddRange(["-ss", N(segment.SourceIn), "-t", N(segment.Duration), "-i", paths[clip.AssetId]]);
                var input = inputCount++;

                {
                    var filter = $"[{input}:v:0]trim=duration={N(segment.Duration)},setpts=PTS-STARTPTS,fps={N(fps)}," +
                        $"scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1,format=yuva420p";
                    if ((clip.FadeIn?.Duration ?? 0) > 0 || (clip.FadeOut?.Duration ?? 0) > 0)
                    {
                        var fade = FadeEnvelope.GainExpression(clip, $"(T+{N(clipOffset)})");
                        filter += clip.VideoFadeMode == VideoFadeMode.Black
                            ? $",geq=lum='16+(lum(X,Y)-16)*({fade})':cb='128+(cb(X,Y)-128)*({fade})':cr='128+(cr(X,Y)-128)*({fade})':a='alpha(X,Y)'"
                            : $",geq=lum='lum(X,Y)':cb='cb(X,Y)':cr='cr(X,Y)':a='alpha(X,Y)*({fade})'";
                    }
                    filter += $",setpts=PTS+{N(segment.Start)}/TB[v{i}]";
                    graph.Add(filter);
                    visualLabels.Add((segment, project.Tracks.FindIndex(track => track.Id == clip.TrackId), $"[v{i}]"));
                }
            }
            if (!audioOnly)
            {
                var current = "[canvas]";
                var layer = 0;
                foreach (var visual in visualLabels.OrderByDescending(item => item.TrackIndex).ThenBy(item => item.Clip.Start))
                {
                    var next = $"[layer{layer++}]";
                    graph.Add($"{current}{visual.Label}overlay=eof_action=pass:repeatlast=0:shortest=0:enable='gte(t,{N(visual.Clip.Start)})*lt(t,{N(visual.Clip.End)})'{next}");
                    current = next;
                }
                graph.Add($"{current}format=yuv420p[vout]");
            }
            if (!options.FrameOnly)
                AddAudioGraph(arguments, graph, clips, paths, tracks, streams, assets, cachedAudio, rangeStart, rangeEnd, ref inputCount);
            await File.WriteAllTextAsync(filterPath, string.Join(";\n", graph), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            arguments.AddRange(["-/filter_complex", filterPath]);
            if (options.FrameOnly) arguments.AddRange(["-map", "[vout]", "-frames:v", "1", "-c:v", "png", "-update", "1", "-threads", "2"]);
            else
            {
                var crf = options.VideoQuality switch { ExportVideoQuality.Compact => "26", ExportVideoQuality.High => "17", _ => "20" };
                if (!audioOnly) arguments.AddRange(["-map", "[vout]", "-c:v", "libx264", "-preset", options.Preview ? "ultrafast" : "veryfast", "-crf", options.Preview ? "25" : crf, "-pix_fmt", "yuv420p", "-movflags", "+faststart", "-threads", "4"]);
                arguments.AddRange(["-map", "[aout]", "-c:a", wave ? options.Preview ? "pcm_s16le" : "pcm_f32le" : mp3 ? "libmp3lame" : "aac"]);
                if (!wave) arguments.AddRange(["-b:a", $"{options.AudioBitrateKbps}k"]);
                var audioDuration = (AudioSampleClock.At(rangeEnd) - AudioSampleClock.At(rangeStart)) / (double)AudioSampleClock.Rate;
                arguments.AddRange(["-ar", "48000", "-ac", "2", "-t", Exact(audioDuration)]);
            }
            arguments.AddRange(["-progress", "pipe:1", "-nostats", temporary]);
            progress?.Report(new(0, options.Preview ? "正在準備預覽…" : "正在匯出…"));
            await RunToolAsync(ffmpeg, arguments, cancellationToken, line =>
            {
                if (line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                    double.TryParse(line.AsSpan(12), NumberStyles.Float, CultureInfo.InvariantCulture, out var us))
                    progress?.Report(new(Math.Clamp(us / 1_000_000 / duration, 0, 0.99), options.Preview ? "正在準備預覽…" : "正在匯出…"));
            }).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(temporary) || new FileInfo(temporary).Length == 0) throw new IOException("媒體引擎沒有產生有效輸出。");
            if (File.Exists(output)) File.Replace(temporary, output, null);
            else File.Move(temporary, output);
            progress?.Report(new(1, options.Preview ? "預覽已就緒" : "匯出完成"));
            return new(output, duration, width, height);
        }
        finally
        {
            foreach (var cached in cachedAudio.Values) cached.Dispose();
            DeleteTemporary(temporary);
            DeleteTemporary(filterPath);
        }
    }

    public static string FindTool(string fileName)
    {
        var configured = Environment.GetEnvironmentVariable("CUTMAKER_FFMPEG_DIR");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(Path.Combine(configured, fileName)))
            return Path.GetFullPath(Path.Combine(configured, fileName));
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            for (var folder = new DirectoryInfo(start); folder is not null; folder = folder.Parent)
            {
                var candidate = Path.Combine(folder.FullName, ".tools", "ffmpeg", "bin", fileName);
                if (File.Exists(candidate)) return candidate;
            }
        }
        throw new FileNotFoundException("尚未找到專案用 FFmpeg。請先執行 scripts\\setup-ffmpeg.ps1。", fileName);
    }

    internal static async Task<string> RunToolAsync(string executable, IEnumerable<string> arguments, CancellationToken cancellationToken,
        Action<string>? onOutput = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = info };
        if (!process.Start()) throw new IOException("無法啟動媒體引擎。");
        using var registration = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        });
        var errors = new ConcurrentQueue<string>();
        var errorTask = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                errors.Enqueue(line.Length > 1000 ? line[..1000] : line);
                while (errors.Count > 16) errors.TryDequeue(out _);
            }
        });
        var output = new StringBuilder();
        var outputTask = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                onOutput?.Invoke(line);
                if (onOutput is null && output.Length < 1_000_000) output.AppendLine(line);
            }
        });
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        await Task.WhenAll(errorTask, outputTask).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (process.ExitCode != 0) throw new IOException($"媒體引擎無法完成處理（{process.ExitCode}）。\n{string.Join("\n", errors)}");
        return output.ToString();
    }

    private static void DeleteTemporary(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
