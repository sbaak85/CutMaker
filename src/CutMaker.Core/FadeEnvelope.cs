using System.Globalization;

namespace CutMaker.Core;

/// <summary>Shared, deterministic fade envelopes for editing, preview and final export.</summary>
public static class FadeEnvelope
{
    public static double CurveValue(FadeSettings fade, double normalized)
    {
        var p = fade.RangeStart + (fade.RangeEnd - fade.RangeStart) * Math.Clamp(normalized, 0, 1);
        return fade.Curve switch
        {
            FadeCurve.EaseIn => p * p,
            FadeCurve.EaseOut => 1 - (1 - p) * (1 - p),
            FadeCurve.SmoothStep => p * p * (3 - 2 * p),
            FadeCurve.EqualPower => Math.Sin(p * Math.PI / 2),
            FadeCurve.Custom => 3 * (1 - p) * (1 - p) * p * fade.Control1 +
                3 * (1 - p) * p * p * fade.Control2 + p * p * p,
            _ => p
        };
    }

    public static double Gain(Clip clip, double localSeconds, bool audio = false)
    {
        var fadeIn = audio && clip.SeparateAudioFades ? clip.AudioFadeIn : clip.FadeIn;
        var fadeOut = audio && clip.SeparateAudioFades ? clip.AudioFadeOut : clip.FadeOut;
        return Factor(fadeIn, localSeconds) * Factor(fadeOut, clip.Duration - localSeconds);
    }

    private static double Factor(FadeSettings? fade, double remaining) =>
        fade is { Duration: > 0 } ? CurveValue(fade, remaining / fade.Duration) : 1;

    /// <summary>FFmpeg expression; caller embeds it inside a single-quoted filter expression.</summary>
    public static string Expression(FadeSettings? fade, string normalized)
    {
        if (fade is not { Duration: > 0 }) return "1";
        var clamped = $"clip(({normalized}),0,1)";
        var p = fade.RangeStart == 0 && fade.RangeEnd == 1 ? clamped
            : $"({N(fade.RangeStart)}+{N(fade.RangeEnd - fade.RangeStart)}*({clamped}))";
        return fade.Curve switch
        {
            FadeCurve.EaseIn => $"pow({p},2)",
            FadeCurve.EaseOut => $"(1-pow(1-({p}),2))",
            FadeCurve.SmoothStep => $"(pow({p},2)*(3-2*({p})))",
            FadeCurve.EqualPower => $"sin(({p})*PI/2)",
            FadeCurve.Custom => $"(3*pow(1-({p}),2)*({p})*{N(fade.Control1)}+3*(1-({p}))*pow({p},2)*{N(fade.Control2)}+pow({p},3))",
            _ => p
        };
    }

    public static string GainExpression(Clip clip, string localTime, bool audio = false)
    {
        var fadeIn = audio && clip.SeparateAudioFades ? clip.AudioFadeIn : clip.FadeIn;
        var fadeOut = audio && clip.SeparateAudioFades ? clip.AudioFadeOut : clip.FadeOut;
        var factors = new List<string>();
        if (fadeIn is { Duration: > 0 }) factors.Add(Expression(fadeIn, $"({localTime})/{N(fadeIn.Duration)}"));
        if (fadeOut is { Duration: > 0 }) factors.Add(Expression(fadeOut, $"({N(clip.Duration)}-({localTime}))/{N(fadeOut.Duration)}"));
        return factors.Count == 0 ? "1" : string.Join("*", factors.Select(factor => $"({factor})"));
    }

    internal static FadeSettings? SliceIn(FadeSettings? fade, double offset, double duration)
    {
        if (fade is not { Duration: > 0 }) return null;
        if (offset >= fade.Duration)
            return fade.RangeEnd == 1 ? null : fade with { Duration = duration, RangeStart = fade.RangeEnd };
        var end = Math.Min(fade.Duration, offset + duration);
        return fade with
        {
            Duration = end - offset,
            RangeStart = Map(fade, offset / fade.Duration),
            RangeEnd = Map(fade, end / fade.Duration)
        };
    }

    internal static FadeSettings? SliceOut(FadeSettings? fade, double originalDuration, double offset, double duration)
    {
        if (fade is not { Duration: > 0 }) return null;
        var end = offset + duration;
        var start = Math.Max(offset, originalDuration - fade.Duration);
        if (end <= start)
            return fade.RangeEnd == 1 ? null : fade with { Duration = duration, RangeStart = fade.RangeEnd };
        return fade with
        {
            Duration = end - start,
            RangeStart = Map(fade, (originalDuration - end) / fade.Duration),
            RangeEnd = Map(fade, (originalDuration - start) / fade.Duration)
        };
    }

    private static double Map(FadeSettings fade, double p) =>
        fade.RangeStart + (fade.RangeEnd - fade.RangeStart) * Math.Clamp(p, 0, 1);
    private static string N(double value) => value.ToString("G17", CultureInfo.InvariantCulture);
}
