namespace CutMaker.Core;

public sealed class ProjectValidationException(string message) : Exception(message);

public static class ProjectValidator
{
    internal const double TimeTolerance = 0.0000001;

    public static void Validate(CutProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        Require(project.SchemaVersion == CutProject.CurrentSchemaVersion, "Unsupported project schema version.");
        Text(project.Title, "Project title");
        Require(project.Video is not null, "Video settings are required.");
        var video = project.Video!;
        Require(video.Width is >= 1 and <= 16384 && video.Height is >= 1 and <= 16384,
            "Video dimensions must be between 1 and 16384 pixels.");
        Require(double.IsFinite(video.Fps) && video.Fps > 0 && video.Fps <= 240,
            "Frame rate must be greater than zero and no more than 240.");
        Require(project.MediaAssets is not null && project.Tracks is not null && project.Clips is not null,
            "Project collections are required.");

        if (project.PlaybackRange is { } range)
            Require(double.IsFinite(range.Start) && double.IsFinite(range.End) && range.Start >= 0 && range.End > range.Start,
                "播放框的結束時間必須大於起點，且時間必須為有限非負數。");
        var assets = new Dictionary<string, MediaAsset>(StringComparer.Ordinal);
        foreach (var asset in project.MediaAssets!)
        {
            Require(asset is not null, "A media asset cannot be null.");
            Text(asset!.Id, "Asset ID");
            Text(asset.Path, "Asset path");
            Require(Enum.IsDefined(asset.Kind), "Unknown media kind.");
            PositiveTime(asset.Duration, "Asset duration");
            Require(assets.TryAdd(asset.Id, asset), $"Duplicate asset ID: {asset.Id}");
        }

        var tracks = new Dictionary<string, Track>(StringComparer.Ordinal);
        foreach (var track in project.Tracks!)
        {
            Require(track is not null, "A track cannot be null.");
            Text(track!.Id, "Track ID");
            Text(track.Name, "Track name");
            Require(Enum.IsDefined(track.Kind), "Unknown track kind.");
            Gain(track.Volume, "Track volume");
            Require(tracks.TryAdd(track.Id, track), $"Duplicate track ID: {track.Id}");
        }

        var clipIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var clip in project.Clips!)
        {
            Require(clip is not null, "A clip cannot be null.");
            Text(clip!.Id, "Clip ID");
            Text(clip.AssetId, "Clip asset ID");
            Text(clip.TrackId, "Clip track ID");
            Require(clipIds.Add(clip.Id), $"Duplicate clip ID: {clip.Id}");
            Require(assets.TryGetValue(clip.AssetId, out var asset), $"Missing asset: {clip.AssetId}");
            Require(tracks.TryGetValue(clip.TrackId, out var track), $"Missing track: {clip.TrackId}");
            Require(track!.Kind == TrackKind.Audio ? asset!.Kind != MediaKind.Image : asset!.Kind != MediaKind.Audio,
                "The media kind is incompatible with its track.");
            Require(clip.TimeRatio == 1 || (asset.Kind == MediaKind.Audio && clip.LinkGroupId is null),
                "時間比例適用於獨立音檔；連結的影片音訊請先解除連結。");
            ValidateClip(clip, asset.Duration, asset.Kind == MediaKind.Image);
        }
    }

    internal static void ValidateClip(Clip clip, double assetDuration, bool isStillImage = false)
    {
        ArgumentNullException.ThrowIfNull(clip);
        PositiveTime(assetDuration, "Asset duration");
        NonNegativeTime(clip.Start, "Clip start");
        NonNegativeTime(clip.SourceIn, "Clip source in-point");
        PositiveTime(clip.Duration, "Clip duration");
        Require(double.IsFinite(clip.TimeRatio) && clip.TimeRatio is >= .25 and <= 4, "時間比例須介於 25% 與 400%。");
        Require(double.IsFinite(clip.End) && double.IsFinite(clip.SourceEnd), "Clip end time is out of range.");
        // Early projects stored a source offset when trimming stills. Keep those files readable;
        // new still trims/splits canonicalize the meaningless offset to zero.
        if (!isStillImage) Require(clip.SourceEnd <= assetDuration + TimeTolerance, "Clip extends past the source duration.");
        Gain(clip.Gain, "Clip gain");
        ValidateFade(clip.FadeIn, clip.Duration);
        ValidateFade(clip.FadeOut, clip.Duration);
        ValidateFade(clip.AudioFadeIn, clip.Duration);
        ValidateFade(clip.AudioFadeOut, clip.Duration);
        Require(Enum.IsDefined(clip.VideoFadeMode), "Unknown video Fade mode.");
        Require(clip.LinkGroupId is null || !string.IsNullOrWhiteSpace(clip.LinkGroupId), "A linked group ID cannot be empty.");
    }

    private static void ValidateFade(FadeSettings? fade, double duration)
    {
        if (fade is null) return;
        NonNegativeTime(fade.Duration, "Fade duration");
        Require(fade.Duration <= duration + TimeTolerance, "Fade cannot be longer than its clip.");
        Require(Enum.IsDefined(fade.Curve), "Unknown fade curve.");
        UnitValue(fade.Control1, "Fade control 1");
        UnitValue(fade.Control2, "Fade control 2");
        UnitValue(fade.RangeStart, "Fade range start");
        UnitValue(fade.RangeEnd, "Fade range end");
        Require(fade.RangeStart <= fade.RangeEnd, "Fade curve range must be ordered.");
    }

    private static void Text(string? value, string name) => Require(!string.IsNullOrWhiteSpace(value), $"{name} is required.");
    private static void PositiveTime(double value, string name) => Require(double.IsFinite(value) && value > 0, $"{name} must be positive and finite.");
    private static void NonNegativeTime(double value, string name) => Require(double.IsFinite(value) && value >= 0, $"{name} must be non-negative and finite.");
    private static void Gain(double value, string name) => Require(double.IsFinite(value) && value >= 0 && value <= 4, $"{name} must be between 0 and 4.");
    private static void UnitValue(double value, string name) => Require(double.IsFinite(value) && value >= 0 && value <= 1, $"{name} must be between 0 and 1.");

    internal static void Require(bool condition, string message)
    {
        if (!condition) throw new ProjectValidationException(message);
    }
}
