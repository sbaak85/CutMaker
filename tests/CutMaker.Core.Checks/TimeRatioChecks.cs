using System.Text.Json;
using CutMaker.Core;

internal static class TimeRatioChecks
{
    internal static void Run()
    {
        void Check(bool ok) { if (!ok) throw new Exception("Time ratio invariant failed."); }
        void Near(double a, double b) => Check(Math.Abs(a-b) < 1e-8);
        var original = new Clip("c", "a", "t", 2, 3, 10, FadeIn: new(2), FadeOut: new(1));
        var shortClip = ClipEditor.ChangeTimeRatio(original, .8);
        Near(shortClip.Duration, 8); Near(shortClip.SourceEnd, 13); Near(shortClip.FadeIn!.Duration, 1.6);
        var longClip = ClipEditor.ChangeTimeRatio(shortClip, 1.2);
        Near(longClip.Duration, 12); Near(longClip.SourceEnd, 13);
        var trim = ClipEditor.TrimStart(shortClip, 4, 20);
        Near(trim.SourceIn, 5.5); Near(trim.End, shortClip.End);
        var tail = ClipEditor.TrimEnd(shortClip, 12, 20); Near(tail.SourceEnd, 15.5);
        var (left, right) = ClipEditor.Split(shortClip, 5.123456789, "r");
        Near(left.SourceEnd, right.SourceIn); Near(right.SourceEnd, shortClip.SourceEnd);
        var ls = AudioSampleClock.Slice(left, 0, 20); var rs = AudioSampleClock.Slice(right, 0, 20);
        Check(ls.SourceStart + ls.Length == rs.SourceStart);
        var project = new CutProject { MediaAssets = [new("a", "a.wav", MediaKind.Audio, 20)], Tracks = [new("t", "T", TrackKind.Audio)],
            Clips = [shortClip], PlaybackRange = new(3, 7, Loop: true) };
        ProjectValidator.Validate(project);
        var restored = JsonSerializer.Deserialize<CutProject>(JsonSerializer.Serialize(project))!;
        Check(restored.Clips[0] == shortClip && restored.PlaybackRange == project.PlaybackRange);
        var legacy = JsonSerializer.Deserialize<Clip>("{\"Id\":\"x\",\"AssetId\":\"a\",\"TrackId\":\"t\",\"Start\":0,\"SourceIn\":0,\"Duration\":1}")!;
        Near(legacy.TimeRatio, 1);
        foreach (var bad in new[] { 0, double.NaN, .2, 5 })
        {
            try { ClipEditor.ChangeTimeRatio(original, bad); throw new Exception("Invalid ratio accepted"); }
            catch (ProjectValidationException) { }
        }
        try { ProjectValidator.Validate(project with { PlaybackRange = new(4, 3) }); throw new Exception("Invalid range accepted"); }
        catch (ProjectValidationException) { }
    }
}
