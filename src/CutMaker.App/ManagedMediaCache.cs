using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace CutMaker.App;

/// <summary>Only hash-named disposable media in explicit cache folders is eligible for cleanup.</summary>
internal static class ManagedMediaCache
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly object Reservations = new();
    private static long _reservedBytes;
    private static readonly string[] Folders = ["audio-preview", "media-visuals", "video-regions", "video-proxies"];
    internal static string Hash(string identity) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
    internal static IDisposable Reserve(long bytes)
    {
        lock (Reservations)
        {
            var budget = EditorPreferences.Current.CacheBytes;
            var remaining = Prune(Math.Max(0, budget - _reservedBytes - bytes)).Remaining;
            if (remaining + _reservedBytes + bytes > budget) throw new IOException("使用中與正在載入的快取已占滿容量，請提高快取上限後再播放。");
            _reservedBytes += bytes;
            return new Reservation(bytes);
        }
    }
    private sealed class Reservation(long bytes) : IDisposable
    {
        private long _bytes = bytes;
        public void Dispose() { lock (Reservations) { _reservedBytes -= _bytes; _bytes = 0; } }
    }
    internal sealed class Lease(string path, FileStream hold, bool reused) : IDisposable
    {
        internal string Path { get; } = path;
        internal bool Reused { get; } = reused;
        public void Dispose() => hold.Dispose();
    }
    internal static async Task<Lease> GetAsync(string folder, string key, string extension,
        Func<string, CancellationToken, Task> create, CancellationToken token)
    {
        if (!Folders.Contains(folder) || key.Length != 64 || !key.All(Uri.IsHexDigit) || extension != ".mp4")
            throw new ArgumentException("Invalid owned cache name.");
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var directory = Path.Combine(EditorPreferences.Root, folder);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, key + extension);
            if (Open(path, true) is { } ready) return ready;
            var temporary = Path.Combine(directory, $"work-{Guid.NewGuid():N}{extension}");
            try
            {
                Prune(EditorPreferences.Current.CacheBytes);
                await create(temporary, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (!File.Exists(temporary) || new FileInfo(temporary).Length == 0) throw new IOException("快取輸出不完整。");
                try { File.Move(temporary, path); }
                catch (IOException) when (File.Exists(path)) { }
                var lease = Open(path, false) ?? throw new IOException("無法開啟預覽快取。");
                Prune(EditorPreferences.Current.CacheBytes);
                return lease;
            }
            finally { TryDelete(temporary); }
        }
        finally { Gate.Release(); }
    }
    private static Lease? Open(string path, bool reused)
    {
        try
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length == 0) { stream.Dispose(); TryDelete(path); return null; }
            try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return new(path, stream, reused);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
    private static IEnumerable<FileInfo> OwnedFiles()
    {
        foreach (var folder in Folders)
        {
            var path = Path.Combine(EditorPreferences.Root, folder);
            if (!Directory.Exists(path)) continue;
            foreach (var file in new DirectoryInfo(path).EnumerateFiles())
                if (file.Name.Length > 64 && file.Name.AsSpan(0, 64).ToString().All(Uri.IsHexDigit) &&
                    file.Extension is ".mp4" or ".f64" or ".png") yield return file;
        }
    }
    internal static long UsageBytes() => OwnedFiles().Sum(file => file.Length);
    internal static (long Remaining, int Busy) Prune(long limit)
    {
        var files = OwnedFiles().OrderBy(file => file.LastWriteTimeUtc).ToArray();
        long total = files.Sum(file => file.Length); var busy = 0;
        foreach (var file in files)
        {
            if (total <= limit) break;
            if (TryDelete(file.FullName)) total -= file.Length; else busy++;
        }
        return (total, busy);
    }
    private static bool TryDelete(string path)
    {
        try { File.Delete(path); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
