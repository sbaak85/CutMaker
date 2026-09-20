using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CutMaker.Core;

namespace CutMaker.App;

public partial class MainWindow
{
    private sealed record EditSnapshot(CutProject Project, string? ClipId, string? TrackId);
    private readonly List<EditSnapshot> _undo = [];
    private readonly List<EditSnapshot> _redo = [];
    private string? _selectedTrackId;
    private enum PointerEdit { Move, TrimStart, TrimEnd, FadeIn, FadeOut }
    private Clip? _pointerOriginal;
    private Clip? _pointerCandidate;
    private TimelineLane? _pointerLane;
    private PointerEdit _pointerMode;
    private Point _pointerOrigin;
    private double _pointerDownTime;
    private double _pointerGrabOffset;
    private bool _pointerMoved;

    // Snapshots own their lists; record items are immutable. Absolute paths keep Undo safe after Save As.
    private EditSnapshot CaptureEdit() => new(_project with
    {
        MediaAssets = _project.MediaAssets.Select(asset => asset with
        {
            Path = _projectPath is null ? Path.GetFullPath(asset.Path) : ProjectStore.ResolveAssetPath(_projectPath, asset)
        }).ToList(),
        Tracks = [.. _project.Tracks], Clips = [.. _project.Clips]
    }, SelectedTimelineClipId, _selectedTrackId);

    internal void RecordUndo()
    {
        _undo.Add(CaptureEdit());
        if (_undo.Count > 100) _undo.RemoveAt(0);
        _redo.Clear();
        RefreshHistoryButtons();
    }

    internal void ClearEditHistory()
    {
        CancelPointerEdit();
        _undo.Clear(); _redo.Clear(); _selectedTrackId = null;
        RefreshHistoryButtons();
    }

