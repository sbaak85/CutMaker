using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CutMaker.Core;

namespace CutMaker.App;

public partial class MainWindow
{
    private sealed record EditSnapshot(CutProject Project, string? ClipId, string? TrackId, string[] SelectedIds);
    private readonly List<EditSnapshot> _undo = [];
    private readonly List<EditSnapshot> _redo = [];
    private string? _selectedTrackId;
    private Clip? _pointerOriginal;
    private Clip? _pointerCandidate;
    private TimelineLane? _pointerLane;
    private PointerEdit _pointerMode;
    private Point _pointerOrigin;
    private double _pointerDownTime;
    private double _pointerGrabOffset;
    private bool _pointerMoved;
    private double _pointerRawTime;
    private double _pointerEffectiveTime;
    private double? _snapGuideTime;
    private string? _snapGuideLabel;

    // Snapshots own their lists; record items are immutable. Absolute paths keep Undo safe after Save As.
    private EditSnapshot CaptureEdit() => new(_project with
    {
        MediaAssets = _project.MediaAssets.Select(asset => asset with
        {
            Path = _projectPath is null ? Path.GetFullPath(asset.Path) : ProjectStore.ResolveAssetPath(_projectPath, asset)
        }).ToList(),
        Tracks = [.. _project.Tracks], Clips = [.. _project.Clips]
    }, SelectedTimelineClipId, _selectedTrackId, SelectionIds().ToArray());

    internal void RecordUndo()
    {
        _undo.Add(CaptureEdit());
        if (_undo.Count > 100) _undo.RemoveAt(0);
        _redo.Clear();
        RefreshHistoryButtons();
    }

    internal void ClearEditHistory()
    {
        ClearCollapsedTracks();
        EndMarqueeSelection(cancel: true);
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
        EndMarqueeSelection(cancel: true);
        CancelPointerEdit();
        _importCancellation?.Cancel();
        _projectGeneration++; // In-flight import/drag results belong to the old state.
        to.Add(CaptureEdit());
        var snapshot = from[^1]; from.RemoveAt(from.Count - 1);
        _project = snapshot.Project with
        {
            MediaAssets = [.. snapshot.Project.MediaAssets], Tracks = [.. snapshot.Project.Tracks], Clips = [.. snapshot.Project.Clips]
        };
        SetClipSelection(snapshot.SelectedIds, snapshot.ClipId); _selectedTrackId = snapshot.TrackId;
        _dirty = true;
        RefreshProject(); RefreshHistoryButtons(); RefreshPreviewAfterEdit();
        StatusText.Text = message;
    }

    internal void FinishEdit(string message)
    {
        _dirty = true;
        RefreshTimeline(); UpdateTitle(); RefreshHistoryButtons(); RefreshPreviewAfterEdit();
        StatusText.Text = message;
    }

    internal bool TryReplaceClip(Clip replacement, string message)
    {
        if (_project.Clips.FirstOrDefault(c => c.Id == replacement.Id) == replacement) return true;
        var ok = CommitBatch(() => PlanLinkedReplacement(replacement), message);
        if (ok) { SetClipSelection([replacement.Id], replacement.Id); _selectedTrackId = replacement.TrackId; RefreshTimeline(); }
        return ok;
    }

    private void SplitClip_Click(object sender, RoutedEventArgs e) => SplitSelectedClip(PlayheadSeconds);
    internal bool SplitSelectedClip(double position)
    {
        var clip = SelectedClip();
        if (clip is null) { StatusText.Text = "請先選取要切割的片段"; return false; }
        var before = _project.Clips.Select(c => c.Id).ToHashSet();
        var ok = CommitBatch(() => TimelineBatchEditor.Split(_project, SelectionIds(), position), "已在播放頭切割選取片段 · Fade 曲線保持連續");
        if (ok) { SetClipSelection(_project.Clips.Where(c => !before.Contains(c.Id)).Select(c => c.Id)); RefreshTimeline(); }
        return ok;
    }

