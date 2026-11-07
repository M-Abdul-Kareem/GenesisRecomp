namespace GenesisRecomp.Machine;

/// <summary>
/// Yamaha YM2612 (OPN2): six FM channels of four operators each, plus the DAC on channel 6, two timers and an LFO.
/// One call to <see cref="Tick"/> produces one output sample at the chip's native rate (clock / 144 = 53,267 Hz).
/// Not modelled: SSG-EG (envelope looping modes), CSM mode, the DAC "ladder" distortion, and the busy flag (always reads 0).
/// </summary>
public sealed class Ym2612
{
    public const double ClockHz = 53693175.0 / 7.0;                  // same clock as the 68000
    public const double SampleRate = ClockHz / 144.0;                // ~53,267 Hz

    // ---------- tables ----------
    private static readonly int[] LogSin = new int[256];             // attenuation of the first quarter of a sine wave, 1/256 octave units
    private static readonly int[] ExpTab = new int[256];             // 2^(-x) mantissa
    private static readonly int[] DetuneTab = new int[4 * 32]
    {
        0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,
        0,0,0,0,0,0,0,0,1,1,1,1,2,2,2,2,2,3,3,3,4,4,4,5,5,6,6,7,8,8,8,8,
        1,1,1,1,2,2,2,2,2,3,3,3,4,4,4,5,5,6,6,7,8,8,9,10,11,12,13,14,16,16,16,16,
        2,2,2,2,2,3,3,3,4,4,4,5,5,6,6,7,8,8,9,10,11,12,13,14,16,17,19,20,22,22,22,22
    };

    // Envelope increments: row = rate pattern, column = (counter >> shift) & 7.
    private static readonly byte[] EgInc =
    {
        0,1,0,1,0,1,0,1,   0,1,0,1,1,1,0,1,   0,1,1,1,0,1,1,1,   0,1,1,1,1,1,1,1,     // rows 0-3   rates 0-47
        1,1,1,1,1,1,1,1,   1,1,1,2,1,1,1,2,   1,2,1,2,1,2,1,2,   1,2,2,2,1,2,2,2,     // rows 4-7   rates 48-51 ...
        2,2,2,2,2,2,2,2,   2,2,2,4,2,2,2,4,   2,4,2,4,2,4,2,4,   2,4,4,4,2,4,4,4,     // rows 8-11
        4,4,4,4,4,4,4,4,   4,4,4,8,4,4,4,8,   4,8,4,8,4,8,4,8,   4,8,8,8,4,8,8,8,     // rows 12-15
        8,8,8,8,8,8,8,8                                                               // row 16      rates 60-63
    };

    private static readonly int[] AmsShift = { 8, 3, 1, 0 };                          // LFO amplitude depth: off, 1.4 dB, 5.9 dB, 11.8 dB
    private static readonly double[] PmCents = { 0, 3.4, 6.7, 10, 14, 20, 40, 80 };   // LFO vibrato depth
    private static readonly double[] LfoHz = { 3.98, 5.56, 6.02, 6.37, 6.88, 9.63, 48.1, 72.2 };
    private static readonly int[] OpOfRegSlot = { 0, 2, 1, 3 };                       // register offsets +0,+4,+8,+C = operators S1,S3,S2,S4

    static Ym2612()
    {
        for (int i = 0; i < 256; i++)
        {
            LogSin[i] = (int)Math.Round(-Math.Log2(Math.Sin((i + 0.5) * Math.PI / 512.0)) * 256.0);
            ExpTab[i] = (int)Math.Round((Math.Pow(2.0, (255 - i) / 256.0) - 1.0) * 1024.0);
        }
    }

    private enum EgState { Attack, Decay, Sustain, Release, Off }

    private sealed class Slot
    {
        // registers
        public int Dt, Mul, Tl, Ks, Ar, D1r, D2r, D1l, Rr;
        public bool AmOn;
        // frequency info (set from the channel, or per slot in channel-3 special mode)
        public int Fnum, Block, Kc;
        // cached
        public int TlAtt;          // Tl << 3
        public int Sl;             // sustain level in attenuation units
        public int RateAr, RateD1r, RateD2r, RateRr;
        // running state
        public uint Phase;         // 20-bit phase accumulator
        public int Env = 1023;     // attenuation, 0 = loudest
        public EgState State = EgState.Off;
        public bool KeyOn;
        public int Prev1, Prev2;   // last two outputs (operator 1 feedback)
    }

