using System.Text.Json;
using CutMaker.Core;

var checks = new (string Name, Action Run)[]
{
    ("Trim head preserves source alignment and original record", TrimHead),
    ("Trim tail can restore source and clamps fades", TrimTail),
    ("Invalid trim and split boundaries are rejected", InvalidEdits),
    ("Split keeps source continuity and total duration", SplitContinuity),
    ("Split preserves exact fade envelopes inside fades and at their boundaries", SplitFadeBoundaries),
    ("Custom and independent audio fades retain all samples through repeated splits", CustomFadeSplits),
    ("Invalid custom controls and ranges are rejected", InvalidFadeControls),
    ("Existing schema-one fades load with compatible defaults", LegacyFadeDefaults),
    ("Images extend and split without advancing source time", ImageEdits),
    ("Project references, finite numbers and fade limits are validated", InvalidProjects),
    ("Save/load round trip, replacement and relative paths", SaveLoad),
    ("Malformed, missing and unsupported schemas are rejected", InvalidFiles),
    ("Rejected or failed saves preserve the old project", SafeSave),
    ("Import classifies supported files without changing source files or project assets", ImportCandidates),
    ("Import rejects directories, unavailable files and unsupported types while keeping valid files", ImportRejections),
    ("Import resolves relative assets and removes existing and batch duplicates", ImportDuplicates),
    ("Timeline placement enforces media and track compatibility", PlacementCompatibility),
    ("Timeline placement preserves existing clips, rejects overlap and allows touching edges", PlacementOverlap),
    ("Timeline placement rejects locked tracks, missing references and invalid times", PlacementRejections),
    ("Repeated source placements keep independent IDs and survive save/load", PlacementRoundTrip),
    ("Timeline positions round to frames without changing source durations", PlacementFrames),
    ("Moving clips preserves source timing and enforces source/target locks", EditingMoves),
    ("Trim and inspector edits reject overlap and invalid source/fade/gain ranges", EditingBounds),
    ("Recovery snapshots preserve sources, isolate sessions and survive cancellation", RecoveryChecks.Run)
};
var failures = 0;
foreach (var (name, run) in checks)
{
    try { run(); Console.WriteLine($"PASS {name}"); }
    catch (Exception error) { failures++; Console.Error.WriteLine($"FAIL {name}: {error}"); }
}
Console.WriteLine($"{checks.Length - failures}/{checks.Length} checks passed.");
return failures == 0 ? 0 : 1;

static Clip SampleClip() => new("clip-1", "asset-1", "video-1", 10, 3, 12, 0.8,
    new FadeSettings(2, FadeCurve.EaseIn), new FadeSettings(3, FadeCurve.EaseOut));

static CutProject SampleProject() => CutProject.CreateEmpty("Core checks") with
{
    MediaAssets = [new("asset-1", "media/sample.mp4", MediaKind.Video, 60)],
    Clips = [SampleClip()]
};

static void TrimHead()
{
    var original = SampleClip();
    var trimmed = ClipEditor.TrimStart(original, 14, 60);
    Near(14, trimmed.Start); Near(7, trimmed.SourceIn); Near(8, trimmed.Duration); Near(original.End, trimmed.End);
    Near(3, original.SourceIn); Near(12, original.Duration);
    var restored = ClipEditor.TrimStart(trimmed, 10, 60);
    Near(original.Start, restored.Start); Near(original.SourceIn, restored.SourceIn); Near(original.Duration, restored.Duration);
}

static void TrimTail()
{
    var original = SampleClip();
    var trimmed = ClipEditor.TrimEnd(original, 11, 60);
    Near(1, trimmed.Duration); Near(1, trimmed.FadeIn!.Duration); Near(1, trimmed.FadeOut!.Duration);
    var extended = ClipEditor.TrimEnd(trimmed, 25, 60);
    Near(15, extended.Duration); Near(original.SourceIn, extended.SourceIn); Near(original.Start, extended.Start);
}

