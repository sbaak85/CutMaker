using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CutMaker.Core;

namespace CutMaker.App;

internal sealed record MediaVisualSet(BitmapSource? Thumbnail, BitmapSource? Waveform);

/// <summary>Low-resolution source overviews. PCM is reduced into fixed-size peak bins while streaming.</summary>
internal static class MediaVisualCache
{
    private static readonly SemaphoreSlim Workers = new(2, 2);
    private const int WaveWidth = 1024;
    private const int WaveHeight = 48;
    private const int WaveSampleRate = 4000;
    private static readonly ConcurrentDictionary<string, MediaVisualSet> Memory = new();
    private static readonly ConcurrentQueue<string> MemoryOrder = new();
    internal static string CacheDirectory => Path.Combine(LayoutSettings.DataDirectory, "media-visuals");
    internal static void ClearMemory() { Memory.Clear(); while (MemoryOrder.TryDequeue(out _)) { } }

    internal static string GetKey(MediaAsset asset, string? projectPath)
    {
        var path = ResolvePath(asset, projectPath);
        var file = new FileInfo(path);
        var signature = string.Join("|", "visual-gold-v2", path.ToUpperInvariant(), file.Exists ? file.LastWriteTimeUtc.Ticks : 0,
            file.Exists ? file.Length : 0, asset.Kind, asset.Duration.ToString("R", CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature))).ToLowerInvariant();
    }

    internal static async Task<MediaVisualSet> GetAsync(MediaAsset asset, string? projectPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = GetKey(asset, projectPath);
        if (Memory.TryGetValue(key, out var known)) return known;
        using var scheduled = await MediaWorkScheduler.BackgroundAsync(cancellationToken).ConfigureAwait(false);
        await Workers.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Memory.TryGetValue(key, out known)) return known;
            cancellationToken.ThrowIfCancellationRequested();
            var path = ResolvePath(asset, projectPath);
            if (!File.Exists(path)) return new(null, null);
            Directory.CreateDirectory(CacheDirectory);
            BitmapSource? thumbnail = null;
            BitmapSource? waveform = null;
            if (asset.Kind != MediaKind.Audio)
            {
                var target = Path.Combine(CacheDirectory, key + "-thumb.png");
                thumbnail = LoadBitmap(target);
                if (thumbnail is null)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(TimeSpan.FromSeconds(25));
                    var temporary = target + "." + Guid.NewGuid().ToString("N") + ".png";
                    try
                    {
                        var args = new List<string> { "-hide_banner", "-nostdin", "-v", "error", "-y", "-threads", "1" };
                        if (asset.Kind == MediaKind.Video) args.AddRange(["-ss", Math.Min(.25, asset.Duration / 3).ToString("0.######", CultureInfo.InvariantCulture)]);
                        args.AddRange(["-i", path, "-map", "0:v:0", "-frames:v", "1", "-an", "-vf",
                            "scale=160:90:force_original_aspect_ratio=decrease,pad=160:90:(ow-iw)/2:(oh-ih)/2:black", "-threads", "1", "-update", "1", temporary]);
                        await MediaRenderService.RunToolAsync(MediaRenderService.FindTool("ffmpeg.exe"), args, timeout.Token, background: true).ConfigureAwait(false);
                        thumbnail = LoadBitmap(temporary);
                        if (thumbnail is not null) File.Move(temporary, target, overwrite: true);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
                    catch (Exception ex) when (IsOptionalVisualError(ex)) { }
                    finally { DeleteTemp(temporary); }
                }
            }
            if (asset.Kind != MediaKind.Image)
            {
                var target = Path.Combine(CacheDirectory, key + "-wave.png");
                waveform = LoadBitmap(target);
                if (waveform is null)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(TimeSpan.FromMinutes(2));
                    try
                    {
                        var peaks = await ReadPeaksAsync(path, asset.Duration, timeout.Token).ConfigureAwait(false);
                        waveform = DrawWaveform(peaks);
                        SaveBitmap(waveform, target);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
                    catch (Exception ex) when (IsOptionalVisualError(ex)) { } // A silent video has no waveform.
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            var result = new MediaVisualSet(thumbnail, waveform);
            Memory[key] = result;
            MemoryOrder.Enqueue(key);
            while (Memory.Count > 128 && MemoryOrder.TryDequeue(out var oldest)) Memory.TryRemove(oldest, out _);
            ManagedMediaCache.Prune(EditorPreferences.Current.CacheBytes);
            return result;
        }
        finally { Workers.Release(); }
    }

    internal static async Task<BitmapSource> GetWaveDetailAsync(MediaAsset asset, string? projectPath, double start, double duration, CancellationToken token)
    {
        var key = ManagedMediaCache.Hash($"wave-detail-gold-v2|{GetKey(asset, projectPath)}|{start:R}|{duration:R}");
        if (Memory.TryGetValue(key, out var known) && known.Waveform is not null) return known.Waveform;
        using var scheduled = await MediaWorkScheduler.BackgroundAsync(token).ConfigureAwait(false);
        Directory.CreateDirectory(CacheDirectory);
        var path = Path.Combine(CacheDirectory, key + "-detail.png");
        var bitmap = LoadBitmap(path);
        if (bitmap is null)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            bitmap = DrawWaveform(await ReadPeaksAsync(ResolvePath(asset, projectPath), duration, timeout.Token, start, 48000, 2048).ConfigureAwait(false));
            SaveBitmap(bitmap, path);
        }
        token.ThrowIfCancellationRequested();
        Memory[key] = new(null, bitmap); MemoryOrder.Enqueue(key);
        while (Memory.Count > 128 && MemoryOrder.TryDequeue(out var oldest)) Memory.TryRemove(oldest, out _);
        ManagedMediaCache.Prune(EditorPreferences.Current.CacheBytes);
        return bitmap;
    }

    private static async Task<float[]> ReadPeaksAsync(string path, double duration, CancellationToken token,
        double start = 0, int sampleRate = WaveSampleRate, int width = WaveWidth)
    {
        var info = new ProcessStartInfo(MediaRenderService.FindTool("ffmpeg.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-hide_banner", "-nostdin", "-v", "error", "-threads", "1", "-ss", start.ToString("G17", CultureInfo.InvariantCulture), "-i", path,
            "-t", duration.ToString("0.#########", CultureInfo.InvariantCulture), "-map", "0:a:0", "-vn", "-ac", "1", "-ar",
            sampleRate.ToString(CultureInfo.InvariantCulture), "-c:a", "pcm_f32le", "-f", "f32le", "pipe:1" })
            info.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = info };
        token.ThrowIfCancellationRequested();
        if (!process.Start()) throw new IOException("無法啟動波形處理。");
        MediaWorkScheduler.LowerPriority(process);
        using var registration = token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        });
        var errors = Task.Run(async () =>
        {
            // Drain without accumulating unbounded FFmpeg output.
            while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is not null) { }
        });
        var peaks = new float[width];
        var bytes = new byte[8192 + 3];
        var remaining = 0;
        long sample = 0;
        var totalSamples = Math.Max(1, duration * sampleRate);
        try
        {
            while (true)
            {
                var count = await process.StandardOutput.BaseStream.ReadAsync(bytes.AsMemory(remaining, 8192), token).ConfigureAwait(false);
                if (count == 0) break;
                count += remaining;
                var usable = count / sizeof(float) * sizeof(float);
                for (var offset = 0; offset < usable; offset += sizeof(float))
                {
                    var value = Math.Abs(BitConverter.ToSingle(bytes, offset));
                    var bin = (int)Math.Clamp(sample++ / totalSamples * width, 0, width - 1);
                    if (float.IsFinite(value)) peaks[bin] = Math.Max(peaks[bin], Math.Clamp(value, 0, 1));
                }
                remaining = count - usable;
                if (remaining > 0) Buffer.BlockCopy(bytes, usable, bytes, 0, remaining);
            }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await errors.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0 || sample == 0) throw new IOException("素材沒有可用的音訊波形。");
            return peaks;
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            await errors.ConfigureAwait(false);
        }
    }

    private static BitmapSource DrawWaveform(float[] peaks)
    {
        var stride = peaks.Length * 4;
        var pixels = new byte[stride * WaveHeight];
        for (var x = 0; x < peaks.Length; x++)
        {
            var half = Math.Max(0, (int)Math.Round(peaks[x] * (WaveHeight / 2 - 1)));
            for (var y = WaveHeight / 2 - half; y <= WaveHeight / 2 + half; y++)
            {
                var index = y * stride + x * 4;
                pixels[index] = 123; pixels[index + 1] = 190; pixels[index + 2] = 222; pixels[index + 3] = 255;
            }
        }
        var bitmap = BitmapSource.Create(peaks.Length, WaveHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource? LoadBitmap(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var stream = File.OpenRead(path);
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); return bitmap;
        }
        catch (Exception ex) when (IsOptionalVisualError(ex)) { return null; }
    }

    private static void SaveBitmap(BitmapSource bitmap, string path)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(temporary)) encoder.Save(stream);
            File.Move(temporary, path, overwrite: true);
        }
        finally { DeleteTemp(temporary); }
    }
    private static string ResolvePath(MediaAsset asset, string? projectPath) => projectPath is null
        ? Path.GetFullPath(asset.Path) : ProjectStore.ResolveAssetPath(projectPath, asset);
    private static bool IsOptionalVisualError(Exception ex) => ex is IOException or UnauthorizedAccessException or
        NotSupportedException or ArgumentException or InvalidOperationException or FormatException or System.ComponentModel.Win32Exception;
    private static void DeleteTemp(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
