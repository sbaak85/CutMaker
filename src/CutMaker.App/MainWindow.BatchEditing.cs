using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using CutMaker.Core;

namespace CutMaker.App;

public partial class MainWindow
{
    public static readonly DependencyProperty SelectedTimelineClipIdsProperty = DependencyProperty.Register(
        nameof(SelectedTimelineClipIds), typeof(IReadOnlyList<string>), typeof(MainWindow), new PropertyMetadata(null));
    public IReadOnlyList<string>? SelectedTimelineClipIds
    { get => (IReadOnlyList<string>?)GetValue(SelectedTimelineClipIdsProperty); set => SetValue(SelectedTimelineClipIdsProperty, value); }
    private Clip[] _clipClipboard = [];

    private HashSet<string> SelectionIds() => TimelineBatchEditor.ExpandLinks(_project,
        (SelectedTimelineClipIds ?? []).Append(SelectedTimelineClipId ?? "").Where(id => _project.Clips.Any(c => c.Id == id)));

    internal void SetClipSelection(IEnumerable<string> ids, string? primary = null)
    {
        var selected = TimelineBatchEditor.ExpandLinks(_project, ids);
        SelectedTimelineClipIds = selected.ToArray();
        SelectedTimelineClipId = primary is not null && selected.Contains(primary) ? primary : selected.FirstOrDefault();
    }

    private void SelectClipClick(string? id, bool toggle)
    {
        if (id is null) { if (!toggle) SetClipSelection([]); return; }
        var selected = SelectionIds();
        var group = TimelineBatchEditor.ExpandLinks(_project, [id]);
        if (toggle)
        {
            if (selected.Contains(id)) selected.ExceptWith(group); else selected.UnionWith(group);
        }
        else if (!selected.Contains(id)) selected = group;
        SetClipSelection(selected, id);
    }

    private void NormalizeClipSelection()
    {
        // Single-clip legacy paths select a new primary; do not retain a previous unrelated set.
        if (SelectedTimelineClipId is null) SelectedTimelineClipIds = [];
        else if (SelectedTimelineClipIds?.Contains(SelectedTimelineClipId) != true) SetClipSelection([SelectedTimelineClipId], SelectedTimelineClipId);
        else SetClipSelection(SelectionIds(), SelectedTimelineClipId);
    }

    private bool CommitBatch(Func<CutProject> operation, string message, bool clearSelection = false)
    {
        try
        {
            var candidate = operation();
            if (candidate.Clips.SequenceEqual(_project.Clips) && candidate.Tracks.SequenceEqual(_project.Tracks)) return false;
            RecordUndo(); _project = candidate;
            if (clearSelection) SetClipSelection([]);
            FinishEdit(message); return true;
        }
        catch (ProjectValidationException error) { StatusText.Text = error.Message; return false; }
    }

    private CutProject PlanLinkedReplacement(Clip replacement, bool moveSelection = false)
    {
        var old = _project.Clips.First(c => c.Id == replacement.Id);
        if (moveSelection)
            return TimelineBatchEditor.Move(_project, SelectionIds(), replacement.Start - old.Start, old.Id, replacement.TrackId);
        if (old.LinkGroupId is not null && old.TrackId != replacement.TrackId)
            throw new ProjectValidationException("連動片段請保留原軌道，或先解除連動。");
        var replacements = new List<Clip> { replacement };
        if (old.LinkGroupId is not null && (old.Start != replacement.Start || old.SourceIn != replacement.SourceIn || old.Duration != replacement.Duration))
        {
            if (old.TrackId != replacement.TrackId) throw new ProjectValidationException("連動片段請保留原軌道，或先解除連動。");
            foreach (var mate in _project.Clips.Where(c => c.Id != old.Id && c.LinkGroupId == old.LinkGroupId))
            {
                var duration = mate.Duration + replacement.Duration - old.Duration;
                FadeSettings? Clamp(FadeSettings? f) => f is null ? null : f with { Duration = Math.Min(f.Duration, duration) };
                replacements.Add(mate with
                {
                    Start = mate.Start + replacement.Start - old.Start,
                    SourceIn = mate.SourceIn + replacement.SourceIn - old.SourceIn, Duration = duration,
                    FadeIn = Clamp(mate.FadeIn), FadeOut = Clamp(mate.FadeOut), AudioFadeIn = Clamp(mate.AudioFadeIn), AudioFadeOut = Clamp(mate.AudioFadeOut)
                });
            }
        }
        return TimelineBatchEditor.Replace(_project, replacements);
    }

