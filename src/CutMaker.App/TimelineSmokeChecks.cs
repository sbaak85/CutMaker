using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CutMaker.Core;

namespace CutMaker.App;

/// <summary>Exercises the same library payload, drop planning and insertion methods used by WPF drag/drop.</summary>
internal static class TimelineSmokeChecks
{
    internal static void Run(MainWindow window, string folder)
    {
        Directory.CreateDirectory(folder);
        var reportPath = Path.Combine(folder, "timeline-result.txt");
        File.WriteAllText(reportPath, "RUNNING: library-to-timeline integration checks.");
        var importedAssets = window.CurrentProject.MediaAssets.ToArray();
        var visual = importedAssets.FirstOrDefault(asset => asset.Kind == MediaKind.Video)
            ?? importedAssets.FirstOrDefault(asset => asset.Kind == MediaKind.Image)
            ?? throw new InvalidOperationException("Timeline smoke check requires a previously imported video or image.");
        var audio = importedAssets.FirstOrDefault(asset => asset.Kind == MediaKind.Audio &&
            string.Equals(Path.GetExtension(asset.Path), ".wav", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Timeline smoke check requires the previously imported PCM WAV.");
        var sourceHashes = new[] { visual.Path, audio.Path }.Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(path => path, Fingerprint, StringComparer.OrdinalIgnoreCase);
        var projectPath = Path.Combine(folder, "timeline-roundtrip.cutmaker");
        var project = CutProject.CreateEmpty("CutMaker · 素材庫拖入軌道驗證") with
        {
            MediaAssets = [.. importedAssets],
            Tracks =
            [
                new("video-1", "影片／圖片軌", TrackKind.Video),
                new("audio-1", "音訊軌", TrackKind.Audio),
                new("video-locked", "已鎖定的影片軌", TrackKind.Video, Locked: true)
            ]
        };
        window.LoadSmokeProject(project, projectPath);
        window.TimelineOffsetSeconds = 0;
        window.TimelinePixelsPerSecond = 40;
        Require(!window.HasUnsavedChanges, "A loaded timeline must start clean.");
        VerifyWheelZoom(window);

        var visualDrag = window.CreateLibraryDragData(visual.Id);
        var audioDrag = window.CreateLibraryDragData(audio.Id);
        Require(!visualDrag.GetDataPresent(DataFormats.FileDrop, false),
            "Dragging a library asset must use an internal payload, not an Explorer FileDrop.");
        Require(!audioDrag.GetDataPresent(DataFormats.FileDrop, false), "Audio must use the internal library payload too.");
        AssertRejected(window, new DataObject(DataFormats.FileDrop, new[] { visual.Path }), "video-1", 80, "Explorer drop directly on the timeline");
        AssertRejected(window, new DataObject(DataFormats.UnicodeText, visual.Id), "video-1", 80, "Plain text asset ID");
        AssertRejected(window, visualDrag, "audio-1", 80, "Visual source on an audio track");
        AssertRejected(window, audioDrag, "video-1", 80, "Audio source on a video track");
        AssertRejected(window, visualDrag, "video-locked", 80, "Locked target track");
        AssertRejected(window, visualDrag, "missing-track", 80, "Missing target track");

        var beforePreview = JsonSerializer.Serialize(window.CurrentProject);
        Require(window.EvaluateTimelineDrop(visualDrag, "video-1", 80, out var previewAsset, out var previewStart, out var previewReason),
            "Compatible drag preview must be accepted: " + previewReason);
        Require(previewAsset == visual, "The drag preview must resolve the original imported source.");
        Near(2, previewStart, "80 pixels at 40 pixels/second must place the clip at two seconds");
        Require(beforePreview == JsonSerializer.Serialize(window.CurrentProject) && !window.HasUnsavedChanges,
            "Drag preview must not insert a clip or dirty the project.");
        var first = Drop(window, visualDrag, "video-1", 80);
        Near(2, first.Start, "Inserted visual start");
        Near(visual.Duration, first.Duration, "Inserted visual source duration");
        Near(0, first.SourceIn, "Inserted visual source in-point");
        Require(first.TrackId == "video-1" && first.AssetId == visual.Id && window.HasUnsavedChanges,
            "Successful insertion must keep its source/track and dirty the project.");
        Require(window.SelectedTimelineClipId == first.Id, "A new clip must become selected.");
        AssertRejected(window, visualDrag, "video-1", 80, "Overlap with an existing clip");

        // A release near the first clip's end must snap to its exact end, including non-frame durations.
        var second = Drop(window, visualDrag, "video-1", first.End * window.TimelinePixelsPerSecond + 1);
        Near(first.End, second.Start, "Repeated source must snap to the touching boundary");
        Near(first.Duration, second.Duration, "Repeated source keeps full source duration");
        Near(0, second.SourceIn, "Repeated source starts at source zero");
        Require(first.Id != second.Id && first.AssetId == second.AssetId,
            "Repeated library drops must create independent clips that reference the same asset.");
        var audioClip = Drop(window, audioDrag, "audio-1", 0);
        Near(0, audioClip.Start, "Audio start");
        Near(audio.Duration, audioClip.Duration, "Audio measured source duration");
        Require(window.CurrentProject.MediaAssets.SequenceEqual(importedAssets),
            "Timeline placement must not duplicate, remove or alter library assets.");

        var expectedClips = window.CurrentProject.Clips.ToArray();
        var expectedTracks = window.CurrentProject.Tracks.ToArray();
        var staleDrag = window.CreateLibraryDragData(visual.Id);
        ProjectStore.Save(projectPath, window.CurrentProject);
        var loaded = ProjectStore.Load(projectPath);
        Require(loaded.Clips.SequenceEqual(expectedClips) && loaded.Tracks.SequenceEqual(expectedTracks) &&
            loaded.MediaAssets.SequenceEqual(importedAssets),
            "Save/load must preserve every clip, source reference, position, duration and track.");
        window.LoadSmokeProject(loaded, projectPath);
        Require(!window.HasUnsavedChanges, "Reopening the saved timeline must clear the dirty state.");
        AssertRejected(window, staleDrag, "video-1", (second.End + 2) * 40,
            "Payload from the previous project generation, even when its asset IDs still exist");

        window.SelectedTimelineClipId = second.Id;
        window.RemoveSelectedClip();
        Require(window.HasUnsavedChanges && window.SelectedTimelineClipId is null &&
            window.CurrentProject.Clips.SequenceEqual(expectedClips.Where(clip => clip.Id != second.Id)),
            "Deleting the selected clip must remove only that clip and dirty the project.");
        Require(window.CurrentProject.MediaAssets.SequenceEqual(importedAssets),
            "Deleting a timeline clip must keep its source in the library.");

        // Isolate viewport mapping from the occupied ranges and snapping boundaries above.
        var mappingProject = CutProject.CreateEmpty("Viewport mapping") with { MediaAssets = [.. importedAssets] };
        window.LoadSmokeProject(mappingProject, Path.Combine(folder, "timeline-mapping.cutmaker"));
        window.TimelineOffsetSeconds = 12;
        window.TimelinePixelsPerSecond = 80;
        var mappingDrag = window.CreateLibraryDragData(audio.Id);
        Require(window.EvaluateTimelineDrop(mappingDrag, "audio-1", 123.3, out _, out var mappedStart, out var mappingReason),
            "Scrolled and zoomed drag preview must be accepted: " + mappingReason);
        const double expectedMappedStart = 406.0 / 30; // Round((12 + 123.3 / 80) * 30) / 30.
        Near(expectedMappedStart, mappedStart, "Viewport offset and zoom must map to project-frame time");
        var mappedClip = Drop(window, mappingDrag, "audio-1", 123.3);
        Near(mappedStart, mappedClip.Start, "Committed drop must use the same start as its preview");
        Near(audio.Duration, mappedClip.Duration, "Zoom must not change source duration");
        Require(mappingProject.Clips.Count == 1, "Viewport mapping test must insert exactly one clip.");

        // Restore a saved, clean visual + audio timeline for the rendered workspace-timeline.png.
        window.TimelineOffsetSeconds = 0;
        window.TimelinePixelsPerSecond = 40;
        window.LoadSmokeProject(ProjectStore.Load(projectPath), projectPath);
        Require(window.CurrentProject.Clips.SequenceEqual(expectedClips) && !window.HasUnsavedChanges,
            "The final visible timeline must exactly restore the saved visual and audio clips.");
        foreach (var (path, originalHash) in sourceHashes)
            Require(Fingerprint(path) == originalHash, "Timeline operations must preserve source bytes: " + Path.GetFileName(path));

        var report = new StringBuilder()
            .AppendLine("PASS: internal library drag payload; direct file/text drop rejection; compatible track and exact drop position;")
            .AppendLine("drag preview without mutation; full measured source duration; SourceIn zero; dirty state and selection;")
            .AppendLine("wrong-track, missing-track, locked-track and overlap rejection without mutation; repeated assets with independent IDs;")
            .AppendLine("touching-edge snapping; nonzero viewport offset and zoom mapping; selected-clip removal preserves library assets;")
            .AppendLine("Ctrl-wheel pointer anchoring; slider synchronization; ordinary-wheel passthrough; zoom limits and viewport boundaries; view-only zoom;")
            .AppendLine("exact save/load restoration; old-project payload rejection; original source bytes unchanged.")
            .AppendLine($"Visual source: {visual.Kind} | {visual.Duration:R} seconds | {visual.Path}")
            .AppendLine($"Audio source: {audio.Duration:R} seconds | {audio.Path}");
        foreach (var clip in window.CurrentProject.Clips)
            report.AppendLine($"Clip {clip.Id} | {clip.TrackId} | start {clip.Start:R} | duration {clip.Duration:R} | source in {clip.SourceIn:R}");
        File.WriteAllText(reportPath, report.ToString());
    }

    private static void VerifyWheelZoom(MainWindow window)
    {
        window.UpdateLayout();
        var slider = (Slider)window.FindName("TimelineZoom");
        var scroll = (System.Windows.Controls.Primitives.ScrollBar)window.FindName("TimelineHorizontalScroll");
        var tracks = (ScrollViewer)window.FindName("TimelineTrackScroll");
        var before = JsonSerializer.Serialize(window.CurrentProject);
        var dirty = window.HasUnsavedChanges;
        var playhead = window.PlayheadSeconds;
        slider.Value = 40;
        window.TimelineOffsetSeconds = 12;
        const double x = 200;
        var anchoredTime = 12 + x / 40;
        Require(!window.TryZoomTimelineWheel(120, ModifierKeys.None, x), "Ordinary wheel must remain available for vertical scrolling");
        Near(40, window.TimelinePixelsPerSecond, "Ordinary wheel keeps scale");
        Require(window.TryZoomTimelineWheel(120, ModifierKeys.Control, x), "Ctrl-wheel must be handled");
        Require(window.TimelinePixelsPerSecond > 40, "Wheel up zooms in");
        Near(anchoredTime, window.TimelineOffsetSeconds + x / window.TimelinePixelsPerSecond, "Zoom-in preserves pointer time");
        Near(window.TimelinePixelsPerSecond, slider.Value, "Wheel updates the slider");
        window.TryZoomTimelineWheel(-120, ModifierKeys.Control, x);
        Near(40, slider.Value, "Opposite wheel restores scale");
        Near(12, window.TimelineOffsetSeconds, "Opposite wheel restores viewport");
        var rightX = tracks.ViewportWidth - window.TrackHeaderWidth - 10;
        window.TimelineOffsetSeconds = scroll.Maximum;
        var rightTime = window.TimelineOffsetSeconds + rightX / window.TimelinePixelsPerSecond;
        window.TryZoomTimelineWheel(120, ModifierKeys.Control, rightX);
        Near(rightTime, window.TimelineOffsetSeconds + rightX / window.TimelinePixelsPerSecond, "Right edge anchor with visible vertical scrollbar");
        Require(window.TimelineOffsetSeconds <= scroll.Maximum, "Zoom offset remains inside scroll range");
        slider.Value = 40;
        window.TimelineOffsetSeconds = 0;
        window.TryZoomTimelineWheel(-120, ModifierKeys.Control, x);
        Near(0, window.TimelineOffsetSeconds, "Zoom-out at project start clamps to zero");
        slider.Value = slider.Maximum;
        window.TryZoomTimelineWheel(120, ModifierKeys.Control, x);
        Near(slider.Maximum, window.TimelinePixelsPerSecond, "Maximum zoom is respected");
        slider.Value = slider.Minimum;
        window.TryZoomTimelineWheel(-120, ModifierKeys.Control, x);
        Near(slider.Minimum, window.TimelinePixelsPerSecond, "Minimum zoom is respected");
        Require(!window.TryZoomTimelineWheel(120, ModifierKeys.Control, -1), "Track-name column does not trigger horizontal zoom");
        slider.Value = 40;
        window.TimelineOffsetSeconds = 0;
        Require(before == JsonSerializer.Serialize(window.CurrentProject) && dirty == window.HasUnsavedChanges && playhead == window.PlayheadSeconds,
            "Zoom preserves project contents, dirty state and playhead");
    }

    private static Clip Drop(MainWindow window, IDataObject data, string trackId, double pointerX)
    {
        Require(window.TryDropLibraryAsset(data, trackId, pointerX, out var clip) && clip is not null,
            $"Expected an accepted library drop on {trackId} at pixel {pointerX:R}.");
        return clip!;
    }

    private static void AssertRejected(MainWindow window, IDataObject data, string trackId, double pointerX, string scenario)
    {
        var before = JsonSerializer.Serialize(window.CurrentProject);
        var wasDirty = window.HasUnsavedChanges;
        var selected = window.SelectedTimelineClipId;
        Require(!window.EvaluateTimelineDrop(data, trackId, pointerX, out _, out _, out var reason) &&
            !string.IsNullOrWhiteSpace(reason), scenario + " must be rejected with an explanation.");
        Require(!window.TryDropLibraryAsset(data, trackId, pointerX, out var added) && added is null,
            scenario + " must not insert a clip.");
        Require(before == JsonSerializer.Serialize(window.CurrentProject) && wasDirty == window.HasUnsavedChanges &&
            selected == window.SelectedTimelineClipId, scenario + " must preserve data, dirty state and selection.");
    }

    private static string Fingerprint(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void Near(double expected, double actual, string scenario) =>
        Require(double.IsFinite(actual) && Math.Abs(expected - actual) <= 0.0000001,
            $"{scenario}: expected {expected:R}, got {actual:R}.");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Timeline smoke check failed: " + message);
    }
}