static void InvalidEdits()
{
    var clip = SampleClip();
    Throws<ProjectValidationException>(() => ClipEditor.TrimStart(clip, 6, 60));
    Throws<ProjectValidationException>(() => ClipEditor.TrimStart(clip, clip.End, 60));
    Throws<ProjectValidationException>(() => ClipEditor.TrimEnd(clip, clip.Start, 60));
    Throws<ProjectValidationException>(() => ClipEditor.TrimEnd(clip, 100, 60));
    Throws<ProjectValidationException>(() => ClipEditor.TrimStart(clip, double.NaN, 60));
    Throws<ProjectValidationException>(() => ClipEditor.Split(clip, clip.Start, "new"));
    Throws<ProjectValidationException>(() => ClipEditor.Split(clip, clip.End, "new"));
    Throws<ProjectValidationException>(() => ClipEditor.Split(clip, 15, clip.Id));
}

static void SplitContinuity()
{
    var original = SampleClip();
    var (left, right) = ClipEditor.Split(original, 15.25, "clip-2");
    Near(original.Start, left.Start); Near(left.End, right.Start); Near(original.End, right.End);
    Near(original.SourceIn, left.SourceIn); Near(left.SourceEnd, right.SourceIn); Near(original.SourceEnd, right.SourceEnd);
    Near(original.Duration, left.Duration + right.Duration);
    True(left.Id == original.Id && right.Id == "clip-2", "Split IDs");
    True(left.FadeOut is null && right.FadeIn is null, "Interior fade edges");
    var project = SampleProject() with { Clips = [left, right] };
    ProjectValidator.Validate(project);
}

static void SplitFadeBoundaries()
{
    var original = SampleClip();
    foreach (var position in new[] { 11.0, 20.0 }) CheckSplitEnvelope(original, position);

    foreach (var position in new[] { original.Start + original.FadeIn!.Duration, original.End - original.FadeOut!.Duration })
    {
        var (left, right) = ClipEditor.Split(original, position, "clip-2");
        True(left.FadeIn == original.FadeIn && right.FadeOut == original.FadeOut, "Boundary split preserves outer Fades");
        Near(left.End, right.Start); Near(left.SourceEnd, right.SourceIn);
        ProjectValidator.Validate(SampleProject() with { Clips = [left, right] });
    }

    var touchingFades = original with { FadeIn = new FadeSettings(6), FadeOut = new FadeSettings(6) };
    var touching = ClipEditor.Split(touchingFades, 16, "clip-2");
    Near(6, touching.Left.FadeIn!.Duration); Near(6, touching.Right.FadeOut!.Duration);
    var overlappingFades = original with { FadeIn = new FadeSettings(7), FadeOut = new FadeSettings(7) };
    CheckSplitEnvelope(overlappingFades, 16);
    var zeroFades = original with { FadeIn = new FadeSettings(), FadeOut = new FadeSettings() };
    ClipEditor.Split(zeroFades, 10.5, "clip-2");
    True(original.FadeIn!.Duration == 2 && original.FadeOut!.Duration == 3 && original.Duration == 12,
        "Split planning does not mutate the source record");
}

static void CheckSplitEnvelope(Clip original, double position)
{
    var (left, right) = ClipEditor.Split(original, position, original.Id + "-split");
    Near(original.SourceIn, left.SourceIn); Near(left.SourceEnd, right.SourceIn); Near(right.SourceEnd, original.SourceEnd);
    ProjectValidator.Validate(SampleProject() with { Clips = [left, right] });
    for (var index = 0; index <= 400; index++)
    {
        var time = original.Duration * index / 400;
        var piece = time < left.Duration ? left : right;
        var offset = time < left.Duration ? 0 : left.Duration;
        Near(FadeEnvelope.Gain(original, time), FadeEnvelope.Gain(piece, time - offset));
        Near(FadeEnvelope.Gain(original, time, audio: true), FadeEnvelope.Gain(piece, time - offset, audio: true));
    }
}