    private sealed class Channel
    {
        public readonly Slot[] S = { new(), new(), new(), new() };     // logical operators S1..S4
        public int Fnum, Block, Kc;
        public int Alg, Fb, Ams, Fms;
        public bool PanL = true, PanR = true;
        public int Latch;                                              // high bits waiting for the low-byte write
    }

    private readonly Channel[] ch = new Channel[6];
    private readonly int[] addrLatch = new int[2];
    private readonly byte[] regs = new byte[512];

    private bool lfoOn;
    private int lfoFreq;
    private double lfoStepAcc;
    private int lfoPos;                 // 0..127
    private int lfoAm;                  // 0..126
    private double lfoPmTri;            // -1..1

    private int egCnt, egDiv;
    private int dacValue = 0x80;
    private bool dacOn;

    // timers
    private int timerA, timerB;
    private int timerACount, timerBCount, timerBDiv;
    private bool timerARun, timerBRun, timerAEnable, timerBEnable;
    private int status;                 // bit0 = timer A overflow, bit1 = timer B overflow
    /// <summary>How often a game switched on SSG-EG / CSM mode, two features this chip emulation does not model.</summary>
    public int SsgEgEnabledWrites, CsmModeWrites;
    private int mode3;                  // channel-3 mode (0 normal, 1/2 special)
    private int ch3Latch;
    private readonly int[] ch3Fnum = new int[3];
    private readonly int[] ch3Block = new int[3];

    public Ym2612() { for (int i = 0; i < 6; i++) ch[i] = new Channel(); Reset(); }

    public void Reset()
    {
        Array.Clear(regs);
        for (int c = 0; c < 6; c++)
        {
            var chn = ch[c];
            chn.Fnum = chn.Block = chn.Kc = chn.Alg = chn.Fb = chn.Ams = chn.Fms = 0;
            chn.PanL = chn.PanR = true;
            foreach (var s in chn.S)
            {
                s.Dt = s.Mul = s.Tl = s.Ks = s.Ar = s.D1r = s.D2r = s.D1l = s.Rr = 0;
                s.AmOn = false; s.Fnum = s.Block = s.Kc = 0;
                s.Phase = 0; s.Env = 1023; s.State = EgState.Off; s.KeyOn = false; s.Prev1 = s.Prev2 = 0;
                RecalcSlot(s);
            }
        }
        lfoOn = false; lfoFreq = 0; lfoStepAcc = 0; lfoPos = 0; lfoAm = 0; lfoPmTri = 0;
        egCnt = 0; egDiv = 0; dacValue = 0x80; dacOn = false;
        timerA = timerB = timerACount = timerBCount = timerBDiv = 0;
        timerARun = timerBRun = timerAEnable = timerBEnable = false;
        status = 0; mode3 = 0; ch3Latch = 0;
        addrLatch[0] = addrLatch[1] = 0;
    }

    // ================= bus interface =================

    /// <summary>port 0/2 = address, 1/3 = data (ports 2 and 3 address the second register bank).</summary>
    public void Write(int port, byte value)
    {
        int part = (port >> 1) & 1;
        if ((port & 1) == 0) { addrLatch[part] = value; return; }
        WriteRegister(part, addrLatch[part], value);
    }

    public byte ReadStatus() => (byte)status;

