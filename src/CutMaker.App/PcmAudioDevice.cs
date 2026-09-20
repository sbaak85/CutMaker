using System.Runtime.InteropServices;
using System.Text;

namespace CutMaker.App;

/// <summary>
/// A small native PCM output queue. The audio device's consumed sample count is the
/// transport clock; neither UI timers nor the number of rendered blocks advance it.
/// </summary>
internal sealed class PcmAudioDevice : IDisposable
{
    internal delegate void FillPcm16(long startFrame, short[] interleaved, int frameCount);

    internal const int SampleRate = 48_000;
    internal const int Channels = 2;
    internal const int BlockFrames = SampleRate / 25;
    internal const int QueueBlocks = 4;
    private const int BytesPerFrame = Channels * sizeof(short);
    private const uint WaveMapper = uint.MaxValue;
    private const uint CallbackEvent = 0x0005_0000;
    private const uint HeaderDone = 1;
    private const uint TimeMilliseconds = 1;
    private const uint TimeSamples = 2;
    private const uint TimeBytes = 4;
    private static readonly uint HeaderSize = (uint)Marshal.SizeOf<WaveHeader>();
    private static readonly int HeaderFlagsOffset = (int)Marshal.OffsetOf<WaveHeader>(nameof(WaveHeader.Flags));

    private readonly object _gate = new();
    private readonly FillPcm16 _fill;
    private readonly long _lengthFrames;
    private readonly bool _muted;
    private readonly AutoResetEvent _wake = new(false);
    private readonly List<AudioBlock> _blocks = [];
    private readonly Thread _worker;
    private IntPtr _device;
    private long _baseFrame;
    private long _nextFrame;
    private long _position;
    private uint _lastClockValue;
    private uint _clockType;
    private ulong _clockWrap;
    private bool _playing;
    private bool _ended;
    private bool _disposed;
    private Exception? _error;

    internal PcmAudioDevice(FillPcm16 fill, long lengthFrames, bool muted = false)
    {
        ArgumentNullException.ThrowIfNull(fill);
        ArgumentOutOfRangeException.ThrowIfNegative(lengthFrames);
        _fill = fill;
        _lengthFrames = lengthFrames;
        _muted = muted;
        _ended = lengthFrames == 0;
        _worker = new Thread(Produce)
        {
            IsBackground = true,
            Name = "CutMaker PCM playback",
            Priority = ThreadPriority.AboveNormal
        };

        try
        {
            var format = new WaveFormat
            {
                FormatTag = 1,
                Channels = Channels,
                SamplesPerSecond = SampleRate,
                AverageBytesPerSecond = SampleRate * BytesPerFrame,
                BlockAlign = BytesPerFrame,
                BitsPerSample = 16
            };
            Check(WaveOutOpen(out _device, WaveMapper, ref format,
                _wake.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, CallbackEvent), "開啟音訊輸出裝置");
            for (var i = 0; i < QueueBlocks; i++)
            {
                var block = new AudioBlock();
                _blocks.Add(block);
                Check(WaveOutPrepareHeader(_device, block.Header, HeaderSize), "準備音訊緩衝");
                block.Prepared = true;
            }
            _worker.Start();
        }
        catch
        {
            ReleaseNativeResources();
            if (_device == IntPtr.Zero) _wake.Dispose();
            throw;
        }
    }

    internal long PositionFrames
    {
        get { lock (_gate) return ReadPosition(); }
    }

    internal bool IsPlaying
    {
        get { lock (_gate) return _playing; }
    }

    internal bool Ended
    {
        get { lock (_gate) return _ended; }
    }

    internal Exception? Error
    {
        get { lock (_gate) return _error; }
    }

