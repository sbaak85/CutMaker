using System.Globalization;
using CutMaker.Core;

namespace CutMaker.App;

public static partial class MediaRenderService
{
    private static string Exact(double value) => value.ToString("G17", CultureInfo.InvariantCulture);

    private static void AddAudioGraph(List<string> arguments, List<string> graph, IReadOnlyList<Clip> clips,
        IReadOnlyDictionary<string, string> paths, IReadOnlyDictionary<string, Track> tracks,
        IReadOnlyDictionary<string, (bool Video, bool Audio)> streams, IReadOnlyDictionary<string, MediaAsset> assets,
        IReadOnlyDictionary<string, AudioPreviewCache.Lease> cachedAudio,
        double rangeStart, double rangeEnd, ref int inputCount)
    {
        var sampleCount = AudioSampleClock.At(rangeEnd) - AudioSampleClock.At(rangeStart);
        var labels = new List<string> { "[silence]" };
        graph.Add($"anullsrc=r={AudioSampleClock.Rate}:cl=stereo,atrim=end_sample={sampleCount},asetpts=N/SR/TB[silence]");
        var audible = clips.Where(clip => !clip.SourceAudioMuted && streams[clip.AssetId].Audio && assets[clip.AssetId].Kind != MediaKind.Image)
            .Select(clip => (Clip: clip, Slice: AudioSampleClock.Slice(clip, rangeStart, rangeEnd)))
            .Where(item => item.Slice.Length > 0);
        var sourceIndex = 0;
        foreach (var source in audible.GroupBy(item => item.Clip.AssetId))
        {
            var fragments = source.ToArray();
            var input = inputCount++;
            // Decode and resample the source only once. Independent -ss/-t for each fragment
            // restarts MP3 decoder state and the resampling filter at every cut. Decoding from
            // source zero preserves one phase/sample sequence, also for non-integer 44.1→48k.
            // Stop after the furthest needed source sample plus decoder/resampler tail context.
            var decodeEnd = fragments.Max(item => item.Slice.SourceStart + item.Slice.Length) / (double)AudioSampleClock.Rate + 1;
            var sourceOffset = 0L;
            if (cachedAudio.TryGetValue(source.Key, out var cache))
            {
                // PCM can seek by exact byte offset: late trims do not scan the song again.
                sourceOffset = fragments.Min(item => item.Slice.SourceStart);
                arguments.AddRange(["-threads", "2", "-f", "f64le", "-ar", AudioSampleClock.Rate.ToString(CultureInfo.InvariantCulture),
                    "-ac", "2", "-skip_initial_bytes", (sourceOffset * AudioPreviewCache.BytesPerFrame).ToString(CultureInfo.InvariantCulture),
                    "-t", Exact(decodeEnd - sourceOffset / (double)AudioSampleClock.Rate), "-i", cache.Path]);
            }
            else
                arguments.AddRange(["-threads", "2", "-t", Exact(decodeEnd), "-i", paths[source.Key]]);
            var splitLabels = string.Concat(fragments.Select((_, index) => $"[audioSource{sourceIndex}_{index}]"));
            graph.Add($"[{input}:a:0]aresample={AudioSampleClock.Rate},aformat=sample_fmts=dblp:channel_layouts=stereo," +
                $"asetpts=N/SR/TB,asplit={fragments.Length}{splitLabels}");
            for (var index = 0; index < fragments.Length; index++)
            {
                var (clip, slice) = fragments[index];
                var label = $"[audio{sourceIndex}_{index}]";
                var localTime = $"((n+{slice.TimelineStart})/{AudioSampleClock.Rate}-{Exact(clip.Start)})";
                var gain = clip.Gain * tracks[clip.TrackId].Volume;
                var expression = $"{Exact(gain)}*({FadeEnvelope.GainExpression(clip, localTime, audio: true)})";
                graph.Add($"[audioSource{sourceIndex}_{index}]atrim=start_sample={slice.SourceStart - sourceOffset}:end_sample={slice.SourceStart - sourceOffset + slice.Length}," +
                    $"asetpts=N/SR/TB,aeval=exprs='val(0)*({expression})|val(1)*({expression})':c=stereo," +
                    $"adelay={slice.OutputStart}S:all=1{label}");
                labels.Add(label);
            }
            sourceIndex++;
        }
        graph.Add($"{string.Concat(labels)}amix=inputs={labels.Count}:duration=longest:dropout_transition=0:normalize=0," +
            $"alimiter=limit=0.95:level=false:latency=true,apad,atrim=end_sample={sampleCount}[aout]");
    }
}