    private void WriteRegister(int part, int addr, byte v)
    {
        regs[(part << 8) | addr] = v;

        if (addr < 0x30)
        {
            if (part != 0) return;
            switch (addr)
            {
                case 0x22:
                    lfoOn = (v & 8) != 0; lfoFreq = v & 7;
                    if (!lfoOn) { lfoPos = 0; lfoStepAcc = 0; lfoAm = 126; lfoPmTri = 0; }
                    break;
                case 0x24: timerA = (timerA & 0x003) | (v << 2); break;
                case 0x25: timerA = (timerA & 0x3FC) | (v & 3); break;
                case 0x26: timerB = v; break;
                case 0x27:
                    { int oldMode = mode3; mode3 = (v >> 6) & 3; if (mode3 == 2) CsmModeWrites++; if ((oldMode != 0) != (mode3 != 0)) ApplyChannelFreq(2); }
                    timerAEnable = (v & 4) != 0; timerBEnable = (v & 8) != 0;
                    if ((v & 0x10) != 0) status &= ~1;
                    if ((v & 0x20) != 0) status &= ~2;
                    if ((v & 1) != 0) { if (!timerARun) timerACount = timerA; timerARun = true; } else timerARun = false;
                    if ((v & 2) != 0) { if (!timerBRun) { timerBCount = timerB; timerBDiv = 0; } timerBRun = true; } else timerBRun = false;
                    break;
                case 0x28: KeyOnOff(v); break;
                case 0x2A: dacValue = v; break;
                case 0x2B: dacOn = (v & 0x80) != 0; break;
            }
            return;
        }

        int c = addr & 3;
        if (c == 3) return;
        int channel = c + part * 3;
        var chn = ch[channel];

        if (addr < 0xA0)
        {
            Slot s = chn.S[OpOfRegSlot[(addr >> 2) & 3]];
            switch (addr & 0xF0)
            {
                case 0x30: s.Dt = (v >> 4) & 7; s.Mul = v & 15; break;
                case 0x40: s.Tl = v & 0x7F; break;
                case 0x50: s.Ks = (v >> 6) & 3; s.Ar = v & 0x1F; break;
                case 0x60: s.AmOn = (v & 0x80) != 0; s.D1r = v & 0x1F; break;
                case 0x70: s.D2r = v & 0x1F; break;
                case 0x80: s.D1l = (v >> 4) & 15; s.Rr = v & 15; break;
                case 0x90: if ((v & 8) != 0) SsgEgEnabledWrites++; break;   // SSG-EG: not implemented (counted so we know if a game uses it)
            }
            RecalcSlot(s);
            return;
        }

        switch (addr & 0xFC)
        {
            case 0xA0: // frequency low byte (applies the latched high bits)
                chn.Fnum = ((chn.Latch & 7) << 8) | v;
                chn.Block = (chn.Latch >> 3) & 7;
                ApplyChannelFreq(channel);
                break;
            case 0xA4: chn.Latch = v & 0x3F; break;
            case 0xA8: // channel-3 per-operator frequency (low): 0xA8 -> S3, 0xA9 -> S1, 0xAA -> S2
                if (part == 0 && c < 3)
                {
                    ch3Fnum[c] = ((ch3Latch & 7) << 8) | v;
                    ch3Block[c] = (ch3Latch >> 3) & 7;
                    ApplyChannelFreq(2);
                }
                break;
            case 0xAC: if (part == 0 && c < 3) ch3Latch = v & 0x3F; break;
            case 0xB0:
                chn.Alg = v & 7; chn.Fb = (v >> 3) & 7;
                break;
            case 0xB4:
                chn.PanL = (v & 0x80) != 0; chn.PanR = (v & 0x40) != 0;
                chn.Ams = (v >> 4) & 3; chn.Fms = v & 7;
                break;
        }
    }

    private static int KeyCode(int fnum, int block)
    {
        int f11 = (fnum >> 10) & 1, f10 = (fnum >> 9) & 1, f9 = (fnum >> 8) & 1, f8 = (fnum >> 7) & 1;
        int n3 = f11 != 0 ? (f10 | f9 | f8) : (f10 & f9 & f8);
        return (block << 2) | (f11 << 1) | n3;
    }

    private void ApplyChannelFreq(int channel)
    {
        var chn = ch[channel];
        chn.Kc = KeyCode(chn.Fnum, chn.Block);
        foreach (var s in chn.S) { s.Fnum = chn.Fnum; s.Block = chn.Block; s.Kc = chn.Kc; }
        if (channel == 2 && mode3 != 0)
        {
            // In special mode three of the four operators have their own frequency: S1 <- A9/AD, S2 <- AA/AE, S3 <- A8/AC, S4 <- normal.
            int[] src = { 1, 2, 0 };
            for (int i = 0; i < 3; i++)
            {
                var s = chn.S[i];
                s.Fnum = ch3Fnum[src[i]]; s.Block = ch3Block[src[i]];
                s.Kc = KeyCode(s.Fnum, s.Block);
            }
        }
        foreach (var s in chn.S) RecalcSlot(s);
    }

    private static int Rate6(int rate5, int ksr)
    {
        if (rate5 == 0) return 0;
        int r = rate5 * 2 + ksr;
        return r > 63 ? 63 : r;
    }

    private void RecalcSlot(Slot s)
    {
        int ksr = s.Kc >> (3 - s.Ks);
        s.TlAtt = s.Tl << 3;
        s.Sl = (s.D1l == 15 ? 31 : s.D1l) << 5;
        s.RateAr = Rate6(s.Ar, ksr);
        s.RateD1r = Rate6(s.D1r, ksr);
        s.RateD2r = Rate6(s.D2r, ksr);
        s.RateRr = Math.Min(63, s.Rr * 4 + 2 + ksr);
    }

