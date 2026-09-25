namespace CutMaker.Core;

public static class ClipEditor
{
    /// <summary>Moves the left edge, retaining source synchronization. Fades stay attached to the new edges and shorten to fit.</summary>
    public static Clip TrimStart(Clip clip, double newTimelineStart, double assetDuration, bool isStillImage = false)
    {
        ProjectValidator.ValidateClip(clip, assetDuration, isStillImage);
        var delta = newTimelineStart - clip.Start;
        var result = ClampFades(clip with
        {
            Start = newTimelineStart,
            SourceIn = isStillImage ? 0 : clip.SourceIn + delta / clip.TimeRatio,
            Duration = clip.Duration - delta
        });
        ProjectValidator.ValidateClip(result, assetDuration, isStillImage);
        return result;
    }

    /// <summary>Moves the right edge up to the source end; stills may extend freely. Fades stay at the new edges.</summary>
    public static Clip TrimEnd(Clip clip, double newTimelineEnd, double assetDuration, bool isStillImage = false)
    {
        ProjectValidator.ValidateClip(clip, assetDuration, isStillImage);
        var result = ClampFades(clip with { Duration = newTimelineEnd - clip.Start, SourceIn = isStillImage ? 0 : clip.SourceIn });
        ProjectValidator.ValidateClip(result, assetDuration, isStillImage);
        return result;
    }

    /// <summary>
    /// Splits strictly inside the clip. Every retained video/audio envelope sample is preserved,
    /// including cuts inside custom or overlapping fades. Source files are never changed.
    /// </summary>
    public static (Clip Left, Clip Right) Split(Clip clip, double timelinePosition, string newRightClipId, bool isStillImage = false)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ProjectValidator.ValidateClip(clip, clip.SourceEnd, isStillImage);
        ProjectValidator.Require(!string.IsNullOrWhiteSpace(newRightClipId) && newRightClipId != clip.Id,
            "The right clip must have a new non-empty ID.");
        ProjectValidator.Require(double.IsFinite(timelinePosition) && timelinePosition > clip.Start && timelinePosition < clip.End,
            "The split must be strictly inside the clip.");
        var leftDuration = timelinePosition - clip.Start;
        var left = Slice(clip, 0, leftDuration, clip.Id, isStillImage);
        var right = Slice(clip, leftDuration, clip.Duration - leftDuration, newRightClipId, isStillImage);
        ProjectValidator.ValidateClip(left, clip.SourceEnd, isStillImage);
        ProjectValidator.ValidateClip(right, clip.SourceEnd, isStillImage);
        return (left, right);
    }

    /// <summary>Retain the same source interval, scaling timeline duration and fade times.</summary>
    public static Clip ChangeTimeRatio(Clip clip, double ratio)
    {
        ProjectValidator.Require(double.IsFinite(ratio) && ratio is >= .25 and <= 4, "時間比例須介於 25% 與 400%。");
        var scale = ratio / clip.TimeRatio;
        FadeSettings? Scale(FadeSettings? fade) => fade is null ? null : fade with { Duration = fade.Duration * scale };
        return clip with { TimeRatio = ratio, Duration = clip.Duration * scale,
            FadeIn = Scale(clip.FadeIn), FadeOut = Scale(clip.FadeOut),
            AudioFadeIn = Scale(clip.AudioFadeIn), AudioFadeOut = Scale(clip.AudioFadeOut) };
    }

    private static Clip Slice(Clip clip, double offset, double duration, string id, bool isStillImage) => clip with
    {
        Id = id, Start = clip.Start + offset, SourceIn = isStillImage ? 0 : clip.SourceIn + offset / clip.TimeRatio, Duration = duration,
        FadeIn = FadeEnvelope.SliceIn(clip.FadeIn, offset, duration),
        FadeOut = FadeEnvelope.SliceOut(clip.FadeOut, clip.Duration, offset, duration),
        AudioFadeIn = FadeEnvelope.SliceIn(clip.AudioFadeIn, offset, duration),
        AudioFadeOut = FadeEnvelope.SliceOut(clip.AudioFadeOut, clip.Duration, offset, duration)
    };

    private static Clip ClampFades(Clip clip) => clip with
    {
        FadeIn = ClampFade(clip.FadeIn, clip.Duration),
        FadeOut = ClampFade(clip.FadeOut, clip.Duration),
        AudioFadeIn = ClampFade(clip.AudioFadeIn, clip.Duration),
        AudioFadeOut = ClampFade(clip.AudioFadeOut, clip.Duration)
    };

    private static FadeSettings? ClampFade(FadeSettings? fade, double duration) =>
        fade is null ? null : fade with { Duration = Math.Min(fade.Duration, Math.Max(0, duration)) };
}
