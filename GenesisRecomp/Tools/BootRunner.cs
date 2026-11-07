using GenesisRecomp.Cpu;
using GenesisRecomp.Machine;

namespace GenesisRecomp.Tools;

public static class BootRunner
{
    public static int Run(byte[] rom, long maxSteps)
    {
        var bus = new GenesisBus(rom);
        var cpu = new Cpu68k(bus);
        cpu.Reset();
        Console.WriteLine($"Reset: PC=0x{cpu.PC:X8}  SP=0x{cpu.A[7]:X8}  SR=0x{cpu.SR:X4}");

        uint lastPc = uint.MaxValue;
        long steps = 0;
        string stop = $"step limit ({maxSteps:N0}) reached";
        var seenVdp = false;

        for (; steps < maxSteps; steps++)
        {
            uint pc = cpu.PC;
            try { cpu.Step(); }
            catch (AddressErrorException ex) { stop = $"address error at PC=0x{pc:X8}: {ex.Message}"; break; }

            if (cpu.LastExceptionVector >= 0)
            {
                stop = $"exception vector {cpu.LastExceptionVector} raised by the instruction at 0x{pc:X8}";
                break;
            }
            if (!seenVdp && bus.Vdp.AccessCount > 0)
            {
                seenVdp = true;
                Console.WriteLine($"First VDP access after {steps + 1:N0} instructions (instruction at 0x{pc:X8}).");
            }
            if (cpu.PC == pc) { stop = $"tight self-loop at 0x{pc:X8} (waiting for something we do not model yet)"; break; }
            lastPc = pc;
        }

        Console.WriteLine($"Stopped after {steps:N0} instructions: {stop}");
        Console.WriteLine($"Final: PC=0x{cpu.PC:X8}  SR=0x{cpu.SR:X4}  A7=0x{cpu.A[7]:X8}");
        Console.WriteLine("  D: " + string.Join(" ", cpu.D.Select(v => v.ToString("X8"))));
        Console.WriteLine("  A: " + string.Join(" ", cpu.A.Select(v => v.ToString("X8"))));
        Console.WriteLine();

        Console.WriteLine($"I/O events ({bus.IoLog.Count}):");
        foreach (string e in bus.IoLog.Take(30)) Console.WriteLine("  " + e);
        Console.WriteLine();

        Console.WriteLine($"VDP accesses: {bus.Vdp.AccessCount:N0} (first 60 shown)");
        foreach (string e in bus.Vdp.Log.Take(60)) Console.WriteLine("  " + e);
        return 0;
    }
}