    private void RefreshHistoryButtons()
    {
        if (FindName("UndoButton") is Button undo) undo.IsEnabled = _undo.Count > 0;
        if (FindName("RedoButton") is Button redo) redo.IsEnabled = _redo.Count > 0;
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => UndoEdit();
    private void Redo_Click(object sender, RoutedEventArgs e) => RedoEdit();
    internal void UndoEdit() => RestoreEdit(_undo, _redo, "已復原");
    internal void RedoEdit() => RestoreEdit(_redo, _undo, "已重做");
    private void RestoreEdit(List<EditSnapshot> from, List<EditSnapshot> to, string message)
    {
        if (from.Count == 0) return;
        CancelPointerEdit();
        _importCancellation?.Cancel();
        _projectGeneration++; // In-flight import/drag results belong to the old state.
        to.Add(CaptureEdit());
        var snapshot = from[^1]; from.RemoveAt(from.Count - 1);
        _project = snapshot.Project with
        {
            MediaAssets = [.. snapshot.Project.MediaAssets], Tracks = [.. snapshot.Project.Tracks], Clips = [.. snapshot.Project.Clips]
        };
        SelectedTimelineClipId = snapshot.ClipId; _selectedTrackId = snapshot.TrackId;
        _dirty = true;
        RefreshProject(); RefreshHistoryButtons(); InvalidatePreview();
        StatusText.Text = message;
    }

    internal void FinishEdit(string message)
    {
        _dirty = true;
        RefreshTimeline(); UpdateTitle(); RefreshHistoryButtons(); InvalidatePreview();
        StatusText.Text = message;
    }

    internal bool TryReplaceClip(Clip replacement, string message)
    {
        if (!TimelineEditor.CanReplace(_project, replacement, out var reason)) { StatusText.Text = reason; return false; }
        var index = _project.Clips.FindIndex(clip => clip.Id == replacement.Id);
        if (_project.Clips[index] == replacement) return true;
        RecordUndo();
        _project.Clips[index] = replacement;
        SelectedTimelineClipId = replacement.Id; _selectedTrackId = replacement.TrackId;
        FinishEdit(message);
        return true;
    }

    private void SplitClip_Click(object sender, RoutedEventArgs e) => SplitSelectedClip(PlayheadSeconds);
    internal bool SplitSelectedClip(double position)
    {
        var clip = SelectedClip();
        if (clip is null) { StatusText.Text = "請先選取要切割的片段"; return false; }
        if (_project.Tracks.Any(track => track.Id == clip.TrackId && track.Locked)) { StatusText.Text = "軌道已鎖定，無法切割"; return false; }
        try
        {
            var (left, right) = ClipEditor.Split(clip, position, $"clip-{Guid.NewGuid():N}");
            RecordUndo();
            _project.Clips[_project.Clips.IndexOf(clip)] = left;
            _project.Clips.Add(right);
            SelectedTimelineClipId = right.Id;
            FinishEdit("已在播放頭位置切割片段 · Ctrl+Z 復原");
            return true;
        }
        catch (ProjectValidationException) { StatusText.Text = "請把播放頭移到片段內部；若切點落在 Fade 內，請先縮短或移除 Fade"; return false; }
    }

    private Clip? SelectedClip() => _project.Clips.FirstOrDefault(clip => clip.Id == SelectedTimelineClipId);
    internal void SelectTimelineClipForChecks(string? id) { SelectedTimelineClipId = id; RefreshTimeline(); }

    private void RefreshSelectionInspector()
    {
        var clip = SelectedClip();
        if (clip is not null) _selectedTrackId = clip.TrackId;
        var track = _project.Tracks.FirstOrDefault(item => item.Id == _selectedTrackId);
        if (FindName("ClipNameText") is TextBlock name)
            name.Text = clip is null ? "選取時間軸片段以編輯" : Path.GetFileName(_project.MediaAssets.First(asset => asset.Id == clip.AssetId).Path);
        SetNumber("ClipStartBox", clip?.Start); SetNumber("ClipSourceInBox", clip?.SourceIn);
        SetNumber("ClipDurationBox", clip?.Duration); SetNumber("ClipGainBox", clip?.Gain * 100);
        SetNumber("FadeInBox", clip is null ? null : clip.FadeIn?.Duration ?? 0);
        SetNumber("FadeOutBox", clip is null ? null : clip.FadeOut?.Duration ?? 0);
        SetCurve("FadeInCurveBox", clip?.FadeIn?.Curve ?? FadeCurve.Linear, clip is not null);
        SetCurve("FadeOutCurveBox", clip?.FadeOut?.Curve ?? FadeCurve.Linear, clip is not null);
        if (FindName("ApplyClipButton") is Button applyClip) applyClip.IsEnabled = clip is not null;
        if (FindName("SplitClipButton") is Button split) split.IsEnabled = clip is not null;
        if (FindName("TrackNameBox") is TextBox trackName) { trackName.Text = track?.Name ?? ""; trackName.IsEnabled = track is not null; }
        SetNumber("TrackVolumeBox", track?.Volume * 100);
        if (FindName("TrackMutedBox") is CheckBox muted) { muted.IsChecked = track?.Muted ?? false; muted.IsEnabled = track is not null; }
        if (FindName("TrackLockedBox") is CheckBox locked) { locked.IsChecked = track?.Locked ?? false; locked.IsEnabled = track is not null; }
        if (FindName("ApplyTrackButton") is Button applyTrack) applyTrack.IsEnabled = track is not null;
    }

    private void SetNumber(string name, double? value)
    {
        if (FindName(name) is not TextBox box) return;
        box.Text = value?.ToString("0.######", CultureInfo.InvariantCulture) ?? "";
        box.IsEnabled = value.HasValue;
    }
    private void SetCurve(string name, FadeCurve curve, bool enabled)
    {
        if (FindName(name) is not ComboBox combo) return;
        combo.ItemsSource ??= new[]
        {
            new CurveChoice(FadeCurve.Linear, "線性"), new CurveChoice(FadeCurve.EaseIn, "慢進"),
            new CurveChoice(FadeCurve.EaseOut, "慢出"), new CurveChoice(FadeCurve.SmoothStep, "平滑 S"),
            new CurveChoice(FadeCurve.EqualPower, "等功率")
        };
        combo.DisplayMemberPath = nameof(CurveChoice.Name); combo.SelectedValuePath = nameof(CurveChoice.Curve);
        combo.SelectedValue = curve; combo.IsEnabled = enabled;
    }
    private sealed record CurveChoice(FadeCurve Curve, string Name);
    private double ReadNumber(string name)
    {
        var text = (FindName(name) as TextBox)?.Text ?? "";
        if (!(double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
            double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)) || !double.IsFinite(value))
            throw new ProjectValidationException("請輸入有效的數值；時間單位為秒，音量為百分比。");
        return value;
    }
    private FadeCurve ReadCurve(string name) => (FindName(name) as ComboBox)?.SelectedValue is FadeCurve curve ? curve : FadeCurve.Linear;

    private void ApplyClip_Click(object sender, RoutedEventArgs e)
    {
        var clip = SelectedClip(); if (clip is null) return;
        try
        {
            TryReplaceClip(clip with
            {
                Start = ReadNumber("ClipStartBox"), SourceIn = ReadNumber("ClipSourceInBox"), Duration = ReadNumber("ClipDurationBox"),
                Gain = ReadNumber("ClipGainBox") / 100,
                FadeIn = new(ReadNumber("FadeInBox"), ReadCurve("FadeInCurveBox")),
                FadeOut = new(ReadNumber("FadeOutBox"), ReadCurve("FadeOutCurveBox"))
            }, "已套用片段時間、音量與 Fade");
        }
        catch (ProjectValidationException error) { StatusText.Text = error.Message; }
    }

