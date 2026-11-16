using System.Windows;
using System.Windows.Media;

namespace CutMaker.App;

internal sealed record LiveLayer(ImageSource Image, double Opacity, bool BlackFade);

/// <summary>Draws the composited timeline frame directly; layers are listed bottom to top.</summary>
internal sealed class LiveFrameView : FrameworkElement
{
    private IReadOnlyList<LiveLayer> _layers = [];
    private double _aspect = 16.0 / 9;

    internal IReadOnlyList<LiveLayer> Layers => _layers;

    internal void SetFrame(double aspect, IReadOnlyList<LiveLayer> layers)
    {
        _aspect = double.IsFinite(aspect) && aspect > 0 ? aspect : 16.0 / 9;
        _layers = layers;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext context)
    {
        if (ActualWidth < 1 || ActualHeight < 1) return;
        var width = Math.Min(ActualWidth, ActualHeight * _aspect);
        var height = width / _aspect;
        var canvas = new Rect((ActualWidth - width) / 2, (ActualHeight - height) / 2, width, height);
        context.DrawRectangle(Brushes.Black, null, canvas);
        context.PushClip(new RectangleGeometry(canvas));
        foreach (var layer in _layers)
        {
            var source = layer.Image;
            var scale = Math.Min(canvas.Width / Math.Max(1, source.Width), canvas.Height / Math.Max(1, source.Height));
            var picture = new Rect(canvas.X + (canvas.Width - source.Width * scale) / 2, canvas.Y + (canvas.Height - source.Height * scale) / 2,
                source.Width * scale, source.Height * scale);
            var opacity = Math.Clamp(layer.Opacity, 0, 1);
            // The renderer pads each layer to the full canvas: its bars cover lower layers too.
            if (!layer.BlackFade) context.PushOpacity(opacity);
            if (picture.Left > canvas.Left + .01)
            {
                context.DrawRectangle(Brushes.Black, null, new Rect(canvas.Left, canvas.Top, picture.Left - canvas.Left, canvas.Height));
                context.DrawRectangle(Brushes.Black, null, new Rect(picture.Right, canvas.Top, canvas.Right - picture.Right, canvas.Height));
            }
            if (picture.Top > canvas.Top + .01)
            {
                context.DrawRectangle(Brushes.Black, null, new Rect(canvas.Left, canvas.Top, canvas.Width, picture.Top - canvas.Top));
                context.DrawRectangle(Brushes.Black, null, new Rect(canvas.Left, picture.Bottom, canvas.Width, canvas.Bottom - picture.Bottom));
            }
            context.DrawImage(source, picture);
            if (!layer.BlackFade) context.Pop();
            else if (opacity < 1)
            {
                context.PushOpacity(1 - opacity);
                context.DrawRectangle(Brushes.Black, null, picture);
                context.Pop();
            }
        }
        context.Pop();
    }
}
