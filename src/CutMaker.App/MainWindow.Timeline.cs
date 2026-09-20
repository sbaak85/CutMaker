using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CutMaker.Core;

namespace CutMaker.App;

public partial class MainWindow
{
    private const string LibraryAssetFormat = "CutMaker.LibraryAsset.v1";
    private readonly string _dragWindowId = Guid.NewGuid().ToString("N");
    private Point? _assetDragStart;
    private string? _assetDragId;
    private bool _draggingFromLibrary;
    private long _lastDragScroll;

    public static readonly DependencyProperty TimelinePixelsPerSecondProperty = DependencyProperty.Register(
        nameof(TimelinePixelsPerSecond), typeof(double), typeof(MainWindow), new PropertyMetadata(40.0));
    public double TimelinePixelsPerSecond { get => (double)GetValue(TimelinePixelsPerSecondProperty); set => SetValue(TimelinePixelsPerSecondProperty, value); }
    public static readonly DependencyProperty TimelineOffsetSecondsProperty = DependencyProperty.Register(
        nameof(TimelineOffsetSeconds), typeof(double), typeof(MainWindow), new PropertyMetadata(0.0));
    public double TimelineOffsetSeconds { get => (double)GetValue(TimelineOffsetSecondsProperty); set => SetValue(TimelineOffsetSecondsProperty, value); }
    public static readonly DependencyProperty SelectedTimelineClipIdProperty = DependencyProperty.Register(
        nameof(SelectedTimelineClipId), typeof(string), typeof(MainWindow), new PropertyMetadata(null));
    public string? SelectedTimelineClipId { get => (string?)GetValue(SelectedTimelineClipIdProperty); set => SetValue(SelectedTimelineClipIdProperty, value); }

    private void RefreshTimeline()
    {
        var focusedTrack = (Keyboard.FocusedElement as TimelineLane)?.Tag as string;
        if (SelectedTimelineClipId is not null && !_project.Clips.Any(clip => clip.Id == SelectedTimelineClipId)) SelectedTimelineClipId = null;
        NormalizeClipSelection();
        var assets = _project.MediaAssets.ToDictionary(asset => asset.Id);
        TrackItems.ItemsSource = _project.Tracks.Select(track => new
        {
            track.Id, track.Name, IsLocked = track.Locked,
            TrackDetail = $"{(track.Muted ? "靜音" : $"音量 {track.Volume * 100:0}%")}{(track.Locked ? " · 鎖定" : "")}",
            Symbol = track.Kind == TrackKind.Video ? "V" : "A",
            Clips = (IReadOnlyList<TimelineClipView>)_project.Clips.Where(clip => clip.TrackId == track.Id).OrderBy(clip => clip.Start)
                .Select(clip => new TimelineClipView(clip.Id, (clip.LinkGroupId is null ? "" : "↔ ") + Path.GetFileName(assets[clip.AssetId].Path), track.Kind == TrackKind.Audio ? MediaKind.Audio : assets[clip.AssetId].Kind, clip.Start, clip.Duration,
                    clip.FadeIn?.Duration ?? 0, clip.FadeOut?.Duration ?? 0, GetMediaVisuals(clip.AssetId)?.Thumbnail, GetMediaVisuals(clip.AssetId)?.Waveform,
                    clip.SourceIn, assets[clip.AssetId].Duration)).ToArray()
        }).ToArray();
        TrackCount.Text = $"{_project.Tracks.Count} 條軌道 · {_project.Clips.Count} 個片段";
        RemoveClipButton.IsEnabled = SelectedTimelineClipId is not null;
        RefreshSelectionInspector();
        UpdateTimelineViewport();
        RefreshMediaVisuals();
        if (focusedTrack is not null)
        {
            TrackItems.UpdateLayout();
            FindLanes(TrackItems).FirstOrDefault(lane => (string)lane.Tag == focusedTrack)?.Focus();
        }
    }

