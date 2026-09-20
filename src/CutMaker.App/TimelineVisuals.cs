using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using CutMaker.Core;

namespace CutMaker.App;

public sealed record TimelineClipView(string Id, string Name, MediaKind Kind, double Start, double Duration, double FadeIn = 0, double FadeOut = 0,
    ImageSource? Thumbnail = null, ImageSource? Waveform = null, double SourceIn = 0, double SourceDuration = 0);
public sealed record TimelineMoveGhost(string Id, double Start, double Duration);

/// <summary>A viewport-sized, retained-data lane. Drawing never changes clip timing or source media.</summary>
public sealed class TimelineLane : FrameworkElement
{
    public static readonly DependencyProperty ClipsProperty = DependencyProperty.Register(
        nameof(Clips), typeof(IReadOnlyList<TimelineClipView>), typeof(TimelineLane),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty PixelsPerSecondProperty = DependencyProperty.Register(
        nameof(PixelsPerSecond), typeof(double), typeof(TimelineLane),
        new FrameworkPropertyMetadata(40d, FrameworkPropertyMetadataOptions.AffectsRender), TimelineDrawing.IsPositiveFinite);
    public static readonly DependencyProperty OffsetSecondsProperty = DependencyProperty.Register(
        nameof(OffsetSeconds), typeof(double), typeof(TimelineLane),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender), TimelineDrawing.IsNonnegativeFinite);
    public static readonly DependencyProperty SelectedClipIdProperty = DependencyProperty.Register(
        nameof(SelectedClipId), typeof(string), typeof(TimelineLane),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IsLockedProperty = DependencyProperty.Register(
        nameof(IsLocked), typeof(bool), typeof(TimelineLane),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SelectedClipIdsProperty = DependencyProperty.Register(
        nameof(SelectedClipIds), typeof(IReadOnlyList<string>), typeof(TimelineLane),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty PlayheadSecondsProperty = DependencyProperty.Register(
        nameof(PlayheadSeconds), typeof(double), typeof(TimelineLane),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender), TimelineDrawing.IsNonnegativeFinite);

    private static readonly Brush BackgroundBrush = TimelineDrawing.Brush("#131A24");
    private static readonly Brush LockedBrush = TimelineDrawing.Brush("#1A1D24");
    private static readonly Brush VideoBrush = TimelineDrawing.Brush("#253D62");
    private static readonly Brush AudioBrush = TimelineDrawing.Brush("#174C50");
    private static readonly Brush ImageBrush = TimelineDrawing.Brush("#453654");
    private static readonly Pen VideoPen = TimelineDrawing.Pen("#5B8DC3");
    private static readonly Pen AudioPen = TimelineDrawing.Pen("#53BDB7");
    private static readonly Pen ImagePen = TimelineDrawing.Pen("#A88CC5");
    private static readonly Pen SelectionPen = TimelineDrawing.Pen("#E9F5FF", 2);
    private static readonly Brush AllowedFill = TimelineDrawing.Brush("#594CBCA5");
    private static readonly Brush DeniedFill = TimelineDrawing.Brush("#59DC6974");
    private static readonly Pen AllowedPen = TimelineDrawing.Pen("#80E1C8", 2);
    private static readonly Pen DeniedPen = TimelineDrawing.Pen("#FF919A", 2);

    private double? _dropStart;
    private double _dropDuration;
    private bool _dropAllowed;
    public IReadOnlyList<TimelineMoveGhost> MovePreviews { get; private set; } = [];

    public TimelineLane()
    {
        Focusable = true;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
    }

    public IReadOnlyList<TimelineClipView>? Clips
    {
        get => (IReadOnlyList<TimelineClipView>?)GetValue(ClipsProperty);
        set => SetValue(ClipsProperty, value);
    }
    public double PixelsPerSecond
    {
        get => (double)GetValue(PixelsPerSecondProperty);
        set => SetValue(PixelsPerSecondProperty, value);
    }
    public double OffsetSeconds
    {
        get => (double)GetValue(OffsetSecondsProperty);
        set => SetValue(OffsetSecondsProperty, value);
    }
    public string? SelectedClipId
    {
        get => (string?)GetValue(SelectedClipIdProperty);
        set => SetValue(SelectedClipIdProperty, value);
    }
    public bool IsLocked
    {
        get => (bool)GetValue(IsLockedProperty);
        set => SetValue(IsLockedProperty, value);
    }
    public IReadOnlyList<string>? SelectedClipIds
    { get => (IReadOnlyList<string>?)GetValue(SelectedClipIdsProperty); set => SetValue(SelectedClipIdsProperty, value); }
    public double PlayheadSeconds { get => (double)GetValue(PlayheadSecondsProperty); set => SetValue(PlayheadSecondsProperty, value); }

    public void SetDropPreview(double? start, double duration, bool allowed)
    {
        MovePreviews = [];
        _dropStart = start is >= 0 && double.IsFinite(start.Value) && duration > 0 && double.IsFinite(duration)
            ? start : null;
        _dropDuration = duration;
        _dropAllowed = allowed;
        InvalidateVisual();
    }

    public void SetMovePreviews(IReadOnlyList<TimelineMoveGhost> clips)
    {
        _dropStart = null;
        MovePreviews = clips;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        drawing.PushClip(new RectangleGeometry(bounds));
        drawing.DrawRectangle(IsLocked ? LockedBrush : BackgroundBrush, null, bounds);
        TimelineDrawing.DrawGrid(drawing, ActualWidth, ActualHeight, PixelsPerSecond, OffsetSeconds);

        var clips = Clips;
        if (clips is null || clips.Count == 0)
            TimelineDrawing.DrawText(this, drawing, IsLocked ? "軌道已鎖定" : "將素材拖到這裡",
                12, TimelineDrawing.Muted, new Point(14, Math.Max(2, (ActualHeight - 18) / 2)), Math.Max(1, ActualWidth - 28));
        else
            foreach (var clip in clips)
                DrawClip(drawing, clip);

        if (_dropStart is double start && TryGetVisibleRect(start, _dropDuration, out var preview))
        {
            var allowed = _dropAllowed && !IsLocked;
            var outline = allowed ? AllowedPen : DeniedPen;
            drawing.DrawRoundedRectangle(allowed ? AllowedFill : DeniedFill, outline, preview, 4, 4);
            var startX = (start - OffsetSeconds) * PixelsPerSecond;
            if (startX >= 0 && startX <= ActualWidth)
                drawing.DrawLine(outline, new Point(startX, 0), new Point(startX, ActualHeight));
            if (preview.Width > 24)
                TimelineDrawing.DrawText(this, drawing,
                    $"{(allowed ? "放入" : "無法放入")} {TimelineDrawing.FormatTime(start)}", 12,
                    TimelineDrawing.Foreground, new Point(preview.Left + 8, preview.Top + 6), preview.Width - 16);
        }

        foreach (var ghost in MovePreviews)
            if (TryGetVisibleRect(ghost.Start, ghost.Duration, out var boundsPreview))
            {
                drawing.DrawRoundedRectangle(AllowedFill, AllowedPen, boundsPreview, 4, 4);
                TimelineDrawing.DrawText(this, drawing, $"移動 {TimelineDrawing.FormatTime(ghost.Start, true)}", 11,
                    TimelineDrawing.Foreground, new Point(boundsPreview.Left + 6, boundsPreview.Top + 7), boundsPreview.Width - 12);
            }

        var playheadX = (PlayheadSeconds - OffsetSeconds) * PixelsPerSecond;
        if (playheadX >= 0 && playheadX <= ActualWidth)
            drawing.DrawLine(TimelineDrawing.PlayheadPen, new Point(playheadX, 0), new Point(playheadX, ActualHeight));
        drawing.DrawLine(TimelineDrawing.Border, new Point(0, ActualHeight - .5), new Point(ActualWidth, ActualHeight - .5));
        drawing.Pop();
    }

    private void DrawClip(DrawingContext drawing, TimelineClipView clip)
    {
        if (!TryGetVisibleRect(clip.Start, clip.Duration, out var rect)) return;
        var fill = clip.Kind switch { MediaKind.Audio => AudioBrush, MediaKind.Image => ImageBrush, _ => VideoBrush };
        var outline = clip.Kind switch { MediaKind.Audio => AudioPen, MediaKind.Image => ImagePen, _ => VideoPen };
        drawing.DrawRoundedRectangle(fill, clip.Id == SelectedClipId || SelectedClipIds?.Contains(clip.Id) == true ? SelectionPen : outline, rect, 4, 4);
        // Thin edge grips make the two trim targets discoverable. Fade ranges remain visible when selected.
        var actualLeft = (clip.Start - OffsetSeconds) * PixelsPerSecond;
        var actualRight = (clip.Start + clip.Duration - OffsetSeconds) * PixelsPerSecond;
        if (rect.Width > 26 && rect.Height > 50)
        {
            var visualRect = new Rect(rect.Left + 7, rect.Top + 42, Math.Max(1, rect.Width - 14), Math.Max(1, rect.Height - 46));
            drawing.PushClip(new RectangleGeometry(visualRect));
            if (clip.Waveform is not null && clip.SourceDuration > 0)
            {
                // The cached waveform spans the source. Use source coordinates so trimmed clips stay aligned.
                var sourceWidth = clip.SourceDuration * PixelsPerSecond;
                drawing.DrawImage(clip.Waveform, new Rect(actualLeft - clip.SourceIn * PixelsPerSecond,
                    visualRect.Top, sourceWidth, visualRect.Height));
            }
            if (clip.Thumbnail is not null && clip.Kind != MediaKind.Audio)
                drawing.DrawImage(clip.Thumbnail, new Rect(visualRect.Left, visualRect.Top, Math.Min(visualRect.Width, visualRect.Height * 16 / 9), visualRect.Height));
            drawing.Pop();
        }
        if (clip.Id == SelectedClipId && rect.Width >= 18)
        {
            if (actualLeft >= 0) drawing.DrawLine(SelectionPen, new Point(actualLeft + 4, rect.Top + 9), new Point(actualLeft + 4, rect.Bottom - 9));
            if (actualRight <= ActualWidth) drawing.DrawLine(SelectionPen, new Point(actualRight - 4, rect.Top + 9), new Point(actualRight - 4, rect.Bottom - 9));
        }
        if (clip.FadeIn > 0)
            drawing.DrawLine(TimelineDrawing.FadePen, new Point(actualLeft, rect.Bottom - 3),
                new Point(actualLeft + clip.FadeIn * PixelsPerSecond, rect.Top + 3));
        if (clip.FadeOut > 0)
            drawing.DrawLine(TimelineDrawing.FadePen, new Point(actualRight - clip.FadeOut * PixelsPerSecond, rect.Top + 3),
                new Point(actualRight, rect.Bottom - 3));
        if (rect.Width <= 18 || rect.Height <= 18) return;

        var textX = rect.Left + 8;
        var textWidth = rect.Width - 16;
        TimelineDrawing.DrawText(this, drawing, clip.Name, 12, TimelineDrawing.Foreground,
            new Point(textX, rect.Top + 5), textWidth);
        if (rect.Height >= 40)
            TimelineDrawing.DrawText(this, drawing,
                $"{TimelineDrawing.FormatTime(clip.Start)} · {TimelineDrawing.FormatTime(clip.Duration, true)}",
                10, TimelineDrawing.Secondary, new Point(textX, rect.Top + 25), textWidth);
        if (clip.Id == SelectedClipId && rect.Width >= 18)
        {
            var edge = Math.Min(8, clip.Duration * PixelsPerSecond / 4);
            var fadeInHandle = Math.Clamp(actualLeft + clip.FadeIn * PixelsPerSecond, actualLeft + edge, actualRight - edge);
            var fadeOutHandle = Math.Clamp(actualRight - clip.FadeOut * PixelsPerSecond, actualLeft + edge, actualRight - edge);
            drawing.DrawRectangle(TimelineDrawing.PlayheadPen.Brush, null, new Rect(fadeInHandle - 4, rect.Top + 1, 8, 8));
            drawing.DrawRectangle(TimelineDrawing.PlayheadPen.Brush, null, new Rect(fadeOutHandle - 4, rect.Top + 1, 8, 8));
        }
    }

    private bool TryGetVisibleRect(double start, double duration, out Rect rect)
    {
        rect = Rect.Empty;
        if (!double.IsFinite(start) || !double.IsFinite(duration) || duration <= 0 || ActualHeight <= 10) return false;
        var end = start + duration;
        var viewportEnd = OffsetSeconds + ActualWidth / PixelsPerSecond;
        if (!double.IsFinite(end) || end <= OffsetSeconds || start >= viewportEnd) return false;
        var left = Math.Max(0, (Math.Max(start, OffsetSeconds) - OffsetSeconds) * PixelsPerSecond);
        var right = Math.Min(ActualWidth, (Math.Min(end, viewportEnd) - OffsetSeconds) * PixelsPerSecond);
        if (!double.IsFinite(left) || !double.IsFinite(right) || right <= left) return false;
        rect = new Rect(left, 5, right - left, ActualHeight - 10);
        return true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var point = e.GetPosition(this);
        var time = OffsetSeconds + point.X / PixelsPerSecond;
        var hit = Clips?.LastOrDefault(clip => time >= clip.Start && time < clip.Start + clip.Duration);
        if (!IsMouseCaptured)
        {
            var edgeWidth = hit is null ? 0 : Math.Min(8, hit.Duration * PixelsPerSecond / 4);
            var nearEdge = hit is not null && (Math.Abs((time - hit.Start) * PixelsPerSecond) <= edgeWidth ||
                Math.Abs((hit.Start + hit.Duration - time) * PixelsPerSecond) <= edgeWidth);
            var nearFade = false;
            if (hit is not null && point.Y <= 17)
            {
                var left = (hit.Start - OffsetSeconds) * PixelsPerSecond;
                var right = left + hit.Duration * PixelsPerSecond;
                var fadeIn = Math.Clamp(left + hit.FadeIn * PixelsPerSecond, left + edgeWidth, right - edgeWidth);
                var fadeOut = Math.Clamp(right - hit.FadeOut * PixelsPerSecond, left + edgeWidth, right - edgeWidth);
                nearFade = Math.Abs(point.X - fadeIn) <= 6 || Math.Abs(point.X - fadeOut) <= 6;
            }
            Cursor = IsLocked || hit is null ? Cursors.Arrow : nearEdge || nearFade ? Cursors.SizeWE : Cursors.SizeAll;
        }
        var tooltip = hit is null ? null
            : $"{hit.Name}\n起點 {TimelineDrawing.FormatTime(hit.Start, true)} · 長度 {TimelineDrawing.FormatTime(hit.Duration, true)}\n拖曳中央移動 · 兩端修剪 · 上緣金色方塊調整 Fade";
        if (!Equals(ToolTip, tooltip)) ToolTip = tooltip;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        ToolTip = null;
        base.OnMouseLeave(e);
    }
}

public sealed class TimelineRuler : FrameworkElement
{
    public static readonly DependencyProperty PixelsPerSecondProperty = DependencyProperty.Register(
        nameof(PixelsPerSecond), typeof(double), typeof(TimelineRuler),
        new FrameworkPropertyMetadata(40d, FrameworkPropertyMetadataOptions.AffectsRender), TimelineDrawing.IsPositiveFinite);
    public static readonly DependencyProperty OffsetSecondsProperty = DependencyProperty.Register(
        nameof(OffsetSeconds), typeof(double), typeof(TimelineRuler),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender), TimelineDrawing.IsNonnegativeFinite);
    public static readonly DependencyProperty PlayheadSecondsProperty = DependencyProperty.Register(
        nameof(PlayheadSeconds), typeof(double), typeof(TimelineRuler),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender), TimelineDrawing.IsNonnegativeFinite);
    private static readonly Brush BackgroundBrush = TimelineDrawing.Brush("#1B2330");

