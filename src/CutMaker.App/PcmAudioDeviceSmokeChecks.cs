using System.Diagnostics;
using System.IO;
using System.Text;

namespace CutMaker.App;

internal static class PcmAudioDeviceSmokeChecks
{
    internal static async Task RunAsync(string folder)
    {
        var report = new StringBuilder();
        var requests = new List<(long Start, int Frames)>();
        var length = PcmAudioDevice.SampleRate * 2L + 117;
        using (var player = new PcmAudioDevice(Fill, length, muted: true))
        {
            var startup = Stopwatch.StartNew();
            player.Start();
            startup.Stop();
            Require(startup.Elapsed < TimeSpan.FromSeconds(2), "The PCM device should start without a media-open wait.");
            lock (requests)
            {
                Require(requests.Count >= PcmAudioDevice.QueueBlocks, "The initial native queue was not primed.");
                Require(requests.Take(PcmAudioDevice.QueueBlocks).Sum(item => item.Frames) ==
                    PcmAudioDevice.BlockFrames * PcmAudioDevice.QueueBlocks, "PCM priming must stay bounded to 160 ms.");
            }
            await WaitUntilAsync(() => player.PositionFrames > PcmAudioDevice.SampleRate / 20,
                "The native output sample clock did not advance.");
            player.Pause();
            var pausePosition = player.PositionFrames;
            await Task.Delay(120);
            Require(!player.IsPlaying && player.PositionFrames == pausePosition, "Pause did not freeze the device sample clock.");
            player.Resume();
            await WaitUntilAsync(() => player.PositionFrames > pausePosition + 1200,
                "Resume did not continue the device sample clock.");

            var seekFrame = PcmAudioDevice.SampleRate + 123L;
            player.Seek(seekFrame);
            Require(player.IsPlaying && player.PositionFrames >= seekFrame, "Playing seek lost its transport state.");
            await WaitUntilAsync(() => player.PositionFrames > seekFrame + 1200, "Playing seek did not advance.");
            player.Pause();
            player.Seek(5432);
            await Task.Delay(80);
            Require(!player.IsPlaying && player.PositionFrames == 5432, "Paused seek started audio or changed the selected sample.");

            lock (requests) requests.Clear();
            player.Start();
            await WaitUntilAsync(() => player.Ended, "Native PCM did not reach EOF.", 5000);
            Require(!player.IsPlaying && player.PositionFrames == length && player.Error is null,
                "Native PCM ended with a wrong sample position or device error.");
            lock (requests)
            {
                long expected = 0;
                foreach (var request in requests)
                {
                    Require(request.Start == expected, "PCM producer skipped or repeated samples at a native buffer boundary.");
                    Require(request.Frames is > 0 and <= PcmAudioDevice.BlockFrames, "PCM block exceeded its bound.");
                    expected += request.Frames;
                }
                Require(expected == length, "PCM producer padded or omitted the partial final block.");
            }
            player.Start(length - 431);
            await WaitUntilAsync(() => player.Ended, "EOF replay of a partial block stalled.");
            Require(player.PositionFrames == length, "EOF replay changed the exact end sample.");
            report.AppendLine($"PASS Native PCM: clock, pause, resume, playing/paused seek, contiguous blocks, partial EOF, replay; startup {startup.Elapsed.TotalMilliseconds:0} ms.");
        }

        // Exercise reset/unprepare/close while all native headers are still queued.
        for (var i = 0; i < 4; i++)
        {
            using var player = new PcmAudioDevice(Fill, length, muted: true);
            player.Start(10 + i);
            player.Seek(137);
            player.Pause();
            player.Resume();
        }
        report.AppendLine("PASS Native PCM: repeated disposal with queued buffers, reopen and reset.");

        using (var player = new PcmAudioDevice((_, _, _) => { }, 0, muted: true))
        {
            player.Start();
            Require(player.Ended && !player.IsPlaying && player.PositionFrames == 0, "Empty audio transport must finish immediately.");
        }
        report.AppendLine("PASS Native PCM: zero-length audio.");

        var produced = 0;
        using (var player = new PcmAudioDevice((_, samples, _) =>
        {
            if (Interlocked.Increment(ref produced) > PcmAudioDevice.QueueBlocks)
                throw new IOException("Expected producer failure for regression check.");
            Array.Clear(samples);
        }, PcmAudioDevice.SampleRate, muted: true))
        {
            player.Start();
            await WaitUntilAsync(() => player.Error is not null, "Background PCM errors were not reported.");
            Require(!player.IsPlaying && !player.Ended && player.Error is IOException,
                "Background PCM failure must stop safely without reporting a completed timeline.");
        }
        report.AppendLine("PASS Native PCM: background source failure reported without an unhandled thread exception.");
        var ready = 0;
        var progressiveRequests = new List<(long Start, int Count)>();
        var progressiveLength = PcmAudioDevice.BlockFrames * 8L + 13;
        using (var player = new PcmAudioDevice((start, samples, count) =>
        {
            if (start >= PcmAudioDevice.BlockFrames * 2L && Volatile.Read(ref ready) == 0) throw new AudioBufferPendingException();
            lock (progressiveRequests) progressiveRequests.Add((start, count));
            Array.Clear(samples);
        }, progressiveLength, muted: true))
        {
            player.Start();
            await WaitUntilAsync(() => player.IsBuffering, "Progressive producer did not expose its pending-data state.");
            var at = player.PositionFrames;
            await Task.Delay(80);
            Require(player.Error is null && player.IsPlaying && at == player.PositionFrames,
                "Waiting for source data must preserve playback intent and stop the sample clock.");
            Volatile.Write(ref ready, 1);
            await WaitUntilAsync(() => player.Ended, "Progressive producer did not resume after data became ready.");
            long next = 0;
            lock (progressiveRequests) foreach (var item in progressiveRequests)
            { Require(item.Start == next, "Progressive buffering skipped or duplicated PCM frames."); next += item.Count; }
            Require(next == progressiveLength && player.PositionFrames == progressiveLength && player.Error is null,
                "Progressive playback did not preserve exact EOF.");
        }
        report.AppendLine("PASS Native PCM: pending progressive data freezes clock, then resumes the exact next sample without padding or omissions.");
        using (var player = new PcmAudioDevice(Fill, length, muted: true))
        {
            var outPoint = PcmAudioDevice.SampleRate / 4 + 13;
            player.SetPlaybackEnd(outPoint); player.Start();
            await WaitUntilAsync(() => player.Ended, "Audition output limit did not stop playback.");
            Require(player.PositionFrames == outPoint, "Audition must stop at the exact requested sample.");
            player.SetPlaybackEnd(null); player.Start(length - 431);
            await WaitUntilAsync(() => player.Ended, "Clearing audition range did not restore timeline EOF.");
            Require(player.PositionFrames == length, "Audition changed the full timeline duration.");
        }
        report.AppendLine("PASS Native PCM: audition range stops at the exact sample and clearing it restores full EOF.");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "pcm-device-checks.txt"), report.ToString());
        return;

        void Fill(long start, short[] samples, int frames)
        {
            lock (requests) requests.Add((start, frames));
            for (var i = 0; i < frames; i++)
                samples[i * 2] = samples[i * 2 + 1] = (short)((start + i) % 1024);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string message, int timeoutMilliseconds = 3000)
    {
        var timer = Stopwatch.StartNew();
        while (!condition() && timer.ElapsedMilliseconds < timeoutMilliseconds) await Task.Delay(15);
        Require(condition(), message);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
