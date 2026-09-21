using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CutMaker.Core;

namespace CutMaker.App;

public partial class MainWindow
{
    private void AddFadeMenu(ContextMenu menu, bool fadeIn)
    {
        var current = SelectedClip();
        if (current is null) return;
        var fade = (fadeIn ? current.FadeIn : current.FadeOut) ?? new FadeSettings(.5);
        var heading = fadeIn ? "淡入" : "淡出";
        var root = new MenuItem { Header = $"{heading} · 套用至選取片段" };
        var precise = new MenuItem { Header = "秒數與曲線…" };
        precise.Click += (_, _) => ShowQuickFade(fadeIn); root.Items.Add(precise);
        var names = new[] { "線性", "慢進", "慢出", "平滑 S", "等功率", "自訂" };
        foreach (var curve in Enum.GetValues<FadeCurve>())
        {
            var item = new MenuItem { Header = names[(int)curve], IsChecked = fade.Curve == curve };
            item.Click += (_, _) => ApplyQuickFade(fadeIn, fade with { Curve = curve, RangeStart = 0, RangeEnd = 1 }); root.Items.Add(item);
        }
        root.Items.Add(new Separator());
        var remove = new MenuItem { Header = $"移除{heading}" }; remove.Click += (_, _) => ApplyQuickFade(fadeIn, null); root.Items.Add(remove);
        var save = new MenuItem { Header = $"將目前{heading}存成常用預設" };
        save.Click += (_, _) =>
        {
            SavePreferencesSafely(fadeIn ? EditorPreferences.Current with { FadeInPreset = fade } : EditorPreferences.Current with { FadeOutPreset = fade });
        }; root.Items.Add(save);
        var preset = fadeIn ? EditorPreferences.Current.FadeInPreset : EditorPreferences.Current.FadeOutPreset;
        var apply = new MenuItem { Header = $"套用常用{heading}預設", IsEnabled = preset is not null };
        apply.Click += (_, _) => ApplyQuickFade(fadeIn, preset); root.Items.Add(apply);
        menu.Items.Add(root);
    }
    private bool ApplyQuickFade(bool fadeIn, FadeSettings? settings)
    {
        var ids = SelectionIds();
        var replacements = _project.Clips.Where(clip => ids.Contains(clip.Id)).Select(clip =>
        {
            var value = settings is null ? null : settings with { Duration = Math.Min(settings.Duration, clip.Duration), RangeStart = 0, RangeEnd = 1 };
            return fadeIn ? clip with { FadeIn = value } : clip with { FadeOut = value };
        }).ToArray();
        return CommitBatch(() => TimelineBatchEditor.Replace(_project, replacements), $"已套用{(fadeIn ? "淡入" : "淡出")}至 {replacements.Length} 個片段 · Ctrl+Z 復原");
    }
    private void ShowQuickFade(bool fadeIn)
    {
        if (SelectedClip() is not { } clip) return;
        var value = (fadeIn ? clip.FadeIn : clip.FadeOut) ?? new FadeSettings(.5);
        var dialog = new Window { Title = fadeIn ? "快速淡入設定" : "快速淡出設定", Owner = this,
            Width = 430, Height = 390, MinWidth = 360, MinHeight = 300, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (Brush)FindResource("PanelBrush"), Foreground = Brushes.White };
        var panel = new StackPanel { Margin = new Thickness(20) }; dialog.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        TextBox Field(string label, double number)
        {
            panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 6, 0, 5) });
            var input = new TextBox { Text = number.ToString("0.#########", CultureInfo.InvariantCulture) }; panel.Children.Add(input); return input;
        }
        var duration = Field($"套用至 {SelectionIds().Count} 個選取片段 · 秒數（超過片段長度會縮短）", value.Duration);
        var curve = new ComboBox { ItemsSource = new[] { "線性", "慢進", "慢出", "平滑 S", "等功率", "自訂" }, SelectedIndex = (int)value.Curve, Margin = new Thickness(0, 10, 0, 0) };
        panel.Children.Add(curve);
        var c1 = Field("自訂控制點 1（0–1）", value.Control1); var c2 = Field("自訂控制點 2（0–1）", value.Control2);
        void EnableCustom() { c1.IsEnabled = c2.IsEnabled = curve.SelectedIndex == (int)FadeCurve.Custom; }
        curve.SelectionChanged += (_, _) => EnableCustom(); EnableCustom();
        var error = new TextBlock { Foreground = Brushes.Salmon, TextWrapping = TextWrapping.Wrap }; panel.Children.Add(error);
        var button = new Button { Content = "套用", IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        button.Click += (_, _) =>
        {
            bool Number(TextBox input, out double number) => double.TryParse(input.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number);
            if (!Number(duration, out var seconds) || seconds < 0 || !Number(c1, out var a) || !Number(c2, out var b) || a is < 0 or > 1 || b is < 0 or > 1)
            { error.Text = "請輸入非負秒數與 0–1 的控制點。"; return; }
            if (ApplyQuickFade(fadeIn, new(seconds, (FadeCurve)curve.SelectedIndex, a, b))) dialog.DialogResult = true;
            else error.Text = StatusText.Text;
        };
        panel.Children.Add(button); dialog.ShowDialog();
    }
    private void SavePreferencesSafely(EditorPreferences preferences)
    {
        try { preferences.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { StatusText.Text = $"偏好設定無法儲存：{ex.Message}"; }
    }
}
