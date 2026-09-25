using System.Globalization;
using System.IO;
using CutMaker.Core;

namespace CutMaker.App;

/// <summary>Pitch-preserving derived sources shared by playback/export and all slices of a source.</summary>
internal sealed class TempoPreparedProject : IDisposable
{
    private readonly List<ManagedMediaCache.Lease> _leases = [];
    internal CutProject Project { get; private set; }
    private TempoPreparedProject(CutProject project) => Project = project;
    public void Dispose() { foreach (var lease in _leases) lease.Dispose(); _leases.Clear(); }
    internal static async Task<TempoPreparedProject> PrepareAsync(CutProject project, string? projectPath,
        IProgress<MediaRenderProgress>? progress, CancellationToken token)
    {
        var result = new TempoPreparedProject(project);
        try
        {
            var assets = project.MediaAssets.ToDictionary(a => a.Id);
            var added = new List<MediaAsset>();
            var replacements = new Dictionary<string, Clip>();
            foreach (var group in project.Clips.Where(c => c.TimeRatio != 1).GroupBy(c => (c.AssetId, c.TimeRatio)))
            {
                token.ThrowIfCancellationRequested();
                var asset = assets[group.Key.AssetId];
                var path = projectPath is null ? Path.GetFullPath(asset.Path) : ProjectStore.ResolveAssetPath(projectPath, asset);
                var file = new FileInfo(path);
                if (!file.Exists) throw new FileNotFoundException("找不到變速音訊來源", path);
                var length = file.Length; var stamp = file.LastWriteTimeUtc;
                var ratio = group.Key.TimeRatio;
                var ffmpeg = MediaRenderService.FindTool("ffmpeg.exe");
                var decoder = new FileInfo(ffmpeg);
                var key = ManagedMediaCache.Hash(string.Join("\n", "tempo-v1", path.ToUpperInvariant(), length, stamp.Ticks,
                    ratio.ToString("R", CultureInfo.InvariantCulture), asset.Duration.ToString("R", CultureInfo.InvariantCulture), decoder.Length, decoder.LastWriteTimeUtc.Ticks));
                var duration = asset.Duration * ratio;
                progress?.Report(new(0, $"準備保留音高的變速音訊：{Path.GetFileName(path)} · {ratio * 100:0.##}%"));
                var lease = await ManagedMediaCache.GetAsync("audio-stretched", key, ".wav", async (output, cancellation) =>
                {
                    using var reservation = ManagedMediaCache.Reserve(checked((long)Math.Ceiling((duration + 1) * 48000) * 16 + 4096));
                    var rate = 1 / ratio;
                    var filters = new List<string> { "aresample=48000" };
                    while (rate > 2) { filters.Add("atempo=2"); rate /= 2; }
                    while (rate < .5) { filters.Add("atempo=0.5"); rate /= .5; }
                    filters.Add("atempo=" + rate.ToString("R", CultureInfo.InvariantCulture));
                    filters.Add("apad");
                    filters.Add("atrim=end_sample=" + AudioSampleClock.At(duration));
                    await MediaRenderService.RunToolAsync(ffmpeg, ["-hide_banner", "-nostdin", "-y", "-loglevel", "error",
                        "-i", path, "-map", "0:a:0", "-vn", "-af", string.Join(",", filters), "-ar", "48000", "-ac", "2",
                        "-c:a", "pcm_f64le", "-rf64", "auto", output], cancellation).ConfigureAwait(false);
                    file.Refresh();
                    if (!file.Exists || file.Length != length || file.LastWriteTimeUtc != stamp)
                        throw new IOException("變速處理期間來源已變動，請重新播放。");
                }, token).ConfigureAwait(false);
                result._leases.Add(lease);
                var id = "tempo-" + Guid.NewGuid().ToString("N");
                added.Add(new(id, lease.Path, MediaKind.Audio, duration));
                foreach (var clip in group)
                    replacements.Add(clip.Id, clip with { AssetId = id, SourceIn = clip.SourceIn * ratio, TimeRatio = 1 });
            }
            if (added.Count > 0)
                result.Project = project with { MediaAssets = [.. project.MediaAssets, .. added],
                    Clips = project.Clips.Select(c => replacements.GetValueOrDefault(c.Id, c)).ToList() };
            return result;
        }
        catch { result.Dispose(); throw; }
    }
}
