using System.IO;
using System.Text;
using System.Text.Json;
using CutMaker.Core;

namespace CutMaker.App;

internal static class BatchSmokeChecks
{
    internal static void Run(MainWindow window, string folder)
    {
        var report = Path.Combine(folder, "batch-result.txt");
        File.WriteAllText(report, "RUNNING: multi-selection, linked operations and atomic batch rejection.");
        CheckCoreOperations();
        var prior = window.CurrentProject;
        var priorPath = window.CurrentProjectPath ?? Path.Combine(folder, "before-batch.cutmaker");
        var audio = prior.MediaAssets.First(a => a.Kind == MediaKind.Audio && a.Duration >= .6);
        var image = prior.MediaAssets.First(a => a.Kind == MediaKind.Image);
        var project = CutProject.CreateEmpty("連動與批次編輯驗證") with
        {
            MediaAssets = [.. prior.MediaAssets],
            Tracks = [new("a", "連動音軌 A", TrackKind.Audio), new("b", "連動音軌 B", TrackKind.Audio),
                new("c", "空白音軌", TrackKind.Audio), new("v", "圖片軌", TrackKind.Video)],
            Clips = [new("one", audio.Id, "a", 1, 0, .6, LinkGroupId: "group"),
                new("two", audio.Id, "b", 1, 0, .6, LinkGroupId: "group"), new("still", image.Id, "v", 0, 0, 5)]
        };
        window.LoadSmokeProject(project, Path.Combine(folder, "batch-roundtrip.cutmaker"));
        window.SetClipSelection(["one"], "one");
        Require(window.SelectedTimelineClipIds?.Count == 2, "Selecting a linked member selects its complete group");
        window.CopySelectedClips();
        Require(window.PasteClips(3), "Pasting copied linked clips succeeds");
        var added = window.CurrentProject.Clips.Where(c => c.Start == 3).ToArray();
        Require(added.Length == 2 && added.Select(c => c.LinkGroupId).Distinct().Count() == 1 && added[0].LinkGroupId != "group",
            "Clipboard creates fresh IDs and a separate linked group");
        Require(window.SelectedTimelineClipIds?.Count == 2, "Pasted group selected together");
        window.UndoEdit(); Require(window.CurrentProject.Clips.Count == 3, "One undo removes the entire pasted group");
        window.RedoEdit(); Require(added.All(c => window.CurrentProject.Clips.Contains(c)), "Redo preserves pasted IDs and link group");
        window.UndoEdit();
        var beforeOverlap = Snapshot(window.CurrentProject);
        Require(!window.PasteClips(1), "Overlapping paste rejected");
        Require(beforeOverlap == Snapshot(window.CurrentProject), "Overlapping paste is atomic");

        window.SetClipSelection(["one"], "one");
        var original = window.CurrentProject.Clips.Single(c => c.Id == "one");
        Require(window.TryReplaceClip(original with { Start = 1.2 }, "batch linked move"), "Inspector movement moves linked partner");
        Require(window.CurrentProject.Clips.Where(c => c.LinkGroupId == "group").All(c => Math.Abs(c.Start - 1.2) < 1e-7), "Linked starts remain synchronized");
        var movedState = Snapshot(window.CurrentProject);
        Require(!window.TryReplaceClip(window.CurrentProject.Clips.Single(c => c.Id == "one") with { TrackId = "c" }, "invalid track"),
            "Linked track-only edits must be rejected");
        Require(movedState == Snapshot(window.CurrentProject), "Track-only rejection preserves the entire group");
        window.UndoEdit();
        var trimmed = ClipEditor.TrimStart(original, 1.1, audio.Duration);
        Require(window.TryReplaceClip(trimmed, "linked trim"), "Linked source trim accepted");
        foreach (var member in window.CurrentProject.Clips.Where(c => c.LinkGroupId == "group"))
        { Near(1.1, member.Start); Near(.1, member.SourceIn); Near(.5, member.Duration); }
        window.UndoEdit();
        window.SetClipSelection(["one"], "one");
        Require(window.SplitSelectedClip(1.3), "Linked split accepted");
        var split = window.CurrentProject.Clips.Where(c => c.AssetId == audio.Id).ToArray();
        Require(split.Length == 4 && split.GroupBy(c => c.LinkGroupId).All(g => g.Count() == 2), "Split forms independent left and right linked pairs");
        foreach (var group in split.GroupBy(c => c.LinkGroupId))
            Require(group.Select(c => (c.Start, c.SourceIn, c.Duration)).Distinct().Count() == 1, "Split pairs preserve source/timeline synchronization");
        window.UndoEdit();

        var partnerTrack = window.CurrentProject.Tracks.Single(t => t.Id == "b");
        Require(window.ApplyTrackSettings(partnerTrack with { Locked = true }), "Partner track locks");
        window.SetClipSelection(["one"], "one");
        var lockedState = Snapshot(window.CurrentProject);
        Require(!window.TryReplaceClip(original with { Start = 2 }, "locked") && !window.DeleteSelectedClips(false),
            "A locked partner rejects both linked movement and deletion");
        Require(lockedState == Snapshot(window.CurrentProject), "Locked batch rejection is atomic");
        window.UndoEdit();
        window.SetClipSelection(["one"], "one");
        Require(window.DeleteSelectedClips(false) && window.CurrentProject.Clips.Count == 1, "Deletion removes all selected linked members");
        window.UndoEdit(); Require(window.CurrentProject.Clips.Count == 3, "Undo restores linked deletion");

        window.SetClipSelection(["still"]);
        var still = window.CurrentProject.Clips.Single(c => c.Id == "still");
        var extended = window.PlanPointerTrim(still, false, 5, 25);
        Near(25, extended.Duration); Near(0, extended.SourceIn);
        Require(window.TryReplaceClip(extended, "long still"), "Image may extend beyond initial import duration");
        Require(window.SplitSelectedClip(20), "Long still split succeeds");
        Require(window.CurrentProject.Clips.Where(c => c.AssetId == image.Id).All(c => c.SourceIn == 0), "Still splits retain zero source offsets");
        window.UndoEdit(); window.UndoEdit();

        var path = Path.Combine(folder, "batch-roundtrip.cutmaker");
        ProjectStore.Save(path, window.CurrentProject);
        Require(ProjectStore.Load(path).Clips.SequenceEqual(window.CurrentProject.Clips), "Linked IDs survive project save/reopen");
        window.LoadSmokeProject(prior, priorPath);
        window.SelectTimelineClipForChecks(prior.Clips.FirstOrDefault()?.Id);
        File.WriteAllText(report, new StringBuilder()
            .AppendLine("PASS: atomic batch move/replace/delete/split/paste; source-audio extraction; lock/overlap rejection;")
            .AppendLine("ripple link synchronization guard; new clipboard link groups; UI multi-selection, copy/paste, linked move/trim/split/delete;")
            .AppendLine("track-only linked edit rejection; exact Undo/Redo; still extension and split; project round trip.").ToString());
    }

