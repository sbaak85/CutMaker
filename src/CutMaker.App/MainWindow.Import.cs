using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using CutMaker.Core;
using Microsoft.Win32;

namespace CutMaker.App;

public partial class MainWindow
{
    private CancellationTokenSource? _importCancellation;
    private int _projectGeneration;
    private IReadOnlyList<ImportProblem> _importProblems = [];
    internal Func<ImportCandidate, CancellationToken, Task<ProbedMedia>> ProbeMediaAsync { get; set; } = MediaProbe.ReadAsync;
    internal CutProject CurrentProject => _project;
    internal bool HasUnsavedChanges => _dirty;

    private static string[] GetDroppedFiles(IDataObject data)
    {
        try
        {
            return data.GetDataPresent(DataFormats.FileDrop, autoConvert: false)
                ? data.GetData(DataFormats.FileDrop, autoConvert: false) as string[] ?? [] : [];
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ExternalException) { return []; }
    }

    internal DragDropEffects GetLibraryDropEffect(IDataObject data, DragDropEffects allowedEffects)
    {
        if (_importCancellation is not null || (allowedEffects & DragDropEffects.Copy) == 0) return DragDropEffects.None;
        // Keep drag-over free of file I/O; full validation happens asynchronously after dropping.
        return GetDroppedFiles(data).Any(MediaImport.IsSupportedPath) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void Library_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = GetLibraryDropEffect(e.Data, e.AllowedEffects);
        ShowLibraryDropFeedback(e.Effects == DragDropEffects.Copy);
        e.Handled = true;
    }

    private void Library_DragLeave(object sender, DragEventArgs e)
    {
        var point = e.GetPosition(LibraryDropZone);
        if (point.X < 0 || point.Y < 0 || point.X >= LibraryDropZone.ActualWidth || point.Y >= LibraryDropZone.ActualHeight)
            ShowLibraryDropFeedback(false);
        e.Handled = true;
    }

    private async void Library_Drop(object sender, DragEventArgs e)
    {
        // Copy paths before awaiting: the Explorer data object belongs to the synchronous OLE drag operation.
        var files = GetDroppedFiles(e.Data);
        e.Effects = GetLibraryDropEffect(e.Data, e.AllowedEffects);
        e.Handled = true;
        ShowLibraryDropFeedback(false);
        if (e.Effects == DragDropEffects.Copy) await ImportFilesAsync(files);
    }

    internal void ShowLibraryDropFeedback(bool active)
    {
        LibraryDropZone.BorderBrush = active ? new SolidColorBrush(Color.FromRgb(120, 220, 203)) : Brushes.Transparent;
        DropOverlay.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void ImportFiles_Click(object sender, RoutedEventArgs e)
    {
        if (_importCancellation is not null) return;
        var dialog = new OpenFileDialog
        {
            Title = "匯入素材", Multiselect = true, CheckFileExists = true,
            Filter = "影片、音訊與圖片|*.mp4;*.mp3;*.wav;*.png;*.jpg;*.jpeg|所有檔案|*.*"
        };
        if (dialog.ShowDialog(this) == true) await ImportFilesAsync(dialog.FileNames);
    }

    internal async Task<ImportBatchResult> ImportFilesAsync(IEnumerable<string> paths)
    {
        if (_importCancellation is not null) return new(0, [], true);
        var files = paths.ToArray();
        if (files.Length == 0) return new(0, [], false);
        using var cancellation = new CancellationTokenSource();
        _importCancellation = cancellation;
        var generation = _projectGeneration;
        var problems = new List<ImportProblem>();
        var imported = 0;
        var cancelled = false;
        _importProblems = [];
        ImportDetailsButton.Visibility = Visibility.Collapsed;
        SetImportBusy(true);
        ImportProgressText.Text = "正在檢查檔案…";
        try
        {
            var baseDirectory = _projectPath is null ? Environment.CurrentDirectory : Path.GetDirectoryName(_projectPath)!;
            var existing = _project.MediaAssets.ToArray();
            var plan = await Task.Run(() => MediaImport.Plan(files, existing, baseDirectory), cancellation.Token);
            problems.AddRange(plan.Rejections.Select(rejection => new ImportProblem(rejection.Path, rejection.Reason switch
            {
                ImportRejectionReason.Duplicate => "素材庫或本次匯入已有這個檔案",
                ImportRejectionReason.Directory => "請拖入檔案；目前不匯入整個資料夾",
                ImportRejectionReason.MissingFile => "找不到檔案或無法存取",
                ImportRejectionReason.UnsupportedType => "目前支援 MP4、MP3、WAV、PNG、JPG／JPEG",
                _ => "檔案路徑無效"
            })));
            for (var index = 0; index < plan.Candidates.Count; index++)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var candidate = plan.Candidates[index];
                ImportProgressText.Text = $"讀取 {index + 1}/{plan.Candidates.Count} · {Path.GetFileName(candidate.FullPath)}";
                ImportProgressText.ToolTip = candidate.FullPath;
                try
                {
                    var media = await ProbeMediaAsync(candidate, cancellation.Token);
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (generation != _projectGeneration) throw new OperationCanceledException(cancellation.Token);
                    if (!double.IsFinite(media.Duration) || media.Duration <= 0)
                        throw new InvalidDataException("無法取得有效時長。");
                    RecordUndo();
                    _project.MediaAssets.Add(new(Guid.NewGuid().ToString("N"), candidate.FullPath, media.Kind, media.Duration));
                    imported++;
                    _dirty = true;
                    RefreshAssets();
                    UpdateTitle();
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException or
                    ArgumentException or InvalidOperationException or COMException or TimeoutException or FormatException)
                {
                    problems.Add(new(candidate.FullPath, ex is TimeoutException ? "讀取超過 15 秒，已略過" : ex.Message));
                }
            }
        }
        catch (OperationCanceledException) { cancelled = true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            problems.Add(new("匯入批次", ex.Message));
        }
        finally
        {
            if (ReferenceEquals(_importCancellation, cancellation))
            {
                _importCancellation = null;
                SetImportBusy(false);
                _importProblems = problems;
                ImportDetailsButton.Visibility = problems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                var summary = $"{(cancelled ? "匯入已取消" : "匯入完成")} · 新增 {imported} 個 · 略過 {problems.Count} 個";
                StatusText.Text = summary;
                AssetHint.Text = summary;
                RefreshEmptyLibrary();
            }
        }
        return new(imported, problems, cancelled);
    }

