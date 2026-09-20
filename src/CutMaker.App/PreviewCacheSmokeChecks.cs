using System.IO;
using System.Diagnostics;
using System.Windows;
using CutMaker.Core;

namespace CutMaker.App;

public partial class MainWindow
{
    internal async Task RunPreviewCacheSmokeAsync(string folder)
    {
        void Require(bool condition, string reason)
        { if (!condition) throw new InvalidOperationException("Preview cache regression: " + reason); }

        var report = Path.Combine(folder, "preview-cache-result.txt");
        File.WriteAllText(report, "RUNNING: long-song cold startup and native streaming audio.\n");
        var original = _project;
        var originalPath = _projectPath;
        var dirty = _dirty;
        var undo = _undo.ToArray(); var redo = _redo.ToArray();
        var selected = SelectedTimelineClipId; var selection = SelectionIds().ToArray();
        var selectedTrack = _selectedTrackId; var collapsed = _collapsedTracks.ToArray();
        var playhead = PlayheadSeconds;
        var source = Path.GetFullPath(Path.Combine(folder, "異星長夜_245秒_冷啟動 #%.mp3"));
        using var fixtureTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await MediaRenderService.RunToolAsync(MediaRenderService.FindTool("ffmpeg.exe"),
            ["-hide_banner", "-nostdin", "-y", "-v", "error", "-f", "lavfi", "-i",
                "sine=frequency=523:sample_rate=44100:duration=245", "-c:a", "libmp3lame", "-b:a", "192k", source], fixtureTimeout.Token);
        var sourceWriteTime = File.GetLastWriteTimeUtc(source);
        var fixture = CutProject.CreateEmpty("Native audio preview cache") with
        {
            Video = new(160, 90, 30),
            MediaAssets = [new("cache-source", source, MediaKind.Audio, 245)],
            Tracks = [new("cache-video", "Empty video", TrackKind.Video), new("cache-audio", "Music", TrackKind.Audio)],
            Clips = [new("cache-clip", "cache-source", "cache-audio", 0, 0, 245)]
        };
        try
        {
            LoadSmokeProject(fixture, Path.Combine(folder, "preview-cache.cutmaker"));
            PreviewPlayer.IsMuted = true;
            SeekPreview(2.5);
            var timer = Stopwatch.StartNew();
            await PreparePreviewAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var coldMilliseconds = timer.ElapsedMilliseconds;
            Require(PreviewIsReady && _audioDevice is not null && _audioPreview is not null && _previewFile is null && PreviewPlayer.Source is null,
                "Long audio must become ready without a mixed WAV or MediaElement opening: " + PreviewHint.Text);
            Require(_previewRangeStart == 0 && Math.Abs(_previewRangeEnd - 245) < 1e-8,
                "Preparation from a later playhead must include the entire timeline from zero.");
            string[] CacheFiles() => Directory.GetFiles(Path.Combine(LayoutSettings.DataDirectory, "audio-preview"), "*.f64")
                .OrderBy(path => path, StringComparer.Ordinal).ToArray();
            var cachedSources = CacheFiles();
            var baselinePlayer = _audioDevice;
            var baselineMixer = _audioPreview;
            var baselineRevision = _previewRevision;
            await Task.Delay(150);
            Require(Math.Abs(PreviewPositionSeconds - 2.5) < .0001,
                "Opening the full cache must retain the latest global playhead.");
            StartPreviewPlayback();
            PreviewPlayer_MediaEnded(PreviewPlayer, new RoutedEventArgs());
            var deadline = DateTime.UtcNow.AddSeconds(4);
            while (PlayheadSeconds < 2.7 && DateTime.UtcNow < deadline) await Task.Delay(50);
            Require(_previewPlaying && PlayheadSeconds >= 2.7,
                "Native PCM playback must advance its real media clock and the timeline.");
            PausePreview();

            NavigatePlayhead(.8, preciseFrame: true);
            Require(PreviewIsReady && PreviewHint.Visibility == Visibility.Collapsed,
                "Frame navigation in a ready audio-only project must not display a false preparation prompt.");

            foreach (var target in new[] { .15, 3.25, 0.0, 1.0 })
            {
                SeekPreview(target);
                await PreparePreviewAsync();
                Require(PreviewIsReady && !_previewPreparing && _previewFile is null &&
                    ReferenceEquals(_audioDevice, baselinePlayer) && ReferenceEquals(_audioPreview, baselineMixer) && _previewRevision == baselineRevision,
                    $"Seeking to {target}s and preparing again must reuse the existing mix plan and native device.");
            }
            SeekPreview(PreviewDuration);
            await TogglePreviewPlaybackAsync();
            Require(_previewPlaying && !_previewPreparing && _previewFile is null &&
                ReferenceEquals(_audioDevice, baselinePlayer) && PlayheadSeconds < .2,
                "Play at EOF must restart the same full cache without another render.");
            PausePreview();

            var track = _project.Tracks.Single(item => item.Id == "cache-audio");
            Require(ApplyTrackSettings(track with { Name = "Renamed music", Locked = true }), "Rename/lock fixture must apply.");
            Require(PreviewIsReady && ReferenceEquals(_audioDevice, baselinePlayer),
                "Track name and editing lock do not alter media and must preserve ready playback.");
            Require(ApplyTrackSettings(_project.Tracks.Single(item => item.Id == track.Id) with { Locked = false }),
                "Unlock fixture must apply.");
            SelectTimelineClipForChecks("cache-clip");
            Require(SplitSelectedClip(1.75), "Razor fixture must split a source-contiguous audio clip.");
            Require(PreviewIsReady && ReferenceEquals(_audioDevice, baselinePlayer),
                "A source-contiguous razor cut must reuse the unchanged sound cache.");

            SeekPreview(1.6); StartPreviewPlayback();
            deadline = DateTime.UtcNow.AddSeconds(3);
            while (PlayheadSeconds < 2.05 && DateTime.UtcNow < deadline) await Task.Delay(30);
            Require(_previewPlaying && PlayheadSeconds >= 2.05 && ReferenceEquals(_audioDevice, baselinePlayer),
                "Playback must cross an edited clip boundary on one continuous device queue.");
            PausePreview();

            Require(ApplyTrackSettings(_project.Tracks.Single(item => item.Id == track.Id) with { Volume = .5 }),
                "Volume fixture must apply.");
            Require(!PreviewIsReady && !_previewPlaying && PreviewPlayer.Source is null && _audioDevice is null,
                "A real volume edit must close obsolete playback while retaining decoded sources.");
            timer.Restart();
            await PreparePreviewAsync();
            var editedMilliseconds = timer.ElapsedMilliseconds;
            Require(PreviewIsReady && !ReferenceEquals(_audioPreview, baselineMixer) && _previewFile is null && cachedSources.SequenceEqual(CacheFiles()),
                "Changed volume must use a new live mix plan with the same decoded sources and no full rendered output.");
            UndoEdit();
            await PreparePreviewAsync();
            Require(PreviewIsReady && _previewFile is null && cachedSources.SequenceEqual(CacheFiles()),
                "Undo must reuse decoded sources without rendering a mixed file.");
            RedoEdit();
            await PreparePreviewAsync();
            Require(PreviewIsReady && _previewFile is null && cachedSources.SequenceEqual(CacheFiles()),
                "Redo must rebuild only the lightweight live mix plan.");

            Require(ApplyTrackSettings(_project.Tracks.Single(item => item.Id == track.Id) with { Volume = .65 }),
                "Pending navigation fixture must apply.");
            var preparation = PreparePreviewAsync(playWhenReady: true);
            var navigationCancellation = _previewCancellation;
            Require(_previewPreparing && navigationCancellation is not null, "Navigation fixture must have an outstanding real render.");
            NavigatePlayhead(.35);
            Require(_previewPreparing && ReferenceEquals(_previewCancellation, navigationCancellation) &&
                !navigationCancellation!.IsCancellationRequested && !_previewPlayWhenReady,
                "Navigation must cancel autoplay while allowing the same pending full buffer to finish.");
            await preparation;
            Require(PreviewIsReady && !_previewPlaying && Math.Abs(PlayheadSeconds - .35) < 1e-8,
                "Preparation must finish paused at the position selected while it was running.");

            Require(ApplyTrackSettings(_project.Tracks.Single(item => item.Id == track.Id) with { Volume = .8 }),
                "Pending Stop fixture must apply.");
            preparation = PreparePreviewAsync(playWhenReady: true);
            var stopCancellation = _previewCancellation;
            Require(_previewPreparing && stopCancellation is not null, "Stop fixture must have an outstanding real render.");
            PreviewStop_Click(this, new RoutedEventArgs());
            Require(_previewPreparing && ReferenceEquals(_previewCancellation, stopCancellation) &&
                !stopCancellation!.IsCancellationRequested && !_previewPlayWhenReady,
                "Stop must clear autoplay without discarding pending buffered work.");
            await preparation;
            Require(PreviewIsReady && !_previewPlaying && PlayheadSeconds == 0,
                "The buffer must remain ready and paused at zero after Stop during preparation.");

            var beforeSourceChange = _audioPreview;
            File.SetLastWriteTimeUtc(source, sourceWriteTime.AddSeconds(2));
            await PreparePreviewAsync();
            Require(PreviewIsReady && !ReferenceEquals(_audioPreview, beforeSourceChange) && !cachedSources.SequenceEqual(CacheFiles()),
                "A changed source file timestamp must prevent reusing stale decoded or mixed audio.");
            var unmutedMixer = _audioPreview;
            Require(ApplyTrackSettings(_project.Tracks.Single(item => item.Id == track.Id) with { Muted = true }),
                "Mute fixture must apply.");
            Require(!PreviewIsReady && !_previewPlaying && PreviewPlayer.Source is null,
                "MUTE must stop the previous unmuted file immediately.");
            await PreparePreviewAsync();
            Require(PreviewIsReady && !ReferenceEquals(_audioPreview, unmutedMixer) && _previewFile is null,
                "An all-muted audio timeline must use a silent live mix without rendering a file.");
            SeekPreview(244.88); StartPreviewPlayback();
            deadline = DateTime.UtcNow.AddSeconds(3);
            while (_previewPlaying && DateTime.UtcNow < deadline) await Task.Delay(20);
            Require(!_previewPlaying && PlayheadSeconds == 245, "Streaming audio must stop at the exact long-song endpoint.");
            UndoEdit(); await PreparePreviewAsync();
            await RunImeTransportSmokeAsync(folder);
            await RunTransportSmokeAsync(folder);
            await PcmAudioDeviceSmokeChecks.RunAsync(folder);

            File.WriteAllText(report,
                $"PASS: cold 245-second MP3 preparation {coldMilliseconds} ms; volume edit {editedMilliseconds} ms; no mixed WAV or MediaElement; " +
                "real native PCM clock, precise pause/seek and long-song EOF; forward/backward seeks and replay reuse the live mixer/device; " +
                "rename/lock and a source-contiguous razor split preserve ready playback; real volume edits close stale playback; " +
                "Undo/Redo reuse decoded sources; navigation and Stop during preparation retain loading and cancel autoplay; " +
                "source changes invalidate stale media; MUTE yields a silent live mix; IME Space and native device lifecycle checks pass.\n");
        }
        finally
        {
            PausePreview();
            File.SetLastWriteTimeUtc(source, sourceWriteTime);
            LoadSmokeProject(original, originalPath ?? Path.Combine(folder, "before-preview-cache.cutmaker"));
            _projectPath = originalPath; _dirty = dirty;
            _undo.Clear(); _undo.AddRange(undo); _redo.Clear(); _redo.AddRange(redo);
            _collapsedTracks.UnionWith(collapsed); _selectedTrackId = selectedTrack;
            SetClipSelection(selection, selected);
            SeekPreview(playhead);
            RefreshProject(); RefreshHistoryButtons(); UpdateLayout();
        }
    }
}
