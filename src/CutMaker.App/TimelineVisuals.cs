using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using CutMaker.Core;

namespace CutMaker.App;

internal enum PointerEdit { Move, TrimStart, TrimEnd, FadeIn, FadeOut }

public sealed record TimelineClipView(string Id, string Name, MediaKind Kind, double Start, double Duration, double FadeIn = 0, double FadeOut = 0,
    ImageSource? Thumbnail = null, ImageSource? Waveform = null, double SourceIn = 0, double SourceDuration = 0,
    FadeSettings? FadeInSettings = null, FadeSettings? FadeOutSettings = null,
    double WaveformStart = 0, double WaveformDuration = 0, double TimeRatio = 1);
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
    public static readonly DependencyProperty IsCompactProperty = DependencyProperty.Register(
        nameof(IsCompact), typeof(bool), typeof(TimelineLane),
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
    private static readonly Pen SelectionPen = TimelineDrawing.Pen("#F0D89D", 1);
    private static readonly Brush AllowedFill = TimelineDrawing.Brush("#594CBCA5");
    private static readonly Brush DeniedFill = TimelineDrawing.Brush("#59DC6974");
    private static readonly Pen AllowedPen = TimelineDrawing.Pen("#80E1C8", 2);
    private static readonly Pen DeniedPen = TimelineDrawing.Pen("#FF919A", 2);

    private static readonly Brush ClipFill = TimelineDrawing.GoldGradient(false, false);
    private static readonly Pen ClipBorder = new(TimelineDrawing.GoldGradient(true, false), 1);
    private static readonly Pen SelectedClipBorder = TimelineDrawing.Pen("#FFE49A", 1.5);
    private static readonly Pen PrimaryClipBorder = TimelineDrawing.Pen("#FFF4CF", 2);
    private static readonly Brush SelectedClipFill = SelectionFill("#95804A", "#655432");
    private static readonly Brush PrimaryClipFill = SelectionFill("#B49B59", "#806937");
    private static Brush SelectionFill(string edge, string center)
    {
        var brush = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(1, 0) };
        foreach (var stop in new[] { (edge, 0d), (center, .3), (center, .7), (edge, 1d) })
            brush.GradientStops.Add(new((Color)ColorConverter.ConvertFromString(stop.Item1), stop.Item2));
        brush.Freeze(); return brush;
    }
    private double? _dropStart;
    private double _dropDuration;
    private bool _dropAllowed;
    private Clip? _fadePreview;
    private TimelineClipView? _hoverClip;
    private PointerEdit _hoverMode;
    private DrawingGroup? _clipDrawing;
    private (object? Clips, double Width, double Height, double Scale, double Offset, string? Selected,
        object? Selection, bool Locked, bool Compact, Clip? Fade, double Dpi)? _drawingKey;
    public IReadOnlyList<TimelineMoveGhost> MovePreviews { get; private set; } = [];

    public TimelineLane()
    {
        Focusable = true;
        InputMethod.SetIsInputMethodEnabled(this, false);
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
    public bool IsCompact
    {
        get => (bool)GetValue(IsCompactProperty);
        set => SetValue(IsCompactProperty, value);
    }
    public double FadeHandleHitHeight => IsCompact ? 9 : 17;
    internal double FadeHitRadius => IsCompact ? 7 : 10;
    internal double EdgeWidth(double duration) => Math.Min(IsCompact ? 8 : 10, duration * PixelsPerSecond / 4);
    internal PointerEdit HitEdit(double start, double duration, double fadeIn, double fadeOut, Point point)
    {
        var left = (start - OffsetSeconds) * PixelsPerSecond;
        var right = left + duration * PixelsPerSecond;
        var edge = EdgeWidth(duration);
        var inX = Math.Clamp(left + fadeIn * PixelsPerSecond, left + edge, right - edge);
        var outX = Math.Clamp(right - fadeOut * PixelsPerSecond, left + edge, right - edge);
        var inDistance = Math.Abs(point.X - inX); var outDistance = Math.Abs(point.X - outX);
        if (point.Y <= FadeHandleHitHeight && Math.Min(inDistance, outDistance) <= FadeHitRadius)
            return inDistance <= outDistance ? PointerEdit.FadeIn : PointerEdit.FadeOut;
        return Math.Abs(point.X - left) <= edge ? PointerEdit.TrimStart : Math.Abs(point.X - right) <= edge ? PointerEdit.TrimEnd : PointerEdit.Move;
    }
    public IReadOnlyList<string>? SelectedClipIds
    { get => (IReadOnlyList<string>?)GetValue(SelectedClipIdsProperty); set => SetValue(SelectedClipIdsProperty, value); }
    public double PlayheadSeconds { get => (double)GetValue(PlayheadSecondsProperty); set => SetValue(PlayheadSecondsProperty, value); }

    public void SetDropPreview(double? start, double duration, bool allowed)
    {
        _fadePreview = null;
        MovePreviews = [];
        _dropStart = start is >= 0 && double.IsFinite(start.Value) && duration > 0 && double.IsFinite(duration)
            ? start : null;
        _dropDuration = duration;
        _dropAllowed = allowed;
        InvalidateVisual();
    }

    public void SetMovePreviews(IReadOnlyList<TimelineMoveGhost> clips)
    {
        _fadePreview = null;
        _dropStart = null;
        MovePreviews = clips;
        InvalidateVisual();
    }

    public void SetFadePreview(Clip? candidate)
    {
        _dropStart = null;
        MovePreviews = [];
        _fadePreview = candidate;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        drawing.PushClip(new RectangleGeometry(bounds));
        var clips = Clips;
        var key = ((object?)clips, ActualWidth, ActualHeight, PixelsPerSecond, OffsetSeconds, SelectedClipId,
            (object?)SelectedClipIds, IsLocked, IsCompact, _fadePreview, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        if (_clipDrawing is null || _drawingKey != key)
        {
            _drawingKey = key;
            _clipDrawing = new DrawingGroup();
            using var content = _clipDrawing.Open();
            content.DrawRectangle(IsLocked ? LockedBrush : BackgroundBrush, null, bounds);
            TimelineDrawing.DrawGrid(content, ActualWidth, ActualHeight, PixelsPerSecond, OffsetSeconds);
            if (clips is null || clips.Count == 0)
                TimelineDrawing.DrawText(this, content, IsLocked ? "軌道已鎖定" : "將素材拖到這裡",
                12, TimelineDrawing.Muted, new Point(14, Math.Max(2, (ActualHeight - 18) / 2)), Math.Max(1, ActualWidth - 28));
            else
            foreach (var clip in VisibleClips(clips))
                // Keep the bound view and mouse capture intact. Only the held drag's fade is provisional;
                // asynchronous waveform updates can still replace the underlying bitmap normally.
                DrawClip(content, _fadePreview is { } candidate && candidate.Id == clip.Id
                    ? clip with { FadeIn = candidate.FadeIn?.Duration ?? 0, FadeOut = candidate.FadeOut?.Duration ?? 0,
                        FadeInSettings = candidate.FadeIn, FadeOutSettings = candidate.FadeOut }
                    : clip);
        }
        drawing.DrawDrawing(_clipDrawing);
        if (_hoverClip is { } hovered && !IsLocked && !IsMouseCaptured && _hoverMode != PointerEdit.Move && TryGetVisibleRect(hovered.Start, hovered.Duration, out var hoverBounds))
        {
            var left = (hovered.Start - OffsetSeconds) * PixelsPerSecond;
            var right = left + hovered.Duration * PixelsPerSecond;
            var edge = EdgeWidth(hovered.Duration);
            if (_hoverMode is PointerEdit.FadeIn or PointerEdit.FadeOut)
            {
                var x = _hoverMode == PointerEdit.FadeIn ? left + hovered.FadeIn * PixelsPerSecond : right - hovered.FadeOut * PixelsPerSecond;
                x = Math.Clamp(x, left + edge, right - edge);
                drawing.DrawRoundedRectangle(null, TimelineDrawing.PlayheadPen, new Rect(x - 6, hoverBounds.Top, 12, IsCompact ? 9 : 12), 2, 2);
            }
            else
            {
                var x = _hoverMode == PointerEdit.TrimStart ? left + 3 : right - 3;
                drawing.DrawLine(TimelineDrawing.PlayheadPen, new Point(x, hoverBounds.Top + 3), new Point(x, hoverBounds.Bottom - 3));
            }
        }

        if (_dropStart is double start && TryGetVisibleRect(start, _dropDuration, out var preview))
        {
            var allowed = _dropAllowed && !IsLocked;
            var outline = allowed ? AllowedPen : DeniedPen;
            drawing.DrawRectangle(allowed ? AllowedFill : DeniedFill, outline, preview);
            var startX = (start - OffsetSeconds) * PixelsPerSecond;
            if (startX >= 0 && startX <= ActualWidth)
                drawing.DrawLine(outline, new Point(startX, 0), new Point(startX, ActualHeight));
            if (preview.Width > 24)
                TimelineDrawing.DrawText(this, drawing,
                    $"{(allowed ? "放入" : "無法放入")} {TimelineDrawing.FormatTime(start)}", 12,
                    TimelineDrawing.Foreground, new Point(preview.Left + 8, preview.Top + (IsCompact ? 5 : 6)), preview.Width - 16);
        }

        foreach (var ghost in MovePreviews)
            if (TryGetVisibleRect(ghost.Start, ghost.Duration, out var boundsPreview))
            {
                drawing.DrawRectangle(AllowedFill, AllowedPen, boundsPreview);
                TimelineDrawing.DrawText(this, drawing, $"移動 {TimelineDrawing.FormatTime(ghost.Start, true)}", 11,
                    TimelineDrawing.Foreground, new Point(boundsPreview.Left + 6, boundsPreview.Top + (IsCompact ? 5 : 7)), boundsPreview.Width - 12);
            }

        var playheadX = (PlayheadSeconds - OffsetSeconds) * PixelsPerSecond;
        if (playheadX >= 0 && playheadX <= ActualWidth)
            drawing.DrawLine(TimelineDrawing.PlayheadPen, new Point(playheadX, 0), new Point(playheadX, ActualHeight));
        drawing.DrawLine(TimelineDrawing.Border, new Point(0, ActualHeight - .5), new Point(ActualWidth, ActualHeight - .5));
        drawing.Pop();
    }

    private IEnumerable<TimelineClipView> VisibleClips(IReadOnlyList<TimelineClipView> clips)
    {
        // Rows are sorted by start and cannot overlap. Binary search skips offscreen history.
        var low = 0; var high = clips.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (clips[middle].Start + clips[middle].Duration <= OffsetSeconds) low = middle + 1;
            else high = middle;
        }
        var end = OffsetSeconds + ActualWidth / PixelsPerSecond;
        for (var index = low; index < clips.Count && clips[index].Start < end; index++) yield return clips[index];
    }

    private void DrawClip(DrawingContext drawing, TimelineClipView clip)
    {
        if (!TryGetVisibleRect(clip.Start, clip.Duration, out var rect)) return;
        var selected = clip.Id == SelectedClipId || SelectedClipIds?.Contains(clip.Id) == true;
        var fullRect = new Rect((clip.Start - OffsetSeconds) * PixelsPerSecond, rect.Top,
            clip.Duration * PixelsPerSecond, rect.Height);
        drawing.DrawRectangle(clip.Id == SelectedClipId ? PrimaryClipFill : selected ? SelectedClipFill : ClipFill,
            clip.Id == SelectedClipId ? PrimaryClipBorder : selected ? SelectedClipBorder : ClipBorder, fullRect);
        // Thin edge grips make the two trim targets discoverable. Fade ranges remain visible when selected.
        var actualLeft = (clip.Start - OffsetSeconds) * PixelsPerSecond;
        var actualRight = (clip.Start + clip.Duration - OffsetSeconds) * PixelsPerSecond;
        if (rect.Width > 26 && (rect.Height > 50 || IsCompact))
        {
            var visualRect = IsCompact
                ? new Rect(rect.Left + 3, rect.Top + 2, Math.Max(1, rect.Width - 6), Math.Max(1, rect.Height - 4))
                : new Rect(rect.Left + 7, rect.Top + 42, Math.Max(1, rect.Width - 14), Math.Max(1, rect.Height - 46));
            drawing.PushClip(new RectangleGeometry(visualRect));
            if (IsCompact) drawing.PushOpacity(.42);
            if (clip.Waveform is not null && clip.SourceDuration > 0)
            {
                // The cached waveform spans the source. Use source coordinates so trimmed clips stay aligned.
                var sourceWidth = (clip.WaveformDuration > 0 ? clip.WaveformDuration : clip.SourceDuration) * PixelsPerSecond * clip.TimeRatio;
                drawing.DrawImage(clip.Waveform, new Rect(actualLeft + (clip.WaveformStart - clip.SourceIn) * PixelsPerSecond * clip.TimeRatio,
                    visualRect.Top, sourceWidth, visualRect.Height));
            }
            if (clip.Thumbnail is not null && clip.Kind != MediaKind.Audio)
                drawing.DrawImage(clip.Thumbnail, new Rect(visualRect.Left, visualRect.Top, Math.Min(visualRect.Width, visualRect.Height * 16 / 9), visualRect.Height));
            if (IsCompact) drawing.Pop();
            drawing.Pop();
        }
        if (clip.Id == SelectedClipId && rect.Width >= 18)
        {
            var gripInset = IsCompact ? 6 : 9;
            if (actualLeft >= 0) drawing.DrawLine(SelectionPen, new Point(actualLeft + 4, rect.Top + gripInset), new Point(actualLeft + 4, rect.Bottom - gripInset));
            if (actualRight <= ActualWidth) drawing.DrawLine(SelectionPen, new Point(actualRight - 4, rect.Top + gripInset), new Point(actualRight - 4, rect.Bottom - gripInset));
        }
        if (clip.FadeIn > 0)
            DrawFade(drawing, rect, actualLeft, clip.FadeIn, clip.FadeInSettings, fadeOut: false);
        if (clip.FadeOut > 0)
            DrawFade(drawing, rect, actualRight - clip.FadeOut * PixelsPerSecond, clip.FadeOut, clip.FadeOutSettings, fadeOut: true);
        if (rect.Width <= 18 || rect.Height <= 18) return;

        var textX = rect.Left + 8;
        var textWidth = rect.Width - 16;
        TimelineDrawing.DrawText(this, drawing, clip.Name, IsCompact ? 11 : 12, TimelineDrawing.Foreground,
            new Point(textX, rect.Top + (IsCompact ? 7 : 5)), textWidth);
        if (rect.Height >= 40)
            TimelineDrawing.DrawText(this, drawing,
                $"{TimelineDrawing.FormatTime(clip.Start)} · {TimelineDrawing.FormatTime(clip.Duration, true)}",
                10, TimelineDrawing.Secondary, new Point(textX, rect.Top + 25), textWidth);
        if (clip.Id == SelectedClipId && rect.Width >= 18)
        {
            var edge = EdgeWidth(clip.Duration);
            var fadeInHandle = Math.Clamp(actualLeft + clip.FadeIn * PixelsPerSecond, actualLeft + edge, actualRight - edge);
            var fadeOutHandle = Math.Clamp(actualRight - clip.FadeOut * PixelsPerSecond, actualLeft + edge, actualRight - edge);
            var handle = IsCompact ? 6 : 8;
            drawing.DrawRectangle(TimelineDrawing.PlayheadPen.Brush, null, new Rect(fadeInHandle - handle / 2, rect.Top + 1, handle, handle));
            drawing.DrawRectangle(TimelineDrawing.PlayheadPen.Brush, null, new Rect(fadeOutHandle - handle / 2, rect.Top + 1, handle, handle));
        }
    }

    private void DrawFade(DrawingContext drawing, Rect rect, double startX, double duration, FadeSettings? settings, bool fadeOut)
    {
        var width = duration * PixelsPerSecond;
        var left = Math.Max(0, startX);
        var right = Math.Min(ActualWidth, startX + width);
        if (right <= left) return;
        settings ??= new FadeSettings(duration);
        // Sample only the visible portion, so long fades and deep zooms stay inexpensive.
        var steps = Math.Clamp((int)Math.Ceiling((right - left) / 2), 1, 4096);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
            for (var index = 0; index <= steps; index++)
            {
                var x = left + (right - left) * index / steps;
                var progress = (x - startX) / width;
                var gain = FadeEnvelope.CurveValue(settings, fadeOut ? 1 - progress : progress);
                var point = new Point(x, rect.Bottom - 3 - gain * (rect.Height - 6));
                if (index == 0) context.BeginFigure(point, false, false);
                else context.LineTo(point, true, false);
            }
        geometry.Freeze();
        drawing.DrawGeometry(null, TimelineDrawing.FadePen, geometry);
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
        var inset = IsCompact ? 2 : 5;
        rect = new Rect(left, inset, right - left, ActualHeight - inset * 2);
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
            var mode = hit is null ? PointerEdit.Move : HitEdit(hit.Start, hit.Duration, hit.FadeIn, hit.FadeOut, point);
            if (_hoverClip != hit || _hoverMode != mode) { _hoverClip = hit; _hoverMode = mode; InvalidateVisual(); }
            Cursor = IsLocked || hit is null ? Cursors.Arrow : mode == PointerEdit.Move ? Cursors.SizeAll : Cursors.SizeWE;
        }
        var tooltip = hit is null ? null
            : $"{hit.Name}\n起點 {TimelineDrawing.FormatTime(hit.Start, true)} · 長度 {TimelineDrawing.FormatTime(hit.Duration, true)}\n拖曳中央移動 · 兩端修剪 · 上緣金色方塊調整 Fade";
        if (!Equals(ToolTip, tooltip)) ToolTip = tooltip;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hoverClip = null; InvalidateVisual();
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
        InputMethod.SetIsInputMethodEnabled(this, false);
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

    public static Brush GoldGradient(bool border, bool selected)
    {
        var brush = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = border ? new(1, 1) : new(1, 0) };
        var edge = border ? (selected ? "#FFE7AC" : "#BDAB76") : "#514C35";
        var center = border ? (selected ? "#A58B50" : "#665D3D") : "#292A22";
        brush.GradientStops.Add(new((Color)ColorConverter.ConvertFromString(edge), 0));
        brush.GradientStops.Add(new((Color)ColorConverter.ConvertFromString(center), .30));
        brush.GradientStops.Add(new((Color)ColorConverter.ConvertFromString(center), .70));
        brush.GradientStops.Add(new((Color)ColorConverter.ConvertFromString(edge), 1));
        brush.Freeze(); return brush;
    }
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
