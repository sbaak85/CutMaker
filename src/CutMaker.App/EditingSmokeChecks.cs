using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using CutMaker.Core;

namespace CutMaker.App;

internal static class EditingSmokeChecks
{
    internal static void Run(MainWindow window, string folder)
    {
        var report = Path.Combine(folder, "editing-result.txt");
        File.WriteAllText(report, "RUNNING: timeline editing, inspector, undo/redo and project round trip.");
        var imported = window.CurrentProject.MediaAssets.ToArray();
        var visual = imported.FirstOrDefault(asset => asset.Kind == MediaKind.Video && asset.Duration >= 4)
            ?? imported.First(asset => asset.Kind == MediaKind.Image);
        var hashes = imported.Where(asset => File.Exists(asset.Path)).ToDictionary(asset => asset.Path, asset => Hash(asset.Path), StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(folder, "editing-roundtrip.cutmaker");
        var project = CutProject.CreateEmpty("CutMaker · 基本剪輯原型驗證") with
        {
            MediaAssets = [.. imported],
            Tracks = [new("video-1", "主要影片", TrackKind.Video), new("video-2", "疊加影片", TrackKind.Video), new("audio-1", "音訊混音", TrackKind.Audio)],
            Clips = [new("editable", visual.Id, "video-1", 1, .5, 3)]
        };
        window.LoadSmokeProject(project, path);
        window.SelectedTimelineClipId = "editable";
        var original = project.Clips[0];
        var moved = TimelineEditor.Move(window.CurrentProject, original.Id, "video-2", 2);
        Require(window.TryReplaceClip(moved, "Smoke move"), "Cross-track move accepted");
        Require(window.HasUnsavedChanges && window.CurrentProject.Clips.Single() == moved, "Move committed and marked dirty");
        window.UndoEdit(); Require(window.CurrentProject.Clips.Single() == original, "Undo move restores exact timing and track");
        window.RedoEdit(); Require(window.CurrentProject.Clips.Single() == moved, "Redo move restores exact edit");

        // At 40 px/sec a press six pixels inside either edge must not add a six-pixel jump.
        var headDrag = window.PlanPointerTrim(moved, true, moved.Start + .15, moved.Start + .25);
        Near(moved.Start + .1, headDrag.Start); Near(moved.SourceIn + .1, headDrag.SourceIn); Near(moved.End, headDrag.End);
        var tailDrag = window.PlanPointerTrim(moved, false, moved.End - .15, moved.End - .25);
        Near(moved.End - .1, tailDrag.End); Near(moved.SourceIn, tailDrag.SourceIn);
        Require(window.PlanPointerTrim(moved, true, moved.Start + .15, moved.Start + .15) == moved &&
            window.PlanPointerTrim(moved, false, moved.End - .15, moved.End - .15) == moved,
            "Stationary press inside either edge preserves exact clip bounds");

        var head = ClipEditor.TrimStart(moved, 2.5, visual.Duration);
        Require(window.TryReplaceClip(head, "Smoke head trim"), "Head trim accepted");
        Near(1, head.SourceIn); Near(2.5, head.Duration); Near(moved.End, head.End);
        var tail = ClipEditor.TrimEnd(head, 4.5, visual.Duration);
        Require(window.TryReplaceClip(tail, "Smoke tail trim"), "Tail trim accepted");
        Near(2, tail.Duration);

        // Exercise the actual inspector Apply handler with numeric text and named controls.
        SetBox(window, "ClipStartBox", "2.5"); SetBox(window, "ClipSourceInBox", "1"); SetBox(window, "ClipDurationBox", "2");
        SetBox(window, "ClipGainBox", "65"); SetBox(window, "FadeInBox", "0.25"); SetBox(window, "FadeOutBox", "0.4");
        ((ComboBox)window.FindName("FadeInCurveBox")).SelectedValue = FadeCurve.SmoothStep;
        ((ComboBox)window.FindName("FadeOutCurveBox")).SelectedValue = FadeCurve.EqualPower;
        ((Button)window.FindName("ApplyClipButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var effects = window.CurrentProject.Clips.Single();
        Near(.65, effects.Gain);
        Require(effects.FadeIn == new FadeSettings(.25, FadeCurve.SmoothStep) && effects.FadeOut == new FadeSettings(.4, FadeCurve.EqualPower),
            "Inspector applies gain, fade durations and chosen curves");

        var beforeInvalid = JsonSerializer.Serialize(window.CurrentProject);
        SetBox(window, "ClipDurationBox", "999");
        ((Button)window.FindName("ApplyClipButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(beforeInvalid == JsonSerializer.Serialize(window.CurrentProject), "Invalid inspector edit does not mutate project");
        Require(!window.TryReplaceClip(effects with { TrackId = "audio-1" }, "invalid"), "Wrong-track edit rejected");
        Require(beforeInvalid == JsonSerializer.Serialize(window.CurrentProject), "Wrong-track rejection is non-destructive");

        Require(!window.SplitSelectedClip(2.6), "Split inside active fade is rejected");
        Require(window.SplitSelectedClip(3.5), "Split inside clip outside fade accepted");
        var pieces = window.CurrentProject.Clips.OrderBy(clip => clip.Start).ToArray();
        Require(pieces.Length == 2, "Split creates two pieces");
        Near(pieces[0].End, pieces[1].Start); Near(pieces[0].SourceEnd, pieces[1].SourceIn);
        Near(effects.Duration, pieces.Sum(clip => clip.Duration));
        Require(pieces[0].FadeOut is null && pieces[1].FadeIn is null, "Split removes inner fades only");
        window.UndoEdit(); Require(window.CurrentProject.Clips.Single() == effects, "Undo split returns original gain and fades");
        window.RedoEdit(); Require(window.CurrentProject.Clips.SequenceEqual(pieces), "Redo restores independent clip IDs and source continuity");

        var selected = window.SelectedTimelineClipId;
        window.RemoveSelectedClip(); Require(window.CurrentProject.Clips.Count == 1, "Delete removes one selected piece");
        window.UndoEdit(); Require(window.CurrentProject.Clips.Count == 2 && window.SelectedTimelineClipId == selected, "Undo delete restores clip selection and data");
        var count = window.CurrentProject.Tracks.Count;
        window.AddSmokeTrack(); Require(window.CurrentProject.Tracks.Count == count + 1, "New track action is recorded");
        window.UndoEdit(); Require(window.CurrentProject.Tracks.Count == count, "Undo new track restores collection");

        var audioTrack = window.CurrentProject.Tracks.First(track => track.Id == "audio-1");
        Require(window.ApplyTrackSettings(audioTrack with { Volume = .45, Muted = true, Name = "音訊 · 靜音測試" }), "Track gain/mute accepted");
        Require(window.CurrentProject.Tracks.First(track => track.Id == "audio-1").Muted, "Track mute applied");
        window.UndoEdit(); Require(window.CurrentProject.Tracks.First(track => track.Id == "audio-1") == audioTrack, "Undo track settings");
        window.RedoEdit(); Near(.45, window.CurrentProject.Tracks.First(track => track.Id == "audio-1").Volume);
        var videoTrack = window.CurrentProject.Tracks.First(track => track.Id == "video-2");
        window.ApplyTrackSettings(videoTrack with { Locked = true });
        var lockedClip = window.CurrentProject.Clips[0];
        Require(!window.TryReplaceClip(lockedClip with { Start = 0 }, "invalid"), "Locked-track editing rejected");
        window.UndoEdit();

        // A new mutation after Undo must invalidate Redo, and Undo must keep independent list snapshots.
        var trackCount = window.CurrentProject.Tracks.Count;
        window.AddSmokeTrack(); window.RedoEdit();
        Require(window.CurrentProject.Tracks.Count == trackCount + 1 && !window.CurrentProject.Tracks.First(track => track.Id == "video-2").Locked,
            "New edit clears redo history");
        window.UndoEdit();
        ProjectStore.Save(path, window.CurrentProject);
        var saved = window.CurrentProject;
        var reopened = ProjectStore.Load(path);
        Require(saved.Clips.SequenceEqual(reopened.Clips) && saved.Tracks.SequenceEqual(reopened.Tracks) && saved.MediaAssets.SequenceEqual(reopened.MediaAssets),
            "Edited timing, source ranges, gain, fades, volume and mute persist exactly");
        window.LoadSmokeProject(reopened, path);
        var clean = JsonSerializer.Serialize(window.CurrentProject);
        window.UndoEdit();
        Require(!window.HasUnsavedChanges && clean == JsonSerializer.Serialize(window.CurrentProject), "Opening a project clears old history");
        window.SelectedTimelineClipId = window.CurrentProject.Clips.First().Id;
        // Refresh the inspector for the final editing screenshot without changing the saved project.
        window.SelectTimelineClipForChecks(window.SelectedTimelineClipId);
        foreach (var (source, fingerprint) in hashes) Require(Hash(source) == fingerprint, "Editing leaves source bytes untouched");
        File.WriteAllText(report, new StringBuilder()
            .AppendLine("PASS: cross-track move; head/tail source-preserving trims without pointer-grab jumps; inspector gain/fade durations/curves; invalid-edit rejection;")
            .AppendLine("split continuity and fade restrictions; delete; exact Undo/Redo of clips and tracks; redo invalidation; locked track rejection;")
            .AppendLine("track gain/mute; edited project save/reopen; history reset on project switch; all imported source hashes unchanged.").ToString());
    }

    private static void SetBox(MainWindow window, string name, string value) => ((TextBox)window.FindName(name)).Text = value;
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static void Near(double expected, double actual) => Require(Math.Abs(expected - actual) < .0000001, $"Expected {expected:R}, got {actual:R}");
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException("Editing smoke check failed: " + message); }
}
