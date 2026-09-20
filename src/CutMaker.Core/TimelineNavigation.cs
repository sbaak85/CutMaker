using System.Globalization;

namespace CutMaker.Core;

/// <summary>Read-only playhead math, independent of rendering and edit history.</summary>
public static class TimelineNavigation
{
    /// <summary>Moves to the adjacent frame boundary, including when the current time is between frames.</summary>
    public static double StepFrame(double seconds, int direction, double fps, double duration)
    {
        ValidateTime(seconds, duration);
        ProjectValidator.Require(direction is -1 or 1, "Frame direction must be -1 or 1.");
        ProjectValidator.Require(double.IsFinite(fps) && fps > 0 && fps <= 240, "Invalid navigation frame rate.");
        var frame = Math.Clamp(seconds, 0, duration) * fps;
        ProjectValidator.Require(double.IsFinite(frame), "Frame position is out of range.");
        // Account for binary floating-point error at an exact frame; do not round arbitrary seeks first.
        const double frameTolerance = 0.0000001;
        var targetFrame = direction > 0 ? Math.Floor(frame + frameTolerance) + 1 : Math.Ceiling(frame - frameTolerance) - 1;
        return Math.Clamp(targetFrame / fps, 0, duration);
    }

    public static double AdjacentBoundary(IEnumerable<Clip> clips, double seconds, int direction, double duration)
    {
        ArgumentNullException.ThrowIfNull(clips);
        ValidateTime(seconds, duration);
        ProjectValidator.Require(direction is -1 or 1, "Boundary direction must be -1 or 1.");
        seconds = Math.Clamp(seconds, 0, duration);
        var boundaries = clips.SelectMany(clip => new[] { clip.Start, clip.End }).Prepend(0).Append(duration)
            .Where(time => double.IsFinite(time) && time >= 0 && time <= duration);
        return direction > 0
            ? boundaries.Where(time => time > seconds + ProjectValidator.TimeTolerance).DefaultIfEmpty(duration).Min()
            : boundaries.Where(time => time < seconds - ProjectValidator.TimeTolerance).DefaultIfEmpty(0).Max();
    }

    /// <summary>Accepts seconds, mm:ss.fff or hh:mm:ss.fff without culture-specific ambiguity.</summary>
    public static bool TryParseTime(string? text, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Trim().Split(':');
        if (parts.Length == 1)
            return double.TryParse(parts[0], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out seconds)
                && double.IsFinite(seconds) && seconds >= 0;
        if (parts.Length is not (2 or 3)) return false;
        if (!double.TryParse(parts[^1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var tail) ||
            !double.IsFinite(tail) || tail is < 0 or >= 60) return false;
        if (!long.TryParse(parts[^2], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) || minutes < 0 ||
            (parts.Length == 3 && minutes >= 60)) return false;
        var hours = 0L;
        if (parts.Length == 3 && (!long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out hours) || hours < 0)) return false;
        seconds = hours * 3600.0 + minutes * 60.0 + tail;
        return double.IsFinite(seconds);
    }

    private static void ValidateTime(double seconds, double duration)
    {
        ProjectValidator.Require(double.IsFinite(seconds) && seconds >= 0, "Navigation time must be finite and non-negative.");
        ProjectValidator.Require(double.IsFinite(duration) && duration >= 0, "Timeline duration must be finite and non-negative.");
    }
}
