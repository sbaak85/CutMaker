# Architecture

## Foundation

- .NET 10 WPF on Windows x64; native resizable panels, no embedded browser or NuGet packages.
- Core uses immutable records with owned project lists. Schema-one `.cutmaker` files store references and edit decisions, not source media. New optional fields keep older projects readable.
- Project-local FFmpeg/ffprobe child processes use argument lists, no shell interpolation. Tools, layout, previews, backups and verification output remain in ignored local directories.

## Import and timeline

Import normalizes and de-duplicates paths. WIC validates images; Windows MediaPlayer checks native playback compatibility and ffprobe supplies precise duration within a shared timeout. Windows can report rounded MP4 durations despite playing the complete fractional tail; native duration metadata is not source timing authority. Supported inputs remain MP4, MP3, WAV, PNG and JPEG. Images start at five seconds but can be extended. Older nonzero image source offsets remain readable; new image trims/splits normalize to zero.

Timeline drawing is viewport-sized. Hit testing, snapping, zoom, drag/drop and trimming share scale/offset. Ctrl+wheel anchors the time under the pointer. Pointer edge drags apply a delta to the actual edge, avoiding a jump from the initial grab point. Lock, compatible media and same-track overlap checks happen before mutation. Group moves preserve track arrangement; individual unlinked clips may cross compatible tracks.

`TimelineBatchEditor` plans atomic move, replacement, paste, split, deletion and same-track ripple deletion. Linked partners expand before validation. Ripple rejects changes that would desynchronize remaining linked groups. Clipboard duplicates receive fresh clip/link IDs. Extraction creates an audio track referencing the same video asset and sets `SourceAudioMuted` on the original, preventing gain changes from restoring duplicate audio. Linked move/trim/split/delete stays synchronized; track deletion explicitly unlinks surviving partners.

Undo snapshots own lists and absolute source paths, retaining up to 100 actions. A committed group operation takes one snapshot; project switches clear history and the internal clipboard.

## Effects and rendering

`FadeEnvelope` provides numeric evaluation and FFmpeg expressions for five presets plus a cubic curve with two intensity controls at fixed time positions. RangeStart/RangeEnd retain the curve segment across razor cuts, including overlapping fades and separate audio envelopes. Manual trims reanchor/clamp outer fades; changing a fade duration or curve starts a fresh full interval.

`MediaRenderService` snapshots the project, probes intersecting sources, applies source trim and timeline offset, letterboxes/scales images, layers video, applies fades/gains, mixes audio and limits peaks. Upper video tracks overlay lower ones. Muting video hides both picture and sound. Opacity fading exposes lower layers; Black fading changes luminance/chroma while retaining cover. Audio may share the video envelopes or use independent ones. Empty/muted gaps remain black and silent and still contribute timeline duration.

Output ranges decode only intersecting clips. Source seek includes the range offset, while Fade expressions retain original local clip time. Frame-only output shares this composition graph and omits audio. Export supports MP4 H.264/AAC and MP3, three video quality choices and 128/192/256/320 kbps. Video dimensions/FPS persist in the project; quality/bitrate/range are session preferences.

Each output is written to a unique sibling and atomically replaced after success. Cancellation kills child processes; failures preserve existing destinations. Source paths and the current project cannot be chosen as output. Progress and bounded error logs are asynchronous.

## Preview and media overviews

Seek requests debounce for 110 ms, cancel previous work and produce one composited frame. Revision/request guards prevent stale frames publishing after edits, switches or newer seeks. Playback prepares at most 20 seconds from the current playhead and uses one WPF MediaElement for synchronized A/V. Native clock positions map to global timeline seconds. At segment end the next segment is prepared, with a possible processing pause. This is bounded rendered playback, not realtime per-track decoding or seamless prefetch.

`MediaVisualCache` generates 160x90 source thumbnails and a 1024-bin waveform from streamed PCM, with fixed decode buffers, two concurrent workers and bounded time/cache size. Keys include absolute path, file metadata and asset duration. Frozen images return to the UI. Updating a lane's draw data preserves bindings, mouse capture and in-progress inspector edits. Waveforms use source coordinates and do not include clip gain/fades.

## Persistence and recovery

Project saves validate and atomically replace the destination; failed saves leave the previous file intact. Missing sources are allowed on project load and visibly marked. Relinking preserves asset IDs, requires the same media kind and sufficient source duration, validates the whole project and supports Undo. Stale asynchronous relink results cannot enter a changed project.

Every 30 seconds, dirty work is captured with absolute media paths and atomically written to a separately leased recovery file. Each window owns its own lease. Discovery skips active sessions and isolates malformed snapshots. Restoration opens a dirty working copy rather than overwriting the original project. Successful save, accepted discard/project switch and normal close clear only that session. Cancellation plus the store lock prevents an older writer resurrecting a removed snapshot.

## Verification

`scripts/verify.ps1` builds Release, runs standalone core checks, offscreen WPF workflows and real FFmpeg render checks. Coverage includes native import, placement, inspector/custom Fade edits, batch/link operations, lock/overlap rejection, Undo/Redo, recovery isolation/races, relink, real waveforms, source hashes, save/reopen, global preview seeking, bounded playback, EOF frames, output settings and small-window layout.

Render checks decode actual pixels and audio to verify layering, trims, fades, independent audio, black mode, gain/mix, range timing, portrait/FPS/bitrate, extraction muting, cancellation and source protection. Reports and PNGs are under ignored `runtime/verification/smoke/` and `runtime/render-checks/`.

Optional `CUTMAKER_SMOKE_EXTRA_MEDIA` accepts a JSON array of real MP4/MP3 paths. Local verification also uses Mozilla CC0 flower.mp4 and t-rex-roar.mp3 fixtures. Offscreen shared-handler/native playback checks are not physical mouse testing or long-project performance certification.
