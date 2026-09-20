using System.IO;
using CutMaker.Core;

namespace CutMaker.App;

public partial class MainWindow
{
    private sealed record AssetVisualState(string Key, MediaVisualSet Visuals);
    private readonly Dictionary<string, AssetVisualState> _assetVisuals = [];
    private readonly HashSet<string> _assetVisualPending = [];
    private CancellationTokenSource _mediaVisualCancellation = new();
    private int _mediaVisualRevision;
    private bool _mediaVisualStopped;

    private void InitializeMediaVisuals() => RefreshMediaVisuals();

    internal MediaVisualSet? GetMediaVisuals(string assetId) =>
        _assetVisuals.TryGetValue(assetId, out var cached) ? cached.Visuals : null;

    // Call after rebuilding timeline rows. Only sources used on the timeline need background overviews.
    private void RefreshMediaVisuals()
    {
        if (_mediaVisualStopped) return;
        var wanted = _project.Clips.Select(clip => clip.AssetId).ToHashSet(StringComparer.Ordinal);
        foreach (var obsolete in _assetVisuals.Keys.Where(id => !wanted.Contains(id)).ToArray()) _assetVisuals.Remove(obsolete);
        foreach (var asset in _project.MediaAssets.Where(asset => wanted.Contains(asset.Id)))
        {
            if (_assetVisualPending.Contains(asset.Id)) continue;
            string key;
            try { key = MediaVisualCache.GetKey(asset, _projectPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { continue; }
            if (_assetVisuals.TryGetValue(asset.Id, out var existing) && existing.Key == key) continue;
            _assetVisuals.Remove(asset.Id);
            _assetVisualPending.Add(asset.Id);
            _ = LoadAssetVisualAsync(asset, _projectPath, key, _mediaVisualRevision, _mediaVisualCancellation.Token);
        }
    }

    private async Task LoadAssetVisualAsync(MediaAsset asset, string? projectPath, string key, int revision, CancellationToken token)
    {
        var changed = false;
        try
        {
            // File decoding/process I/O is isolated from the UI. Frozen images are safe to return across threads.
            var result = await Task.Run(() => MediaVisualCache.GetAsync(asset, projectPath, token), token);
            if (token.IsCancellationRequested || revision != _mediaVisualRevision) return;
            var current = _project.MediaAssets.FirstOrDefault(item => item.Id == asset.Id);
            if (current is not null && MediaVisualCache.GetKey(current, _projectPath) == key)
            {
                _assetVisuals[asset.Id] = new(key, result);
                changed = true;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or
            InvalidOperationException or FormatException or System.ComponentModel.Win32Exception)
        {
            if (!token.IsCancellationRequested && revision == _mediaVisualRevision)
            {
                // An optional overview must never prevent editing or create a retry loop on every UI refresh.
                _assetVisuals[asset.Id] = new(key, new(null, null));
                changed = true;
            }
        }
        finally
        {
            if (revision == _mediaVisualRevision)
            {
                _assetVisualPending.Remove(asset.Id);
                if (changed && !_mediaVisualStopped) ApplyMediaVisualsToLanes();
                else if (!token.IsCancellationRequested && !_mediaVisualStopped) RefreshMediaVisuals();
            }
        }
    }

    private void ApplyMediaVisualsToLanes()
    {
        // Updating a lane's draw data leaves mouse capture, rows and in-progress inspector typing intact.
        var clips = _project.Clips.ToDictionary(clip => clip.Id);
        foreach (var lane in FindLanes(TrackItems))
        {
            if (lane.Clips is null) continue;
            var views = lane.Clips.Select(view =>
            {
                var visual = clips.TryGetValue(view.Id, out var clip) ? GetMediaVisuals(clip.AssetId) : null;
                return view with { Thumbnail = visual?.Thumbnail, Waveform = visual?.Waveform };
            }).ToArray();
            lane.SetCurrentValue(TimelineLane.ClipsProperty, views);
        }
    }

    private void ResetMediaVisuals()
    {
        _mediaVisualRevision++;
        _mediaVisualCancellation.Cancel();
        _mediaVisualCancellation.Dispose();
        _mediaVisualCancellation = new();
        _assetVisualPending.Clear();
        _assetVisuals.Clear();
    }

    private void ShutdownMediaVisuals()
    {
        _mediaVisualStopped = true;
        ResetMediaVisuals();
    }
}
