using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CutMaker.Core;

namespace CutMaker.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var smoke = e.Args.Contains("--smoke-test");
        if (smoke) ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var window = new MainWindow();
        MainWindow = window;
        if (smoke)
        {
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -20000;
            window.Top = -20000;
            window.SuppressClosePrompt = true;
        }
        window.Show();
        if (smoke) window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => RunSmoke(window));
    }

    private async void RunSmoke(MainWindow window)
    {
        var folder = Path.Combine(LayoutSettings.DataDirectory, "smoke");
        Directory.CreateDirectory(folder);
        try
        {
            window.ApplyLayout(new());
            await SettleLayout(window);
            window.VerifyLayoutBounds();
            Render(window, Path.Combine(folder, "workspace-default.png"));

            await ImportSmokeChecks.RunAsync(window, folder);
            await SettleLayout(window);
            window.VerifyLayoutBounds();
            Render(window, Path.Combine(folder, "workspace-imported.png"));
            window.ShowLibraryDropFeedback(true);
            await SettleLayout(window);
            Render(window, Path.Combine(folder, "workspace-drop-target.png"));
            window.ShowLibraryDropFeedback(false);

            TimelineSmokeChecks.Run(window, folder);
            await SettleLayout(window);
            window.VerifyLayoutBounds();
            Render(window, Path.Combine(folder, "workspace-timeline.png"));
            window.TryZoomTimelineWheel(360, System.Windows.Input.ModifierKeys.Control, 200);
            await SettleLayout(window);
            Render(window, Path.Combine(folder, "workspace-wheel-zoom.png"));
            window.TryZoomTimelineWheel(-360, System.Windows.Input.ModifierKeys.Control, 200);

            EditingSmokeChecks.Run(window, folder);
            await SettleLayout(window);
            window.VerifyLayoutBounds();
            Render(window, Path.Combine(folder, "workspace-editing.png"));
            BatchSmokeChecks.Run(window, folder);
            ConvenienceSmokeChecks.Run(window, folder, name => Render(window, Path.Combine(folder, name)));
            NavigationSmokeChecks.Run(window, folder);
            ZoomRangeSmokeChecks.Run(window, folder, name => Render(window, Path.Combine(folder, name)));
            await TrackControlsSmokeChecks.RunAsync(window, folder, name => Render(window, Path.Combine(folder, name)));
            await RecoverySmokeChecks.RunAsync(window, folder);
            await window.RunPreviewSmokeAsync(folder);
            await SettleLayout(window);
            Render(window, Path.Combine(folder, "workspace-preview.png"));

            var longName = "這是一個很長的素材名稱_用來確認完整顯示與自動換行_訪談錄音與環境音效_第一段素材_2026年09月20日.wav";
            var project = CutProject.CreateEmpty("CutMaker · 長名稱與版面驗證") with
            {
                MediaAssets = [new("long-name", Path.Combine("media", longName), MediaKind.Audio, 120)],
                Tracks = [new("audio-1", "很長的音訊軌道名稱 — 訪談、環境音與背景配樂", TrackKind.Audio)]
            };
            var projectPath = Path.Combine(folder, "project-roundtrip.cutmaker");
            ProjectStore.Save(projectPath, project);
            window.LoadSmokeProject(ProjectStore.Load(projectPath), projectPath);
            window.AddSmokeTrack();
            // Simulate enlarged panels, then shrink the window. Bounds must be clamped, not clipped.
            window.ApplyLayout(new(1160, 760, 900, 700, 900, 380, 320));
            window.Width = 1160;
            window.Height = 760;
            await SettleLayout(window);
            window.VerifyLayoutBounds();
            Render(window, Path.Combine(folder, "workspace-compact-long-name.png"));

            // Exercise the minimum window size and persisted layout reload.
            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            await SettleLayout(window);
            window.VerifyLayoutBounds();
            Render(window, Path.Combine(folder, "workspace-minimum.png"));
            var saved = window.CaptureLayout();
            saved.Save();
            if (LayoutSettings.Load() != saved) throw new InvalidOperationException("Layout round-trip failed.");
            window.MinWidth = 900;
            window.MinHeight = 580;
            window.Width = 900;
            window.Height = 580;
            await SettleLayout(window);
            window.VerifyLayoutBounds();
            Render(window, Path.Combine(folder, "workspace-small-display.png"));
            window.CapturePendingPreviewLayoutSmoke(() => Render(window, Path.Combine(folder, "workspace-small-preview-preparing.png")));
            window.CancelRenderButton.Visibility = Visibility.Visible;
            window.RenderProgressBar.Visibility = Visibility.Visible;
            window.RenderProgressBar.Value = 45;
            await SettleLayout(window);
            window.VerifyLayoutBounds();
            Render(window, Path.Combine(folder, "workspace-small-export.png"));
            window.CancelRenderButton.Visibility = Visibility.Collapsed;
            window.RenderProgressBar.Visibility = Visibility.Collapsed;
            window.MinWidth = Math.Min(1000, SystemParameters.WorkArea.Width);
            window.MinHeight = Math.Min(720, SystemParameters.WorkArea.Height);
            window.ApplyLayout(new());
            await SettleLayout(window);
            File.WriteAllText(Path.Combine(folder, "result.txt"), "PASS: startup; native media import; file-drop handling; import cancellation; timeline placement; editing and independent/custom Fade; multi-selection and linked batch operations; marquee selection and group cross-track movement; frame/boundary/fit navigation; quarter-width zoom; editable compact tracks and mute; recovery and relink; source waveforms and thumbnails; quick-seek frames, Space transport and continuous native preview playback; output settings; project round-trip; add track; default/compact/minimum/small-display layout bounds; visible media rows and long filenames; layout persistence; PNG rendering.");
            window.Close();
            Shutdown(0);
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(folder, "result.txt"), ex.ToString());
            window.Close();
            Shutdown(1);
        }
    }

    private static async Task SettleLayout(MainWindow window)
    {
        await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
        window.ClampPanels();
        await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
        window.ClampPanels();
        await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);
    }

    private static void Render(MainWindow window, string path)
    {
        var root = (FrameworkElement)window.Content;
        root.UpdateLayout();
        File.WriteAllText(path + ".txt", $"Window={window.ActualWidth}x{window.ActualHeight}; root={root.ActualWidth}x{root.ActualHeight}; {window.CaptureLayout()}");
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
            drawing.DrawRectangle(new VisualBrush(root)
            {
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(VisualTreeHelper.GetOffset(root).X, VisualTreeHelper.GetOffset(root).Y, root.ActualWidth, root.ActualHeight),
                Stretch = Stretch.Fill
            }, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
