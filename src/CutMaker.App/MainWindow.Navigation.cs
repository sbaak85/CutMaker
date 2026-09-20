using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CutMaker.Core;

namespace CutMaker.App;

public partial class MainWindow
{
    /// <summary>Called only for timeline/preview focus, leaving text, sliders and native selection keys alone.</summary>
    internal bool TryHandleNavigationShortcut(Key key, ModifierKeys modifiers)
    {
        if (key is Key.Left or Key.Right)
        {
            var direction = key == Key.Left ? -1 : 1;
            switch (modifiers)
            {
                case ModifierKeys.None: StepPreviewFrame(direction); return true;
                case ModifierKeys.Shift: NavigatePlayhead(PlayheadSeconds + direction, "移動一秒"); return true;
                case ModifierKeys.Alt: NavigateBoundary(direction); return true;
                default: return false;
            }
        }
        if (modifiers == ModifierKeys.None && key is Key.Home or Key.End)
        {
            NavigatePlayhead(key == Key.Home ? 0 : PreviewDuration, key == Key.Home ? "時間軸起點" : "時間軸終點");
            return true;
        }
        if (modifiers == ModifierKeys.Control && key is Key.D0 or Key.NumPad0)
        { FitTimelineToView(); return true; }
        if (modifiers == ModifierKeys.Control && key == Key.G)
        { ShowGoToTimeDialog(); return true; }
        return false;
    }

    private void FrameBack_Click(object sender, RoutedEventArgs e) => StepPreviewFrame(-1);
    private void FrameForward_Click(object sender, RoutedEventArgs e) => StepPreviewFrame(1);
    private void FitTimeline_Click(object sender, RoutedEventArgs e) => FitTimelineToView();
    private void PreviousBoundary_Click(object sender, RoutedEventArgs e) => NavigateBoundary(-1);
    private void NextBoundary_Click(object sender, RoutedEventArgs e) => NavigateBoundary(1);
    private void SelectedStart_Click(object sender, RoutedEventArgs e) => NavigateSelectionEdge(false);
    private void SelectedEnd_Click(object sender, RoutedEventArgs e) => NavigateSelectionEdge(true);
    private void GoToTime_Click(object sender, RoutedEventArgs e) => ShowGoToTimeDialog();
    private void PreviewSurface_MouseDown(object sender, MouseButtonEventArgs e)
    {
        Keyboard.Focus(PreviewPane);
        e.Handled = true;
    }

    private bool NavigationGestureActive => _pointerOriginal is not null || IsMarqueeSelecting || _draggingFromLibrary || _previewScrubbing;

    internal void StepPreviewFrame(int direction) => NavigatePlayhead(
        TimelineNavigation.StepFrame(PlayheadSeconds, direction, _project.Video.Fps, PreviewDuration),
        direction < 0 ? "前一格" : "後一格", preciseFrame: true);

    private void NavigateBoundary(int direction) => NavigatePlayhead(
        TimelineNavigation.AdjacentBoundary(_project.Clips, PlayheadSeconds, direction, PreviewDuration),
        direction < 0 ? "前一個片段邊界" : "下一個片段邊界");

    private void NavigateSelectionEdge(bool end)
    {
        var selected = SelectionIds();
        var clips = _project.Clips.Where(clip => selected.Contains(clip.Id)).ToArray();
        if (clips.Length == 0) { StatusText.Text = "請先選取片段"; return; }
        NavigatePlayhead(end ? clips.Max(clip => clip.End) : clips.Min(clip => clip.Start), end ? "選取範圍終點" : "選取範圍起點");
    }

    internal void NavigatePlayhead(double seconds, string label = "定位", bool preciseFrame = false)
    {
        if (NavigationGestureActive || !double.IsFinite(seconds)) return;
        // A pending prepare may have captured playWhenReady=true; stopping only its current clock is insufficient.
        if (_previewPreparing) InvalidatePreview();
        PausePreview();
        SeekPreview(Math.Clamp(seconds, 0, PreviewDuration));
        if (preciseFrame && PreviewIsReady) QueuePreviewFrame();
        KeepPlayheadVisible();
        StatusText.Text = $"{label} · {FormatPreviewTime(PlayheadSeconds)} · {PlayheadSeconds.ToString("0.#########", CultureInfo.InvariantCulture)} 秒";
    }

    private void KeepPlayheadVisible()
    {
        UpdateTimelineViewport();
        var visible = TimelineViewportWidth / TimelinePixelsPerSecond;
        var padding = Math.Min(visible / 4, 36 / TimelinePixelsPerSecond);
        if (PlayheadSeconds < TimelineOffsetSeconds + padding)
            TimelineOffsetSeconds = Math.Clamp(PlayheadSeconds - padding, 0, TimelineHorizontalScroll.Maximum);
        else if (PlayheadSeconds > TimelineOffsetSeconds + visible - padding)
            TimelineOffsetSeconds = Math.Clamp(PlayheadSeconds - visible + padding, 0, TimelineHorizontalScroll.Maximum);
    }

    internal void FitTimelineToView()
    {
        if (NavigationGestureActive) return;
        UpdateLayout();
        UpdateTimelineViewport();
        // Reserve a little room after the final edge. Empty timelines show a useful one-minute overview.
        var duration = PreviewDuration;
        var span = duration > 0 ? duration + Math.Max(1 / _project.Video.Fps, duration * .04) : 60;
        var scale = Math.Min(TimelineZoom.Maximum, TimelineViewportWidth / span);
        if (!double.IsFinite(scale) || scale <= 0) return;
        TimelineZoom.Value = scale;
        TimelinePixelsPerSecond = scale;
        UpdateTimelineViewport();
        TimelineOffsetSeconds = 0;
        StatusText.Text = duration > 0 ? $"顯示整條時間軸 · {FormatPreviewTime(duration)} · Ctrl+滾輪可局部放大" : "空白時間軸 · 顯示一分鐘範圍";
    }

    private void ShowGoToTimeDialog()
    {
        if (NavigationGestureActive) return;
        var dialog = new Window
        {
            Title = "跳至時間", Owner = this, Width = 420, SizeToContent = SizeToContent.Height,
            MinWidth = 340, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (Brush)FindResource("PanelBrush"), Foreground = Brushes.White
        };
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock
        {
            Text = $"輸入秒數、分:秒 或 時:分:秒，例如 12.5 或 01:12.5。\n時間軸總長 {PreviewDuration.ToString("0.#########", CultureInfo.InvariantCulture)} 秒。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12)
        });
        var input = new TextBox { Text = PlayheadSeconds.ToString("0.#########", CultureInfo.InvariantCulture), MinHeight = 30 };
        panel.Children.Add(input);
        var error = new TextBlock { Foreground = Brushes.Salmon, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 10) };
        panel.Children.Add(error);
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", IsCancel = true };
        var go = new Button { Content = "定位", IsDefault = true };
        row.Children.Add(cancel); row.Children.Add(go); panel.Children.Add(row); dialog.Content = panel;
        double target = 0;
        go.Click += (_, _) =>
        {
            if (!TimelineNavigation.TryParseTime(input.Text, out target) || target > PreviewDuration)
            { error.Text = "請輸入介於 0 與時間軸總長之間的有效時間。"; return; }
            dialog.DialogResult = true;
        };
        dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        if (dialog.ShowDialog() == true) NavigatePlayhead(target);
    }
}