static void CustomFadeSplits()
{
    foreach (var curve in Enum.GetValues<FadeCurve>())
    {
        var original = SampleClip() with
        {
            FadeIn = new(10, curve, .92, .15), FadeOut = new(8, curve, .2, .8),
            SeparateAudioFades = true, AudioFadeIn = new(9, FadeCurve.Custom, .13, .91),
            AudioFadeOut = new(11, FadeCurve.EqualPower), VideoFadeMode = VideoFadeMode.Black
        };
        foreach (var offset in new[] { .001, 1.2, 4.5, 6.0, 10.5, 11.999 })
            CheckSplitEnvelope(original, original.Start + offset);
        var first = ClipEditor.Split(original, original.Start + 2, "second");
        CheckSplitEnvelope(first.Right, first.Right.Start + .75);
        var second = ClipEditor.Split(first.Right, first.Right.Start + .75, "third");
        Near(FadeEnvelope.Gain(original, 2.9), FadeEnvelope.Gain(second.Right, .15));
        True(first.Right.VideoFadeMode == VideoFadeMode.Black && first.Right.SeparateAudioFades, "Split retains mode and independent audio");
    }
    var custom = new FadeSettings(1, FadeCurve.Custom, .9, .1);
    Near(0, FadeEnvelope.CurveValue(custom, 0)); Near(1, FadeEnvelope.CurveValue(custom, 1));
    Near(.409375, FadeEnvelope.CurveValue(custom, .25));
    var distinct = SampleClip() with { SeparateAudioFades = true, AudioFadeIn = null, AudioFadeOut = null };
    Near(1, FadeEnvelope.Gain(distinct, 0, audio: true)); Near(0, FadeEnvelope.Gain(distinct, 0));
}

static void InvalidFadeControls()
{
    var project = SampleProject();
    foreach (var fade in new[]
    {
        new FadeSettings(1, FadeCurve.Custom, double.NaN), new FadeSettings(1, Control1: -.01),
        new FadeSettings(1, Control2: 1.01), new FadeSettings(1, RangeStart: .8, RangeEnd: .2),
        new FadeSettings(1, RangeStart: -1), new FadeSettings(1, RangeEnd: double.PositiveInfinity)
    }) Throws<ProjectValidationException>(() => ProjectValidator.Validate(project with { Clips = [SampleClip() with { AudioFadeIn = fade }] }));
    Throws<ProjectValidationException>(() => ProjectValidator.Validate(project with { Clips = [SampleClip() with { VideoFadeMode = (VideoFadeMode)99 }] }));
}

static void LegacyFadeDefaults() => InTempFolder(folder =>
{
    var path = Path.Combine(folder, "old.cutmaker");
    File.WriteAllText(path, """
        {"schemaVersion":1,"title":"Old project","mediaAssets":[{"id":"asset-1","path":"x.mp4","kind":"video","duration":20}],
        "tracks":[{"id":"video-1","name":"Video","kind":"video"}],
        "clips":[{"id":"clip-1","assetId":"asset-1","trackId":"video-1","start":0,"sourceIn":0,"duration":10,
        "fadeIn":{"duration":2,"curve":"easeIn"},"fadeOut":{"duration":3,"curve":"linear"}}]}
        """);
    var clip = ProjectStore.Load(path).Clips[0];
    True(!clip.SeparateAudioFades && clip.VideoFadeMode == VideoFadeMode.Opacity && clip.LinkGroupId is null, "Legacy mode defaults");
    Near(1.0 / 3, clip.FadeIn!.Control1); Near(2.0 / 3, clip.FadeIn.Control2);
    Near(0, clip.FadeIn.RangeStart); Near(1, clip.FadeIn.RangeEnd);
    Near(.25, FadeEnvelope.Gain(clip, 1)); Near(.25, FadeEnvelope.Gain(clip, 1, audio: true));
    var pieces = ClipEditor.Split(clip, 1, "right");
    var project = ProjectStore.Load(path) with { Clips = [pieces.Left, pieces.Right] };
    ProjectStore.Save(path, project);
    True(ProjectStore.Load(path).Clips.SequenceEqual(project.Clips), "Custom segment fields survive save and reopen");
});

