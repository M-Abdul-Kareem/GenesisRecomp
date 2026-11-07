using GenesisRecomp.Machine;

namespace GenesisRecomp.Tools;

/// <summary>
/// Runs a CP/M .com program (such as the zexdoc / zexall Z80 instruction exercisers) on the Z80 core.
/// The program is loaded at 0x0100; calls to address 0005h are treated as CP/M BDOS "print" calls.
/// Usage: GenesisRecomp --verify-z80 zexdoc.cim
/// </summary>
public static class Z80Verify
{
    private sealed class CpmBus : IZ80Bus
    {
        public readonly byte[] Mem = new byte[0x10000];
        public byte Read(ushort a) => Mem[a];
        public void Write(ushort a, byte v) => Mem[a] = v;
        public byte In(ushort p) => 0xFF;
        public void Out(ushort p, byte v) { }
    }

    public static int Run(string path)
    {
        var bus = new CpmBus();
        byte[] prog = File.ReadAllBytes(path);
        Array.Copy(prog, 0, bus.Mem, 0x100, prog.Length);
        bus.Mem[0] = 0x76;                       // warm boot -> HALT
        bus.Mem[5] = 0xC9;                       // BDOS entry (intercepted below)
        bus.Mem[6] = 0x00; bus.Mem[7] = 0xF0;    // top of memory pointer used by the programs

        var z = new Z80(bus) { PC = 0x100, SP = 0xF000 };
        var output = new System.Text.StringBuilder();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        long lastReport = 0;
        int failures = 0;

        void Flush()
        {
            string text = output.ToString();
            Console.Write(text);
            if (text.Contains("ERROR")) failures++;
            output.Clear();
        }

        while (true)
        {
            if (z.PC == 0x0000) break;
            if (z.PC == 0x0005)
            {
                if (z.C == 2) output.Append((char)z.E);
                else if (z.C == 9)
                {
                    ushort a = z.DE;
                    while (bus.Mem[a] != '$') output.Append((char)bus.Mem[a++]);
                }
                if (output.Length > 0 && output.ToString().Contains('\n')) Flush();
                ushort ret = (ushort)(bus.Mem[z.SP] | (bus.Mem[(ushort)(z.SP + 1)] << 8));
                z.SP += 2; z.PC = ret;
                continue;
            }
            z.Step();
            if (watch.ElapsedMilliseconds - lastReport > 60000)
            {
                lastReport = watch.ElapsedMilliseconds;
                Console.Error.WriteLine($"   ... {z.Cycles / 1e9:F2} billion T-states in {watch.Elapsed.TotalSeconds:F0}s ({z.Cycles / watch.Elapsed.TotalSeconds / 1e6:F0} MHz effective)");
            }
        }
        Flush();
        Console.WriteLine();
        Console.WriteLine($"Finished: {z.Cycles / 1e9:F2} billion T-states in {watch.Elapsed.TotalSeconds:F0}s ({z.Cycles / watch.Elapsed.TotalSeconds / 1e6:F0} MHz effective). Lines with ERROR: {failures}");
        return failures == 0 ? 0 : 1;
    }
}
