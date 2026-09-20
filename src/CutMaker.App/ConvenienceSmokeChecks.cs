using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CutMaker.Core;

namespace CutMaker.App;

/// <summary>Exercises real laid-out lane coordinates and the same selection/move paths as pointer editing.</summary>
internal static class ConvenienceSmokeChecks
{
    internal static void Run(MainWindow window, string folder, Action<string>? capture = null)
    {
        Directory.CreateDirectory(folder);
        var report = Path.Combine(folder, "convenience-result.txt");
        File.WriteAllText(report, "RUNNING: marquee selection and atomic cross-track group movement.");
        var previous = window.CurrentProject;
        var previousPath = window.CurrentProjectPath ?? Path.Combine(folder, "before-convenience.cutmaker");
        var previousLayout = window.CaptureLayout();
        var previousScale = window.TimelinePixelsPerSecond;
        var previousOffset = window.TimelineOffsetSeconds;
        var previousSelection = window.SelectedTimelineClipIds?.ToArray() ?? [];
        var previousPrimary = window.SelectedTimelineClipId;
        var path = Path.Combine(folder, "convenience-check.cutmaker");
        try
        {
            window.ApplyLayout(previousLayout with { TimelineHeight = 480 });
            var project = CreateProject(previous.MediaAssets);
            Load(window, project, path);
            CheckMarquee(window, project, capture);
            CheckSelectionHistory(window, project, path);
            CheckScrolledCoordinates(window, project, path);
            CheckSnapping(window, project, path);
            CheckGroupMovement(window, project, path, capture);
            File.WriteAllText(report,
                "PASS: reverse-direction multi-track marquee using actual layout; Ctrl additive selection; cancellation restores selection and primary; " +
                "linked partners expand outside rectangle; locked clips and groups with locked partners excluded; horizontal zoom/offset and vertical scroll mapping; " +
                "selection preserves project data, dirty state and existing Undo/Redo; snapping toggle for trim and selected-group edges preserves frame quantization; " +
                "every destination ghost matches committed same-kind cross-track movement; locked/occupied/out-of-range rejection; exact Undo/Redo.\n");
        }
        finally
        {
            window.EndMarqueeSelection(cancel: true);
            window.LoadSmokeProject(previous, previousPath);
            window.ApplyLayout(previousLayout);
            window.TimelinePixelsPerSecond = previousScale;
            window.TimelineOffsetSeconds = previousOffset;
            window.SetClipSelection(previousSelection, previousPrimary);
            window.UpdateLayout();
        }
    }

    private static CutProject CreateProject(IReadOnlyList<MediaAsset> assets)
    {
        var video = assets.First(a => a.Kind == MediaKind.Video && a.Duration >= .6);
        var audio = assets.First(a => a.Kind == MediaKind.Audio && a.Duration >= .6);
        var image = assets.First(a => a.Kind == MediaKind.Image);
        var project = CutProject.CreateEmpty("CutMaker · 框選與跨軌群組移動") with
        {
            MediaAssets = [.. assets],
            Tracks =
            [
                new("v1", "影片一 · 連動主片段", TrackKind.Video),
                new("a1", "音訊一 · 連動音訊", TrackKind.Audio),
                new("v2", "影片二 · 多選片段", TrackKind.Video),
                new("a2", "音訊二 · 跨軌目標", TrackKind.Audio),
                new("v3", "影片三 · 鎖定連動測試", TrackKind.Video),
                new("a3", "音訊三", TrackKind.Audio),
                new("v-locked", "鎖定影片", TrackKind.Video, Locked: true),
                new("a-locked", "鎖定連動音訊", TrackKind.Audio, Locked: true)
            ],
            Clips =
            [
                new("linked-video", video.Id, "v1", 2, 0, .6, LinkGroupId: "av", SourceAudioMuted: true),
                new("linked-audio", video.Id, "a1", 2, 0, .6, LinkGroupId: "av"),
                new("independent", image.Id, "v2", 2.2, 0, .6),
                new("distant", image.Id, "v1", 6, 0, 1),
                new("scroll-video", image.Id, "v2", 22, 0, 1),
                new("scroll-audio", audio.Id, "a2", 22.2, 0, .6),
                new("locked-partner-video", video.Id, "v3", 2, 0, .6, LinkGroupId: "locked-av"),
                new("locked-partner-audio", video.Id, "a-locked", 2, 0, .6, LinkGroupId: "locked-av"),
                new("locked-alone", image.Id, "v-locked", 2, 0, .6)
            ]
        };
        ProjectValidator.Validate(project);
        return project;
    }