static void ImageEdits()
{
    var project = CutProject.CreateEmpty() with { MediaAssets = [new("still", "image.png", MediaKind.Image, 5)], Clips = [new("image", "still", "video-1", 3, 0, 5)] };
    var longer = ClipEditor.TrimEnd(project.Clips[0], 123, 5, isStillImage: true);
    Near(120, longer.Duration); Near(0, longer.SourceIn);
    True(TimelineEditor.CanReplace(project, longer, out _), "Still source duration is a default, not a hard bound");
    var trimmed = ClipEditor.TrimStart(longer, 4, 5, isStillImage: true);
    Near(0, trimmed.SourceIn);
    var split = ClipEditor.Split(trimmed, 10, "image-right", isStillImage: true);
    Near(0, split.Left.SourceIn); Near(0, split.Right.SourceIn);
    ProjectValidator.Validate(project with { Clips = [split.Left, split.Right] });
    var legacy = project.Clips[0] with { SourceIn = 1, Duration = 4 };
    ProjectValidator.Validate(project with { Clips = [legacy] });
    Near(0, ClipEditor.TrimEnd(legacy, legacy.End + 1, 5, isStillImage: true).SourceIn);
}

static void InvalidProjects()
{
    var valid = SampleProject();
    ProjectValidator.Validate(valid);
    foreach (var clip in new[]
    {
        SampleClip() with { AssetId = "missing" },
        SampleClip() with { TrackId = "missing" },
        SampleClip() with { Start = -1 },
        SampleClip() with { Duration = double.PositiveInfinity },
        SampleClip() with { Duration = 100 },
        SampleClip() with { Gain = double.NaN },
        SampleClip() with { FadeIn = new FadeSettings(20) }
    }) Throws<ProjectValidationException>(() => ProjectValidator.Validate(valid with { Clips = [clip] }));
    Throws<ProjectValidationException>(() => ProjectValidator.Validate(valid with { Clips = [SampleClip(), SampleClip()] }));
    Throws<ProjectValidationException>(() => ProjectValidator.Validate(valid with { MediaAssets = [valid.MediaAssets[0], valid.MediaAssets[0]] }));
    Throws<ProjectValidationException>(() => ProjectValidator.Validate(valid with { Tracks = [valid.Tracks[0], valid.Tracks[0]] }));
    Throws<ProjectValidationException>(() => ProjectValidator.Validate(valid with { Video = new VideoSettings(Fps: 0) }));
    Throws<ProjectValidationException>(() => ProjectValidator.Validate(valid with { Tracks = [new("video-1", "Video", TrackKind.Video, Volume: -1)] }));
    Throws<ProjectValidationException>(() => ProjectValidator.Validate(valid with { MediaAssets = [new("asset-1", "x.wav", MediaKind.Audio, 60)] }));
}

static void SaveLoad() => InTempFolder(folder =>
{
    var path = Path.Combine(folder, "project.cutmaker");
    var original = SampleProject();
    ProjectStore.Save(path, original);
    var loaded = ProjectStore.Load(path);
    True(loaded.Title == original.Title && loaded.Clips.Single() == original.Clips.Single(), "Round trip");
    True(loaded.Tracks.SequenceEqual(original.Tracks) && loaded.MediaAssets.SequenceEqual(original.MediaAssets), "Round trip collections");
    True(ProjectStore.ResolveAssetPath(path, loaded.MediaAssets[0]) == Path.GetFullPath(Path.Combine(folder, "media", "sample.mp4")), "Relative media path");
    ProjectStore.Save(path, loaded with { Title = "Replaced" });
    True(ProjectStore.Load(path).Title == "Replaced", "Existing-file replacement");
    True(Directory.GetFiles(folder, "*.tmp").Length == 0, "No temporary files after save");
});

static void InvalidFiles() => InTempFolder(folder =>
{
    var path = Path.Combine(folder, "invalid.cutmaker");
    File.WriteAllText(path, "{invalid");
    Throws<JsonException>(() => ProjectStore.Load(path));
    File.WriteAllText(path, "{}");
    Throws<InvalidDataException>(() => ProjectStore.Load(path));
    File.WriteAllText(path, "{\"schemaVersion\":999}");
    Throws<NotSupportedException>(() => ProjectStore.Load(path));
    File.WriteAllText(path, "{\"schemaVersion\":1,\"title\":\"\"}");
    Throws<ProjectValidationException>(() => ProjectStore.Load(path));
});

