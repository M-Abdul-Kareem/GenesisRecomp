namespace GenesisRecomp.Cpu;

/// <summary>
/// Recursive-traversal disassembly: starting from known entry points, follow the code
/// wherever it can go (branches, jumps, calls, fall-through). Bytes never reached this
/// way are treated as data, which avoids decoding graphics/music as nonsense instructions.
/// </summary>
public sealed class CodeMap
{
    public SortedDictionary<uint, Instruction> Instructions { get; } = new();
    public HashSet<uint> Labels { get; } = new();
    public List<uint> IndirectSites { get; } = new();    // JMP/JSR whose target is not static
    public List<uint> BadAddresses { get; } = new();     // reached addresses that did not decode
    public HashSet<uint> ExternalTargets { get; } = new(); // jump/call targets outside the ROM (RAM, etc.)

    public int BytesCovered { get; private set; }

    public static CodeMap Build(byte[] rom, IEnumerable<uint> roots)
    {
        var map = new CodeMap();
        var work = new Stack<uint>();
        foreach (uint r in roots)
        {
            map.Labels.Add(r);
            work.Push(r);
        }

        while (work.Count > 0)
        {
            uint addr = work.Pop();
            while (true)
            {
                if (map.Instructions.ContainsKey(addr)) break;
                if (addr >= rom.Length || (addr & 1) != 0) { map.ExternalTargets.Add(addr); break; }

                Instruction ins = Decoder.Decode(rom, addr);
                if (!ins.IsValid) { map.BadAddresses.Add(addr); break; }

                map.Instructions[addr] = ins;
                map.BytesCovered += ins.Length;

                if (ins.Target is uint t && (ins.Flow is FlowKind.Jump or FlowKind.Branch or FlowKind.Call))
                {
                    map.Labels.Add(t);
                    work.Push(t);
                }

                if (ins.Flow is FlowKind.IndirectJump or FlowKind.IndirectCall)
                    map.IndirectSites.Add(addr);

                if (ins.Flow is FlowKind.Jump or FlowKind.Return or FlowKind.Halt or FlowKind.IndirectJump)
                    break;                              // execution does not fall through
                addr = ins.NextAddress;
            }
        }
        return map;
    }
}
