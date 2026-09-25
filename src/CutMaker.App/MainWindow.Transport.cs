using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace CutMaker.App;

public partial class MainWindow
{
    private Task _transportTask = Task.CompletedTask;

    // Route transport independently of focus left behind by ruler/slider capture.
    // Text entry, menus and non-transport buttons retain their native Space behavior.
    internal bool TryHandleTransportKey(Key key, ModifierKeys modifiers, DependencyObject? origin, bool repeat = false)
    {
        if (key != Key.Space || modifiers != ModifierKeys.None) return false;
        for (var item = origin; item is not null; item = InputParent(item))
        {
            if (ReferenceEquals(item, PreviewPlayButton) || ReferenceEquals(item, PreviewStopButton)) break;
            if (item is TextBoxBase or ComboBox or MenuItem or ButtonBase) return false;
        }
        if (repeat) return true;
        if (_panElement is not null && Mouse.MiddleButton == MouseButtonState.Pressed) return true;
        if (Mouse.LeftButton == MouseButtonState.Pressed &&
            (IsMarqueeSelecting || _pointerOriginal is not null || _previewScrubbing || _rangeDragOriginal is not null)) return true;
        // A handled/lost mouse-up must not leave a completed gesture swallowing every shortcut.
        EndMarqueeSelection(cancel: true);
        CancelPointerEdit();
        EndPreviewScrub(resumePlayback: false);
        for (var capture = Mouse.Captured as DependencyObject; capture is not null; capture = InputParent(capture))
        {
            if (!ReferenceEquals(capture, TimelineTimeRuler) && !ReferenceEquals(capture, PreviewSeekSlider)) continue;
            Mouse.Capture(null);
            break;
        }
        _transportTask = TogglePreviewPlaybackAsync();
        return true;
    }

    private static DependencyObject? InputParent(DependencyObject item) => item is Visual or Visual3D
        ? VisualTreeHelper.GetParent(item) : LogicalTreeHelper.GetParent(item);
}
