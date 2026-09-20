namespace CutMaker.Core;

/// <summary>Atomic multi-clip operations. Inputs and source files are never changed.</summary>
public static class TimelineBatchEditor
{
    public static HashSet<string> ExpandLinks(CutProject project, IEnumerable<string> ids)
    {
        var result = ids.ToHashSet();
        var links = project.Clips.Where(c => result.Contains(c.Id) && c.LinkGroupId is not null).Select(c => c.LinkGroupId).ToHashSet();
        result.UnionWith(project.Clips.Where(c => c.LinkGroupId is not null && links.Contains(c.LinkGroupId)).Select(c => c.Id));
        return result;
    }

    public static CutProject Replace(CutProject project, IEnumerable<Clip> replacements)
    {
        var changed = replacements.ToDictionary(c => c.Id);
        foreach (var clip in changed.Values)
        {
            var old = project.Clips.FirstOrDefault(c => c.Id == clip.Id);
            ProjectValidator.Require(old is not null && old.AssetId == clip.AssetId, "找不到原始片段，或來源素材已變更。");
            Unlocked(project, old!.TrackId); Unlocked(project, clip.TrackId);
        }
        return Validate(project with { Clips = project.Clips.Select(c => changed.GetValueOrDefault(c.Id, c)).ToList() });
    }

    public static CutProject Move(CutProject project, IEnumerable<string> ids, double delta, string primaryId, string targetTrack)
    {
        var selected = ExpandLinks(project, ids);
        var primary = project.Clips.First(c => c.Id == primaryId);
        // A group keeps its track arrangement. A single unlinked clip can move across compatible tracks.
        ProjectValidator.Require(selected.Count == 1 || primary.TrackId == targetTrack, "多選或連動片段會保留原軌道；可水平拖曳整組。");
        return Replace(project, project.Clips.Where(c => selected.Contains(c.Id)).Select(c => c with
        { Start = c.Start + delta, TrackId = selected.Count == 1 ? targetTrack : c.TrackId }));
    }

    public static CutProject Delete(CutProject project, IEnumerable<string> ids, bool ripple)
    {
        var selected = ExpandLinks(project, ids);
        var removed = project.Clips.Where(c => selected.Contains(c.Id)).ToArray();
        foreach (var c in removed) Unlocked(project, c.TrackId);
        var rest = project.Clips.Where(c => !selected.Contains(c.Id)).ToList();
        if (ripple)
        {
            rest = rest.Select(c =>
            {
                var shift = removed.Where(r => r.TrackId == c.TrackId && r.End <= c.Start + 1e-7).Sum(r => r.Duration);
                if (shift <= 0) return c;
                Unlocked(project, c.TrackId);
                return c with { Start = Math.Max(0, c.Start - shift) };
            }).ToList();
            foreach (var group in rest.Where(c => c.LinkGroupId is not null).GroupBy(c => c.LinkGroupId))
            {
                var shifts = group.Select(c => project.Clips.First(old => old.Id == c.Id).Start - c.Start).ToArray();
                ProjectValidator.Require(shifts.Max() - shifts.Min() < 1e-7, "此同軌波紋刪除會使連動片段失去同步；請先解除連動或一般刪除。");
            }
        }
        return Validate(project with { Clips = rest });
    }

    public static (CutProject Project, string[] AddedIds) Paste(CutProject project, IReadOnlyList<Clip> copied, double start)
    {
        ProjectValidator.Require(copied.Count > 0 && double.IsFinite(start) && start >= 0, "剪貼簿沒有片段或貼上位置無效。");
        var offset = start - copied.Min(c => c.Start);
        var groups = copied.Where(c => c.LinkGroupId is not null).Select(c => c.LinkGroupId!).Distinct().ToDictionary(g => g, _ => Guid.NewGuid().ToString("N"));
        var added = copied.Select(c => c with
        { Id = Guid.NewGuid().ToString("N"), Start = c.Start + offset, LinkGroupId = c.LinkGroupId is null ? null : groups[c.LinkGroupId] }).ToArray();
        foreach (var c in added) Unlocked(project, c.TrackId);
        return (Validate(project with { Clips = [.. project.Clips, .. added] }), added.Select(c => c.Id).ToArray());
    }

    public static CutProject Split(CutProject project, IEnumerable<string> ids, double position)
    {
        var selected = ExpandLinks(project, ids);
        var result = new List<Clip>();
        var rightGroups = new Dictionary<string, string>();
        var count = 0;
        foreach (var clip in project.Clips)
        {
            if (!selected.Contains(clip.Id) || position <= clip.Start + 1e-7 || position >= clip.End - 1e-7) { result.Add(clip); continue; }
            Unlocked(project, clip.TrackId);
            var image = project.MediaAssets.First(a => a.Id == clip.AssetId).Kind == MediaKind.Image;
            var (left, right) = ClipEditor.Split(clip, position, Guid.NewGuid().ToString("N"), image);
            if (clip.LinkGroupId is { } link)
            {
                if (!rightGroups.TryGetValue(link, out var newLink)) rightGroups[link] = newLink = Guid.NewGuid().ToString("N");
                right = right with { LinkGroupId = newLink };
            }
            result.Add(left); result.Add(right); count++;
        }
        ProjectValidator.Require(count > 0, "請把播放頭移到選取片段的內部。");
        return Validate(project with { Clips = result });
    }

    public static CutProject ExtractAudio(CutProject project, string clipId)
    {
        var clip = project.Clips.First(c => c.Id == clipId);
        Unlocked(project, clip.TrackId);
        ProjectValidator.Require(project.Tracks.First(t => t.Id == clip.TrackId).Kind == TrackKind.Video &&
            project.MediaAssets.First(a => a.Id == clip.AssetId).Kind == MediaKind.Video, "請選取影片軌上的影片片段。");
        ProjectValidator.Require(clip.LinkGroupId is null, "此片段已有連動，請勿重複分離。");
        var link = Guid.NewGuid().ToString("N");
        var sourceTrack = project.Tracks.First(t => t.Id == clip.TrackId);
        var track = new Track(Guid.NewGuid().ToString("N"), $"分離音訊 · {sourceTrack.Name}", TrackKind.Audio, sourceTrack.Muted, Volume: sourceTrack.Volume);
        var audio = clip with { Id = Guid.NewGuid().ToString("N"), TrackId = track.Id, LinkGroupId = link, SourceAudioMuted = false };
        return Validate(project with
        {
            Tracks = [.. project.Tracks, track],
            Clips = [.. project.Clips.Select(c => c.Id == clip.Id ? c with { SourceAudioMuted = true, LinkGroupId = link } : c), audio]
        });
    }

    public static CutProject Validate(CutProject project)
    {
        ProjectValidator.Validate(project);
        foreach (var track in project.Clips.GroupBy(c => c.TrackId))
        {
            var ordered = track.OrderBy(c => c.Start).ToArray();
            for (var i = 1; i < ordered.Length; i++)
                ProjectValidator.Require(ordered[i].Start >= ordered[i - 1].End - 1e-7, "同一軌道的片段不能重疊，請移到空白位置或新增軌道。");
        }
        return project;
    }

    private static void Unlocked(CutProject project, string trackId) => ProjectValidator.Require(
        project.Tracks.Any(t => t.Id == trackId && !t.Locked), "軌道已鎖定或不存在，無法修改。");
}