    private void KeyOnOff(byte v)
    {
        int c = v & 7;
        if (c == 3 || c == 7) return;
        var chn = ch[c < 3 ? c : c - 1];
        for (int op = 0; op < 4; op++)
        {
            Slot s = chn.S[op];
            if ((v & (0x10 << op)) != 0)
            {
                if (!s.KeyOn)
                {
                    s.KeyOn = true;
                    s.Phase = 0;
                    s.State = EgState.Attack;
                    if (s.RateAr >= 62) { s.Env = 0; s.State = s.Env >= s.Sl ? EgState.Sustain : EgState.Decay; }
                }
            }
            else if (s.KeyOn)
            {
                s.KeyOn = false;
                if (s.State != EgState.Off) s.State = EgState.Release;
            }
        }
    }

    // ================= sample generation =================

    /// <summary>Generates one stereo sample (about +-32767 full scale).</summary>
    public void Tick(out int left, out int right)
    {
        AdvanceTimers();
        AdvanceLfo();
        if (++egDiv == 3) { egDiv = 0; UpdateEnvelopes(); }

        int mixL = 0, mixR = 0;
        for (int c = 0; c < 6; c++)
        {
            var chn = ch[c];
            int outv;
            if (c == 5 && dacOn) outv = (dacValue - 128) << 6;
            else outv = ComputeChannel(chn);
            if (chn.PanL) mixL += outv;
            if (chn.PanR) mixR += outv;
        }
        left = Clamp16(mixL >> 1);
        right = Clamp16(mixR >> 1);
    }

    private static int Clamp16(int v) => v > 32767 ? 32767 : v < -32768 ? -32768 : v;

    private void AdvanceTimers()
    {
        if (timerARun && ++timerACount >= 1024)
        {
            timerACount = timerA;
            if (timerAEnable) status |= 1;
        }
        if (timerBRun && ++timerBDiv >= 16)
        {
            timerBDiv = 0;
            if (++timerBCount >= 256)
            {
                timerBCount = timerB;
                if (timerBEnable) status |= 2;
            }
        }
    }

    private void AdvanceLfo()
    {
        if (!lfoOn) return;
        lfoStepAcc += 1.0;
        double perStep = SampleRate / (LfoHz[lfoFreq] * 128.0);
        if (lfoStepAcc < perStep) return;
        lfoStepAcc -= perStep;
        lfoPos = (lfoPos + 1) & 127;
        lfoAm = (lfoPos < 64 ? lfoPos ^ 63 : lfoPos & 63) * 2;
        lfoPmTri = lfoPos < 32 ? lfoPos / 32.0 : lfoPos < 96 ? (64 - lfoPos) / 32.0 : (lfoPos - 128) / 32.0;
    }

    private void UpdateEnvelopes()
    {
        egCnt = (egCnt + 1) & 0xFFF;
        for (int c = 0; c < 6; c++)
            foreach (var s in ch[c].S)
            {
                switch (s.State)
                {
                    case EgState.Attack:
                    {
                        int inc = RateInc(s.RateAr);
                        if (inc > 0)
                        {
                            s.Env += ((~s.Env) * inc) >> 4;
                            if (s.Env <= 0) { s.Env = 0; s.State = s.Sl == 0 ? EgState.Sustain : EgState.Decay; }
                        }
                        break;
                    }
                    case EgState.Decay:
                        s.Env += RateInc(s.RateD1r);
                        if (s.Env >= s.Sl) { if (s.Env > 1023) s.Env = 1023; s.State = EgState.Sustain; }
                        break;
                    case EgState.Sustain:
                        s.Env += RateInc(s.RateD2r);
                        if (s.Env > 1023) s.Env = 1023;
                        break;
                    case EgState.Release:
                        s.Env += RateInc(s.RateRr);
                        if (s.Env >= 1023) { s.Env = 1023; s.State = EgState.Off; }
                        break;
                }
            }
    }

    /// <summary>Envelope step for the current counter value at the given 6-bit rate (0 = no change).</summary>
    private int RateInc(int rate)
    {
        if (rate == 0) return 0;
        int group = rate >> 2, sub = rate & 3;
        int shift = group <= 11 ? 11 - group : 0;
        if ((egCnt & ((1 << shift) - 1)) != 0) return 0;
        int row = group <= 11 ? sub : group <= 14 ? 4 * (group - 11) + sub : 16;
        return EgInc[row * 8 + ((egCnt >> shift) & 7)];
    }

