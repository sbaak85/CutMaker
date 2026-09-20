using System.IO;
using System.Windows;
using CutMaker.Core;

namespace CutMaker.App;

public partial class MainWindow
{
    private async Task RunPreviewCacheSmokeAsync(string folder)
    {
        void Require(bool condition, string reason)
        { if (!condition) throw new InvalidOperationException("Preview cache regression: " + reason); }

        var report = Path.Combine(folder, "preview-cache-result.txt");
        File.WriteAllText(report, "RUNNING: whole-timeline cache and native audio-only playback.\n");
        var original = _project;
        var originalPath = _projectPath;
        var dirty = _dirty;
        var undo = _undo.ToArray(); var redo = _redo.ToArray();
        var selected = SelectedTimelineClipId; var selection = SelectionIds().ToArray();
        var selectedTrack = _selectedTrackId; var collapsed = _collapsedTracks.ToArray();
        var playhead = PlayheadSeconds;
        var source = Path.GetFullPath(Path.Combine(folder, "preview-cache-source.wav"));
        using var fixtureTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await MediaRenderService.RunToolAsync(MediaRenderService.FindTool("ffmpeg.exe"),
            ["-hide_banner", "-nostdin", "-y", "-v", "error", "-f", "lavfi", "-i",
                "sine=frequency=523:sample_rate=44100:duration=4", "-c:a", "pcm_s16le", source], fixtureTimeout.Token);
        var sourceWriteTime = File.GetLastWriteTimeUtc(source);
        var fixture = CutProject.CreateEmpty("Native audio preview cache") with
        {
            Video = new(160, 90, 30),
            MediaAssets = [new("cache-source", source, MediaKind.Audio, 4)],
            Tracks = [new("cache-video", "Empty video", TrackKind.Video), new("cache-audio", "Music", TrackKind.Audio)],
            Clips = [new("cache-clip", "cache-source", "cache-audio", 0, 0, 4)]
        };
        try
        {
            LoadSmokeProject(fixture, Path.Combine(folder, "preview-cache.cutmaker"));
            SeekPreview(2.5);
            await PreparePreviewAsync();
            Require(PreviewIsReady && _previewFile is not null && Path.GetExtension(_previewFile) == ".wav",
                "An audio-only timeline must open a native PCM cache, including when an empty video track exists: " + PreviewHint.Text);
            Require(_previewRangeStart == 0 && Math.Abs(_previewRangeEnd - 4) < 1e-8,
                "Preparation from a later playhead must include the entire timeline from zero.");
            Require(PreviewPlayer.NaturalDuration.HasTimeSpan && PreviewPlayer.NaturalVideoWidth == 0,
                "Windows must load the audio-only cache without a black video stream.");
            var baselineFile = _previewFile!;
            var baselineWrite = File.GetLastWriteTimeUtc(baselineFile);
            var baselinePlayer = PreviewPlayer;
            var baselineRevision = _previewRevision;
            await Task.Delay(150);
            Require(Math.Abs(PreviewPlayer.Position.TotalSeconds - 2.5) < .12,
                "Opening the full cache must retain the latest global playhead.");
            StartPreviewPlayback();
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
                Require(PreviewIsReady && !_previewPreparing && _previewFile == baselineFile &&
                    ReferenceEquals(PreviewPlayer, baselinePlayer) && _previewRevision == baselineRevision &&
                    File.GetLastWriteTimeUtc(baselineFile) == baselineWrite,
                    $"Seeking to {target}s and preparing again must reuse the existing file and native player.");
            }
            SeekPreview(PreviewDuration);
            await TogglePreviewPlaybackAsync();
            Require(_previewPlaying && !_previewPreparing && _previewFile == baselineFile &&
                ReferenceEquals(PreviewPlayer, baselinePlayer) && PlayheadSeconds < .2,
                "Play at EOF must restart the same full cache without another render.");
            PausePreview();

            var track = _project.Tracks.Single(item => item.Id == "cache-audio");
            Require(ApplyTrackSettings(track with { Name = "Renamed music", Locked = true }), "Rename/lock fixture must apply.");
            Require(PreviewIsReady && _previewFile == baselineFile && ReferenceEquals(PreviewPlayer, baselinePlayer),
                "Track name and editing lock do not alter media and must preserve ready playback.");
            Require(ApplyTrackSettings(_project.Tracks.Single(item => item.Id == track.Id) with { Locked = false }),
                "Unlock fixture must apply.");
            SelectTimelineClipForChecks("cache-clip");
            Require(SplitSelectedClip(1.75), "Razor fixture must split a source-contiguous audio clip.");
            Require(PreviewIsReady && _previewFile == baselineFile && ReferenceEquals(PreviewPlayer, baselinePlayer),
                "A source-contiguous razor cut must reuse the unchanged sound cache.");

            Require(ApplyTrackSettings(_project.Tracks.Single(item => item.Id == track.Id) with { Volume = .5 }),
                "Volume fixture must apply.");
            Require(!PreviewIsReady && !_previewPlaying && PreviewPlayer.Source is null && File.Exists(baselineFile),
                "A real volume edit must close obsolete playback while retaining its file for Undo.");
            await PreparePreviewAsync();
            Require(PreviewIsReady && _previewFile != baselineFile,
                "Changed volume must use a different rendered mix.");
            var quieterFile = _previewFile;
            UndoEdit();
            await PreparePreviewAsync();
            Require(PreviewIsReady && _previewFile == baselineFile && File.GetLastWriteTimeUtc(baselineFile) == baselineWrite,
                "Undo must reopen the prior prepared file without rewriting its audio.");
            RedoEdit();
            await PreparePreviewAsync();
            Require(PreviewIsReady && _previewFile == quieterFile,
                "Redo must reopen its already prepared mix.");

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

            var beforeSourceChange = _previewFile;
            File.SetLastWriteTimeUtc(source, sourceWriteTime.AddSeconds(2));
            await PreparePreviewAsync();
            Require(PreviewIsReady && _previewFile != beforeSourceChange,
                "A changed source file timestamp must prevent reusing stale decoded or mixed audio.");
            var unmutedFile = _previewFile;
            Require(ApplyTrackSettings(_project.Tracks.Single(item => item.Id == track.Id) with { Muted = true }),
                "Mute fixture must apply.");
            Require(!PreviewIsReady && !_previewPlaying && PreviewPlayer.Source is null,
                "MUTE must stop the previous unmuted file immediately.");
            await PreparePreviewAsync();
            Require(PreviewIsReady && _previewFile != unmutedFile && Path.GetExtension(_previewFile) == ".wav",
                "An all-muted audio timeline must prepare a new silent audio cache.");

            File.WriteAllText(report,
                "PASS: real native PCM-only loading and advancing media clock; prepare from mid-song covers 0 through EOF; " +
                "forward/backward seeks, repeated preparation and EOF replay reuse the file and player; " +
                "rename/lock and a source-contiguous razor split preserve ready playback; real volume edits close stale playback; " +
                "Undo/Redo reuse earlier rendered files; navigation and Stop during real preparation retain buffering and cancel autoplay; " +
                "source metadata changes invalidate stale media; MUTE replaces unmuted audio with a distinct silent cache.\n");
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
