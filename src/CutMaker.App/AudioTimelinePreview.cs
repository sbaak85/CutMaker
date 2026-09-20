using System.Buffers;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using CutMaker.Core;

namespace CutMaker.App;

/// <summary>
/// A seekable, bounded-memory audio timeline. Source PCM is decoded once; edits only change
/// this lightweight mix plan. ReadFrames never renders a complete mixed output file.
/// </summary>
internal sealed class AudioTimelinePreview : IDisposable
{
    private const long MaximumOwnedBytes = 2L * 1024 * 1024 * 1024;
    private const int BlockFrames = 4096;
    private readonly object _sync = new();
    private readonly Source[] _sources;
    private readonly Fragment[] _fragments;
    private bool _disposed;

    private sealed class Source(string path, long firstFrame, AudioPreviewCache.Lease? lease, bool owned) : IDisposable
    {
        internal FileStream Reader { get; } = new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1, options: FileOptions.RandomAccess);
        internal long FirstFrame { get; } = firstFrame;
        internal long Frames => Reader.Length / AudioPreviewCache.BytesPerFrame;

        public void Dispose()
        {
            Reader.Dispose();
            lease?.Dispose();
            if (owned) DeleteTemporary(path);
        }
    }

    private sealed record Fragment(Clip Clip, AudioSampleSlice Slice, double Gain, Source Source);

    private AudioTimelinePreview(long durationFrames, Source[] sources, Fragment[] fragments)
    {
        DurationFrames = durationFrames;
        _sources = sources;
        _fragments = fragments;
    }

    internal long DurationFrames { get; }

    internal static async Task<AudioTimelinePreview> PrepareAsync(CutProject absoluteSnapshot,
        IProgress<MediaRenderProgress>? progress = null, CancellationToken cancellationToken = default,
        AudioPreviewCache? cache = null)
    {
        // Own the lists even when a caller did not already make a snapshot.
        var project = absoluteSnapshot with
        {
            MediaAssets = [.. absoluteSnapshot.MediaAssets], Tracks = [.. absoluteSnapshot.Tracks],
            Clips = [.. absoluteSnapshot.Clips]
        };
        ProjectValidator.Validate(project);
        cancellationToken.ThrowIfCancellationRequested();
        var duration = project.Clips.Count == 0 ? 0 : project.Clips.Max(clip => clip.End);
        var assets = project.MediaAssets.ToDictionary(asset => asset.Id);
        var tracks = project.Tracks.ToDictionary(track => track.Id);
        var audible = project.Clips.Where(clip => !tracks[clip.TrackId].Muted && !clip.SourceAudioMuted &&
            clip.Gain > 0 && tracks[clip.TrackId].Volume > 0 && assets[clip.AssetId].Kind != MediaKind.Image).ToArray();
        var sources = new Dictionary<string, Source>(StringComparer.OrdinalIgnoreCase);
        var fragments = new List<Fragment>();
        long ownedBytes = 0;
        try
        {
            var ffmpeg = audible.Length == 0 ? string.Empty : MediaRenderService.FindTool("ffmpeg.exe");
            foreach (var group in audible.GroupBy(clip => Path.GetFullPath(assets[clip.AssetId].Path), StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var asset = assets[group.First().AssetId];
                if (asset.Kind == MediaKind.Video && !await HasAudioAsync(group.Key, cancellationToken).ConfigureAwait(false)) continue;
                var slices = group.Select(clip => (Clip: clip, Slice: AudioSampleClock.Slice(clip, 0, duration)))
                    .Where(item => item.Slice.Length > 0).ToArray();
                if (slices.Length == 0) continue;
                var lease = await (cache ?? AudioPreviewCache.Shared).AcquireAsync(group.Key, asset.Duration,
                    ffmpeg, progress, cancellationToken).ConfigureAwait(false);
                Source source;
                if (lease is not null)
                {
                    try { source = new Source(lease.Path, 0, lease, owned: false); }
                    catch { lease.Dispose(); throw; }
                }
                else
                {
                    // A busy/full shared cache must not silently omit this source. Keep only the
                    // required source span in a private, bounded temporary PCM file. Decode from
                    // zero before sample trimming so its resampler phase still matches export.
                    var first = slices.Min(item => item.Slice.SourceStart);
                    var end = slices.Max(item => item.Slice.SourceStart + item.Slice.Length);
                    var requiredBytes = checked((end - first) * AudioPreviewCache.BytesPerFrame);
                    if (requiredBytes > MaximumOwnedBytes - ownedBytes)
                        throw new IOException("即時音訊預覽所需的來源範圍超過 2 GiB 暫存上限，請縮短使用的來源範圍後再播放。");
                    source = await PrepareOwnedSourceAsync(group.Key, first, end, ffmpeg, progress, cancellationToken).ConfigureAwait(false);
                    ownedBytes += source.Reader.Length;
                }
                sources.Add(group.Key, source);
                foreach (var (clip, slice) in slices)
                    fragments.Add(new(clip, slice, clip.Gain * tracks[clip.TrackId].Volume, source));
            }
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new(1, "音訊已就緒；播放時即時混音。"));
            return new(AudioSampleClock.At(duration), sources.Values.ToArray(), fragments.ToArray());
        }
        catch
        {
            foreach (var source in sources.Values) source.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Fill interleaved stereo PCM16 at 48 kHz. Returns frames through timeline EOF; any
    /// remaining requested frames are zeroed. Random reads do not change a playback cursor.
    /// </summary>
    internal int ReadFrames(long startFrame, short[] output, int frameCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startFrame);
        ArgumentOutOfRangeException.ThrowIfNegative(frameCount);
        ArgumentNullException.ThrowIfNull(output);
        if (frameCount > output.Length / 2) throw new ArgumentException("The output buffer must hold stereo frames.", nameof(output));
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Array.Clear(output, 0, frameCount * 2);
            var available = (int)Math.Min(frameCount, Math.Max(0, DurationFrames - startFrame));
            if (available == 0) return 0;
            var mix = ArrayPool<double>.Shared.Rent(BlockFrames * 2);
            var sourceSamples = ArrayPool<double>.Shared.Rent(BlockFrames * 2);
            try
            {
                for (var outputFrame = 0; outputFrame < available; outputFrame += BlockFrames)
                {
                    var count = Math.Min(BlockFrames, available - outputFrame);
                    Array.Clear(mix, 0, count * 2);
                    var first = startFrame + outputFrame;
                    var end = first + count;
                    foreach (var fragment in _fragments)
                    {
                        var begin = Math.Max(first, fragment.Slice.TimelineStart);
                        var finish = Math.Min(end, fragment.Slice.TimelineStart + fragment.Slice.Length);
                        if (finish <= begin) continue;
                        var sourceFirst = fragment.Slice.SourceStart + begin - fragment.Slice.TimelineStart - fragment.Source.FirstFrame;
                        var sourceCount = (int)Math.Min(finish - begin, Math.Max(0, fragment.Source.Frames - sourceFirst));
                        if (sourceFirst < 0) throw new IOException("音訊預覽的來源範圍無效。");
                        if (sourceCount == 0) continue; // A codec can end slightly before its container duration.
                        ReadSource(fragment.Source.Reader, sourceFirst, sourceSamples.AsSpan(0, sourceCount * 2));
                        var target = (int)(begin - first) * 2;
                        for (var frame = 0; frame < sourceCount; frame++)
                        {
                            var localSeconds = (begin + frame) / (double)AudioSampleClock.Rate - fragment.Clip.Start;
                            var gain = fragment.Gain * FadeEnvelope.Gain(fragment.Clip, localSeconds, audio: true);
                            mix[target + frame * 2] += sourceSamples[frame * 2] * gain;
                            mix[target + frame * 2 + 1] += sourceSamples[frame * 2 + 1] * gain;
                        }
                    }
                    for (var sample = 0; sample < count * 2; sample++)
                    {
                        // Stateless peak protection makes arbitrary seek and block boundaries
                        // deterministic. Export retains its look-ahead limiter; overloaded mixes
                        // therefore sound different only while this .95 safety ceiling is hit.
                        var value = double.IsFinite(mix[sample]) ? Math.Clamp(mix[sample], -.95, .95) : 0;
                        output[outputFrame * 2 + sample] = (short)Math.Round(value * 32768, MidpointRounding.ToEven);
                    }
                }
            }
            finally
            {
                ArrayPool<double>.Shared.Return(mix);
                ArrayPool<double>.Shared.Return(sourceSamples);
            }
            return available;
        }
    }

    private static void ReadSource(FileStream reader, long startFrame, Span<double> samples)
    {
        var bytes = MemoryMarshal.AsBytes(samples);
        var offset = checked(startFrame * AudioPreviewCache.BytesPerFrame);
        while (!bytes.IsEmpty)
        {
            var read = RandomAccess.Read(reader.SafeFileHandle, bytes, offset);
            if (read == 0) throw new EndOfStreamException("來源音訊快取在播放期間提前結束。");
            bytes = bytes[read..];
            offset += read;
        }
    }

    private static async Task<bool> HasAudioAsync(string path, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var json = await MediaRenderService.RunToolAsync(MediaRenderService.FindTool("ffprobe.exe"),
                ["-v", "error", "-select_streams", "a:0", "-show_entries", "stream=index", "-of", "json", path], timeout.Token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            return document.RootElement.GetProperty("streams").GetArrayLength() != 0;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new IOException($"讀取音訊來源逾時：{Path.GetFileName(path)}"); }
    }

    private static async Task<Source> PrepareOwnedSourceAsync(string path, long first, long end, string ffmpeg,
        IProgress<MediaRenderProgress>? progress, CancellationToken cancellationToken)
    {
        var folder = Path.Combine(Environment.GetEnvironmentVariable("CUTMAKER_DATA_DIR") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CutMaker"), "audio-preview-work");
        Directory.CreateDirectory(folder);
        var output = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".f64");
        var before = new FileInfo(path);
        var identity = (before.Length, before.LastWriteTimeUtc);
        try
        {
            progress?.Report(new(0, $"準備音訊使用範圍：{Path.GetFileName(path)}…"));
            var decodeEnd = (end / (double)AudioSampleClock.Rate + 1).ToString("G17", CultureInfo.InvariantCulture);
            await MediaRenderService.RunToolAsync(ffmpeg,
                ["-hide_banner", "-nostdin", "-y", "-v", "error", "-threads", "2", "-t", decodeEnd, "-i", path,
                "-map", "0:a:0", "-vn", "-af",
                $"aresample={AudioSampleClock.Rate},aformat=sample_fmts=dblp:channel_layouts=stereo,atrim=start_sample={first}:end_sample={end},asetpts=N/SR/TB",
                "-c:a", "pcm_f64le", "-f", "f64le", output], cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var after = new FileInfo(path);
            if (identity != (after.Length, after.LastWriteTimeUtc))
                throw new IOException("載入期間來源音訊已變更，請重新播放。");
            var result = new FileInfo(output);
            if (!result.Exists || result.Length > (end - first) * AudioPreviewCache.BytesPerFrame || result.Length % AudioPreviewCache.BytesPerFrame != 0)
                throw new IOException("音訊暫存檔案不完整，請重新播放。");
            return new(output, first, null, owned: true);
        }
        catch { DeleteTemporary(output); throw; }
    }

    private static void DeleteTemporary(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var source in _sources) source.Dispose();
        }
    }
}
