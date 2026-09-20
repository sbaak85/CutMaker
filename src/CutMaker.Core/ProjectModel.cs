namespace CutMaker.Core;

public enum MediaKind { Video, Audio, Image }
public enum TrackKind { Video, Audio }
public enum FadeCurve { Linear, EaseIn, EaseOut, SmoothStep, EqualPower, Custom }
public enum VideoFadeMode { Opacity, Black }

public sealed record VideoSettings(int Width = 1920, int Height = 1080, double Fps = 30);
/// <summary>
/// Custom curves are cubic Bezier values with fixed time controls at 1/3 and 2/3.
/// RangeStart/RangeEnd retain the original curve interval after a razor cut.
/// </summary>
public sealed record FadeSettings(double Duration = 0, FadeCurve Curve = FadeCurve.Linear,
    double Control1 = 1.0 / 3, double Control2 = 2.0 / 3, double RangeStart = 0, double RangeEnd = 1);

/// <summary>Duration is the usable source duration in seconds; for stills it is only the initial placement duration.</summary>
public sealed record MediaAsset(string Id, string Path, MediaKind Kind, double Duration);

public sealed record Track(
    string Id, string Name, TrackKind Kind,
    bool Muted = false, bool Locked = false, double Volume = 1);

/// <summary>All times are seconds. Trimming changes references to source media, never source files.</summary>
public sealed record Clip(
    string Id, string AssetId, string TrackId,
    double Start, double SourceIn, double Duration,
    double Gain = 1, FadeSettings? FadeIn = null, FadeSettings? FadeOut = null,
    FadeSettings? AudioFadeIn = null, FadeSettings? AudioFadeOut = null, bool SeparateAudioFades = false,
    VideoFadeMode VideoFadeMode = VideoFadeMode.Opacity, string? LinkGroupId = null, bool SourceAudioMuted = false)
{
    public double End => Start + Duration;
    public double SourceEnd => SourceIn + Duration;
}

public sealed record CutProject
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string Title { get; init; } = "Untitled";
    public VideoSettings Video { get; init; } = new();
    public List<MediaAsset> MediaAssets { get; init; } = [];
    public List<Track> Tracks { get; init; } = [];
    public List<Clip> Clips { get; init; } = [];

    public static CutProject CreateEmpty(string title = "Untitled") => new()
    {
        Title = title,
        Tracks =
        [
            new("video-1", "Video 1", TrackKind.Video),
            new("audio-1", "Audio 1", TrackKind.Audio)
        ]
    };
}