    private static void CheckMarquee(MainWindow window, CutProject project, Action<string>? capture)
    {
        var before = Snapshot(project);
        Require(!window.HasUnsavedChanges && !Button(window, "UndoButton").IsEnabled, "Fresh selection fixture is clean with no Undo.");
        window.SetClipSelection(["distant"], "distant");

        // Start in blank space at the lower right, then drag up/left across three physical rows.
        Begin(window, "v2", 3.2, .8, additive: false);
        Update(window, "v1", 1.5, .15);
        Require(window.IsMarqueeSelecting, "A moved blank-space gesture enters marquee mode.");
        Selection(window, "linked-video", "linked-audio", "independent");
        capture?.Invoke("workspace-marquee.png");
        window.EndMarqueeSelection(cancel: false);
        Require(!window.IsMarqueeSelecting, "Mouse release finishes the marquee.");
        Selection(window, "linked-video", "linked-audio", "independent");
        Require(!window.HasUnsavedChanges && before == Snapshot(window.CurrentProject), "Selection cannot modify project data or dirty state.");
        window.UndoEdit();
        Require(!window.HasUnsavedChanges && !Button(window, "UndoButton").IsEnabled && before == Snapshot(window.CurrentProject),
            "Selection must not add an Undo step.");

        window.SetClipSelection(["distant"], "distant");
        Begin(window, "v2", 3.2, .8, additive: true);
        Update(window, "v2", 1.5, .15);
        window.EndMarqueeSelection(cancel: false);
        Selection(window, "distant", "independent");

        var selectedBeforeCancel = window.SelectedTimelineClipIds!.ToArray();
        var primaryBeforeCancel = window.SelectedTimelineClipId;
        Begin(window, "v1", 3.2, .8, additive: false);
        Update(window, "v1", 1.5, .15);
        Selection(window, "linked-video", "linked-audio");
        window.EndMarqueeSelection(cancel: true);
        Selection(window, selectedBeforeCancel);
        Require(window.SelectedTimelineClipId == primaryBeforeCancel, "Escape/cancel restores the previous primary selection.");

        // Only the video intersects the rectangle; its audio partner must still join the selection.
        Begin(window, "v1", 3.2, .8, additive: false);
        Update(window, "v1", 1.5, .15);
        window.EndMarqueeSelection(cancel: false);
        Selection(window, "linked-video", "linked-audio");

        ScrollTo(window, "v3");
        Begin(window, "v3", 3.2, .8, additive: false);
        Update(window, "v3", 1.5, .15);
        window.EndMarqueeSelection(cancel: false);
        Selection(window);
        ScrollTo(window, "v-locked");
        Begin(window, "v-locked", 3.2, .8, additive: false);
        Update(window, "v-locked", 1.5, .15);
        window.EndMarqueeSelection(cancel: false);
        Selection(window);
        Require(before == Snapshot(window.CurrentProject) && !window.HasUnsavedChanges,
            "Linked/locked selection checks preserve every clip, track and source reference.");
    }

    private static void CheckSelectionHistory(MainWindow window, CutProject project, string path)
    {
        Load(window, project, path);
        var track = project.Tracks.Single(t => t.Id == "v1");
        Require(window.ApplyTrackSettings(track with { Name = "已有的可復原編輯" }), "History fixture records a real edit.");
        Begin(window, "v2", 3.2, .8, additive: false);
        Update(window, "v1", 1.5, .15);
        window.EndMarqueeSelection(cancel: false);
        window.UndoEdit();
        Require(window.CurrentProject.Tracks.Single(t => t.Id == track.Id) == track,
            "Undo after selecting must undo the preceding edit, not selection.");
        Require(Button(window, "RedoButton").IsEnabled, "Selection does not destroy the prior edit's Redo history.");
        Begin(window, "v1", 3.2, .8, additive: false);
        Update(window, "v1", 1.5, .15);
        window.EndMarqueeSelection(cancel: false);
        window.RedoEdit();
        Require(window.CurrentProject.Tracks.Single(t => t.Id == track.Id).Name == "已有的可復原編輯",
            "Redo remains usable after a further selection-only gesture.");
    }

    private static void CheckScrolledCoordinates(MainWindow window, CutProject project, string path)
    {
        Load(window, project, path);
        window.TimelinePixelsPerSecond = 80;
        window.TimelineOffsetSeconds = 20;
        ScrollTo(window, "v2");
        var scroll = (ScrollViewer)window.FindName("TimelineTrackScroll");
        Require(scroll.VerticalOffset > 0, "Coordinate test must actually scroll vertically.");
        Begin(window, "a2", 23.5, .8, additive: false);
        Update(window, "v2", 21.5, .15);
        window.EndMarqueeSelection(cancel: false);
        Selection(window, "scroll-video", "scroll-audio");
        Require(!window.HasUnsavedChanges && Snapshot(window.CurrentProject) == Snapshot(project),
            "Zoomed, horizontally offset and vertically scrolled selection preserves timing.");
    }

