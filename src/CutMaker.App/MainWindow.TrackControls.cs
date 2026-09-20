using System.Windows;
using System.Windows.Controls;
using CutMaker.Core;

namespace CutMaker.App;

public partial class MainWindow
{
    // Track height is a view preference for this open project, never an edit to its media or timing.
    private readonly HashSet<string> _collapsedTracks = [];

    internal bool IsTrackCollapsed(string id) => _collapsedTracks.Contains(id);
    private void ClearCollapsedTracks() => _collapsedTracks.Clear();
    private void PruneCollapsedTracks() => _collapsedTracks.RemoveWhere(id => !_project.Tracks.Any(track => track.Id == id));

    private void CollapseTrack_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id }) ToggleTrackCollapsed(id);
        e.Handled = true;
    }

    internal bool ToggleTrackCollapsed(string id)
    {
        if (!_project.Tracks.Any(track => track.Id == id)) return false;
        EndMarqueeSelection(cancel: true);
        CancelPointerEdit();
        var verticalOffset = TimelineTrackScroll.VerticalOffset;
        if (!_collapsedTracks.Add(id)) _collapsedTracks.Remove(id);
        RefreshTimeline();
        TrackItems.UpdateLayout();
        TimelineTrackScroll.ScrollToVerticalOffset(verticalOffset);
        FindLanes(TrackItems).FirstOrDefault(lane => (string)lane.Tag == id)?.Focus();
        StatusText.Text = IsTrackCollapsed(id) ? "軌道已收折 · 仍可選取、移動、修剪及切割" : "軌道已展開";
        return true;
    }

    private void MuteTrack_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id }) ToggleTrackMute(id);
        e.Handled = true;
    }

    internal bool ToggleTrackMute(string id)
    {
        var track = _project.Tracks.FirstOrDefault(track => track.Id == id && track.Kind == TrackKind.Audio);
        if (track is null) return false;
        EndMarqueeSelection(cancel: true);
        CancelPointerEdit();
        if (!ApplyTrackSettings(track with { Muted = !track.Muted })) return false;
        // ApplyTrackSettings shares the normal edit path: Undo, project saving and preview invalidation.
        TrackItems.UpdateLayout();
        FindLanes(TrackItems).FirstOrDefault(lane => (string)lane.Tag == id)?.Focus();
        StatusText.Text = track.Muted ? $"已解除「{track.Name}」靜音" : $"「{track.Name}」已靜音 · 預覽及匯出均不播放此軌聲音";
        return true;
    }
}
