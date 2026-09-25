using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using CutMaker.Core;

namespace CutMaker.App;

/// <summary>
/// A small session-owned LRU of completed full-timeline previews. The editor owns the
/// active MediaElement; close it before removing a file. No source media is admitted here.
/// </summary>
internal sealed class PreviewRenderCache : IDisposable
{
    private sealed record Entry(string Key, string Path, long Bytes);
    private readonly string _folder;
    private readonly int _maxEntries;
    private readonly long _maxBytes;
    private readonly LinkedList<Entry> _entries = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _byKey = new(StringComparer.Ordinal);

    internal PreviewRenderCache(string sessionFolder, int maxEntries = 4, long maxBytes = 512L * 1024 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxEntries, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);
        _folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sessionFolder)) + Path.DirectorySeparatorChar;
        _maxEntries = maxEntries;
        _maxBytes = maxBytes;
    }

    internal bool TryGet(string key, out string path)
    {
        if (_byKey.TryGetValue(key, out var node))
        {
            var file = new FileInfo(node.Value.Path);
            if (file.Exists && file.Length == node.Value.Bytes && file.Length > 0)
            {
                _entries.Remove(node);
                _entries.AddFirst(node);
                path = node.Value.Path;
                return true;
            }
            RemoveNode(node);
        }
        path = string.Empty;
        return false;
    }

    internal void Add(string key, string path)
    {
        path = Path.GetFullPath(path);
        if (!path.StartsWith(_folder, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Preview cache files must belong to its session folder.", nameof(path));
        var file = new FileInfo(path);
        if (!file.Exists || file.Length == 0) throw new IOException("Cannot cache an incomplete preview.");
        // A file has only one owner/key, so eviction cannot accidentally delete another entry.
        foreach (var node in _entries.Where(entry => string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            var existing = _byKey[node.Key];
            _entries.Remove(existing);
            _byKey.Remove(node.Key);
        }
        Remove(key);
        var added = _entries.AddFirst(new Entry(key, path, file.Length));
        _byKey.Add(key, added);
        // Keep the newest usable result even when one long project exceeds the soft byte cap.
        while (_entries.Count > 1 && (_entries.Count > _maxEntries || _entries.Sum(entry => entry.Bytes) > _maxBytes))
            RemoveNode(_entries.Last!);
    }

    internal bool ContainsFile(string? path) => path is not null && _entries.Any(entry =>
        string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase));

    internal void Remove(string key)
    {
        if (_byKey.TryGetValue(key, out var node)) RemoveNode(node);
    }

    internal void RemoveFile(string? path)
    {
        if (path is null) return;
        foreach (var entry in _entries.Where(entry => string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase)).ToArray())
            Remove(entry.Key);
    }

    internal void Clear()
    {
        while (_entries.Last is { } node) RemoveNode(node);
    }

    public void Dispose() => Clear();

    private void RemoveNode(LinkedListNode<Entry> node)
    {
        _entries.Remove(node);
        _byKey.Remove(node.Value.Key);
        try { File.Delete(node.Value.Path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal static bool IsAudioOnly(CutProject project)
    {
        var visualTracks = project.Tracks.Where(track => !track.Muted && track.Kind == TrackKind.Video).Select(track => track.Id).ToHashSet();
        var assets = project.MediaAssets.ToDictionary(asset => asset.Id);
        return !project.Clips.Any(clip => visualTracks.Contains(clip.TrackId) &&
            assets.TryGetValue(clip.AssetId, out var asset) && asset.Kind != MediaKind.Audio);
    }

    /// <summary>
    /// The snapshot must contain absolute source paths. UI-only names, locks, IDs, selection,
    /// playhead and unused library entries are excluded; source replacements invalidate the key.
    /// </summary>
    internal static string CreateKey(CutProject project)
    {
        var audioOnly = IsAudioOnly(project);
        var duration = project.Clips.Count == 0 ? 0 : project.Clips.Max(clip => clip.End);
        var assets = project.MediaAssets.ToDictionary(asset => asset.Id);
        var sourceKeys = new Dictionary<string, string>(StringComparer.Ordinal);
        string Source(MediaAsset asset)
        {
            if (sourceKeys.TryGetValue(asset.Id, out var key)) return key;
            var path = Path.GetFullPath(asset.Path);
            var file = new FileInfo(path);
            key = string.Join('\n', OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path,
                ((int)asset.Kind).ToString(CultureInfo.InvariantCulture),
                file.Exists ? file.Length.ToString(CultureInfo.InvariantCulture) : "missing",
                file.Exists ? file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture) : "missing");
            sourceKeys.Add(asset.Id, key);
            return key;
        }

        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true);
        writer.Write("CutMaker-full-preview-v1");
        writer.Write(audioOnly);
        writer.Write(AudioSampleClock.At(duration));
        if (!audioOnly)
        {
            writer.Write(duration.ToString("0.#########", CultureInfo.InvariantCulture));
            writer.Write(project.Video.Width);
            writer.Write(project.Video.Height);
            writer.Write(project.Video.Fps);
        }

        // The order of occupied video tracks controls compositing. Empty/hidden tracks have
        // no render contribution, so renaming/locking/adding one does not discard the buffer.
        foreach (var track in project.Tracks.Where(track => !track.Muted))
        {
            var clips = project.Clips.Where(clip => clip.TrackId == track.Id).OrderBy(clip => clip.Start).ToArray();
            var visual = track.Kind == TrackKind.Video ? clips.Where(clip => assets[clip.AssetId].Kind != MediaKind.Audio).ToArray() : [];
            if (visual.Length == 0) continue;
            writer.Write("video-track");
            writer.Write(visual.Length);
            foreach (var clip in visual)
            {
                var asset = assets[clip.AssetId];
                writer.Write(Source(asset));
                writer.Write(clip.Start); writer.Write(clip.SourceIn); writer.Write(clip.Duration);
                WriteFade(writer, clip.FadeIn); WriteFade(writer, clip.FadeOut);
                writer.Write((int)clip.VideoFadeMode);
            }
        }

        foreach (var track in project.Tracks.Where(track => !track.Muted))
        {
            var spans = new List<AudioSpan>();
            foreach (var clip in project.Clips.Where(clip => clip.TrackId == track.Id && !clip.SourceAudioMuted &&
                assets[clip.AssetId].Kind != MediaKind.Image).OrderBy(clip => clip.Start))
            {
                var slice = AudioSampleClock.Slice(clip, 0, duration);
                if (slice.Length == 0) continue;
                var fadeIn = ActiveFade(clip.SeparateAudioFades ? clip.AudioFadeIn : clip.FadeIn);
                var fadeOut = ActiveFade(clip.SeparateAudioFades ? clip.AudioFadeOut : clip.FadeOut);
                var span = new AudioSpan(Source(assets[clip.AssetId]) + "\nratio=" + clip.TimeRatio.ToString("R", CultureInfo.InvariantCulture), slice.TimelineStart, slice.TimelineStart + slice.Length,
                    slice.SourceStart - slice.TimelineStart, clip.Gain * track.Volume, fadeIn, fadeOut, clip.Start, clip.Duration);
                // Only merge provably identical flat sample sequences. General sliced fades
                // remain distinct instead of risking a false hit after a real envelope edit.
                if (spans.LastOrDefault() is { FadeIn: null, FadeOut: null } previous && fadeIn is null && fadeOut is null &&
                    previous.End == span.Start && previous.Source == span.Source && previous.Mapping == span.Mapping && previous.Gain == span.Gain)
                    spans[^1] = previous with { End = span.End };
                else spans.Add(span);
            }
            if (spans.Count == 0) continue;
            writer.Write("audio-track");
            writer.Write(spans.Count);
            foreach (var span in spans)
            {
                writer.Write(span.Source); writer.Write(span.Start); writer.Write(span.End); writer.Write(span.Mapping); writer.Write(span.Gain);
                WriteFade(writer, span.FadeIn); WriteFade(writer, span.FadeOut);
                if (span.FadeIn is not null || span.FadeOut is not null)
                { writer.Write(span.ClipStart); writer.Write(span.ClipDuration); }
            }
        }
        writer.Flush();
        return Convert.ToHexString(SHA256.HashData(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length))));
    }

    private sealed record AudioSpan(string Source, long Start, long End, long Mapping, double Gain,
        FadeSettings? FadeIn, FadeSettings? FadeOut, double ClipStart, double ClipDuration);

    private static FadeSettings? ActiveFade(FadeSettings? fade) => fade is { Duration: > 0 } ? fade : null;

    private static void WriteFade(BinaryWriter writer, FadeSettings? fade)
    {
        fade = ActiveFade(fade);
        writer.Write(fade is not null);
        if (fade is null) return;
        writer.Write(fade.Duration); writer.Write((int)fade.Curve);
        if (fade.Curve == FadeCurve.Custom) { writer.Write(fade.Control1); writer.Write(fade.Control2); }
        writer.Write(fade.RangeStart); writer.Write(fade.RangeEnd);
    }
}