    private void TimelineZoom_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        TimelinePixelsPerSecond = e.NewValue;
        UpdateTimelineViewport();
    }
    private void Timeline_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (TryZoomTimelineWheel(e.Delta, Keyboard.Modifiers, e.GetPosition(TimelineTimeRuler).X))
            e.Handled = true;
    }

    internal bool TryZoomTimelineWheel(int delta, ModifierKeys modifiers, double pointerX)
    {
        if ((modifiers & ModifierKeys.Control) == 0 || delta == 0 || !double.IsFinite(pointerX) ||
            pointerX < 0 || pointerX > TimelineViewportWidth) return false;
        // Keep captured trim/move/scrub coordinates stable until the current gesture ends.
        if (_pointerOriginal is not null || _draggingFromLibrary || _previewScrubbing) return true;
        var oldScale = TimelinePixelsPerSecond;
        var anchorTime = TimelineOffsetSeconds + pointerX / oldScale;
        var scale = Math.Clamp(oldScale * Math.Pow(1.2, Math.Clamp(delta / 120.0, -20, 20)),
            TimelineZoom.Minimum, TimelineZoom.Maximum);
        if (Math.Abs(scale - oldScale) < 0.0000001) return true;
        TimelineZoom.Value = scale;
        // The slider callback updates the scroll range before restoring the pointer's time anchor.
        TimelinePixelsPerSecond = scale;
        UpdateTimelineViewport();
        TimelineOffsetSeconds = Math.Clamp(anchorTime - pointerX / scale, 0, TimelineHorizontalScroll.Maximum);
        return true;
    }

    private double TimelineViewportWidth => Math.Max(1,
        (TimelineTrackScroll?.ViewportWidth is > 0 ? TimelineTrackScroll.ViewportWidth
            : (TimelineContent?.ActualWidth ?? 0) - SystemParameters.VerticalScrollBarWidth) - TrackHeaderWidth - 8);

    private void TimelineTrackScroll_Changed(object sender, ScrollChangedEventArgs e)
    {
        if (e.ViewportWidthChange != 0) UpdateTimelineViewport();
    }
    private void TimelineContent_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateTimelineViewport();
    private void UpdateTimelineViewport()
    {
        if (TimelineContent is null || TimelineHorizontalScroll is null || TimelineContent.ActualWidth <= 0) return;
        var visibleSeconds = TimelineViewportWidth / TimelinePixelsPerSecond;
        var end = _project.Clips.Count > 0 ? _project.Clips.Max(clip => clip.End) : 0;
        // Rendering is viewport-based. Even long media does not allocate a giant canvas.
        var length = Math.Max(60, end + 30);
        TimelineHorizontalScroll.Maximum = Math.Max(0, length - visibleSeconds);
        TimelineHorizontalScroll.ViewportSize = visibleSeconds;
        TimelineHorizontalScroll.LargeChange = Math.Max(1, visibleSeconds * 0.8);
        TimelineOffsetSeconds = Math.Clamp(TimelineOffsetSeconds, 0, TimelineHorizontalScroll.Maximum);
    }

    private void AssetGrid_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var row = ItemsControl.ContainerFromElement(AssetGrid, e.OriginalSource as DependencyObject) as DataGridRow;
        _assetDragId = (row?.Item as AssetRow)?.Id;
        _assetDragStart = _assetDragId is null ? null : e.GetPosition(AssetGrid);
    }

    private void AssetGrid_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Released) { ResetLibraryDragCandidate(); return; }
        if (_draggingFromLibrary || e.LeftButton != MouseButtonState.Pressed || _assetDragStart is not { } origin || _assetDragId is null) return;
        var point = e.GetPosition(AssetGrid);
        if (Math.Abs(point.X - origin.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(point.Y - origin.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var assetId = _assetDragId;
        _assetDragStart = null;
        _draggingFromLibrary = true;
        try
        {
            DragDrop.DoDragDrop(AssetGrid, CreateLibraryDragData(assetId), DragDropEffects.Copy);
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException)
        {
            StatusText.Text = $"無法開始拖曳：{ex.Message}";
        }
        finally
        {
            _draggingFromLibrary = false;
            _assetDragId = null;
            foreach (var lane in FindLanes(TrackItems)) lane.SetDropPreview(null, 0, false);
        }
        e.Handled = true;
    }

    private void ResetLibraryDragCandidate()
    {
        _assetDragStart = null;
        _assetDragId = null;
    }

    internal IDataObject CreateLibraryDragData(string assetId) => new DataObject(LibraryAssetFormat,
        $"{_dragWindowId}|{_projectGeneration.ToString(CultureInfo.InvariantCulture)}|{assetId}");

    private MediaAsset? GetDraggedAsset(IDataObject data)
    {
        try
        {
            if (!data.GetDataPresent(LibraryAssetFormat, false) || data.GetData(LibraryAssetFormat, false) is not string value) return null;
            var parts = value.Split('|', 3);
            if (parts.Length != 3 || parts[0] != _dragWindowId ||
                !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var generation) || generation != _projectGeneration) return null;
            return _project.MediaAssets.FirstOrDefault(asset => asset.Id == parts[2]);
        }
        catch (ExternalException) { return null; }
    }

    internal bool EvaluateTimelineDrop(IDataObject data, string trackId, double pointerX,
        out MediaAsset? asset, out double start, out string reason)
    {
        asset = GetDraggedAsset(data);
        start = 0;
        if (asset is null) { reason = "請從目前專案的素材庫拖曳素材"; return false; }
        if (!double.IsFinite(pointerX)) { reason = "放置位置無效"; return false; }
        try
        {
            start = TimelinePlacement.RoundToFrame(Math.Max(0, TimelineOffsetSeconds + pointerX / TimelinePixelsPerSecond), _project.Video.Fps);
        }
        catch (ProjectValidationException)
        {
            reason = "放置時間超出可用範圍";
            return false;
        }
        var tolerance = Math.Min(0.25, 8 / TimelinePixelsPerSecond);
        var boundaries = _project.Clips.SelectMany(clip => new[] { clip.Start, clip.End }).Prepend(0);
        var requestedStart = start;
        var nearest = boundaries.MinBy(boundary => Math.Abs(boundary - requestedStart));
        if (Math.Abs(nearest - start) <= tolerance) start = nearest;
        return TimelinePlacement.CanPlace(_project, asset.Id, trackId, start, out reason);
    }

    private void TimelineLane_DragOver(object sender, DragEventArgs e)
    {
        var lane = (TimelineLane)sender;
        var position = e.GetPosition(lane);
        var canCopy = (e.AllowedEffects & DragDropEffects.Copy) != 0;
        if (canCopy && GetDraggedAsset(e.Data) is not null) ScrollDuringDrag(position.X, lane.ActualWidth);
        var allowed = EvaluateTimelineDrop(e.Data, (string)lane.Tag, position.X, out var asset, out var start, out var reason) && canCopy;
        e.Effects = allowed ? DragDropEffects.Copy : DragDropEffects.None;
        lane.SetDropPreview(asset is null ? null : start, asset?.Duration ?? 0, allowed);
        StatusText.Text = allowed ? $"放開以加入「{Path.GetFileName(asset!.Path)}」· 起點 {FormatDuration(start)}" : reason;
        e.Handled = true;
    }

    private void TimelineLane_DragLeave(object sender, DragEventArgs e)
    {
        ((TimelineLane)sender).SetDropPreview(null, 0, false);
        e.Handled = true;
    }

    private void TimelineLane_Drop(object sender, DragEventArgs e)
    {
        var lane = (TimelineLane)sender;
        var x = e.GetPosition(lane).X;
        lane.SetDropPreview(null, 0, false);
        var placed = (e.AllowedEffects & DragDropEffects.Copy) != 0 && TryDropLibraryAsset(e.Data, (string)lane.Tag, x, out _);
        e.Effects = placed ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    internal bool TryDropLibraryAsset(IDataObject data, string trackId, double pointerX, out Clip? added)
    {
        added = null;
        if (!EvaluateTimelineDrop(data, trackId, pointerX, out var asset, out var start, out var reason))
        {
            StatusText.Text = reason;
            return false;
        }
        added = TimelinePlacement.CreateClip(_project, asset!.Id, trackId, start);
        RecordUndo();
        _project.Clips.Add(added);
        SelectedTimelineClipId = added.Id;
        FinishEdit($"已加入「{Path.GetFileName(asset.Path)}」· 起點 {FormatDuration(start)} · 長度 {FormatDuration(added.Duration)}");
        return true;
    }

    private void ScrollDuringDrag(double x, double width)
    {
        if (Stopwatch.GetElapsedTime(_lastDragScroll).TotalMilliseconds < 90) return;
        var direction = x < 20 ? -1 : x > width - 20 ? 1 : 0;
        if (direction == 0) return;
        _lastDragScroll = Stopwatch.GetTimestamp();
        TimelineOffsetSeconds = Math.Clamp(TimelineOffsetSeconds + direction * 20 / TimelinePixelsPerSecond, 0, TimelineHorizontalScroll.Maximum);
    }

    private void TimelineLane_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var lane = (TimelineLane)sender;
        var point = e.GetPosition(lane);
        var time = TimelineOffsetSeconds + point.X / TimelinePixelsPerSecond;
        var selected = _project.Clips.LastOrDefault(clip => clip.TrackId == (string)lane.Tag && clip.Start <= time && time < clip.End);
        _selectedTrackId = (string)lane.Tag;
        SelectClipClick(selected?.Id, Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
        RemoveClipButton.IsEnabled = SelectedTimelineClipId is not null;
        RefreshSelectionInspector();
        Keyboard.Focus(lane);
        if (selected is not null && SelectionIds().Contains(selected.Id) && !Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            StatusText.Text = $"已選取 {SelectionIds().Count} 個片段 · Ctrl+點選多選 · 拖曳中央移動、兩端修剪";
            BeginPointerEdit(lane, selected, point);
        }
        SeekPreview(Math.Max(0, time));
        e.Handled = true;
    }

    private void RemoveClip_Click(object sender, RoutedEventArgs e) => RemoveSelectedClip();
    internal void RemoveSelectedClip()
    {
        DeleteSelectedClips(false);
    }

    private static IEnumerable<TimelineLane> FindLanes(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is TimelineLane lane) yield return lane;
            foreach (var descendant in FindLanes(child)) yield return descendant;
        }
    }
}