static void SafeSave() => InTempFolder(folder =>
{
    var path = Path.Combine(folder, "safe.cutmaker");
    ProjectStore.Save(path, SampleProject());
    var before = File.ReadAllBytes(path);
    Throws<ProjectValidationException>(() => ProjectStore.Save(path, SampleProject() with { Title = "" }));
    True(before.SequenceEqual(File.ReadAllBytes(path)), "Validation failure preserved original bytes");
    using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        if (OperatingSystem.IsWindows())
            Throws<IOException>(() => ProjectStore.Save(path, SampleProject() with { Title = "Cannot replace locked file" }));
    }
    True(before.SequenceEqual(File.ReadAllBytes(path)), "I/O failure preserved original bytes");
    True(Directory.GetFiles(folder, "*.tmp").Length == 0, "Failed-save cleanup");
});

static void ImportCandidates() => InTempFolder(folder =>
{
    var files = new (string Name, MediaKind Kind)[]
    {
        ("影片名稱很長 這是拖曳匯入的測試.MP4", MediaKind.Video),
        ("voice.Mp3", MediaKind.Audio), ("sound.wav", MediaKind.Audio),
        ("透明圖.png", MediaKind.Image), ("photo.JPG", MediaKind.Image), ("photo.JPEG", MediaKind.Image)
    };
    // Deliberately not encoded media: this helper only plans files; the app must probe them.
    var sourceBytes = new byte[] { 1, 2, 3, 4 };
    foreach (var file in files) File.WriteAllBytes(Path.Combine(folder, file.Name), sourceBytes);
    var existing = new List<MediaAsset>();
    var plan = MediaImport.Plan(files.Select(file => file.Name), existing, folder);
    True(plan.Rejections.Count == 0 && plan.Candidates.Count == files.Length, "Supported files accepted");
    for (var index = 0; index < files.Length; index++)
    {
        var candidate = plan.Candidates[index];
        True(candidate.FullPath == Path.Combine(folder, files[index].Name), "Absolute path and input order preserved");
        True(candidate.ExpectedKind == files[index].Kind, "Expected media kind");
        True(File.ReadAllBytes(candidate.FullPath).SequenceEqual(sourceBytes), "Source bytes unchanged");
    }
    True(existing.Count == 0, "Planning does not mutate the project's assets");
    True(!MediaImport.IsSupportedPath(null) && !MediaImport.IsSupportedPath("") &&
        !MediaImport.IsSupportedPath("movie.mp4.exe") && !MediaImport.IsSupportedPath("README"),
        "Only supported final extensions qualify");
});

static void ImportRejections() => InTempFolder(folder =>
{
    Directory.CreateDirectory(Path.Combine(folder, "directory.mp4"));
    File.WriteAllText(Path.Combine(folder, "directory.mp4", "nested.wav"), "not traversed");
    File.WriteAllText(Path.Combine(folder, "notes.txt"), "unsupported");
    File.WriteAllText(Path.Combine(folder, "valid.mp3"), "candidate only");
    var plan = MediaImport.Plan(["", "invalid\0.mp4", "directory.mp4", "missing.wav", "notes.txt", "valid.mp3"], [], folder);
    True(plan.Candidates.Count == 1 && Path.GetFileName(plan.Candidates[0].FullPath) == "valid.mp3",
        "Rejected entries do not stop the batch or recursively import directories");
    True(plan.Rejections.Select(item => item.Reason).SequenceEqual(new[]
    {
        ImportRejectionReason.InvalidPath, ImportRejectionReason.InvalidPath, ImportRejectionReason.Directory,
        ImportRejectionReason.MissingFile, ImportRejectionReason.UnsupportedType
    }), "Each rejection has a useful reason");
    True(plan.Rejections[3].Path == "missing.wav", "Rejection preserves the submitted path");
    True(MediaImport.Plan([], [], folder).Candidates.Count == 0, "An empty drop is harmless");
});

