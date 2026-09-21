using System.ComponentModel;
using System.Windows;
using CutMaker.Core;

namespace CutMaker.App;

/// <summary>Stable row identity keeps unaffected WPF containers, focus and pointer capture alive.</summary>
internal sealed class TimelineTrackRow(Track track, bool collapsed, IReadOnlyList<TimelineClipView> clips) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private Track _track = track;
    public string Id => _track.Id;
    public string Name => _track.Name;
    public bool IsLocked => _track.Locked;
    public bool IsCollapsed { get; private set; } = collapsed;
    public double RowHeight => IsCollapsed ? 30 : 86;
    public string CollapseGlyph => IsCollapsed ? "▸" : "▾";
    public string CollapseToolTip => IsCollapsed ? "展開軌道" : "收折軌道";
    public bool IsAudio => _track.Kind == TrackKind.Audio;
    public bool IsMuted => _track.Muted;
    public string MuteToolTip => IsMuted ? "解除此軌道靜音" : "靜音此軌道";
    public Visibility DetailVisibility => IsCollapsed ? Visibility.Collapsed : Visibility.Visible;
    public string TrackDetail => $"{(IsMuted ? "靜音" : $"音量 {_track.Volume * 100:0}%")}{(IsLocked ? " · 鎖定" : "")}";
    public string Symbol => IsAudio ? "A" : "V";
    public IReadOnlyList<TimelineClipView> Clips { get; private set; } = clips;

    internal void Update(Track track, bool collapsed, IReadOnlyList<TimelineClipView> clips)
    {
        var propertiesChanged = _track != track || IsCollapsed != collapsed;
        var clipsChanged = !Clips.SequenceEqual(clips);
        _track = track; IsCollapsed = collapsed;
        if (clipsChanged) Clips = clips;
        if (propertiesChanged) PropertyChanged?.Invoke(this, new(null));
        else if (clipsChanged) PropertyChanged?.Invoke(this, new(nameof(Clips)));
    }
}