    private Clip? SelectedClip() => _project.Clips.FirstOrDefault(clip => clip.Id == SelectedTimelineClipId);
    internal void SelectTimelineClipForChecks(string? id) { SetClipSelection(id is null ? [] : [id]); RefreshTimeline(); }

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
        RefreshEffectsInspector(clip);
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
            new CurveChoice(FadeCurve.EqualPower, "等功率"), new CurveChoice(FadeCurve.Custom, "自訂曲線")
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
            TryReplaceClip(ReadEffects(clip with
            {
                Start = ReadNumber("ClipStartBox"), SourceIn = ReadNumber("ClipSourceInBox"), Duration = ReadNumber("ClipDurationBox"),
                Gain = ReadNumber("ClipGainBox") / 100
            }), "已套用片段時間、音量與 Fade");
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
        _selectedTrackId = id; SetClipSelection([]);
        RemoveClipButton.IsEnabled = false; RefreshSelectionInspector();
    }

    private void BeginPointerEdit(TimelineLane lane, Clip clip, Point point)
    {
        if (lane.IsLocked) return;
        _pointerMode = lane.HitEdit(clip.Start, clip.Duration, clip.FadeIn?.Duration ?? 0, clip.FadeOut?.Duration ?? 0, point);
        _pointerOriginal = clip; _pointerCandidate = null; _pointerLane = lane; _pointerOrigin = point;
        _pointerDownTime = TimelineOffsetSeconds + point.X / TimelinePixelsPerSecond;
        _pointerRawTime = _pointerEffectiveTime = _pointerDownTime;
        _pointerGrabOffset = _pointerDownTime - clip.Start;
        if (_pointerMode == PointerEdit.FadeIn) _pointerGrabOffset -= clip.FadeIn?.Duration ?? 0;
        else if (_pointerMode == PointerEdit.FadeOut) _pointerGrabOffset -= clip.Duration - (clip.FadeOut?.Duration ?? 0);
        _pointerMoved = false;
        lane.CaptureMouse();
        lane.Cursor = _pointerMode == PointerEdit.Move ? Cursors.SizeAll : Cursors.SizeWE;
    }

    private void TimelineLane_MouseMove(object sender, MouseEventArgs e)
    {
        if (IsMarqueeSelecting)
        {
            if (e.LeftButton != MouseButtonState.Pressed) EndMarqueeSelection(cancel: true);
            else UpdateMarqueeFromPointer(e);
            e.Handled = true; return;
        }
        if (_pointerOriginal is null || _pointerLane is not { } captured)
        {
            if (sender is TimelineLane hovered)
            {
                var time = TimelineOffsetSeconds + e.GetPosition(hovered).X / TimelinePixelsPerSecond;
                var hit = hovered.Clips?.FirstOrDefault(c => c.Start <= time && time < c.Start + c.Duration);
                hovered.ToolTip = hit is null ? null : $"{hit.Name}\n起點 {FormatDuration(hit.Start)} · 長度 {FormatDuration(hit.Duration)}";
            }
            return;
        }
        if (e.LeftButton != MouseButtonState.Pressed) { CancelPointerEdit(); return; }
        var local = e.GetPosition(captured);
        if (!_pointerMoved && Math.Abs(local.X - _pointerOrigin.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(local.Y - _pointerOrigin.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        ScrollTracksDuringDrag(e.GetPosition(TimelineContent).Y);
        var target = _pointerMode == PointerEdit.Move ? FindLanes(TrackItems).FirstOrDefault(lane =>
        {
            var point = e.GetPosition(lane);
            return point.X >= 0 && point.X <= lane.ActualWidth && point.Y >= 0 && point.Y <= lane.ActualHeight;
        }) : captured;
        UpdatePointerEditAt(target, target is null ? default : e.GetPosition(target));
        e.Handled = true;
    }

    private void UpdatePointerEditAt(TimelineLane? target, Point point, ModifierKeys? modifiers = null)
    {
        if (_pointerOriginal is not { } original || _pointerLane is null) return;
        MediaWorkScheduler.NotifyInteraction();
        _pointerMoved = true;
        foreach (var lane in FindLanes(TrackItems)) lane.SetDropPreview(null, 0, false);
        if (target is null) { _pointerCandidate = null; HidePointerFeedback(); StatusText.Text = "請將片段拖至相容軌道內"; return; }
        ScrollDuringDrag(point.X, target.ActualWidth);
        var rawTime = Math.Max(0, TimelineOffsetSeconds + point.X / TimelinePixelsPerSecond);
        var fine = (modifiers ?? Keyboard.Modifiers).HasFlag(ModifierKeys.Shift);
        _pointerEffectiveTime += (rawTime - _pointerRawTime) * (fine ? .1 : 1);
        _pointerRawTime = rawTime;
        rawTime = Math.Max(0, _pointerEffectiveTime);
        _snapGuideTime = null; _snapGuideLabel = null;
        try
        {
            var next = _pointerMode switch
            {
                PointerEdit.TrimStart => PlanPointerTrim(original, true, _pointerDownTime, rawTime),
                PointerEdit.TrimEnd => PlanPointerTrim(original, false, _pointerDownTime, rawTime),
                PointerEdit.FadeIn => original with { FadeIn = (original.FadeIn ?? new()) with { Duration = Math.Clamp(TimelinePlacement.RoundToFrame(Math.Max(0, rawTime - _pointerGrabOffset - original.Start), _project.Video.Fps), 0, original.Duration), RangeStart = 0, RangeEnd = 1 } },
                PointerEdit.FadeOut => original with { FadeOut = (original.FadeOut ?? new()) with { Duration = Math.Clamp(TimelinePlacement.RoundToFrame(Math.Max(0, original.End - rawTime + _pointerGrabOffset), _project.Video.Fps), 0, original.Duration), RangeStart = 0, RangeEnd = 1 } },
                _ => original with { Start = SnapEditTime(Math.Max(0, rawTime - _pointerGrabOffset), original.Id, original.Duration), TrackId = (string)target.Tag }
            };
            var allowed = true;
            var reason = "";
            try
            {
                if (_pointerMode == PointerEdit.Move) { allowed = TryPreviewGroupMove(next); reason = StatusText.Text; }
                else PlanLinkedReplacement(next);
            }
            catch (ProjectValidationException error) { allowed = false; reason = error.Message; }
            _pointerCandidate = allowed ? next : null;
            if (allowed && _pointerMode is PointerEdit.FadeIn or PointerEdit.FadeOut)
                target.SetFadePreview(next);
            else if (_pointerMode != PointerEdit.Move) target.SetDropPreview(next.Start, next.Duration, allowed);
            var action = _pointerMode switch { PointerEdit.Move => "移動", PointerEdit.FadeIn => $"淡入 {next.FadeIn!.Duration:0.###} 秒", PointerEdit.FadeOut => $"淡出 {next.FadeOut!.Duration:0.###} 秒", _ => "修剪" };
            StatusText.Text = allowed ? $"{action} · 起點 {FormatDuration(next.Start)} · 長度 {FormatDuration(next.Duration)} · 放開套用"
                : reason;
            var change = _pointerMode switch
            {
                PointerEdit.FadeIn => (next.FadeIn?.Duration ?? 0) - (original.FadeIn?.Duration ?? 0),
                PointerEdit.FadeOut => (next.FadeOut?.Duration ?? 0) - (original.FadeOut?.Duration ?? 0),
                PointerEdit.TrimEnd => next.End - original.End,
                _ => next.Start - original.Start
            };
            ShowPointerFeedback(target, point, allowed ? $"{action}{(fine ? " · 精細" : "")}\n起點 {next.Start:0.###} 秒 · 長度 {next.Duration:0.###} 秒 · Δ {change:+0.###;-0.###;0} 秒" : reason);
        }
        catch (ProjectValidationException error) { _pointerCandidate = null; HidePointerFeedback(); StatusText.Text = error.Message; }
    }

    internal Clip PlanPointerTrim(Clip original, bool trimStart, double pointerDownTime, double pointerTime)
    {
        var asset = _project.MediaAssets.First(asset => asset.Id == original.AssetId);
        var duration = asset.Duration;
        var image = asset.Kind == MediaKind.Image;
        var minimum = Math.Min(1 / _project.Video.Fps, original.Duration);
        // The press may be several pixels inside the edge. Apply only its drag delta to that edge.
        var originalEdge = trimStart ? original.Start : original.End;
        var edge = SnapEditTime(Math.Max(0, originalEdge + pointerTime - pointerDownTime), original.Id);
        return trimStart
            ? ClipEditor.TrimStart(original, Math.Clamp(edge, image ? 0 : Math.Max(0, original.Start - original.SourceIn), original.End - minimum), duration, image)
            : ClipEditor.TrimEnd(original, Math.Clamp(edge, original.Start + minimum, image ? double.MaxValue : original.Start + duration - original.SourceIn), duration, image);
    }

    internal double SnapEditTime(double time, string clipId, double duration = 0)
    {
        var result = TimelinePlacement.RoundToFrame(time, _project.Video.Fps);
        _snapGuideTime = null; _snapGuideLabel = null;
        if (!ShouldSnapTimeline) return result;
        var tolerance = Math.Min(.25, 8 / TimelinePixelsPerSecond);
        var selected = SelectionIds();
        var boundaries = _project.Clips.Where(clip => clip.Id != clipId && !selected.Contains(clip.Id))
            .SelectMany(clip => new[] { (Time: clip.Start, Label: "片段起點"), (Time: clip.End, Label: "片段終點") })
            .Prepend((0d, "時間軸起點")).Append((PlayheadSeconds, "播放頭"));
        var primary = _project.Clips.FirstOrDefault(c => c.Id == clipId);
        var offsets = duration > 0 && primary is not null && selected.Contains(primary.Id)
            ? _project.Clips.Where(c => selected.Contains(c.Id)).SelectMany(c => new[] { c.Start - primary.Start, c.End - primary.Start }).ToArray()
            : duration > 0 ? new[] { 0.0, duration } : new[] { 0.0 };
        var candidates = boundaries.SelectMany(edge => offsets.Select(offset => (Time: edge.Item1 - offset, Edge: edge.Item1, Label: edge.Item2))).Where(value => value.Time >= 0);
        var nearest = candidates.MinBy(value => Math.Abs(value.Time - result));
        if (Math.Abs(nearest.Time - result) > tolerance) return result;
        _snapGuideTime = nearest.Edge; _snapGuideLabel = nearest.Label;
        return nearest.Time;
    }

    private void TimelineLane_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (IsMarqueeSelecting)
        { UpdateMarqueeFromPointer(e, autoScroll: false); EndMarqueeSelection(cancel: false); e.Handled = true; return; }
        var candidate = _pointerCandidate;
        var moved = _pointerMoved;
        var moveSelection = _pointerMode == PointerEdit.Move;
        CancelPointerEdit();
        if (moved && candidate is not null) CommitBatch(() => PlanLinkedReplacement(candidate, moveSelection), "已更新選取片段 · Ctrl+Z 復原");
        e.Handled = true;
    }
    private void TimelineLane_LostCapture(object sender, MouseEventArgs e)
    { if (IsMarqueeSelecting) EndMarqueeSelection(cancel: true); CancelPointerEdit(); }
    private void CancelPointerEdit()
    {
        var lane = _pointerLane;
        _pointerOriginal = null; _pointerCandidate = null; _pointerLane = null; _pointerMoved = false;
        HidePointerFeedback();
        if (lane is not null) { lane.Cursor = Cursors.Arrow; if (lane.IsMouseCaptured) lane.ReleaseMouseCapture(); }
        if (TrackItems is not null) foreach (var item in FindLanes(TrackItems)) item.SetDropPreview(null, 0, false);
    }
}
