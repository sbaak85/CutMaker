using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CutMaker.App;

public partial class MainWindow
{
    private void Performance_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Window { Title = "預覽與快取", Owner = this, Width = 480, Height = 420, MinWidth = 390, MinHeight = 320,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = (Brush)FindResource("PanelBrush"), Foreground = Brushes.White };
        var panel = new StackPanel { Margin = new Thickness(20) };
        dialog.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        void Label(string text) => panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 9, 0, 7) });
        Label("影片預覽品質（匯出仍使用原始素材與輸出設定）");
        var quality = new ComboBox { ItemsSource = new[] { "低 · 最高寬 480", "中 · 最高寬 960", "高 · 最高寬 1280" }, SelectedIndex = (int)EditorPreferences.Current.PreviewQuality };
        panel.Children.Add(quality);
        var proxies = new CheckBox { Content = "為大型影片建立可重用的低解析度代理素材", Foreground = Brushes.White,
            IsChecked = EditorPreferences.Current.UseVideoProxies, Margin = new Thickness(0, 14, 0, 6) };
        panel.Children.Add(proxies);
        Label("首次建立代理需要時間；之後重用。音訊始終使用原始來源。");
        Label("共用快取容量上限（GiB，1–32）；使用中的檔案會保留。");
        var capacity = new TextBox { Text = EditorPreferences.Current.CacheGiB.ToString() }; panel.Children.Add(capacity);
        var usage = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 8) }; panel.Children.Add(usage);
        void RefreshUsage() => usage.Text = $"目前共用快取：{ManagedMediaCache.UsageBytes() / (1024.0 * 1024):0.0} MiB；另有本次工作階段的完整播放檔。";
        RefreshUsage();
        var clear = new Button { Content = "清除未使用的共用快取" };
        clear.Click += (_, _) =>
        {
            try { var result = ManagedMediaCache.Prune(0); MediaVisualCache.ClearMemory(); RefreshUsage(); usage.Text += $" 已保留 {result.Busy} 個使用中或無法移除的檔案。"; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { usage.Text = ex.Message; }
        };
        panel.Children.Add(clear);
        var apply = new Button { Content = "套用", IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        apply.Click += (_, _) =>
        {
            if (!int.TryParse(capacity.Text, out var size) || size is < 1 or > 32) { usage.Text = "請輸入 1–32 的整數。"; return; }
            try
            {
                var previous = EditorPreferences.Current;
                var next = previous with { CacheGiB = size, PreviewQuality = (PreviewQuality)quality.SelectedIndex, UseVideoProxies = proxies.IsChecked == true };
                next.Save(); ManagedMediaCache.Prune(next.CacheBytes);
                if (previous.PreviewQuality != next.PreviewQuality || previous.UseVideoProxies != next.UseVideoProxies)
                    if (!PreviewRenderCache.IsAudioOnly(_project)) InvalidatePreview();
                dialog.DialogResult = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { usage.Text = ex.Message; }
        };
        panel.Children.Add(apply); dialog.ShowDialog();
    }
}
