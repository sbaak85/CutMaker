using System.Diagnostics;
using System.Globalization;
using System.IO;
using CutMaker.Core;

namespace CutMaker.App;

internal sealed class AudioBufferPendingException : IOException;

/// <summary>Decode once from source zero to retain resampling phase; expose committed PCM as it arrives.</summary>
internal sealed class ProgressiveAudioSource : IDisposable
{
    private readonly CancellationTokenSource _stop;
    private readonly string _temporary;
    private readonly string _target;
    private readonly Task _decode;
    private AudioPreviewCache.Lease? _lease;
    private long _frames;
    private Exception? _error;
    private int _complete;
    private bool _disposed;
    internal FileStream Reader { get; }
    internal long AvailableFrames => Interlocked.Read(ref _frames);
    internal bool Complete => Volatile.Read(ref _complete) != 0;

    private ProgressiveAudioSource(string path, string target, string ffmpeg, long maximumBytes, IDisposable reservation, CancellationToken token)
    {
        _target = target;
        _temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        _stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var writer = new FileStream(_temporary, FileMode.CreateNew, FileAccess.Write, FileShare.Read | FileShare.Delete, 65536, FileOptions.Asynchronous);
        try { Reader = new FileStream(_temporary, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.RandomAccess); }
        catch { writer.Dispose(); _stop.Dispose(); File.Delete(_temporary); throw; }
        _decode = Task.Run(async () =>
        {
            try
            {
                var key = AudioPreviewCache.GetKey(path, ffmpeg);
                var info = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var arg in new[] { "-hide_banner", "-nostdin", "-v", "error", "-threads", "2", "-i", path, "-map", "0:a:0", "-vn", "-af",
                    $"aresample={AudioSampleClock.Rate},aformat=sample_fmts=dblp:channel_layouts=stereo,asetpts=N/SR/TB", "-c:a", "pcm_f64le", "-f", "f64le", "pipe:1" }) info.ArgumentList.Add(arg);
                using var process = Process.Start(info) ?? throw new IOException("無法啟動音訊解碼。");
                using var kill = _stop.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { } });
                var errors = Task.Run(async () => { while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is not null) { } });
                try
                {
                    var bytes = new byte[65536]; long written = 0;
                    while (true)
                    {
                        var count = await process.StandardOutput.BaseStream.ReadAsync(bytes, _stop.Token).ConfigureAwait(false);
                        if (count == 0) break;
                        if (written + count > maximumBytes) throw new IOException("音訊快取超過設定上限，請提高快取容量。");
                        await writer.WriteAsync(bytes.AsMemory(0, count), _stop.Token).ConfigureAwait(false);
                        await writer.FlushAsync(_stop.Token).ConfigureAwait(false);
                        written += count;
                        Interlocked.Exchange(ref _frames, written / AudioPreviewCache.BytesPerFrame);
                    }
                    await process.WaitForExitAsync(_stop.Token).ConfigureAwait(false);
                    _stop.Token.ThrowIfCancellationRequested();
                    if (process.ExitCode != 0 || written == 0 || written % AudioPreviewCache.BytesPerFrame != 0) throw new IOException("漸進音訊解碼未完成。");
                    if (key != AudioPreviewCache.GetKey(path, ffmpeg)) throw new IOException("解碼期間來源已變更。");
                    await writer.DisposeAsync().ConfigureAwait(false);
                    try { File.Move(_temporary, _target); }
                    catch (IOException) when (File.Exists(_target)) { }
                    _lease = AudioPreviewCache.TryOpen(_target, false);
                }
                finally
                {
                    try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                    await errors.ConfigureAwait(false);
                }
            }
            catch (Exception ex) { _error = ex; }
            finally { await writer.DisposeAsync().ConfigureAwait(false); reservation.Dispose(); Volatile.Write(ref _complete, 1); }
        });
    }

    internal static async Task<ProgressiveAudioSource> StartAsync(string path, string ffmpeg, double requiredThrough,
        long maximumBytes, IProgress<MediaRenderProgress>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var target = Path.Combine(EditorPreferences.Root, "audio-preview", AudioPreviewCache.GetKey(path, ffmpeg) + ".f64");
        var reservation = ManagedMediaCache.Reserve(maximumBytes);
        ProgressiveAudioSource source;
        try { source = new ProgressiveAudioSource(path, target, ffmpeg, maximumBytes, reservation, token); }
        catch { reservation.Dispose(); throw; }
        try
        {
            var required = AudioSampleClock.At(requiredThrough);
            while (source.AvailableFrames < required && !source.Complete)
            {
                progress?.Report(new(Math.Min(.99, source.AvailableFrames / (double)Math.Max(1, required)), "優先準備播放位置，剩餘音訊將在背景載入…"));
                await Task.Delay(15, token).ConfigureAwait(false);
            }
            source.ThrowIfFailed(); token.ThrowIfCancellationRequested();
            return source;
        }
        catch { source.Dispose(); throw; }
    }
    internal int ReadableFrames(long first, int count)
    {
        ThrowIfFailed();
        var available = AvailableFrames;
        if (first + count > available && !Complete) throw new AudioBufferPendingException();
        return (int)Math.Min(count, Math.Max(0, available - first));
    }
    private void ThrowIfFailed()
    {
        if (_error is { } error) throw new IOException("背景音訊載入失敗。", error);
    }
    internal void Cancel() => _stop.Cancel();
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _stop.Cancel(); _decode.GetAwaiter().GetResult();
        Reader.Dispose(); _lease?.Dispose(); _stop.Dispose();
        try { File.Delete(_temporary); } catch (IOException) { }
    }
}
