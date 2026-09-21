using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CutMaker.Core;

namespace CutMaker.App;

public partial class MainWindow
{
    private sealed record WaveDetail(string AssetKey, double Start, double Duration, BitmapSource Image);
    private readonly Dictionary<string, WaveDetail> _waveDetails = [];
    private readonly DispatcherTimer _waveDetailTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(180) };
    private CancellationTokenSource? _waveDetailCancellation;
    private bool _waveDetailsInitialized;
    private void InitializeWaveDetails()
    {
        _waveDetailsInitialized = true;
        _waveDetailTimer.Tick += async (_, _) => { _waveDetailTimer.Stop(); await LoadVisibleWaveDetailsAsync(); };
    }
    private void QueueWaveDetails()
    {
        if (!_waveDetailsInitialized || _mediaVisualStopped) return;
        _waveDetailCancellation?.Cancel();
        _waveDetailTimer.Stop(); _waveDetailTimer.Start();
    }
    private TimelineClipView WithWaveDetail(TimelineClipView view)
    {
        if (!_waveDetails.TryGetValue(view.Id, out var detail)) return view;
        var visibleFirst = view.SourceIn + Math.Max(0, TimelineOffsetSeconds - view.Start);
        var visibleEnd = view.SourceIn + Math.Min(view.Duration, TimelineOffsetSeconds + Math.Max(1, TimelineTimeRuler.ActualWidth) / TimelinePixelsPerSecond - view.Start);
        return detail.Start <= visibleFirst && detail.Start + detail.Duration >= visibleEnd
            ? view with { Waveform = detail.Image, WaveformStart = detail.Start, WaveformDuration = detail.Duration } : view;
    }
    private async Task LoadVisibleWaveDetailsAsync()
    {
        _waveDetailTimer.Stop();
        _waveDetailCancellation?.Cancel();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_mediaVisualCancellation.Token);
        _waveDetailCancellation = cancellation;
        var token = cancellation.Token;
        try
        {
            var clips = _project.Clips.ToDictionary(clip => clip.Id);
            var assets = _project.MediaAssets.ToDictionary(asset => asset.Id);
            foreach (var lane in FindLanes(TrackItems).ToArray())
            {
                var y = lane.TranslatePoint(new Point(0, 0), TimelineTrackScroll).Y;
                if (y + lane.ActualHeight < 0 || y > TimelineTrackScroll.ActualHeight || lane.Clips is null) continue;
                foreach (var view in lane.Clips.ToArray())
                {
                    token.ThrowIfCancellationRequested();
                    if (!clips.TryGetValue(view.Id, out var clip)) continue;
                    var asset = assets[clip.AssetId];
                    if (asset.Kind == MediaKind.Video && GetMediaVisuals(asset.Id)?.Waveform is null) continue;
                    if (asset.Kind == MediaKind.Image || asset.Duration * TimelinePixelsPerSecond <= 2048 ||
                        clip.End <= TimelineOffsetSeconds || clip.Start >= TimelineOffsetSeconds + lane.ActualWidth / TimelinePixelsPerSecond) continue;
                    var bucket = Math.Max(.25, Math.Pow(2, Math.Ceiling(Math.Log2(Math.Max(.25, lane.ActualWidth / TimelinePixelsPerSecond)))));
                    var visibleFirst = clip.SourceIn + Math.Max(0, TimelineOffsetSeconds - clip.Start);
                    var first = Math.Floor(visibleFirst / bucket) * bucket;
                    var duration = Math.Min(asset.Duration - first, bucket * 2);
                    if (duration <= 0) continue;
                    var key = MediaVisualCache.GetKey(asset, _projectPath);
                    if (_waveDetails.TryGetValue(clip.Id, out var previous) && previous.AssetKey == key && previous.Start == first && previous.Duration == duration) continue;
                    var bitmap = await Task.Run(() => MediaVisualCache.GetWaveDetailAsync(asset, _projectPath, first, duration, token), token);
                    token.ThrowIfCancellationRequested();
                    if (MediaVisualCache.GetKey(asset, _projectPath) != key) continue;
                    _waveDetails[clip.Id] = new(key, first, duration, bitmap);
                    // Keep the row and its pointer capture alive while a finer waveform arrives.
                    if (lane.Clips is { } current) lane.SetCurrentValue(TimelineLane.ClipsProperty, current.Select(WithWaveDetail).ToArray());
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException) { }
        finally { if (ReferenceEquals(_waveDetailCancellation, cancellation)) _waveDetailCancellation = null; }
    }
}
