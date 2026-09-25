using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using CutMaker.Core;

namespace CutMaker.App;

public partial class MainWindow
{
    private PlaybackRange? _rangeDragOriginal;
    private PlaybackRange? _rangeDragCandidate;
    private double _rangeDragDelta;
    private PlaybackRange? EffectivePlaybackRange => _project.PlaybackRange is { Enabled: true } r &&
        r.Start < PreviewDuration && Math.Min(r.End, PreviewDuration) > r.Start
        ? r with { End = Math.Min(r.End, PreviewDuration) } : null;

    private void PlaybackRange_Click(object sender, RoutedEventArgs e)
    {
        if (PreviewDuration <= 0) { StatusText.Text = "請先將素材放入軌道。"; return; }
        var selected = SelectedClip();
        var initial = _project.PlaybackRange ?? new PlaybackRange(selected?.Start ?? Math.Min(PlayheadSeconds, Math.Max(0, PreviewDuration - 1)), selected?.End ?? PreviewDuration);
        var dialog = new Window { Title = "播放框", Owner = this, Width = 430, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (Brush)FindResource("PanelBrush"), Foreground = Brushes.White };
        var panel = new StackPanel { Margin = new Thickness(20) }; dialog.Content = panel;
        var enabled = new CheckBox { Content = "侷限在播放框內播放", IsChecked = initial.Enabled, Foreground = Brushes.White, Margin = new Thickness(0,0,0,10) };
        var loop = new CheckBox { Content = "到框尾後回到框頭循環", IsChecked = initial.Loop, Foreground = Brushes.White, Margin = new Thickness(0,10,0,10) };
        panel.Children.Add(enabled);
        TextBox Row(string label, double value)
        {
            panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0,6,0,3) });
            var row = new DockPanel();
            var input = new TextBox { Text = value.ToString("0.#########", CultureInfo.InvariantCulture) };
            var use = new Button { Content = "使用播放頭", Padding = new Thickness(8,4,8,4) };
            use.Click += (_, _) => input.Text = PlayheadSeconds.ToString("0.#########", CultureInfo.InvariantCulture);
            DockPanel.SetDock(use, Dock.Right); row.Children.Add(use); row.Children.Add(input); panel.Children.Add(row); return input;
        }
        var start = Row("框頭（秒）", initial.Start); var end = Row("框尾（秒）", Math.Min(initial.End, PreviewDuration));
        panel.Children.Add(loop);
        panel.Children.Add(new TextBlock { Text = "拖曳時間軸上播放框的左右邊線也能調整範圍。\n播放框適用於所有軌道，並隨專案儲存。", TextWrapping = TextWrapping.Wrap });
        var error = new TextBlock { Foreground = Brushes.Salmon, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,8,0,8) }; panel.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; panel.Children.Add(buttons);
        var remove = new Button { Content = "移除播放框", IsEnabled = _project.PlaybackRange is not null };
        var cancel = new Button { Content = "取消", IsCancel = true }; var apply = new Button { Content = "套用", IsDefault = true };
        buttons.Children.Add(remove); buttons.Children.Add(cancel); buttons.Children.Add(apply);
        remove.Click += (_, _) => { SetPlaybackRange(null); dialog.DialogResult = true; };
        apply.Click += (_, _) =>
        {
            if (!double.TryParse(start.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var a) ||
                !double.TryParse(end.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var b) ||
                !double.IsFinite(a) || !double.IsFinite(b) || a < 0 || b <= a || b > PreviewDuration)
            { error.Text = "請輸入時間軸內的秒數；框尾必須大於框頭。"; return; }
            SetPlaybackRange(new(a, b, enabled.IsChecked == true, loop.IsChecked == true)); dialog.DialogResult = true;
        };
        dialog.ShowDialog();
    }

    internal void SetPlaybackRange(PlaybackRange? range)
    {
        ProjectValidator.Validate(_project with { PlaybackRange = range });
        if (_project.PlaybackRange == range) return;
        PausePreview(); ClearAudition(); RecordUndo();
        _project = _project with { PlaybackRange = range };
        FinishEdit(range is null ? "已移除播放框" : $"播放框：{range.Start:0.###}–{range.End:0.###} 秒 · {(range.Loop ? "循環" : "到尾停止")}");
    }

    private void RefreshPlaybackRangeVisual()
    {
        if (PlaybackRangeCanvas is null || TimelineContent is null) return;
        var r = _rangeDragCandidate ?? _project.PlaybackRange;
        PlaybackRangeCanvas.Visibility = r is null ? Visibility.Collapsed : Visibility.Visible;
        if (r is null) return;
        var left = TrackHeaderWidth + 8;
        var width = TimelineViewportWidth;
        var x1 = (r.Start - TimelineOffsetSeconds) * TimelinePixelsPerSecond;
        var x2 = (r.End - TimelineOffsetSeconds) * TimelinePixelsPerSecond;
        var a = Math.Clamp(x1, 0, width); var b = Math.Clamp(x2, 0, width);
        Canvas.SetLeft(PlaybackRangeOutline, left + a);
        PlaybackRangeOutline.Width = Math.Max(0, b - a);
        PlaybackRangeOutline.Height = Math.Max(0, TimelineContent.ActualHeight - 1);
        PlaybackRangeOutline.Visibility = b > a ? Visibility.Visible : Visibility.Collapsed;
        PlaybackRangeCanvas.Opacity = r.Enabled ? 1 : .4;
        foreach (var (handle, x) in new[] { (PlaybackRangeStartHandle, x1), (PlaybackRangeEndHandle, x2) })
        {
            Canvas.SetLeft(handle, left + x - 4); Canvas.SetTop(handle, 0);
            handle.Height = Math.Max(1, TimelineContent.ActualHeight - 1);
            handle.Visibility = x >= 0 && x <= width ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void PlaybackRange_DragStarted(object sender, DragStartedEventArgs e)
    {
        PausePreview(); _rangeDragOriginal = _project.PlaybackRange; _rangeDragCandidate = _rangeDragOriginal; _rangeDragDelta = 0;
        Keyboard.Focus((Thumb)sender); e.Handled = true;
    }
    private void PlaybackRange_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_rangeDragOriginal is not { } r || PreviewDuration <= 0) return;
        _rangeDragDelta += e.HorizontalChange / TimelinePixelsPerSecond;
        var minimum = Math.Min(1 / _project.Video.Fps, PreviewDuration);
        r = r with { Start = Math.Min(r.Start, Math.Max(0, PreviewDuration - minimum)), End = Math.Min(r.End, PreviewDuration) };
        _rangeDragCandidate = ReferenceEquals(sender, PlaybackRangeStartHandle)
            ? r with { Start = Math.Clamp(r.Start + _rangeDragDelta, 0, Math.Max(0, Math.Min(r.End, PreviewDuration) - minimum)), End = Math.Min(r.End, PreviewDuration) }
            : r with { End = Math.Clamp(r.End + _rangeDragDelta, Math.Min(PreviewDuration, r.Start + minimum), PreviewDuration) };
        RefreshPlaybackRangeVisual(); e.Handled = true;
    }
    private void PlaybackRange_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        var candidate = _rangeDragCandidate; _rangeDragOriginal = _rangeDragCandidate = null;
        if (!e.Canceled && candidate is not null) SetPlaybackRange(candidate);
        RefreshPlaybackRangeVisual(); e.Handled = true;
    }
    private void PlaybackRange_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { ((Thumb)sender).CancelDrag(); e.Handled = true; }
    }
    private bool CheckPlaybackRangeEnd(double position)
    {
        if (_auditionEnd is not null || EffectivePlaybackRange is not { } range ||
            (position < range.End - .000001 && _audioDevice?.Ended != true)) return false;
        PausePreview();
        if (range.Loop) { SeekPreview(range.Start); StartPreviewPlayback(); }
        else { SeekPreview(range.End); StatusText.Text = "播放框已播放完畢"; }
        return true;
    }
}
