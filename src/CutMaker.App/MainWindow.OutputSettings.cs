using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CutMaker.Core;

namespace CutMaker.App;

public partial class MainWindow
{
    private ExportVideoQuality _exportVideoQuality = ExportVideoQuality.Balanced;
    private int _exportAudioBitrate = 192;
    private bool _exportMarkedRange;
    private double _exportInSeconds;
    private double _exportOutSeconds;

    private void OutputSettings_Click(object sender, RoutedEventArgs e) => ShowOutputSettings(false);

    internal void ResetOutputRange()
    {
        _exportMarkedRange = false;
        _exportInSeconds = 0;
        _exportOutSeconds = 0;
    }

    private bool ShowOutputSettings(bool forExport)
        => CreateOutputSettingsWindow(forExport).ShowDialog() == true;

    private Window CreateOutputSettingsWindow(bool forExport)
    {
        var dialog = new Window
        {
            Title = forExport ? "匯出設定" : "專案與匯出設定", Owner = this,
            Width = 540, Height = 670, MinWidth = 420, MinHeight = 440,
            MaxHeight = Math.Max(440, SystemParameters.WorkArea.Height - 48),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (Brush)FindResource("PanelBrush"), Foreground = Brushes.White
        };
        var panel = new StackPanel { Margin = new Thickness(20) };
        dialog.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        void Label(string text) => panel.Children.Add(new TextBlock { Text = text, Margin = new Thickness(0, 12, 0, 5), TextWrapping = TextWrapping.Wrap });
        Label("畫面尺寸與影格率（儲存於專案，可復原）");
        var formats = new (string Name, int Width, int Height)[]
        {
            ("橫向 1080p · 1920 × 1080", 1920, 1080), ("橫向 720p · 1280 × 720", 1280, 720),
            ("直向 1080p · 1080 × 1920", 1080, 1920), ("正方形 · 1080 × 1080", 1080, 1080),
            ($"目前尺寸 · {_project.Video.Width} × {_project.Video.Height}", _project.Video.Width, _project.Video.Height)
        };
        var resolution = new ComboBox { ItemsSource = formats.Select(item => item.Name).ToArray(), MinHeight = 32 };
        resolution.SelectedIndex = Array.FindIndex(formats, item => item.Width == _project.Video.Width && item.Height == _project.Video.Height);
        panel.Children.Add(resolution);
        var fpsValues = new[] { 24.0, 25.0, 30.0, 60.0, _project.Video.Fps }.Distinct().ToArray();
        var fps = new ComboBox { ItemsSource = fpsValues.Select(value => $"{value:0.###} fps").ToArray(),
            SelectedIndex = Array.IndexOf(fpsValues, _project.Video.Fps), MinHeight = 32, Margin = new Thickness(0, 7, 0, 0) };
        panel.Children.Add(fps);
        Label("MP4 畫質");
        var quality = new ComboBox { ItemsSource = new[] { "較小檔案", "平衡（建議）", "高畫質" }, SelectedIndex = (int)_exportVideoQuality, MinHeight = 32 };
        panel.Children.Add(quality);
        Label("音訊位元率（MP4／MP3）");
        var bitrates = new[] { 128, 192, 256, 320 };
        var bitrate = new ComboBox { ItemsSource = bitrates.Select(value => $"{value} kbps").ToArray(), SelectedIndex = Array.IndexOf(bitrates, _exportAudioBitrate), MinHeight = 32 };
        panel.Children.Add(bitrate);
        Label("輸出區間（秒）；畫質、位元率及區間適用於本次工作階段");
        var marked = new CheckBox { Content = "只匯出指定區間", Foreground = Brushes.White,
            IsChecked = _exportMarkedRange, Margin = new Thickness(0, 3, 0, 6) };
        panel.Children.Add(marked);
        var start = new TextBox { Text = _exportInSeconds.ToString("0.#########", CultureInfo.InvariantCulture), MinWidth = 170 };
        var end = new TextBox { Text = (_exportOutSeconds > _exportInSeconds ? Math.Min(_exportOutSeconds, PreviewDuration) : PreviewDuration).ToString("0.#########", CultureInfo.InvariantCulture), MinWidth = 170 };
        void RangeRow(string name, TextBox input)
        {
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
            var label = new TextBlock { Text = name, Width = 52, VerticalAlignment = VerticalAlignment.Center };
            var button = new Button { Content = "使用播放頭", Padding = new Thickness(9, 5, 9, 5) };
            button.Click += (_, _) => { input.Text = PlayheadSeconds.ToString("0.#########", CultureInfo.InvariantCulture); marked.IsChecked = true; };
            DockPanel.SetDock(label, Dock.Left); DockPanel.SetDock(button, Dock.Right);
            row.Children.Add(label); row.Children.Add(button); row.Children.Add(input); panel.Children.Add(row);
        }
        RangeRow("起點", start); RangeRow("終點", end);
        Label($"時間軸總長 {PreviewDuration:0.#########} 秒。匯出區間會從 0 秒開始，不改動剪輯內容。");
        var error = new TextBlock { Foreground = Brushes.Salmon, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 8) };
        panel.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", IsCancel = true };
        var apply = new Button { Content = forExport ? "選擇輸出檔案…" : "套用設定", IsDefault = true };
        buttons.Children.Add(cancel); buttons.Children.Add(apply); panel.Children.Add(buttons);
        apply.Click += (_, _) =>
        {
            if (!double.TryParse(start.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var inTime) ||
                !double.TryParse(end.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var outTime) ||
                !double.IsFinite(inTime) || !double.IsFinite(outTime))
            { error.Text = "請以秒輸入有效數值，例如 12.5。"; return; }
            if (marked.IsChecked == true && (inTime < 0 || outTime <= inTime || outTime > PreviewDuration + 1e-7))
            { error.Text = "起點必須大於或等於 0，終點須大於起點且不可超過時間軸。"; return; }
            var chosen = formats[resolution.SelectedIndex];
            var video = new VideoSettings(chosen.Width, chosen.Height, fpsValues[fps.SelectedIndex]);
            if (_project.Video != video)
            {
                RecordUndo(); _project = _project with { Video = video };
                FinishEdit("已更新專案畫面設定"); RefreshProject();
            }
            _exportVideoQuality = (ExportVideoQuality)quality.SelectedIndex;
            _exportAudioBitrate = bitrates[bitrate.SelectedIndex];
            _exportMarkedRange = marked.IsChecked == true;
            _exportInSeconds = inTime; _exportOutSeconds = outTime;
            dialog.DialogResult = true;
        };
        return dialog;
    }

    private MediaRenderOptions CurrentExportOptions() => new(VideoQuality: _exportVideoQuality, AudioBitrateKbps: _exportAudioBitrate,
        OutputStart: _exportMarkedRange ? _exportInSeconds : null, OutputEnd: _exportMarkedRange ? _exportOutSeconds : null);

    internal void RunOutputSettingsSmoke(string folder)
    {
        var original = _project;
        var dirty = _dirty;
        var undo = _undo.ToArray(); var redo = _redo.ToArray();
        var quality = _exportVideoQuality; var bitrate = _exportAudioBitrate;
        var marked = _exportMarkedRange; var inTime = _exportInSeconds; var outTime = _exportOutSeconds;
        var dialog = CreateOutputSettingsWindow(true);
        dialog.WindowStartupLocation = WindowStartupLocation.Manual;
        dialog.Left = -10000; dialog.Top = -10000;
        Exception? failure = null;
        dialog.Loaded += (_, _) => dialog.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            try
            {
                dialog.UpdateLayout();
                void Capture(string filename)
                {
                    var image = new RenderTargetBitmap((int)Math.Ceiling(dialog.ActualWidth), (int)Math.Ceiling(dialog.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    image.Render(dialog);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                    using var file = File.Create(Path.Combine(folder, filename)); encoder.Save(file);
                }
                Capture("output-settings.png");
                if (dialog.Content is not ScrollViewer { Content: StackPanel panel } scroller || panel.Children.OfType<ComboBox>().Count() != 4)
                    throw new InvalidOperationException("Output settings presets are missing from the dialog.");
                var choices = panel.Children.OfType<ComboBox>().ToArray();
                var rangeInputs = panel.Children.OfType<DockPanel>().SelectMany(row => row.Children.OfType<TextBox>()).ToArray();
                var checkbox = panel.Children.OfType<CheckBox>().Single();
                var buttons = panel.Children.OfType<StackPanel>().Single().Children.OfType<Button>().ToArray();
                var errorText = panel.Children.OfType<TextBlock>().Last();
                dialog.Width = 560; dialog.Height = 480; dialog.UpdateLayout();
                scroller.ScrollToEnd(); dialog.UpdateLayout();
                var applyBottom = buttons[^1].TransformToAncestor(dialog).Transform(new Point(0, buttons[^1].ActualHeight));
                if (applyBottom.Y > dialog.ActualHeight || applyBottom.Y < 0)
                    throw new InvalidOperationException("Output dialog buttons cannot be reached after scrolling in a small window.");
                Capture("output-settings-small.png");
                checkbox.IsChecked = true; rangeInputs[0].Text = "1"; rangeInputs[1].Text = "1";
                buttons[^1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (!dialog.IsVisible || string.IsNullOrWhiteSpace(errorText.Text) || _project != original)
                    throw new InvalidOperationException("An invalid export range was accepted or changed the project.");
                checkbox.IsChecked = false;
                choices[0].SelectedIndex = 2; choices[1].SelectedIndex = 3; choices[2].SelectedIndex = 2; choices[3].SelectedIndex = 3;
                buttons[^1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (_project.Video != new VideoSettings(1080, 1920, 60) || _exportVideoQuality != ExportVideoQuality.High || _exportAudioBitrate != 320)
                    throw new InvalidOperationException("Output settings dialog did not apply its selected values.");
                UndoEdit();
                if (_project.Video != original.Video) throw new InvalidOperationException("Project output settings are not undoable.");
            }
            catch (Exception error) { failure = error; }
            finally { if (dialog.IsVisible) dialog.DialogResult = false; }
        }));
        dialog.ShowDialog();
        _project = original; _dirty = dirty;
        _undo.Clear(); _undo.AddRange(undo); _redo.Clear(); _redo.AddRange(redo);
        _exportVideoQuality = quality; _exportAudioBitrate = bitrate; _exportMarkedRange = marked;
        _exportInSeconds = inTime; _exportOutSeconds = outTime;
        RefreshProject(); RefreshHistoryButtons(); InvalidatePreview();
        if (failure is not null) throw failure;
        File.WriteAllText(Path.Combine(folder, "output-settings-result.txt"),
            "PASS: output settings dialog rendered; invalid range rejected; portrait, 60 fps, high quality and 320 kbps applied; project settings undo restores original values.");
    }
}
