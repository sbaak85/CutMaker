using System.Text.Json;
using CutMaker.Core;

internal static class GroupMoveChecks
{
    public static void Run()
    {
        InterleavedTracksAndLinkedPairs();
        VacatedPositionsAndTouchingEdges();
        RejectWholeGroup();
        InvalidArguments();
    }

    private static CutProject Sample() => new()
    {
        Title = "Group moves",
        MediaAssets = [new("video", "source.mp4", MediaKind.Video, 60), new("audio", "source.wav", MediaKind.Audio, 60)],
        Tracks = Enumerable.Range(0, 4).SelectMany(i => new[]
        {
            new Track($"v{i}", $"Video {i}", TrackKind.Video),
            new Track($"a{i}", $"Audio {i}", TrackKind.Audio)
        }).ToList(),
        Clips =
        [
            new("primary", "video", "v0", 5, 1, 2, .6, new(.5, FadeCurve.EaseIn), new(.25),
                LinkGroupId: "av", SourceAudioMuted: true),
            new("partner", "video", "a0", 5.25, 1.25, 1.5, .6, new(.2), LinkGroupId: "av"),
            new("second-video", "video", "v2", 8, 4, 1),
            new("second-audio", "audio", "a2", 8.5, 2, 1)
        ]
    };

    private static void InterleavedTracksAndLinkedPairs()
    {
        var project = Sample();
        var before = JsonSerializer.Serialize(project);
        var ids = new[] { "primary", "second-video", "second-audio" };
        var plan = TimelineBatchEditor.PlanMove(project, ids, 1.25, "primary", "v1");
        Assert(plan.TrackOffset == 1 && plan.MovedClips.Count == 4, "A linked partner is included without explicit selection");
        var destinations = new Dictionary<string, string>
        { ["primary"] = "v1", ["partner"] = "a1", ["second-video"] = "v3", ["second-audio"] = "a3" };
        foreach (var original in project.Clips)
        {
            var moved = plan.MovedClips.Single(c => c.Id == original.Id);
            Assert(moved == (original with { TrackId = destinations[original.Id], Start = original.Start + 1.25 }),
                "Moves retain source timing, Fade curves, gain, mute and link identity");
            Assert(plan.Project.Clips.Single(c => c.Id == original.Id) == moved, "Ghosts match the complete candidate project");
        }
        Assert(before == JsonSerializer.Serialize(project), "Planning does not mutate input records, assets or track order");
        var reversed = TimelineBatchEditor.Move(plan.Project, ids, -1.25, "partner", "a0");
        Assert(reversed.Clips.SequenceEqual(project.Clips), "Audio-primary moves use the same typed-track offset and preserve all relative timing");
        Assert(reversed.Tracks.SequenceEqual(project.Tracks) && reversed.MediaAssets.SequenceEqual(project.MediaAssets),
            "No source or track is inserted or changed by a group move");

        var horizontal = TimelineBatchEditor.PlanMove(project, ["primary"], .5, "primary", "v0");
        Assert(horizontal.TrackOffset == 0 && horizontal.MovedClips.Count == 2, "Same-track movement keeps linked pairs together");
        Assert(horizontal.Project.Clips.Single(c => c.Id == "second-video") == project.Clips[2], "Unselected clips stay fixed");
        var single = TimelineBatchEditor.Move(project, ["second-video"], 0, "second-video", "v3");
        Assert(single.Clips[2].TrackId == "v3" && single.Clips[3] == project.Clips[3], "A single unlinked clip moves to another same-kind track");
    }

    private static void VacatedPositionsAndTouchingEdges()
    {
        var project = Sample() with
        {
            Clips = [new("first", "video", "v0", 5, 0, 2), new("next", "video", "v0", 8, 2, 2)]
        };
        var moved = TimelineBatchEditor.Move(project, ["first", "next"], 3, "first", "v0");
        Assert(moved.Clips[0].Start == 8 && moved.Clips[1].Start == 11,
            "A group may move into its own vacated positions without sequential collision failures");
        var edge = project with { Clips = [.. project.Clips, new("obstacle", "video", "v1", 10, 0, 2)] };
        var touching = TimelineBatchEditor.Move(edge, ["first"], 3, "first", "v1");
        Assert(touching.Clips[0].End == touching.Clips[2].Start, "Destination touching is allowed without overlap");
    }

