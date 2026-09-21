using System.IO;
using System.Text.Json;

namespace CutMaker.App;

public partial class MainWindow
{
    private sealed record ProjectView(LayoutSettings Layout, double Zoom, double Offset, double Vertical, double Playhead, string[] Collapsed);
    private string? ProjectViewPath => _projectPath is null ? null : Path.Combine(EditorPreferences.Root, "project-views",
        ManagedMediaCache.Hash(Path.GetFullPath(_projectPath).ToUpperInvariant()) + ".json");
    private void SaveProjectView()
    {
        if (ProjectViewPath is not { } path || !File.Exists(_projectPath)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var value = new ProjectView(CaptureLayout(), TimelinePixelsPerSecond, TimelineOffsetSeconds, TimelineTrackScroll.VerticalOffset, PlayheadSeconds, _collapsedTracks.ToArray());
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllText(temporary, JsonSerializer.Serialize(value)); File.Move(temporary, path, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { PreviewDiagnostics.Write("save-view-failed", ex.Message); }
    }
    private void RestoreProjectView()
    {
        if (ProjectViewPath is not { } path || !File.Exists(path)) return;
        try
        {
            var value = JsonSerializer.Deserialize<ProjectView>(File.ReadAllText(path));
            if (value is null || value.Layout is null) return;
            ApplyLayout(value.Layout);
            _collapsedTracks.Clear(); _collapsedTracks.UnionWith((value.Collapsed ?? []).Where(id => _project.Tracks.Any(track => track.Id == id)));
            RefreshTimeline(); UpdateLayout();
            TimelineZoom.Value = LayoutSettings.Bounded(value.Zoom, TimelineZoom.Minimum, TimelineZoom.Maximum, 40);
            TimelineOffsetSeconds = LayoutSettings.Bounded(value.Offset, 0, TimelineHorizontalScroll.Maximum, 0);
            TimelineTrackScroll.ScrollToVerticalOffset(LayoutSettings.Bounded(value.Vertical, 0, TimelineTrackScroll.ScrollableHeight, 0));
            SeekPreview(LayoutSettings.Bounded(value.Playhead, 0, PreviewDuration, 0));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { PreviewDiagnostics.Write("load-view-failed", ex.Message); }
    }
}
