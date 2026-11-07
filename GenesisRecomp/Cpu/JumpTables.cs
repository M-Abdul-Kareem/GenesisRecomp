using System.Text.RegularExpressions;

namespace GenesisRecomp.Cpu;

public enum TableKind
{
    BranchTable,    // jump lands in a run of BRA instructions
    OffsetTable,    // 16-bit offsets from the table base: target = base + offset
    AbsWordTable,   // 16-bit absolute addresses (code in the first 64 KB): MOVE.W tbl,Dn / MOVEA.L Dn,An / JMP (An)
    PointerTable    // 32-bit absolute addresses:                            MOVE.L tbl,Dn / MOVEA.L Dn,An / JSR (An)
}

public sealed class JumpTable
{
    public uint Site { get; init; }                         // address of the JMP / JSR
    public uint Base { get; init; }                         // first table entry
    public TableKind Kind { get; init; }
    public List<uint> EntryAddresses { get; } = new();      // address of each entry
    public List<uint> Targets { get; } = new();             // where each entry leads
}

/// <summary>
/// Resolves the indirect jumps that plain code-following cannot: dispatch tables.
/// Table sizes are inferred from the data itself (where the run of valid entries stops).
/// </summary>
public static class JumpTables
{
    private const int MaxEntries = 256;
    private static readonly Regex RegIndexedZero = new(@"^-?\$[0-9A-F]+\((A\d),[DA]\d\.[WL]\)(,D\d)?$", RegexOptions.Compiled);
    private static readonly Regex RegIndirect = new(@"^\((A\d)\)$", RegexOptions.Compiled);

    public static void Resolve(byte[] rom, CodeMap map, out List<JumpTable> found, out List<(uint Site, string Reason)> unresolved)
    {
        found = new List<JumpTable>();
        unresolved = new List<(uint, string)>();

        // A branch table ends where another table begins (tables are often stored back to back).
        var otherBases = new HashSet<uint>();
        foreach (var ins in map.Instructions.Values)
        {
            if (ins.PcIndexedBase is uint b) otherBases.Add(b);
            if (ins.Mnemonic == "LEA" && ins.PcRelAddress is uint lb) otherBases.Add(lb);
        }

        var byNext = new Dictionary<uint, Instruction>();
        foreach (var ins in map.Instructions.Values) byNext[ins.NextAddress] = ins;

        foreach (uint site in map.IndirectSites)
        {
            JumpTable? t = TryResolve(rom, map.Instructions[site], byNext, otherBases);
            if (t != null && t.EntryAddresses.Count > 0) found.Add(t);
            else unresolved.Add((site, map.Instructions[site].Operands.StartsWith("(A") || map.Instructions[site].Operands.StartsWith("$0(")
                ? "target comes from a register set elsewhere (callback / runtime value)"
                : "table found but its entries are not valid code"));
        }
    }

    private static JumpTable? TryResolve(byte[] rom, Instruction jump,
        Dictionary<uint, Instruction> byNext, HashSet<uint> otherBases)
    {
        uint site = jump.Address;

        // 1) JMP/JSR tbl(PC,Dn): lands in a run of BRAs, or reads a word offset
        if (jump.PcIndexedBase is uint pcBase)
        {
            var t = ReadBranchTable(rom, site, pcBase, otherBases);
            return t.EntryAddresses.Count > 0 ? t : ReadOffsetTable(rom, site, pcBase);
        }

        // 2) JMP/JSR 0(An,Dn) where An was loaded with LEA tbl(PC),An: word offsets from that base
        var m = RegIndexedZero.Match(jump.Operands);
        if (m.Success && FindLeaBase(jump, m.Groups[1].Value, byNext) is uint leaBase)
            return ReadOffsetTable(rom, site, leaBase);

        // 2b) JMP/JSR (An) after:  LEA tbl(PC),An / MOVE.W d(An,Dx),Dm / ADDA.L Dm,An  -> word offsets from tbl
        var ind = RegIndirect.Match(jump.Operands);
        if (ind.Success
            && byNext.TryGetValue(jump.Address, out Instruction? adda)
            && adda.Mnemonic == "ADDA.L" && adda.Operands.EndsWith("," + ind.Groups[1].Value)
            && byNext.TryGetValue(adda.Address, out Instruction? offLoad)
            && offLoad.Mnemonic == "MOVE.W" && offLoad.Operands.EndsWith("," + adda.Operands.Split(',')[0]))
        {
            var om = RegIndexedZero.Match(offLoad.Operands);
            if (om.Success && FindLeaBase(offLoad, om.Groups[1].Value, byNext) is uint ob)
                return ReadOffsetTable(rom, site, ob);
        }

        // 3) JMP/JSR (An) after:  MOVE.{W,L} <table entry>,Dm  /  MOVEA.L Dm,An
        m = RegIndirect.Match(jump.Operands);
        if (m.Success
            && byNext.TryGetValue(jump.Address, out Instruction? movea)
            && movea.Mnemonic is "MOVEA.L" or "MOVE.L" && movea.Operands.EndsWith("," + m.Groups[1].Value)
            && byNext.TryGetValue(movea.Address, out Instruction? load)
            && load.Mnemonic is "MOVE.L" or "MOVE.W" && load.Operands.EndsWith("," + movea.Operands.Split(',')[0]))
        {
            uint? tableBase = load.PcIndexedBase;
            if (tableBase is null)
            {
                var lm = RegIndexedZero.Match(load.Operands);
                if (lm.Success) tableBase = FindLeaBase(load, lm.Groups[1].Value, byNext);
            }
            if (tableBase is uint tb)
                return load.Mnemonic == "MOVE.L"
                    ? ReadAddressTable(rom, site, tb, TableKind.PointerTable, otherBases)
                    : ReadAddressTable(rom, site, tb, TableKind.AbsWordTable, otherBases);
        }
        return null;
    }

