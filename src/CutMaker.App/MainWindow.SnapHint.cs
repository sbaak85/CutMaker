using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace CutMaker.App;

public partial class MainWindow
{
    private int _snapHintVersion;
    private void ShowSnapHint(bool enabled)
    {
        var version = ++_snapHintVersion;
        SnapToggleHint.BeginAnimation(UIElement.OpacityProperty, null);
        SnapToggleHint.Opacity = 1;
        SnapToggleHintText.Text = enabled ? "吸附已開啟" : "吸附已關閉";
        SnapToggleHint.Background = TimelineDrawing.Brush(enabled ? "#72F598" : "#E7E9EC");
        SnapToggleHint.BorderBrush = TimelineDrawing.Brush(enabled ? "#A5FFBF" : "#FAFBFC");
        SnapToggleHintText.Foreground = TimelineDrawing.Brush("#17231C");
        SnapToggleHint.Visibility = Visibility.Visible;
        SnapToggleHint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = SnapToggleHint.DesiredSize.Width;
        var left = TrackHeaderWidth + 8;
        var x = left + (PlayheadSeconds - TimelineOffsetSeconds) * TimelinePixelsPerSecond + 12;
        Canvas.SetLeft(SnapToggleHint, Math.Clamp(x, left, Math.Max(left, TimelineContent.ActualWidth - width - 18)));
        Canvas.SetTop(SnapToggleHint, 0);
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromSeconds(.2)) { BeginTime = TimeSpan.FromSeconds(1) };
        fade.Completed += (_, _) => { if (version == _snapHintVersion) SnapToggleHint.Visibility = Visibility.Collapsed; };
        SnapToggleHint.BeginAnimation(UIElement.OpacityProperty, fade);
    }
}