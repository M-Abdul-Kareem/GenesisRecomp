namespace GenesisRecomp.Machine;

/// <summary>
/// The Mega Drive's sound hardware as one unit: YM2612 (FM + DAC) and SN76489 (PSG).
/// Time is kept in master-clock cycles (53.693175 MHz; the 68000 runs at master/7, the Z80 at master/15).
/// Whenever the game touches a sound chip, <see cref="SyncTo"/> first generates all samples up to that moment,
/// so register writes (DAC sample streaming, timers, note changes) land at the right time.
/// </summary>
public sealed class SoundSystem
{
    /// <summary>The YM2612 makes one sample every 144 of its clocks = 1008 master cycles.</summary>
    public const int MasterPerSample = 1008;

    public readonly Ym2612 Ym = new();
    public readonly Sn76489 Psg = new();

    /// <summary>When false the chips still run (so timers work) but no audio is stored.</summary>
    public bool Capture;
    /// <summary>Sample rate of the buffers returned by <see cref="Collect"/>.</summary>
    public int OutputRate = 48000;

    private long rendered;                                   // master time up to which samples exist
    private int[] natL = new int[8192], natR = new int[8192];
    private int natCount;
    private double readPos;                                  // fractional read position in the native stream

    private short[] outBuf = new short[8192];

    /// <summary>Brings the chips up to date with the given master-clock time (ignored if already past it).</summary>
    public long SyncTicks;      // stopwatch ticks spent generating samples (for profiling)

    public void SyncTo(long master)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        while (rendered + MasterPerSample <= master)
        {
            Ym.Tick(out int l, out int r);
            double p = Psg.Render(MasterPerSample);
            l += (int)p; r += (int)p;
            l = l > 32767 ? 32767 : l < -32768 ? -32768 : l;
            r = r > 32767 ? 32767 : r < -32768 ? -32768 : r;
            rendered += MasterPerSample;
            if (Capture) Push(l, r);
        }
        SyncTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
    }

    private void Push(int l, int r)
    {
        if (natCount == natL.Length)
        {
            // Nobody is collecting; keep the newest samples only.
            int keep = natL.Length / 2;
            Array.Copy(natL, natCount - keep, natL, 0, keep);
            Array.Copy(natR, natCount - keep, natR, 0, keep);
            natCount = keep; readPos = Math.Max(0, readPos - (natL.Length - keep));
        }
        natL[natCount] = l; natR[natCount] = r; natCount++;
    }

    /// <summary>
    /// Resamples everything generated so far to <see cref="OutputRate"/> (linear interpolation).
    /// Returns interleaved left/right 16-bit samples; the array is reused by the next call.
    /// </summary>
    public (short[] Samples, int Frames) Collect()
    {
        double step = Ym2612.SampleRate / OutputRate;
        int frames = 0;
        int capacity = outBuf.Length / 2;
        while (readPos + 1 < natCount && frames < capacity)
        {
            int i = (int)readPos;
            double f = readPos - i;
            outBuf[frames * 2] = (short)(natL[i] + (natL[i + 1] - natL[i]) * f);
            outBuf[frames * 2 + 1] = (short)(natR[i] + (natR[i + 1] - natR[i]) * f);
            frames++;
            readPos += step;
        }
        int drop = Math.Min((int)readPos, natCount);
        if (drop > 0)
        {
            Array.Copy(natL, drop, natL, 0, natCount - drop);
            Array.Copy(natR, drop, natR, 0, natCount - drop);
            natCount -= drop; readPos -= drop;
        }
        return (outBuf, frames);
    }

    public void Reset() { Ym.Reset(); Psg.Reset(); }
}
