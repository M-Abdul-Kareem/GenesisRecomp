using GenesisRecomp.Cpu;

namespace GenesisRecomp.Recomp;

/// <summary>GenesisRecomp --recompile rom.bin [--out file.cs] [--max-seg N]</summary>
public static class RecompCommand
{
    public static int Run(string[] args)
    {
        string romPath = args[1];
        string outPath = RomRecompiler.FindDefaultOutput();
        var opt = new RecompOptions();
        for (int k = 2; k < args.Length - 1; k++)
        {
            if (args[k] == "--out") outPath = args[k + 1];
            if (args[k] == "--max-seg") opt.MaxSegment = Math.Max(1, int.Parse(args[k + 1]));
        }
        if (!File.Exists(romPath)) { Console.WriteLine($"File not found: {romPath}"); return 1; }

        byte[] rom = File.ReadAllBytes(romPath);
        try { RomHeader.Parse(rom); }
        catch (InvalidDataException ex) { Console.WriteLine($"Error: {ex.Message}"); return 1; }
        if (rom.Length > 0x400000)
            Console.WriteLine("Warning: ROM is larger than 4 MB, so it probably uses bank switching. Code in the banked area can't be recompiled statically; it will run in the interpreter.");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        RecompReport r = RomRecompiler.Generate(rom, outPath, opt);
        sw.Stop();

        Console.WriteLine($"Recompiled {Path.GetFileName(romPath)}  (CRC32 {r.Crc:X8})");
        Console.WriteLine($"  code found        : {r.Instructions:N0} instructions, {r.Bytes:N0} bytes ({r.RomCoverage:F1}% of the ROM)");
        Console.WriteLine($"  converted to C#   : {r.Lifted:N0} instructions ({100.0 * r.Lifted / Math.Max(1, r.Instructions):F1}%)");
        Console.WriteLine($"  left to interpreter: {r.Fallback:N0} instructions");
        foreach (var kv in r.FallbackByMnemonic.OrderByDescending(k => k.Value).Take(8))
            Console.WriteLine($"      {kv.Key,-8} {kv.Value,6:N0}");
        Console.WriteLine($"  generated methods : {r.Segments:N0} (max {opt.MaxSegment} instructions each)");
        Console.WriteLine($"  indirect jumps/calls not resolved statically: {r.IndirectSites}  (they dispatch at run time; unknown targets run in the interpreter)");
        Console.WriteLine($"  wrote {r.OutputPath}  ({new FileInfo(r.OutputPath).Length / 1024:N0} KB, {sw.Elapsed.TotalSeconds:F1}s)");
        Console.WriteLine();
        Console.WriteLine("Now rebuild the project. The program then contains this game's recompiled code and uses it whenever it is given this ROM");
        Console.WriteLine("(matched by CRC32). Games recompiled earlier stay in the program; any other ROM still runs, through the interpreter.");
        Console.WriteLine("Add --interp to the command line to compare with the interpreter.");
        return 0;
    }
}
