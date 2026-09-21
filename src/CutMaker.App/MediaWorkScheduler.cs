using System.Diagnostics;

namespace CutMaker.App;

/// <summary>One optional background decoder; user-requested work starts ahead of pending overviews.</summary>
internal static class MediaWorkScheduler
{
    private static readonly SemaphoreSlim Background = new(1, 1);
    private static int _foreground;
    private static long _interaction;
    internal static void NotifyInteraction() => Interlocked.Exchange(ref _interaction, Stopwatch.GetTimestamp());
    internal static IDisposable Foreground()
    {
        Interlocked.Increment(ref _foreground);
        return new Scope(() => Interlocked.Decrement(ref _foreground));
    }
    internal static async Task<IDisposable> BackgroundAsync(CancellationToken token)
    {
        await Background.WaitAsync(token).ConfigureAwait(false);
        try
        {
            while (Volatile.Read(ref _foreground) > 0 || Stopwatch.GetElapsedTime(Interlocked.Read(ref _interaction)).TotalMilliseconds < 180)
                await Task.Delay(30, token).ConfigureAwait(false);
            return new Scope(() => Background.Release());
        }
        catch { Background.Release(); throw; }
    }
    internal static void LowerPriority(Process process)
    {
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
    }
    private sealed class Scope(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
