using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Media.Imaging;

namespace CutMaker.App;

/// <summary>
/// Per-source preview frames, independent of the timeline. Every frame is an independently
/// decodable JPEG at a constant rate from source time zero, so moving, trimming, cutting or
/// fading a clip never invalidates it. Source media is opened read-only.
/// </summary>
internal sealed class VideoFrameStore : IDisposable
{
    private static readonly byte[] Magic = "CMFRAME1"u8.ToArray();
    private const int TrailerBytes = 32;
    private readonly object _sync = new();
    private readonly FileStream _reader;
    private readonly List<long> _ends = [];
    private bool _disposed;

    private VideoFrameStore(string path, FileStream reader, double fps, bool complete)
    { Path = path; _reader = reader; Fps = fps; Complete = complete; }

    internal string Path { get; }
    internal double Fps { get; }
    internal bool Complete { get; }
    internal int Count { get { lock (_sync) return _ends.Count; } }

    internal static VideoFrameStore? OpenComplete(string path)
    {
        FileStream? reader = null;
        try
        {
            try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            // Denying write/delete sharing is the lease that keeps cache cleanup away from an open store.
            reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.RandomAccess);
            var trailer = new byte[TrailerBytes];
            if (reader.Length < TrailerBytes || RandomAccess.Read(reader.SafeFileHandle, trailer, reader.Length - TrailerBytes) != TrailerBytes ||
                !trailer.AsSpan(24, 8).SequenceEqual(Magic)) { reader.Dispose(); return null; }
            var count = BinaryPrimitives.ReadInt32LittleEndian(trailer);
            var fps = BinaryPrimitives.ReadDoubleLittleEndian(trailer.AsSpan(8));
            var table = BinaryPrimitives.ReadInt64LittleEndian(trailer.AsSpan(16));
            if (count <= 0 || !double.IsFinite(fps) || fps <= 0 || table <= 0 || table + (long)count * 8 + TrailerBytes != reader.Length)
            { reader.Dispose(); return null; }
            var bytes = new byte[count * 8];
            var read = 0;
            while (read < bytes.Length)
            {
                var part = RandomAccess.Read(reader.SafeFileHandle, bytes.AsSpan(read), table + read);
                if (part == 0) { reader.Dispose(); return null; }
                read += part;
            }
            var store = new VideoFrameStore(path, reader, fps, complete: true);
            long previous = 0;
            for (var index = 0; index < count; index++)
            {
                var end = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(index * 8));
                if (end <= previous || end > table) { reader.Dispose(); return null; }
                store._ends.Add(end); previous = end;
            }
            return store;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { reader?.Dispose(); return null; }
    }

    internal static VideoFrameStore OpenBuilding(string path, double fps) =>
        new(path, new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.RandomAccess), fps, complete: false);

    internal void Publish(long end) { lock (_sync) _ends.Add(end); }

    internal static void WriteTrailer(FileStream writer, IReadOnlyList<long> ends, double fps)
    {
        var table = writer.Position;
        var bytes = new byte[ends.Count * 8 + TrailerBytes];
        for (var index = 0; index < ends.Count; index++) BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(index * 8), ends[index]);
        var trailer = bytes.AsSpan(ends.Count * 8);
        BinaryPrimitives.WriteInt32LittleEndian(trailer, ends.Count);
        BinaryPrimitives.WriteDoubleLittleEndian(trailer[8..], fps);
        BinaryPrimitives.WriteInt64LittleEndian(trailer[16..], table);
        Magic.CopyTo(trailer[24..]);
        writer.Write(bytes);
    }

    /// <summary>Thread-safe; returns a frozen bitmap, or null when that frame is not stored (yet).</summary>
    internal BitmapSource? Decode(int index)
    {
        byte[] data;
        lock (_sync)
        {
            if (_disposed || index < 0 || index >= _ends.Count) return null;
            var start = index == 0 ? 0 : _ends[index - 1];
            data = new byte[checked((int)(_ends[index] - start))];
            var read = 0;
            try
            {
                while (read < data.Length)
                {
                    var part = RandomAccess.Read(_reader.SafeFileHandle, data.AsSpan(read), start + read);
                    if (part == 0) return null;
                    read += part;
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { return null; }
        }
        try
        {
            using var stream = new MemoryStream(data, writable: false);
            var frame = new JpegBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            frame.Freeze();
            return frame;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or IOException or ArgumentException or
            System.Runtime.InteropServices.COMException or InvalidOperationException) { return null; }
    }

    public void Dispose() { lock (_sync) { if (_disposed) return; _disposed = true; _reader.Dispose(); } }
}

internal static class VideoFrameCache
{
    internal const string Folder = "video-frames";
    private static readonly ConcurrentDictionary<string, VideoFrameStore> Stores = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Task> Builds = new(StringComparer.Ordinal);
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static int _version;

    /// <summary>Changes whenever a store appears, grows into a complete one or is discarded.</summary>
    internal static int Version => Volatile.Read(ref _version);
    internal static event Action? Changed;
    private static string Directory_ => Path.Combine(EditorPreferences.Root, Folder);

    internal static string? Key(string sourcePath, int size)
    {
        try
        {
            var source = new FileInfo(sourcePath);
            if (!source.Exists) return null;
            var decoder = new FileInfo(MediaRenderService.FindTool("ffmpeg.exe"));
            return ManagedMediaCache.Hash(string.Join("|", "frames-v1", source.FullName.ToUpperInvariant(), source.Length,
                source.LastWriteTimeUtc.Ticks, size, decoder.Length, decoder.LastWriteTimeUtc.Ticks));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException) { return null; }
    }

    /// <summary>A complete store, or one still being written whose finished frames are already usable.</summary>
    internal static VideoFrameStore? TryGet(string sourcePath, int size)
    {
        var key = Key(sourcePath, size);
        if (key is null) return null;
        if (Stores.TryGetValue(key, out var store)) return store;
        var opened = VideoFrameStore.OpenComplete(Path.Combine(Directory_, key + ".cmf"));
        if (opened is null) return null;
        if (Stores.TryAdd(key, opened)) return opened;
        opened.Dispose();
        return Stores.TryGetValue(key, out store) ? store : null;
    }

    internal static bool IsComplete(string sourcePath, int size) => TryGet(sourcePath, size) is { Complete: true };

    /// <summary>Builds the store once; concurrent callers for the same source share the work.</summary>
    internal static Task EnsureAsync(string sourcePath, double duration, int size, IProgress<double>? progress, CancellationToken token)
    {
        if (IsComplete(sourcePath, size)) { progress?.Report(1); return Task.CompletedTask; }
        var key = Key(sourcePath, size) ?? throw new FileNotFoundException($"找不到素材：{sourcePath}", sourcePath);
        lock (Builds)
        {
            if (Builds.TryGetValue(key, out var running) && !running.IsCompleted) return running.WaitAsync(token);
            var task = Task.Run(() => BuildAsync(key, sourcePath, duration, size, progress, token), CancellationToken.None);
            Builds[key] = task;
            return task;
        }
    }

    private static async Task BuildAsync(string key, string sourcePath, double duration, int size, IProgress<double>? progress, CancellationToken token)
    {
        await Gate.WaitAsync(token).ConfigureAwait(false);
        VideoFrameStore? building = null;
        var directory = Directory_;
        var work = Path.Combine(directory, $"work-{Guid.NewGuid():N}.cmf");
        try
        {
            if (IsComplete(sourcePath, size)) { progress?.Report(1); return; }
            Directory.CreateDirectory(directory);
            foreach (var stale in new DirectoryInfo(directory).EnumerateFiles("work-*.cmf").Where(file => file.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1)))
                try { stale.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            ManagedMediaCache.Prune(EditorPreferences.Current.CacheBytes);
            var ffmpeg = MediaRenderService.FindTool("ffmpeg.exe");
            var fps = await ProbeRateAsync(sourcePath, token).ConfigureAwait(false);
            var expected = Math.Max(1, duration * fps);
            var ends = new List<long>();
            var info = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "-hide_banner", "-nostdin", "-v", "error", "-i", sourcePath, "-map", "0:v:0", "-an", "-sn", "-vf",
                $"fps={fps.ToString("R", CultureInfo.InvariantCulture)}:start_time=0,scale='min({size},iw)':'min({size},ih)':force_original_aspect_ratio=decrease:force_divisible_by=2,setsar=1",
                "-c:v", "mjpeg", "-q:v", "6", "-pix_fmt", "yuvj420p", "-f", "mjpeg", "pipe:1" }) info.ArgumentList.Add(argument);
            using var process = new Process { StartInfo = info };
            using (var writer = new FileStream(work, FileMode.CreateNew, FileAccess.Write, FileShare.Read | FileShare.Delete, 1 << 20))
            {
                if (!process.Start()) throw new IOException("無法啟動媒體引擎。");
                MediaWorkScheduler.LowerPriority(process);
                using var registration = token.Register(() =>
                {
                    try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { }
                });
                var errors = new StringBuilder();
                var errorTask = Task.Run(async () =>
                {
                    while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
                        if (errors.Length < 4000) errors.AppendLine(line);
                });
                building = VideoFrameStore.OpenBuilding(work, fps);
                Stores[key] = building;
                var input = process.StandardOutput.BaseStream;
                var buffer = new byte[1 << 20];
                long position = 0; var marker = false; var announced = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    await writer.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    // Entropy-coded JPEG data escapes 0xFF, so FF D9 only occurs as the end-of-image marker.
                    var found = new List<long>();
                    for (var index = 0; index < read; index++)
                    {
                        var value = buffer[index];
                        if (marker && value == 0xD9) found.Add(position + index + 1);
                        marker = value == 0xFF;
                    }
                    position += read;
                    if (found.Count == 0) continue;
                    writer.Flush();
                    foreach (var end in found) { ends.Add(end); building.Publish(end); }
                    progress?.Report(Math.Min(.99, ends.Count / expected));
                    // Tell viewers roughly twice a second of source that more frames became usable.
                    if (ends.Count - announced >= fps / 2) { announced = ends.Count; Announce(); }
                }
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                await errorTask.ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (process.ExitCode != 0) throw new IOException($"媒體引擎無法建立影格暫存（{process.ExitCode}）。\n{errors}");
                if (ends.Count == 0 || ends[^1] != position) throw new IOException("影格暫存輸出不完整。");
                if (key != Key(sourcePath, size)) throw new IOException("暫存期間來源影片已變更，請重新暫存。");
                VideoFrameStore.WriteTrailer(writer, ends, fps);
            }
            // Swap the growing store for the published file; readers fall back for the instant between.
            Stores.TryRemove(key, out _);
            building.Dispose(); building = null;
            var target = Path.Combine(directory, key + ".cmf");
            try { File.Move(work, target); }
            catch (IOException) when (File.Exists(target)) { }
            if (VideoFrameStore.OpenComplete(target) is { } complete && !Stores.TryAdd(key, complete)) complete.Dispose();
            ManagedMediaCache.Prune(EditorPreferences.Current.CacheBytes);
            progress?.Report(1);
        }
        finally
        {
            if (building is not null) { Stores.TryRemove(new(key, building)); building.Dispose(); }
            try { File.Delete(work); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            Gate.Release();
            Announce();
        }
    }

    private static void Announce() { Interlocked.Increment(ref _version); Changed?.Invoke(); }

    private static async Task<double> ProbeRateAsync(string path, CancellationToken token)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var text = (await MediaRenderService.RunToolAsync(MediaRenderService.FindTool("ffprobe.exe"),
                ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=avg_frame_rate", "-of", "default=nw=1:nk=1", path], timeout.Token).ConfigureAwait(false)).Trim();
            var parts = text.Split('/');
            if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator) &&
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator) && denominator > 0)
            {
                var rate = numerator / denominator;
                if (double.IsFinite(rate) && rate >= 1) return Math.Min(30, rate);
            }
        }
        catch (IOException) { }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        return 30;
    }
}
