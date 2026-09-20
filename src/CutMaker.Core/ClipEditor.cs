namespace CutMaker.Core;

public static class ClipEditor
{
    /// <summary>Moves the left edge while preserving the right edge and source/timeline synchronization.</summary>
    public static Clip TrimStart(Clip clip, double newTimelineStart, double assetDuration)
    {
        ProjectValidator.ValidateClip(clip, assetDuration);
        var delta = newTimelineStart - clip.Start;
        var result = ClampFades(clip with
        {
            Start = newTimelineStart,
            SourceIn = clip.SourceIn + delta,
            Duration = clip.Duration - delta
        });
        ProjectValidator.ValidateClip(result, assetDuration);
        return result;
    }

    /// <summary>Moves the right edge; restoring a previous trim is allowed up to the original source end.</summary>
    public static Clip TrimEnd(Clip clip, double newTimelineEnd, double assetDuration)
    {
        ProjectValidator.ValidateClip(clip, assetDuration);
        var result = ClampFades(clip with { Duration = newTimelineEnd - clip.Start });
        ProjectValidator.ValidateClip(result, assetDuration);
        return result;
    }

    /// <summary>
    /// Splits strictly inside the clip and outside active fade ranges. Fade boundaries are valid cut points.
    /// Outer fades remain unchanged; interior cut edges have no fade. A cut inside a fade is rejected
    /// until the caller removes or shortens that fade, because partial fade envelopes are not yet modeled.
    /// </summary>
    public static (Clip Left, Clip Right) Split(Clip clip, double timelinePosition, string newRightClipId)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ProjectValidator.ValidateClip(clip, clip.SourceEnd);
        ProjectValidator.Require(!string.IsNullOrWhiteSpace(newRightClipId) && newRightClipId != clip.Id,
            "The right clip must have a new non-empty ID.");
        ProjectValidator.Require(double.IsFinite(timelinePosition) && timelinePosition > clip.Start && timelinePosition < clip.End,
            "The split must be strictly inside the clip.");
        var fadeInEnd = clip.Start + (clip.FadeIn?.Duration ?? 0);
        var fadeOutStart = clip.End - (clip.FadeOut?.Duration ?? 0);
        ProjectValidator.Require(timelinePosition >= fadeInEnd && timelinePosition <= fadeOutStart,
            "Cannot split inside an active Fade. Remove or shorten the Fade first, or cut at its boundary.");
        var leftDuration = timelinePosition - clip.Start;
        var left = ClampFades(clip with { Duration = leftDuration, FadeOut = null });
        var right = ClampFades(clip with
        {
            Id = newRightClipId,
            Start = timelinePosition,
            SourceIn = clip.SourceIn + leftDuration,
            Duration = clip.Duration - leftDuration,
            FadeIn = null
        });
        ProjectValidator.ValidateClip(left, clip.SourceEnd);
        ProjectValidator.ValidateClip(right, clip.SourceEnd);
        return (left, right);
    }

    private static Clip ClampFades(Clip clip) => clip with
    {
        FadeIn = ClampFade(clip.FadeIn, clip.Duration),
        FadeOut = ClampFade(clip.FadeOut, clip.Duration)
    };

    private static FadeSettings? ClampFade(FadeSettings? fade, double duration) =>
        fade is null ? null : fade with { Duration = Math.Min(fade.Duration, Math.Max(0, duration)) };
}