    private void ApplyTrack_Click(object sender, RoutedEventArgs e)
    {
        var track = _project.Tracks.FirstOrDefault(item => item.Id == _selectedTrackId);
        if (track is null) return;
        try
        {
            var replacement = track with
            {
                Name = (FindName("TrackNameBox") as TextBox)?.Text.Trim() ?? track.Name,
                Volume = ReadNumber("TrackVolumeBox") / 100,
                Muted = (FindName("TrackMutedBox") as CheckBox)?.IsChecked == true,
                Locked = (FindName("TrackLockedBox") as CheckBox)?.IsChecked == true
            };
            ApplyTrackSettings(replacement);
        }
        catch (ProjectValidationException error) { StatusText.Text = error.Message; }
    }

    internal bool ApplyTrackSettings(Track replacement)
    {
        var index = _project.Tracks.FindIndex(track => track.Id == replacement.Id);
        if (index < 0) return false;
        if (_project.Tracks[index] == replacement) return true;
        var candidate = _project with { Tracks = _project.Tracks.Select(track => track.Id == replacement.Id ? replacement : track).ToList() };
        ProjectValidator.Validate(candidate);
        RecordUndo(); _project.Tracks[index] = replacement;
        _selectedTrackId = replacement.Id;
        FinishEdit("已套用軌道名稱、音量、靜音與鎖定設定");
        return true;
    }

