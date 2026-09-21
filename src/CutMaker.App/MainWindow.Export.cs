using System.IO;
using System.Diagnostics;
using System.Windows;
using CutMaker.Core;
using Microsoft.Win32;

namespace CutMaker.App;

public partial class MainWindow
{
    private CancellationTokenSource? _exportCancellation;
    private int _exportContextGeneration;
    private string? _lastExportPath;
    internal bool IsExporting => _exportCancellation is not null;

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_exportCancellation is not null) return;
        if (_project.Clips.Count == 0) { StatusText.Text = "請先把素材放入時間軸，再匯出。"; return; }
        if (!ShowOutputSettings(true)) return;
        var name = string.Concat(_project.Title.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        var dialog = new SaveFileDialog
        {
            Filter = "MP4 影片 (*.mp4)|*.mp4|MP3 音訊 (*.mp3)|*.mp3", DefaultExt = EditorPreferences.Current.ExportFormat == 2 ? ".mp3" : ".mp4", AddExtension = true,
            FileName = string.IsNullOrWhiteSpace(name) ? "CutMaker" : name, OverwritePrompt = true,
            FilterIndex = EditorPreferences.Current.ExportFormat,
            InitialDirectory = Directory.Exists(EditorPreferences.Current.ExportFolder) ? EditorPreferences.Current.ExportFolder : ""
        };
        if (dialog.ShowDialog(this) != true) return;
        SavePreferencesSafely(EditorPreferences.Current with { ExportFolder = Path.GetDirectoryName(dialog.FileName)!, ExportFormat = dialog.FilterIndex });
        var snapshot = _project with { MediaAssets = [.. _project.MediaAssets], Tracks = [.. _project.Tracks], Clips = [.. _project.Clips] };
        var projectPath = _projectPath;
        var generation = _exportContextGeneration;
        var options = CurrentExportOptions();
        using var cancellation = new CancellationTokenSource();
        _exportCancellation = cancellation;
        ExportButton.IsEnabled = false;
        CancelRenderButton.Visibility = Visibility.Visible;
        RenderProgressBar.Visibility = Visibility.Visible;
        RenderProgressBar.Value = 0;
        try
        {
            var progress = new Progress<MediaRenderProgress>(update =>
            {
                if (_exportCancellation != cancellation || generation != _exportContextGeneration) return;
                RenderProgressBar.Value = update.Fraction * 100;
                StatusText.Text = $"{update.Message} {update.Fraction:P0} · 使用開始匯出時的剪輯內容";
            });
            var result = await MediaRenderService.RenderAsync(snapshot, projectPath, dialog.FileName, options, progress: progress,
                cancellationToken: cancellation.Token);
            _lastExportPath = result.OutputPath; OpenExportFolderButton.IsEnabled = true;
            if (generation == _exportContextGeneration) StatusText.Text = $"已匯出 {result.Duration:0.##} 秒 · {result.OutputPath}";
        }
        catch (OperationCanceledException)
        { if (generation == _exportContextGeneration) StatusText.Text = "已取消匯出，原有輸出檔與來源素材保留。"; }
        catch (Exception ex)
        {
            if (generation == _exportContextGeneration)
            {
                StatusText.Text = "匯出失敗，原有輸出檔與來源素材保留。";
                ShowError("無法匯出", ex);
            }
        }
        finally
        {
            if (_exportCancellation == cancellation)
            {
                _exportCancellation = null;
                ExportButton.IsEnabled = true;
                CancelRenderButton.Visibility = Visibility.Collapsed;
                RenderProgressBar.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void CancelRender_Click(object sender, RoutedEventArgs e) => _exportCancellation?.Cancel();
    private void OpenExportFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_lastExportPath is null || !File.Exists(_lastExportPath)) { StatusText.Text = "上次匯出檔案已移動或不存在。"; return; }
        try
        {
            var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            info.ArgumentList.Add("/select,"); info.ArgumentList.Add(_lastExportPath); Process.Start(info);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { StatusText.Text = ex.Message; }
    }
    internal void CancelExport()
    {
        // Project switches suppress stale status; ordinary edits and Undo still report snapshot export progress.
        _exportContextGeneration++;
        _exportCancellation?.Cancel();
    }
}
