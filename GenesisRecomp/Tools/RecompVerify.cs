using GenesisRecomp.Cpu;
using GenesisRecomp.Recomp;

namespace GenesisRecomp.Tools;

/// <summary>
/// Proves the recompiled code does exactly what the interpreter does.
/// For every instruction in the ROM, from several random CPU states, it runs the instruction once through the interpreter
/// and once through its generated method, then compares all registers, flags, PC, the cycle count and every memory write.
/// Needs code generated with  --recompile rom.bin --max-seg 1  (one method per instruction), then a rebuild.
///   GenesisRecomp --verify-recomp rom.bin [trials-per-instruction]
/// </summary>
public static class RecompVerify
{
    private sealed class TestBus : IBus
    {
        private readonly byte[] rom;
        private readonly uint seed;
        public readonly Dictionary<uint, byte> Over = new();
        public readonly List<(char Kind, uint Addr, uint Val)> Log = new();

        public TestBus(byte[] rom, uint seed) { this.rom = rom; this.seed = seed; }

        private byte Base(uint a)
        {
            if (a < rom.Length) return rom[a];
            uint h = (a * 2654435761u) ^ seed;
            h ^= h >> 15; h *= 2246822519u; h ^= h >> 13;
            return (byte)h;
        }
        private byte Get(uint a) { a &= 0xFFFFFF; return Over.TryGetValue(a, out byte b) ? b : Base(a); }
        private void Put(uint a, byte v) => Over[a & 0xFFFFFF] = v;

        public byte Read8(uint a) => Get(a);
        public ushort Read16(uint a) => (ushort)((Get(a) << 8) | Get(a + 1));
        public uint Read32(uint a) => ((uint)Read16(a) << 16) | Read16(a + 2);
        public void Write8(uint a, byte v) { Log.Add(('b', a & 0xFFFFFF, v)); Put(a, v); }
        public void Write16(uint a, ushort v) { Log.Add(('w', a & 0xFFFFFF, v)); Put(a, (byte)(v >> 8)); Put(a + 1, (byte)v); }
        public void Write32(uint a, uint v) { Log.Add(('l', a & 0xFFFFFF, v)); Write16w(a, (ushort)(v >> 16)); Write16w(a + 2, (ushort)v); }
        private void Write16w(uint a, ushort v) { Put(a, (byte)(v >> 8)); Put(a + 1, (byte)v); }
    }

    private static uint RandomValue(Random rng)
    {
        switch (rng.Next(8))
        {
            case 0: return (uint)rng.Next(0, 16);
            case 1: return (uint)rng.Next(0, 256);
            case 2: return new uint[] { 0, 0xFFFF, 0xFFFFFFFF, 0x80, 0x8000, 0x80000000, 0x7FFFFFFF, 0xFF, 0xFFFFFF00 }[rng.Next(9)];
            default: return (uint)rng.NextInt64(0, 0x100000000L);
        }
    }

    private static uint RandomAddress(Random rng)
    {
        uint a = (uint)rng.Next(0x1000, 0xFFFFF0);
        return rng.Next(8) == 0 ? a | 1 : a & ~1u;      // mostly even; sometimes odd to exercise address errors
    }

    private static (Cpu68k Cpu, TestBus Bus) MakeCpu(byte[] rom, uint seed, Random state, uint pc)
    {
        var bus = new TestBus(rom, seed);
        var cpu = new Cpu68k(bus);
        cpu.SR = (ushort)(0x2000 | state.Next(0, 32));
        for (int r = 0; r < 8; r++) cpu.D[r] = RandomValue(state);
        for (int r = 0; r < 7; r++) cpu.A[r] = RandomAddress(state);
        cpu.A[7] = (uint)(0xFF8000 + (state.Next(0, 0x3000) & ~1));
        cpu.PC = pc;
        return (cpu, bus);
    }

