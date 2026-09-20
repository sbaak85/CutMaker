namespace CutMaker.Core;

/// <summary>
/// A single sample clock for preview and export. Quantize shared timeline boundaries, never
/// individual durations: the end sample of one razor fragment is the start sample of the next.
/// </summary>
public static class AudioSampleClock
{
    public const int Rate = 48000;

    public static long At(double seconds)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(double.IsFinite(seconds), true, nameof(seconds));
        // Samples describe [start,end): the first retained sample must be at or after start.
        // Rounding to nearest could give a right fragment a sample BEFORE its start, where a
        // sliced fade is correctly clamped but differs from the unsliced curve. Remove only
        // sub-millionth-sample floating point noise before taking this shared boundary.
        return checked((long)Math.Ceiling(Math.Round(seconds * Rate, 6)));
    }

    public static AudioSampleSlice Slice(Clip clip, double rangeStart, double rangeEnd)
    {
        var first = Math.Max(At(clip.Start), At(rangeStart));
        var end = Math.Min(At(clip.End), At(rangeEnd));
        // SourceIn - Start is invariant under a razor cut. Rounding each in-point separately
        // would occasionally shift the right fragment by one sample.
        var mapping = checked((long)Math.Round(Math.Round((clip.SourceIn - clip.Start) * Rate, 6), MidpointRounding.AwayFromZero));
        var sourceFirst = checked(first + mapping);
        return new(first, sourceFirst, Math.Max(0, end - first), Math.Max(0, first - At(rangeStart)));
    }
}

public sealed record AudioSampleSlice(long TimelineStart, long SourceStart, long Length, long OutputStart);