    private static void CheckSnapping(MainWindow window, CutProject project, string path)
    {
        Load(window, project, path);
        var originalSetting = window.TimelineSnapEnabled;
        var before = Snapshot(window.CurrentProject);
        try
        {
            window.SetClipSelection(["linked-video"], "linked-video");
            // The requested edge is deliberately off the project frame grid and within the snap radius of 6 s.
            var requested = 6 - 1 / project.Video.Fps + .003;
            var expectedFrame = 6 - 1 / project.Video.Fps;
            window.TimelineSnapEnabled = true;
            Require(Math.Abs(window.SnapEditTime(requested, "linked-video") - 6) < 1e-7,
                "Enabled snapping aligns a trim edge with an unselected clip boundary.");
            window.TimelineSnapEnabled = false;
            var unsnapped = window.SnapEditTime(requested, "linked-video");
            Require(Math.Abs(unsnapped - expectedFrame) < 1e-7 && Math.Abs(unsnapped - requested) > 1e-4,
                "Disabling boundary snapping still quantizes a trim edge to project frames.");

            // The last selected member ends .8 s after the primary starts; this edge can snap the entire group.
            window.SetClipSelection(["linked-video", "independent"], "linked-video");
            window.TimelineSnapEnabled = true;
            Require(Math.Abs(window.SnapEditTime(5.231, "linked-video", .6) - 5.2) < 1e-7,
                "Group movement can align a secondary member's end with an unselected clip boundary.");
            window.TimelineSnapEnabled = false;
            Require(Math.Abs(window.SnapEditTime(5.231, "linked-video", .6) - 157.0 / 30) < 1e-7,
                "Disabled group snapping keeps the project frame grid without pulling the group to a nearby edge.");
            Require(before == Snapshot(window.CurrentProject) && !window.HasUnsavedChanges && !Button(window, "UndoButton").IsEnabled,
                "Snapping previews and view-setting changes cannot mutate project data, dirty state or history.");
        }
        finally { window.TimelineSnapEnabled = originalSetting; }
    }

    private static void CheckGroupMovement(MainWindow window, CutProject project, string path, Action<string>? capture)
    {
        Load(window, project, path);
        window.SetClipSelection(["linked-video", "independent"], "linked-video");
        var original = project.Clips.Single(c => c.Id == "linked-video");
        var candidate = original with { Start = 6, TrackId = "v2" };
        var expectedTracks = new Dictionary<string, string> { ["linked-video"] = "v2", ["linked-audio"] = "a2", ["independent"] = "v3" };
        Require(window.TryPreviewGroupMove(candidate), "The full cross-track group has a valid pointer preview.");
        var ghosts = Descendants((ItemsControl)window.FindName("TrackItems")).OfType<TimelineLane>()
            .SelectMany(lane => lane.MovePreviews.Select(ghost => (Track: (string)lane.Tag, Ghost: ghost))).ToArray();
        Require(ghosts.Length == 3, "A move preview draws every selected and linked member.");
        foreach (var (id, track) in expectedTracks)
        {
            var old = project.Clips.Single(c => c.Id == id);
            Require(ghosts.Any(g => g.Track == track && g.Ghost.Id == id && g.Ghost.Start == old.Start + 4 && g.Ghost.Duration == old.Duration),
                "Each move ghost uses its own destination track, time and duration.");
        }
        Require(Snapshot(window.CurrentProject) == Snapshot(project) && !window.HasUnsavedChanges && !Button(window, "UndoButton").IsEnabled,
            "Move preview cannot alter the project or history before release.");
        ScrollTo(window, "v2");
        capture?.Invoke("workspace-group-move.png");
        Require(!window.TryPreviewGroupMove(original with { Start = 6, TrackId = "v3" }), "Out-of-range group preview is denied.");
        Require(Descendants((ItemsControl)window.FindName("TrackItems")).OfType<TimelineLane>().All(lane => lane.MovePreviews.Count == 0),
            "A denied preview clears every previous valid group ghost.");
        Require(window.TryCommitGroupMove(candidate), "A same-kind cross-track move commits the complete selected group.");
        foreach (var old in project.Clips)
        {
            var expected = expectedTracks.TryGetValue(old.Id, out var track) ? old with { Start = old.Start + 4, TrackId = track } : old;
            Require(window.CurrentProject.Clips.Single(c => c.Id == old.Id) == expected,
                "Group move preserves source ranges/effects and moves every selected or linked member by the same time and track delta.");
        }
        Selection(window, "linked-video", "linked-audio", "independent");
        var moved = Snapshot(window.CurrentProject);
        window.UndoEdit();
        Require(Snapshot(window.CurrentProject) == Snapshot(project), "One Undo restores all moved clips exactly.");
        Selection(window, "linked-video", "linked-audio", "independent");
        window.RedoEdit();
        Require(Snapshot(window.CurrentProject) == moved, "One Redo restores the exact group destination and IDs.");

        CheckRejectedMove(window, project, path, original with { Start = 6, TrackId = "v3" }, "A member past the last same-kind track rejects the group.");
        var locked = project with { Tracks = project.Tracks.Select(t => t.Id == "a2" ? t with { Locked = true } : t).ToList() };
        CheckRejectedMove(window, locked, path, candidate, "A locked audio destination rejects the complete video/audio group.");
        var occupied = project with { Clips = [.. project.Clips, new("obstacle", project.Clips.Single(c => c.Id == "independent").AssetId, "v3", 6.3, 0, .6)] };
        CheckRejectedMove(window, occupied, path, candidate, "An overlap on a secondary destination rejects the complete group.");
    }

