using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CutMaker.Core;

namespace CutMaker.App;

internal static class TrackControlsSmokeChecks
{
    internal static Task RunAsync(MainWindow window, string folder, Action<string>? capture = null)
        => window.RunTrackControlsSmokeAsync(folder, capture);
}

public partial class MainWindow
{
    internal async Task RunTrackControlsSmokeAsync(string folder, Action<string>? capture)
    {
        var report = Path.Combine(folder, "track-controls-result.txt");
        File.WriteAllText(report, "RUNNING: compact track controls and independent MUTE.");
        var original = _project; var originalPath = _projectPath;
        var dirty = _dirty; var undo = _undo.ToArray(); var redo = _redo.ToArray();
        var selected = SelectedTimelineClipId; var selection = SelectedTimelineClipIds?.ToArray();
        var selectedTrack = _selectedTrackId; var collapsed = _collapsedTracks.ToArray();
        var layout = CaptureLayout(); var minWidth = MinWidth; var minHeight = MinHeight;
        var zoom = TimelineZoom.Value; var scale = TimelinePixelsPerSecond; var offset = TimelineOffsetSeconds;
        var playhead = PlayheadSeconds;
        void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Track controls smoke check failed: " + message); }
        TimelineLane Lane(string id) => FindLanes(TrackItems).Single(lane => (string)lane.Tag == id);
        bool HasRowHeight(TimelineLane lane, double expected) =>
            Math.Abs(lane.ActualHeight - expected) <= .51 / VisualTreeHelper.GetDpi(lane).DpiScaleY;
        Button RowButton(string track, string name) => Descendants(TrackItems).OfType<Button>()
            .Single(button => button.Name == name && Equals(button.Tag, track));
        void Click(string id, string name)
        {
            RowButton(id, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            UpdateLayout();
        }
        try
        {
            var audio = original.MediaAssets.Where(asset => asset.Kind == MediaKind.Audio && asset.Duration >= .7)
                .OrderByDescending(asset => asset.Duration).First();
            var image = original.MediaAssets.First(asset => asset.Kind == MediaKind.Image);
            var duration = Math.Min(6, audio.Duration - .1);
            var fixture = CutProject.CreateEmpty("CutMaker · 收折軌道與獨立靜音") with
            {
                MediaAssets = [.. original.MediaAssets],
                Tracks =
                [
                    new("compact-v1", "影片一 · 背景畫面", TrackKind.Video),
                    new("compact-a1", "音訊一 · 很長的訪談與背景配樂名稱（完整名稱保留在提示）", TrackKind.Audio),
                    new("compact-v2", "影片二 · 保持展開", TrackKind.Video),
                    new("compact-a2", "音訊二 · 獨立靜音", TrackKind.Audio, Locked: true)
                ],
                Clips =
                [
                    new("compact-picture", image.Id, "compact-v1", 0, 0, 8, FadeIn: new(.8), FadeOut: new(.8)),
                    new("compact-audio", audio.Id, "compact-a1", 1, 0, duration, FadeIn: new(.12), FadeOut: new(.12)),
                    new("compact-second-audio", audio.Id, "compact-a2", 2, 0, duration)
                ]
            };
            var path = Path.Combine(folder, "track-controls.cutmaker");
            LoadSmokeProject(fixture, path);
            ApplyLayout(layout with { TimelineHeight = 390 });
            TimelineZoom.Value = 90; TimelinePixelsPerSecond = 90; TimelineOffsetSeconds = 0;
            SetClipSelection(["compact-audio"], "compact-audio");
            RefreshTimeline(); UpdateLayout();
            var sentinel = CaptureEdit(); _undo.Add(sentinel); _redo.Add(sentinel); RefreshHistoryButtons();
            var before = JsonSerializer.Serialize(_project);
            var previewRevision = _previewRevision;
            Click("compact-a1", "CollapseTrackButton");
            Require(Lane("compact-a1").IsCompact && HasRowHeight(Lane("compact-a1"), 30) && HasRowHeight(Lane("compact-v2"), 86),
                $"Collapse changes only its track to one-line height while other rows stay expanded. Actual: compact={Lane("compact-a1").IsCompact}, " +
                $"height={Lane("compact-a1").ActualHeight:R}, expanded={Lane("compact-v2").ActualHeight:R}, DPI={VisualTreeHelper.GetDpi(Lane("compact-a1")).DpiScaleY:R}.");
            Require(before == JsonSerializer.Serialize(_project) && !_dirty && _undo.Count == 1 && _redo.Count == 1 &&
                ReferenceEquals(_undo[0], sentinel) && ReferenceEquals(_redo[0], sentinel) && _previewRevision == previewRevision,
                "Collapsing must preserve media, timing, dirty state, Undo/Redo and prepared preview.");
            Require(SelectedTimelineClipId == "compact-audio", "Collapse preserves clip selection.");
            Require(RowButton("compact-v1", "MuteTrackButton").Visibility == Visibility.Collapsed &&
                RowButton("compact-a1", "MuteTrackButton").ActualHeight <= 24,
                "MUTE belongs to audio rows and remains fully visible when collapsed.");
            var longName = fixture.Tracks[1].Name;
            var label = Descendants(TrackItems).OfType<TextBlock>().Single(text => text.Text == longName);
            Require(label.TextTrimming == TextTrimming.CharacterEllipsis && Equals(label.ToolTip, longName),
                "Track title is one line with an unabridged tooltip.");

            var originalClip = _project.Clips.Single(clip => clip.Id == "compact-audio");
            var lane = Lane("compact-a1");
            var left = originalClip.Start * TimelinePixelsPerSecond;
            var right = originalClip.End * TimelinePixelsPerSecond;
            void Hit(Point point, PointerEdit expected)
            {
                Require(ReferenceEquals(lane.InputHitTest(point), lane), "Compact lane still accepts pointer input.");
                BeginPointerEdit(lane, originalClip, point, captureMouse: false);
                try { Require(_pointerMode == expected, $"Compact hit target selects {expected}, got {_pointerMode}."); }
                finally { CancelPointerEdit(); }
            }
            Hit(new((left + right) / 2, 20), PointerEdit.Move);
            Hit(new(left + 1, 20), PointerEdit.TrimStart);
            Hit(new(right - 1, 20), PointerEdit.TrimEnd);
            Hit(new(left + .12 * TimelinePixelsPerSecond, 5), PointerEdit.FadeIn);
            Hit(new(right - .12 * TimelinePixelsPerSecond, 5), PointerEdit.FadeOut);
            Require(TryCommitGroupMove(originalClip with { Start = originalClip.Start + .5 }), "Collapsed clip can move using the shared group edit path.");
            UndoEdit(); Require(_project.Clips.Single(clip => clip.Id == originalClip.Id) == originalClip && IsTrackCollapsed(originalClip.TrackId),
                "Undo restores timing without expanding the track.");
            Require(TryReplaceClip(PlanPointerTrim(originalClip, true, originalClip.Start, originalClip.Start + .1), "Compact trim"),
                "Collapsed clip can trim using the shared pointer plan.");
            UndoEdit();
            SelectTimelineClipForChecks(originalClip.Id);
            Require(SplitSelectedClip(originalClip.Start + originalClip.Duration / 2) &&
                _project.Clips.Count(clip => clip.TrackId == originalClip.TrackId) == 2 && Lane(originalClip.TrackId).IsCompact,
                "Razor split updates the compact row immediately.");
            UndoEdit();

            Click("compact-v1", "CollapseTrackButton");
            for (var attempt = 0; attempt < 200 && _assetVisualPending.Count != 0; attempt++) await Task.Delay(50);
            Require(Lane("compact-a1").Clips!.Single().Waveform is not null && Lane("compact-v1").Clips!.Single().Thumbnail is not null,
                "Compact rendering retains the real source waveform and image overview.");
            VerifyLayoutBounds(); capture?.Invoke("workspace-tracks-compact.png");

            var unmuted = _project.Tracks.Single(track => track.Id == "compact-a1");
            var oldBackground = RowButton("compact-a1", "MuteTrackButton").Background.ToString();
            previewRevision = _previewRevision;
            Click("compact-a1", "MuteTrackButton");
            Require(_project.Tracks.Single(track => track.Id == "compact-a1").Muted &&
                !_project.Tracks.Single(track => track.Id == "compact-a2").Muted && _dirty,
                "MUTE changes only the chosen audio track and is a saved project edit.");
            Require(!PreviewIsReady && !_previewPlaying && PreviewPlayer.Source is null && _previewRevision > previewRevision,
                "MUTE invalidates old preview immediately so cached sound cannot continue.");
            Require(oldBackground != RowButton("compact-a1", "MuteTrackButton").Background.ToString() &&
                Equals(RowButton("compact-a1", "MuteTrackButton").ToolTip, "解除此軌道靜音"),
                "Muted button has a distinct persistent state and accurate tooltip.");
            ProjectStore.Save(path, _project);
            Require(ProjectStore.Load(path).Tracks.Single(track => track.Id == "compact-a1").Muted,
                "MUTE survives project save/reopen and uses the existing render track flag.");
            capture?.Invoke("workspace-tracks-muted.png");
            UndoEdit(); Require(_project.Tracks.Single(track => track.Id == unmuted.Id) == unmuted && IsTrackCollapsed(unmuted.Id),
                "One Undo restores the original track without changing its height.");
            RedoEdit(); Require(_project.Tracks.Single(track => track.Id == unmuted.Id).Muted, "Redo restores MUTE.");
            Click("compact-a1", "MuteTrackButton"); Require(!_project.Tracks.Single(track => track.Id == unmuted.Id).Muted,
                "Pressing MUTE again unmutes the same track.");
            Click("compact-a2", "MuteTrackButton"); Require(_project.Tracks.Single(track => track.Id == "compact-a2").Muted,
                "Clip editing lock does not prevent intentional monitoring mute.");

            Click("compact-v2", "CollapseTrackButton"); Click("compact-a2", "CollapseTrackButton");
            MinWidth = 900; MinHeight = 580; Width = 900; Height = 580;
            ClampPanels(); UpdateLayout(); ClampPanels(); UpdateLayout();
            VerifyLayoutBounds(); capture?.Invoke("workspace-tracks-compact-small.png");
            Require(FindLanes(TrackItems).All(item => HasRowHeight(item, 30)), "All compact tracks stay one line at small display size.");
            LoadSmokeProject(fixture, path); UpdateLayout();
            Require(FindLanes(TrackItems).All(item => !item.IsCompact && HasRowHeight(item, 86)),
                "Changing projects clears transient collapsed track state, including reused IDs.");
            File.WriteAllText(report,
                "PASS: independent 30px track collapse; one-line long names with full tooltips; view state leaves project/dirty/history/preview untouched; " +
                "compact real waveform/thumbnail/Fade visualization; pointer move, both trim edges and Fade hits; move/trim/split and Undo; " +
                "audio-only MUTE in both row sizes, distinct active styling, independent tracks, immediate cached preview invalidation, project persistence and Undo/Redo; " +
                "small-display bounds and project-switch state reset.\n");
        }
        finally
        {
            CancelPointerEdit();
            LoadSmokeProject(original, originalPath ?? Path.Combine(folder, "before-track-controls.cutmaker"));
            _projectPath = originalPath; _dirty = dirty;
            _undo.Clear(); _undo.AddRange(undo); _redo.Clear(); _redo.AddRange(redo);
            _collapsedTracks.UnionWith(collapsed); _selectedTrackId = selectedTrack;
            SetClipSelection(selection ?? [], selected);
            MinWidth = minWidth; MinHeight = minHeight; ApplyLayout(layout);
            TimelineZoom.Value = zoom; TimelinePixelsPerSecond = scale; TimelineOffsetSeconds = offset; PlayheadSeconds = playhead;
            RefreshProject(); RefreshHistoryButtons(); UpdateLayout();
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
