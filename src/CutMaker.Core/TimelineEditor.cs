namespace CutMaker.Core;

/// <summary>Validates a proposed edit without mutating either the project or the original clip.</summary>
public static class TimelineEditor
{
    public static bool CanReplace(CutProject project, Clip replacement, out string reason)
    {
        reason = string.Empty;
        var original = project.Clips.FirstOrDefault(clip => clip.Id == replacement.Id);
        if (original is null) return Reject("找不到要編輯的片段。", out reason);
        if (original.AssetId != replacement.AssetId) return Reject("編輯不能更換來源素材。", out reason);
        var sourceTrack = project.Tracks.FirstOrDefault(track => track.Id == original.TrackId);
        var target = project.Tracks.FirstOrDefault(track => track.Id == replacement.TrackId);
        var asset = project.MediaAssets.FirstOrDefault(item => item.Id == original.AssetId);
        if (sourceTrack is null || target is null || asset is null) return Reject("找不到片段來源或軌道。", out reason);
        if (sourceTrack.Locked || target.Locked) return Reject("軌道已鎖定，請先解除鎖定。", out reason);
        if (target.Kind == TrackKind.Audio ? asset.Kind == MediaKind.Image : asset.Kind == MediaKind.Audio)
            return Reject("圖片請放入影片軌；音訊請放入音訊軌。", out reason);
        try { ProjectValidator.ValidateClip(replacement, asset.Duration, asset.Kind == MediaKind.Image); }
        catch (ProjectValidationException error) { return Reject(error.Message, out reason); }
        if (project.Clips.Any(clip => clip.Id != replacement.Id && clip.TrackId == replacement.TrackId &&
            replacement.Start < clip.End - ProjectValidator.TimeTolerance && replacement.End > clip.Start + ProjectValidator.TimeTolerance))
            return Reject("這個位置會與既有片段重疊，請移到空白處或其他軌道。", out reason);
        return true;
    }

    public static Clip Move(CutProject project, string clipId, string trackId, double start)
    {
        var original = project.Clips.FirstOrDefault(clip => clip.Id == clipId)
            ?? throw new ProjectValidationException("找不到要移動的片段。");
        var replacement = original with { TrackId = trackId, Start = start };
        if (!CanReplace(project, replacement, out var reason)) throw new ProjectValidationException(reason);
        return replacement;
    }

    private static bool Reject(string message, out string reason) { reason = message; return false; }
}