    private static void CheckCoreOperations()
    {
        var source = CutProject.CreateEmpty("batch core") with
        {
            MediaAssets = [new("movie", "source.mp4", MediaKind.Video, 60)],
            Tracks = [new("v", "Video", TrackKind.Video, Volume: .7), new("v2", "Second", TrackKind.Video)],
            Clips = [new("movie-clip", "movie", "v", 2, 1, 4, .6, new(3, FadeCurve.Custom, .8, .2), new(2))]
        };
        var before = Snapshot(source);
        var extracted = TimelineBatchEditor.ExtractAudio(source, "movie-clip");
        Require(before == Snapshot(source), "Extract planning preserves input project");
        var video = extracted.Clips.Single(c => c.Id == "movie-clip");
        var sound = extracted.Clips.Single(c => c.Id != video.Id);
        Require(video.SourceAudioMuted && !sound.SourceAudioMuted && video.Gain == .6 && sound.Gain == .6,
            "Extraction disables original audio independently of gain");
        Require(video.LinkGroupId is not null && video.LinkGroupId == sound.LinkGroupId, "Extraction links original video and audio");
        Near(.7, extracted.Tracks.Single(t => t.Id == sound.TrackId).Volume);
        var selected = TimelineBatchEditor.ExpandLinks(extracted, [video.Id]);
        Require(selected.SetEquals([video.Id, sound.Id]), "Link expansion includes source and extracted audio");
        var moved = TimelineBatchEditor.Move(extracted, [video.Id], 2, video.Id, video.TrackId);
        Require(moved.Clips.All(c => c.Start == 4 && c.SourceIn == 1), "Group move preserves source and tracks");
        Reject(() => TimelineBatchEditor.Move(extracted, [video.Id], 2, video.Id, "v2"), "Group cross-track move rejected");
        var locked = extracted with { Tracks = extracted.Tracks.Select(t => t.Id == sound.TrackId ? t with { Locked = true } : t).ToList() };
        var lockedBefore = Snapshot(locked);
        Reject(() => TimelineBatchEditor.Move(locked, [video.Id], 1, video.Id, video.TrackId), "Locked partner prevents group movement");
        Reject(() => TimelineBatchEditor.Split(locked, [video.Id], 3), "Locked partner prevents group splitting");
        Reject(() => TimelineBatchEditor.Delete(locked, [video.Id], false), "Locked partner prevents group deletion");
        Require(lockedBefore == Snapshot(locked), "Failed group edits preserve all input clips");
        var obstacle = extracted with { Clips = [.. extracted.Clips, new("obstacle", "movie", "v", 8, 0, 2)] };
        var obstacleBefore = Snapshot(obstacle);
        Reject(() => TimelineBatchEditor.Move(obstacle, [video.Id], 4, video.Id, video.TrackId), "Group overlap rejected");
        Require(obstacleBefore == Snapshot(obstacle), "Rejected overlapping group movement is atomic");

        var cut = TimelineBatchEditor.Split(extracted, [video.Id], 3);
        Require(cut.Clips.Count == 4 && cut.Clips.GroupBy(c => c.LinkGroupId).All(g => g.Count() == 2), "Core split generates separate complete link pairs");
        foreach (var original in extracted.Clips)
        {
            var right = cut.Clips.Single(c => c.TrackId == original.TrackId && c.Start == 3);
            Near(FadeEnvelope.Gain(original, 1.5, true), FadeEnvelope.Gain(right, .5, true));
        }

        var paste = TimelineBatchEditor.Paste(extracted, extracted.Clips, 10);
        Require(paste.AddedIds.Length == 2 && paste.Project.Clips.Select(c => c.Id).Distinct().Count() == 4, "Paste generates independent clip IDs");
        var newLinks = paste.Project.Clips.Where(c => paste.AddedIds.Contains(c.Id)).Select(c => c.LinkGroupId).Distinct().ToArray();
        Require(newLinks.Length == 1 && newLinks[0] != video.LinkGroupId, "Paste generates a new shared link identity");

        var preceding = new Clip("preceding", "movie", "v", 0, 0, 1);
        var unsafeRipple = extracted with { Clips = [preceding, .. extracted.Clips] };
        var rippleBefore = Snapshot(unsafeRipple);
        Reject(() => TimelineBatchEditor.Delete(unsafeRipple, [preceding.Id], true), "One-track ripple cannot desynchronize linked clips");
        Require(rippleBefore == Snapshot(unsafeRipple), "Rejected ripple preserves all positions");
        var safeRipple = source with { Clips = [new("first", "movie", "v", 0, 0, 1), new("next", "movie", "v", 3, 0, 2)] };
        var closed = TimelineBatchEditor.Delete(safeRipple, ["first"], true);
        Near(2, closed.Clips.Single().Start);
    }

    private static string Snapshot(CutProject project) => JsonSerializer.Serialize(project);
    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (ProjectValidationException) { return; }
        throw new InvalidOperationException("Batch smoke check failed: " + message);
    }
    private static void Near(double expected, double actual) => Require(Math.Abs(expected - actual) < 1e-6, $"Expected {expected:R}, got {actual:R}");
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException("Batch smoke check failed: " + message); }
}
