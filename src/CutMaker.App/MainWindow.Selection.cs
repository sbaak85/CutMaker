using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CutMaker.Core;

namespace CutMaker.App;

public partial class MainWindow
{
    private TimelineLane? _marqueeLane;
    private Point _marqueeOrigin;
    private double _marqueeStartTime;
    private bool _marqueeAdditive;
    private bool _marqueeMoved;
    private string[] _marqueeOriginalIds = [];
    private string? _marqueeOriginalPrimary;
    internal bool IsMarqueeSelecting => _marqueeLane is not null;

    internal void BeginMarqueeSelection(TimelineLane lane, Point lanePoint, bool additive, bool captureMouse = true)
    {
        CancelPointerEdit();
        EndMarqueeSelection(cancel: true);
        _marqueeOriginalIds = SelectionIds().ToArray();
        _marqueeOriginalPrimary = SelectedTimelineClipId;
        _marqueeLane = lane;
        _marqueeOrigin = lane.TranslatePoint(lanePoint, TrackItems);
        _marqueeStartTime = Math.Max(0, TimelineOffsetSeconds + lanePoint.X / TimelinePixelsPerSecond);
        _marqueeAdditive = additive;
        _marqueeMoved = false;
        if (!additive) SetClipSelection([]);
        RemoveClipButton.IsEnabled = SelectedTimelineClipId is not null;
        RefreshSelectionInspector();
        if (captureMouse) lane.CaptureMouse();
        lane.Cursor = Cursors.Cross;
    }