    private void CopyClips_Click(object sender, RoutedEventArgs e) => CopySelectedClips();
    internal void CopySelectedClips()
    {
        var ids = SelectionIds(); _clipClipboard = _project.Clips.Where(c => ids.Contains(c.Id)).ToArray();
        StatusText.Text = _clipClipboard.Length == 0 ? "請先選取片段" : $"已複製 {_clipClipboard.Length} 個片段 · 移動播放頭後 Ctrl+V 貼上";
    }
    private void PasteClips_Click(object sender, RoutedEventArgs e) => PasteClips(PlayheadSeconds);
    internal bool PasteClips(double time)
    {
        string[] added = [];
        var ok = CommitBatch(() => { var plan = TimelineBatchEditor.Paste(_project, _clipClipboard, time); added = plan.AddedIds; return plan.Project; }, "已貼上片段 · 保留各片段間距與原軌道");
        if (ok) { SetClipSelection(added); RefreshTimeline(); }
        return ok;
    }
    private void DuplicateClips_Click(object sender, RoutedEventArgs e)
    {
        CopySelectedClips(); if (_clipClipboard.Length > 0) PasteClips(_clipClipboard.Max(c => c.End));
    }
    private void SelectAllClips_Click(object sender, RoutedEventArgs e)
    { SetClipSelection(_project.Clips.Where(c => !_project.Tracks.First(t => t.Id == c.TrackId).Locked).Select(c => c.Id)); RefreshTimeline(); }
    private void RippleDelete_Click(object sender, RoutedEventArgs e) => DeleteSelectedClips(true);
    internal bool DeleteSelectedClips(bool ripple) => CommitBatch(() => TimelineBatchEditor.Delete(_project, SelectionIds(), ripple),
        ripple ? "已刪除並將同軌後續片段前移 · Ctrl+Z 復原" : "已移除選取片段 · 來源檔案保持不變", true);

    private async void ExtractAudio_Click(object sender, RoutedEventArgs e)
    {
        var clip = SelectedClip(); if (clip is null) { StatusText.Text = "請先選取影片片段"; return; }
        var asset = _project.MediaAssets.First(a => a.Id == clip.AssetId);
        if (asset.Kind != MediaKind.Video || _project.Tracks.First(t => t.Id == clip.TrackId).Kind != TrackKind.Video)
        { StatusText.Text = "請選取影片軌上的影片片段"; return; }
        var generation = _projectGeneration;
        try
        {
            StatusText.Text = "正在確認影片音訊…";
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var path = _projectPath is null ? Path.GetFullPath(asset.Path) : ProjectStore.ResolveAssetPath(_projectPath, asset);
            var json = await MediaRenderService.RunToolAsync(MediaRenderService.FindTool("ffprobe.exe"),
                ["-v", "error", "-select_streams", "a", "-show_entries", "stream=codec_type", "-of", "json", path], timeout.Token);
            using var info = JsonDocument.Parse(json);
            if (generation != _projectGeneration || _project.Clips.FirstOrDefault(c => c.Id == clip.Id) != clip ||
                _project.MediaAssets.FirstOrDefault(a => a.Id == asset.Id) != asset) return;
            if (!info.RootElement.GetProperty("streams").EnumerateArray().Any()) { StatusText.Text = "此影片沒有音訊串流"; return; }
            CommitBatch(() => TimelineBatchEditor.ExtractAudio(_project, clip.Id), "已分離音訊並連動 · 移動、修剪、切割與刪除會同步");
            SetClipSelection([clip.Id], clip.Id); RefreshTimeline();
        }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or JsonException or OperationCanceledException or System.ComponentModel.Win32Exception)
        { if (generation == _projectGeneration) StatusText.Text = $"無法分離音訊：{error.Message}"; }
    }

    private void UnlinkClips_Click(object sender, RoutedEventArgs e)
    {
        var ids = SelectionIds();
        CommitBatch(() => TimelineBatchEditor.Replace(_project, _project.Clips.Where(c => ids.Contains(c.Id)).Select(c => c with { LinkGroupId = null })), "已解除連動 · 之後可各別編輯");
    }

    private void MoveTrackUp_Click(object sender, RoutedEventArgs e) => MoveTrack(-1);
    private void MoveTrackDown_Click(object sender, RoutedEventArgs e) => MoveTrack(1);
    private void MoveTrack(int direction)
    {
        var index = _project.Tracks.FindIndex(t => t.Id == _selectedTrackId);
        if (index < 0 || index + direction < 0 || index + direction >= _project.Tracks.Count) return;
        var tracks = _project.Tracks.ToList(); var item = tracks[index];
        if (item.Locked) { StatusText.Text = "請先解除軌道鎖定"; return; }
        tracks.RemoveAt(index); tracks.Insert(index + direction, item);
        CommitBatch(() => _project with { Tracks = tracks }, "已調整軌道順序 · 上方影片軌顯示於前景");
    }
    private void DeleteTrack_Click(object sender, RoutedEventArgs e)
    {
        var track = _project.Tracks.FirstOrDefault(t => t.Id == _selectedTrackId); if (track is null) return;
        if (track.Locked) { StatusText.Text = "請先解除軌道鎖定"; return; }
        // Removing a track keeps linked clips on other tracks and explicitly unlinks them.
        var links = _project.Clips.Where(c => c.TrackId == track.Id).Select(c => c.LinkGroupId).Where(g => g is not null).ToHashSet();
        if (_project.Clips.Any(c => c.TrackId != track.Id && c.LinkGroupId is not null && links.Contains(c.LinkGroupId) && _project.Tracks.First(t => t.Id == c.TrackId).Locked))
        { StatusText.Text = "相連片段位於鎖定軌道，請先解除鎖定"; return; }
        CommitBatch(() => _project with
        {
            Tracks = _project.Tracks.Where(t => t.Id != track.Id).ToList(),
            Clips = _project.Clips.Where(c => c.TrackId != track.Id).Select(c => c.LinkGroupId is not null && links.Contains(c.LinkGroupId) ? c with { LinkGroupId = null } : c).ToList()
        }, "已移除軌道及其片段 · Ctrl+Z 可復原", true);
    }
}
