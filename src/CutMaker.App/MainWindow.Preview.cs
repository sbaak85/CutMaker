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
    private PreviewRenderCache? _previewCache;
    private string? _previewRenderKey;
    private AudioTimelinePreview? _audioPreview;
    private PcmAudioDevice? _audioDevice;
    private double PreviewPositionSeconds => _audioDevice is { } audio
        ? audio.PositionFrames / (double)AudioSampleClock.Rate : PreviewPlayer.Position.TotalSeconds;
    private string PreviewFolder => Path.GetFullPath(Path.Combine(LayoutSettings.DataDirectory, "preview", _previewSessionId));
    private PreviewRenderCache PreviewCache => _previewCache ??= new(PreviewFolder);
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
                if (_audioDevice?.Error is { } audioError)
                {
                    ReportAudioPreviewFailure(audioError);
                    return;
                }
                MediaWorkScheduler.NotifyInteraction();
                PreviewHint.Visibility = _audioDevice?.IsBuffering == true ? Visibility.Visible : Visibility.Collapsed;
                if (_audioDevice?.IsBuffering == true) PreviewHint.Text = "正在補充來源音訊，完成後接續播放…";
                var position = PreviewPositionSeconds;
                _previewPlaybackFarthest = Math.Max(_previewPlaybackFarthest, position);
                PlayheadSeconds = Math.Clamp(_previewRangeStart + position, 0, PreviewDuration);
                if (CheckAuditionEnd(_previewRangeStart + position)) return;
                if (_audioDevice is { Ended: true })
                {
                    PausePreview();
                    PlayheadSeconds = PreviewDuration;
                }
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
            PreviewHint.Text = PreviewDuration <= 0 ? "將素材放入軌道，開始編輯" : PreviewRenderCache.IsAudioOnly(_project)
                ? "音訊專案 · 按播放直接試聽\n首次載入曲目後可直接定位與重播"
                : "拖曳時間尺定位\n按「播放」準備完整預覽，之後可直接定位與重播";
        RefreshPreviewControls();
    }

    private CutProject CreatePreviewSnapshot() => _project with
    {
        MediaAssets = _project.MediaAssets.Select(asset => asset with
        {
            Path = _projectPath is null ? Path.GetFullPath(asset.Path) : ProjectStore.ResolveAssetPath(_projectPath, asset)
        }).ToList(),
        Tracks = [.. _project.Tracks], Clips = [.. _project.Clips]
    };

    private static string PreviewContentKey(CutProject snapshot) => PreviewRenderCache.CreateKey(snapshot) +
        (PreviewRenderCache.IsAudioOnly(snapshot) ? "" : $"|{EditorPreferences.Current.PreviewWidth}|{EditorPreferences.Current.UseVideoProxies}");

    // Names, locks, selection and lossless razor cuts do not change the rendered sound.
    // Keep both the playing clock and an in-flight preload when the content is identical.
    private void RefreshPreviewAfterEdit()
    {
        if (_previewRenderKey is not null && (PreviewIsReady || _previewPreparing) &&
            _previewRenderKey == PreviewContentKey(CreatePreviewSnapshot()))
        { RefreshPreviewControls(); return; }
        if (_previewPlaying && _audioPreview is { } live && _audioDevice is { } device &&
            live.TryUpdateControls(CreatePreviewSnapshot(), device.PositionFrames + PcmAudioDevice.BlockFrames * PcmAudioDevice.QueueBlocks))
        {
            _previewRenderKey = PreviewContentKey(CreatePreviewSnapshot());
            RefreshPreviewControls(); return;
        }
        InvalidatePreview();
    }

    /// <summary>Stop stale playback, retaining finished content caches for replay/undo.</summary>
    internal void InvalidatePreview()
    {
        ClearAudition(resetDevice: false);
        _previewRevision++;
        _previewFrameRequest++;
        _previewFrameCancellation?.Cancel();
        _previewCancellation?.Cancel();
        _previewOpenCompletion?.TrySetCanceled();
        _previewReadyRevision = -1;
        _previewRenderKey = null;
        _previewOpened = false;
        _previewPlaying = false;
        _previewPlaybackFarthest = 0;
        _previewPreparing = false;
        _previewPlayWhenReady = false;
        _previewScrubbing = false;
        _resumeAfterPreviewScrub = false;
        if (!_previewInitialized || _previewShutdown) return;
        _previewTimer.Stop();
        CloseAudioPreview();
        PreviewPlayer.Close();
        PreviewPlayer.Source = null;
        PreviewStillImage.Source = null;
        PreviewStillImage.Visibility = Visibility.Collapsed;
        if (_previewFile is not null && !PreviewCache.ContainsFile(_previewFile)) DeletePreviewFile(_previewFile);
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
        _previewCache?.Clear();
    }

    private async void PreparePreview_Click(object sender, RoutedEventArgs e) => await PreparePreviewAsync();
    private async void PreviewPlay_Click(object sender, RoutedEventArgs e) => await TogglePreviewPlaybackAsync();
    private async Task TogglePreviewPlaybackAsync()
    {
        _auditionRequest++;
        EndPreviewScrub(resumePlayback: false);
        if (_previewRenderKey is not null && _previewRenderKey != PreviewContentKey(CreatePreviewSnapshot()))
            InvalidatePreview();
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
        ClearAudition();
        // Stop the playback intent, but let the reusable preload finish in the background.
        _previewPlayWhenReady = false;
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
        using var foreground = MediaWorkScheduler.Foreground();
        var snapshot = CreatePreviewSnapshot();
        var key = PreviewContentKey(snapshot);
        if (PreviewIsReady && _previewRenderKey == key)
        {
            if (PlayheadSeconds >= PreviewDuration - 0.00001) SeekPreview(0);
            if (playWhenReady) StartPreviewPlayback();
            return;
        }
        InvalidatePreview();
        _previewFrameCancellation?.Cancel();
        var revision = _previewRevision;
        if (PlayheadSeconds >= PreviewDuration - 0.00001) PlayheadSeconds = 0;
        _previewRenderKey = key;
        _previewRangeStart = 0;
        // Cache the entire timeline: backward seeks and replay share one file/decoder.
        _previewRangeEnd = PreviewDuration;
        using var cancellation = new CancellationTokenSource();
        _previewCancellation = cancellation;
        _previewPreparing = true;
        _previewPlayWhenReady = playWhenReady;
        var folder = PreviewFolder;
        var audioOnly = PreviewRenderCache.IsAudioOnly(snapshot);
        var output = string.Empty;
        var reused = !audioOnly && PreviewCache.TryGet(key, out output);
        if (!audioOnly && !reused) output = Path.Combine(folder, $"preview-{revision}.mp4");
        var preparingLabel = audioOnly ? "正在載入曲目" : "正在準備完整影音快取";
        PreviewHint.Text = reused ? "正在開啟已暫存的預覽…" : $"{preparingLabel}… 0%";
        PreviewRangeText.Text = $"播放區間 {FormatPreviewTime(_previewRangeStart)} – {FormatPreviewTime(_previewRangeEnd)}";
        StatusText.Text = audioOnly ? "首次解碼曲目後直接即時混音播放，無須製作整首混音檔" : "首次準備後可直接定位與重播；可繼續定位，修改影音內容才需更新";
        RefreshPreviewControls();
        var phase = audioOnly ? "載入音訊來源" : "製作影音快取";
        var started = System.Diagnostics.Stopwatch.StartNew();
        PreviewDiagnostics.Write("prepare-start", $"revision={revision}; audioOnly={audioOnly}; duration={PreviewDuration:0.###}; videoRenderCacheHit={reused}");
        try
        {
            Directory.CreateDirectory(folder);
            var progress = new Progress<MediaRenderProgress>(value =>
            {
                if (_previewShutdown || revision != _previewRevision || cancellation.IsCancellationRequested) return;
                PreviewHint.Text = $"{preparingLabel}… {Math.Clamp(value.Fraction, 0, 1):P0}\n{value.Message}";
            });
            if (audioOnly)
            {
                // Yield once so pending transport/navigation input remains responsive even
                // when all decoded source files are already cached.
                await Task.Yield();
                var audio = await AudioTimelinePreview.PrepareAsync(snapshot, progress, cancellation.Token, startSeconds: PlayheadSeconds);
                try
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (_previewShutdown || revision != _previewRevision) return;
                    if (key != PreviewContentKey(snapshot)) throw new IOException("來源檔案在載入期間已變更，請重新播放。");
                    phase = "開啟音訊裝置";
                    var device = new PcmAudioDevice((frame, buffer, count) => { audio.ReadFrames(frame, buffer, count); },
                        audio.DurationFrames, muted: PreviewPlayer.IsMuted);
                    _audioPreview = audio;
                    _audioDevice = device;
                    device.Seek(AudioSampleClock.At(PlayheadSeconds));
                    _previewReadyRevision = revision;
                    _previewOpened = true;
                    PreviewStillImage.Visibility = Visibility.Collapsed;
                    PreviewHint.Visibility = Visibility.Collapsed;
                    PreviewRangeText.Text = "音訊即時混音 · 可直接定位與重播";
                    StatusText.Text = "音訊已就緒 · 直接即時混音播放";
                    PreviewDiagnostics.Write("audio-ready", $"revision={revision}; elapsedMs={started.ElapsedMilliseconds}");
                    if (_previewPlayWhenReady) StartPreviewPlayback();
                    return;
                }
                finally { if (!ReferenceEquals(_audioPreview, audio)) audio.Dispose(); }
            }
            if (!reused)
                await VideoRegionPreview.RenderAsync(snapshot, output, EditorPreferences.Current.PreviewWidth,
                    EditorPreferences.Current.UseVideoProxies, progress, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (_previewShutdown || revision != _previewRevision) return;
            if (key != PreviewContentKey(snapshot))
                throw new IOException("來源檔案在準備期間已變更，請重新播放以更新快取。");
            PreviewCache.Add(key, output);
            ReplacePreviewPlayer();
            _previewFile = output;
            _previewReadyRevision = revision;
            _previewOpenCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            PreviewHint.Text = "正在開啟預覽…";
            PreviewPlayer.Volume = 0;
            PreviewPlayer.Source = new Uri(output, UriKind.Absolute);
            // Allow the native graph to reach MediaOpened before pausing it. It stays
            // inaudible until the current sender confirms that loading has completed.
            phase = "開啟影片播放器";
            PreviewDiagnostics.Write("video-opening", $"revision={revision}; elapsedMs={started.ElapsedMilliseconds}; file={output}");
            PreviewPlayer.Play();
            await _previewOpenCompletion.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellation.Token);
            if (_previewShutdown || revision != _previewRevision) return;
            if (key != PreviewContentKey(snapshot))
                throw new IOException("來源檔案在開啟期間已變更，請重新播放以更新快取。");
            StatusText.Text = reused ? "已重用預覽快取，可直接定位與重播" : "完整預覽已暫存，可直接定位與重播";
            PreviewDiagnostics.Write("video-ready", $"revision={revision}; elapsedMs={started.ElapsedMilliseconds}");
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
            PreviewDiagnostics.Write("prepare-failed", $"phase={phase}; revision={revision}; elapsedMs={started.ElapsedMilliseconds}; {ex}");
            if (!_previewShutdown && revision == _previewRevision)
            {
                CloseFailedPreview();
                PreviewHint.Text = ex is TimeoutException ? $"{phase}逾時\n影片已處理完成，但播放器未能開啟。請重試；詳細原因已記錄。" : $"{phase}失敗\n{ex.Message}";
                StatusText.Text = "預覽失敗；剪輯資料仍保留，可重新嘗試";
            }
        }
        finally
        {
            if (output.Length > 0 && !string.Equals(_previewFile, output, StringComparison.OrdinalIgnoreCase) && !PreviewCache.ContainsFile(output)) DeletePreviewFile(output);
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
        if (_audioDevice is not null || !ReferenceEquals(sender, PreviewPlayer) || !PreviewIsReady || !_previewPlaying || _previewScrubbing) return;
        // Also reject a queued end from an earlier seek of this same player. Use the
        // exact rendered range, since Windows can truncate NaturalDuration to seconds.
        var duration = _previewRangeEnd - _previewRangeStart;
        // At real EOF Windows may reset Position to the truncated NaturalDuration.
        // A clock sample from this Play/Seek session can still prove the full tail ran.
        var reached = Math.Max(PreviewPlayer.Position.TotalSeconds, _previewPlaybackFarthest);
        if (Math.Abs(reached - duration) > Math.Min(.12, duration / 4)) return;
        if (CheckAuditionEnd(_previewRangeEnd)) return;
        PausePreview();
        PlayheadSeconds = _previewRangeEnd;
    }
    private void PreviewPlayer_MediaFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        if (_audioDevice is not null || _previewFile is null || !ReferenceEquals(sender, PreviewPlayer) || _previewShutdown || _previewReadyRevision != _previewRevision) return;
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
        CloseAudioPreview();
        PreviewPlayer.Close();
        PreviewPlayer.Source = null;
        if (_previewFile is not null) PreviewCache.RemoveFile(_previewFile);
        DeletePreviewFile(_previewFile);
        _previewFile = null;
        PreviewHint.Visibility = Visibility.Visible;
    }

    private void CloseAudioPreview()
    {
        var device = _audioDevice;
        var mixer = _audioPreview;
        _audioDevice = null;
        _audioPreview = null;
        mixer?.CancelPendingReads();
        try { device?.Dispose(); }
        catch (Exception ex) { PreviewDiagnostics.Write("audio-close-failed", ex.ToString()); }
        finally { mixer?.Dispose(); }
    }

    private void ReportAudioPreviewFailure(Exception error)
    {
        CloseFailedPreview();
        PreviewHint.Text = $"音訊播放失敗\n{error.Message}";
        StatusText.Text = "請確認音訊輸出裝置後重新播放；詳細原因已記錄";
        PreviewDiagnostics.Write("audio-playback-failed", error.ToString());
        RefreshPreviewControls();
    }

    private void StartPreviewPlayback()
    {
        if (!PreviewIsReady) return;
        if (PlayheadSeconds >= PreviewDuration - 0.00001) SeekPreview(0);
        if (!PreviewContains(PlayheadSeconds)) return;
        _previewFrameCancellation?.Cancel();
        PreviewStillImage.Visibility = Visibility.Collapsed;
        PreviewHint.Visibility = Visibility.Collapsed;
        _previewPlaybackFarthest = Math.Max(0, PlayheadSeconds - _previewRangeStart);
        try
        {
            if (_audioDevice is { } audio) audio.Resume();
            else PreviewPlayer.Play();
        }
        catch (Exception ex) when (_audioDevice is not null && ex is InvalidOperationException or IOException)
        { ReportAudioPreviewFailure(ex); return; }
        _previewPlaying = true;
        _previewTimer.Start();
        RefreshPreviewControls();
    }
    private void PausePreview()
    {
        if (!_previewInitialized || _previewShutdown) return;
        if (PreviewIsReady)
        {
            try
            {
                if (_audioDevice is { } audio) audio.Pause();
                else PreviewPlayer.Pause();
            }
            catch (Exception ex) when (_audioDevice is not null && ex is InvalidOperationException or IOException)
            { ReportAudioPreviewFailure(ex); return; }
        }
        _previewPlaying = false;
        _previewTimer.Stop();
        RefreshPreviewControls();
    }

    private bool PreviewContains(double seconds) => seconds >= _previewRangeStart - 0.00001 && seconds < _previewRangeEnd - 0.00000001;

    private void QueuePreviewFrame()
    {
        MediaWorkScheduler.NotifyInteraction();
        _previewFrameCancellation?.Cancel();
        if (!_previewInitialized || _previewShutdown || PreviewDuration <= 0 || _previewPreparing) return;
        if (PreviewRenderCache.IsAudioOnly(_project))
        {
            PreviewStillImage.Source = null;
            PreviewStillImage.Visibility = Visibility.Collapsed;
            PreviewHint.Text = PreviewIsReady ? "音訊已就緒" : "音訊專案 · 按播放直接試聽";
            PreviewHint.Visibility = PreviewIsReady ? Visibility.Collapsed : Visibility.Visible;
            PreviewRangeText.Text = $"音訊定位 {FormatPreviewTime(PlayheadSeconds)}";
            _previewFrameTask = Task.CompletedTask;
            return;
        }
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
            using var foreground = MediaWorkScheduler.Foreground();
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
            PreviewRangeText.Text = $"目前畫面 {FormatPreviewTime(seconds)} · 播放時重用完整快取";
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

    internal void SeekPreview(double seconds, bool preserveAudition = false)
    {
        if (!double.IsFinite(seconds)) return;
        if (!preserveAudition) { _auditionRequest++; ClearAudition(); }
        PlayheadSeconds = Math.Clamp(seconds, 0, PreviewDuration);
        _previewPlaybackFarthest = Math.Max(0, PlayheadSeconds - _previewRangeStart);
        if (PreviewIsReady && (PreviewContains(PlayheadSeconds) || PlayheadSeconds == _previewRangeEnd))
        {
            _previewFrameCancellation?.Cancel();
            PreviewStillImage.Visibility = Visibility.Collapsed;
            PreviewHint.Visibility = Visibility.Collapsed;
            try
            {
                if (_audioDevice is { } audio) audio.Seek(AudioSampleClock.At(PlayheadSeconds));
                else PreviewPlayer.Position = TimeSpan.FromSeconds(PlayheadSeconds - _previewRangeStart);
            }
            catch (Exception ex) when (_audioDevice is not null && ex is InvalidOperationException or IOException)
            { ReportAudioPreviewFailure(ex); }
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
            if (_previewPreparing || _previewPlaying || !PreviewIsReady || PreviewPlayer.Source is null || PlayheadSeconds != 0)
                throw new InvalidOperationException("Stop during preparation must cancel pending auto-play, retain completed preload and keep the playhead at zero.");
            InvalidatePreview();
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
            await RunImeTransportSmokeAsync(folder);
            await RunTransportSmokeAsync(folder);
            await RunPreviewCacheSmokeAsync(folder);
            SeekPreview(0);
            await PreparePreviewAsync();
            using var foreground = MediaWorkScheduler.Foreground();
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
            // A single whole-project file supports backward seeks and uninterrupted playback.
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
                if (!PreviewIsReady || Math.Abs(_previewRangeStart) > 1e-8 || Math.Abs(_previewRangeEnd - 65) > 1e-8)
                    throw new InvalidOperationException($"Continuous preview did not reach the project end: {_previewRangeStart} - {_previewRangeEnd}; {PreviewHint.Text}");
                using var rangeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var continuousDuration = await MediaProbe.ReadContainerDurationAsync(_previewFile!, rangeTimeout.Token);
                if (Math.Abs(continuousDuration - 65) > .05)
                    throw new InvalidOperationException($"Continuous preview encoded {continuousDuration}s instead of 65s.");
                SeekPreview(22.25);
                await Task.Delay(150);
                if (Math.Abs(PreviewPlayer.Position.TotalSeconds - 22.25) > .12)
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
                "PASS: a 65-second timeline prepares one continuous file from zero to the end; native playback crosses the old 20-second boundary without stopping or swapping players, retaining global time.",
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