static void ImportDuplicates() => InTempFolder(folder =>
{
    var mediaFolder = Path.Combine(folder, "media");
    Directory.CreateDirectory(mediaFolder);
    var firstPath = Path.Combine(mediaFolder, "first.mp4");
    var secondPath = Path.Combine(mediaFolder, "second.wav");
    File.WriteAllText(firstPath, "first");
    File.WriteAllText(secondPath, "second");
    var existing = new[] { new MediaAsset("existing", Path.Combine("media", "FIRST.MP4"), MediaKind.Video, 12) };
    var paths = new List<string> { firstPath, secondPath, Path.Combine("media", "..", "media", "second.wav") };
    if (OperatingSystem.IsWindows()) paths.Add(secondPath.ToUpperInvariant());
    var plan = MediaImport.Plan(paths, existing, folder);
    True(plan.Candidates.Count == 1 && plan.Candidates[0].FullPath == secondPath, "Only the new file is imported");
    True(plan.Rejections.Count == paths.Count - 1 && plan.Rejections.All(item => item.Reason == ImportRejectionReason.Duplicate),
        "Existing relative paths and normalized batch paths compare without case sensitivity");
    True(existing[0].Path == Path.Combine("media", "FIRST.MP4") && existing[0].Duration == 12,
        "Planning preserves existing asset paths and durations");
});

static CutProject PlacementProject() => CutProject.CreateEmpty("Timeline placement checks") with
{
    MediaAssets =
    [
        new("video", "media/video.mp4", MediaKind.Video, 4.125),
        new("audio", "media/speech.wav", MediaKind.Audio, 9.75),
        new("image", "media/long-name-picture.png", MediaKind.Image, 5)
    ]
};

static void PlacementCompatibility()
{
    var project = PlacementProject();
    foreach (var asset in project.MediaAssets)
    foreach (var track in project.Tracks)
    {
        var expected = track.Kind == TrackKind.Audio ? asset.Kind == MediaKind.Audio : asset.Kind != MediaKind.Audio;
        var allowed = TimelinePlacement.CanPlace(project, asset.Id, track.Id, 0, out var reason);
        True(allowed == expected, $"Placement compatibility for {asset.Kind} on {track.Kind}");
        True(allowed == string.IsNullOrEmpty(reason), "Rejected placements explain the cause");
        if (allowed)
        {
            var clip = TimelinePlacement.CreateClip(project, asset.Id, track.Id, 0);
            Near(0, clip.Start); Near(0, clip.SourceIn); Near(asset.Duration, clip.Duration);
            True(clip.AssetId == asset.Id && clip.TrackId == track.Id, "Original media and selected track are referenced");
        }
        else Throws<ProjectValidationException>(() => TimelinePlacement.CreateClip(project, asset.Id, track.Id, 0));
    }
    True(project.Clips.Count == 0, "Planning and creating candidates do not change the project");
}

static void PlacementOverlap()
{
    var project = PlacementProject();
    var existing = TimelinePlacement.CreateClip(project, "image", "video-1", 10);
    project.Clips.Add(existing); // Occupies [10, 15).
    var before = JsonSerializer.Serialize(project);
    foreach (var start in new[] { 5.001, 9.0, 10.0, 12.0, 14.999 })
    {
        True(!TimelinePlacement.CanPlace(project, "image", "video-1", start, out var reason), "Every positive overlap is rejected");
        True(reason.Contains("重疊"), "Overlap receives a useful reason");
        Throws<ProjectValidationException>(() => TimelinePlacement.CreateClip(project, "image", "video-1", start));
    }
    foreach (var start in new[] { 0.0, 5.0, 15.0, 20.0 })
        TimelinePlacement.CreateClip(project, "image", "video-1", start);
    True(before == JsonSerializer.Serialize(project), "Accepted and rejected candidates preserve every existing value");

    project.Tracks.Add(new("video-2", "Video 2", TrackKind.Video));
    TimelinePlacement.CreateClip(project, "image", "video-2", 10);
    TimelinePlacement.CreateClip(project, "audio", "audio-1", 10);
    var covering = project with { MediaAssets = [new("long-video", "long.mp4", MediaKind.Video, 30)] };
    True(!TimelinePlacement.CanPlace(covering, "long-video", "video-1", 0, out _),
        "A new clip enclosing an existing clip is rejected too");
}

