namespace GenesisRecomp.Machine;

/// <summary>
/// Texas Instruments SN76489 as used in the Mega Drive's VDP: three square-wave tone generators and one noise generator.
/// Clocked at the Z80 clock (3.579545 MHz) divided by 16.
/// </summary>
public sealed class Sn76489
{
    public const double ClockHz = 53693175.0 / 15.0;
    public const double TickRate = ClockHz / 16.0;                   // ~223,722 ticks per second

    private static readonly double[] Level = BuildLevels();
    private static double[] BuildLevels()
    {
        var t = new double[16];
        for (int i = 0; i < 15; i++) t[i] = Math.Pow(10.0, -2.0 * i / 20.0);   // 2 dB per step
        t[15] = 0;
        return t;
    }

    private readonly int[] tonePeriod = new int[3];
    private readonly int[] toneCount = new int[3];
    private readonly bool[] toneOut = new bool[3];
    private readonly int[] volume = { 15, 15, 15, 15 };           // attenuation; 15 = silent
    private int noiseControl;
    private int noiseCount;
    private bool noiseFlip;
    private int lfsr = 0x8000;
    private int latchChannel;
    private bool latchIsVolume;
    private double tickFraction;

    /// <summary>Peak amplitude of one channel at full volume (the mixer scales the PSG against the FM chip).</summary>
    public double Gain = 2800;

    public Sn76489() { Reset(); }

    public void Reset()
    {
        for (int i = 0; i < 3; i++) { tonePeriod[i] = 0; toneCount[i] = 0; toneOut[i] = false; }
        for (int i = 0; i < 4; i++) volume[i] = 15;
        noiseControl = 0; noiseCount = 0; noiseFlip = false; lfsr = 0x8000;
        latchChannel = 0; latchIsVolume = false; tickFraction = 0;
    }

    public void Write(byte v)
    {
        if ((v & 0x80) != 0)
        {
            latchChannel = (v >> 5) & 3;
            latchIsVolume = (v & 0x10) != 0;
            int data = v & 0x0F;
            if (latchIsVolume) volume[latchChannel] = data;
            else if (latchChannel < 3) tonePeriod[latchChannel] = (tonePeriod[latchChannel] & 0x3F0) | data;
            else SetNoise(data);
        }
        else
        {
            int data = v & 0x3F;
            if (latchIsVolume) volume[latchChannel] = data & 0x0F;
            else if (latchChannel < 3) tonePeriod[latchChannel] = (tonePeriod[latchChannel] & 0x00F) | (data << 4);
            else SetNoise(data & 7);
        }
    }

    private void SetNoise(int data)
    {
        noiseControl = data & 7;
        lfsr = 0x8000;                                              // writing the noise register resets the shift register
    }

    private int NoisePeriod() => (noiseControl & 3) switch
    {
        0 => 0x10, 1 => 0x20, 2 => 0x40, _ => Math.Max(1, tonePeriod[2])
    };

    private void TickOnce()
    {
        for (int i = 0; i < 3; i++)
        {
            if (--toneCount[i] <= 0)
            {
                toneCount[i] = Math.Max(1, tonePeriod[i]);
                toneOut[i] = !toneOut[i];
            }
        }
        if (--noiseCount <= 0)
        {
            noiseCount = NoisePeriod();
            noiseFlip = !noiseFlip;
            if (noiseFlip)                                          // the shift register moves on the rising edge
            {
                int feedback = (noiseControl & 4) != 0
                    ? ((lfsr & 1) ^ ((lfsr >> 3) & 1))              // white noise: taps on bits 0 and 3
                    : (lfsr & 1);                                   // periodic noise
                lfsr = (lfsr >> 1) | (feedback << 15);
            }
        }
    }

    /// <summary>Mono output averaged over <paramref name="masterCycles"/> master-clock cycles (one native audio sample).</summary>
    public double Render(int masterCycles)
    {
        tickFraction += masterCycles / 240.0;                       // one PSG tick = 16 Z80 clocks = 240 master cycles
        int ticks = (int)tickFraction;
        tickFraction -= ticks;
        if (ticks == 0) ticks = 0;

        double sum = 0;
        for (int t = 0; t < ticks; t++)
        {
            TickOnce();
            double s = 0;
            for (int i = 0; i < 3; i++) s += (toneOut[i] ? 1.0 : -1.0) * Level[volume[i]];
            s += ((lfsr & 1) != 0 ? 1.0 : -1.0) * Level[volume[3]];
            sum += s;
        }
        return ticks == 0 ? 0 : sum / ticks * Gain;
    }
}
