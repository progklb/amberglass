using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Amberglass.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Amberglass.Preview.Audio;

/// <summary>
/// Plays PCM through macOS AudioQueue.
///
/// AudioQueue rather than a bundled audio library: it is part of the OS, so there is no native
/// binary to ship, sign or keep up to date. The interop surface is small — create, allocate, enqueue,
/// start, stop, dispose — because all this has to do is accept samples.
///
/// The queue asks for data rather than being pushed to: a callback fires whenever a buffer has been
/// consumed, and refills it from whatever the decoder has produced. That callback runs on an audio
/// thread owned by CoreAudio, so it does no allocation, takes no locks that a slow caller could
/// hold, and never throws — an exception crossing back into native code would take the process down.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class CoreAudioOutput : IAudioOutput
{
    private const string AudioToolbox =
        "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";

    /// <summary>'lpcm' — linear PCM, as a big-endian four-character code.</summary>
    private const uint FormatLinearPcm = 0x6C70636D;

    private const uint FlagIsSignedInteger = 1 << 2;
    private const uint FlagIsPacked = 1 << 3;

    /// <summary>
    /// Three buffers of a tenth of a second. Enough that a scheduling hiccup in the decoder does
    /// not produce a gap, while keeping the delay between a seek and hearing it short.
    /// </summary>
    private const int BufferCount = 3;
    private const double BufferSeconds = 0.1;

    private readonly ILogger<CoreAudioOutput> _logger;

    /// <summary>
    /// Decoded audio waiting to be played. Bounded so that reading from it blocks the decoder once
    /// the device is a few buffers ahead — that back-pressure is what keeps audio at realtime speed
    /// instead of ffmpeg racing to the end of the file.
    /// </summary>
    private BlockingCollection<byte[]>? _pending;

    private IntPtr _queue;
    private readonly List<IntPtr> _buffers = [];

    // Held as a field because the queue keeps the pointer: a collected delegate would leave native
    // code calling into freed memory.
    private AudioQueueOutputCallback? _callback;

    private AudioFormat _format = AudioFormat.Default;
    private byte[] _remainder = [];
    private int _remainderOffset;
    private volatile bool _stopping;

    public CoreAudioOutput(ILogger<CoreAudioOutput> logger)
    {
        _logger = logger;
    }

    public bool IsAvailable => OperatingSystem.IsMacOS();

    public void Start(AudioFormat format)
    {
        Stop();

        _format = format;
        _stopping = false;
        _remainder = [];
        _remainderOffset = 0;
        _pending = new BlockingCollection<byte[]>(boundedCapacity: BufferCount + 2);

        var description = new AudioStreamBasicDescription
        {
            SampleRate = format.SampleRate,
            FormatID = FormatLinearPcm,
            FormatFlags = FlagIsSignedInteger | FlagIsPacked,
            BytesPerPacket = (uint)format.BytesPerFrame,
            FramesPerPacket = 1,
            BytesPerFrame = (uint)format.BytesPerFrame,
            ChannelsPerFrame = (uint)format.Channels,
            BitsPerChannel = (uint)format.BitsPerSample
        };

        _callback = OnBufferConsumed;

        // A null run loop means CoreAudio calls back on a thread of its own, which is what we want:
        // nothing here needs to touch the UI.
        var status = AudioQueueNewOutput(ref description, _callback, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out _queue);
        if (status != 0)
        {
            _logger.LogWarning("AudioQueueNewOutput failed with status {Status}; playback will be silent", status);
            _queue = IntPtr.Zero;
            return;
        }

        var bufferBytes = (int)(format.BytesPerSecond * BufferSeconds);

        for (var i = 0; i < BufferCount; i++)
        {
            if (AudioQueueAllocateBuffer(_queue, (uint)bufferBytes, out var buffer) != 0)
            {
                continue;
            }

            _buffers.Add(buffer);

            // Primed with silence so the queue has something to start on; the callback takes over
            // from there.
            FillBuffer(buffer, bufferBytes);
        }

        AudioQueueStart(_queue, IntPtr.Zero);
    }

    public void Write(ReadOnlySpan<byte> pcm, CancellationToken cancellationToken)
    {
        if (_pending is not { } pending || pcm.IsEmpty)
        {
            return;
        }

        try
        {
            pending.Add(pcm.ToArray(), cancellationToken);
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException)
        {
            // Cancelled, or stopped while a write was in flight.
        }
    }

    public void Stop()
    {
        _stopping = true;

        if (_pending is { } pending)
        {
            pending.CompleteAdding();
            _pending = null;
        }

        if (_queue != IntPtr.Zero)
        {
            AudioQueueStop(_queue, true);
            AudioQueueDispose(_queue, true);
            _queue = IntPtr.Zero;
        }

        _buffers.Clear();
        _callback = null;
    }

    public void Dispose() => Stop();

    /// <summary>
    /// Called by CoreAudio when a buffer has finished playing. Refills it and hands it back.
    /// Everything here is best-effort: falling behind produces silence, never an exception.
    /// </summary>
    private void OnBufferConsumed(IntPtr userData, IntPtr queue, IntPtr buffer)
    {
        try
        {
            if (_stopping || _queue == IntPtr.Zero)
            {
                return;
            }

            var capacity = (int)Marshal.ReadInt32(buffer);
            FillBuffer(buffer, capacity);
        }
        catch (Exception ex)
        {
            // An exception escaping into native code would terminate the process.
            _logger.LogDebug(ex, "Audio buffer refill failed");
        }
    }

    /// <summary>
    /// Fills one device buffer, drawing from the pending queue and padding with silence when the
    /// decoder has not kept up. Always enqueues, so the callback chain never stalls.
    /// </summary>
    private void FillBuffer(IntPtr buffer, int capacity)
    {
        var data = Marshal.ReadIntPtr(buffer, IntPtr.Size);
        var written = 0;

        while (written < capacity)
        {
            if (_remainderOffset >= _remainder.Length)
            {
                if (!TryTakeNext())
                {
                    break;
                }
            }

            var take = Math.Min(capacity - written, _remainder.Length - _remainderOffset);
            Marshal.Copy(_remainder, _remainderOffset, data + written, take);
            _remainderOffset += take;
            written += take;
        }

        // Silence for the rest: a partially filled buffer would otherwise replay whatever was in it.
        for (var i = written; i < capacity; i++)
        {
            Marshal.WriteByte(data, i, 0);
        }

        // mAudioDataByteSize sits after the capacity and the data pointer.
        Marshal.WriteInt32(buffer, IntPtr.Size + IntPtr.Size, capacity);

        AudioQueueEnqueueBuffer(_queue, buffer, 0, IntPtr.Zero);
    }

    private bool TryTakeNext()
    {
        if (_pending is not { } pending)
        {
            return false;
        }

        // Never blocks: the audio thread must not wait on the decoder.
        if (!pending.TryTake(out var next))
        {
            return false;
        }

        _remainder = next;
        _remainderOffset = 0;
        return true;
    }

    private delegate void AudioQueueOutputCallback(IntPtr userData, IntPtr queue, IntPtr buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioStreamBasicDescription
    {
        public double SampleRate;
        public uint FormatID;
        public uint FormatFlags;
        public uint BytesPerPacket;
        public uint FramesPerPacket;
        public uint BytesPerFrame;
        public uint ChannelsPerFrame;
        public uint BitsPerChannel;
        public uint Reserved;
    }

    [DllImport(AudioToolbox)]
    private static extern int AudioQueueNewOutput(
        ref AudioStreamBasicDescription format,
        AudioQueueOutputCallback callback,
        IntPtr userData,
        IntPtr callbackRunLoop,
        IntPtr callbackRunLoopMode,
        uint flags,
        out IntPtr audioQueue);

    [DllImport(AudioToolbox)]
    private static extern int AudioQueueAllocateBuffer(IntPtr audioQueue, uint bufferByteSize, out IntPtr buffer);

    [DllImport(AudioToolbox)]
    private static extern int AudioQueueEnqueueBuffer(IntPtr audioQueue, IntPtr buffer, uint packetCount, IntPtr packetDescriptions);

    [DllImport(AudioToolbox)]
    private static extern int AudioQueueStart(IntPtr audioQueue, IntPtr startTime);

    [DllImport(AudioToolbox)]
    private static extern int AudioQueueStop(IntPtr audioQueue, bool immediate);

    [DllImport(AudioToolbox)]
    private static extern int AudioQueueDispose(IntPtr audioQueue, bool immediate);
}