    /// <summary>Looks back through straight-line predecessors for  LEA tbl(PC),An.</summary>
    private static uint? FindLeaBase(Instruction from, string reg, Dictionary<uint, Instruction> byNext)
    {
        Instruction cur = from;
        for (int i = 0; i < 10; i++)
        {
            if (!byNext.TryGetValue(cur.Address, out Instruction? prev)) return null;
            if (prev.Mnemonic == "LEA" && prev.Operands.EndsWith("," + reg) && prev.PcRelAddress is uint a) return a;
            cur = prev;
        }
        return null;
    }

    private static JumpTable ReadBranchTable(byte[] rom, uint site, uint baseAddr, HashSet<uint> otherBases)
    {
        var t = new JumpTable { Site = site, Base = baseAddr, Kind = TableKind.BranchTable };
        uint addr = baseAddr;
        while (t.EntryAddresses.Count < MaxEntries)
        {
            if (addr != baseAddr && otherBases.Contains(addr)) break;
            Instruction e = Decoder.Decode(rom, addr);
            if (!e.IsValid || !e.Mnemonic.StartsWith("BRA") || e.Target is null) break;
            t.EntryAddresses.Add(addr);
            t.Targets.Add(e.Target.Value);
            addr = e.NextAddress;
        }
        return t;
    }

    /// <summary>
    /// Word-offset table: target = base + offset. The code starts right after the table, so the
    /// smallest offset seen so far tells us where the table must end.
    /// </summary>
    private static JumpTable ReadOffsetTable(byte[] rom, uint site, uint baseAddr)
    {
        var t = new JumpTable { Site = site, Base = baseAddr, Kind = TableKind.OffsetTable };
        uint minOffset = uint.MaxValue;
        for (uint i = 0; i < MaxEntries; i++)
        {
            uint entryAddr = baseAddr + 2 * i;
            if (2 * i >= minOffset || entryAddr + 1 >= rom.Length) break;
            ushort off = RomHeader.ReadU16(rom, (int)entryAddr);
            uint target = baseAddr + off;
            if (off >= 0x8000 || (off & 1) != 0 || target >= rom.Length || !Decoder.Decode(rom, target).IsValid)
            {
                t.EntryAddresses.Clear();      // not an offset table after all
                t.Targets.Clear();
                break;
            }
            minOffset = Math.Min(minOffset, off);
            t.EntryAddresses.Add(entryAddr);
            t.Targets.Add(target);
        }
        return t;
    }

    /// <summary>Table of absolute addresses (16-bit or 32-bit). Ends at the first entry that is not valid code.</summary>
    private static JumpTable ReadAddressTable(byte[] rom, uint site, uint baseAddr, TableKind kind, HashSet<uint> otherBases)
    {
        var t = new JumpTable { Site = site, Base = baseAddr, Kind = kind };
        int width = kind == TableKind.PointerTable ? 4 : 2;
        uint addr = baseAddr;
        uint minTarget = uint.MaxValue;
        for (int i = 0; i < MaxEntries && addr + (uint)width <= rom.Length; i++)
        {
            if (addr >= minTarget) break;                                  // code starts here
            if (i > 0 && otherBases.Contains(addr)) break;                  // another table starts here
            uint v = width == 4 ? RomHeader.ReadU32(rom, (int)addr) : RomHeader.ReadU16(rom, (int)addr);
            bool valid = v >= 0x200 && v < rom.Length && (v & 1) == 0 && Decoder.Decode(rom, v).IsValid;
            if (!valid)
            {
                if (i == 0 && kind == TableKind.AbsWordTable) { addr += (uint)width; continue; }       // dummy first entry
                break;
            }
            t.EntryAddresses.Add(addr);
            t.Targets.Add(v);
            if (v > addr) minTarget = Math.Min(minTarget, v);
            addr += (uint)width;
        }
        return t;
    }
}
