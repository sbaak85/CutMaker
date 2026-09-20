using System.Windows;
using System.Windows.Controls;
using CutMaker.Core;

namespace CutMaker.App;

public partial class MainWindow
{
    private bool _refreshingEffects;
    private sealed record VideoFadeChoice(VideoFadeMode Mode, string Name);

    private void RefreshEffectsInspector(Clip? clip)
    {
        _refreshingEffects = true;
        try
        {
            if (FindName("SeparateAudioFadesBox") is CheckBox separate)
            {
                separate.IsChecked = clip?.SeparateAudioFades ?? false;
                separate.IsEnabled = clip is not null;
            }
            if (FindName("VideoFadeModeBox") is ComboBox mode)
            {
                mode.ItemsSource ??= new[]
                {
                    new VideoFadeChoice(VideoFadeMode.Opacity, "透明淡化（顯示下層）"),
                    new VideoFadeChoice(VideoFadeMode.Black, "淡黑（保留遮蓋）")
                };
                mode.DisplayMemberPath = nameof(VideoFadeChoice.Name);
                mode.SelectedValuePath = nameof(VideoFadeChoice.Mode);
                mode.SelectedValue = clip?.VideoFadeMode ?? VideoFadeMode.Opacity;
                mode.IsEnabled = clip is not null;
            }
            RefreshFadeFields("FadeIn", clip?.FadeIn, clip is not null);
            RefreshFadeFields("FadeOut", clip?.FadeOut, clip is not null);
            RefreshFadeFields("AudioFadeIn", clip is { SeparateAudioFades: true } ? clip.AudioFadeIn : clip?.FadeIn, clip is not null);
            RefreshFadeFields("AudioFadeOut", clip is { SeparateAudioFades: true } ? clip.AudioFadeOut : clip?.FadeOut, clip is not null);
        }
        finally { _refreshingEffects = false; }
        UpdateEffectsControls();
    }

    private void RefreshFadeFields(string prefix, FadeSettings? fade, bool hasClip)
    {
        SetNumber(prefix + "Box", hasClip ? fade?.Duration ?? 0 : null);
        SetCurve(prefix + "CurveBox", fade?.Curve ?? FadeCurve.Linear, hasClip);
        SetNumber(prefix + "Control1Box", hasClip ? fade?.Control1 ?? 1.0 / 3 : null);
        SetNumber(prefix + "Control2Box", hasClip ? fade?.Control2 ?? 2.0 / 3 : null);
    }

    private Clip ReadEffects(Clip candidate)
    {
        var original = SelectedClip() ?? candidate;
        var separate = (FindName("SeparateAudioFadesBox") as CheckBox)?.IsChecked == true;
        var result = candidate with
        {
            FadeIn = ReadFadeFields("FadeIn", original.FadeIn),
            FadeOut = ReadFadeFields("FadeOut", original.FadeOut),
            SeparateAudioFades = separate,
            VideoFadeMode = (FindName("VideoFadeModeBox") as ComboBox)?.SelectedValue is VideoFadeMode mode
                ? mode : original.VideoFadeMode
        };
        return separate ? result with
        {
            AudioFadeIn = ReadFadeFields("AudioFadeIn", original.SeparateAudioFades ? original.AudioFadeIn : original.FadeIn),
            AudioFadeOut = ReadFadeFields("AudioFadeOut", original.SeparateAudioFades ? original.AudioFadeOut : original.FadeOut)
        } : result;
    }

    private FadeSettings? ReadFadeFields(string prefix, FadeSettings? original)
    {
        var duration = ReadNumber(prefix + "Box");
        var curve = ReadCurve(prefix + "CurveBox");
        var control1 = curve == FadeCurve.Custom ? ReadNumber(prefix + "Control1Box") : original?.Control1 ?? 1.0 / 3;
        var control2 = curve == FadeCurve.Custom ? ReadNumber(prefix + "Control2Box") : original?.Control2 ?? 2.0 / 3;
        // Merely applying a position/gain edit must not reset a split envelope's retained curve interval.
        if (original is not null && Close(duration, original.Duration) && curve == original.Curve &&
            Close(control1, original.Control1) && Close(control2, original.Control2)) return original;
        return duration == 0 && original is null ? null : new(duration, curve, control1, control2);
    }

    private static bool Close(double left, double right) => Math.Abs(left - right) <= 0.000001;

    private void EffectsMode_Changed(object sender, RoutedEventArgs e) => UpdateEffectsControls();
    private void FadeCurve_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateEffectsControls();

    private void UpdateEffectsControls()
    {
        if (_refreshingEffects) return;
        var hasClip = SelectedClip() is not null;
        var separate = (FindName("SeparateAudioFadesBox") as CheckBox)?.IsChecked == true;
        foreach (var prefix in new[] { "FadeIn", "FadeOut", "AudioFadeIn", "AudioFadeOut" })
        {
            var enabled = hasClip && (!prefix.StartsWith("Audio", StringComparison.Ordinal) || separate);
            if (FindName(prefix + "Box") is TextBox duration) duration.IsEnabled = enabled;
            if (FindName(prefix + "CurveBox") is ComboBox curve) curve.IsEnabled = enabled;
            var custom = enabled && ReadCurve(prefix + "CurveBox") == FadeCurve.Custom;
            if (FindName(prefix + "Control1Box") is TextBox control1) control1.IsEnabled = custom;
            if (FindName(prefix + "Control2Box") is TextBox control2) control2.IsEnabled = custom;
        }
    }
}
