using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CutMaker.Core;

namespace CutMaker.App;

public partial class MainWindow
{
    internal async Task RunPerformanceSmokeAsync(string folder, Action<string> capture)
    {
        var original = _project; var path = _projectPath; var dirty = _dirty;
        var undo = _undo.ToArray(); var redo = _redo.ToArray(); var layout = CaptureLayout();
        var zoom = TimelineZoom.Value; var offset = TimelineOffsetSeconds;
        var selected = SelectedTimelineClipId; var selection = SelectedTimelineClipIds?.ToArray();
        var report = new List<string>();
        void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Performance UI: " + message); report.Add("PASS: " + message); }
        try
        {
            var source = Path.Combine(folder, "wave-detail-source.wav");
            await MediaRenderService.RunToolAsync(MediaRenderService.FindTool("ffmpeg.exe"),
                ["-hide_banner", "-nostdin", "-y", "-v", "error", "-f", "lavfi", "-i", "aevalsrc=if(between(t\\,20\\,20.05)\\,0.5\\,0):s=48000:d=30", "-c:a", "pcm_s16le", source], CancellationToken.None);
            var project = new CutProject
            {
                MediaAssets = [new("a", source, MediaKind.Audio, 30)],
                Tracks = [new("a", "波形細節與播放中音量", TrackKind.Audio), new("b", "未修改軌道", TrackKind.Audio)],
                Clips = [new("a", "a", "a", 0, 0, 30), new("b", "a", "b", 0, 0, 30, Gain: .5)]
            };
            LoadSmokeProject(project, Path.Combine(folder, "performance.cutmaker"));
            TimelineZoom.Value = 100; TimelinePixelsPerSecond = 100; TimelineOffsetSeconds = 16;
            RefreshTimeline(); UpdateLayout();
            var a = FindLanes(TrackItems).Single(lane => Equals(lane.Tag, "a"));
            var b = FindLanes(TrackItems).Single(lane => Equals(lane.Tag, "b"));
            var clock = Stopwatch.StartNew();
            for (var index = 0; index < 100; index++)
            {
                _project = _project with { Clips = [_project.Clips[0] with { Gain = .5 + index / 100.0 }, _project.Clips[1]] };
                RefreshTimeline(); UpdateLayout();
            }
            Check(ReferenceEquals(a, FindLanes(TrackItems).Single(lane => Equals(lane.Tag, "a"))) &&
                ReferenceEquals(b, FindLanes(TrackItems).Single(lane => Equals(lane.Tag, "b"))),
                $"100 edits retain both WPF lane containers; elapsed={clock.ElapsedMilliseconds}ms");
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (_assetVisualPending.Count > 0 && DateTime.UtcNow < deadline) await Task.Delay(25);
            await LoadVisibleWaveDetailsAsync();
            Check(a.Clips!.Single().Waveform?.Width == 2048 && a.Clips!.Single().WaveformStart == 16 && a.Clips!.Single().WaveformDuration == 14,
                "zoomed waveform loads 2048 bins for source seconds 16–30, retaining source coordinates");
            var bitmap = a.Clips!.Single().Waveform!;
            var pixels = new byte[2048 * 48 * 4]; ((System.Windows.Media.Imaging.BitmapSource)bitmap).CopyPixels(pixels, 2048 * 4, 0);
            var columns = Enumerable.Range(0, 2048).Where(x => pixels[(15 * 2048 + x) * 4 + 3] > 0).ToArray();
            Check(columns.Length > 0 && Math.Abs(columns[0] - (20 - 16) / 14.0 * 2048) < 3,
                "local waveform places the actual 20-second pulse at its correct source time");
            capture("workspace-wave-detail.png");
            SeekPreview(19.9); await PreparePreviewAsync(); StartPreviewPlayback();
            var device = _audioDevice; var mixer = _audioPreview;
            var before = device!.PositionFrames;
            Check(ApplyTrackSettings(_project.Tracks[0] with { Volume = .4 }), "live track volume edit accepted");
            Check(PreviewIsReady && _previewPlaying && ReferenceEquals(device, _audioDevice) && ReferenceEquals(mixer, _audioPreview),
                "playing volume edit preserves device, mixer and playback intent");
            Check(ToggleTrackMute("a") && ReferenceEquals(device, _audioDevice) && _previewPlaying, "playing MUTE preserves the native device");
            await Task.Delay(250);
            Check(device.PositionFrames > before && device.Error is null, "native clock continues through live volume and MUTE changes");
            PausePreview();
            File.WriteAllLines(Path.Combine(folder, "performance-result.txt"), report);
        }
        finally
        {
            LoadSmokeProject(original, path ?? Path.Combine(folder, "before-performance.cutmaker"));
            _projectPath = path; _dirty = dirty; _undo.Clear(); _undo.AddRange(undo); _redo.Clear(); _redo.AddRange(redo);
            SetClipSelection(selection ?? [], selected); ApplyLayout(layout); TimelineZoom.Value = zoom; TimelineOffsetSeconds = offset;
            RefreshProject(); RefreshHistoryButtons(); UpdateLayout();
        }
    }
}
