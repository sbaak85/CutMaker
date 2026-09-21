using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using CutMaker.Core;
using Microsoft.Win32;

namespace CutMaker.App;

public partial class MainWindow : Window
{
    private CutProject _project = CreateProject();
    private string? _projectPath;
    private bool _dirty;
    private bool _refreshing = true;
    private bool _clamping;
    internal bool SuppressClosePrompt { get; set; }

    public static readonly DependencyProperty TrackHeaderWidthProperty = DependencyProperty.Register(
        nameof(TrackHeaderWidth), typeof(double), typeof(MainWindow), new PropertyMetadata(250.0));
    public double TrackHeaderWidth { get => (double)GetValue(TrackHeaderWidthProperty); set => SetValue(TrackHeaderWidthProperty, value); }

    public MainWindow()
    {
        InitializeComponent();
        var workArea = SystemParameters.WorkArea;
        MinWidth = Math.Min(MinWidth, workArea.Width);
        MinHeight = Math.Min(MinHeight, workArea.Height);
        ApplyLayout(LayoutSettings.Load());
        RefreshProject();
        InitializePreview();
        _refreshing = false;
        Loaded += (_, _) => ClampPanels();
        SizeChanged += (_, _) => ClampPanels();
        ProjectToolbar.SizeChanged += (_, _) => ClampPanels();
        AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(HandleShortcut), true);
        PreviewMouseLeftButtonDown += (_, _) => ResetLibraryDragCandidate();
        PreviewMouseLeftButtonUp += (_, _) => ResetLibraryDragCandidate();
        Deactivated += (_, _) => ResetLibraryDragCandidate();
        InitializeRecovery();
        InitializeMediaVisuals();
        InitializeInteraction();
    }

    private static CutProject CreateProject() => CutProject.CreateEmpty("未命名專案") with
    {
        Tracks = [new("video-1", "影片 1", TrackKind.Video), new("audio-1", "音訊 1", TrackKind.Audio)]
    };

    private void RefreshProject()
    {
        _refreshing = true;
        ProjectNameBox.Text = _project.Title;
        ProjectLocation.Text = _projectPath ?? "尚未儲存";
        ProjectLocation.ToolTip = _projectPath;
        ProjectFormat.Text = $"{_project.Video.Width} × {_project.Video.Height} / {_project.Video.Fps:0.##} fps";
        PreviewFormat.Text = ProjectFormat.Text;
        RefreshTimeline();
        RefreshAssets();
        UpdateTitle();
        _refreshing = false;
        RefreshPreviewState();
    }

    private void UpdateTitle() => Title = $"{(_dirty ? "● " : "")}{_project.Title} — CutMaker";

    private void New_Click(object sender, RoutedEventArgs e)
    {
        _importCancellation?.Cancel();
        if (!MayDiscard()) return;
        ResetProjectImport();
        _project = CreateProject();
        _projectPath = null;
        _dirty = false;
        RefreshProject();
        StatusText.Text = "已建立新專案";
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "CutMaker 專案 (*.cutmaker)|*.cutmaker", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var loaded = ProjectStore.Load(dialog.FileName);
            // Modal dialogs pump the dispatcher: an import may finish while the user selects a project.
            _importCancellation?.Cancel();
            if (!MayDiscard()) return;
            ResetProjectImport();
            _project = loaded;
            _projectPath = dialog.FileName;
            _dirty = false;
            RefreshProject();
            RestoreProjectView();
            var missing = _project.MediaAssets.Count(asset => !File.Exists(ProjectStore.ResolveAssetPath(_projectPath, asset)));
            StatusText.Text = missing == 0 ? "專案已開啟" : $"專案已開啟 · {missing} 個素材目前找不到，剪輯資料已保留";
        }
        catch (Exception ex) when (IsProjectError(ex)) { ShowError("無法開啟專案", ex); }
    }

    private void Save_Click(object sender, RoutedEventArgs e) => SaveProject(false);
    private void SaveAs_Click(object sender, RoutedEventArgs e) => SaveProject(true);

    private bool SaveProject(bool saveAs)
    {
        string? target = _projectPath;
        if (saveAs || target is null)
        {
            var dialog = new SaveFileDialog
            {
                Filter = "CutMaker 專案 (*.cutmaker)|*.cutmaker", DefaultExt = ".cutmaker", AddExtension = true,
                FileName = string.Concat(_project.Title.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries))
            };
            if (dialog.ShowDialog(this) != true) return false;
            target = dialog.FileName;
        }
        try
        {
            var outputPath = Path.GetFullPath(target);
            if (_project.MediaAssets.Any(asset => string.Equals(outputPath,
                    _projectPath is null ? Path.GetFullPath(asset.Path) : ProjectStore.ResolveAssetPath(_projectPath, asset),
                    StringComparison.OrdinalIgnoreCase)))
                throw new IOException("專案檔不能覆蓋來源素材，請使用新的 .cutmaker 檔名。");
            // Preserve relative media references when Save As changes the project folder.
            var saved = _project;
            if (_projectPath is not null && !string.Equals(_projectPath, target, StringComparison.OrdinalIgnoreCase))
            {
                var destination = Path.GetDirectoryName(Path.GetFullPath(target))!;
                saved = saved with { MediaAssets = saved.MediaAssets.Select(asset => asset with
                {
                    Path = Path.GetRelativePath(destination, ProjectStore.ResolveAssetPath(_projectPath, asset))
                }).ToList() };
            }
            ProjectStore.Save(target, saved);
            _project = saved;
            _projectPath = target;
            _dirty = false;
            RecoveryProjectSaved();
            SaveProjectView();
            RefreshProject();
            StatusText.Text = "專案已儲存";
            return true;
        }
        catch (Exception ex) when (IsProjectError(ex)) { ShowError("無法儲存專案", ex); return false; }
    }

    private static bool IsProjectError(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or
        JsonException or ProjectValidationException or NotSupportedException or ArgumentException;
    private void ShowError(string title, Exception ex) => MessageBox.Show(this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    private bool MayDiscard()
    {
        if (!_dirty || SuppressClosePrompt) return true;
        var choice = MessageBox.Show(this, "儲存目前專案的變更？", "尚未儲存", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return choice == MessageBoxResult.No || (choice == MessageBoxResult.Yes && SaveProject(false));
    }

    private void AddVideo_Click(object sender, RoutedEventArgs e) => AddTrack(TrackKind.Video);
    private void AddAudio_Click(object sender, RoutedEventArgs e) => AddTrack(TrackKind.Audio);
    private void AddTrack(TrackKind kind)
    {
        RecordUndo();
        var prefix = kind == TrackKind.Video ? "影片" : "音訊";
        _project.Tracks.Add(new(Guid.NewGuid().ToString("N"), $"{prefix} {_project.Tracks.Count(track => track.Kind == kind) + 1}", kind));
        _dirty = true;
        RefreshProject();
        RefreshPreviewAfterEdit();
        StatusText.Text = $"已新增{prefix}軌";
    }

    private void ProjectName_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_refreshing) return;
        RecordUndo();
        _project = _project with { Title = ProjectNameBox.Text };
        _dirty = true;
        UpdateTitle();
    }

    private void HandleShortcut(object sender, KeyEventArgs e)
    {
        // TSF may wrap Space before PreviewKeyDown when a Chinese IME owns focus.
        // Keep the underlying key, then apply the normal text/control-specific guards.
        var key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            _ => e.Key
        };
        var modifiers = Keyboard.Modifiers;
        if (key == Key.F1 && modifiers == ModifierKeys.None && _pointerOriginal is null)
        { Shortcuts_Click(this, e); e.Handled = true; return; }
        if (TryHandleTransportKey(key, modifiers, e.OriginalSource as DependencyObject ?? Keyboard.FocusedElement as DependencyObject, e.IsRepeat))
        { e.Handled = true; return; }
        var timelineFocus = Keyboard.FocusedElement is TimelineLane or TimelineRuler;
        if (key == Key.Escape && modifiers == ModifierKeys.None)
        {
            if (_panElement is not null) { CancelTimelinePan(); e.Handled = true; return; }
            if (IsMarqueeSelecting) { EndMarqueeSelection(cancel: true); e.Handled = true; return; }
            if (_pointerOriginal is not null) { CancelPointerEdit(); StatusText.Text = "已取消拖曳"; e.Handled = true; return; }
            if (timelineFocus) { ClearSelection_Click(this, e); e.Handled = true; return; }
        }
        if (IsMarqueeSelecting || _pointerOriginal is not null) { e.Handled = true; return; }
        var navigationFocus = timelineFocus || (PreviewPane.IsKeyboardFocusWithin &&
            Keyboard.FocusedElement is not TextBoxBase and not ComboBox and not Slider);
        if (navigationFocus && TryHandleNavigationShortcut(key, modifiers)) { e.Handled = true; return; }
        if (timelineFocus)
        {
            if (TryHandleClipShortcut(key, modifiers, e.IsRepeat)) { e.Handled = true; return; }
            if (modifiers == ModifierKeys.Control)
            {
                switch (key)
                {
                    case Key.A: SelectAllClips_Click(this, e); e.Handled = true; return;
                    case Key.C: CopySelectedClips(); e.Handled = true; return;
                    case Key.X: CutClips_Click(this, e); e.Handled = true; return;
                    case Key.V: PasteClips(PlayheadSeconds); e.Handled = true; return;
                    case Key.D: DuplicateClips_Click(this, e); e.Handled = true; return;
                }
            }
            if (key == Key.C && modifiers == ModifierKeys.None && !e.IsRepeat)
            { SetTimelineSnapping(!TimelineSnapEnabled); e.Handled = true; return; }
            if (key == Key.Delete && modifiers == ModifierKeys.Shift)
            { DeleteSelectedClips(true); e.Handled = true; return; }
        }
        if (key == Key.Delete && modifiers == ModifierKeys.None &&
            (timelineFocus || ReferenceEquals(Keyboard.FocusedElement, RemoveClipButton)))
        { RemoveSelectedClip(); e.Handled = true; return; }
        if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            if (key == Key.Space && navigationFocus && !e.IsRepeat) { Audition_Click(this, e); e.Handled = true; return; }
            if (key == Key.S) { SaveProject(true); e.Handled = true; }
            else if (key == Key.Z && Keyboard.FocusedElement is not TextBoxBase) { RedoEdit(); e.Handled = true; }
            return;
        }
        if (modifiers != ModifierKeys.Control) return;
        switch (key)
        {
            case Key.N: New_Click(this, e); break;
            case Key.O: Open_Click(this, e); break;
            case Key.S: SaveProject(false); break;
            case Key.I: ImportFiles_Click(this, e); break;

            case Key.Z when Keyboard.FocusedElement is not TextBoxBase: Undo_Click(this, e); break;
            case Key.Y when Keyboard.FocusedElement is not TextBoxBase: Redo_Click(this, e); break;
            default: return;
        }
        e.Handled = true;
    }
    internal void ApplyLayout(LayoutSettings layout)
    {
        var workArea = SystemParameters.WorkArea;
        Width = LayoutSettings.Bounded(layout.WindowWidth, MinWidth, workArea.Width, 1440);
        Height = LayoutSettings.Bounded(layout.WindowHeight, MinHeight, workArea.Height, 920);
        LibraryColumn.Width = new(LayoutSettings.Bounded(layout.LibraryWidth, 280, 1000, 390));
        InspectorColumn.Width = new(LayoutSettings.Bounded(layout.InspectorWidth, 250, 800, 290));
        TimelineRow.Height = new(LayoutSettings.Bounded(layout.TimelineHeight, 190, 1000, 310));
        TrackHeaderColumn.Width = new(LayoutSettings.Bounded(layout.TrackHeaderWidth, 180, 700, 250));
        TrackHeaderWidth = TrackHeaderColumn.Width.Value;
        AssetNameColumn.Width = new DataGridLength(LayoutSettings.Bounded(layout.AssetNameWidth, 220, 1600, 260));
        ClampPanels();
    }

    internal void ClampPanels()
    {
        if (_clamping || !IsLoaded || RootLayout.ActualWidth <= 0) return;
        _clamping = true;
        try
        {
            // High DPI and small displays can offer less space than the normal desktop minimum.
            var client = (FrameworkElement)VisualTreeHelper.GetParent(RootLayout);
            var availableWidth = Math.Max(0, client.ActualWidth - RootLayout.Margin.Left - RootLayout.Margin.Right);
            var availableHeight = Math.Max(0, client.ActualHeight - RootLayout.Margin.Top - RootLayout.Margin.Bottom);
            var compactHeight = availableHeight < 540;
            var headerHeight = compactHeight ? 34 : 58;
            TimelinePanel.Padding = compactHeight ? new Thickness(14, 6, 14, 6) : new Thickness(14);
            RootLayout.RowDefinitions[0].Height = new GridLength(headerHeight);
            foreach (var button in ProjectToolbar.Children.OfType<Button>())
                button.Padding = compactHeight ? new Thickness(8, 5, 8, 5) : new Thickness(14, 8, 14, 8);
            var panelSpace = Math.Max(0, availableWidth - 16);
            LibraryColumn.MinWidth = Math.Min(280, panelSpace * 0.34);
            InspectorColumn.MinWidth = Math.Min(250, panelSpace * 0.30);
            PreviewColumn.MinWidth = Math.Min(280, panelSpace - LibraryColumn.MinWidth - InspectorColumn.MinWidth);
            var available = panelSpace - PreviewColumn.MinWidth;
            var libraryMin = LibraryColumn.MinWidth;
            var inspectorMin = InspectorColumn.MinWidth;
            var library = Math.Max(libraryMin, LibraryColumn.Width.IsAbsolute ? LibraryColumn.Width.Value : LibraryColumn.ActualWidth);
            var inspector = Math.Max(inspectorMin, InspectorColumn.Width.IsAbsolute ? InspectorColumn.Width.Value : InspectorColumn.ActualWidth);
            var extra = library + inspector - available;
            if (extra > 0)
            {
                var flexible = library - libraryMin + inspector - inspectorMin;
                if (flexible > 0)
                {
                    library -= extra * (library - libraryMin) / flexible;
                    inspector = available - library;
                }
            }
            LibraryColumn.Width = new(Math.Max(libraryMin, library));
            InspectorColumn.Width = new(Math.Max(inspectorMin, inspector));
            PreviewColumn.Width = new(1, GridUnitType.Star);
            var toolbarHeight = Math.Max(44, ProjectToolbar.ActualHeight);
            var verticalSpace = Math.Max(0, availableHeight - headerHeight - toolbarHeight - 8 - 32);
            WorkspaceRow.MinHeight = Math.Min(compactHeight ? 220 : 280, verticalSpace * 0.64);
            TimelineRow.MinHeight = Math.Min(190, verticalSpace - WorkspaceRow.MinHeight);
            var maxTimeline = availableHeight - headerHeight - toolbarHeight - 8 - 32 - WorkspaceRow.MinHeight;
            TimelineRow.Height = new(LayoutSettings.Bounded(TimelineRow.Height.IsAbsolute ? TimelineRow.Height.Value : TimelineRow.ActualHeight, TimelineRow.MinHeight, maxTimeline, 310));
            WorkspaceRow.Height = new(1, GridUnitType.Star);
            TrackHeaderWidth = LayoutSettings.Bounded(TrackHeaderColumn.Width.IsAbsolute ? TrackHeaderColumn.Width.Value : TrackHeaderColumn.ActualWidth, 180, availableWidth - 28 - 288, 250);
            TrackHeaderColumn.Width = new(TrackHeaderWidth);
            UpdateTimelineViewport();
        }
        finally { _clamping = false; }
    }

    private void TrackHeader_DragDelta(object sender, DragDeltaEventArgs e)
    { TrackHeaderWidth = TrackHeaderColumn.ActualWidth; UpdateTimelineViewport(); }
    private void Splitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        ClampPanels();
        SaveLayout();
    }
    private void ResetLayout_Click(object sender, RoutedEventArgs e) { ApplyLayout(new()); SaveLayout(); StatusText.Text = "版面已重設"; }
    internal LayoutSettings CaptureLayout() => new(
        WindowState == WindowState.Normal ? Width : RestoreBounds.Width,
        WindowState == WindowState.Normal ? Height : RestoreBounds.Height,
        LibraryColumn.ActualWidth, InspectorColumn.ActualWidth, TimelineRow.ActualHeight,
        TrackHeaderWidth, AssetNameColumn.ActualWidth);
    private void SaveLayout()
    {
        try { CaptureLayout().Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { StatusText.Text = "目前版面有效，但無法儲存版面設定。"; }
    }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _importCancellation?.Cancel();
        if (!MayDiscard()) { e.Cancel = true; return; }
        ResetProjectImport();
        ShutdownPreview();
        ShutdownRecovery();
        ShutdownMediaVisuals();
        SaveLayout();
    }

    internal void LoadSmokeProject(CutProject project, string path)
    {
        ResetProjectImport();
        _project = project;
        _projectPath = path;
        _dirty = false;
        RefreshProject();
    }
    internal void AddSmokeTrack() => AddTrack(TrackKind.Audio);
    internal void VerifyLayoutBounds()
    {
        var client = (FrameworkElement)VisualTreeHelper.GetParent(RootLayout);
        if (RootLayout.ActualHeight + RootLayout.Margin.Top + RootLayout.Margin.Bottom > client.ActualHeight + 1)
            throw new InvalidOperationException("Root layout overflows client height.");
        if (RootLayout.ActualWidth + RootLayout.Margin.Left + RootLayout.Margin.Right > client.ActualWidth + 1)
            throw new InvalidOperationException("Root layout overflows client width.");
        if (RootLayout.ActualWidth + 1 < LibraryColumn.ActualWidth + InspectorColumn.ActualWidth + PreviewColumn.ActualWidth + 16)
            throw new InvalidOperationException("Workspace columns overflow.");
        var rows = RootLayout.RowDefinitions.Sum(row => row.ActualHeight);
        if (rows > RootLayout.ActualHeight + 1) throw new InvalidOperationException("Workspace rows overflow.");
        if (AssetNameColumn.ActualWidth < 219 || TrackHeaderWidth < 179) throw new InvalidOperationException("Name columns are too narrow.");
        if (AssetGrid.Items.Count > 0 && AssetGrid.ActualHeight < 64)
            throw new InvalidOperationException("The media list must retain room for its header and at least one visible row.");
        if (TrackItems.Items.Count > 0 && TimelineTrackScroll.ViewportHeight < 25)
            throw new InvalidOperationException("The timeline must retain visible space for scrolling tracks.");
    }
}