    private int Operator(Slot s, int modulation, int amBoost)
    {
        int env = s.Env + s.TlAtt + amBoost;
        if (env > 1023) env = 1023;
        int phase = (int)((s.Phase >> 10) + (uint)modulation) & 0x3FF;
        int q = phase & 0xFF;
        if ((phase & 0x100) != 0) q ^= 0xFF;
        int att = LogSin[q] + (env << 2);
        if (att > 0xFFF) att = 0xFFF;
        int mag = ((ExpTab[att & 0xFF] | 0x400) << 1) >> (att >> 8);
        return (phase & 0x200) != 0 ? -mag : mag;
    }

    private void AdvancePhase(Channel chn, Slot s)
    {
        int fn = s.Fnum;
        if (lfoOn && chn.Fms != 0)
            fn = (int)(fn * Math.Pow(2.0, PmCents[chn.Fms] * lfoPmTri / 1200.0));
        int inc = (fn << s.Block) >> 1;
        int dt = DetuneTab[(s.Dt & 3) * 32 + s.Kc];
        inc += (s.Dt & 4) != 0 ? -dt : dt;
        inc &= 0x1FFFF;
        inc = s.Mul == 0 ? inc >> 1 : inc * s.Mul;
        s.Phase = (s.Phase + (uint)inc) & 0xFFFFF;
    }

    private int ComputeChannel(Channel chn)
    {
        if (chn.S[0].State == EgState.Off && chn.S[1].State == EgState.Off &&
            chn.S[2].State == EgState.Off && chn.S[3].State == EgState.Off)
        {
            chn.S[0].Prev1 = chn.S[0].Prev2 = 0;
            return 0;                                   // nothing sounding on this channel
        }
        // Phase generators first (they run even for silent operators).
        foreach (var s in chn.S) AdvancePhase(chn, s);

        int am = (lfoOn && chn.Ams != 0) ? lfoAm >> AmsShift[chn.Ams] : 0;
        Slot s1 = chn.S[0], s2 = chn.S[1], s3 = chn.S[2], s4 = chn.S[3];

        int fbMod = chn.Fb == 0 ? 0 : (s1.Prev1 + s1.Prev2) >> (10 - chn.Fb);
        int o1 = Operator(s1, fbMod, s1.AmOn ? am : 0);
        s1.Prev2 = s1.Prev1; s1.Prev1 = o1;

        int o2, o3, o4, result;
        int a2 = s2.AmOn ? am : 0, a3 = s3.AmOn ? am : 0, a4 = s4.AmOn ? am : 0;
        switch (chn.Alg)
        {
            case 0: o2 = Operator(s2, o1 >> 1, a2); o3 = Operator(s3, o2 >> 1, a3); result = Operator(s4, o3 >> 1, a4); break;
            case 1: o2 = Operator(s2, 0, a2); o3 = Operator(s3, (o1 + o2) >> 1, a3); result = Operator(s4, o3 >> 1, a4); break;
            case 2: o2 = Operator(s2, 0, a2); o3 = Operator(s3, o2 >> 1, a3); result = Operator(s4, (o1 + o3) >> 1, a4); break;
            case 3: o2 = Operator(s2, o1 >> 1, a2); o3 = Operator(s3, 0, a3); result = Operator(s4, (o2 + o3) >> 1, a4); break;
            case 4: o2 = Operator(s2, o1 >> 1, a2); o3 = Operator(s3, 0, a3); o4 = Operator(s4, o3 >> 1, a4); result = o2 + o4; break;
            case 5: o2 = Operator(s2, o1 >> 1, a2); o3 = Operator(s3, o1 >> 1, a3); o4 = Operator(s4, o1 >> 1, a4); result = o2 + o3 + o4; break;
            case 6: o2 = Operator(s2, o1 >> 1, a2); o3 = Operator(s3, 0, a3); o4 = Operator(s4, 0, a4); result = o2 + o3 + o4; break;
            default: o2 = Operator(s2, 0, a2); o3 = Operator(s3, 0, a3); o4 = Operator(s4, 0, a4); result = o1 + o2 + o3 + o4; break;
        }

        return result > 8191 ? 8191 : result < -8192 ? -8192 : result;
    }

    /// <summary>For diagnostics and tests: true if any operator on any channel is currently sounding.</summary>
    public bool AnyActive()
    {
        foreach (var c in ch) foreach (var s in c.S) if (s.State != EgState.Off) return true;
        return false;
    }
}
