using GenesisRecomp.Cpu;

namespace GenesisRecomp.Machine;

/// <summary>A Mega Drive: 68000 + Z80 + VDP + sound chips, run one video frame at a time (NTSC: 262 lines of 488 CPU cycles).</summary>
public sealed class GenesisMachine
{
    public const int CyclesPerLine = 488, LinesPerFrame = 262;

    public readonly GenesisBus Bus;
    public readonly Cpu68k Cpu;
    public Vdp Vdp => Bus.Vdp;
    /// <summary>Build the rest of a live-recompiled game on a background thread while it plays (turned off by --verify tools and benchmarks that want a plain single thread).</summary>
    public static bool WarmUpRecompiler = true;
    public long FrameCount;
    /// <summary>Stopwatch ticks spent in the 68000 + Z80 loop (the rest of a frame is video and sound). For profiling.</summary>
    public long CpuLoopTicks;

    /// <summary>How many times each exception vector was raised, and where the first few came from.</summary>
    public readonly Dictionary<int, int> ExceptionCounts = new();
    public readonly List<string> FirstExceptions = new();

    private long lineEnd;
    private long z80Time;                       // master-clock time the Z80 has been simulated up to (68000 cycle = 7, Z80 cycle = 15)

    public GenesisMachine(byte[] rom)
    {
        Bus = new GenesisBus(rom);
        Cpu = new Cpu68k(Bus);
        Bus.Vdp.IrqChanged = Cpu.SetIrqLine;
        Cpu.InterruptAcknowledged = Bus.Vdp.InterruptAcknowledged;
        Cpu.Reset();
        lineEnd = Cpu.Cycles;
        // Use the statically recompiled code if this program contains it for exactly this ROM; otherwise interpret.
        if (!Cpu.SelectRecompiled(Recomp.RomRecompiler.Crc32(rom)) && Cpu68k.AllowRecompiled && Cpu68k.AllowLive)
        {
            // Not built into the program: translate the game's 68000 code now, in memory (pieces are compiled the first time they run).
            try { var live = Recomp.LiveRecompiler.Create(rom); Cpu.SelectLive(live); if (WarmUpRecompiler) live.StartWarmup(); }
            catch (Exception) { /* anything unexpected: just interpret */ }
        }
    }

    /// <summary>True when the 68000 is running the generated (statically recompiled) code instead of the interpreter.</summary>
    public bool UsingRecompiledCode => Cpu.UseRecompiled;

    public void RunFrame()
    {
        bool recompiled = Cpu.UseRecompiled;
        for (int line = 0; line < LinesPerFrame; line++)
        {
            long lineStart = lineEnd;
            lineEnd += CyclesPerLine;
            Vdp.CycleInLine = 0;
            Vdp.StartLine(line);
            if (line == Vdp.Height) Bus.Z80.IntLine = true;                     // the vertical interrupt also goes to the Z80...
            else if (line == Vdp.Height + 1) Bus.Z80.IntLine = false;           // ...for about one scanline
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            while (Cpu.Cycles < lineEnd)
            {
                Vdp.CycleInLine = (int)(Cpu.Cycles - lineStart);
                uint pc = Cpu.PC;
                Bus.Now68k = Cpu.Cycles * 7;
                if (recompiled) Cpu.StepBlock(); else Cpu.Step();
                if (Cpu.LastExceptionVector >= 0) NoteException(Cpu.LastExceptionVector, pc);
                if (Cpu.Stopped && Cpu.Cycles < lineEnd) Cpu.Cycles = lineEnd;   // STOP: idle until an interrupt
                RunZ80(Cpu.Cycles * 7);
            }
            CpuLoopTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        }
        Bus.Sound.SyncTo(lineEnd * 7);
        FrameCount++;
    }

    /// <summary>Lets the Z80 catch up with the 68000 (both measured in master-clock cycles).</summary>
    private void RunZ80(long target)
    {
        if (!Bus.Z80Runnable) { z80Time = target; return; }             // held in reset or bus taken: its clock just keeps up
        var z80 = Bus.Z80;
        while (z80Time < target)
        {
            Bus.Z80Time = z80Time;
            z80Time += z80.Step() * 15L;
        }
    }

    private void NoteException(int vector, uint pc)
    {
        ExceptionCounts[vector] = ExceptionCounts.GetValueOrDefault(vector) + 1;
        if (FirstExceptions.Count < 12) FirstExceptions.Add($"vector {vector} at PC=0x{pc:X6} (frame {FrameCount})");
    }
}
