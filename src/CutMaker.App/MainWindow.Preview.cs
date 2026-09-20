using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
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
    private bool _previewPlayWhenReady;
    private bool _updatingPreviewSeek;
    private bool _previewScrubbing;
    private bool _resumeAfterPreviewScrub;
    private CancellationTokenSource? _previewFrameCancellation;
    private Task _previewFrameTask = Task.CompletedTask;
    private int _previewFrameRequest;
    private double _previewRangeStart;
    private double _previewRangeEnd;
    private double _previewPlaybackFarthest;
    internal Func<CutProject, string, MediaRenderOptions, CancellationToken, Task> RenderStillMediaAsync { get; set; } =
        async (project, output, options, token) => { await MediaRenderService.RenderAsync(project, null, output, options, cancellationToken: token); };

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
            {
                var position = PreviewPlayer.Position.TotalSeconds;
                _previewPlaybackFarthest = Math.Max(_previewPlaybackFarthest, position);
                PlayheadSeconds = Math.Clamp(_previewRangeStart + position, 0, PreviewDuration);
            }
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
            PreviewHint.Text = PreviewDuration > 0 ? "拖曳時間尺查看目前畫面\n按「播放」準備從播放頭到結尾的連續預覽" : "將素材放入軌道，開始編輯";
        RefreshPreviewControls();
    }

    /// <summary>Edits cancel stale playback and refresh one frame; continuous playback is prepared on request.</summary>
    internal void InvalidatePreview()
    {
        _previewRevision++;
        _previewFrameRequest++;
        _previewFrameCancellation?.Cancel();
        _previewCancellation?.Cancel();
        _previewOpenCompletion?.TrySetCanceled();
        _previewReadyRevision = -1;
        _previewOpened = false;
        _previewPlaying = false;
        _previewPlaybackFarthest = 0;
        _previewPreparing = false;
        _previewPlayWhenReady = false;
        _previewScrubbing = false;
        _resumeAfterPreviewScrub = false;
        if (!_previewInitialized || _previewShutdown) return;
        _previewTimer.Stop();
        PreviewPlayer.Close();
        PreviewPlayer.Source = null;
        PreviewStillImage.Source = null;
        PreviewStillImage.Visibility = Visibility.Collapsed;
        DeletePreviewFile(_previewFile);
        _previewFile = null;
        PlayheadSeconds = Math.Clamp(PlayheadSeconds, 0, PreviewDuration);
        PreviewHint.Text = PreviewDuration > 0 ? "正在更新目前畫面…" : "將素材放入軌道，開始編輯";
        PreviewHint.Visibility = Visibility.Visible;
        PreviewRangeText.Text = "拖曳定位會自動更新畫面";
        RefreshPreviewControls();
        QueuePreviewFrame();
    }

    internal void ShutdownPreview()
    {
        if (_previewShutdown) return;
        InvalidatePreview();
        _previewShutdown = true;
        _previewFrameCancellation?.Cancel();
        _previewTimer.Stop();
    }

    private async void PreparePreview_Click(object sender, RoutedEventArgs e) => await PreparePreviewAsync();
    private async void PreviewPlay_Click(object sender, RoutedEventArgs e) => await TogglePreviewPlaybackAsync();
    private async Task TogglePreviewPlaybackAsync()
    {
        EndPreviewScrub(resumePlayback: false);
        if (_previewPreparing)
        {
            _previewPlayWhenReady = !_previewPlayWhenReady;
            StatusText.Text = _previewPlayWhenReady ? "預覽完成後將自動播放 · 空白鍵取消待播" : "已取消待播 · 預覽完成後保持暫停";
            RefreshPreviewControls();
            return;
        }
        if (!PreviewIsReady || !PreviewContains(PlayheadSeconds)) { await PreparePreviewAsync(playWhenReady: true); return; }
        if (_previewPlaying) PausePreview();
        else StartPreviewPlayback();
    }
    private void PreviewStop_Click(object sender, RoutedEventArgs e)
    {
        // Cancel an outstanding Play request even when its window already includes zero.
        if (_previewPreparing) InvalidatePreview();
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
        _previewFrameCancellation?.Cancel();
        var revision = _previewRevision;
        if (PlayheadSeconds >= PreviewDuration - 0.00001) PlayheadSeconds = 0;
        _previewRangeStart = PlayheadSeconds;
        // One continuous file/decoder clock: no recurring render or player swap at 20-second boundaries.
        _previewRangeEnd = PreviewDuration;
        using var cancellation = new CancellationTokenSource();
        _previewCancellation = cancellation;
        _previewPreparing = true;
        _previewPlayWhenReady = playWhenReady;
        var folder = Path.GetFullPath(Path.Combine(LayoutSettings.DataDirectory, "preview", _previewSessionId));
        var output = Path.Combine(folder, $"preview-{revision}.mp4");
        PreviewHint.Text = "正在產生多軌預覽… 0%";
        PreviewRangeText.Text = $"播放區間 {FormatPreviewTime(_previewRangeStart)} – {FormatPreviewTime(_previewRangeEnd)}";
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
            await MediaRenderService.RenderAsync(snapshot, null, output, new MediaRenderOptions(Preview: true,
                OutputStart: _previewRangeStart, OutputEnd: _previewRangeEnd), progress, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (_previewShutdown || revision != _previewRevision) return;
            ReplacePreviewPlayer();
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
            StatusText.Text = "連續預覽已準備，可播放至結尾；編輯或定位到快取之前需重新準備";
            if (_previewPlayWhenReady) StartPreviewPlayback();
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
                _previewPlayWhenReady = false;
                RefreshPreviewControls();
            }
        }
    }

    private void ReplacePreviewPlayer()
    {
        // WPF media events do not identify the URI that raised them. An old source can
        // queue MediaOpened/MediaEnded before Close, then deliver it after a new Source.
        // Give each rendered source its own sender so those events are distinguishable.
        var previous = PreviewPlayer;
        var panel = (Panel)previous.Parent;
        var index = panel.Children.IndexOf(previous);
        var player = new MediaElement
        {
            Name = nameof(PreviewPlayer), LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual,
            Stretch = previous.Stretch, IsMuted = previous.IsMuted, Volume = 0, ScrubbingEnabled = true,
            HorizontalAlignment = previous.HorizontalAlignment, VerticalAlignment = previous.VerticalAlignment,
            IsHitTestVisible = previous.IsHitTestVisible
        };
        Grid.SetRow(player, Grid.GetRow(previous)); Grid.SetColumn(player, Grid.GetColumn(previous));
        Grid.SetRowSpan(player, Grid.GetRowSpan(previous)); Grid.SetColumnSpan(player, Grid.GetColumnSpan(previous));
        Panel.SetZIndex(player, Panel.GetZIndex(previous));
        previous.MediaOpened -= PreviewPlayer_MediaOpened;
        previous.MediaEnded -= PreviewPlayer_MediaEnded;
        previous.MediaFailed -= PreviewPlayer_MediaFailed;
        previous.Close(); previous.Source = null;
        panel.Children.RemoveAt(index);
        UnregisterName(nameof(PreviewPlayer));
        PreviewPlayer = player;
        RegisterName(nameof(PreviewPlayer), player);
        player.MediaOpened += PreviewPlayer_MediaOpened;
        player.MediaEnded += PreviewPlayer_MediaEnded;
        player.MediaFailed += PreviewPlayer_MediaFailed;
        panel.Children.Insert(index, player);
    }

    private void PreviewPlayer_MediaOpened(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(sender, PreviewPlayer) || _previewOpened || _previewShutdown || _previewReadyRevision != _previewRevision || _previewFile is null ||
            !string.Equals(PreviewPlayer.Source?.LocalPath, _previewFile, StringComparison.OrdinalIgnoreCase)) return;
        _previewOpened = true;
        PreviewPlayer.Volume = 1;
        PreviewPlayer.Position = TimeSpan.FromSeconds(Math.Clamp(PlayheadSeconds - _previewRangeStart, 0, _previewRangeEnd - _previewRangeStart));
        PreviewPlayer.Pause();
        PreviewHint.Visibility = Visibility.Collapsed;
        PreviewStillImage.Visibility = Visibility.Collapsed;
        _previewOpenCompletion?.TrySetResult(true);
        RefreshPreviewControls();
    }
    private void PreviewPlayer_MediaEnded(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(sender, PreviewPlayer) || !PreviewIsReady || !_previewPlaying || _previewScrubbing) return;
        // Also reject a queued end from an earlier seek of this same player. Use the
        // exact rendered range, since Windows can truncate NaturalDuration to seconds.
        var duration = _previewRangeEnd - _previewRangeStart;
        // At real EOF Windows may reset Position to the truncated NaturalDuration.
        // A clock sample from this Play/Seek session can still prove the full tail ran.
        var reached = Math.Max(PreviewPlayer.Position.TotalSeconds, _previewPlaybackFarthest);
        if (Math.Abs(reached - duration) > Math.Min(.12, duration / 4)) return;
        PausePreview();
        PlayheadSeconds = _previewRangeEnd;
    }
    private void PreviewPlayer_MediaFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        if (!ReferenceEquals(sender, PreviewPlayer) || _previewShutdown || _previewReadyRevision != _previewRevision) return;
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
        if (PlayheadSeconds >= PreviewDuration - 0.00001) { _ = PreparePreviewAsync(playWhenReady: true); return; }
        if (!PreviewContains(PlayheadSeconds)) return;
        _previewFrameCancellation?.Cancel();
        PreviewStillImage.Visibility = Visibility.Collapsed;
        _previewPlaybackFarthest = Math.Max(0, PlayheadSeconds - _previewRangeStart);
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

    private bool PreviewContains(double seconds) => seconds >= _previewRangeStart - 0.00001 && seconds < _previewRangeEnd - 0.00000001;

    private void QueuePreviewFrame()
    {
        _previewFrameCancellation?.Cancel();
        if (!_previewInitialized || _previewShutdown || PreviewDuration <= 0 || _previewPreparing) return;
        var cancellation = new CancellationTokenSource();
        _previewFrameCancellation = cancellation;
        _previewFrameTask = RenderPreviewFrameAsync(++_previewFrameRequest, _previewRevision, PlayheadSeconds, cancellation);
    }

    private async Task RenderPreviewFrameAsync(int request, int revision, double seconds, CancellationTokenSource cancellation)
    {
        string? output = null;
        try
        {
            // Coalesce mouse motion; canceled requests never publish their frame or status.
            await Task.Delay(110, cancellation.Token);
            var duration = PreviewDuration;
            var fps = Math.Min(30, _project.Video.Fps);
            var time = Math.Max(0, Math.Min(seconds, TimelineNavigation.StepFrame(duration, -1, _project.Video.Fps, duration)));
            var end = Math.Min(duration, time + 2 / fps);
            var snapshot = _project with
            {
                MediaAssets = _project.MediaAssets.Select(asset => asset with
                {
                    Path = _projectPath is null ? Path.GetFullPath(asset.Path) : ProjectStore.ResolveAssetPath(_projectPath, asset)
                }).ToList(), Tracks = [.. _project.Tracks], Clips = [.. _project.Clips]
            };
            var folder = Path.GetFullPath(Path.Combine(LayoutSettings.DataDirectory, "preview", _previewSessionId));
            Directory.CreateDirectory(folder);
            output = Path.Combine(folder, $"frame-{request}.png");
            await RenderStillMediaAsync(snapshot, output,
                new MediaRenderOptions(Preview: true, Width: Math.Min(640, _project.Video.Width),
                    OutputStart: time, OutputEnd: end, FrameOnly: true), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (_previewShutdown || revision != _previewRevision || request != _previewFrameRequest) return;
            var bitmap = new BitmapImage();
            using (var stream = File.OpenRead(output))
            {
                bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
            }
            PreviewStillImage.Source = bitmap;
            PreviewStillImage.Visibility = Visibility.Visible;
            PreviewHint.Visibility = Visibility.Collapsed;
            PreviewRangeText.Text = $"目前畫面 {FormatPreviewTime(seconds)} · 播放時準備到結尾的連續預覽";
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!_previewShutdown && !cancellation.IsCancellationRequested && revision == _previewRevision && request == _previewFrameRequest)
            {
                PreviewHint.Text = $"無法讀取目前畫面\n{error.Message}";
                PreviewHint.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            DeletePreviewFile(output);
            if (ReferenceEquals(_previewFrameCancellation, cancellation)) _previewFrameCancellation = null;
            cancellation.Dispose();
        }
    }

    internal void SeekPreview(double seconds)
    {
        if (!double.IsFinite(seconds)) return;
        PlayheadSeconds = Math.Clamp(seconds, 0, PreviewDuration);
        _previewPlaybackFarthest = Math.Max(0, PlayheadSeconds - _previewRangeStart);
        if (_previewPreparing && !PreviewContains(PlayheadSeconds)) InvalidatePreview();
        if (PreviewIsReady && PreviewContains(PlayheadSeconds))
        {
            _previewFrameCancellation?.Cancel();
            PreviewStillImage.Visibility = Visibility.Collapsed;
            PreviewHint.Visibility = Visibility.Collapsed;
            PreviewPlayer.Position = TimeSpan.FromSeconds(PlayheadSeconds - _previewRangeStart);
        }
        else
        {
            PausePreview();
            QueuePreviewFrame();
        }
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
        _previewPlayWhenReady = false;
        _resumeAfterPreviewScrub = _previewPlaying;
        _previewScrubbing = true;
        PausePreview();
    }
    private void EndPreviewScrub(bool resumePlayback = true)
    {
        if (!_previewScrubbing) return;
        _previewScrubbing = false;
        var resume = resumePlayback && _resumeAfterPreviewScrub;
        _resumeAfterPreviewScrub = false;
        if (resume && PreviewIsReady && PreviewContains(PlayheadSeconds)) StartPreviewPlayback();
        else if (resume) _ = PreparePreviewAsync(playWhenReady: true);
    }
    private void TimelineRuler_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var ruler = (UIElement)sender;
        Keyboard.Focus(ruler);
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
        PreparePreviewButton.Visibility = _previewPreparing ? Visibility.Collapsed : Visibility.Visible;
        PreviewPlayButton.IsEnabled = PreviewDuration > 0;
        PreviewPlayButton.Content = _previewPreparing && _previewPlayWhenReady ? "待播" : _previewPlaying ? "暫停" : "播放";
        PreviewPlayButton.ToolTip = _previewPreparing
            ? (_previewPlayWhenReady ? "準備完成後自動播放；空白鍵取消待播" : "按下或空白鍵，準備完成後自動播放")
            : "播放／暫停 · 空白鍵";
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
            SeekPreview(0);
            var stoppedPlayRequest = PreparePreviewAsync(playWhenReady: true);
            if (!_previewPreparing) throw new InvalidOperationException("Stop regression requires an outstanding Play preparation.");
            PreviewStop_Click(this, new RoutedEventArgs());
            await stoppedPlayRequest;
            await _previewFrameTask;
            if (_previewPreparing || _previewPlaying || PreviewIsReady || PreviewPlayer.Source is not null || PlayheadSeconds != 0)
                throw new InvalidOperationException("Stop during preparation must cancel pending auto-play and keep the playhead at zero.");
            SeekPreview(Math.Min(.6, PreviewDuration / 2));
            var staleFrame = _previewFrameTask;
            SeekPreview(Math.Min(.8, PreviewDuration / 2));
            await Task.WhenAll(staleFrame, _previewFrameTask);
            if (PreviewStillImage.Source is not BitmapSource || PreviewStillImage.Visibility != Visibility.Visible ||
                PreviewHint.Visibility != Visibility.Collapsed || !PreviewRangeText.Text.Contains(FormatPreviewTime(PlayheadSeconds), StringComparison.Ordinal))
                throw new InvalidOperationException($"Seek must display a current frame before a full playback render: {PreviewHint.Text}");
            SeekPreview(PreviewDuration);
            await _previewFrameTask;
            if (PreviewStillImage.Source is not BitmapSource || PreviewHint.Visibility != Visibility.Collapsed ||
                !PreviewRangeText.Text.Contains(FormatPreviewTime(PreviewDuration), StringComparison.Ordinal))
                throw new InvalidOperationException("Seeking to EOF must show the final frame instead of producing an empty PNG.");
            SeekPreview(0);
            var obsoletePlayer = PreviewPlayer;
            await PreparePreviewAsync();
            if (!PreviewIsReady || !PreviewPlayer.NaturalDuration.HasTimeSpan)
                throw new InvalidOperationException($"Native preview failed to open: {PreviewHint.Text}");
            if (ReferenceEquals(obsoletePlayer, PreviewPlayer) || !ReferenceEquals(FindName(nameof(PreviewPlayer)), PreviewPlayer) || !PreviewPlayer.IsMuted)
                throw new InvalidOperationException("Preview sources must have distinct native senders while retaining namescope and mute state.");
            var originalStillRenderer = RenderStillMediaAsync;
            var delayedFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var frameStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                // Model an encoder failure already in flight when the user switches to valid cached video.
                // Its continuation has the same request/revision, so cancellation itself must suppress the error.
                RenderStillMediaAsync = (_, _, _, _) => { frameStarted.TrySetResult(); return delayedFailure.Task; };
                QueuePreviewFrame();
                await frameStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                SeekPreview(Math.Min(.2, PreviewDuration / 2));
                var cachedHint = PreviewHint.Text;
                delayedFailure.TrySetException(new IOException("Delayed canceled frame failure"));
                await _previewFrameTask;
                if (!PreviewIsReady || PreviewHint.Visibility != Visibility.Collapsed || PreviewHint.Text != cachedHint)
                    throw new InvalidOperationException("A canceled frame failure replaced valid cached preview state.");
            }
            finally
            {
                delayedFailure.TrySetCanceled();
                RenderStillMediaAsync = originalStillRenderer;
            }
            await RunTransportSmokeAsync(folder);
            SeekPreview(0);
            await PreparePreviewAsync();
            var duration = PreviewDuration;
            var naturalDuration = PreviewPlayer.NaturalDuration.TimeSpan.TotalSeconds;
            var naturalWidth = PreviewPlayer.NaturalVideoWidth;
            var naturalHeight = PreviewPlayer.NaturalVideoHeight;
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
            PreviewPlayer_MediaEnded(PreviewPlayer, new RoutedEventArgs());
            PreviewPlayer_MediaOpened(obsoletePlayer, new RoutedEventArgs());
            PreviewPlayer_MediaEnded(obsoletePlayer, new RoutedEventArgs());
            if (_previewPlaying || Math.Abs(PlayheadSeconds - seek) > .01)
                throw new InvalidOperationException("Paused or old-source native events changed the current playhead.");
            StartPreviewPlayback();
            PreviewPlayer_MediaEnded(PreviewPlayer, new RoutedEventArgs());
            PreviewPlayer_MediaOpened(PreviewPlayer, new RoutedEventArgs());
            PreviewPlayer_MediaOpened(obsoletePlayer, new RoutedEventArgs());
            PreviewPlayer_MediaEnded(obsoletePlayer, new RoutedEventArgs());
            if (!_previewPlaying || Math.Abs(PlayheadSeconds - seek) > .12)
                throw new InvalidOperationException("Early End or duplicate/stale Open events interrupted active playback.");
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
            // One file spans the remainder: a cut at the old 20-second boundary must not swap players.
            var normalProject = _project;
            try
            {
                _project = _project with
                {
                    Video = _project.Video with { Width = 160, Height = 90 },
                    Tracks = [.. _project.Tracks, new("preview-long-muted", "Long muted tail", TrackKind.Video, Muted: true)],
                    MediaAssets = [.. _project.MediaAssets, new("preview-long-muted", Path.Combine(folder, "unused-muted.mp4"), MediaKind.Video, 65)],
                    Clips = [.. _project.Clips, new("preview-long-muted", "preview-long-muted", "preview-long-muted", 0, 0, 65)]
                };
                InvalidatePreview(); SeekPreview(21.25);
                await PreparePreviewAsync();
                if (!PreviewIsReady || Math.Abs(_previewRangeStart - 21.25) > 1e-8 || Math.Abs(_previewRangeEnd - 65) > 1e-8)
                    throw new InvalidOperationException($"Continuous preview did not reach the project end: {_previewRangeStart} - {_previewRangeEnd}; {PreviewHint.Text}");
                using var rangeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var continuousDuration = await MediaProbe.ReadContainerDurationAsync(_previewFile!, rangeTimeout.Token);
                if (Math.Abs(continuousDuration - 43.75) > .05)
                    throw new InvalidOperationException($"Continuous preview encoded {continuousDuration}s instead of 43.75s.");
                SeekPreview(22.25);
                await Task.Delay(150);
                if (Math.Abs(PreviewPlayer.Position.TotalSeconds - 1) > .12)
                    throw new InvalidOperationException("Global playback seek was not translated into local preview-window time.");
                StartPreviewPlayback(); await Task.Delay(500); PausePreview();
                if (PlayheadSeconds < 22.35 || PlayheadSeconds > 23.25)
                    throw new InvalidOperationException($"Continuous preview lost its global playhead offset: {PlayheadSeconds}.");
                var continuousPlayer = PreviewPlayer;
                var continuousFile = _previewFile;
                SeekPreview(40.95);
                StartPreviewPlayback();
                var continuityDeadline = DateTime.UtcNow.AddSeconds(5);
                while (PlayheadSeconds < 41.65 && DateTime.UtcNow < continuityDeadline) await Task.Delay(50);
                if (!_previewPlaying || _previewPreparing || !ReferenceEquals(continuousPlayer, PreviewPlayer) ||
                    _previewFile != continuousFile || PlayheadSeconds < 41.65)
                    throw new InvalidOperationException("Playback stopped or swapped players at the former 20-second boundary.");
                PausePreview();
            }
            finally { _project = normalProject; InvalidatePreview(); }
            RunOutputSettingsSmoke(folder);
            SeekPreview(visibleClip is null ? duration / 2 : visibleClip.Start + visibleClip.Duration / 2);
            await _previewFrameTask;
            File.WriteAllLines(Path.Combine(folder, "preview-result.txt"),
            [
                "PASS: stale render/frame cancellation, immediate seek and EOF still frames, rendered MP4 MediaOpened, precise FFprobe duration, native seek, fractional tail seek, full playback to end and playhead update.",
                "PASS: a 65-second timeline prepares one continuous file from the playhead to the end; native playback crosses the old 20-second boundary without stopping or swapping players, retaining global-to-local offsets.",
                "PASS: Stop cancels pending auto-play even inside its prepared range; canceled late frame errors cannot replace cached playback state.",
                "PASS: paused, early and old-source MediaEnded plus duplicate/stale MediaOpened events cannot reset the current native playback clock.",
                $"Timeline duration {duration:0.000}s; encoded duration {encodedDuration:0.000}s; Windows NaturalDuration reports {naturalDuration:0.000}s.",
                $"Native seek {positionAfterSeek:0.000}s; playback advanced to {advanced:0.000}s.",
                $"Native tail seek {tailPosition:0.000}s; full playback reached {maxPosition:0.000}s before MediaEnded.",
                $"Rendered dimensions {naturalWidth} x {naturalHeight}."
            ]);
        }
        finally
        {
            PausePreview();
            PreviewPlayer.IsMuted = originalMuted;
        }
    }
}
