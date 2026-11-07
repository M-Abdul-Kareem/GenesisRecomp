using System.Runtime.InteropServices;

namespace GenesisRecomp.Machine;

/// <summary>
/// Plays 16-bit stereo audio through the Windows multimedia API (winmm.dll) - no extra packages needed.
/// Audio is queued as a few small buffers. <see cref="Submit"/> waits while too many are queued, which is what
/// keeps the emulation running at exactly the speed of the sound card.
/// </summary>
public sealed class AudioOut : IDisposable
{
    private const int BufferCount = 8;
    private const int MaxFramesPerBuffer = 4096;
    private const uint WaveMapper = 0xFFFFFFFF;
    private const uint WhdrDone = 0x1, WhdrPrepared = 0x2, WhdrInQueue = 0x10;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct WaveFormatEx
    {
        public ushort FormatTag, Channels;
        public uint SamplesPerSec, AvgBytesPerSec;
        public ushort BlockAlign, BitsPerSample, Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHdr
    {
        public IntPtr Data;
        public uint BufferLength, BytesRecorded;
        public IntPtr User;
        public uint Flags, Loops;
        public IntPtr Next, Reserved;
    }

    [DllImport("winmm.dll")] private static extern uint waveOutOpen(out IntPtr hwo, uint deviceId, ref WaveFormatEx format, IntPtr callback, IntPtr instance, uint flags);
    [DllImport("winmm.dll")] private static extern uint waveOutClose(IntPtr hwo);
    [DllImport("winmm.dll")] private static extern uint waveOutReset(IntPtr hwo);
    [DllImport("winmm.dll")] private static extern uint waveOutPrepareHeader(IntPtr hwo, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutUnprepareHeader(IntPtr hwo, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutWrite(IntPtr hwo, IntPtr header, uint size);

    private static readonly int HeaderSize = Marshal.SizeOf<WaveHdr>();

    private IntPtr device;
    private readonly IntPtr[] headers = new IntPtr[BufferCount];
    private readonly IntPtr[] data = new IntPtr[BufferCount];
    private int next;

    public int SampleRate { get; }

    /// <summary>How many buffers may be waiting to play before <see cref="Submit"/> blocks (latency = this x one frame).</summary>
    public int MaxQueued { get; set; } = 3;

    /// <summary>Creates the output, or returns null if there is no usable sound device.</summary>
    public static AudioOut? TryCreate(int sampleRate)
    {
        try
        {
            var a = new AudioOut(sampleRate);
            return a.device == IntPtr.Zero ? null : a;
        }
        catch (Exception) { return null; }       // DllNotFound on non-Windows, no device, etc.
    }

    private AudioOut(int sampleRate)
    {
        SampleRate = sampleRate;
        var fmt = new WaveFormatEx
        {
            FormatTag = 1, Channels = 2, SamplesPerSec = (uint)sampleRate, BitsPerSample = 16,
            BlockAlign = 4, AvgBytesPerSec = (uint)sampleRate * 4, Size = 0
        };
        if (waveOutOpen(out device, WaveMapper, ref fmt, IntPtr.Zero, IntPtr.Zero, 0) != 0) { device = IntPtr.Zero; return; }

        for (int i = 0; i < BufferCount; i++)
        {
            data[i] = Marshal.AllocHGlobal(MaxFramesPerBuffer * 4);
            headers[i] = Marshal.AllocHGlobal(HeaderSize);
            var h = new WaveHdr { Data = data[i], BufferLength = (uint)(MaxFramesPerBuffer * 4) };
            Marshal.StructureToPtr(h, headers[i], false);
            if (waveOutPrepareHeader(device, headers[i], (uint)HeaderSize) != 0) { Dispose(); device = IntPtr.Zero; return; }
        }
    }

    private uint FlagsOf(int i) => (uint)Marshal.ReadInt32(headers[i], (int)Marshal.OffsetOf<WaveHdr>(nameof(WaveHdr.Flags)));

    private bool IsPlayingOrQueued(int i)
    {
        uint f = FlagsOf(i);
        return (f & WhdrInQueue) != 0 && (f & WhdrDone) == 0;
    }

    private int Queued()
    {
        int n = 0;
        for (int i = 0; i < BufferCount; i++) if (IsPlayingOrQueued(i)) n++;
        return n;
    }

    /// <summary>
    /// Queues interleaved stereo samples for playback. Blocks (for at most about a quarter of a second)
    /// while the queue is full, so the caller runs at the sound card's pace. Returns false if audio was dropped.
    /// </summary>
    public bool Submit(short[] samples, int frames)
    {
        if (device == IntPtr.Zero || frames <= 0) return false;
        if (frames > MaxFramesPerBuffer) frames = MaxFramesPerBuffer;

        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (Queued() >= MaxQueued)
        {
            if (timer.ElapsedMilliseconds > 250) return false;       // the device has stalled: give up on this chunk
            Thread.Sleep(1);
        }

        int slot = -1;
        for (int k = 0; k < BufferCount; k++)
        {
            int i = (next + k) % BufferCount;
            if (!IsPlayingOrQueued(i)) { slot = i; break; }
        }
        if (slot < 0) return false;
        next = (slot + 1) % BufferCount;

        Marshal.Copy(samples, 0, data[slot], frames * 2);
        Marshal.WriteInt32(headers[slot], (int)Marshal.OffsetOf<WaveHdr>(nameof(WaveHdr.BufferLength)), frames * 4);
        return waveOutWrite(device, headers[slot], (uint)HeaderSize) == 0;
    }

    /// <summary>True while some audio is still waiting to play (used to decide whether the sound card is keeping time).</summary>
    public bool HasQueuedAudio => device != IntPtr.Zero && Queued() > 0;

    /// <summary>Stops playback and discards anything queued.</summary>
    public void Flush()
    {
        if (device != IntPtr.Zero) waveOutReset(device);
    }

    public void Dispose()
    {
        if (device != IntPtr.Zero)
        {
            waveOutReset(device);
            for (int i = 0; i < BufferCount; i++)
                if (headers[i] != IntPtr.Zero) waveOutUnprepareHeader(device, headers[i], (uint)HeaderSize);
            waveOutClose(device);
            device = IntPtr.Zero;
        }
        for (int i = 0; i < BufferCount; i++)
        {
            if (headers[i] != IntPtr.Zero) { Marshal.FreeHGlobal(headers[i]); headers[i] = IntPtr.Zero; }
            if (data[i] != IntPtr.Zero) { Marshal.FreeHGlobal(data[i]); data[i] = IntPtr.Zero; }
        }
    }
}