    private static void CheckRejectedMove(MainWindow window, CutProject project, string path, Clip candidate, string reason)
    {
        Load(window, project, path);
        window.SetClipSelection(["linked-video", "independent"], "linked-video");
        var before = Snapshot(project);
        Require(!window.TryCommitGroupMove(candidate), reason);
        Require(before == Snapshot(window.CurrentProject) && !window.HasUnsavedChanges && !Button(window, "UndoButton").IsEnabled,
            "Rejected group movement preserves all project data, clean state and history.");
        Selection(window, "linked-video", "linked-audio", "independent");
    }

    private static void Load(MainWindow window, CutProject project, string path)
    {
        window.LoadSmokeProject(project with { MediaAssets = [.. project.MediaAssets], Tracks = [.. project.Tracks], Clips = [.. project.Clips] }, path);
        window.TimelinePixelsPerSecond = 100;
        window.TimelineOffsetSeconds = 0;
        ((ScrollViewer)window.FindName("TimelineTrackScroll")).ScrollToTop();
        window.UpdateLayout();
    }

    private static void Begin(MainWindow window, string track, double seconds, double heightFraction, bool additive)
    {
        var lane = Lane(window, track);
        window.BeginMarqueeSelection(lane, LanePoint(window, lane, seconds, heightFraction), additive, captureMouse: false);
    }

    private static void Update(MainWindow window, string track, double seconds, double heightFraction)
    {
        var lane = Lane(window, track);
        var items = (ItemsControl)window.FindName("TrackItems");
        window.UpdateMarqueeSelection(lane.TranslatePoint(LanePoint(window, lane, seconds, heightFraction), items));
    }

    private static Point LanePoint(MainWindow window, TimelineLane lane, double seconds, double heightFraction) =>
        new((seconds - window.TimelineOffsetSeconds) * window.TimelinePixelsPerSecond, lane.ActualHeight * heightFraction);

    private static void ScrollTo(MainWindow window, string track)
    {
        var lane = Lane(window, track);
        var items = (ItemsControl)window.FindName("TrackItems");
        var top = lane.TranslatePoint(new Point(0, 0), items).Y;
        ((ScrollViewer)window.FindName("TimelineTrackScroll")).ScrollToVerticalOffset(top);
        window.UpdateLayout();
    }

    private static TimelineLane Lane(MainWindow window, string track)
    {
        window.UpdateLayout();
        var lane = Descendants((ItemsControl)window.FindName("TrackItems")).OfType<TimelineLane>().Single(l => (string)l.Tag == track);
        Require(lane.ActualWidth > 400 && lane.ActualHeight > 40, "Selection checks require actual laid-out lane bounds.");
        return lane;
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

    private static Button Button(MainWindow window, string name) => (Button)window.FindName(name);
    private static void Selection(MainWindow window, params string[] expected) => Require(
        (window.SelectedTimelineClipIds ?? []).ToHashSet().SetEquals(expected),
        $"Expected selection [{string.Join(", ", expected)}], got [{string.Join(", ", window.SelectedTimelineClipIds ?? [])}].");
    private static string Snapshot(CutProject project) => JsonSerializer.Serialize(project);
    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException("Convenience smoke check failed: " + message); }
}
