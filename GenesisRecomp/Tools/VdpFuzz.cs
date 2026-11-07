using GenesisRecomp.Machine;

namespace GenesisRecomp.Tools;

/// <summary>
/// Stress test for the fast video renderer: random video memory, colours, scroll values and register settings
/// (window plane, plane sizes, scroll modes, 32/40-cell width, 224/240 lines, sprites...),
/// every line drawn by both renderers and compared.   GenesisRecomp --verify-vdp-fuzz [iterations]
/// </summary>
public static class VdpFuzz
{
    public static int Run(string[] args)
    {
        int iterations = args.Length >= 2 ? int.Parse(args[1]) : 300;
        Vdp.VerifyRenderer = true;
        var rng = new Random(12345);
        var vdp = new Vdp(_ => 0);
        long lines = 0;
        for (int it = 0; it < iterations; it++)
        {
            int density = rng.Next(1, 5);                                 // 1 = mostly empty tiles ... 4 = mostly random
            for (int i = 0; i < vdp.Vram.Length; i++)
                vdp.Vram[i] = rng.Next(density) == 0 ? (byte)rng.Next(256) : (byte)0;
            for (int i = 0; i < 64; i++) { vdp.Cram[i] = (ushort)rng.Next(0x1000); vdp.Vsram[i] = (ushort)rng.Next(0x400); }
            for (int i = 0; i < 24; i++) vdp.R[i] = (byte)rng.Next(256);
            vdp.R[1] |= 0x40;                                             // display on (the display-off path is trivial)
            if (rng.Next(5) != 0) vdp.R[11] &= 0xFB;                      // mostly no column scroll: that is the path under test
            if (rng.Next(3) == 0) { vdp.R[17] = 0; vdp.R[18] = 0; }       // window off about a third of the time
            for (int l = 0; l < 262; l++) { vdp.StartLine(l); lines++; }
            if (vdp.RenderMismatches > 0) break;
        }
        Console.WriteLine($"Fuzz: {iterations} random VDP setups, {vdp.FastLines:N0} lines by the fast renderer, {vdp.ReferenceLines:N0} by the reference");
        Console.WriteLine(vdp.RenderMismatches == 0
            ? "Mismatches: 0 - FAST RENDERER MATCHES THE REFERENCE"
            : $"Mismatches: {vdp.RenderMismatches}  first: {vdp.FirstRenderMismatch}");
        return vdp.RenderMismatches == 0 ? 0 : 1;
    }
}