    private void SetImportBusy(bool busy)
    {
        ImportFilesButton.IsEnabled = !busy;
        ImportProgressPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CancelImportButton.IsEnabled = busy;
        if (!busy) ShowLibraryDropFeedback(false);
    }

    private void CancelImport_Click(object sender, RoutedEventArgs e)
    {
        _importCancellation?.Cancel();
        CancelImportButton.IsEnabled = false;
        ImportProgressText.Text = "正在取消，已匯入的素材會保留…";
    }

    private void ImportDetails_Click(object sender, RoutedEventArgs e)
    {
        var details = string.Join("\n\n", _importProblems.Take(40).Select(problem => $"{problem.Path}\n{problem.Reason}"));
        if (_importProblems.Count > 40) details += $"\n\n另有 {_importProblems.Count - 40} 個檔案未列出。";
        MessageBox.Show(this, details, "略過的素材", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ResetProjectImport()
    {
        RecoveryProjectChanging();
        ResetOutputRange();
        ResetMediaVisuals();
        SetClipSelection([]);
        _clipClipboard = [];
        CancelExport();
        InvalidatePreview();
        ClearEditHistory();
        PlayheadSeconds = 0;
        _projectGeneration++;
        TimelineOffsetSeconds = 0;
        SelectedTimelineClipId = null;
        ResetLibraryDragCandidate();
        _importCancellation?.Cancel();
        _importCancellation = null;
        _importProblems = [];
        SetImportBusy(false);
        ImportDetailsButton.Visibility = Visibility.Collapsed;
    }

    private void RefreshAssets()
    {
        var selectedPath = (AssetGrid.SelectedItem as AssetRow)?.FullPath;
        var baseDirectory = _projectPath is null ? Environment.CurrentDirectory : Path.GetDirectoryName(_projectPath)!;
        var rows = _project.MediaAssets.Select(asset => new AssetRow(asset.Id, (File.Exists(Path.GetFullPath(asset.Path, baseDirectory)) ? "" : "⚠ 找不到檔案 · ") + Path.GetFileName(asset.Path),
            Path.GetFullPath(asset.Path, baseDirectory),
            asset.Kind switch { MediaKind.Video => "影片", MediaKind.Audio => "音訊", _ => "圖片" },
            FormatDuration(asset.Duration))).ToList();
        AssetGrid.ItemsSource = rows;
        if (selectedPath is not null) AssetGrid.SelectedItem = rows.FirstOrDefault(row => row.FullPath == selectedPath);
        AssetHint.Text = _project.MediaAssets.Count == 0 ? "MP4 · MP3 · WAV · PNG · JPG／JPEG"
            : $"{_project.MediaAssets.Count} 個素材 · 來源檔案保留在原位置";
        RefreshEmptyLibrary();
    }

    private void RefreshEmptyLibrary() => EmptyLibraryHint.Visibility = _project.MediaAssets.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    private static string FormatDuration(double seconds)
    {
        if (seconds > TimeSpan.MaxValue.TotalSeconds) return "—";
        var value = TimeSpan.FromSeconds(seconds);
        return value.TotalHours >= 1 ? $"{(long)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}" : $"{value.Minutes:00}:{value.Seconds:00}.{value.Milliseconds:000}";
    }
    private sealed record AssetRow(string Id, string Name, string FullPath, string Kind, string Duration);
}

internal sealed record ImportProblem(string Path, string Reason);
internal sealed record ImportBatchResult(int ImportedCount, IReadOnlyList<ImportProblem> Problems, bool Cancelled);
