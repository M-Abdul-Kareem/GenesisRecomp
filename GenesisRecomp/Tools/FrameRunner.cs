using GenesisRecomp.Cpu;
using GenesisRecomp.Machine;

namespace GenesisRecomp.Tools;

/// <summary>
/// Runs the machine for a number of frames and saves screenshots.
///   --run rom.bin [--frames N] [--dump 60,120,200-210] [--out folder] [--press start@100-110]...
/// </summary>
public static class FrameRunner
{
    private static readonly Dictionary<string, byte> Buttons = new()
    {
        ["up"] = 1, ["down"] = 2, ["left"] = 4, ["right"] = 8, ["b"] = 16, ["c"] = 32, ["a"] = 64, ["start"] = 128
    };

    public static int Run(string[] args)
    {
        string romPath = args[1];
        int frames = 300;
        string outDir = "frames";
        var dump = new HashSet<int>();
        var presses = new List<(byte Mask, int From, int To)>();

        for (int i = 2; i < args.Length - 1; i += 2)
        {
            switch (args[i])
            {
                case "--frames": frames = int.Parse(args[i + 1]); break;
                case "--out": outDir = args[i + 1]; break;
                case "--dump":
                    foreach (string part in args[i + 1].Split(','))
                    {
                        var r = part.Split('-');
                        int a = int.Parse(r[0]), b = r.Length > 1 ? int.Parse(r[1]) : a;
                        for (int f = a; f <= b; f++) dump.Add(f);
                    }
                    break;
                case "--press":
                {
                    var at = args[i + 1].Split('@');
                    var range = at[1].Split('-');
                    presses.Add((Buttons[at[0].ToLowerInvariant()], int.Parse(range[0]), int.Parse(range[^1])));
                    break;
                }
            }
        }

        var m = new GenesisMachine(File.ReadAllBytes(romPath));
        Directory.CreateDirectory(outDir);
        string stop = "";
        for (int f = 0; f < frames; f++)
        {
            byte pad = 0;
            foreach (var p in presses) if (f >= p.From && f <= p.To) pad |= p.Mask;
            m.Bus.Pad1 = pad;
            try { m.RunFrame(); }
            catch (AddressErrorException ex) { stop = $"address error at frame {f}, instruction at 0x{m.Cpu.CurrentInstructionAddress:X6}: {ex.Message}"; break; }
            if (dump.Contains(f))
                Bmp.Save(Path.Combine(outDir, $"frame_{f:D4}.bmp"), m.Vdp.Frame, 320, m.Vdp.Width, m.Vdp.Height);
        }

        var v = m.Vdp;
        if (m.Cpu.RecompSource is GenesisRecomp.Recomp.LiveRecompiler lr)
            Console.WriteLine($"CPU: recompiled in memory when the game was opened (code found in {lr.SetupMs:F0} ms; {lr.CompiledCount:N0} of {lr.SegmentCount:N0} pieces were reached and built in {lr.CompileMs:F0} ms total; {lr.FailedCount} failed; " +
                              $"{100.0 * m.Cpu.FallbackInsts / Math.Max(1, m.Cpu.InstructionCount):F1}% of instructions went through the interpreter)");
        else if (m.UsingRecompiledCode)
            Console.WriteLine($"CPU: recompiled code built into the program ({100.0 * m.Cpu.FallbackInsts / Math.Max(1, m.Cpu.InstructionCount):F1}% of instructions went through the interpreter)");
        else
            Console.WriteLine("CPU: interpreter");
        Console.WriteLine($"YM2612 features not emulated, times the game used them: SSG-EG {m.Bus.Sound.Ym.SsgEgEnabledWrites}, CSM {m.Bus.Sound.Ym.CsmModeWrites}");
        if (GenesisRecomp.Machine.Vdp.VerifyRenderer)
            Console.WriteLine($"VDP check: {m.Vdp.FastLines:N0} lines drawn by the fast renderer, {m.Vdp.ReferenceLines:N0} by the reference; mismatches: {m.Vdp.RenderMismatches}" + (m.Vdp.FirstRenderMismatch != null ? $" (first: {m.Vdp.FirstRenderMismatch})" : ""));
        Console.WriteLine($"Ran {m.FrameCount} frames ({m.Cpu.InstructionCount:N0} instructions, {m.Cpu.Cycles:N0} cycles). {stop}");
        Console.WriteLine($"PC=0x{m.Cpu.PC:X6}  SR=0x{m.Cpu.SR:X4}  Stopped={m.Cpu.Stopped}");
        Console.WriteLine($"VDP: {v.Width}x{v.Height}, display {((v.R[1] & 0x40) != 0 ? "on" : "off")}, VBlank interrupts enabled {((v.R[1] & 0x20) != 0)}, " +
                          $"VBlanks {v.VintCount}, HBlank interrupts {v.HintCount}, DMAs {v.DmaCount}");
        Console.WriteLine("VDP registers: " + string.Join(" ", v.R.Take(24).Select((b, i) => $"{i:X2}={b:X2}")));
        Console.WriteLine("Exceptions raised: " + (m.ExceptionCounts.Count == 0 ? "none" : string.Join(", ", m.ExceptionCounts.Select(kv => $"vector {kv.Key} x{kv.Value}"))));
        foreach (string e in m.FirstExceptions.Take(8)) Console.WriteLine("  " + e);
        Console.WriteLine($"Screenshots in {Path.GetFullPath(outDir)}");
        return 0;
    }
}