    public TimelineRuler()
    {
        Focusable = true;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
    }

    public double PixelsPerSecond
    {
        get => (double)GetValue(PixelsPerSecondProperty);
        set => SetValue(PixelsPerSecondProperty, value);
    }
    public double OffsetSeconds
    {
        get => (double)GetValue(OffsetSecondsProperty);
        set => SetValue(OffsetSecondsProperty, value);
    }
    public double PlayheadSeconds { get => (double)GetValue(PlayheadSecondsProperty); set => SetValue(PlayheadSecondsProperty, value); }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        drawing.PushClip(new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight)));
        drawing.DrawRectangle(BackgroundBrush, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var major = TimelineDrawing.MajorInterval(PixelsPerSecond);
        TimelineDrawing.VisitTicks(ActualWidth, PixelsPerSecond, OffsetSeconds, major / 5, (time, x, index) =>
        {
            var isMajor = index % 5 == 0;
            drawing.DrawLine(TimelineDrawing.Border, new Point(x, Math.Max(0, ActualHeight - (isMajor ? 10 : 5))),
                new Point(x, ActualHeight));
            if (isMajor && x < ActualWidth - 8)
                TimelineDrawing.DrawText(this, drawing, TimelineDrawing.FormatTime(time, major < 1),
                    10, TimelineDrawing.Secondary, new Point(x + 5, 3), Math.Min(110, ActualWidth - x - 5));
        });
        var playheadX = (PlayheadSeconds - OffsetSeconds) * PixelsPerSecond;
        if (playheadX >= 0 && playheadX <= ActualWidth)
        {
            drawing.DrawLine(TimelineDrawing.PlayheadPen, new Point(playheadX, 0), new Point(playheadX, ActualHeight));
            var marker = new StreamGeometry();
            using (var context = marker.Open())
            {
                context.BeginFigure(new Point(playheadX - 5, 0), true, true);
                context.LineTo(new Point(playheadX + 5, 0), true, false);
                context.LineTo(new Point(playheadX, 8), true, false);
            }
            drawing.DrawGeometry(TimelineDrawing.PlayheadPen.Brush, null, marker);
        }
        drawing.DrawLine(TimelineDrawing.Border, new Point(0, ActualHeight - .5), new Point(ActualWidth, ActualHeight - .5));
        drawing.Pop();
    }
}

