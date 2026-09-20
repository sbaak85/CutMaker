using CutMaker.App;
using CutMaker.Core;

internal static class PreviewRenderCacheChecks
{
    internal static void Run(string folder, Action<bool, string> check)
    {
        Directory.CreateDirectory(folder);
        var source = Path.Combine(folder, "source-kept.bin");
        File.WriteAllBytes(source, [1, 2, 3, 4]);
        var project = new CutProject
        {
            MediaAssets = [new("asset", source, MediaKind.Audio, 300)],
            Tracks = [new("audio", "Audio", TrackKind.Audio)],
            Clips = [new("clip", "asset", "audio", .012345, 1.54321, 251.4)]
        };
        var originalKey = PreviewRenderCache.CreateKey(project);
        var renamed = project with
        {
            Title = "Different title",
            MediaAssets = [project.MediaAssets[0] with { Id = "renamed-asset" }, new("unused", Path.Combine(folder, "missing-unused.wav"), MediaKind.Audio, 2)],
            Tracks = [new("empty", "Unused video lane", TrackKind.Video), project.Tracks[0] with { Id = "renamed-track", Name = "New name", Locked = true }],
            Clips = [project.Clips[0] with { Id = "renamed-clip", AssetId = "renamed-asset", TrackId = "renamed-track", LinkGroupId = "link" }]
        };
        check(PreviewRenderCache.CreateKey(renamed) == originalKey, "preview cache ignores names, locks, IDs, links, empty tracks and unused library assets");
        var split = ClipEditor.Split(project.Clips[0], 23.1234567, "right");
        var cutProject = project with { Clips = [split.Left, split.Right] };
        var secondSplit = ClipEditor.Split(split.Right, 85.987654, "last");
        cutProject = cutProject with { Clips = [split.Left, secondSplit.Left, secondSplit.Right] };
        check(PreviewRenderCache.CreateKey(cutProject) == originalKey, "preview cache reuses identical flat audio after non-frame-aligned razor cuts");
        check(PreviewRenderCache.IsAudioOnly(project) && PreviewRenderCache.IsAudioOnly(renamed), "empty video lanes do not force black-video rendering for audio projects");
        check(PreviewRenderCache.CreateKey(project with { Tracks = [project.Tracks[0] with { Muted = true }] }) != originalKey &&
            PreviewRenderCache.CreateKey(project with { Tracks = [project.Tracks[0] with { Volume = .5 }] }) != originalKey &&
            PreviewRenderCache.CreateKey(project with { Clips = [project.Clips[0] with { Gain = .5 }] }) != originalKey &&
            PreviewRenderCache.CreateKey(project with { Clips = [project.Clips[0] with { SourceIn = 2 }] }) != originalKey &&
            PreviewRenderCache.CreateKey(project with { Clips = [project.Clips[0] with { SourceAudioMuted = true }] }) != originalKey,
            "preview cache invalidates actual mute, volume, source trim and source-audio changes");
        check(PreviewRenderCache.CreateKey(project with { Clips = [project.Clips[0] with { FadeIn = new(2, FadeCurve.EaseIn) }] }) != originalKey &&
            PreviewRenderCache.CreateKey(project with { Clips = [project.Clips[0] with { SeparateAudioFades = true, AudioFadeIn = new(2, FadeCurve.Custom, .7, .3) }] }) != originalKey,
            "preview cache invalidates shared and independent audio fade changes");
        var mutedTail = project with
        {
            Tracks = [.. project.Tracks, new("muted", "Muted", TrackKind.Audio, Muted: true)],
            Clips = [.. project.Clips, new("tail", "asset", "muted", 260, 0, 3)]
        };
        var mutedTailKey = PreviewRenderCache.CreateKey(mutedTail);
        check(mutedTailKey != originalKey && PreviewRenderCache.CreateKey(mutedTail with
            { Clips = [project.Clips[0], mutedTail.Clips[^1] with { Gain = .2, SourceIn = 4 }] }) == mutedTailKey,
            "muted tails preserve timeline duration while inaudible edits retain preview cache");
        var sourceStamp = File.GetLastWriteTimeUtc(source);
        File.SetLastWriteTimeUtc(source, sourceStamp.AddSeconds(2));
        check(PreviewRenderCache.CreateKey(project) != originalKey, "source file replacement timestamp invalidates prepared preview");
        File.SetLastWriteTimeUtc(source, sourceStamp);
        File.AppendAllText(source, "new source bytes");
        check(PreviewRenderCache.CreateKey(project) != originalKey, "source file size change invalidates prepared preview");

        var videoProject = project with
        {
            MediaAssets = [.. project.MediaAssets, new("video", source, MediaKind.Video, 300)],
            Tracks = [new("front", "Front", TrackKind.Video), new("back", "Back", TrackKind.Video), .. project.Tracks],
            Clips = [.. project.Clips, new("front-clip", "video", "front", 0, 0, 1), new("back-clip", "video", "back", 0, 1, 1)]
        };
        var videoKey = PreviewRenderCache.CreateKey(videoProject);
        check(!PreviewRenderCache.IsAudioOnly(videoProject) && videoKey != PreviewRenderCache.CreateKey(videoProject with
            { Tracks = [videoProject.Tracks[1], videoProject.Tracks[0], videoProject.Tracks[2]] }) &&
            videoKey != PreviewRenderCache.CreateKey(videoProject with { Video = new(1280, 720, 25) }),
            "visible video track order and project video settings invalidate prepared preview");

        var cacheFolder = Path.Combine(folder, "session");
        Directory.CreateDirectory(cacheFolder);
        using var cache = new PreviewRenderCache(cacheFolder, maxEntries: 2, maxBytes: 12);
        string Output(string name, int bytes = 4)
        {
            var path = Path.Combine(cacheFolder, name + ".wav");
            File.WriteAllBytes(path, new byte[bytes]);
            return path;
        }
        var first = Output("first");
        var second = Output("second");
        cache.Add("first", first); cache.Add("second", second);
        check(cache.TryGet("first", out var reused) && reused == first && cache.ContainsFile(first), "prepared preview lookup reuses the existing file");
        var third = Output("third"); cache.Add("third", third);
        check(!cache.TryGet("second", out _) && !File.Exists(second) && cache.TryGet("first", out _), "prepared preview eviction respects least-recent use");
        var large = Output("large", 20); cache.Add("large", large);
        check(!File.Exists(first) && !File.Exists(third) && cache.TryGet("large", out _), "byte cap evicts old previews while retaining one long current project");
        File.WriteAllBytes(large, [9]);
        check(!cache.TryGet("large", out _), "truncated cached preview is never reused");
        var outsideRejected = false;
        try { cache.Add("source", source); }
        catch (ArgumentException) { outsideRejected = true; }
        check(outsideRejected && File.Exists(source), "preview cache refuses files outside its session and preserves source media");
        var final = Output("final"); cache.Add("final", final);
        cache.RemoveFile(final);
        check(!cache.ContainsFile(final) && !File.Exists(final), "failed playback can remove its cached output before retry");
        var cleared = Output("cleared"); cache.Add("cleared", cleared); cache.Clear();
        check(!File.Exists(cleared) && File.Exists(source), "session cache cleanup removes owned previews only");
    }
}
