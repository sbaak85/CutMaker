using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using CutMaker.Core;

namespace CutMaker.App;

internal static class NavigationSmokeChecks
{
    internal static void Run(MainWindow window, string folder) => window.RunNavigationSmoke(folder);
}

public partial class MainWindow
{
    /// <summary>Runs the same commands as buttons/shortcuts, restoring the caller's project and editing state.</summary>
    internal void RunNavigationSmoke(string folder)
    {
        Directory.CreateDirectory(folder);
        var report = Path.Combine(folder, "navigation-result.txt");
        File.WriteAllText(report, "RUNNING: frame, boundary, fit and exact-time navigation checks.");
        var original = _project;
        var originalPath = _projectPath;
        var dirty = _dirty;
        var undo = _undo.ToArray(); var redo = _redo.ToArray();
        var selected = SelectedTimelineClipId; var selectedIds = SelectedTimelineClipIds?.ToArray();
        var track = _selectedTrackId; var playhead = PlayheadSeconds;
        var scale = TimelinePixelsPerSecond; var sliderScale = TimelineZoom.Value;
        var minimum = TimelineZoom.Minimum; var offset = TimelineOffsetSeconds;
        var status = StatusText.Text;
        void Require(bool condition, string text)
        { if (!condition) throw new InvalidOperationException("Navigation smoke check failed: " + text); }
        void Near(double expected, double actual, string text)
            => Require(double.IsFinite(actual) && Math.Abs(expected - actual) < 1e-8, $"{text}: expected {expected:R}, got {actual:R}");
        try
        {
            InvalidatePreview();
            var image = original.MediaAssets.FirstOrDefault(asset => asset.Kind == MediaKind.Image)
                ?? new MediaAsset("navigation-image", Path.Combine(folder, "navigation-unused.png"), MediaKind.Image, 5);
            _project = CutProject.CreateEmpty("Navigation fixture") with
            {
                Video = new VideoSettings(Fps: 30), MediaAssets = [image],
                Clips =
                [
                    new("nav-1", image.Id, "video-1", 2.003, 0, 3.012),
                    new("nav-2", image.Id, "video-1", 10.123, 0, 2.034),
                    new("nav-tail", image.Id, "video-1", 30, 0, 300.013)
                ]
            };
            _projectPath = null;
            _dirty = false;
            SetClipSelection(["nav-1", "nav-2"], "nav-1");
            RefreshProject();
            TimelineZoom.Value = 40; TimelinePixelsPerSecond = 40; TimelineOffsetSeconds = 0;
            _undo.Clear(); _redo.Clear();
            var sentinel = CaptureEdit(); _undo.Add(sentinel); _redo.Add(sentinel);
            var contents = JsonSerializer.Serialize(_project);
            void Unchanged(string context) => Require(contents == JsonSerializer.Serialize(_project) && !_dirty &&
                _undo.Count == 1 && _redo.Count == 1 && ReferenceEquals(_undo[0], sentinel) && ReferenceEquals(_redo[0], sentinel) &&
                SelectedTimelineClipId == "nav-1" && SelectionIds().SetEquals(["nav-1", "nav-2"]), context + " must preserve project, selection, dirty state and history");

            PlayheadSeconds = 1;
            Require(TryHandleNavigationShortcut(Key.Right, ModifierKeys.None), "Right must be handled");
            Near(31.0 / 30, PlayheadSeconds, "Next frame at 30 fps");
            TryHandleNavigationShortcut(Key.Left, ModifierKeys.None);
            Near(1, PlayheadSeconds, "Opposite frame restores exact frame time");
            PlayheadSeconds = 1.01; FrameBack_Click(this, new RoutedEventArgs());
            Near(1, PlayheadSeconds, "Previous frame from an off-frame seek");
            PlayheadSeconds = 1.01; FrameForward_Click(this, new RoutedEventArgs());
            Near(31.0 / 30, PlayheadSeconds, "Next frame from an off-frame seek");
            TryHandleNavigationShortcut(Key.Home, ModifierKeys.None);
            StepPreviewFrame(-1); Near(0, PlayheadSeconds, "Backward stepping clamps at zero");
            TryHandleNavigationShortcut(Key.End, ModifierKeys.None);
            Near(330.013, PlayheadSeconds, "End retains fractional project duration");
            StepPreviewFrame(1); Near(330.013, PlayheadSeconds, "Forward stepping clamps at EOF");
            StepPreviewFrame(-1); Near(330, PlayheadSeconds, "Previous frame at a fractional EOF");
            PlayheadSeconds = 12.157;
            TryHandleNavigationShortcut(Key.Right, ModifierKeys.Shift); Near(13.157, PlayheadSeconds, "Shift moves exactly one second");
            TryHandleNavigationShortcut(Key.Left, ModifierKeys.Shift); Near(12.157, PlayheadSeconds, "Shift keeps fractional offset");
            Require(!TryHandleNavigationShortcut(Key.Left, ModifierKeys.Control) &&
                !TryHandleNavigationShortcut(Key.Home, ModifierKeys.Shift), "Unassigned modified keys pass through");
            Unchanged("Frame/time navigation");

            PlayheadSeconds = 5.015;
            TryHandleNavigationShortcut(Key.Right, ModifierKeys.Alt); Near(10.123, PlayheadSeconds, "Next exact clip boundary across a gap");
            TryHandleNavigationShortcut(Key.Left, ModifierKeys.Alt); Near(5.015, PlayheadSeconds, "Previous exact clip boundary across a gap");
            PreviousBoundary_Click(this, new RoutedEventArgs()); Near(2.003, PlayheadSeconds, "Boundary navigation skips the current boundary");
            NextBoundary_Click(this, new RoutedEventArgs()); Near(5.015, PlayheadSeconds, "Boundary button uses exact non-frame endpoint");
            SelectedStart_Click(this, new RoutedEventArgs()); Near(2.003, PlayheadSeconds, "Selected range start");
            SelectedEnd_Click(this, new RoutedEventArgs()); Near(12.157, PlayheadSeconds, "Selected range end");
            Unchanged("Clip-boundary navigation");

            _previewPreparing = true; _previewRangeStart = 0; _previewRangeEnd = 20; _previewPlaying = true; _previewPlayWhenReady = true;
            NavigatePlayhead(1.5);
            Require(_previewPreparing && !_previewPlaying && !_previewPlayWhenReady, "Navigation must cancel pending autoplay without discarding the preload");
            _previewPreparing = false;
            NavigatePlayhead(330.013);
            var x = (PlayheadSeconds - TimelineOffsetSeconds) * TimelinePixelsPerSecond;
            Require(TimelineOffsetSeconds > 0 && x >= 0 && x < TimelineViewportWidth, "Navigation follows the playhead horizontally");
            NavigatePlayhead(0); Near(0, TimelineOffsetSeconds, "Returning home scrolls back to zero");
            PlayheadSeconds = 12.157;
            FitTimelineToView();
            Require(TimelinePixelsPerSecond < 10 && PreviewDuration * TimelinePixelsPerSecond < TimelineViewportWidth,
                "Fit can zoom out below the old slider minimum and show the entire project");
            Near(0, TimelineOffsetSeconds, "Fit starts at time zero");
            Near(12.157, PlayheadSeconds, "Fit does not move the playhead");
            Near(TimelinePixelsPerSecond, TimelineZoom.Value, "Fit keeps the slider synchronized");
            Unchanged("Playback stop, viewport and fit");

            const double fps = 59.94;
            _project = _project with { Video = _project.Video with { Fps = fps } };
            PlayheadSeconds = 123 / fps;
            StepPreviewFrame(1); Near(124 / fps, PlayheadSeconds, "59.94 fps next frame");
            StepPreviewFrame(-1); Near(123 / fps, PlayheadSeconds, "59.94 fps round trip");
            PlayheadSeconds = 123.4 / fps; StepPreviewFrame(-1); Near(123 / fps, PlayheadSeconds, "59.94 fps off-frame previous");
            PlayheadSeconds = 123.4 / fps; StepPreviewFrame(1); Near(124 / fps, PlayheadSeconds, "59.94 fps off-frame next");
            _project = _project with { Clips = [new("nav-day", image.Id, "video-1", 0, 0, 86400)] };
            FitTimelineToView();
            Require(PreviewDuration * TimelinePixelsPerSecond < TimelineViewportWidth,
                "Fit supports a day-long timeline without a hard minimum scale");
            _project = _project with { Clips = [] };
            RefreshProject();
            NavigatePlayhead(10); StepPreviewFrame(1); NavigateBoundary(1); FitTimelineToView();
            Near(0, PlayheadSeconds, "Empty project navigation remains at zero");
            Require(double.IsFinite(TimelinePixelsPerSecond) && TimelinePixelsPerSecond > 0, "Empty project fit has a finite positive scale");

            foreach (var (input, expected) in new[] { ("12.5", 12.5), ("01:12.5", 72.5), ("02:01:12.125", 7272.125), ("0", 0.0) })
            {
                Require(TimelineNavigation.TryParseTime(input, out var parsed), "Time input must parse: " + input);
                Near(expected, parsed, "Parsed time " + input);
            }
            foreach (var invalid in new[] { "", "NaN", "Infinity", "-1", "12:60", "1:60:00", "1:2:3:4", "1,2", "1e20" })
                Require(!TimelineNavigation.TryParseTime(invalid, out _), "Invalid time must be rejected: " + invalid);
            File.WriteAllText(report, "PASS: project-frame stepping at 30/59.94 fps; off-frame seek direction; zero and fractional EOF; exact one-second and clip-boundary navigation; selected range edges; pending autoplay cancellation; viewport follow; whole-project fit below the previous minimum including day-long and empty projects; exact seconds and timecode parsing; project/selection/dirty/history preservation; caller project/path restoration.");
        }
        finally
        {
            _project = original; _projectPath = originalPath; _dirty = dirty;
            _undo.Clear(); _undo.AddRange(undo); _redo.Clear(); _redo.AddRange(redo);
            SelectedTimelineClipId = selected; SelectedTimelineClipIds = selectedIds; _selectedTrackId = track;
            TimelineZoom.Minimum = minimum; TimelineZoom.Value = sliderScale; TimelinePixelsPerSecond = scale;
            RefreshProject(); RefreshHistoryButtons(); InvalidatePreview(); SeekPreview(playhead);
            TimelineOffsetSeconds = Math.Clamp(offset, 0, TimelineHorizontalScroll.Maximum);
            StatusText.Text = status;
        }
    }
}