static void PlacementRejections()
{
    var project = PlacementProject();
    foreach (var start in new[] { -1.0, double.NaN, double.PositiveInfinity, double.NegativeInfinity, double.MaxValue })
        Throws<ProjectValidationException>(() => TimelinePlacement.CreateClip(project, "video", "video-1", start));
    foreach (var duration in new[] { 0.0, -1.0, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
    {
        var invalid = project with { MediaAssets = [project.MediaAssets[0] with { Duration = duration }] };
        True(!TimelinePlacement.CanPlace(invalid, "video", "video-1", 0, out var reason) && reason.Length > 0,
            "Invalid source durations cannot enter the timeline");
    }
    var overflow = project with { MediaAssets = [project.MediaAssets[0] with { Duration = double.MaxValue }] };
    Throws<ProjectValidationException>(() => TimelinePlacement.CreateClip(overflow, "video", "video-1", double.MaxValue));
    foreach (var references in new[] { ("missing", "video-1"), ("video", "missing"), ("", "video-1"), ("video", "") })
        Throws<ProjectValidationException>(() => TimelinePlacement.CreateClip(project, references.Item1, references.Item2, 0));
    var locked = project with { Tracks = [project.Tracks[0] with { Locked = true }] };
    True(!TimelinePlacement.CanPlace(locked, "video", "video-1", 0, out var lockedReason) && lockedReason.Contains("鎖定"),
        "Locked tracks explain why the drop is rejected");
    Throws<ProjectValidationException>(() => TimelinePlacement.CreateClip(locked, "video", "video-1", 0));
    var muted = project with { Tracks = [project.Tracks[0] with { Muted = true }] };
    TimelinePlacement.CreateClip(muted, "video", "video-1", 0);
    True(project.Clips.Count == 0 && project.Tracks.All(track => !track.Locked && !track.Muted),
        "Rejected placement and track variants leave original project unchanged");
}

static void PlacementRoundTrip() => InTempFolder(folder =>
{
    var project = PlacementProject();
    var sourcesBefore = project.MediaAssets.ToArray();
    var first = TimelinePlacement.CreateClip(project, "video", "video-1", 0);
    project.Clips.Add(first);
    var second = TimelinePlacement.CreateClip(project, "video", "video-1", first.End);
    True(project.Clips.Count == 1, "Creating a repeat candidate does not add it implicitly");
    project.Clips.Add(second);
    project.Clips.Add(TimelinePlacement.CreateClip(project, "audio", "audio-1", 0));
    True(first.Id != second.Id && first.AssetId == second.AssetId, "Repeated source placements have independent clip IDs");
    Near(first.Duration, second.Duration); Near(0, second.SourceIn); Near(first.End, second.Start);
    var path = Path.Combine(folder, "timeline.cutmaker");
    ProjectStore.Save(path, project);
    var reopened = ProjectStore.Load(path);
    True(reopened.Clips.SequenceEqual(project.Clips) && reopened.Tracks.SequenceEqual(project.Tracks),
        "Saved positions, durations, IDs and track assignments survive reopening");
    True(reopened.MediaAssets.SequenceEqual(sourcesBefore), "Repeated placements never duplicate or alter source assets");
    True(!TimelinePlacement.CanPlace(reopened, "video", "video-1", 0, out _), "Reopened clips still protect their occupied range");
    var third = TimelinePlacement.CreateClip(reopened, "video", "video-1", second.End);
    True(reopened.Clips.All(clip => clip.Id != third.Id), "New IDs stay independent after reopen");
});

static void PlacementFrames()
{
    Near(0, TimelinePlacement.RoundToFrame(0, 30));
    Near(1.0 / 30, TimelinePlacement.RoundToFrame(0.02, 30));
    Near(1, TimelinePlacement.RoundToFrame(1.001, 30));
    Near(1.5, TimelinePlacement.RoundToFrame(1.25, 2));
    var fractionalFps = 30000.0 / 1001;
    Near(30 / fractionalFps, TimelinePlacement.RoundToFrame(1, fractionalFps));
    foreach (var fps in new[] { 0.0, -1.0, 241.0, double.NaN, double.PositiveInfinity })
        Throws<ProjectValidationException>(() => TimelinePlacement.RoundToFrame(1, fps));
    foreach (var start in new[] { -0.01, double.NaN, double.PositiveInfinity, double.MaxValue })
        Throws<ProjectValidationException>(() => TimelinePlacement.RoundToFrame(start, 30));
    var project = PlacementProject();
    var clip = TimelinePlacement.CreateClip(project, "video", "video-1", TimelinePlacement.RoundToFrame(1.02, 30));
    Near(31.0 / 30, clip.Start); Near(4.125, clip.Duration);
}

static void EditingMoves()
{
    var project = SampleProject();
    project.Tracks.Add(new("video-2", "Second video", TrackKind.Video));
    var original = project.Clips[0];
    var moved = TimelineEditor.Move(project, original.Id, "video-2", 1.5);
    True(moved.SourceIn == original.SourceIn && moved.Duration == original.Duration && moved.FadeIn == original.FadeIn &&
        moved.FadeOut == original.FadeOut && moved.Gain == original.Gain, "Move preserves source and effects");
    True(project.Clips[0] == original, "Move planning is pure");
    True(TimelineEditor.CanReplace(project, original, out _), "Unchanged clip does not overlap itself");
    True(TimelineEditor.Move(project, original.Id, "audio-1", 0).TrackId == "audio-1", "Video source may supply an extracted audio track");
    Throws<ProjectValidationException>(() => TimelineEditor.Move(project, original.Id, "missing", 0));
    Throws<ProjectValidationException>(() => TimelineEditor.Move(project, original.Id, "video-2", -1));
    project.Tracks[0] = project.Tracks[0] with { Locked = true };
    Throws<ProjectValidationException>(() => TimelineEditor.Move(project, original.Id, "video-2", 0));
    project.Tracks[0] = project.Tracks[0] with { Locked = false };
    project.Tracks[2] = project.Tracks[2] with { Locked = true };
    Throws<ProjectValidationException>(() => TimelineEditor.Move(project, original.Id, "video-2", 0));
}

static void EditingBounds()
{
    var project = SampleProject();
    var original = project.Clips[0];
    project.Clips.Add(original with { Id = "next", Start = original.End + 1 });
    var touching = ClipEditor.TrimEnd(original, original.End + 1, 60);
    True(TimelineEditor.CanReplace(project, touching, out _), "Trim may touch next clip");
    var overlap = ClipEditor.TrimEnd(original, original.End + 1.01, 60);
    True(!TimelineEditor.CanReplace(project, overlap, out var reason) && reason.Contains("重疊"), "Trim cannot overwrite next clip");
    foreach (var candidate in new[]
    {
        original with { Duration = 61 }, original with { Gain = 4.1 }, original with { SourceIn = -1 },
        original with { FadeIn = new(99) }, original with { FadeOut = new(1, (FadeCurve)99) },
        original with { Start = double.NaN }, original with { AssetId = "another" }
    }) True(!TimelineEditor.CanReplace(project, candidate, out _), "Invalid inspector edits are rejected without mutation");
    True(project.Clips[0] == original, "Rejected edits preserve original");
}

static void InTempFolder(Action<string> action)
{
    var folder = Path.Combine(Path.GetTempPath(), "CutMaker.Core.Checks", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(folder);
    try { action(folder); }
    finally { Directory.Delete(folder, recursive: true); }
}

static void Near(double expected, double actual)
{
    if (Math.Abs(expected - actual) > 0.000001) throw new Exception($"Expected {expected}, got {actual}.");
}
static void True(bool value, string message)
{
    if (!value) throw new Exception(message);
}
static void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}.");
}
