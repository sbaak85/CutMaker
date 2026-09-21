using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using CutMaker.Core;

namespace CutMaker.App;

public partial class MainWindow
{
    internal async Task RunInteractionSmokeAsync(string folder, Action<string> capture)
    {
        var original = _project; var path = _projectPath; var dirty = _dirty; var undo = _undo.ToArray(); var redo = _redo.ToArray();
        var preferences = EditorPreferences.Current; var layout = CaptureLayout(); var zoom = TimelineZoom.Value; var offset = TimelineOffsetSeconds;
        var selected = SelectedTimelineClipId; var selection = SelectedTimelineClipIds?.ToArray(); var loop = _auditionLoop;
        var report = new List<string>();
        void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Interaction UI: " + message); report.Add("PASS: " + message); }
        async Task Until(Func<bool> ready, string message)
        {
            var end = DateTime.UtcNow.AddSeconds(7);
            while (!ready() && DateTime.UtcNow < end) await Task.Delay(20);
            Check(ready(), message);
        }
        try
        {
            var image = original.MediaAssets.First(asset => asset.Kind == MediaKind.Image);
            var fixture = new CutProject { MediaAssets = [image], Tracks = [new("a", "A", TrackKind.Video), new("b", "B", TrackKind.Video)],
                Clips = [new("a", image.Id, "a", 2, 0, 12, FadeIn: new(.8)), new("b", image.Id, "b", 4, 0, 2)] };
            var fixturePath = Path.Combine(folder, "interaction.cutmaker");
            ProjectStore.Save(fixturePath, fixture); LoadSmokeProject(fixture, fixturePath);
            TimelineZoom.Value = 80; TimelinePixelsPerSecond = 80; TimelineOffsetSeconds = 1;
            SelectTimelineClipForChecks("a"); UpdateLayout();
            PlayheadSeconds = 6;
            Check(TryHandleNavigationShortcut(Key.W, ModifierKeys.None) && PlayheadSeconds == 2, "W navigates to the primary clip start");
            Check(TryHandleNavigationShortcut(Key.S, ModifierKeys.None) && PlayheadSeconds == 14, "S navigates to the primary clip end");
            PlayheadSeconds = 5;
            Check(TryHandleNavigationShortcut(Key.A, ModifierKeys.None) && Math.Abs(PlayheadSeconds - (5 - 1 / _project.Video.Fps)) < 1e-8, "A steps backward one project frame");
            Check(TryHandleNavigationShortcut(Key.D, ModifierKeys.None) && Math.Abs(PlayheadSeconds - 5) < 1e-8, "D steps forward one project frame");
            ShowSnapHint(true); UpdateLayout(); capture("workspace-snap-toggle-hint.png");
            await Task.Delay(500);
            Check(SnapToggleHint.Visibility == Visibility.Visible && SnapToggleHint.Opacity > .99, "snap hint remains fully visible during its one-second hold");
            ShowSnapHint(false);
            Check(SnapToggleHintText.Text == "吸附已關閉" && SnapToggleHint.Opacity > .99, "new snap toggle replaces and restarts the hint");
            await Until(() => SnapToggleHint.Visibility == Visibility.Collapsed, "snap hint disappears after hold and fade");
            PlayheadSeconds = 6;
            var trimUndo = _undo.Count;
            Check(TryHandleClipShortcut(Key.OemOpenBrackets, ModifierKeys.None, false) &&
                _project.Clips.Single(c => c.Id == "a").Start == 6 && _project.Clips.Count == 2 && _undo.Count == trimUndo + 1,
                "left bracket trims the head without splitting and records one Undo");
            UndoEdit();
            Check(TryHandleClipShortcut(Key.OemCloseBrackets, ModifierKeys.None, false) &&
                _project.Clips.Single(c => c.Id == "a").End == 6 && _project.Clips.Count == 2,
                "right bracket trims the tail without creating a clip");
            UndoEdit();
            PlayheadSeconds = 1;
            Check(!TrimSelectedToPlayhead(true) && _project.Clips.SequenceEqual(fixture.Clips), "outside playhead cannot extend or trim a clip");
            PlayheadSeconds = 6;
            Check(!TryHandleClipShortcut(Key.B, ModifierKeys.Control, false), "Ctrl+B no longer invokes splitting");
            Check(TryHandleClipShortcut(Key.B, ModifierKeys.None, false) && _project.Clips.Count == 3, "plain B splits at the playhead");
            UndoEdit(); SelectTimelineClipForChecks("a");
            Check(TimelineTimeRuler.ActualHeight >= 42, "ruler offers a taller 42-DIP drag surface");
            var lane = FindLanes(TrackItems).Single(item => Equals(item.Tag, "a"));
            var gestureUndo = _undo.Count;
            BeginTimelineBlankGesture(3, lane, new(0, 20), ModifierKeys.None, captureMouse: false);
            Check(!IsMarqueeSelecting && _previewScrubbing && PlayheadSeconds == 3 && SelectedTimelineClipId == "a" && _undo.Count == gestureUndo,
                "plain empty-space gesture seeks and preserves selection without creating an edit");
            EndPreviewScrub(resumePlayback: false);
            BeginTimelineBlankGesture(1, lane, new(0, 20), ModifierKeys.Control, captureMouse: false);
            Check(IsMarqueeSelecting && !_previewScrubbing, "Ctrl empty-space gesture starts marquee instead of scrubbing");
            EndMarqueeSelection(cancel: true);
            SetClipSelection(["a", "b"], "a");
            BeginTimelineBlankGesture(1, lane, new(0, 20), ModifierKeys.Control, captureMouse: false);
            UpdateMarqueeSelection(lane.TranslatePoint(new Point(240, 60), TrackItems));
            EndMarqueeSelection(cancel: false);
            Check(SelectionIds().SetEquals(["a"]), "single-row Ctrl marquee replaces stale selections on other tracks");
            SetClipSelection(["b"], "b");
            BeginTimelineBlankGesture(1, lane, new(0, 20), ModifierKeys.Control | ModifierKeys.Shift, captureMouse: false);
            UpdateMarqueeSelection(lane.TranslatePoint(new Point(240, 60), TrackItems));
            EndMarqueeSelection(cancel: false);
            Check(SelectionIds().SetEquals(["a", "b"]), "Ctrl Shift marquee explicitly adds another row");
            BeginPointerEdit(lane, _project.Clips.First(c => c.Id == "a"), new(240, 40), captureMouse: false);
            TimelineLane_MouseUp(lane, new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.MouseUpEvent });
            Check(SelectionIds().SetEquals(["a"]), "click and release exits a multi-selection without moving clips");
            capture("workspace-selection-primary.png");
            var clip = _project.Clips[0]; var x = (clip.Start + .8 - TimelineOffsetSeconds) * TimelinePixelsPerSecond;
            Check(lane.HitEdit(clip.Start, clip.Duration, .8, 0, new(x + 9, 10)) == PointerEdit.FadeIn,
                "expanded Fade handle accepts a point nine pixels away");
            BeginPointerEdit(lane, clip, new(x, 10), captureMouse: false);
            UpdatePointerEditAt(lane, new(x + 80, 10), ModifierKeys.Shift);
            Check(Math.Abs(_pointerCandidate!.FadeIn!.Duration - .9) < 1e-8 && TimelineDragHint.Visibility == Visibility.Visible,
                "Shift drag advances Fade by one tenth and displays an in-place numeric hint");
            UpdatePointerEditAt(lane, new(x + 160, 10), ModifierKeys.None);
            Check(Math.Abs(_pointerCandidate!.FadeIn!.Duration - 1.9) < 1e-8, "releasing Shift changes sensitivity without a pointer jump");
            capture("workspace-precision-drag.png"); CancelPointerEdit();
            Check(TimelineDragHint.Visibility == Visibility.Collapsed && _project.Clips[0] == clip, "cancel hides drag feedback and preserves the clip");
            PlayheadSeconds = 5;
            Check(Math.Abs(SnapEditTime(4.98, clip.Id) - 5) < 1e-8 && _snapGuideLabel == "播放頭", "snapping identifies the playhead as an alignment target");
            ShowPointerFeedback(lane, new(x, 20), "吸附驗證");
            Check(TimelineSnapLine.Visibility == Visibility.Visible && TimelineSnapText.Text.Contains("播放頭"), "snap guide displays its actual target");
            capture("workspace-snap-guide.png"); HidePointerFeedback();
            var oldScale = TimelinePixelsPerSecond; var oldOffset = TimelineOffsetSeconds;
            Check(TryPanTimelineWheel(-120, ModifierKeys.Shift) && TimelineOffsetSeconds > oldOffset && TimelinePixelsPerSecond == oldScale,
                "Shift wheel pans horizontally without changing zoom");
            Check(!TryPanTimelineWheel(120, ModifierKeys.None), "ordinary wheel retains vertical scrolling");
            SetClipSelection(["a", "b"], "a");
            var previous = _undo.Count;
            Check(ApplyQuickFade(false, new(3, FadeCurve.EqualPower)) && _project.Clips[0].FadeOut!.Duration == 3 &&
                _project.Clips[1].FadeOut!.Duration == 2 && _undo.Count == previous + 1, "batch quick Fade clamps short clips and records one Undo");
            UndoEdit(); Check(_project.Clips.SequenceEqual(fixture.Clips), "one Undo restores every quick-Fade target");
            SavePreferencesSafely(preferences with { FadeOutPreset = new(1.25, FadeCurve.SmoothStep), ExportFormat = 2, AudioBitrate = 320 });
            var stored = JsonSerializer.Deserialize<EditorPreferences>(File.ReadAllText(Path.Combine(EditorPreferences.Root, "preferences.json")))!;
            Check(stored.FadeOutPreset?.Duration == 1.25 && stored.ExportFormat == 2 && stored.AudioBitrate == 320,
                "Fade preset and export format/bitrate persist in the preference file");
            ToggleTrackCollapsed("b"); TimelineOffsetSeconds = 1; var contents = JsonSerializer.Serialize(_project); SaveProjectView();
            _collapsedTracks.Clear(); TimelineZoom.Value = 40; TimelineOffsetSeconds = 0; RefreshTimeline(); RestoreProjectView(); UpdateLayout();
            Check(IsTrackCollapsed("b") && Math.Abs(TimelinePixelsPerSecond - 80) < 1e-8 && Math.Abs(TimelineOffsetSeconds - 1) < 1e-8 &&
                contents == JsonSerializer.Serialize(_project), "project view restores zoom, offset and collapsed tracks without editing media");

            var source = Path.Combine(folder, "wave-detail-source.wav");
            var audio = new CutProject { MediaAssets = [new("a", source, MediaKind.Audio, 30)], Tracks = [new("a", "A", TrackKind.Audio)],
                Clips = [new("a", "a", "a", 0, 19, 4.5)] };
            LoadSmokeProject(audio, Path.Combine(folder, "audition.cutmaker"));
            SeekPreview(2.25); _auditionLoop = false; await AuditionJunctionAsync();
            Check(_auditionEnd == 4.25 && Math.Abs(_auditionStart - .25) < 1e-8 && _previewPlaying, "junction audition starts two seconds before the playhead");
            await Until(() => !_previewPlaying, "one-shot audition stops automatically");
            Check(Math.Abs(PlayheadSeconds - 4.25) < 1e-8 && _auditionEnd is null, "audio audition ends at its exact requested out point");
            SeekPreview(4.4); _auditionLoop = true; await AuditionJunctionAsync();
            var device = _audioDevice; SeekPreview(4.45, preserveAudition: true);
            await Until(() => PlayheadSeconds < 3.3, "loop audition returns to its start");
            Check(_previewPlaying && ReferenceEquals(device, _audioDevice), "loop audition reuses the prepared device");
            SeekPreview(1); Check(_auditionEnd is null, "manual navigation exits the audition range"); PausePreview();
            File.WriteAllLines(Path.Combine(folder, "interaction-result.txt"), report);
        }
        finally
        {
            CancelPointerEdit(); ClearAudition(); _auditionLoop = loop; preferences.Save();
            LoadSmokeProject(original, path ?? Path.Combine(folder, "before-interaction.cutmaker")); _projectPath = path; _dirty = dirty;
            _undo.Clear(); _undo.AddRange(undo); _redo.Clear(); _redo.AddRange(redo);
            SetClipSelection(selection ?? [], selected); ApplyLayout(layout); TimelineZoom.Value = zoom; TimelineOffsetSeconds = offset;
            RefreshProject(); RefreshHistoryButtons(); UpdateLayout();
        }
    }
}
