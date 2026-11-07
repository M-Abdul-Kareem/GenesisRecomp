namespace GenesisRecomp.Machine;

/// <summary>
/// Sega Mega Drive video display processor (VDP), Mode 5.
/// Models the registers, the three video memories, the control/data ports, DMA (instantaneous),
/// the status register and the vertical/horizontal blanking interrupts. Rendering is in VdpRender.cs.
/// </summary>
public sealed partial class Vdp
{
    /// <summary>Stopwatch ticks spent drawing scanlines (for profiling).</summary>
    public long RenderTicks;
    public readonly byte[] Vram = new byte[0x10000];
    public readonly ushort[] Cram = new ushort[64];
    public readonly ushort[] Vsram = new ushort[64];      // only the first 40 entries exist on hardware
    public readonly byte[] R = new byte[32];              // VDP registers 0-23

    /// <summary>Raised when the VDP asserts or releases an interrupt line (level 6 = VBlank, level 4 = HBlank).</summary>
    public Action<int, bool>? IrqChanged;

    public int AccessCount, DmaCount, VintCount, HintCount;
    public readonly List<string> Log = new();
    public int LogLimit = 600;
    public int CycleInLine;                               // set by the machine so status/HV reads can look like real time

    private readonly Func<uint, ushort> busRead16;        // used by 68000 -> VDP DMA
    private uint addr;
    private int code;
    private bool pending;                                 // first word of a two-word command was written
    private bool dmaFillPending;
    private bool vintPending, hintPending, vblank, oddFrame, spriteOverflow, spriteCollision;
    private int line, hCounter;

    public Vdp(Func<uint, ushort> busRead16) => this.busRead16 = busRead16;

    public bool InVblank => vblank;
    public int CurrentLine => line;

    // ---------- bus-facing ports ----------

    public ushort Read(uint address, int width)
    {
        AccessCount++;
        uint port = address & 0x1F;
        string name;
        ushort result;
        if (port < 4) { name = "data"; result = ReadData(); }
        else if (port < 8) { name = "control/status"; pending = false; result = Status(); }
        else if (port < 16) { name = "HV counter"; result = HvCounter(); }
        else { name = "PSG/other"; result = 0; }
        if (Log.Count < LogLimit) Log.Add($"VDP read{width * 8}  {address:X6} ({name}) -> {result:X4}");
        return result;
    }

    public void Write(uint address, ushort value, int width)
    {
        AccessCount++;
        uint port = address & 0x1F;
        if (width == 1) value = (ushort)((value & 0xFF) | ((value & 0xFF) << 8));    // byte writes are mirrored into both halves
        string name = port < 4 ? "data" : port < 8 ? "control" : port < 16 ? "HV counter" : "PSG/other";
        if (Log.Count < LogLimit) Log.Add($"VDP write{width * 8} {address:X6} ({name}) = {value:X4}");
        if (port < 4) WriteData(value);
        else if (port < 8) WriteControl(value);
        // 0x10-0x1F: PSG sound chip (not emulated yet)
    }

    // ---------- control port ----------

    private void WriteControl(ushort v)
    {
        if (pending)
        {
            pending = false;
            code = (code & 0x03) | ((v >> 2) & 0x3C);                   // CD5..CD2 come from bits 7-4 of the second word
            addr = (addr & 0x3FFF) | ((uint)(v & 3) << 14);
            if ((code & 0x20) != 0 && (R[1] & 0x10) != 0) StartDma();
            return;
        }
        if ((v & 0xC000) == 0x8000)                                     // register write: 100R RRRR DDDD DDDD
        {
            int reg = (v >> 8) & 0x1F;
            if (reg < 24)
            {
                R[reg] = (byte)v;
                if (reg <= 1) UpdateIrq();
            }
            return;
        }
        code = (code & 0x3C) | (v >> 14);
        addr = (addr & 0xC000) | (uint)(v & 0x3FFF);
        pending = true;
    }

    private ushort Status()
    {
        ushort s = 0x3600;                                              // FIFO empty, unused bits as seen on hardware
        if (vintPending) s |= 0x80;
        if (spriteOverflow) s |= 0x40;
        if (spriteCollision) s |= 0x20;
        if (oddFrame) s |= 0x10;
        if (vblank || (R[1] & 0x40) == 0) s |= 0x08;                    // display off counts as blanking
        if (CycleInLine >= 400) s |= 0x04;                              // horizontal blanking: last part of the line
        spriteOverflow = spriteCollision = false;
        return s;
    }

    private ushort HvCounter()
    {
        int v = line <= 0xEA ? line : line - 6;                         // NTSC V28 counter jump
        int h = CycleInLine * 180 / 488;
        return (ushort)(((v & 0xFF) << 8) | (h & 0xFF));
    }

    // ---------- data port ----------

