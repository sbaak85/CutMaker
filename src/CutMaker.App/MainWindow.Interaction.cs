using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CutMaker.App;

public partial class MainWindow
{
    private UIElement? _panElement;
    private Point _panOrigin;
    private double _panOffset, _panVertical, _panScale;
    private void InitializeInteraction()
    {
        PreviewMouseDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Middle || e.OriginalSource is not (TimelineLane or TimelineRuler)) return;
            CancelPointerEdit(); EndMarqueeSelection(cancel: true); EndPreviewScrub(resumePlayback: false);
            _panElement = (UIElement)e.OriginalSource;
            _panOrigin = e.GetPosition(TimelineContent); _panOffset = TimelineOffsetSeconds;
            _panVertical = TimelineTrackScroll.VerticalOffset; _panScale = TimelinePixelsPerSecond;
            _panElement.CaptureMouse(); ((FrameworkElement)_panElement).Cursor = Cursors.ScrollAll; e.Handled = true;
        };
        PreviewMouseMove += (_, e) =>
        {
            if (_panElement is null) return;
            if (e.MiddleButton != MouseButtonState.Pressed) { CancelTimelinePan(); return; }
            var current = e.GetPosition(TimelineContent);
            PanTimeline(_panOffset - (current.X - _panOrigin.X) / _panScale);
            TimelineTrackScroll.ScrollToVerticalOffset(_panVertical - (current.Y - _panOrigin.Y));
            MediaWorkScheduler.NotifyInteraction(); e.Handled = true;
        };
        PreviewMouseUp += (_, e) =>
        { if (e.ChangedButton == MouseButton.Middle && _panElement is not null) { CancelTimelinePan(); e.Handled = true; } };
        AddHandler(Mouse.LostMouseCaptureEvent, new MouseEventHandler((_, e) =>
        { if (ReferenceEquals(e.OriginalSource, _panElement)) CancelTimelinePan(); }), true);
        Deactivated += (_, _) => { CancelTimelinePan(); CancelPointerEdit(); };
        TimelineTrackScroll.ScrollChanged += (_, _) => QueueWaveDetails();
    }
    private void CancelTimelinePan()
    {
        var element = _panElement; _panElement = null;
        if (element is FrameworkElement target) target.Cursor = Cursors.Arrow;
        if (element?.IsMouseCaptured == true) element.ReleaseMouseCapture();
    }
    private void PanTimeline(double seconds) => TimelineOffsetSeconds = Math.Clamp(seconds, 0, TimelineHorizontalScroll.Maximum);
    internal bool TryPanTimelineWheel(int delta, ModifierKeys modifiers)
    {
        if (modifiers != ModifierKeys.Shift) return false;
        PanTimeline(TimelineOffsetSeconds - delta / 120.0 * 80 / TimelinePixelsPerSecond);
        MediaWorkScheduler.NotifyInteraction(); return true;
    }
    private void ShowPointerFeedback(TimelineLane lane, Point point, string text)
    {
        var position = lane.TranslatePoint(point, TimelineContent);
        TimelineDragHintText.Text = text; TimelineDragHint.Visibility = Visibility.Visible;
        Canvas.SetLeft(TimelineDragHint, Math.Clamp(position.X + 16, 0, Math.Max(0, TimelineContent.ActualWidth - 320)));
        Canvas.SetTop(TimelineDragHint, Math.Clamp(position.Y + 16, 0, Math.Max(0, TimelineContent.ActualHeight - 56)));
        if (_snapGuideTime is not { } time) { TimelineSnapLine.Visibility = TimelineSnapLabel.Visibility = Visibility.Collapsed; return; }
        var origin = lane.TranslatePoint(new Point(), TimelineContent);
        var x = origin.X + (time - TimelineOffsetSeconds) * TimelinePixelsPerSecond;
        if (x < origin.X || x > TimelineContent.ActualWidth) { TimelineSnapLine.Visibility = TimelineSnapLabel.Visibility = Visibility.Collapsed; return; }
        TimelineSnapLine.X1 = TimelineSnapLine.X2 = x; TimelineSnapLine.Y2 = TimelineContent.ActualHeight;
        TimelineSnapLine.Visibility = TimelineSnapLabel.Visibility = Visibility.Visible;
        TimelineSnapText.Text = $"{_snapGuideLabel} {time:0.###} 秒";
        Canvas.SetLeft(TimelineSnapLabel, Math.Clamp(x + 4, origin.X, Math.Max(origin.X, TimelineContent.ActualWidth - 160)));
        Canvas.SetTop(TimelineSnapLabel, 2);
    }
    private void HidePointerFeedback()
    {
        if (TimelineDragHint is null) return;
        TimelineDragHint.Visibility = TimelineSnapLine.Visibility = TimelineSnapLabel.Visibility = Visibility.Collapsed;
        _snapGuideTime = null; _snapGuideLabel = null;
    }
    private void Shortcuts_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Window { Title = "快捷鍵與滑鼠操作", Owner = this, Width = 570, Height = 610, MinWidth = 390, MinHeight = 300,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = (Brush)FindResource("PanelBrush"), Foreground = Brushes.White };
        dialog.Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = new TextBlock
        {
            Margin = new Thickness(22), TextWrapping = TextWrapping.Wrap, FontSize = 14,
            Text = "播放／暫停：空白鍵\n切割：B\n修剪開頭／結尾至播放頭：[／]\n空白處拖曳定位；Ctrl+左鍵拖曳框選\n試聽接點：Ctrl+Shift+Space\n\n復原／重做：Ctrl+Z／Ctrl+Y\n剪下／複製／貼上：Ctrl+X／C／V\n全選：Ctrl+A\n複製到尾端：Ctrl+D\n刪除：Delete；同軌波紋刪除：Shift+Delete\n\n縮放：Ctrl+滾輪；顯示全部：Ctrl+0\n水平捲動：Shift+滾輪\n平移時間軸：按住中鍵拖曳\n精細拖曳：拖動時按住 Shift\n吸附開關：C；暫停吸附：按住 Alt\n\n選取素材開頭／結尾：W／S\n前後一格：A／D 或 ←／→；前後一秒：Shift+←／→\n前後片段邊界：Alt+←／→\n起點／終點：Home／End；精確定位：Ctrl+G\n\nFade：拖曳金色方塊，右鍵設定秒數／曲線／預設\n取消拖曳或框選：Esc\n\n儲存／另存：Ctrl+S／Ctrl+Shift+S\n新增／開啟／匯入：Ctrl+N／Ctrl+O／Ctrl+I\n操作說明：F1\n\n剪輯快捷鍵以時間軸焦點為主；文字欄位保留原本輸入操作。"
        } };
        dialog.ShowDialog();
    }
}
