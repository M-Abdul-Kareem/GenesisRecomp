using GenesisRecomp.Cpu;

namespace GenesisRecomp.Recomp;

/// <summary>
/// Recompiles a game's 68000 code in memory when the game is opened: no compiler, no Visual Studio, no files.
/// Opening a game finds all the code (a fraction of a second), cuts it into segments, and from then on each segment is
/// turned into machine code the first time the game reaches it (see <see cref="ExprLifter"/>).
/// Anything it can't handle (code running from RAM, unusual instructions, unknown jump targets) goes to the interpreter.
/// </summary>
public sealed class LiveRecompiler
{
    private readonly List<RomRecompiler.Segment> segs;
    private readonly int[] segIndex;                 // by address/2: segment number + 1, or 0 = not a segment start
    private readonly Action<Cpu68k>?[] compiled;

    public RecompReport Report { get; }
    public int SegmentCount => segs.Count;
    public int CompiledCount { get; private set; }   // (updated under a lock; read for display only)
    public int FailedCount { get; private set; }
    public string? FirstFailure { get; private set; }
    /// <summary>Time spent finding the code when the game was opened (the machine code itself is made later, piece by piece).</summary>
    public double SetupMs { get; }
    /// <summary>Total time spent turning pieces into machine code so far.</summary>
    public double CompileMs { get; private set; }

    private LiveRecompiler(byte[] rom, int maxSegment)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Report = new RecompReport { Crc = RomRecompiler.Crc32(rom) };
        BlockGraph g = RomRecompiler.Analyze(rom);
        segs = RomRecompiler.Segmentize(g, new RecompOptions { MaxSegment = maxSegment }, Report);
        Report.Segments = segs.Count;
        segIndex = new int[(rom.Length >> 1) + 1];
        compiled = new Action<Cpu68k>?[segIndex.Length];
        for (int k = 0; k < segs.Count; k++)
        {
            uint i = segs[k].Start >> 1;
            if (i < segIndex.Length) segIndex[i] = k + 1;
        }
        SetupMs = sw.Elapsed.TotalMilliseconds;
    }

    /// <param name="maxSegment">Longest run of instructions per piece of machine code (1 = one per instruction, used by --verify-live).</param>
    public static LiveRecompiler Create(byte[] rom, int maxSegment = 16) => new(rom, maxSegment);

    private readonly object statLock = new();

    /// <summary>Turn the piece that starts at (address/2) <paramref name="i"/> into machine code. Null if it can't be (the interpreter covers it).</summary>
    private Action<Cpu68k>? BuildPiece(uint i)
    {
        int s = segIndex[i];
        if (s == 0) return null;
        RomRecompiler.Segment seg = segs[s - 1];
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            var lifted = new bool[seg.Code.Count];
            for (int k = 0; k < lifted.Length; k++) lifted[k] = seg.Code[k] != null;
            Action<Cpu68k> act = ExprLifter.Build(seg.Insts, lifted);
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            lock (statLock) { CompiledCount++; CompileMs += ms; }
            compiled[i] = act;
            return act;
        }
        catch (Exception ex)
        {
            lock (statLock) { FailedCount++; FirstFailure ??= $"{seg.Start:X6}: {ex.Message}"; }
            segIndex[i] = 0;                           // never try again; the interpreter covers it
            return null;
        }
    }

    /// <summary>
    /// Builds the pieces the game has not reached yet on a low-priority background thread, so that reaching new parts of the game
    /// (a new level, say) never has to wait for machine code to be made. Safe alongside the game: a piece built twice is just built twice.
    /// </summary>
    public void StartWarmup()
    {
        var t = new Thread(() =>
        {
            for (int k = 0; k < segs.Count; k++)
            {
                uint i = segs[k].Start >> 1;
                if (i >= (uint)compiled.Length || compiled[i] != null || segIndex[i] == 0) continue;
                BuildPiece(i);
            }
        }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "recompiler warm-up" };
        t.Start();
    }

    /// <summary>Run the segment that starts at <paramref name="pc"/>. False if there is none (the caller interprets one instruction).</summary>
    public bool Run(Cpu68k cpu, uint pc)
    {
        if ((pc & 1) != 0) return false;
        uint i = pc >> 1;
        if (i >= (uint)compiled.Length) return false;
        Action<Cpu68k>? act = compiled[i] ?? BuildPiece(i);
        if (act == null) return false;
        act(cpu);
        return true;
    }
}
