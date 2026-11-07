namespace GenesisRecomp.Cpu;

/// <summary>A straight-line run of instructions: entered only at the top, left only at the bottom.</summary>
public sealed class BasicBlock
{
    public uint Start { get; init; }
    public List<DecodedInst> Insts { get; } = new();

    public DecodedInst Last => Insts[^1];
    /// <summary>First address after the block (exclusive).</summary>
    public uint End => Last.NextAddress;

    /// <summary>True if execution can continue at <see cref="End"/> (plain fall-through, a not-taken branch, or a call that returns).</summary>
    public bool FallsThrough => Last.Flow is FlowKind.Normal or FlowKind.Branch or FlowKind.Call or FlowKind.IndirectCall;

    /// <summary>Static jump/branch destination, if the block ends in one.</summary>
    public uint? BranchTarget => Last.Flow is FlowKind.Jump or FlowKind.Branch ? Last.Target : null;

    public override string ToString() => $"block {Start:X6}-{End:X6} ({Insts.Count} instr, ends {Last.Flow})";
}

/// <summary>
/// The recompiler's view of the program: every reachable instruction, grouped into basic blocks,
/// plus the set of function entry points (vector targets and call destinations).
/// Built by the same recursive traversal as <see cref="CodeMap"/>, but on structured instructions.
/// </summary>
public sealed class BlockGraph
{
    public SortedDictionary<uint, DecodedInst> Instructions { get; } = new();
    public SortedDictionary<uint, BasicBlock> Blocks { get; } = new();
    public HashSet<uint> Roots { get; } = new();
    public HashSet<uint> FunctionEntries { get; } = new();
    public List<uint> IndirectSites { get; } = new();
    public List<uint> BadAddresses { get; } = new();
    /// <summary>Jump/call targets that are not in the ROM (RAM code and the like) - these must run in the interpreter.</summary>
    public HashSet<uint> ExternalTargets { get; } = new();

    public int BytesCovered { get; private set; }

    /// <param name="roots">Known entry points (reset vector, interrupt vectors, resolved jump-table targets...).</param>
    public static BlockGraph Build(byte[] rom, IEnumerable<uint> roots)
    {
        var g = new BlockGraph();
        var leaders = new HashSet<uint>();
        var work = new Stack<uint>();

        foreach (uint r in roots)
        {
            g.Roots.Add(r);
            g.FunctionEntries.Add(r);
            leaders.Add(r);
            work.Push(r);
        }

        // 1. Recursive traversal: find every reachable instruction.
        while (work.Count > 0)
        {
            uint addr = work.Pop();
            while (true)
            {
                if (g.Instructions.ContainsKey(addr)) break;
                if (addr >= rom.Length || (addr & 1) != 0) { g.ExternalTargets.Add(addr); break; }

                DecodedInst ins = StructDecoder.Decode(rom, addr);
                if (!ins.IsValid) { g.BadAddresses.Add(addr); break; }

                g.Instructions[addr] = ins;
                g.BytesCovered += ins.Length;

                if (ins.Target is uint t && ins.Flow is FlowKind.Jump or FlowKind.Branch or FlowKind.Call)
                {
                    leaders.Add(t);
                    if (ins.Flow == FlowKind.Call) g.FunctionEntries.Add(t);
                    work.Push(t);
                }
                if (ins.Flow == FlowKind.Branch) leaders.Add(ins.NextAddress);   // the not-taken path starts a block

                if (ins.Flow is FlowKind.IndirectJump or FlowKind.IndirectCall)
                    g.IndirectSites.Add(addr);

                if (!ins.FallsThrough()) break;          // a conditional branch ends the block but execution can still fall through
                addr = ins.NextAddress;
            }
        }

        // 2. Split the instruction stream into blocks.
        BasicBlock? cur = null;
        foreach (var (addr, ins) in g.Instructions)
        {
            bool startNew = cur == null
                            || leaders.Contains(addr)
                            || cur.End != addr
                            || EndsBlock(cur.Last.Flow);
            if (startNew)
            {
                cur = new BasicBlock { Start = addr };
                g.Blocks[addr] = cur;
            }
            cur!.Insts.Add(ins);
        }
        return g;
    }

    /// <summary>Does this kind of instruction end a basic block? (Conditional branches do, even though execution may fall through.)</summary>
    public static bool EndsBlock(FlowKind f) => f is FlowKind.Jump or FlowKind.Branch or FlowKind.Return or FlowKind.Halt or FlowKind.IndirectJump;

    /// <summary>Blocks that can run directly after the given one (statically known successors only).</summary>
    public IEnumerable<BasicBlock> Successors(BasicBlock b)
    {
        if (b.BranchTarget is uint t && Blocks.TryGetValue(t, out var tb)) yield return tb;
        if (b.FallsThrough && Blocks.TryGetValue(b.End, out var fb)) yield return fb;
    }
}

public static class DecodedInstExtensions
{
    /// <summary>Can execution continue at the next instruction? (False for unconditional jumps, returns, halts, indirect jumps.)</summary>
    public static bool FallsThrough(this DecodedInst i) =>
        i.Flow is not (FlowKind.Jump or FlowKind.Return or FlowKind.Halt or FlowKind.IndirectJump);
}