    private void TimelineTrack_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement element && element.Tag is string id) SelectTimelineTrack(id);
    }
    internal void SelectTimelineTrack(string id)
    {
        _selectedTrackId = id; SelectedTimelineClipId = null;
        RemoveClipButton.IsEnabled = false; RefreshSelectionInspector();
    }

    private void BeginPointerEdit(TimelineLane lane, Clip clip, Point point)
    {
        if (lane.IsLocked) return;
        var left = (clip.Start - TimelineOffsetSeconds) * TimelinePixelsPerSecond;
        var right = (clip.End - TimelineOffsetSeconds) * TimelinePixelsPerSecond;
        var edge = Math.Min(8, (right - left) / 4);
        var fadeInHandle = Math.Clamp(left + (clip.FadeIn?.Duration ?? 0) * TimelinePixelsPerSecond, left + edge, right - edge);
        var fadeOutHandle = Math.Clamp(right - (clip.FadeOut?.Duration ?? 0) * TimelinePixelsPerSecond, left + edge, right - edge);
        _pointerMode = point.Y <= 17 && Math.Abs(point.X - fadeInHandle) <= 6 ? PointerEdit.FadeIn
            : point.Y <= 17 && Math.Abs(point.X - fadeOutHandle) <= 6 ? PointerEdit.FadeOut
            : Math.Abs(point.X - left) <= edge ? PointerEdit.TrimStart
            : Math.Abs(point.X - right) <= edge ? PointerEdit.TrimEnd : PointerEdit.Move;
        _pointerOriginal = clip; _pointerCandidate = null; _pointerLane = lane; _pointerOrigin = point;
        _pointerDownTime = TimelineOffsetSeconds + point.X / TimelinePixelsPerSecond;
        _pointerGrabOffset = _pointerDownTime - clip.Start;
        if (_pointerMode == PointerEdit.FadeIn) _pointerGrabOffset -= clip.FadeIn?.Duration ?? 0;
        else if (_pointerMode == PointerEdit.FadeOut) _pointerGrabOffset -= clip.Duration - (clip.FadeOut?.Duration ?? 0);
        _pointerMoved = false;
        lane.CaptureMouse();
        lane.Cursor = _pointerMode == PointerEdit.Move ? Cursors.SizeAll : Cursors.SizeWE;
    }

    private void TimelineLane_MouseMove(object sender, MouseEventArgs e)
    {
        if (_pointerOriginal is not { } original || _pointerLane is not { } captured) return;
        if (e.LeftButton != MouseButtonState.Pressed) { CancelPointerEdit(); return; }
        var local = e.GetPosition(captured);
        if (!_pointerMoved && Math.Abs(local.X - _pointerOrigin.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(local.Y - _pointerOrigin.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _pointerMoved = true;
        var target = _pointerMode == PointerEdit.Move ? FindLanes(TrackItems).FirstOrDefault(lane =>
        {
            var point = e.GetPosition(lane);
            return point.X >= 0 && point.X <= lane.ActualWidth && point.Y >= 0 && point.Y <= lane.ActualHeight;
        }) : captured;
        foreach (var lane in FindLanes(TrackItems)) lane.SetDropPreview(null, 0, false);
        if (target is null) { _pointerCandidate = null; StatusText.Text = "請將片段拖至相容軌道內"; return; }
        var point = e.GetPosition(target);
        ScrollDuringDrag(point.X, target.ActualWidth);
        var rawTime = Math.Max(0, TimelineOffsetSeconds + point.X / TimelinePixelsPerSecond);
        try
        {
            var next = _pointerMode switch
            {
                PointerEdit.TrimStart => PlanPointerTrim(original, true, _pointerDownTime, rawTime),
                PointerEdit.TrimEnd => PlanPointerTrim(original, false, _pointerDownTime, rawTime),
                PointerEdit.FadeIn => original with { FadeIn = new(Math.Clamp(TimelinePlacement.RoundToFrame(Math.Max(0, rawTime - _pointerGrabOffset - original.Start), _project.Video.Fps), 0, original.Duration), original.FadeIn?.Curve ?? FadeCurve.Linear) },
                PointerEdit.FadeOut => original with { FadeOut = new(Math.Clamp(TimelinePlacement.RoundToFrame(Math.Max(0, original.End - rawTime + _pointerGrabOffset), _project.Video.Fps), 0, original.Duration), original.FadeOut?.Curve ?? FadeCurve.Linear) },
                _ => original with { Start = SnapEditTime(Math.Max(0, rawTime - _pointerGrabOffset), original.Id, original.Duration), TrackId = (string)target.Tag }
            };
            var allowed = TimelineEditor.CanReplace(_project, next, out var reason);
            _pointerCandidate = allowed ? next : null;
            target.SetDropPreview(next.Start, next.Duration, allowed);
            var action = _pointerMode switch { PointerEdit.Move => "移動", PointerEdit.FadeIn => $"淡入 {next.FadeIn!.Duration:0.###} 秒", PointerEdit.FadeOut => $"淡出 {next.FadeOut!.Duration:0.###} 秒", _ => "修剪" };
            StatusText.Text = allowed ? $"{action} · 起點 {FormatDuration(next.Start)} · 長度 {FormatDuration(next.Duration)} · 放開套用"
                : reason;
        }
        catch (ProjectValidationException error) { _pointerCandidate = null; StatusText.Text = error.Message; }
        e.Handled = true;
    }

    internal Clip PlanPointerTrim(Clip original, bool trimStart, double pointerDownTime, double pointerTime)
    {
        var duration = _project.MediaAssets.First(asset => asset.Id == original.AssetId).Duration;
        var minimum = Math.Min(1 / _project.Video.Fps, original.Duration);
        // The press may be several pixels inside the edge. Apply only its drag delta to that edge.
        var originalEdge = trimStart ? original.Start : original.End;
        var edge = SnapEditTime(Math.Max(0, originalEdge + pointerTime - pointerDownTime), original.Id);
        return trimStart
            ? ClipEditor.TrimStart(original, Math.Clamp(edge, Math.Max(0, original.Start - original.SourceIn), original.End - minimum), duration)
            : ClipEditor.TrimEnd(original, Math.Clamp(edge, original.Start + minimum, original.Start + duration - original.SourceIn), duration);
    }

    private double SnapEditTime(double time, string clipId, double duration = 0)
    {
        var result = TimelinePlacement.RoundToFrame(time, _project.Video.Fps);
        var tolerance = Math.Min(.25, 8 / TimelinePixelsPerSecond);
        var boundaries = _project.Clips.Where(clip => clip.Id != clipId).SelectMany(clip => new[] { clip.Start, clip.End }).Prepend(0);
        var candidates = boundaries.SelectMany(edge => duration > 0 ? new[] { edge, edge - duration } : new[] { edge }).Where(value => value >= 0);
        var nearest = candidates.MinBy(value => Math.Abs(value - result));
        return Math.Abs(nearest - result) <= tolerance ? nearest : result;
    }

    private void TimelineLane_MouseUp(object sender, MouseButtonEventArgs e)
    {
        var candidate = _pointerCandidate;
        var moved = _pointerMoved;
        CancelPointerEdit();
        if (moved && candidate is not null) TryReplaceClip(candidate, "已更新片段位置與長度 · Ctrl+Z 復原");
        e.Handled = true;
    }
    private void TimelineLane_LostCapture(object sender, MouseEventArgs e) => CancelPointerEdit();
    private void CancelPointerEdit()
    {
        var lane = _pointerLane;
        _pointerOriginal = null; _pointerCandidate = null; _pointerLane = null; _pointerMoved = false;
        if (lane is not null) { lane.Cursor = Cursors.Arrow; if (lane.IsMouseCaptured) lane.ReleaseMouseCapture(); }
        if (TrackItems is not null) foreach (var item in FindLanes(TrackItems)) item.SetDropPreview(null, 0, false);
    }
}
