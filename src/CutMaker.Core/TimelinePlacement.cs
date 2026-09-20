namespace CutMaker.Core;

/// <summary>Plans non-destructive insertions; callers explicitly add a successful result to their project.</summary>
public static class TimelinePlacement
{
    public static bool CanPlace(CutProject project, string assetId, string trackId, double start, out string reason)
    {
        ArgumentNullException.ThrowIfNull(project);
        reason = string.Empty;
        if (!double.IsFinite(start) || start < 0)
            return Reject("片段起點必須是大於或等於 0 的有效時間。", out reason);
        if (project.MediaAssets is null || project.Tracks is null || project.Clips is null)
            return Reject("專案資料不完整，無法放入素材。", out reason);

        var asset = project.MediaAssets.FirstOrDefault(item => item?.Id == assetId);
        var track = project.Tracks.FirstOrDefault(item => item?.Id == trackId);
        if (asset is null)
            return Reject("找不到這個素材，請重新從素材庫選取。", out reason);
        if (track is null)
            return Reject("找不到目標軌道。", out reason);
        if (track.Locked)
            return Reject("這條軌道已鎖定，請先解除鎖定。", out reason);

        var compatible = track.Kind switch
        {
            TrackKind.Video => asset.Kind is MediaKind.Video or MediaKind.Image,
            TrackKind.Audio => asset.Kind == MediaKind.Audio,
            _ => false
        };
        if (!compatible)
            return Reject("影片與圖片請放入影片軌；音訊請放入音訊軌。", out reason);
        if (!double.IsFinite(asset.Duration) || asset.Duration <= 0)
            return Reject("素材沒有有效的播放長度，請重新匯入。", out reason);

        var end = start + asset.Duration;
        if (!double.IsFinite(end) || end <= start)
            return Reject("片段的結束時間超出可使用的範圍。", out reason);

        foreach (var existing in project.Clips)
        {
            if (existing is null)
                return Reject("專案含有不完整的片段資料，無法放入素材。", out reason);
            if (existing.TrackId != trackId) continue;
            if (!double.IsFinite(existing.Start) || existing.Start < 0 ||
                !double.IsFinite(existing.End) || existing.End <= existing.Start)
                return Reject("這條軌道含有無效的片段時間，無法放入素材。", out reason);
            // Half-open intervals allow touching edges without replacing or shortening either clip.
            if (start < existing.End && end > existing.Start)
                return Reject("這個位置會與既有片段重疊，請移到空白處或新增軌道。", out reason);
        }

        return true;
    }

    public static Clip CreateClip(CutProject project, string assetId, string trackId, double start)
    {
        if (!CanPlace(project, assetId, trackId, start, out var reason))
            throw new ProjectValidationException(reason);

        var asset = project.MediaAssets.First(item => item.Id == assetId);
        string id;
        do { id = $"clip-{Guid.NewGuid():N}"; }
        while (project.Clips.Any(item => item.Id == id));
        return new Clip(id, asset.Id, trackId, start, 0, asset.Duration);
    }

    /// <summary>Rounds a timeline position, never a source duration, to the nearest project frame.</summary>
    public static double RoundToFrame(double seconds, double fps)
    {
        ProjectValidator.Require(double.IsFinite(seconds) && seconds >= 0,
            "Timeline position must be non-negative and finite.");
        ProjectValidator.Require(double.IsFinite(fps) && fps > 0 && fps <= 240,
            "Frame rate must be greater than zero and no more than 240.");
        var frame = seconds * fps;
        ProjectValidator.Require(double.IsFinite(frame), "Timeline position is out of range.");
        var result = Math.Round(frame, MidpointRounding.AwayFromZero) / fps;
        ProjectValidator.Require(double.IsFinite(result), "Timeline position is out of range.");
        return result;
    }

    private static bool Reject(string message, out string reason)
    {
        reason = message;
        return false;
    }
}
