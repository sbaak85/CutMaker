# Architecture

## Foundation

- .NET 10 WPF on Windows x64, resizable native panels; no embedded browser or NuGet packages.
- Core uses immutable records with owned project lists: source references, timeline seconds, trims, split, move validation and atomic JSON save.
- `.cutmaker` schema 1 stores editing decisions, not source media. Sources are never rewritten by editing.
- Preview/export use project-local FFmpeg/ffprobe child processes, argument lists (no shell) and a shared filter graph.
- The launcher isolates tools, layout, caches and test output inside ignored local directories.

## Import and timeline

Import plans normalized paths and de-duplicates including relative project references. WIC validates still images. Windows MediaPlayer checks native audio/video compatibility and ffprobe provides precise duration under a shared timeout/cancellation budget. Windows can report whole-second MP4 durations even while playing the complete fractional tail; its duration metadata is not used for source timing. MP4, MP3, WAV, PNG and JPEG are currently allowed. Stills have five seconds usable duration. Each imported source is referenced in place; failed files do not prevent other imports. Project generations prevent stale asynchronous imports or drag payloads entering a new project.

Internal library drags use window/project/asset identity. The timeline draws only the viewport; scale and offset are shared by rendering, hit testing, trim/move/drop and ruler. Existing clips move across compatible unlocked tracks; source offsets are preserved. Edges trim against source bounds and reject same-track overlap. Gold top handles adjust Fade durations. The inspector validates all values before committing a single edit.

Snapshots own their lists and normalize source paths to absolute references, so Undo remains correct across Save As. The history retains up to 100 actions and resets on project switch. Inspector changes, drags and splits are recorded once per committed operation. Library imports are recorded per successful asset.

## Preview and export

A single `MediaRenderService` handles MP4, MP3 and preview. It snapshots the project before async processing, probes streams, seeks/trims each source, places it in timeline time, scales/letterboxes and layers video, applies clip Fade envelopes, mixes original video audio plus audio tracks, applies clip/track gains and a peak limiter. First video track is on top. Muting a video track hides its image and audio. Gaps remain black/silent and muted tails still determine duration.

Fade math is shared in the render graph: Linear, square EaseIn, quadratic EaseOut, cubic SmoothStep and sine EqualPower. The envelope multiplies image alpha and audio samples. Fade over a lower image reveals that image; over the black canvas it fades to black. No independent global fade-to-black is claimed.

A preview request renders a lower-resolution MP4, then uses one native WPF MediaElement for synchronized playback and seeking. Timeline edits cancel obsolete requests, invalidate the cache and stop old playback. Requests snapshot absolute paths and use revision checks; stale continuations cannot reopen old preview media after editing or switching projects. Playback timer reads the native player clock, not an independently accumulating time estimate.

Export snapshots permit ongoing edits. Progress is async; cancel kills the child process tree. Files are rendered to unique siblings, replaced atomically only after success, and temporary files are cleaned up. Export refuses a destination matching any source or the project. Cancellation/failure retain existing outputs. Tool errors include bounded stderr. Paths are passed as ProcessStartInfo.ArgumentList entries, including Unicode, spaces and shell punctuation.

This prototype renders before playback, not realtime per-track decoding. Long projects need preparation time. No output range, proxy management, arbitrary Bezier, waveform, multiselection or autosave UI is claimed.

## Invariants

All starts/source offsets are finite and nonnegative; durations positive; source ranges within assets; fades within clip length; gains 0–4; identifiers unique and references present. Cutting inside an active Fade is explicitly rejected until envelope segmentation exists; other splits preserve outer fades and source continuity. Existing projects remain intact on failed save. Missing sources are permitted on load and reported; rendering fails clearly when needed sources are missing.

## Verification

`scripts/verify.ps1` builds, runs standalone core checks, offscreen WPF smoke with actual imports and native preview playback, then standalone FFmpeg output checks. Smoke checks use the same editing and inspector application methods as UI. They cover library payloads, placement, move/trim/split/history, track settings, stale operations, saved data, unchanged source hashes, layout persistence and four screen sizes. It renders PNGs under `runtime/verification/smoke/`.

Render checks synthesize sources, export MP4/MP3, inspect codecs/duration, decode pixels for layer/source/fade timing, decode audio for gain/mix/envelope checks, exercise images/silence, and verify failure/cancel/source protection. Results go to `runtime/render-checks/`. Native smoke validates MediaOpened, seeking and actual player-clock advancement. This is not physical Explorer drag testing or long-project performance certification.

Optional `CUTMAKER_SMOKE_EXTRA_MEDIA` accepts a JSON array of real MP4/MP3 paths. Local validation also uses Mozilla CC0 `flower.mp4` and `t-rex-roar.mp3` fixtures, downloaded only under ignored runtime data.

References: [WPF](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/overview/), [FFmpeg filters](https://ffmpeg.org/ffmpeg-filters.html), [FFmpeg download](https://ffmpeg.org/download.html).
