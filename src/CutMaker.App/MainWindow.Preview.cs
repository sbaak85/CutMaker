using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using CutMaker.Core;

namespace CutMaker.App;

public partial class MainWindow
{
    public static readonly DependencyProperty PlayheadSecondsProperty = DependencyProperty.Register(
        nameof(PlayheadSeconds), typeof(double), typeof(MainWindow),
        new PropertyMetadata(0.0, (owner, _) => ((MainWindow)owner).RefreshPreviewTime()));
    public double PlayheadSeconds { get => (double)GetValue(PlayheadSecondsProperty); set => SetValue(PlayheadSecondsProperty, value); }

    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly string _previewSessionId = Guid.NewGuid().ToString("N");
    private CancellationTokenSource? _previewCancellation;
    private TaskCompletionSource<bool>? _previewOpenCompletion;
    private string? _previewFile;
    private int _previewRevision;
    private int _previewReadyRevision = -1;
    private bool _previewInitialized;
    private bool _previewShutdown;
    private bool _previewOpened;
    private bool _previewPlaying;
    private bool _previewPreparing;
    private bool _updatingPreviewSeek;
    private bool _previewScrubbing;
    private bool _resumeAfterPreviewScrub;

    internal bool PreviewIsReady => _previewOpened && _previewReadyRevision == _previewRevision;
    internal double PreviewDuration => _project.Clips.Count == 0 ? 0 : _project.Clips.Max(clip => clip.End);

    internal void InitializePreview()
    {
        if (_previewInitialized) return;
        _previewInitialized = true;
        PreviewPlayer.ScrubbingEnabled = true;
        _previewTimer.Tick += (_, _) =>
        {
            if (PreviewIsReady && _previewPlaying && !_previewScrubbing)
                PlayheadSeconds = Math.Clamp(PreviewPlayer.Position.TotalSeconds, 0, PreviewDuration);
        };
        PreviewSeekSlider.AddHandler(Mouse.LostMouseCaptureEvent, new MouseEventHandler((_, _) => EndPreviewScrub()), true);
        AddHandler(Mouse.LostMouseCaptureEvent, new MouseEventHandler((_, e) =>
        {
            if (e.OriginalSource is TimelineRuler) EndPreviewScrub();
        }), true);
        InvalidatePreview();
    }

    internal void RefreshPreviewState()
    {
        if (!_previewInitialized || _previewShutdown) return;
        PlayheadSeconds = Math.Clamp(PlayheadSeconds, 0, PreviewDuration);
        if (!PreviewIsReady && !_previewPreparing)
            PreviewHint.Text = PreviewDuration > 0 ? "按「更新預覽」或「播放」產生多軌預覽\n編輯後須重新更新；可拖曳時間尺定位" : "將素材放入軌道，開始編輯";
        RefreshPreviewControls();
    }

    /// <summary>Edits stop obsolete playback and cancel its render. Render again only when requested.</summary>
    internal void InvalidatePreview()
    {
        _previewRevision++;
        _previewCancellation?.Cancel();
        _previewOpenCompletion?.TrySetCanceled();
        _previewReadyRevision = -1;
        _previewOpened = false;
        _previewPlaying = false;
        _previewPreparing = false;
        _previewScrubbing = false;
        _resumeAfterPreviewScrub = false;
        if (!_previewInitialized || _previewShutdown) return;
        _previewTimer.Stop();
        PreviewPlayer.Close();
        PreviewPlayer.Source = null;
        DeletePreviewFile(_previewFile);
        _previewFile = null;
        PlayheadSeconds = Math.Clamp(PlayheadSeconds, 0, PreviewDuration);
        PreviewHint.Text = PreviewDuration > 0 ? "按「更新預覽」或「播放」產生多軌預覽\n編輯後須重新更新；可拖曳時間尺定位" : "將素材放入軌道，開始編輯";
        PreviewHint.Visibility = Visibility.Visible;
        RefreshPreviewControls();
    }

