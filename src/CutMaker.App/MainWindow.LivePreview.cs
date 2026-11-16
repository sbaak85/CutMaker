using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CutMaker.Core;

namespace CutMaker.App;

/// <summary>
/// Live video preview: the picture is composited from per-source frame stores at the playhead
/// and the sound is the real-time mixer, so timeline edits need no re-render or re-cache.
/// </summary>
public partial class MainWindow
{
    private sealed record LiveClip(Clip Clip, bool Image, string Path, VideoFrameStore? Store, BitmapSource? Still);

    /// <summary>The offscreen regression suite keeps exercising the rendered-file path.</summary>
    internal bool UseRenderedPreviewForChecks { get; set; }
    private bool LiveMode => EditorPreferences.Current.LivePreview && !UseRenderedPreviewForChecks;

    private static readonly Dictionary<string, BitmapSource?> LiveStills = new(StringComparer.Ordinal);
    private readonly object _liveFrameSync = new();
    private readonly Dictionary<(VideoFrameStore Store, int Index), LinkedListNode<((VideoFrameStore, int) Key, BitmapSource Frame)>> _liveFrames = [];
    private readonly LinkedList<((VideoFrameStore, int) Key, BitmapSource Frame)> _liveFrameOrder = new();
    private LiveClip[] _livePlan = [];
    private bool _livePlanValid;
    private bool _livePlanVisual;
    private int _livePlanMissing;
    private int _livePlanVersion = -1;
    private int _livePlanSize;
    private CutProject? _livePlanProject;
    private int _livePrefetching;
    private int _liveRefreshQueued;
    private bool _liveSubscribed;
    private CancellationTokenSource? _liveCacheCancellation;
    private Task _liveCacheTask = Task.CompletedTask;

    private void InvalidateLivePlan() => _livePlanValid = false;