    private void WriteData(ushort v)
    {
        pending = false;
        if (dmaFillPending) { dmaFillPending = false; DmaFill(v); return; }
        WriteDataWord(v);
    }

    private void WriteDataWord(ushort v)
    {
        switch (code & 0x0F)
        {
            case 1:                                                      // VRAM write
            {
                int a = (int)(addr & 0xFFFF);
                if ((a & 1) != 0) v = (ushort)((v >> 8) | (v << 8));     // odd address swaps the two bytes
                Vram[a & 0xFFFE] = (byte)(v >> 8);
                Vram[(a & 0xFFFE) + 1] = (byte)v;
                break;
            }
            case 3: Cram[(addr >> 1) & 0x3F] = (ushort)(v & 0x0EEE); break;
            case 5:
            {
                int i = (int)((addr >> 1) & 0x3F);
                if (i < 40) Vsram[i] = (ushort)(v & 0x07FF);
                break;
            }
        }
        addr = (addr + R[15]) & 0xFFFF;
    }

    private ushort ReadData()
    {
        pending = false;
        ushort v = 0;
        switch (code & 0x0F)
        {
            case 0: { int a = (int)(addr & 0xFFFE); v = (ushort)((Vram[a] << 8) | Vram[a + 1]); break; }
            case 8: v = Cram[(addr >> 1) & 0x3F]; break;
            case 4: { int i = (int)((addr >> 1) & 0x3F); v = i < 40 ? Vsram[i] : (ushort)0; break; }
        }
        addr = (addr + R[15]) & 0xFFFF;
        return v;
    }

    // ---------- DMA (performed instantly) ----------

    private int DmaLength()
    {
        int len = R[19] | (R[20] << 8);
        return len == 0 ? 0x10000 : len;
    }

    private void StartDma()
    {
        DmaCount++;
        int mode = R[23] >> 6;
        if (mode < 2) { Dma68k(); return; }
        if (mode == 2) { dmaFillPending = true; return; }
        DmaCopy();
    }

    /// <summary>68000 memory (ROM or RAM) to VRAM/CRAM/VSRAM, one word at a time.</summary>
    private void Dma68k()
    {
        int len = DmaLength();
        uint src = ((uint)R[21] | ((uint)R[22] << 8) | ((uint)(R[23] & 0x7F) << 16)) << 1;
        for (int i = 0; i < len; i++)
        {
            WriteDataWord(busRead16(src));
            src = (src & 0xFE0000) | ((src + 2) & 0x1FFFF);          // the source wraps inside its 128 KB block
        }
        FinishDma(src >> 1);
    }

    /// <summary>VRAM fill: the data word is written normally, then the high byte is repeated for the length.</summary>
    private void DmaFill(ushort data)
    {
        int len = DmaLength();
        WriteDataWord(data);
        byte fill = (byte)(data >> 8);
        for (int i = 0; i < len; i++)
        {
            Vram[addr & 0xFFFF] = fill;
            addr = (addr + R[15]) & 0xFFFF;
        }
        FinishDma(null);
    }

    private void DmaCopy()
    {
        int len = DmaLength();
        uint src = (uint)(R[21] | (R[22] << 8));
        for (int i = 0; i < len; i++)
        {
            Vram[addr & 0xFFFF] = Vram[src & 0xFFFF];
            src++;
            addr = (addr + R[15]) & 0xFFFF;
        }
        FinishDma(null);
    }

    private void FinishDma(uint? nextSourceWord)
    {
        R[19] = R[20] = 0;                                              // the length counts down to zero
        if (nextSourceWord is uint s)
        {
            R[21] = (byte)s;
            R[22] = (byte)(s >> 8);
            R[23] = (byte)((R[23] & 0x80) | ((s >> 16) & 0x7F));
        }
    }

    // ---------- timing events (called by the machine) ----------

    public void StartLine(int l)
    {
        line = l;
        int active = Height;
        if (l == 0)
        {
            vblank = false;
            oddFrame = !oddFrame;
            vintPending = false;
            hCounter = R[10];
        }
        if (l < active)
        {
            long rt = System.Diagnostics.Stopwatch.GetTimestamp();
            RenderLine(l);
            RenderTicks += System.Diagnostics.Stopwatch.GetTimestamp() - rt;
            if (hCounter == 0) { hCounter = R[10]; hintPending = true; HintCount++; }
            else hCounter--;
        }
        else hCounter = R[10];
        if (l == active) { vblank = true; vintPending = true; VintCount++; }
        UpdateIrq();
    }

    public void InterruptAcknowledged(int level)
    {
        if (level == 6) vintPending = false;
        else if (level == 4) hintPending = false;
        UpdateIrq();
    }

    private void UpdateIrq()
    {
        IrqChanged?.Invoke(6, vintPending && (R[1] & 0x20) != 0);
        IrqChanged?.Invoke(4, hintPending && (R[0] & 0x10) != 0);
    }
}