    internal void Start(long startFrame = 0)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            ResetAt(startFrame);
            BeginPlayback();
        }
        _wake.Set();
    }

    internal void Pause()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_playing) return;
            Check(WaveOutPause(_device), "暫停音訊播放");
            ReadPosition();
            _playing = false;
        }
    }

    internal void Resume()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_playing || _ended) return;
            if (_error is not null) throw new InvalidOperationException("音訊播放失敗。", _error);
            BeginPlayback();
        }
        _wake.Set();
    }

    /// <summary>Flushes old queued samples and keeps the current play/pause state.</summary>
    internal void Seek(long frame)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var resume = _playing;
            ResetAt(frame);
            if (resume) BeginPlayback();
        }
        _wake.Set();
    }

    private void ResetAt(long frame)
    {
        Check(WaveOutReset(_device), "重新定位音訊");
        Check(WaveOutPause(_device), "準備音訊播放位置");
        foreach (var block in _blocks) block.Queued = false;
        _baseFrame = _nextFrame = _position = Math.Clamp(frame, 0, _lengthFrames);
        _clockWrap = 0;
        _clockType = 0;
        _lastClockValue = 0;
        _playing = false;
        _ended = _baseFrame == _lengthFrames;
        _error = null;
    }

    private void BeginPlayback()
    {
        if (_ended) return;
        // Pause before priming all four blocks, so the first block cannot run dry
        // while the remaining blocks are still being filled.
        Check(WaveOutPause(_device), "準備音訊播放");
        FillQueue();
        Check(WaveOutRestart(_device), "開始音訊播放");
        _playing = true;
    }

    private void Produce()
    {
        while (true)
        {
            int waitMilliseconds;
            lock (_gate)
            {
                if (_disposed) return;
                if (_playing)
                {
                    try
                    {
                        FillQueue();
                        ReadPosition();
                        if (_nextFrame == _lengthFrames && _blocks.All(block => !block.Queued))
                        {
                            _position = _lengthFrames;
                            _ended = true;
                            _playing = false;
                        }
                    }
                    catch (Exception ex)
                    {
                        _error = ex;
                        _playing = false;
                        WaveOutPause(_device);
                    }
                }
                waitMilliseconds = _playing ? 20 : Timeout.Infinite;
            }
            // Native completion events normally wake us once per block. A short
            // watchdog also handles drivers which coalesce those notifications.
            _wake.WaitOne(waitMilliseconds);
        }
    }

    private void FillQueue()
    {
        foreach (var block in _blocks)
        {
            if (block.Queued)
            {
                if ((unchecked((uint)Marshal.ReadInt32(block.Header, HeaderFlagsOffset)) & HeaderDone) == 0)
                    continue;
                block.Queued = false;
            }
            if (_nextFrame >= _lengthFrames) continue;
            var frames = (int)Math.Min(BlockFrames, _lengthFrames - _nextFrame);
            _fill(_nextFrame, block.Samples, frames);
            if (_muted) Array.Clear(block.Samples, 0, frames * Channels);
            Marshal.Copy(block.Samples, 0, block.Data, frames * Channels);
            // A prepared header may use a shorter final buffer. Never pad EOF
            // with silence or reset the device at a timeline clip boundary.
            Marshal.WriteInt32(block.Header, IntPtr.Size, frames * BytesPerFrame);
            Check(WaveOutWrite(_device, block.Header, HeaderSize), "送出音訊緩衝");
            block.Queued = true;
            _nextFrame += frames;
        }
    }

    private long ReadPosition()
    {
        if (_disposed || _ended || _device == IntPtr.Zero) return _position;
        var time = new MultimediaTime { Type = TimeSamples };
        var result = WaveOutGetPosition(_device, ref time, (uint)Marshal.SizeOf<MultimediaTime>());
        if (result != 0)
        {
            _error ??= NativeError(result, "讀取音訊播放位置");
            _playing = false;
            return _position;
        }
        // The driver may return bytes or milliseconds instead of samples. Keep
        // its 32-bit counter monotonic across wraps (about 6 hours for bytes).
        if (_clockType != time.Type)
        {
            _clockType = time.Type;
            _clockWrap = 0;
        }
        else if (time.Value < _lastClockValue && _lastClockValue - time.Value > int.MaxValue)
            _clockWrap += 1UL << 32;
        _lastClockValue = time.Value;
        var units = _clockWrap + time.Value;
        var elapsed = time.Type switch
        {
            TimeSamples => (long)units,
            TimeBytes => (long)(units / BytesPerFrame),
            TimeMilliseconds => (long)(units * SampleRate / 1000),
            _ => -1
        };
        if (elapsed < 0)
        {
            _error ??= new InvalidOperationException("音訊裝置未提供可用的播放位置。");
            _playing = false;
            return _position;
        }
        _position = Math.Clamp(Math.Max(_position, _baseFrame + elapsed), _baseFrame, _lengthFrames);
        return _position;
    }

    public void Dispose()
    {
        var stopWorker = false;
        lock (_gate)
        {
            if (_disposed && _device == IntPtr.Zero) return;
            if (!_disposed)
            {
                ReadPosition();
                _disposed = true;
                _playing = false;
                stopWorker = true;
            }
        }
        if (stopWorker)
        {
            _wake.Set();
            _worker.Join();
        }
        lock (_gate)
        {
            ReleaseNativeResources();
            if (_device == IntPtr.Zero) _wake.Dispose();
        }
    }

    private void ReleaseNativeResources()
    {
        // waveOutReset returns every pending header before unprepare/free. The
        // worker has stopped, so no producer can submit another native buffer.
        if (_device != IntPtr.Zero) NoteFailure(WaveOutReset(_device), "停止音訊輸出");
        foreach (var block in _blocks.ToArray())
        {
            if (block.Prepared)
            {
                var result = WaveOutUnprepareHeader(_device, block.Header, HeaderSize);
                if (result == 0) block.Prepared = false;
                else NoteFailure(result, "釋放音訊緩衝");
            }
            if (block.Prepared) continue;
            block.Dispose();
            _blocks.Remove(block);
        }
        if (_device == IntPtr.Zero) return;
        var closeResult = WaveOutClose(_device);
        if (closeResult != 0)
        {
            NoteFailure(closeResult, "關閉音訊輸出裝置");
            // Keep the event and any still-owned native memory valid if a broken
            // driver rejects close. A later Dispose can retry the teardown.
            return;
        }
        _device = IntPtr.Zero;
        // A successful device close also guarantees no native playback or
        // callback can access a header whose unprepare failed during teardown.
        foreach (var block in _blocks) block.Dispose();
        _blocks.Clear();
    }

    private void NoteFailure(uint result, string operation)
    {
        if (result != 0) _error ??= NativeError(result, operation);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static void Check(uint result, string operation)
    {
        if (result != 0) throw NativeError(result, operation);
    }

    private static InvalidOperationException NativeError(uint result, string operation)
    {
        var message = new StringBuilder(256);
        WaveOutGetErrorText(result, message, message.Capacity);
        return new InvalidOperationException($"{operation}失敗（{result}）：{message}");
    }

    private sealed class AudioBlock : IDisposable
    {
        internal readonly short[] Samples = new short[BlockFrames * Channels];
        internal readonly IntPtr Data;
        internal readonly IntPtr Header;
        internal bool Prepared;
        internal bool Queued;

        internal AudioBlock()
        {
            Data = Marshal.AllocHGlobal(BlockFrames * BytesPerFrame);
            try
            {
                Header = Marshal.AllocHGlobal((int)HeaderSize);
                Marshal.StructureToPtr(new WaveHeader
                {
                    Data = Data,
                    BufferLength = BlockFrames * BytesPerFrame
                }, Header, false);
            }
            catch
            {
                if (Header != IntPtr.Zero) Marshal.FreeHGlobal(Header);
                Marshal.FreeHGlobal(Data);
                throw;
            }
        }

        public void Dispose()
        {
            Marshal.FreeHGlobal(Header);
            Marshal.FreeHGlobal(Data);
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormat
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSecond;
        public uint AverageBytesPerSecond;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort ExtraSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public IntPtr Data;
        public uint BufferLength;
        public uint BytesRecorded;
        public UIntPtr User;
        public uint Flags;
        public uint Loops;
        public IntPtr Next;
        public UIntPtr Reserved;
    }

    [StructLayout(LayoutKind.Explicit, Size = 12)]
    private struct MultimediaTime
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(4)] public uint Value;
    }

    [DllImport("winmm.dll", EntryPoint = "waveOutOpen", ExactSpelling = true)]
    private static extern uint WaveOutOpen(out IntPtr device, uint deviceId, ref WaveFormat format,
        IntPtr callback, IntPtr instance, uint flags);

    [DllImport("winmm.dll", EntryPoint = "waveOutPrepareHeader", ExactSpelling = true)]
    private static extern uint WaveOutPrepareHeader(IntPtr device, IntPtr header, uint size);

    [DllImport("winmm.dll", EntryPoint = "waveOutUnprepareHeader", ExactSpelling = true)]
    private static extern uint WaveOutUnprepareHeader(IntPtr device, IntPtr header, uint size);

    [DllImport("winmm.dll", EntryPoint = "waveOutWrite", ExactSpelling = true)]
    private static extern uint WaveOutWrite(IntPtr device, IntPtr header, uint size);

    [DllImport("winmm.dll", EntryPoint = "waveOutPause", ExactSpelling = true)]
    private static extern uint WaveOutPause(IntPtr device);

    [DllImport("winmm.dll", EntryPoint = "waveOutRestart", ExactSpelling = true)]
    private static extern uint WaveOutRestart(IntPtr device);

    [DllImport("winmm.dll", EntryPoint = "waveOutReset", ExactSpelling = true)]
    private static extern uint WaveOutReset(IntPtr device);

    [DllImport("winmm.dll", EntryPoint = "waveOutClose", ExactSpelling = true)]
    private static extern uint WaveOutClose(IntPtr device);

    [DllImport("winmm.dll", EntryPoint = "waveOutGetPosition", ExactSpelling = true)]
    private static extern uint WaveOutGetPosition(IntPtr device, ref MultimediaTime time, uint size);

    [DllImport("winmm.dll", EntryPoint = "waveOutGetErrorTextW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint WaveOutGetErrorText(uint error, StringBuilder message, int length);
}
