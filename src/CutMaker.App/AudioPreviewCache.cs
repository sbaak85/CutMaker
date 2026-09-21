using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using CutMaker.Core;

namespace CutMaker.App;

/// <summary>Whole-source PCM for preview only. Effects and edits are applied after this cache.</summary>
internal sealed class AudioPreviewCache(string directory, long maximumBytes = 2L * 1024 * 1024 * 1024,
    long maximumEntryBytes = 1024L * 1024 * 1024)
{
    internal const int BytesPerFrame = sizeof(double) * 2;
    // Keep decoding single-file-at-a-time, including cache instances used by verification.
    private static readonly SemaphoreSlim Gate = new(1, 1);
    internal static AudioPreviewCache Shared { get; } = new(Path.Combine(
        Environment.GetEnvironmentVariable("CUTMAKER_DATA_DIR") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CutMaker"), "audio-preview"));

    private long Budget => ReferenceEquals(this, Shared) ? EditorPreferences.Current.CacheBytes / 2 : maximumBytes;
    private long EntryBudget => ReferenceEquals(this, Shared) ? EditorPreferences.Current.CacheBytes / 2 : maximumEntryBytes;

    internal sealed class Lease(string path, FileStream reader, bool reused) : IDisposable
    {
        internal string Path { get; } = path;
        internal bool Reused { get; } = reused;
        // Deny deletion while FFmpeg is opening/reading this source, including other app windows.
        public void Dispose() => reader.Dispose();
    }

    internal async Task<Lease?> AcquireAsync(string sourcePath, double duration, string ffmpeg,
        IProgress<MediaRenderProgress>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var estimatedBytes = Math.Ceiling((duration + 1) * AudioSampleClock.Rate) * BytesPerFrame;
        if (!double.IsFinite(estimatedBytes) || estimatedBytes > EntryBudget) return null;
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(directory);
            var key = GetKey(sourcePath, ffmpeg);
            var target = Path.GetFullPath(Path.Combine(directory, key + ".f64"));
            Touch(target);
            if (TryOpen(target, reused: true) is { } ready)
            {
                return ready;
            }
            // Reserve enough room before decoding. Locked active files are never removed.
            if (!Prune(Budget - (long)estimatedBytes)) return null;
            var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                progress?.Report(new(0, $"首次載入音訊：{System.IO.Path.GetFileName(sourcePath)}…"));
                await MediaRenderService.RunToolAsync(ffmpeg,
                    ["-hide_banner", "-nostdin", "-y", "-v", "error", "-threads", "2", "-i", sourcePath,
                    "-map", "0:a:0", "-vn", "-af",
                    $"aresample={AudioSampleClock.Rate},aformat=sample_fmts=dblp:channel_layouts=stereo,asetpts=N/SR/TB",
                    "-c:a", "pcm_f64le", "-f", "f64le", "-fs", EntryBudget.ToString(CultureInfo.InvariantCulture),
                    "-progress", "pipe:1", "-nostats", temporary], cancellationToken, line =>
                    {
                        if (line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                            double.TryParse(line.AsSpan(12), NumberStyles.Float, CultureInfo.InvariantCulture, out var microseconds))
                            progress?.Report(new(Math.Clamp(microseconds / 1_000_000 / Math.Max(duration, .001), 0, .99),
                                $"首次載入音訊：{System.IO.Path.GetFileName(sourcePath)}…"));
                    }).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                var file = new FileInfo(temporary);
                // -fs can stop successfully at the size limit: never publish a truncated source.
                if (!file.Exists || file.Length == 0 || file.Length >= EntryBudget || file.Length % BytesPerFrame != 0)
                    return null;
                if (key != GetKey(sourcePath, ffmpeg))
                    throw new IOException("載入期間來源音訊已變更，請重新播放。");
                if (!Prune(Budget - file.Length)) return null;
                try { File.Move(temporary, target); }
                catch (IOException) when (File.Exists(target)) { } // Another window published the same source.
                var published = TryOpen(target, reused: false);
                if (ReferenceEquals(this, Shared)) ManagedMediaCache.Prune(EditorPreferences.Current.CacheBytes);
                return published;
            }
            finally { Delete(temporary); }
        }
        finally { Gate.Release(); }
    }

    internal Lease? TryAcquireExisting(string sourcePath, string ffmpeg) => TryOpen(Path.Combine(directory, GetKey(sourcePath, ffmpeg) + ".f64"), true);

    internal static string GetKey(string sourcePath, string ffmpeg)
    {
        var source = new FileInfo(sourcePath);
        if (!source.Exists) throw new FileNotFoundException("找不到音訊來源。", sourcePath);
        var decoder = new FileInfo(ffmpeg);
        var identity = string.Join("|", "audio-pcm-v1-f64-stereo-48000", source.FullName.ToUpperInvariant(),
            source.Length, source.LastWriteTimeUtc.Ticks, decoder.FullName.ToUpperInvariant(), decoder.Length, decoder.LastWriteTimeUtc.Ticks);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
    }

    internal static Lease? TryOpen(string path, bool reused)
    {
        try
        {
            var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (reader.Length > 0 && reader.Length % BytesPerFrame == 0) return new(path, reader, reused);
            reader.Dispose();
            Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }

    private bool Prune(long targetBytes)
    {
        var files = new DirectoryInfo(directory).EnumerateFiles("*.f64").OrderBy(file => file.LastWriteTimeUtc).ToArray();
        var total = files.Sum(file => file.Length);
        foreach (var file in files)
        {
            if (total <= targetBytes) break;
            var length = file.Length;
            if (Delete(file.FullName)) total -= length;
        }
        // Clean abandoned work from interrupted prior sessions, never a current decode.
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*.tmp")
            .Where(file => file.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1))) Delete(file.FullName);
        return total <= targetBytes;
    }

    private static void Touch(string path)
    {
        try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static bool Delete(string path)
    {
        try { File.Delete(path); return true; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
