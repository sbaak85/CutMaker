using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using CutMaker.Core;

namespace CutMaker.App;

internal static class ZoomRangeSmokeChecks
{
    internal static void Run(MainWindow window, string folder, Action<string>? capture = null)
        => window.RunZoomRangeSmoke(folder, capture);
}

public partial class MainWindow
{
    internal void RunZoomRangeSmoke(string folder, Action<string>? capture)
    {
        Directory.CreateDirectory(folder);
        var report = Path.Combine(folder, "zoom-range-result.txt");
        File.WriteAllText(report, "RUNNING: quarter-width minimum timeline zoom checks.");
        var original = _project;
        var originalPath = _projectPath;
        var dirty = _dirty;
        var undo = _undo.ToArray(); var redo = _redo.ToArray();
        var selected = SelectedTimelineClipId; var selectedIds = SelectedTimelineClipIds?.ToArray();
        var selectedTrack = _selectedTrackId; var playhead = PlayheadSeconds;
        var scale = TimelinePixelsPerSecond; var offset = TimelineOffsetSeconds;
        var layout = CaptureLayout(); var status = StatusText.Text;
        void Require(bool condition, string text)
        { if (!condition) throw new InvalidOperationException("Zoom range smoke check failed: " + text); }
        void Near(double expected, double actual, string text)
            => Require(double.IsFinite(actual) && Math.Abs(expected - actual) < 1e-8 * Math.Max(1, Math.Abs(expected)),
                $"{text}: expected {expected:R}, got {actual:R}");
        void CheckMinimum(double duration, string label)
        {
            var expected = Math.Min(10, TimelineViewportWidth * .25 / (duration > 0 ? duration : 60));
            Near(expected, TimelineZoom.Minimum, label + " derives its minimum from the current viewport and duration");
            TimelineZoom.Value = 40;
            for (var count = 0; count < 12; count++) TryZoomTimelineWheel(-2400, ModifierKeys.Control, 0);
            Near(expected, TimelinePixelsPerSecond, label + " wheel reaches the minimum without stopping at fit");
            Near(TimelinePixelsPerSecond, TimelineZoom.Value, label + " keeps slider synchronized");
            Near(0, TimelineOffsetSeconds, label + " keeps the whole range visible from zero");
            Require((duration > 0 ? duration : 60) * TimelinePixelsPerSecond <= TimelineViewportWidth * .25 + 1e-8,
                label + " renders the full timeline in at most one quarter of the lane width");
        }
        try
        {
            InvalidatePreview();
            var image = original.MediaAssets.FirstOrDefault(asset => asset.Kind == MediaKind.Image)
                ?? new MediaAsset("zoom-image", Path.Combine(folder, "zoom-unused.png"), MediaKind.Image, 5);
            _project = CutProject.CreateEmpty("CutMaker · 時間軸 25% 縮放驗證") with
            {
                MediaAssets = [image],
                Clips = [new("zoom-main", image.Id, "video-1", 0, 0, 300)]
            };
            _projectPath = null; _dirty = false;
            SetClipSelection(["zoom-main"], "zoom-main");
            RefreshProject(); UpdateLayout(); UpdateTimelineViewport();
            _undo.Clear(); _redo.Clear();
            var sentinel = CaptureEdit(); _undo.Add(sentinel); _redo.Add(sentinel);
            var contents = JsonSerializer.Serialize(_project);
            PlayheadSeconds = 12.5;
            CheckMinimum(300, "Five-minute project");
            capture?.Invoke("workspace-quarter-zoom.png");
            FitTimelineToView();
            Near(1 / 1.04, 300 * TimelinePixelsPerSecond / TimelineViewportWidth, "Ctrl+0 still fits the project with a four-percent margin");
            Require(TimelinePixelsPerSecond > TimelineZoom.Minimum * 3.8,
                "Fit and minimum zoom are distinct scales");
            CheckMinimum(300, "Zooming out after fit");
            Require(contents == JsonSerializer.Serialize(_project) && !_dirty &&
                _undo.Count == 1 && _redo.Count == 1 && ReferenceEquals(_undo[0], sentinel) && ReferenceEquals(_redo[0], sentinel) &&
                SelectedTimelineClipId == "zoom-main" && SelectionIds().SetEquals(["zoom-main"]),
                "Zoom and fit preserve project, dirty state, selection and Undo/Redo");
            Near(12.5, PlayheadSeconds, "Zoom and fit preserve playhead");

            // Check the cursor anchor with room on both sides, including a zoom-out step.
            TimelineZoom.Value = 40; TimelineOffsetSeconds = 50;
            var pointer = Math.Min(200, TimelineViewportWidth * .4);
            var anchoredTime = TimelineOffsetSeconds + pointer / TimelinePixelsPerSecond;
            TryZoomTimelineWheel(-120, ModifierKeys.Control, pointer);
            Near(anchoredTime, TimelineOffsetSeconds + pointer / TimelinePixelsPerSecond, "Wheel out preserves time beneath cursor");
            TryZoomTimelineWheel(120, ModifierKeys.Control, pointer);
            Near(40, TimelineZoom.Value, "Opposite wheel restores scale");
            Near(50, TimelineOffsetSeconds, "Opposite wheel restores offset");

            var oldMinimum = TimelineZoom.Minimum;
            Width += 120; UpdateLayout();
            Require(TimelineZoom.Minimum != oldMinimum, "Window resize updates minimum without another wheel or fit command");
            CheckMinimum(300, "Resized viewport");
            var headerWidth = TrackHeaderWidth;
            oldMinimum = TimelineZoom.Minimum;
            TrackHeaderColumn.Width = new GridLength(headerWidth + 80);
            UpdateLayout();
            TrackHeader_DragDelta(this, new DragDeltaEventArgs(80, 0));
            Require(TimelineZoom.Minimum < oldMinimum, "Resizing the track header updates the usable-lane minimum during the drag");
            CheckMinimum(300, "Resized track header");

            // Growing/shrinking edits update the range immediately, independent of opening Fit.
            _project = _project with { Clips = [.. _project.Clips, new("zoom-tail", image.Id, "video-1", 900, 0, 10)] };
            RefreshTimeline();
            CheckMinimum(910, "Added distant clip (including empty time before it)");
            _project = _project with { Clips = [new("zoom-main", image.Id, "video-1", 0, 0, 300)] };
            RefreshTimeline();
            CheckMinimum(300, "Removed distant clip");
            _project = _project with { Clips = [new("zoom-day", image.Id, "video-1", 0, 0, 86400)] };
            RefreshTimeline();
            CheckMinimum(86400, "Day-long project");
            _project = _project with { Clips = [new("zoom-short", image.Id, "video-1", 0, 0, 1.0 / 30)] };
            RefreshTimeline();
            CheckMinimum(1.0 / 30, "One-frame project");
            Require(TimelineZoom.Minimum < TimelineZoom.Maximum, "Very short clips keep a usable zoom-in range");
            _project = _project with { Clips = [] };
            RefreshTimeline();
            CheckMinimum(0, "Empty one-minute reference span");
            Require(double.IsFinite(TimelineHorizontalScroll.Maximum) && double.IsFinite(TimelineHorizontalScroll.ViewportSize),
                "Empty project viewport remains finite");

            File.WriteAllText(report,
                "PASS: Ctrl-wheel reaches a quarter-width project overview before and after Fit; independent Fit remains approximately 96%; " +
                "slider synchronization; cursor anchor in both directions; dynamic bounds during window and track-header resizing; " +
                "clip additions, removals and distant ends including leading gaps; five-minute, day-long, one-frame and empty timelines; " +
                "view-only operations preserve project contents, selection, dirty state, playhead and Undo/Redo.");
        }
        finally
        {
            _project = original; _projectPath = originalPath; _dirty = dirty;
            _undo.Clear(); _undo.AddRange(undo); _redo.Clear(); _redo.AddRange(redo);
            SelectedTimelineClipId = selected; SelectedTimelineClipIds = selectedIds; _selectedTrackId = selectedTrack;
            ApplyLayout(layout); RefreshProject(); UpdateLayout();
            TimelineZoom.Value = scale; TimelinePixelsPerSecond = scale; UpdateTimelineViewport();
            RefreshHistoryButtons(); InvalidatePreview(); SeekPreview(playhead);
            TimelineOffsetSeconds = Math.Clamp(offset, 0, TimelineHorizontalScroll.Maximum);
            StatusText.Text = status;
        }
    }
}
