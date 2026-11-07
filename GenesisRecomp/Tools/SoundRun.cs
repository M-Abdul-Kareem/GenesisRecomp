using GenesisRecomp.Machine;

namespace GenesisRecomp.Tools;

/// <summary>
/// Runs a ROM without a window and records its sound.
/// Usage: GenesisRecomp --run-rom game.bin [frames=300] [out.wav]
/// Prints the state of the Z80 and the first bytes of Z80 RAM, which is handy for checking that a game's sound driver started.
/// </summary>
public static class SoundRun
{
    public static int Run(string romPath, int frames, string? wavPath)
    {
        byte[] rom = File.ReadAllBytes(romPath);
        var m = new GenesisMachine(rom);
        var snd = m.Bus.Sound;
        snd.Capture = wavPath != null;
        snd.OutputRate = 48000;
        var pcm = new List<short>();

        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (int f = 0; f < frames; f++)
        {
            m.RunFrame();
            if (wavPath != null)
            {
                var (buf, n) = snd.Collect();
                for (int i = 0; i < n * 2; i++) pcm.Add(buf[i]);
            }
        }
        watch.Stop();

        double cpuSec = m.CpuLoopTicks / (double)System.Diagnostics.Stopwatch.Frequency;
        Console.WriteLine($"CPU: {(m.UsingRecompiledCode ? "recompiled code" : "interpreter")}; 68000+Z80 loop took {cpuSec:F2}s of {watch.Elapsed.TotalSeconds:F2}s ({100 * cpuSec / watch.Elapsed.TotalSeconds:F0}%)");
        double F = System.Diagnostics.Stopwatch.Frequency;
        Console.WriteLine($"video rendering {m.Vdp.RenderTicks / F:F2}s, sound synthesis {snd.SyncTicks / F:F2}s (part of it inside the CPU loop)");
        var z = m.Bus.Z80;
        Console.WriteLine($"Ran {frames} frames in {watch.Elapsed.TotalSeconds:F2}s ({frames / watch.Elapsed.TotalSeconds:F0} fps, 60 needed for real time)");
        Console.WriteLine($"Z80: PC={z.PC:X4} SP={z.SP:X4} IFF1={z.IFF1} IM={z.IM} halted={z.Halted} runnable={m.Bus.Z80Runnable}");
        Console.WriteLine("Z80 RAM 1FF0: " + string.Join(" ", Enumerable.Range(0, 8).Select(i => m.Bus.Z80Ram[0x1FF0 + i].ToString("X2"))));
        Console.WriteLine($"YM2612 sounding: {snd.Ym.AnyActive()}");
        if (m.ExceptionCounts.Count > 0)
            Console.WriteLine("68000 exceptions: " + string.Join(", ", m.ExceptionCounts.Select(kv => $"vector {kv.Key} x{kv.Value}")));

        if (wavPath != null)
        {
            using var w = new BinaryWriter(File.Create(wavPath));
            int bytes = pcm.Count * 2;
            w.Write("RIFF"u8.ToArray()); w.Write(36 + bytes); w.Write("WAVE"u8.ToArray());
            w.Write("fmt "u8.ToArray()); w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(48000); w.Write(48000 * 4); w.Write((short)4); w.Write((short)16);
            w.Write("data"u8.ToArray()); w.Write(bytes);
            foreach (short s in pcm) w.Write(s);
            Console.WriteLine($"Wrote {wavPath} ({pcm.Count / 2 / 48000.0:F2} s of audio)");
        }
        return 0;
    }
}
