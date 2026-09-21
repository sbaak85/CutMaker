using System.Globalization;
using System.IO;
using CutMaker.Core;

namespace CutMaker.App;

/// <summary>Frame-aligned visual regions are cached independently; audio is rendered once across all seams.</summary>
internal static class VideoRegionPreview
{
    internal sealed record Result(int RenderedRegions, int ReusedRegions, int ProxySources);
    internal static async Task<Result> RenderAsync(CutProject project, string output, int width,
        bool proxies, IProgress<MediaRenderProgress>? progress, CancellationToken token)
    {
        var duration = project.Clips.Max(clip => clip.End);
        var fps = Math.Min(30, project.Video.Fps);
        width = Math.Min(width, project.Video.Width) / 2 * 2;
        var height = Math.Max(2, (int)Math.Round(project.Video.Height * (double)width / project.Video.Width) / 2 * 2);
        var framesPerRegion = Math.Max(1, (int)Math.Round(fps * 10));
        var frames = (long)Math.Ceiling(duration * fps - 1e-7);
        var count = (int)((frames + framesPerRegion - 1) / framesPerRegion);
        var leases = new List<ManagedMediaCache.Lease>();
        var proxyLeases = new List<ManagedMediaCache.Lease>();
        var work = Path.Combine(Path.GetDirectoryName(output)!, "regions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var rendered = 0; var reused = 0;
        var ffmpeg = MediaRenderService.FindTool("ffmpeg.exe");
        var decoder = new FileInfo(ffmpeg);
        var version = $"regions-v1|{decoder.Length}|{decoder.LastWriteTimeUtc.Ticks}|{width}|{height}|{fps:R}";
        var visualProject = project;
        try
        {
            if (proxies)
            {
                var assets = project.MediaAssets.ToList();
                var used = project.Clips.Select(clip => clip.AssetId).ToHashSet();
                for (var index = 0; index < assets.Count; index++)
                {
                    var asset = assets[index];
                    if (!used.Contains(asset.Id) || asset.Kind != MediaKind.Video) continue;
                    var source = new FileInfo(asset.Path);
                    // Small sources already decode cheaply; avoid creating an unnecessary second encode.
                    if (source.Length < 128L * 1024 * 1024) continue;
                    var key = ManagedMediaCache.Hash($"proxy-v1|{source.FullName.ToUpperInvariant()}|{source.Length}|{source.LastWriteTimeUtc.Ticks}|{version}");
                    progress?.Report(new(0, $"準備代理素材：{source.Name}…"));
                    var lease = await ManagedMediaCache.GetAsync("video-proxies", key, ".mp4", async (path, cancel) =>
                    {
                        await MediaRenderService.RunToolAsync(ffmpeg,
                            ["-hide_banner", "-nostdin", "-y", "-v", "error", "-threads", "2", "-i", source.FullName,
                            "-map", "0:v:0", "-an", "-vf", $"scale={width}:{height}:force_original_aspect_ratio=decrease:force_divisible_by=2,setsar=1",
                            "-fps_mode", "passthrough", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "20", "-pix_fmt", "yuv420p", "-threads", "2", path], cancel).ConfigureAwait(false);
                    }, token).ConfigureAwait(false);
                    proxyLeases.Add(lease);
                    assets[index] = asset with { Path = lease.Path };
                }
                visualProject = project with { MediaAssets = assets };
            }
            for (var index = 0; index < count; index++)
            {
                token.ThrowIfCancellationRequested();
                var start = index * (long)framesPerRegion / fps;
                var end = Math.Min(duration, (index + 1L) * framesPerRegion / fps);
                var key = RegionKey(project, start, end, version + "|" + proxies);
                var lease = await ManagedMediaCache.GetAsync("video-regions", key, ".mp4", async (path, cancel) =>
                {
                    await MediaRenderService.RenderAsync(visualProject, null, path,
                        new(Preview: true, Width: width, Height: height, Fps: fps, OutputStart: start, OutputEnd: end, VideoOnly: true),
                        cancellationToken: cancel).ConfigureAwait(false);
                }, token).ConfigureAwait(false);
                leases.Add(lease);
                if (lease.Reused) reused++; else rendered++;
                progress?.Report(new(.8 * (index + 1) / count, $"畫面區間 {index + 1}/{count} · 重用 {reused} 段"));
            }
            var audio = Path.Combine(work, "audio.wav");
            await MediaRenderService.RenderAsync(project, null, audio, new(Preview: true), cancellationToken: token).ConfigureAwait(false);
            var list = Path.Combine(work, "concat.txt");
            await File.WriteAllLinesAsync(list, leases.Select(lease => "file '" + lease.Path.Replace("\\", "/").Replace("'", "'\\''") + "'"), token).ConfigureAwait(false);
            var muxed = Path.Combine(work, "preview.mp4");
            progress?.Report(new(.9, "合併連續畫面與音訊…"));
            await MediaRenderService.RunToolAsync(ffmpeg,
                ["-hide_banner", "-nostdin", "-y", "-v", "error", "-f", "concat", "-safe", "0", "-i", list, "-i", audio,
                "-map", "0:v:0", "-map", "1:a:0", "-c:v", "copy", "-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-ac", "2",
                "-t", duration.ToString("G17", CultureInfo.InvariantCulture), "-movflags", "+faststart", muxed], token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            File.Move(muxed, output, true);
            progress?.Report(new(1, $"預覽完成 · 重算 {rendered} 段／重用 {reused} 段"));
            return new(rendered, reused, proxyLeases.Count);
        }
        finally
        {
            foreach (var lease in leases) lease.Dispose();
            foreach (var lease in proxyLeases) lease.Dispose();
            // This GUID directory is created and exclusively owned by this invocation.
            foreach (var name in new[] { "audio.wav", "concat.txt", "preview.mp4" })
                try { File.Delete(Path.Combine(work, name)); } catch (IOException) { }
            try { Directory.Delete(work); } catch (IOException) { }
            ManagedMediaCache.Prune(EditorPreferences.Current.CacheBytes);
        }
    }

    internal static string RegionKey(CutProject project, double start, double end, string settings)
    {
        var visualTracks = project.Tracks.Where(track => track.Kind == TrackKind.Video && !track.Muted).ToArray();
        var ids = visualTracks.Select(track => track.Id).ToHashSet();
        var assets = project.MediaAssets.ToDictionary(asset => asset.Id);
        var clips = new List<Clip>();
        foreach (var source in project.Clips.Where(clip => ids.Contains(clip.TrackId) && clip.Start < end && clip.End > start))
        {
            var clip = source;
            var image = assets[clip.AssetId].Kind == MediaKind.Image;
            if (assets[clip.AssetId].Kind == MediaKind.Audio) continue;
            if (clip.Start < start) clip = ClipEditor.Split(clip, start, clip.Id + "-region", image).Right;
            if (clip.End > end) clip = ClipEditor.Split(clip, end, clip.Id + "-tail", image).Left;
            clips.Add(clip with { Start = clip.Start - start, Gain = 1, SourceAudioMuted = true,
                AudioFadeIn = null, AudioFadeOut = null, SeparateAudioFades = false });
        }
        var subset = project with { Clips = clips, Tracks = visualTracks.Select(track => track with { Volume = 1 }).ToList() };
        return ManagedMediaCache.Hash($"{settings}|{end - start:R}|{PreviewRenderCache.CreateKey(subset)}");
    }
}
