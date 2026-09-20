using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CutMaker.Core;

namespace CutMaker.App;

public partial class MainWindow
{
    /// <summary>Exercises the shared held-pointer path and actual rendered handles before MouseUp.</summary>
    internal void RunFadeDragSmoke(string folder, Action<string>? capture)
    {
        var report = Path.Combine(folder, "fade-drag-result.txt");
        File.WriteAllText(report, "RUNNING: live Fade handles and curves before pointer release.\n");
        var original = _project; var originalPath = _projectPath;
        var dirty = _dirty; var undo = _undo.ToArray(); var redo = _redo.ToArray();
        var selected = SelectedTimelineClipId; var selection = SelectedTimelineClipIds?.ToArray();
        var selectedTrack = _selectedTrackId; var collapsed = _collapsedTracks.ToArray();
        var layout = CaptureLayout(); var scale = TimelinePixelsPerSecond;
        var sliderScale = TimelineZoom.Value; var offset = TimelineOffsetSeconds; var playhead = PlayheadSeconds;
        void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Fade drag smoke check failed: " + message); }
        TimelineLane Lane(string id = "fade-live-v1") => FindLanes(TrackItems).Single(lane => Equals(lane.Tag, id));
        Clip EditedClip() => _project.Clips.Single(clip => clip.Id == "fade-live");
        static BitmapSource RenderLane(TimelineLane lane)
        {
            lane.UpdateLayout();
            var image = new RenderTargetBitmap((int)Math.Ceiling(lane.ActualWidth), (int)Math.Ceiling(lane.ActualHeight),
                96, 96, PixelFormats.Pbgra32);
            // Rendering an attached lane directly includes its parent grid offset. Mirror the
            // workspace screenshot renderer so the resulting pixels use lane-local 96dpi units.
            var visual = new DrawingVisual();
            var offset = VisualTreeHelper.GetOffset(lane);
            using (var drawing = visual.RenderOpen())
                drawing.DrawRectangle(new VisualBrush(lane)
                {
                    ViewboxUnits = BrushMappingMode.Absolute,
                    Viewbox = new Rect(offset.X, offset.Y, lane.ActualWidth, lane.ActualHeight),
                    Stretch = Stretch.Fill
                }, null, new Rect(0, 0, lane.ActualWidth, lane.ActualHeight));
            image.Render(visual); image.Freeze(); return image;
        }
        static void SaveImage(BitmapSource image, string path)
        {
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using var output = File.Create(path); encoder.Save(output);
        }
        static byte[] Pixels(BitmapSource image)
        {
            var result = new byte[image.PixelWidth * image.PixelHeight * 4];
            image.CopyPixels(result, image.PixelWidth * 4, 0); return result;
        }
        static int GoldPixels(BitmapSource image, double x, bool compact)
        {
            var pixels = Pixels(image); var count = 0;
            // Handle centers: expanded rect.Top=5, 8px handle; compact rect.Top=2, 6px handle.
            var top = compact ? 4 : 7; var bottom = compact ? 7 : 11;
            for (var y = top; y <= bottom && y < image.PixelHeight; y++)
                for (var column = Math.Max(0, (int)Math.Round(x) - 2); column <= Math.Min(image.PixelWidth - 1, (int)Math.Round(x) + 2); column++)
                {
                    var i = (y * image.PixelWidth + column) * 4;
                    if (pixels[i + 2] >= 245 && pixels[i + 1] is >= 190 and <= 217 && pixels[i] is >= 98 and <= 127) count++;
                }
            return count;
        }
        MouseButtonEventArgs MouseUp() => new(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        { RoutedEvent = Mouse.MouseUpEvent };
        void CancelWithEscape(TimelineLane lane)
        {
            var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this), Environment.TickCount, Key.Escape)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent, Source = lane };
            HandleShortcut(this, key);
            Require(key.Handled, "Esc is handled while the Fade gesture is active.");
        }
        try
        {
            var image = original.MediaAssets.First(asset => asset.Kind == MediaKind.Image);
            var fixture = CutProject.CreateEmpty("CutMaker · Fade 拖曳即時變化") with
            {
                MediaAssets = [image],
                Tracks = [new("fade-live-v1", "Fade 曲線 · 拖曳中即時顯示", TrackKind.Video),
                    new("fade-live-v2", "連動夥伴 · 保持獨立效果", TrackKind.Video)],
                Clips =
                [
                    new("fade-live", image.Id, "fade-live-v1", 2, 0, 12,
                        FadeIn: new(.8, FadeCurve.EaseIn),
                        FadeOut: new(1.2, FadeCurve.Custom, Control1: .1, Control2: .85, RangeStart: .15, RangeEnd: .9),
                        LinkGroupId: "fade-live-link"),
                    new("fade-live-mate", image.Id, "fade-live-v2", 2, 0, 12,
                        FadeIn: new(.5, FadeCurve.EqualPower), FadeOut: new(.5), LinkGroupId: "fade-live-link")
                ]
            };
            LoadSmokeProject(fixture, Path.Combine(folder, "fade-drag.cutmaker"));
            ApplyLayout(layout with { TimelineHeight = 310 });
            TimelineZoom.Value = 36; TimelinePixelsPerSecond = 36; TimelineOffsetSeconds = 1; PlayheadSeconds = 0;
            SetClipSelection(["fade-live"], "fade-live"); RefreshTimeline(); UpdateLayout();
            var sentinel = CaptureEdit(); _redo.Add(sentinel); RefreshHistoryButtons();

            foreach (var compact in new[] { false, true })
            {
                if (compact) { ToggleTrackCollapsed("fade-live-v1"); UpdateLayout(); }
                foreach (var fadeIn in new[] { true, false })
                {
                    var lane = Lane(); var clip = EditedClip(); var expectedMode = fadeIn ? PointerEdit.FadeIn : PointerEdit.FadeOut;
                    Require(lane.IsCompact == compact, "Test uses the intended full or 30px compact row.");
                    Require(lane.PixelsPerSecond == TimelinePixelsPerSecond && lane.OffsetSeconds == TimelineOffsetSeconds &&
                        lane.SelectedClipId == clip.Id, "Lane bindings use the expected scale, offset and primary selection.");
                    var before = RenderLane(lane); var beforeBytes = Pixels(before);
                    var content = JsonSerializer.Serialize(_project); var revision = _previewRevision;
                    var previousDirty = _dirty; var previousUndo = _undo.Count; var previousRedo = _redo.Count;
                    var mate = _project.Clips.Single(item => item.Id == "fade-live-mate");
                    var originalDuration = fadeIn ? clip.FadeIn!.Duration : clip.FadeOut!.Duration;
                    double HandleX(double duration) => ((fadeIn ? clip.Start + duration : clip.End - duration) - TimelineOffsetSeconds) * TimelinePixelsPerSecond;
                    var initialX = HandleX(originalDuration);
                    Require(initialX > 20 && initialX < lane.ActualWidth - 20, "Fixture starts away from autoscroll edges.");
                    BeginPointerEdit(lane, clip, new(initialX, compact ? 5 : 10));
                    Require(_pointerMode == expectedMode && _pointerOriginal == clip, "Gold handle starts the correct Fade edit.");
                    var captured = lane.IsMouseCaptured;
                    foreach (var duration in new[] { 2.4, 3.2 })
                    {
                        var x = HandleX(duration);
                        UpdatePointerEditAt(lane, new(x, compact ? 5 : 10));
                        Require(_pointerCandidate is not null && _pointerMoved, "Held pointer creates a valid candidate before MouseUp.");
                        var fade = fadeIn ? _pointerCandidate!.FadeIn! : _pointerCandidate!.FadeOut!;
                        Require(Math.Abs(fade.Duration - duration) < 1e-8 && fade.RangeStart == 0 && fade.RangeEnd == 1,
                            "Drag maps zoom/offset coordinates to Fade seconds and resets a sliced curve's range.");
                        Require(fade.Curve == (fadeIn ? FadeCurve.EaseIn : FadeCurve.Custom), "Drag preserves the chosen curve.");
                        Require(content == JsonSerializer.Serialize(_project) && _dirty == previousDirty &&
                            _undo.Count == previousUndo && _redo.Count == previousRedo && _previewRevision == revision,
                            "Pointer movement changes only the display, with no project, history or preview invalidation.");
                        Require(!captured || lane.IsMouseCaptured, "Updating the curve retains mouse capture until release.");
                        var during = RenderLane(lane);
                        var duringBytes = Pixels(during);
                        var differences = beforeBytes.Zip(duringBytes).Count(pair => pair.First != pair.Second);
                        var gold = GoldPixels(during, x, compact);
                        var context = $"compact={compact}; fadeIn={fadeIn}; duration={duration:R}; x={x:R}; initialX={initialX:R}; " +
                            $"scale={TimelinePixelsPerSecond:R}/{lane.PixelsPerSecond:R}; offset={TimelineOffsetSeconds:R}/{lane.OffsetSeconds:R}; " +
                            $"selected={lane.SelectedClipId}; size={lane.ActualWidth:R}x{lane.ActualHeight:R}; " +
                            $"dpi={VisualTreeHelper.GetDpi(lane)}; visualOffset={VisualTreeHelper.GetOffset(lane)}; " +
                            $"gold={gold}; oldGold={GoldPixels(during, initialX, compact)}; byteDifferences={differences}";
                        File.AppendAllText(report, context + "\n");
                        if (differences == 0 || gold < 6)
                        {
                            SaveImage(before, Path.Combine(folder, "fade-drag-failed-before.png"));
                            SaveImage(during, Path.Combine(folder, "fade-drag-failed-during.png"));
                        }
                        Require(differences > 0 && gold >= 6,
                            "The actual rendered gold Fade point moves to each intermediate pointer location before release. " + context);
                        Require(GoldPixels(during, initialX, compact) < 3,
                            "The old gold handle is removed while dragging, rather than leaving a stale point behind a ghost.");
                        // Keep the same point position and vary only the envelope. This would fail
                        // if the lane still displayed every preset as the old straight diagonal.
                        var candidate = _pointerCandidate!;
                        var linear = fade with { Curve = FadeCurve.Linear };
                        lane.SetFadePreview(fadeIn ? candidate with { FadeIn = linear } : candidate with { FadeOut = linear });
                        var straight = Pixels(RenderLane(lane));
                        lane.SetFadePreview(candidate);
                        Require(!straight.SequenceEqual(Pixels(during)),
                            "Rendered curve shape follows EaseIn/Custom settings instead of always drawing a diagonal.");
                    }
                    capture?.Invoke($"workspace-fade-{(compact ? "compact" : "expanded")}-{(fadeIn ? "in" : "out")}-dragging.png");
                    if (fadeIn)
                    {
                        CancelWithEscape(lane);
                        Require(_pointerOriginal is null && _pointerCandidate is null && !lane.IsMouseCaptured &&
                            content == JsonSerializer.Serialize(_project) && beforeBytes.SequenceEqual(Pixels(RenderLane(lane))),
                            "Esc releases capture and restores the original Fade pixels without committing.");
                    }
                    else
                    {
                        TimelineLane_MouseUp(lane, MouseUp()); UpdateLayout();
                        Require(_pointerOriginal is null && !lane.IsMouseCaptured && _undo.Count == previousUndo + 1 && _redo.Count == 0,
                            "MouseUp commits the complete drag as one Undo step and clears Redo.");
                        Require(Math.Abs(EditedClip().FadeOut!.Duration - 3.2) < 1e-8 &&
                            _project.Clips.Single(item => item.Id == mate.Id) == mate,
                            "Committed Fade matches its final visual candidate and preserves the linked partner's independent effects.");
                        UndoEdit(); UpdateLayout();
                        Require(EditedClip() == clip && IsTrackCollapsed(clip.TrackId) == compact,
                            "One Undo restores the entire drag while retaining the compact view state.");
                    }
                }
            }

            var lostLane = Lane(); var lostClip = EditedClip();
            var lostBefore = Pixels(RenderLane(lostLane)); var lostContents = JsonSerializer.Serialize(_project);
            var lostUndo = _undo.Count; var lostRevision = _previewRevision;
            var lostX = (lostClip.Start + lostClip.FadeIn!.Duration - TimelineOffsetSeconds) * TimelinePixelsPerSecond;
            BeginPointerEdit(lostLane, lostClip, new(lostX, 5));
            UpdatePointerEditAt(lostLane, new(lostX + 48, 5));
            TimelineLane_LostCapture(lostLane, new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount));
            Require(_pointerOriginal is null && !lostLane.IsMouseCaptured && lostContents == JsonSerializer.Serialize(_project) &&
                lostUndo == _undo.Count && lostRevision == _previewRevision && lostBefore.SequenceEqual(Pixels(RenderLane(lostLane))),
                "Losing capture clears the temporary Fade and restores the original pixels without an edit.");

            File.WriteAllText(report,
                "PASS: shared held-pointer Fade-in and Fade-out path; two intermediate handle/curve renders before MouseUp; " +
                "normal and 30px compact rows at nonzero offset; gold point relocation and stale point removal; " +
                "curve preset/range preservation; project/history/preview unchanged during drag; Esc and capture-loss rollback; " +
                "one-step commit/Undo and linked partner independent effects.\n");
        }
        finally
        {
            CancelPointerEdit();
            LoadSmokeProject(original, originalPath ?? Path.Combine(folder, "before-fade-drag.cutmaker"));
            _projectPath = originalPath; _dirty = dirty;
            _undo.Clear(); _undo.AddRange(undo); _redo.Clear(); _redo.AddRange(redo);
            _collapsedTracks.UnionWith(collapsed); _selectedTrackId = selectedTrack;
            SetClipSelection(selection ?? [], selected); ApplyLayout(layout);
            TimelineZoom.Value = sliderScale; TimelinePixelsPerSecond = scale; TimelineOffsetSeconds = offset; PlayheadSeconds = playhead;
            RefreshProject(); RefreshHistoryButtons(); UpdateLayout();
        }
    }
}