    private static void RejectWholeGroup()
    {
        var original = Sample();
        var ids = new[] { "primary", "second-video", "second-audio" };
        Reject(original, () => TimelineBatchEditor.Move(original, ids, 0, "primary", "v2"), "影片");
        var fewerAudio = original with { Tracks = original.Tracks.Where(t => t.Id != "a3").ToList() };
        Reject(fewerAudio, () => TimelineBatchEditor.Move(fewerAudio, ids, 0, "primary", "v1"), "音訊");
        Reject(original, () => TimelineBatchEditor.Move(original, ["primary", "second-video"], 0, "second-video", "v1"), "上方");
        Reject(original, () => TimelineBatchEditor.Move(original, ids, -6, "primary", "v1"));
        // A failure on a secondary or implicitly linked clip must reject the otherwise valid primary move.
        foreach (var trackId in new[] { "v0", "v1", "a0", "a1", "v2", "v3", "a2", "a3" })
        {
            var locked = original with { Tracks = original.Tracks.Select(t => t.Id == trackId ? t with { Locked = true } : t).ToList() };
            Reject(locked, () => TimelineBatchEditor.Move(locked, ids, 0, "primary", "v1"), "鎖定");
        }
        foreach (var (track, start) in new[] { ("v1", 5.5), ("a1", 5.5), ("v3", 8.5), ("a3", 9.0) })
        {
            var blocked = original with { Clips = [.. original.Clips, new("obstacle", track[0] == 'v' ? "video" : "audio", track, start, 0, 1)] };
            Reject(blocked, () => TimelineBatchEditor.Move(blocked, ids, 0, "primary", "v1"), "重疊");
        }
        Reject(original, () => TimelineBatchEditor.Move(original, ["primary"], 0, "primary", "a1"), "相同類型");
        Reject(original, () => TimelineBatchEditor.Move(original, ["second-video"], 0, "second-video", "a3"), "相同類型");
        Reject(original, () => TimelineBatchEditor.Move(original, ["second-audio"], 0, "second-audio", "v3"), "相同類型");
    }

    private static void InvalidArguments()
    {
        var project = Sample();
        foreach (var delta in new[] { double.NaN, double.NegativeInfinity, double.PositiveInfinity, double.MaxValue })
            Reject(project, () => TimelineBatchEditor.Move(project, ["primary"], delta, "primary", "v1"));
        Reject(project, () => TimelineBatchEditor.Move(project, [], 0, "primary", "v1"));
        Reject(project, () => TimelineBatchEditor.Move(project, ["primary"], 0, "missing", "v1"));
        Reject(project, () => TimelineBatchEditor.Move(project, ["primary", "missing"], 0, "primary", "v1"));
        Reject(project, () => TimelineBatchEditor.Move(project, ["primary"], 0, "primary", "missing"));
        Reject(project, () => TimelineBatchEditor.Move(project, ["second-video"], 0, "primary", "v1"));
    }

    private static void Reject(CutProject project, Action operation, string? message = null)
    {
        var before = JsonSerializer.Serialize(project);
        try { operation(); }
        catch (ProjectValidationException error)
        {
            Assert(message is null || error.Message.Contains(message, StringComparison.Ordinal), "Rejections explain the unavailable destination");
            Assert(before == JsonSerializer.Serialize(project), "A rejected group plan leaves the entire input project unchanged");
            return;
        }
        throw new Exception("Expected complete group move to be rejected.");
    }

    private static void Assert(bool condition, string message)
    { if (!condition) throw new Exception(message); }
}
