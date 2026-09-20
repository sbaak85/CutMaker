using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CutMaker.Core;

namespace CutMaker.App;

internal sealed record ProbedMedia(MediaKind Kind, double Duration);

internal static class MediaProbe
{
    public static async Task<ProbedMedia> ReadAsync(ImportCandidate candidate, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (candidate.ExpectedKind == MediaKind.Image)
        {
            // Decode a small thumbnail to validate the image without retaining a full-size bitmap or file lock.
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var stream = File.OpenRead(candidate.FullPath);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 64;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0) throw new InvalidDataException("無法讀取圖片尺寸。");
                cancellationToken.ThrowIfCancellationRequested();
                return new ProbedMedia(MediaKind.Image, 5); // A still image has a chosen default duration, not a source duration.
            }, cancellationToken);
        }

        // Native playback validates compatibility, but Windows can truncate MP4 NaturalDuration
        // to whole seconds (including zero for sub-second clips). FFprobe supplies precise time.
        var player = new MediaPlayer { Volume = 0, IsMuted = true };
        var completion = new TaskCompletionSource<ProbedMedia>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler opened = (_, _) =>
        {
            if (!player.HasAudio && !player.HasVideo)
            {
                completion.TrySetException(new InvalidDataException("無法取得可用的影音串流。"));
                return;
            }
            completion.TrySetResult(new(player.HasVideo ? MediaKind.Video : MediaKind.Audio, 0));
        };
        EventHandler<ExceptionEventArgs> failed = (_, args) => completion.TrySetException(
            new InvalidDataException("Windows 無法讀取此檔案，可能是編碼不支援或檔案損毀。", args.ErrorException));
        player.MediaOpened += opened;
        player.MediaFailed += failed;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            player.Open(new Uri(candidate.FullPath, UriKind.Absolute));
            var native = await completion.Task.WaitAsync(timeout.Token);
            var duration = await ReadContainerDurationAsync(candidate.FullPath, timeout.Token);
            return native with { Duration = duration };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("讀取影音超過 15 秒。"); }
        finally
        {
            player.MediaOpened -= opened;
            player.MediaFailed -= failed;
            player.Close();
        }
    }

    internal static async Task<double> ReadContainerDurationAsync(string path, CancellationToken cancellationToken)
    {
        var ffprobe = MediaRenderService.FindTool("ffprobe.exe");
        var json = await MediaRenderService.RunToolAsync(ffprobe,
            ["-v", "error", "-show_entries", "format=duration:stream=duration", "-of", "json", path], cancellationToken);
        using var metadata = JsonDocument.Parse(json);
        var root = metadata.RootElement;
        if (root.TryGetProperty("format", out var format) && TryDuration(format, out var duration)) return duration;
        if (root.TryGetProperty("streams", out var streams))
        {
            var times = streams.EnumerateArray().Select(stream => TryDuration(stream, out var time) ? time : 0).ToArray();
            if (times.Length > 0 && times.Max() > 0) return times.Max();
        }
        throw new InvalidDataException("FFprobe 無法取得有效的影音時長。");
    }

    private static bool TryDuration(JsonElement element, out double duration)
    {
        duration = 0;
        if (!element.TryGetProperty("duration", out var value)) return false;
        return double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out duration) &&
            double.IsFinite(duration) && duration > 0;
    }
}
