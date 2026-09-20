using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Input;

namespace CutMaker.App;

public partial class MainWindow
{
    private async Task RunImeTransportSmokeAsync(string folder)
    {
        var report = Path.Combine(folder, "ime-hotkey-result.txt");
        File.WriteAllText(report, $"Ruler IME enabled={InputMethod.GetIsInputMethodEnabled(TimelineTimeRuler)}; " +
            $"Play button IME enabled={InputMethod.GetIsInputMethodEnabled(PreviewPlayButton)}; " +
            $"Text input IME enabled={InputMethod.GetIsInputMethodEnabled(ClipStartBox)}.\n");
        // WPF's input manager wraps keys before PreviewKeyDown when IME is enabled.
        // The previous regression constructed only plain Key.Space, bypassing this path.
        var markIme = typeof(KeyEventArgs).GetMethod("MarkImeProcessed", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("WPF IME regression requires KeyEventArgs.MarkImeProcessed.");
        KeyEventArgs ImeSpace(UIElement source)
        {
            var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this), Environment.TickCount, Key.Space)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            markIme.Invoke(key, null);
            if (key.Key != Key.ImeProcessed || key.ImeProcessedKey != Key.Space)
                throw new InvalidOperationException("IME fixture does not match WPF's processed-key representation.");
            source.RaiseEvent(key);
            return key;
        }
        foreach (var surface in new UIElement[] { TimelineTimeRuler, FindLanes(TrackItems).First(), PreviewPane, PreviewSeekSlider })
        {
            if (InputMethod.GetIsInputMethodEnabled(surface))
                throw new InvalidOperationException($"Editing surface still enables IME: {surface.GetType().Name}.");
            PausePreview(); SeekPreview(.2);
            BeginPreviewScrub(); SeekPreview(.3); EndPreviewScrub();
            var first = ImeSpace(surface);
            await _transportTask;
            if (!first.Handled || !_previewPlaying)
                throw new InvalidOperationException("IME Space after ruler seek was ignored: Key=ImeProcessed, ImeProcessedKey=Space.");
            var deadline = DateTime.UtcNow.AddSeconds(4);
            while (PlayheadSeconds < .45 && DateTime.UtcNow < deadline) await Task.Delay(50);
            if (PlayheadSeconds < .45)
                throw new InvalidOperationException("IME Space changed UI state without advancing native playback.");
            var second = ImeSpace(surface);
            await _transportTask;
            if (!second.Handled || _previewPlaying)
                throw new InvalidOperationException("IME Space after ruler seek did not pause.");
        }
        foreach (var input in new UIElement[] { ClipStartBox, ProjectNameBox, FadeInCurveBox, RemoveClipButton })
        {
            var priorTask = _transportTask;
            ImeSpace(input);
            if (!ReferenceEquals(priorTask, _transportTask) || _previewPlaying)
                throw new InvalidOperationException($"IME Space was stolen from native input: {input.GetType().Name}.");
        }
        if (!InputMethod.GetIsInputMethodEnabled(ProjectNameBox) || !InputMethod.GetIsInputMethodEnabled(ClipStartBox))
            throw new InvalidOperationException("The hotkey fix must not disable Chinese text input.");
        File.AppendAllText(report,
            "PASS: IME-processed Space after seek starts, advances and pauses native playback from ruler/lane/preview/slider; editing surfaces disable IME; project-name and text inputs retain IME; text/choices/ordinary buttons do not trigger transport.\n");
    }
}