    public static int Run(string[] args)
    {
        byte[] rom = File.ReadAllBytes(args[1]);
        int trials = args.Length >= 3 ? int.Parse(args[2]) : 8;

        bool live = args[0] == "--verify-live";
        uint crc = RomRecompiler.Crc32(rom);
        LiveRecompiler? liveRec = null;
        if (live)
        {
            liveRec = LiveRecompiler.Create(rom, 1);          // one piece of machine code per instruction
        }
        else
        {
            if (!Cpu68k.RecompiledGames.TryGetValue(crc, out var game))
            {
                Console.WriteLine("This program does not contain recompiled code for this ROM. Run  --recompile <rom> --max-seg 1  and rebuild first.");
                return 2;
            }
            if (game.MaxSegment != 1)
            {
                Console.WriteLine($"The generated code was made with max-seg {game.MaxSegment}. Regenerate with  --recompile <rom> --max-seg 1  and rebuild.");
                return 2;
            }
        }
        Console.WriteLine(live ? "Checking the in-memory recompiler (the code a downloaded exe makes when a game is opened)..."
                               : "Checking the code generated into this program (Generated\\Rom_*.cs)...");

        BlockGraph g = RomRecompiler.Analyze(rom);
        int tested = 0, runs = 0, fallbacks = 0, bad = 0, thrown = 0;
        var perMn = new Dictionary<Mn, (int Runs, int Bad)>();

        foreach (var (addr, ins) in g.Instructions)
        {
            bool lifted = Lifter_TryLift(ins);
            if (!lifted) fallbacks++;
            tested++;
            for (int t = 0; t < trials; t++)
            {
                uint seed = addr * 16777619u + (uint)t;
                var stateA = new Random((int)seed);
                var stateB = new Random((int)seed);
                var (interp, busI) = MakeCpu(rom, seed, stateA, addr);
                var (gen, busG) = MakeCpu(rom, seed, stateB, addr);
                if (live) gen.SelectLive(liveRec!); else gen.SelectRecompiled(crc);

                string? exI = null, exG = null;
                try { interp.InterpretOne(); } catch (AddressErrorException e) { exI = $"AE {e.Address:X}"; }
                bool had = true;
                try { had = gen.RunGenerated(); } catch (AddressErrorException e) { exG = $"AE {e.Address:X}"; }

                runs++;
                string? diff = null;
                if (!had) diff = "no generated method for this address";
                else if (exI != exG) diff = $"exception differs: interpreter={exI ?? "none"} generated={exG ?? "none"}";
                else
                {
                    if (exI != null) thrown++;
                    diff = Compare(interp, gen, busI, busG, compareTiming: exI == null);
                }

                var cur = perMn.GetValueOrDefault(ins.Op);
                perMn[ins.Op] = (cur.Runs + 1, cur.Bad + (diff != null ? 1 : 0));
                if (diff != null)
                {
                    bad++;
                    if (bad <= 15) Console.WriteLine($"MISMATCH {addr:X6}  {ins.Text}   trial {t}: {diff}");
                    break;
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Instructions tested : {tested:N0}  ({tested - fallbacks:N0} recompiled, {fallbacks:N0} interpreter fallbacks)");
        Console.WriteLine($"Runs compared       : {runs:N0}  ({thrown:N0} ended in an address error in both)");
        Console.WriteLine($"Mismatches          : {bad:N0}");
        if (live && liveRec!.FailedCount > 0) Console.WriteLine($"In-memory build failures: {liveRec.FailedCount}  first: {liveRec.FirstFailure}");
        if (bad == 0) Console.WriteLine("ALL RECOMPILED INSTRUCTIONS MATCH THE INTERPRETER");
        else foreach (var kv in perMn.Where(k => k.Value.Bad > 0)) Console.WriteLine($"   {kv.Key}: {kv.Value.Bad} bad instructions");
        return bad == 0 ? 0 : 1;
    }

    private static bool Lifter_TryLift(DecodedInst ins)
    {
        // The Lifter is internal to the Recomp namespace; this goes through the public generator's rules via a tiny shim.
        return RecompShim.CanLift(ins);
    }

    private static string? Compare(Cpu68k a, Cpu68k b, TestBus ba, TestBus bb, bool compareTiming)
    {
        for (int r = 0; r < 8; r++)
        {
            if (a.D[r] != b.D[r]) return $"D{r}: interpreter {a.D[r]:X8}, generated {b.D[r]:X8}";
            if (a.A[r] != b.A[r]) return $"A{r}: interpreter {a.A[r]:X8}, generated {b.A[r]:X8}";
        }
        if (a.SR != b.SR) return $"SR: interpreter {a.SR:X4}, generated {b.SR:X4}";
        if (ba.Log.Count != bb.Log.Count) return $"memory writes: interpreter {ba.Log.Count}, generated {bb.Log.Count}";
        for (int k = 0; k < ba.Log.Count; k++)
            if (ba.Log[k] != bb.Log[k])
                return $"write #{k}: interpreter {ba.Log[k].Kind} {ba.Log[k].Addr:X6}={ba.Log[k].Val:X}, generated {bb.Log[k].Kind} {bb.Log[k].Addr:X6}={bb.Log[k].Val:X}";
        if (!compareTiming) return null;
        if (a.PC != b.PC) return $"PC: interpreter {a.PC:X6}, generated {b.PC:X6}";
        if (a.Cycles != b.Cycles) return $"cycles: interpreter {a.Cycles}, generated {b.Cycles}";
        if (a.Stopped != b.Stopped) return "Stopped flag differs";
        if (a.LastExceptionVector != b.LastExceptionVector) return $"exception vector: interpreter {a.LastExceptionVector}, generated {b.LastExceptionVector}";
        return null;
    }
}