    /// <summary>True when the timeline has visible pictures and live mode is on.</summary>
    private bool EnsureLivePlan()
    {
        if (!LiveMode) return false;
        var size = EditorPreferences.Current.PreviewWidth;
        var version = VideoFrameCache.Version;
        if (_livePlanValid && ReferenceEquals(_livePlanProject, _project) && _livePlanVersion == version && _livePlanSize == size)
            return _livePlanVisual;
        if (!_liveSubscribed) { _liveSubscribed = true; VideoFrameCache.Changed += LiveCacheChanged; }
        var assets = _project.MediaAssets.ToDictionary(asset => asset.Id);
        var plan = new List<(int Track, LiveClip Clip)>();
        var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < _project.Tracks.Count; index++)
        {
            var track = _project.Tracks[index];
            if (track.Kind != TrackKind.Video || track.Muted) continue;
            foreach (var clip in _project.Clips.Where(clip => clip.TrackId == track.Id))
            {
                if (!assets.TryGetValue(clip.AssetId, out var asset) || asset.Kind == MediaKind.Audio) continue;
                var path = _projectPath is null ? Path.GetFullPath(asset.Path) : ProjectStore.ResolveAssetPath(_projectPath, asset);
                if (asset.Kind == MediaKind.Image) plan.Add((index, new(clip, true, path, null, LoadLiveStill(path, size))));
                else
                {
                    var store = VideoFrameCache.TryGet(path, size);
                    if (store is not { Complete: true }) missing.Add(path);
                    plan.Add((index, new(clip, false, path, store, null)));
                }
            }
        }
        // Lower track indices are composited in front, as in the export graph.
        _livePlan = plan.OrderBy(item => item.Track).Select(item => item.Clip).ToArray();
        _livePlanVisual = _livePlan.Length != 0;
        _livePlanMissing = missing.Count;
        _livePlanProject = _project; _livePlanVersion = version; _livePlanSize = size; _livePlanValid = true;
        if (_liveCacheCancellation is null)
            CachePreviewButton.Content = _livePlanVisual && _livePlanMissing == 0 ? "已暫存" : "暫存快取";
        return _livePlanVisual;
    }

    private static BitmapSource? LoadLiveStill(string path, int size)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists) return null;
            var key = $"{file.FullName.ToUpperInvariant()}|{file.Length}|{file.LastWriteTimeUtc.Ticks}|{size}";
            if (LiveStills.TryGetValue(key, out var cached)) return cached;
            BitmapSource? image = null;
            try
            {
                int width, height;
                using (var stream = File.OpenRead(path))
                {
                    var probe = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                    width = probe.PixelWidth; height = probe.PixelHeight;
                }
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                if (width > size && width >= height) bitmap.DecodePixelWidth = size;
                else if (height > size) bitmap.DecodePixelHeight = size;
                bitmap.EndInit(); bitmap.Freeze();
                image = bitmap;
            }
            catch (Exception ex) when (ex is NotSupportedException or FileFormatException or IOException or UnauthorizedAccessException or
                ArgumentException or System.Runtime.InteropServices.COMException or InvalidOperationException) { }
            if (LiveStills.Count > 64) LiveStills.Clear();
            LiveStills[key] = image;
            return image;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    private BitmapSource? LiveFrame(VideoFrameStore store, int index, bool decode = true)
    {
        lock (_liveFrameSync)
        {
            if (_liveFrames.TryGetValue((store, index), out var node))
            {
                _liveFrameOrder.Remove(node); _liveFrameOrder.AddFirst(node);
                return node.Value.Frame;
            }
        }
        if (!decode) return null;
        var frame = store.Decode(index);
        if (frame is null) return null;
        lock (_liveFrameSync)
        {
            if (!_liveFrames.ContainsKey((store, index)))
            {
                _liveFrames[(store, index)] = _liveFrameOrder.AddFirst(((store, index), frame));
                while (_liveFrameOrder.Count > 16)
                {
                    _liveFrames.Remove(_liveFrameOrder.Last!.Value.Key);
                    _liveFrameOrder.RemoveLast();
                }
            }
        }
        return frame;
    }

    private static int LiveFrameIndex(LiveClip item, double time) =>
        (int)Math.Floor((item.Clip.SourceIn + time - item.Clip.Start) * item.Store!.Fps + 1e-6);

    /// <summary>Layers bottom to top, or null when a required source frame is not stored yet.</summary>
    private List<LiveLayer>? ComposeLiveFrame(double seconds)
    {
        var duration = PreviewDuration;
        var time = Math.Clamp(seconds, 0, Math.Max(0, duration - .5 / Math.Min(30, _project.Video.Fps)));
        var layers = new List<LiveLayer>();
        foreach (var item in _livePlan)
        {
            var clip = item.Clip;
            if (time < clip.Start || time >= clip.End) continue;
            BitmapSource? picture;
            if (item.Image) picture = item.Still;
            else
            {
                if (item.Store is not { } store) return null;
                var index = Math.Max(0, LiveFrameIndex(item, time));
                var count = store.Count;
                // A codec can end slightly before its container duration: hold the last frame.
                if (index >= count) { if (!store.Complete || count == 0) return null; index = count - 1; }
                picture = LiveFrame(store, index);
            }
            if (picture is null) return null;
            var gain = Math.Clamp(FadeEnvelope.Gain(clip, time - clip.Start), 0, 1);
            var black = clip.VideoFadeMode == VideoFadeMode.Black;
            layers.Add(new(picture, gain, black));
            var format = picture.Format;
            var opaque = !item.Image || format == PixelFormats.Bgr24 || format == PixelFormats.Bgr32 || format == PixelFormats.Rgb24 ||
                format == PixelFormats.Gray8 || format == PixelFormats.Gray16 || format == PixelFormats.Rgb48 || format == PixelFormats.Cmyk32;
            if (opaque && (gain >= 1 || black)) break;
        }
        layers.Reverse();
        return layers;
    }

    /// <summary>Shows the composited frame at the playhead; false leaves the rendered-still fallback in charge.</summary>
    private bool RefreshLiveFrame()
    {
        if (!_previewInitialized || _previewShutdown) return false;
        if (PreviewDuration <= 0 || !EnsureLivePlan())
        {
            PreviewLiveView.Visibility = Visibility.Collapsed;
            return false;
        }
        var layers = ComposeLiveFrame(PlayheadSeconds);
        if (layers is null)
        {
            PreviewLiveView.Visibility = Visibility.Collapsed;
            if (_previewPlaying)
            {
                PreviewHint.Text = "此段畫面仍在暫存，完成後自動顯示…";
                PreviewHint.Visibility = Visibility.Visible;
            }
            return false;
        }
        PreviewLiveView.SetFrame(_project.Video.Width / (double)Math.Max(1, _project.Video.Height), layers);
        PreviewLiveView.Visibility = Visibility.Visible;
        PreviewStillImage.Visibility = Visibility.Collapsed;
        PreviewHint.Visibility = Visibility.Collapsed;
        _previewFrameCancellation?.Cancel();
        if (_previewPlaying) PrefetchLiveFrames(PlayheadSeconds);
        return true;
    }

    private void PrefetchLiveFrames(double seconds)
    {
        if (Interlocked.Exchange(ref _livePrefetching, 1) == 1) return;
        var plan = _livePlan;
        var step = 1 / Math.Min(30, _project.Video.Fps);
        _ = Task.Run(() =>
        {
            try
            {
                for (var ahead = 1; ahead <= 3; ahead++)
                {
                    var time = seconds + ahead * step;
                    foreach (var item in plan)
                    {
                        if (item.Image || item.Store is not { } store || time < item.Clip.Start || time >= item.Clip.End) continue;
                        var index = LiveFrameIndex(item, time);
                        if (index >= 0 && index < store.Count) LiveFrame(store, index);
                    }
                }
            }
            finally { Volatile.Write(ref _livePrefetching, 0); }
        });
    }

    private void LiveCacheChanged()
    {
        if (Interlocked.Exchange(ref _liveRefreshQueued, 1) == 1) return;
        Dispatcher.BeginInvoke(() =>
        {
            Volatile.Write(ref _liveRefreshQueued, 0);
            if (_previewShutdown) return;
            // Frames that just became available replace the slower rendered still.
            RefreshLiveFrame();
        });
    }

    private void ShutdownLivePreview()
    {
        _liveCacheCancellation?.Cancel();
        if (_liveSubscribed) { _liveSubscribed = false; VideoFrameCache.Changed -= LiveCacheChanged; }
    }

    private async void CachePreview_Click(object sender, RoutedEventArgs e)
    {
        if (_liveCacheCancellation is { } running) { running.Cancel(); return; }
        if (!EditorPreferences.Current.LivePreview)
        {
            StatusText.Text = "請先在「預覽與快取」開啟即時預覽，才能使用素材暫存快取";
            return;
        }
        await CacheLivePreviewAsync(wholeLibrary: true, audio: true);
    }

    /// <summary>One-time per source: store preview frames (and decoded sound) so later edits are instant.</summary>
    internal async Task CacheLivePreviewAsync(bool wholeLibrary, bool audio)
    {
        if (_liveCacheCancellation is not null || _previewShutdown) return;
        var snapshot = CreatePreviewSnapshot();
        var size = EditorPreferences.Current.PreviewWidth;
        var used = snapshot.Clips.Select(clip => clip.AssetId).ToHashSet();
        var videos = snapshot.MediaAssets.Where(asset => asset.Kind == MediaKind.Video && (wholeLibrary || used.Contains(asset.Id)) && File.Exists(asset.Path))
            .GroupBy(asset => asset.Path, StringComparer.OrdinalIgnoreCase).Select(group => group.First())
            .Where(asset => !VideoFrameCache.IsComplete(asset.Path, size)).ToArray();
        var sounds = !audio ? [] : snapshot.MediaAssets.Where(asset => asset.Kind != MediaKind.Image && used.Contains(asset.Id) && File.Exists(asset.Path))
            .GroupBy(asset => asset.Path, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToArray();
        if (videos.Length == 0 && sounds.Length == 0)
        {
            StatusText.Text = snapshot.MediaAssets.Count == 0 ? "請先匯入素材" : "素材皆已暫存 · 剪輯後可直接播放";
            return;
        }
        using var cancellation = new CancellationTokenSource();
        _liveCacheCancellation = cancellation;
        var total = videos.Length + sounds.Length;
        var done = 0;
        void Report(string name, double fraction)
        {
            if (_previewShutdown || cancellation.IsCancellationRequested) return;
            CachePreviewButton.Content = $"取消暫存 {Math.Clamp((done + fraction) / total, 0, 1):P0}";
            StatusText.Text = $"暫存素材 {done + 1}/{total}：{name} · 已完成的部分可立即預覽";
        }
        try
        {
            foreach (var asset in videos)
            {
                var name = Path.GetFileName(asset.Path);
                Report(name, 0);
                await VideoFrameCache.EnsureAsync(asset.Path, asset.Duration, size, new Progress<double>(value => Report(name, value)), cancellation.Token);
                done++;
            }
            var ffmpeg = sounds.Length == 0 ? string.Empty : MediaRenderService.FindTool("ffmpeg.exe");
            foreach (var asset in sounds)
            {
                var name = Path.GetFileName(asset.Path);
                Report(name, 0);
                try
                {
                    using var lease = await AudioPreviewCache.Shared.AcquireAsync(asset.Path, asset.Duration, ffmpeg,
                        new Progress<MediaRenderProgress>(value => Report(name, value.Fraction)), cancellation.Token);
                }
                // A silent video has no audio stream to decode; playback simply mixes nothing for it.
                catch (IOException) when (asset.Kind == MediaKind.Video) { }
                done++;
            }
            if (!_previewShutdown) StatusText.Text = "暫存完成 · 移動、修剪、切割與 Fade 之後可直接播放，不需重新快取";
        }
        catch (OperationCanceledException) { if (!_previewShutdown) StatusText.Text = "已取消暫存 · 已完成的素材仍可使用"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            PreviewDiagnostics.Write("live-cache-failed", ex.ToString());
            if (!_previewShutdown) StatusText.Text = "暫存失敗：" + ex.Message.Split('\n')[0];
        }
        finally
        {
            _liveCacheCancellation = null;
            if (!_previewShutdown)
            {
                InvalidateLivePlan();
                CachePreviewButton.Content = "暫存快取";
                if (!RefreshLiveFrame() && !_previewPlaying && !_previewPreparing && LiveMode && _livePlanVisual) QueuePreviewFrame();
            }
        }
    }

    /// <summary>Playback never waits for pictures: missing sources are stored in the background.</summary>
    private void StartLiveCachingIfNeeded()
    {
        if (!EnsureLivePlan() || _livePlanMissing == 0 || _liveCacheCancellation is not null) return;
        _liveCacheTask = CacheLivePreviewAsync(wholeLibrary: false, audio: false);
    }
}