    internal void UpdateMarqueeSelection(Point pointInTrackItems)
    {
        if (_marqueeLane is null) return;
        if (!_marqueeMoved && Math.Abs(pointInTrackItems.X - _marqueeOrigin.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pointInTrackItems.Y - _marqueeOrigin.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _marqueeMoved = true;
        var laneLeft = _marqueeLane.TranslatePoint(new Point(), TrackItems).X;
        var time = Math.Max(0, TimelineOffsetSeconds + (pointInTrackItems.X - laneLeft) / TimelinePixelsPerSecond);
        var low = Math.Min(_marqueeStartTime, time);
        var high = Math.Max(_marqueeStartTime, time);
        var top = Math.Min(_marqueeOrigin.Y, pointInTrackItems.Y);
        var bottom = Math.Max(_marqueeOrigin.Y, pointInTrackItems.Y);
        var tracks = FindLanes(TrackItems).Where(lane =>
        {
            var y = lane.TranslatePoint(new Point(), TrackItems).Y;
            return !lane.IsLocked && bottom > y + 5 && top < y + lane.ActualHeight - 5;
        }).Select(lane => (string)lane.Tag).ToHashSet();
        var hit = _project.Clips.Where(c => tracks.Contains(c.TrackId) && c.Start < high && c.End > low)
            .Where(c => !TimelineBatchEditor.ExpandLinks(_project, [c.Id]).Any(id =>
                _project.Tracks.First(t => t.Id == _project.Clips.First(clip => clip.Id == id).TrackId).Locked))
            .Select(c => c.Id);
        SetClipSelection(_marqueeAdditive ? _marqueeOriginalIds.Concat(hit) : hit);
        RemoveClipButton.IsEnabled = SelectedTimelineClipId is not null;
        RefreshSelectionInspector();
        StatusText.Text = $"框選 {SelectionIds().Count} 個片段 · Ctrl+Shift+框選追加 · Esc 取消";

        var origin = TrackItems.TranslatePoint(new Point(laneLeft, 0), TimelineContent);
        var left = Math.Clamp(origin.X + (low - TimelineOffsetSeconds) * TimelinePixelsPerSecond, origin.X, origin.X + TimelineViewportWidth);
        var right = Math.Clamp(origin.X + (high - TimelineOffsetSeconds) * TimelinePixelsPerSecond, origin.X, origin.X + TimelineViewportWidth);
        var visibleTop = Math.Clamp(origin.Y + top, 0, TimelineTrackScroll.ViewportHeight);
        var visibleBottom = Math.Clamp(origin.Y + bottom, 0, TimelineTrackScroll.ViewportHeight);
        Canvas.SetLeft(TimelineMarqueeRect, left); Canvas.SetTop(TimelineMarqueeRect, visibleTop);
        TimelineMarqueeRect.Width = Math.Max(0, right - left);
        TimelineMarqueeRect.Height = Math.Max(0, visibleBottom - visibleTop);
        TimelineMarqueeRect.Visibility = Visibility.Visible;
    }

    internal void EndMarqueeSelection(bool cancel)
    {
        var lane = _marqueeLane;
        if (lane is null) return;
        _marqueeLane = null;
        if (cancel) SetClipSelection(_marqueeOriginalIds, _marqueeOriginalPrimary);
        TimelineMarqueeRect.Visibility = Visibility.Collapsed;
        lane.Cursor = Cursors.Arrow;
        if (lane.IsMouseCaptured) lane.ReleaseMouseCapture();
        RemoveClipButton.IsEnabled = SelectedTimelineClipId is not null;
        RefreshSelectionInspector();
        StatusText.Text = cancel ? "已取消框選" : $"已選取 {SelectionIds().Count} 個片段 · 拖曳中央移動整組";
    }

    private void UpdateMarqueeFromPointer(MouseEventArgs e, bool autoScroll = true)
    {
        UpdateMarqueeSelection(e.GetPosition(TrackItems));
        if (!_marqueeMoved || !autoScroll) return;
        var viewportPoint = e.GetPosition(TimelineContent);
        ScrollDuringDrag(viewportPoint.X - TrackHeaderWidth - 8, TimelineViewportWidth);
        ScrollTracksDuringDrag(viewportPoint.Y);
        UpdateMarqueeSelection(e.GetPosition(TrackItems));
    }

    private void ScrollTracksDuringDrag(double y)
    {
        var direction = y < 16 ? -1 : y > TimelineTrackScroll.ViewportHeight - 16 ? 1 : 0;
        if (direction != 0) TimelineTrackScroll.ScrollToVerticalOffset(TimelineTrackScroll.VerticalOffset + direction * 14);
    }

    internal bool TryCommitGroupMove(Clip replacement) => CommitBatch(
        () => PlanLinkedReplacement(replacement, moveSelection: true), "已移動選取片段 · 保留影音連動與間距 · Ctrl+Z 復原");

    internal bool TryPreviewGroupMove(Clip replacement)
    {
        foreach (var lane in FindLanes(TrackItems)) lane.SetDropPreview(null, 0, false);
        try
        {
            var original = _project.Clips.First(c => c.Id == replacement.Id);
            var plan = TimelineBatchEditor.PlanMove(_project, SelectionIds(), replacement.Start - original.Start, original.Id, replacement.TrackId);
            foreach (var lane in FindLanes(TrackItems))
                lane.SetMovePreviews(plan.MovedClips.Where(c => c.TrackId == (string)lane.Tag)
                    .Select(c => new TimelineMoveGhost(c.Id, c.Start, c.Duration)).ToArray());
            return true;
        }
        catch (ProjectValidationException error)
        {
            FindLanes(TrackItems).FirstOrDefault(lane => (string)lane.Tag == replacement.TrackId)?.SetDropPreview(replacement.Start, replacement.Duration, false);
            StatusText.Text = error.Message; return false;
        }
    }

    internal bool TimelineSnapEnabled { get; set; } = true;
    private bool ShouldSnapTimeline => TimelineSnapEnabled && !Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
    private void SnapTimeline_Click(object sender, RoutedEventArgs e) => SetTimelineSnapping(SnapMenuItem.IsChecked);
    private void SetTimelineSnapping(bool enabled)
    {
        TimelineSnapEnabled = enabled; SnapMenuItem.IsChecked = enabled;
        ShowSnapHint(enabled);
        StatusText.Text = enabled ? "吸附已開啟 · 拖曳時按 Alt 可暫時停用" : "吸附已關閉 · C 重新開啟";
    }

    private void CutClips_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectionIds();
        var copies = _project.Clips.Where(c => selected.Contains(c.Id)).ToArray();
        if (copies.Length == 0) { StatusText.Text = "請先選取片段"; return; }
        if (DeleteSelectedClips(false)) { _clipClipboard = copies; StatusText.Text = "已剪下片段 · 移動播放頭後 Ctrl+V 貼上"; }
    }

    private void ClearSelection_Click(object sender, RoutedEventArgs e)
    { SetClipSelection([]); RemoveClipButton.IsEnabled = false; RefreshSelectionInspector(); }

    private void TimelineLane_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (IsMarqueeSelecting || _pointerOriginal is not null) return;
        var lane = (TimelineLane)sender;
        var time = TimelineOffsetSeconds + e.GetPosition(lane).X / TimelinePixelsPerSecond;
        var clip = _project.Clips.FirstOrDefault(c => c.TrackId == (string)lane.Tag && c.Start <= time && time < c.End);
        if (clip is not null) SelectClipClick(clip.Id, toggle: false);
        _selectedTrackId = (string)lane.Tag;
        RemoveClipButton.IsEnabled = SelectedTimelineClipId is not null;
        RefreshSelectionInspector();
        lane.Focus();
        var menu = new ContextMenu();
        if (clip is not null)
        {
            var hit = lane.HitEdit(clip.Start, clip.Duration, clip.FadeIn?.Duration ?? 0, clip.FadeOut?.Duration ?? 0, e.GetPosition(lane));
            if (hit == PointerEdit.FadeOut) { AddFadeMenu(menu, false); AddFadeMenu(menu, true); }
            else { AddFadeMenu(menu, true); AddFadeMenu(menu, false); }
            menu.Items.Add(new Separator());
        }
        void Item(string text, RoutedEventHandler handler, string gesture = "")
        {
            var item = new MenuItem { Header = text, InputGestureText = gesture };
            item.Click += handler; menu.Items.Add(item);
        }
        Item("剪下", CutClips_Click, "Ctrl+X"); Item("複製", CopyClips_Click, "Ctrl+C");
        Item("貼到播放頭", PasteClips_Click, "Ctrl+V"); Item("複製到尾端", DuplicateClips_Click, "Ctrl+D");
        menu.Items.Add(new Separator());
        Item("切割", SplitClip_Click, "B"); Item("刪除", RemoveClip_Click, "Delete");
        Item("同軌波紋刪除", RippleDelete_Click, "Shift+Delete");
        menu.Items.Add(new Separator());
        Item("定位到選取起點", SelectedStart_Click); Item("定位到選取終點", SelectedEnd_Click);
        menu.PlacementTarget = lane; menu.IsOpen = true;
        e.Handled = true;
    }
}