internal static class TimelineDrawing
{
    private static readonly Typeface Typeface = new(new FontFamily("Microsoft JhengHei UI, Segoe UI"),
        FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    public static readonly Brush Foreground = Brush("#ECF4FF");
    public static readonly Brush Secondary = Brush("#C5D3E2");
    public static readonly Brush Muted = Brush("#7E8CA2");
    public static readonly Pen Border = Pen("#354357");
    public static readonly Pen PlayheadPen = Pen("#FFCC71", 1.5);
    public static readonly Pen FadePen = Pen("#D4E5FA", 1);
    private static readonly Pen GridLine = Pen("#233043");

    public static bool IsPositiveFinite(object value) => value is double number && double.IsFinite(number) && number > 0;
    public static bool IsNonnegativeFinite(object value) => value is double number && double.IsFinite(number) && number >= 0;

    public static Brush Brush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    public static Pen Pen(string color, double thickness = 1)
    {
        var pen = new Pen(Brush(color), thickness);
        pen.Freeze();
        return pen;
    }

    public static string FormatTime(double seconds, bool fractions = false)
    {
        if (!double.IsFinite(seconds) || seconds < 0) return "--:--";
        if (fractions) seconds = Math.Round(seconds, 1);
        var hours = Math.Floor(seconds / 3600);
        var minutes = Math.Floor(seconds / 60) % 60;
        var second = seconds % 60;
        var secondsText = fractions ? second.ToString("00.0", CultureInfo.InvariantCulture)
            : Math.Floor(second).ToString("00", CultureInfo.InvariantCulture);
        return hours >= 1 ? $"{hours:00}:{minutes:00}:{secondsText}" : $"{minutes:00}:{secondsText}";
    }

    public static void DrawText(Visual owner, DrawingContext drawing, string text, double size, Brush brush, Point position, double maxWidth)
    {
        if (!double.IsFinite(maxWidth) || maxWidth < 1) return;
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            Typeface, size, brush, VisualTreeHelper.GetDpi(owner).PixelsPerDip)
        {
            MaxTextWidth = maxWidth,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis
        };
        drawing.DrawText(formatted, position);
    }

