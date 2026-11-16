using System.Diagnostics;
using System.IO;
using System.Windows;
using CutMaker.Core;

namespace CutMaker.App;

public partial class MainWindow
{
    /// <summary>Edits after the one-time source store must not render, re-cache or delay the picture.</summary>
    internal async Task RunLivePreviewSmokeAsync(string folder)
    {
        void Require(bool condition, string reason)
        { if (!condition) throw new InvalidOperationException("Live preview regression: " + reason); }

        var original = _project; var originalPath = _projectPath; var dirty = _dirty;
        var undo = _undo.ToArray(); var redo = _redo.ToArray();
        var selected = SelectedTimelineClipId; var selection = SelectionIds().ToArray();
        var selectedTrack = _selectedTrackId; var playhead = PlayheadSeconds;
        var rendered = UseRenderedPreviewForChecks;
        var source = Path.GetFullPath(Path.Combine(folder, "live-preview-source.mp4"));
        using (var fixtureTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            await MediaRenderService.RunToolAsync(MediaRenderService.FindTool("ffmpeg.exe"),
                ["-hide_banner", "-nostdin", "-y", "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=30:duration=4",
                    "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=4", "-c:v", "libx264", "-preset", "ultrafast",
                    "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", source], fixtureTimeout.Token);
        var fixture = CutProject.CreateEmpty("Live preview") with
        {
            Video = new(320, 180, 30),
            MediaAssets = [new("live-source", source, MediaKind.Video, 4)],
            Tracks = [new("live-video", "Video", TrackKind.Video), new("live-audio", "Audio", TrackKind.Audio)],
            Clips = [new("live-clip", "live-source", "live-video", 0, 0, 4)]
        };
        try
        {
            UseRenderedPreviewForChecks = false;
            LoadSmokeProject(fixture, Path.Combine(folder, "live-preview.cutmaker"));
            InvalidatePreview();
            var size = EditorPreferences.Current.PreviewWidth;
            var clock = Stopwatch.StartNew();
            await CacheLivePreviewAsync(wholeLibrary: true, audio: true).WaitAsync(TimeSpan.FromSeconds(60));
            var cacheMilliseconds = clock.ElapsedMilliseconds;
            var store = VideoFrameCache.TryGet(source, size);
            Require(store is { Complete: true } && Math.Abs(store.Count - 120) <= 2 && Math.Abs(store.Fps - 30) < .01,
                $"The one-time source store must hold every frame: {store?.Count} at {store?.Fps} fps; {StatusText.Text}");
            string[] Files(string name) => Directory.Exists(Path.Combine(EditorPreferences.Root, name))
                ? Directory.GetFiles(Path.Combine(EditorPreferences.Root, name)).OrderBy(path => path, StringComparer.Ordinal).ToArray() : [];
            var frames = Files(VideoFrameCache.Folder); var regions = Files("video-regions"); var sounds = Files("audio-preview");

            SeekPreview(1.0);
            Require(PreviewLiveView.Visibility == Visibility.Visible && PreviewLiveView.Layers.Count == 1 &&
                PreviewStillImage.Visibility != Visibility.Visible && _previewFrameTask.IsCompleted,
                "A stored source must show its frame synchronously, without a render process.");
            var frameAtOne = PreviewLiveView.Layers[0].Image;

            clock.Restart();
            await PreparePreviewAsync();
            var firstMilliseconds = clock.ElapsedMilliseconds;
            Require(PreviewIsReady && _audioDevice is not null && _previewFile is null && PreviewPlayer.Source is null,
                "Live playback uses the real-time mixer and no rendered timeline file: " + PreviewHint.Text);
            StartPreviewPlayback();
            var deadline = DateTime.UtcNow.AddSeconds(4);
            while (PlayheadSeconds < 1.3 && DateTime.UtcNow < deadline) await Task.Delay(30);
            Require(_previewPlaying && PlayheadSeconds >= 1.3 && PreviewLiveView.Visibility == Visibility.Visible &&
                !ReferenceEquals(PreviewLiveView.Layers[0].Image, frameAtOne), "Playback must advance the composited picture with the audio clock.");
            PausePreview();

            // Move: the same source frame must appear half a second later, immediately.
            SeekPreview(1.5);
            var clip = _project.Clips.Single();
            SelectTimelineClipForChecks(clip.Id);
            Require(TryCommitGroupMove(clip with { Start = .5 }), "Move fixture must apply: " + StatusText.Text);
            Require(PreviewLiveView.Visibility == Visibility.Visible && PreviewLiveView.Layers.Count == 1 &&
                ReferenceEquals(PreviewLiveView.Layers[0].Image, frameAtOne) && PreviewStillImage.Visibility != Visibility.Visible,
                "Moving a clip must update the picture at once from the stored source frames.");
            clock.Restart();
            await PreparePreviewAsync();
            var editMilliseconds = clock.ElapsedMilliseconds;
            Require(PreviewIsReady && _previewFile is null && editMilliseconds < 3000,
                $"Replaying after a move must not rebuild a cache ({editMilliseconds} ms).");
            SeekPreview(.2);
            Require(PreviewLiveView.Visibility == Visibility.Visible && PreviewLiveView.Layers.Count == 0, "A timeline gap is a black frame.");

            // Trim, razor cut and Fade reuse the same store.
            clip = _project.Clips.Single();
            Require(TryReplaceClip(PlanPointerTrim(clip, true, clip.Start, clip.Start + .5), "Live trim"), "Trim fixture must apply.");
            clip = _project.Clips.Single();
            Require(Math.Abs(clip.Start - 1) < 1e-6 && Math.Abs(clip.SourceIn - .5) < 1e-6, "Trim fixture keeps the source aligned.");
            SeekPreview(1.5);
            Require(PreviewLiveView.Layers.Count == 1 && ReferenceEquals(PreviewLiveView.Layers[0].Image, frameAtOne),
                "A trimmed clip maps the playhead to the same source frame.");
            SelectTimelineClipForChecks(clip.Id);
            Require(SplitSelectedClip(2.0), "Razor fixture must apply.");
            SeekPreview(1.5);
            Require(PreviewLiveView.Layers.Count == 1 && ReferenceEquals(PreviewLiveView.Layers[0].Image, frameAtOne), "A razor cut keeps the picture.");
            var first = _project.Clips.OrderBy(item => item.Start).First();
            Require(TryReplaceClip(first with { FadeIn = new FadeSettings(1) }, "Live fade"), "Fade fixture must apply.");
            SeekPreview(first.Start + .25);
            Require(PreviewLiveView.Layers.Count == 1 && Math.Abs(PreviewLiveView.Layers[0].Opacity - .25) < .02 && !PreviewLiveView.Layers[0].BlackFade,
                "A Fade is applied while compositing, not by re-rendering.");
            UpdateLayout();
            var shot = new System.Windows.Media.Imaging.RenderTargetBitmap(Math.Max(1, (int)PreviewSurface.ActualWidth),
                Math.Max(1, (int)PreviewSurface.ActualHeight), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            shot.Render(PreviewSurface);
            var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
            png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(shot));
            using (var stream = File.Create(Path.Combine(folder, "live-preview-frame.png"))) png.Save(stream);
            clock.Restart();
            await PreparePreviewAsync();
            var fadeMilliseconds = clock.ElapsedMilliseconds;
            Require(PreviewIsReady && fadeMilliseconds < 3000, $"Replaying after trim, cut and Fade must be immediate ({fadeMilliseconds} ms).");
            UndoEdit();
            SeekPreview(first.Start + .25);
            Require(PreviewLiveView.Layers.Count == 1 && PreviewLiveView.Layers[0].Opacity >= 1, "Undo restores the picture without a cache.");
            Require(frames.SequenceEqual(Files(VideoFrameCache.Folder)) && regions.SequenceEqual(Files("video-regions")) &&
                sounds.SequenceEqual(Files("audio-preview")), "Edits must not create, replace or remove any cache file.");

            File.WriteAllText(Path.Combine(folder, "live-preview-result.txt"),
                $"PASS: one-time source store {cacheMilliseconds} ms ({store!.Count} frames); first play {firstMilliseconds} ms; replay after move {editMilliseconds} ms, " +
                $"after trim/cut/Fade {fadeMilliseconds} ms; picture updates synchronously after move, trim, razor cut, Fade and Undo; " +
                "gaps are black; playback follows the real-time audio clock; no rendered timeline file and no cache file changed by edits.\n");
        }
        finally
        {
            PausePreview();
            UseRenderedPreviewForChecks = rendered;
            CachePreviewButton.Content = "暫存快取";
            LoadSmokeProject(original, originalPath ?? Path.Combine(folder, "before-live-preview.cutmaker"));
            _projectPath = originalPath; _dirty = dirty;
            _undo.Clear(); _undo.AddRange(undo); _redo.Clear(); _redo.AddRange(redo);
            _selectedTrackId = selectedTrack;
            SetClipSelection(selection, selected);
            InvalidatePreview();
            SeekPreview(playhead);
            RefreshProject(); RefreshHistoryButtons(); UpdateLayout();
        }
    }
}