    internal void ShutdownPreview()
    {
        if (_previewShutdown) return;
        InvalidatePreview();
        _previewShutdown = true;
        _previewTimer.Stop();
    }

    private async void PreparePreview_Click(object sender, RoutedEventArgs e) => await PreparePreviewAsync();
    private async void PreviewPlay_Click(object sender, RoutedEventArgs e)
    {
        if (_previewPreparing) return;
        if (!PreviewIsReady) { await PreparePreviewAsync(playWhenReady: true); return; }
        if (_previewPlaying) PausePreview();
        else StartPreviewPlayback();
    }
    private void PreviewStop_Click(object sender, RoutedEventArgs e)
    {
        PausePreview();
        SeekPreview(0);
    }
    private void CancelPreview_Click(object sender, RoutedEventArgs e)
    {
        _previewCancellation?.Cancel();
        PreviewHint.Text = "正在取消預覽處理…";
        PreviewCancelButton.IsEnabled = false;
    }

    internal async Task PreparePreviewAsync(bool playWhenReady = false)
    {
        if (!_previewInitialized || _previewShutdown || _previewPreparing) return;
        if (PreviewDuration <= 0) { StatusText.Text = "請先將素材放入軌道"; return; }
        InvalidatePreview();
        var revision = _previewRevision;
        using var cancellation = new CancellationTokenSource();
        _previewCancellation = cancellation;
        _previewPreparing = true;
        var folder = Path.GetFullPath(Path.Combine(LayoutSettings.DataDirectory, "preview", _previewSessionId));
        var output = Path.Combine(folder, $"preview-{revision}.mp4");
        PreviewHint.Text = "正在產生多軌預覽… 0%";
        StatusText.Text = "正在處理預覽，可繼續編輯；編輯會取消本次處理";
        RefreshPreviewControls();
        try
        {
            // This snapshot remains independent of later edits and Save As path changes.
            var snapshot = _project with
            {
                MediaAssets = _project.MediaAssets.Select(asset => asset with
                {
                    Path = _projectPath is null ? Path.GetFullPath(asset.Path) : ProjectStore.ResolveAssetPath(_projectPath, asset)
                }).ToList(),
                Tracks = _project.Tracks.ToList(), Clips = _project.Clips.ToList()
            };
            Directory.CreateDirectory(folder);
            var progress = new Progress<MediaRenderProgress>(value =>
            {
                if (_previewShutdown || revision != _previewRevision || cancellation.IsCancellationRequested) return;
                PreviewHint.Text = $"正在產生多軌預覽… {Math.Clamp(value.Fraction, 0, 1):P0}\n{value.Message}";
            });
            await MediaRenderService.RenderAsync(snapshot, null, output, new MediaRenderOptions(Preview: true), progress, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (_previewShutdown || revision != _previewRevision) return;
            _previewFile = output;
            _previewReadyRevision = revision;
            _previewOpenCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            PreviewHint.Text = "正在開啟預覽…";
            PreviewPlayer.Volume = 0;
            PreviewPlayer.Source = new Uri(output, UriKind.Absolute);
            // Manual MediaElement must enter an active clock state to load. Pause immediately;
            // the volume stays zero until MediaOpened so preparing never emits audio.
            PreviewPlayer.Play();
            PreviewPlayer.Pause();
            await _previewOpenCompletion.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellation.Token);
            if (_previewShutdown || revision != _previewRevision) return;
            StatusText.Text = "預覽已更新，可播放或拖曳時間尺試看／試聽";
            if (playWhenReady) StartPreviewPlayback();
        }
        catch (OperationCanceledException)
        {
            if (!_previewShutdown && revision == _previewRevision)
            {
                CloseFailedPreview();
                PreviewHint.Text = "已取消預覽，按「更新預覽」重試";
                StatusText.Text = "預覽處理已取消";
            }
        }
        catch (Exception ex)
        {
            if (!_previewShutdown && revision == _previewRevision)
            {
                CloseFailedPreview();
                PreviewHint.Text = $"無法準備預覽\n{ex.Message}";
                StatusText.Text = "預覽失敗；剪輯資料仍保留，可重新嘗試";
            }
        }
        finally
        {
            if (!string.Equals(_previewFile, output, StringComparison.OrdinalIgnoreCase)) DeletePreviewFile(output);
            if (ReferenceEquals(_previewCancellation, cancellation)) _previewCancellation = null;
            if (!_previewShutdown && revision == _previewRevision)
            {
                _previewPreparing = false;
                RefreshPreviewControls();
            }
        }
    }

