using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CutMaker.Core;
using Microsoft.Win32;

namespace CutMaker.App;

public partial class MainWindow
{
    private DispatcherTimer? _recoveryTimer;
    private RecoveryStore? _recoveryStore;
    private CancellationTokenSource _recoveryCancellation = new();
    private CancellationTokenSource? _relinkCancellation;
    private bool _recoveryWriting;
    private bool _recoveryStopped;
    internal static string RecoveryDirectory => Path.Combine(LayoutSettings.DataDirectory, "recovery");
    internal string? CurrentRecoverySnapshotPath => _recoveryStore?.SnapshotPath;
    internal string? CurrentProjectPath => _projectPath;

    private void InitializeRecovery()
    {
        _recoveryTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(30)
        };
        _recoveryTimer.Tick += async (_, _) => await SaveRecoveryNowAsync();
        _recoveryTimer.Start();
        Loaded += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            if (SuppressClosePrompt || Environment.GetCommandLineArgs().Contains("--smoke-test")) return;
            ShowRecoveryChooser(onlyIfAvailable: true);
        });
    }

    // Called on the UI thread: capture mutable lists first, then perform the flushed atomic write in the background.
    internal async Task<bool> SaveRecoveryNowAsync()
    {
        if (!_dirty || _recoveryStopped || _recoveryWriting) return false;
        var token = _recoveryCancellation.Token;
        _recoveryWriting = true;
        try
        {
            var snapshot = RecoveryStore.Capture(_project, _projectPath);
            var originalPath = _projectPath;
            var store = _recoveryStore ??= new RecoveryStore(RecoveryDirectory);
            await Task.Run(() => store.Save(snapshot, originalPath, token), token);
            if (!token.IsCancellationRequested && FindName("RecoveryStatusText") is TextBlock status)
                status.Text = $"復原備份 {DateTime.Now:HH:mm:ss}";
            return !token.IsCancellationRequested;
        }
        catch (OperationCanceledException) { return false; }
        catch (ObjectDisposedException) when (token.IsCancellationRequested) { return false; }
        catch (Exception ex) when (RecoveryStore.IsRecoveryError(ex))
        {
            if (!token.IsCancellationRequested)
                StatusText.Text = "自動復原備份未成功，請按 Ctrl+S 儲存 · " + ex.Message;
            return false;
        }
        finally { _recoveryWriting = false; }
    }

    // Invoke only after Save succeeds. A cancelled writer cannot resurrect the pre-save snapshot.
    internal void RecoveryProjectSaved()
    {
        CancelRecoveryWrite();
        ClearOwnedRecovery();
        if (FindName("RecoveryStatusText") is TextBlock status) status.Text = "已儲存 · 每 30 秒備份未存變更";
    }

    // Invoke after the user has accepted save/discard, before assigning another project or closing.
    internal void RecoveryProjectChanging()
    {
        _relinkCancellation?.Cancel();
        CancelRecoveryWrite();
        ClearOwnedRecovery();
        _recoveryStore?.Dispose();
        _recoveryStore = null;
        if (FindName("RecoveryStatusText") is TextBlock status) status.Text = "每 30 秒備份未存變更";
    }

    private void CancelRecoveryWrite()
    {
        _recoveryCancellation.Cancel();
        _recoveryCancellation.Dispose();
        _recoveryCancellation = new();
    }

    private void ClearOwnedRecovery()
    {
        try { _recoveryStore?.ClearSnapshot(); }
        catch (Exception ex) when (RecoveryStore.IsRecoveryError(ex))
        { StatusText.Text = "專案處理完成，但舊的復原備份未能移除 · " + ex.Message; }
    }

    private void ShutdownRecovery()
    {
        _recoveryStopped = true;
        _recoveryTimer?.Stop();
        _relinkCancellation?.Cancel();
        _recoveryCancellation.Cancel();
        _recoveryStore?.Dispose();
    }

    private void Recovery_Click(object sender, RoutedEventArgs e) => ShowRecoveryChooser(onlyIfAvailable: false);

    private void ShowRecoveryChooser(bool onlyIfAvailable)
    {
        try
        {
            var entries = RecoveryStore.Discover(RecoveryDirectory, out var unreadable);
            if (entries.Count == 0)
            {
                if (!onlyIfAvailable || unreadable > 0)
                    StatusText.Text = unreadable == 0 ? "沒有待復原的備份；未儲存變更會每 30 秒自動備份" : $"有 {unreadable} 份備份無法讀取，原檔已保留";
                return;
            }
            var dialog = new Window
            {
                Title = "復原未儲存的工作", Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Width = Math.Min(720, SystemParameters.WorkArea.Width), Height = Math.Min(440, SystemParameters.WorkArea.Height),
                MinWidth = 360, MinHeight = 260, Background = Background, Foreground = Foreground, ShowInTaskbar = false
            };
            var grid = new Grid { Margin = new Thickness(18) };
            grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
            var hint = new TextBlock
            {
                Text = "選擇自動備份。復原後會以尚未儲存的工作副本開啟，請另存專案。" +
                    (unreadable > 0 ? $"\n另有 {unreadable} 份無法讀取的備份，檔案已保留。" : ""),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12)
            };
            grid.Children.Add(hint);
            var list = new ListBox { Background = Background, Foreground = Foreground, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            foreach (var entry in entries)
                list.Items.Add(new ListBoxItem
                {
                    Tag = entry,
                    Content = new TextBlock
                    {
                        Text = $"{entry.Project.Title}\n{entry.SavedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {entry.OriginalProjectPath ?? "從未儲存的專案"}",
                        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 7, 4, 7), ToolTip = entry.OriginalProjectPath
                    }
                });
            list.SelectedIndex = 0;
            Grid.SetRow(list, 1); grid.Children.Add(list);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            var restore = new Button { Content = "復原選取備份", Padding = new Thickness(14, 7, 14, 7), IsDefault = true };
            var cancel = new Button { Content = "稍後再說", Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(10, 0, 0, 0), IsCancel = true };
            restore.Click += (_, _) => { if (list.SelectedItem is ListBoxItem) dialog.DialogResult = true; };
            actions.Children.Add(restore); actions.Children.Add(cancel);
            Grid.SetRow(actions, 2); grid.Children.Add(actions); dialog.Content = grid;
            if (dialog.ShowDialog() == true && list.SelectedItem is ListBoxItem { Tag: RecoveryEntry selected })
                RestoreRecovery(selected.SessionId, promptForChanges: true);
        }
        catch (Exception ex) when (RecoveryStore.IsRecoveryError(ex)) { ShowError("無法讀取復原備份", ex); }
    }

    internal bool RestoreRecovery(string sessionId, bool promptForChanges)
    {
        RecoveryStore? candidate = null;
        try
        {
            candidate = new RecoveryStore(RecoveryDirectory, sessionId);
            var entry = candidate.Read(); // Validate and acquire ownership before asking to replace the current workspace.
            _importCancellation?.Cancel();
            if (promptForChanges && !MayDiscard()) return false;
            ResetProjectImport();
            _project = RecoveryStore.Capture(entry.Project, null);
            _projectPath = null;
            _dirty = true;
            _recoveryStore = candidate;
            candidate = null;
            RefreshProject();
            StatusText.Text = $"已復原 {entry.SavedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss} 的工作副本 · 請按 Ctrl+S 另存";
            return true;
        }
        catch (Exception ex) when (RecoveryStore.IsRecoveryError(ex))
        { StatusText.Text = "復原失敗，備份已保留 · " + ex.Message; return false; }
        finally { candidate?.Dispose(); }
    }

    private async void RelinkAsset_Click(object sender, RoutedEventArgs e)
    {
        if (_relinkCancellation is not null) return;
        if (AssetGrid.SelectedItem is not AssetRow row) { StatusText.Text = "請先選取素材庫中要重新連結的素材"; return; }
        var dialog = new OpenFileDialog
        {
            Title = "重新連結素材 · " + row.Name, CheckFileExists = true,
            Filter = "影片、音訊與圖片|*.mp4;*.mp3;*.wav;*.png;*.jpg;*.jpeg|所有檔案|*.*"
        };
        if (dialog.ShowDialog(this) == true) await RelinkAssetAsync(row.Id, dialog.FileName);
    }

    internal async Task<bool> RelinkAssetAsync(string assetId, string path)
    {
        if (_relinkCancellation is not null) return false;
        var original = _project.MediaAssets.FirstOrDefault(asset => asset.Id == assetId);
        if (original is null) return false;
        using var cancellation = new CancellationTokenSource();
        _relinkCancellation = cancellation;
        var generation = _projectGeneration;
        if (FindName("RelinkAssetButton") is Button button) button.IsEnabled = false;
        try
        {
            var plan = MediaImport.Plan([path], [], Environment.CurrentDirectory);
            if (plan.Candidates.Count != 1) throw new InvalidDataException("請選擇可讀取的 MP4、MP3、WAV、PNG 或 JPG／JPEG 檔案。");
            var candidate = plan.Candidates[0];
            StatusText.Text = "正在檢查重新連結的素材…";
            var media = await ProbeMediaAsync(candidate, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (generation != _projectGeneration || _project.MediaAssets.FirstOrDefault(asset => asset.Id == assetId) != original)
                return false;
            if (media.Kind != original.Kind) throw new InvalidDataException("新素材類型必須與原素材相同（影片、音訊或圖片）。");
            if (!double.IsFinite(media.Duration) || media.Duration <= 0) throw new InvalidDataException("新素材沒有有效的播放時長。");
            var required = _project.Clips.Where(clip => clip.AssetId == assetId).Select(clip => clip.SourceEnd).DefaultIfEmpty(0).Max();
            var duration = media.Kind == MediaKind.Image ? Math.Max(original.Duration, required) : media.Duration;
            if (duration + 0.0000001 < required) throw new InvalidDataException($"新素材至少需 {required:0.###} 秒，才能保留所有片段的剪輯範圍。");
            var replacement = original with { Path = candidate.FullPath, Duration = duration };
            var updated = _project with { MediaAssets = _project.MediaAssets.Select(asset => asset.Id == assetId ? replacement : asset).ToList() };
            ProjectValidator.Validate(updated);
            RecordUndo(); _project = updated;
            RefreshAssets();
            AssetGrid.SelectedItem = AssetGrid.Items.OfType<AssetRow>().FirstOrDefault(row => row.Id == assetId);
            FinishEdit("已重新連結素材，原有片段與剪輯位置已保留 · Ctrl+Z 復原");
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex) when (RecoveryStore.IsRecoveryError(ex) || ex is InvalidOperationException or COMException or TimeoutException or FormatException)
        {
            if (generation == _projectGeneration) StatusText.Text = "重新連結失敗 · " + ex.Message;
            return false;
        }
        finally
        {
            if (ReferenceEquals(_relinkCancellation, cancellation))
            {
                _relinkCancellation = null;
                if (FindName("RelinkAssetButton") is Button relink) relink.IsEnabled = true;
            }
        }
    }
}