    public static double MajorInterval(double pixelsPerSecond)
    {
        var target = 85 / pixelsPerSecond;
        var scale = Math.Pow(10, Math.Floor(Math.Log10(target)));
        var normalized = target / scale;
        return (normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10) * scale;
    }

    public static void DrawGrid(DrawingContext drawing, double width, double height, double pixelsPerSecond, double offset)
        => VisitTicks(width, pixelsPerSecond, offset, MajorInterval(pixelsPerSecond),
            (_, x, _) => drawing.DrawLine(GridLine, new Point(x, 0), new Point(x, height)));

    public static void VisitTicks(double width, double pixelsPerSecond, double offset, double interval, Action<double, double, long> visitor)
    {
        if (!double.IsFinite(interval) || interval <= 0) return;
        var first = Math.Ceiling(offset / interval);
        if (!double.IsFinite(first) || first > long.MaxValue - 10_000) return;
        var firstIndex = (long)first;
        // Work is proportional to the visible viewport, never to source duration.
        for (var i = 0; i < 10_000; i++)
        {
            var index = firstIndex + i;
            var time = index * interval;
            var x = (time - offset) * pixelsPerSecond;
            if (!double.IsFinite(x) || x > width) break;
            if (x >= 0) visitor(time, x, index);
        }
    }
}