    private void PreviewPlayer_MediaOpened(object sender, RoutedEventArgs e)
    {
        if (_previewShutdown || _previewReadyRevision != _previewRevision || _previewFile is null ||
            !string.Equals(PreviewPlayer.Source?.LocalPath, _previewFile, StringComparison.OrdinalIgnoreCase)) return;
        _previewOpened = true;
        PreviewPlayer.Volume = 1;
        PreviewPlayer.Position = TimeSpan.FromSeconds(Math.Clamp(PlayheadSeconds, 0, PreviewDuration));
        PreviewPlayer.Pause();
        PreviewHint.Visibility = Visibility.Collapsed;
        _previewOpenCompletion?.TrySetResult(true);
        RefreshPreviewControls();
    }
    private void PreviewPlayer_MediaEnded(object sender, RoutedEventArgs e)
    {
        if (!PreviewIsReady) return;
        PausePreview();
        PlayheadSeconds = PreviewDuration;
    }
    private void PreviewPlayer_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        if (_previewShutdown || _previewReadyRevision != _previewRevision) return;
        var error = e.ErrorException ?? new InvalidOperationException("Windows 無法播放產生的預覽檔案。");
        _previewOpenCompletion?.TrySetException(error);
        CloseFailedPreview();
        PreviewHint.Text = $"預覽播放失敗\n{error.Message}";
        StatusText.Text = "預覽播放失敗；請更新預覽後再試";
        RefreshPreviewControls();
    }
    private void CloseFailedPreview()
    {
        _previewOpened = false;
        _previewReadyRevision = -1;
        _previewPlaying = false;
        _previewTimer.Stop();
        PreviewPlayer.Close();
        PreviewPlayer.Source = null;
        DeletePreviewFile(_previewFile);
        _previewFile = null;
        PreviewHint.Visibility = Visibility.Visible;
    }

    private void StartPreviewPlayback()
    {
        if (!PreviewIsReady) return;
        if (PlayheadSeconds >= PreviewDuration - 0.02) SeekPreview(0);
        PreviewPlayer.Play();
        _previewPlaying = true;
        _previewTimer.Start();
        RefreshPreviewControls();
    }
    private void PausePreview()
    {
        if (!_previewInitialized || _previewShutdown) return;
        if (PreviewIsReady) PreviewPlayer.Pause();
        _previewPlaying = false;
        _previewTimer.Stop();
        RefreshPreviewControls();
    }

    internal void SeekPreview(double seconds)
    {
        if (!double.IsFinite(seconds)) return;
        PlayheadSeconds = Math.Clamp(seconds, 0, PreviewDuration);
        if (PreviewIsReady) PreviewPlayer.Position = TimeSpan.FromSeconds(PlayheadSeconds);
    }
    private void PreviewSeek_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_previewInitialized && !_updatingPreviewSeek) SeekPreview(e.NewValue);
    }
    private void PreviewSeek_PointerDown(object sender, MouseButtonEventArgs e) => BeginPreviewScrub();
    private void PreviewSeek_PointerUp(object sender, MouseButtonEventArgs e) => EndPreviewScrub();

    private void BeginPreviewScrub()
    {
        if (_previewScrubbing) return;
        _resumeAfterPreviewScrub = _previewPlaying;
        _previewScrubbing = true;
        PausePreview();
    }
    private void EndPreviewScrub()
    {
        if (!_previewScrubbing) return;
        _previewScrubbing = false;
        var resume = _resumeAfterPreviewScrub;
        _resumeAfterPreviewScrub = false;
        if (resume) StartPreviewPlayback();
    }
    private void TimelineRuler_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var ruler = (UIElement)sender;
        BeginPreviewScrub();
        ruler.CaptureMouse();
        SeekPreview(TimelineOffsetSeconds + e.GetPosition(ruler).X / TimelinePixelsPerSecond);
        e.Handled = true;
    }
    private void TimelineRuler_MouseMove(object sender, MouseEventArgs e)
    {
        var ruler = (UIElement)sender;
        if (!ruler.IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed) return;
        SeekPreview(TimelineOffsetSeconds + e.GetPosition(ruler).X / TimelinePixelsPerSecond);
        e.Handled = true;
    }
    private void TimelineRuler_MouseUp(object sender, MouseButtonEventArgs e)
    {
        var ruler = (UIElement)sender;
        if (!ruler.IsMouseCaptured) return;
        SeekPreview(TimelineOffsetSeconds + e.GetPosition(ruler).X / TimelinePixelsPerSecond);
        ruler.ReleaseMouseCapture();
        EndPreviewScrub();
        e.Handled = true;
    }

    private void RefreshPreviewControls()
    {
        if (!_previewInitialized || _previewShutdown) return;
        PreparePreviewButton.IsEnabled = !_previewPreparing && PreviewDuration > 0;
        PreviewPlayButton.IsEnabled = !_previewPreparing && PreviewDuration > 0;
        PreviewPlayButton.Content = _previewPlaying ? "暫停" : "播放";
        PreviewStopButton.IsEnabled = PreviewDuration > 0;
        PreviewCancelButton.Visibility = _previewPreparing ? Visibility.Visible : Visibility.Collapsed;
        PreviewCancelButton.IsEnabled = _previewPreparing;
        PreviewSeekSlider.IsEnabled = PreviewDuration > 0;
        RefreshPreviewTime();
    }
    private void RefreshPreviewTime()
    {
        if (!_previewInitialized || _previewShutdown) return;
        _updatingPreviewSeek = true;
        try
        {
            PreviewSeekSlider.Maximum = Math.Max(0.001, PreviewDuration);
            PreviewSeekSlider.Value = Math.Clamp(PlayheadSeconds, 0, PreviewSeekSlider.Maximum);
        }
        finally { _updatingPreviewSeek = false; }
        PreviewTimeText.Text = $"{FormatPreviewTime(PlayheadSeconds)} / {FormatPreviewTime(PreviewDuration)}";
    }
    private static string FormatPreviewTime(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds / 10:00}";
    }
    private static void DeletePreviewFile(string? file)
    {
        if (file is null) return;
        try { File.Delete(file); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal async Task RunPreviewSmokeAsync(string folder)
    {
        if (PreviewDuration < 0.5) throw new InvalidOperationException("Preview smoke requires a timeline at least half a second long.");
        var originalMuted = PreviewPlayer.IsMuted;
        PreviewPlayer.IsMuted = true;
        try
        {
            // Exercise a real outstanding request: invalidating must prevent its continuation
            // from loading obsolete media even if the encoder finishes concurrently.
            var obsolete = PreparePreviewAsync();
            InvalidatePreview();
            await obsolete;
            if (PreviewIsReady || PreviewPlayer.Source is not null)
                throw new InvalidOperationException("An obsolete preview was loaded after invalidation.");
            await PreparePreviewAsync();
            if (!PreviewIsReady || !PreviewPlayer.NaturalDuration.HasTimeSpan)
                throw new InvalidOperationException($"Native preview failed to open: {PreviewHint.Text}");
            var duration = PreviewDuration;
            var naturalDuration = PreviewPlayer.NaturalDuration.TimeSpan.TotalSeconds;
            File.Copy(_previewFile!, Path.Combine(folder, "preview-verified.mp4"), true);
            using var probeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var encodedDuration = await MediaProbe.ReadContainerDurationAsync(_previewFile!, probeTimeout.Token);
            if (Math.Abs(encodedDuration - duration) > 0.05)
                throw new InvalidOperationException($"Encoded preview duration mismatch: expected {duration}, actual {encodedDuration}.");
            var seek = Math.Min(1.2, duration / 3);
            SeekPreview(seek);
            await Task.Delay(150);
            var positionAfterSeek = PreviewPlayer.Position.TotalSeconds;
            if (Math.Abs(positionAfterSeek - seek) > 0.12)
                throw new InvalidOperationException($"Native preview seek mismatch: expected {seek}, actual {positionAfterSeek}.");
            StartPreviewPlayback();
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (PreviewPlayer.Position.TotalSeconds < seek + 0.15 && DateTime.UtcNow < deadline) await Task.Delay(100);
            var advanced = PreviewPlayer.Position.TotalSeconds;
            PausePreview();
            if (advanced < seek + 0.1 || PlayheadSeconds < seek + 0.05)
                throw new InvalidOperationException($"Native preview clock did not advance: {seek} -> {advanced}; UI {PlayheadSeconds}.");
            // NaturalDuration is rounded down to whole seconds by Windows for some MP4s.
            // Verify the native decoder still seeks and plays the fractional tail itself.
            var tailSeek = duration - Math.Min(.2, duration / 4);
            SeekPreview(tailSeek);
            await Task.Delay(150);
            var tailPosition = PreviewPlayer.Position.TotalSeconds;
            if (Math.Abs(tailPosition - tailSeek) > .12)
                throw new InvalidOperationException($"Native preview tail seek mismatch: expected {tailSeek}, actual {tailPosition}.");
            SeekPreview(0);
            StartPreviewPlayback();
            var endDeadline = DateTime.UtcNow.AddSeconds(duration + 5);
            var maxPosition = 0.0;
            while (_previewPlaying && DateTime.UtcNow < endDeadline)
            {
                await Task.Delay(80);
                maxPosition = Math.Max(maxPosition, PreviewPlayer.Position.TotalSeconds);
            }
            if (_previewPlaying || maxPosition < duration - .15 || Math.Abs(PlayheadSeconds - duration) > .001)
                throw new InvalidOperationException($"Native preview did not play its complete tail: expected {duration}, max playback {maxPosition}, playhead {PlayheadSeconds}.");
            var visibleClip = _project.Clips.FirstOrDefault(clip => _project.Tracks.Any(track =>
                track.Id == clip.TrackId && track.Kind == TrackKind.Video && !track.Muted));
            SeekPreview(visibleClip is null ? duration / 2 : visibleClip.Start + visibleClip.Duration / 2);
            await Task.Delay(200);
            File.WriteAllLines(Path.Combine(folder, "preview-result.txt"),
            [
                "PASS: stale render cancellation, rendered MP4 MediaOpened, precise FFprobe duration, native seek, fractional tail seek, full playback to end and playhead update.",
                $"Timeline duration {duration:0.000}s; encoded duration {encodedDuration:0.000}s; Windows NaturalDuration reports {naturalDuration:0.000}s.",
                $"Native seek {positionAfterSeek:0.000}s; playback advanced to {advanced:0.000}s.",
                $"Native tail seek {tailPosition:0.000}s; full playback reached {maxPosition:0.000}s before MediaEnded.",
                $"Rendered dimensions {PreviewPlayer.NaturalVideoWidth} x {PreviewPlayer.NaturalVideoHeight}."
            ]);
        }
        finally
        {
            PausePreview();
            PreviewPlayer.IsMuted = originalMuted;
        }
    }
}
