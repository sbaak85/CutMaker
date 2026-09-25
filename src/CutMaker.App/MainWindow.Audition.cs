using System.IO;
using System.Windows;
using CutMaker.Core;

namespace CutMaker.App;

public partial class MainWindow
{
    private double? _auditionEnd;
    private double _auditionStart;
    private bool _auditionLoop;
    private long _auditionRequest;
    private async void Audition_Click(object sender, RoutedEventArgs e) => await AuditionJunctionAsync();
    private void AuditionLoop_Click(object sender, RoutedEventArgs e) => _auditionLoop = AuditionLoopMenuItem.IsChecked;
    internal async Task AuditionJunctionAsync()
    {
        if (PreviewDuration <= 0) { StatusText.Text = "請先將素材放入軌道。"; return; }
        var center = PlayheadSeconds;
        var bounds = EffectivePlaybackRange;
        if (bounds is not null) center = Math.Clamp(center, bounds.Start, bounds.End);
        var start = Math.Max(bounds?.Start ?? 0, center - 2);
        var end = Math.Min(bounds?.End ?? PreviewDuration, center + 2);
        if (end <= start) return;
        var key = PreviewContentKey(CreatePreviewSnapshot());
        PausePreview(); ClearAudition(); SeekPreview(start);
        var request = _auditionRequest;
        if (!PreviewIsReady) await PreparePreviewAsync();
        if (!PreviewIsReady || request != _auditionRequest || key != PreviewContentKey(CreatePreviewSnapshot())) return;
        _auditionStart = start; _auditionEnd = end;
        try { _audioDevice?.SetPlaybackEnd(AudioSampleClock.At(end)); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException) { ClearAudition(false); ReportAudioPreviewFailure(ex); return; }
        SeekPreview(start, preserveAudition: true); StartPreviewPlayback();
        StatusText.Text = $"{(_auditionLoop ? "循環" : "")}試聽接點：{start:0.###}–{end:0.###} 秒 · 空白鍵暫停；停止或另行定位結束試聽";
    }
    private bool CheckAuditionEnd(double position)
    {
        if (_auditionEnd is not { } end || (position < end - .000001 && _audioDevice?.Ended != true)) return false;
        PausePreview();
        if (_auditionLoop)
        { SeekPreview(_auditionStart, preserveAudition: true); StartPreviewPlayback(); }
        else
        { ClearAudition(); SeekPreview(end); StatusText.Text = "接點試聽完成"; }
        return true;
    }
    private void ClearAudition(bool resetDevice = true)
    {
        if (_auditionEnd is null) return;
        _auditionEnd = null;
        if (resetDevice && _audioDevice is { } audio)
            try { audio.SetPlaybackEnd(null); }
            catch (Exception ex) when (ex is InvalidOperationException or IOException) { PreviewDiagnostics.Write("audition-clear-failed", ex.Message); }
    }
}
