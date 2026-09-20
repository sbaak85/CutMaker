using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace CutMaker.App;

public partial class MainWindow
{
    internal void CapturePendingPreviewLayoutSmoke(Action capture)
    {
        var preparing = _previewPreparing; var pending = _previewPlayWhenReady;
        try
        {
            _previewPreparing = true; _previewPlayWhenReady = true;
            RefreshPreviewControls(); UpdateLayout();
            foreach (var control in new FrameworkElement[] { PreviewPlayButton, PreviewStopButton, PreviewCancelButton, PreviewSeekSlider, PreviewTimeText })
            {
                var bounds = control.TransformToAncestor(PreviewPane).TransformBounds(new Rect(control.RenderSize));
                if (bounds.Left < 0 || bounds.Top < 0 || bounds.Right > PreviewPane.ActualWidth + 1 || bounds.Bottom > PreviewPane.ActualHeight + 1)
                    throw new InvalidOperationException($"Pending preview control is clipped: {control.Name} {bounds}.");
            }
            capture();
        }
        finally { _previewPreparing = preparing; _previewPlayWhenReady = pending; RefreshPreviewControls(); }
    }

    private async Task RunTransportSmokeAsync(string folder)
    {
        void Require(bool value, string reason)
        { if (!value) throw new InvalidOperationException("Transport regression: " + reason); }
        void Space(UIElement source)
        {
            var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this), Environment.TickCount, Key.Space)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            source.RaiseEvent(key);
            Require(key.Handled, "Space must be handled through the actual routed window shortcut.");
        }
        var project = JsonSerializer.Serialize(_project);
        var dirty = _dirty;
        var undoCount = _undo.Count; var redoCount = _redo.Count;
        PausePreview(); SeekPreview(.2);
        // Reproduce a release consumed by a captured control: transport must finalize the stale scrub.
        BeginPreviewScrub(); SeekPreview(.3);
        TimelineTimeRuler.CaptureMouse();
        Space(TimelineTimeRuler); await _transportTask;
        Require(_previewPlaying && !_previewScrubbing, "Ruler seek followed by Space must start playback and release scrub state.");
        Require(!TimelineTimeRuler.IsMouseCaptured, "Recovered ruler gesture must release capture so other controls remain clickable.");
        var deadline = DateTime.UtcNow.AddSeconds(4);
        while (PlayheadSeconds < .45 && DateTime.UtcNow < deadline) await Task.Delay(50);
        Require(PlayheadSeconds >= .45, "The native clock and playhead must actually advance after ruler + Space.");
        TryHandleTransportKey(Key.Space, ModifierKeys.None, TimelineTimeRuler, repeat: true);
        Require(_previewPlaying, "Holding Space must not repeatedly toggle playback.");
        Space(TimelineTimeRuler); await _transportTask;
        Require(!_previewPlaying, "Second Space pauses after ruler navigation.");

        BeginPreviewScrub(); SeekPreview(.4); EndPreviewScrub();
        Space(PreviewSeekSlider); await _transportTask;
        Require(_previewPlaying, "Preview seek slider retains the Space transport command.");
        Space(PreviewStopButton); await _transportTask;
        Require(!_previewPlaying && PlayheadSeconds > 0, "Space has consistent play/pause semantics even with Stop focused.");
        var lane = FindLanes(TrackItems).First();
        _pointerOriginal = _project.Clips.First(); _pointerLane = lane;
        Space(lane); await _transportTask;
        Require(_previewPlaying && _pointerOriginal is null, "A stale clip gesture cannot swallow Space after release.");
        Space(PreviewPlayButton); await _transportTask;
        Require(!_previewPlaying, "Transport button focus does not change Space semantics.");
        Require(!TryHandleTransportKey(Key.Space, ModifierKeys.None, ClipStartBox) &&
            !TryHandleTransportKey(Key.Space, ModifierKeys.None, FadeInCurveBox) &&
            !TryHandleTransportKey(Key.Space, ModifierKeys.None, RemoveClipButton),
            "Text, choices and ordinary buttons keep native Space behavior.");
        Require(!TryHandleTransportKey(Key.Space, ModifierKeys.Control, lane), "Modified Space is not intercepted.");

        SeekPreview(.2);
        InvalidatePreview();
        var prepare = PreparePreviewAsync();
        Require(_previewPreparing, "Pending-prepare fixture must be real.");
        Space(TimelineTimeRuler); await _transportTask;
        Require(_previewPlayWhenReady, "Space during preparation must queue playback instead of being ignored.");
        await prepare;
        Require(PreviewIsReady && _previewPlaying, "Queued playback must start after native media opens.");
        PausePreview();
        InvalidatePreview();
        prepare = PreparePreviewAsync(playWhenReady: true);
        Space(TimelineTimeRuler); await _transportTask;
        Require(!_previewPlayWhenReady, "A second transport intent can cancel pending autoplay.");
        await prepare;
        Require(PreviewIsReady && !_previewPlaying, "Canceled autoplay remains paused after preparation.");
        Require(project == JsonSerializer.Serialize(_project) && _dirty == dirty && _undo.Count == undoCount && _redo.Count == redoCount,
            "Transport does not modify the project, dirty flag or edit history.");
        File.WriteAllText(Path.Combine(folder, "transport-result.txt"),
            "PASS: routed Space after ruler/slider navigation; stale scrub and pointer release recovery; actual native clock advancement; consistent play/pause with transport button focus; repeat suppression; native text/choice/button keys; queued and canceled autoplay during real preparation; unchanged project and history.");
    }
}
