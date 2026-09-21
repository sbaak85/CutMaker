using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using CutMaker.Core;

namespace CutMaker.App;

public partial class MainWindow
{
    internal bool TrimSelectedToPlayhead(bool start)
    {
        var clip = SelectedClip();
        if (clip is null || PlayheadSeconds <= clip.Start || PlayheadSeconds >= clip.End)
        { StatusText.Text = "請將播放頭放在選取片段內部"; return false; }
        var asset = _project.MediaAssets.First(a => a.Id == clip.AssetId);
        try
        {
            var replacement = start
                ? ClipEditor.TrimStart(clip, PlayheadSeconds, asset.Duration, asset.Kind == MediaKind.Image)
                : ClipEditor.TrimEnd(clip, PlayheadSeconds, asset.Duration, asset.Kind == MediaKind.Image);
            return TryReplaceClip(replacement, start ? "已將片段開頭修剪至播放頭" : "已將片段結尾修剪至播放頭");
        }
        catch (ProjectValidationException error) { StatusText.Text = error.Message; return false; }
    }

    internal bool TryHandleClipShortcut(Key key, ModifierKeys modifiers, bool repeat)
    {
        if (modifiers != ModifierKeys.None || key is not (Key.B or Key.OemOpenBrackets or Key.OemCloseBrackets)) return false;
        if (!repeat)
        {
            if (key == Key.B) SplitSelectedClip(PlayheadSeconds);
            else TrimSelectedToPlayhead(key == Key.OemOpenBrackets);
        }
        return true;
    }

    private void TimelineBlank_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var point = e.GetPosition(TimelineTimeRuler);
        if (point.X < 0 || point.X > TimelineViewportWidth) return;
        var source = e.OriginalSource as DependencyObject;
        TimelineLane? lane = null;
        for (var node = source; node is not null && node != TimelineContent; node = VisualTreeHelper.GetParent(node))
        {
            if (node is System.Windows.Controls.Primitives.ScrollBar or System.Windows.Controls.Primitives.Thumb) return;
            if (node is TimelineLane found) { lane = found; break; }
        }
        var time = Math.Max(0, TimelineOffsetSeconds + point.X / TimelinePixelsPerSecond);
        if (lane is not null)
        {
            var local = e.GetPosition(lane);
            var inset = lane.IsCompact ? 2 : 5;
            if (local.Y >= inset && local.Y <= lane.ActualHeight - inset &&
                lane.Clips?.Any(c => c.Start <= time && time < c.Start + c.Duration) == true) return;
        }
        lane ??= FindLanes(TrackItems).LastOrDefault();
        BeginTimelineBlankGesture(time, lane, lane is null ? default : e.GetPosition(lane), Keyboard.Modifiers);
        e.Handled = true;
    }

    internal void BeginTimelineBlankGesture(double time, TimelineLane? lane, Point lanePoint, ModifierKeys modifiers, bool captureMouse = true)
    {
        if (modifiers.HasFlag(ModifierKeys.Control))
        {

            if (lane is null) return;
            BeginMarqueeSelection(lane, lanePoint, additive: modifiers.HasFlag(ModifierKeys.Shift), captureMouse: captureMouse);
            Keyboard.Focus(lane);
        }
        else
        {
            Keyboard.Focus(TimelineTimeRuler);
            BeginPreviewScrub();
            if (captureMouse) TimelineTimeRuler.CaptureMouse();
            SeekPreview(time);
        }

    }
}