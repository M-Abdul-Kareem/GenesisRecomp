using System.Text.RegularExpressions;
using GenesisRecomp.Cpu;

namespace GenesisRecomp.Tools;

/// <summary>
/// Proves that the structured decoder (StructDecoder) behaves exactly like the original text decoder (Decoder).
///   1. Exhaustive: every one of the 65,536 opcode words, each followed by many different extension-word patterns,
///      at several addresses and with truncated ROMs. Length, flow, targets, validity and the formatted text must all match.
///   2. Listing replay: rebuilds code bytes from a listing file (address, hex words, text) and checks that the new
///      decoder reproduces every line.
/// Usage: GenesisRecomp --verify-decoder [listing_reference.txt]
/// </summary>
public static class DecoderVerify
{
    private static readonly Regex LineRx = new(@"^([0-9A-F]{8})  ", RegexOptions.Compiled);

    public static int Run(string? listingPath)
    {
        int failures = 0;
        failures += Exhaustive();
        if (listingPath != null)
        {
            failures += ListingReplay(listingPath, out byte[]? rom, out List<uint> labels);
            if (rom != null) failures += BlockChecks(rom, labels);
        }
        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL DECODER CHECKS PASSED" : $"DECODER CHECKS FAILED: {failures} mismatches");
        return failures == 0 ? 0 : 1;
    }

    private static string? Diff(Instruction o, DecodedInst n)
    {
        if (o.IsValid != n.IsValid) return $"valid {o.IsValid} vs {n.IsValid}";
        if (o.Length != n.Length) return $"length {o.Length} vs {n.Length}";
        if (o.Flow != n.Flow) return $"flow {o.Flow} vs {n.Flow}";
        if (o.Target != n.Target) return $"target {o.Target:X} vs {n.Target:X}";
        if (o.PcIndexedBase != n.PcIndexedBase) return $"pcIdx {o.PcIndexedBase:X} vs {n.PcIndexedBase:X}";
        if (o.PcRelAddress != n.PcRelAddress) return $"pcRel {o.PcRelAddress:X} vs {n.PcRelAddress:X}";
        if (o.Text != n.Text) return $"text '{o.Text}' vs '{n.Text}'";
        return null;
    }

    private static int Exhaustive()
    {
        Console.WriteLine("1. Exhaustive comparison, old decoder vs structured decoder");
        const uint Addr = 0x1000;
        // Extension-word patterns: edge values and pseudo-random ones.
        var patterns = new List<ushort[]>
        {
            new ushort[] { 0, 0, 0, 0, 0 },
            new ushort[] { 0xFFFF, 0xFFFF, 0xFFFF, 0xFFFF, 0xFFFF },
            new ushort[] { 0x0000, 0x0000, 0x0000, 0x0000, 0x0000 },
            new ushort[] { 0x8000, 0x7FFF, 0x8000, 0x7FFF, 0x8000 },
            new ushort[] { 0x1234, 0x5678, 0x9ABC, 0xDEF0, 0x1357 },
            new ushort[] { 0x0100, 0x0100, 0x0100, 0x0100, 0x0100 },   // 68020 index-extension bit
            new ushort[] { 0xF8FF, 0x08FF, 0x8800, 0x00FF, 0xFF00 },
        };
        var rng = new Random(20260504);
        for (int k = 0; k < 24; k++)
        {
            var p = new ushort[5];
            for (int i = 0; i < 5; i++) p[i] = (ushort)rng.Next(0x10000);
            patterns.Add(p);
        }

        long compared = 0, mismatches = 0, valid = 0;
        int shown = 0;
        var rom = new byte[0x2000];

        foreach (var pat in patterns)
        {
            for (int op = 0; op < 0x10000; op++)
            {
                Array.Clear(rom, (int)Addr, 16);
                rom[Addr] = (byte)(op >> 8); rom[Addr + 1] = (byte)op;
                for (int i = 0; i < 5; i++)
                {
                    rom[Addr + 2 + i * 2] = (byte)(pat[i] >> 8);
                    rom[Addr + 3 + i * 2] = (byte)pat[i];
                }
                Instruction o = Decoder.Decode(rom, Addr);
                DecodedInst n = StructDecoder.Decode(rom, Addr);
                compared++;
                if (o.IsValid) valid++;
                string? d = Diff(o, n);
                if (d != null)
                {
                    mismatches++;
                    if (shown++ < 15) Console.WriteLine($"   MISMATCH op={op:X4} pat={pat[0]:X4},{pat[1]:X4}: {d}");
                }
            }
        }

        // Truncated ROMs: the code ends partway through an instruction.
        for (int truncate = 2; truncate <= 12; truncate += 2)
        {
            var small = new byte[truncate];
            for (int op = 0; op < 0x10000; op++)
            {
                small[0] = (byte)(op >> 8); small[1] = (byte)op;
                for (int i = 2; i < truncate; i++) small[i] = (byte)(0x30 + i);
                Instruction o = Decoder.Decode(small, 0);
                DecodedInst n = StructDecoder.Decode(small, 0);
                compared++;
                string? d = Diff(o, n);
                if (d != null)
                {
                    mismatches++;
                    if (shown++ < 15) Console.WriteLine($"   MISMATCH (rom len {truncate}) op={op:X4}: {d}");
                }
            }
        }

        // Odd addresses must be rejected identically.
        {
            Instruction o = Decoder.Decode(rom, Addr + 1);
            DecodedInst n = StructDecoder.Decode(rom, Addr + 1);
            compared++;
            if (Diff(o, n) != null) mismatches++;
        }

        Console.WriteLine($"   {compared:N0} decodes compared ({valid:N0} valid instructions), {mismatches} mismatches");
        return (int)mismatches;
    }

    private static int ListingReplay(string path, out byte[]? romOut, out List<uint> labels)
    {
        Console.WriteLine($"2. Listing replay: {path}");
        romOut = null;
        labels = new List<uint>();
        if (!File.Exists(path)) { Console.WriteLine("   file not found"); return 1; }

        var entries = new List<(uint Addr, byte[] Bytes, string Text)>();
        uint max = 0;
        bool pendingLabel = false;
        foreach (string line in File.ReadLines(path))
        {
            if (!LineRx.IsMatch(line) || line.Length < 36)
            {
                // A label line ("Reset:" / "L_00020E:") labels the instruction that follows it.
                if (line.EndsWith(':')) pendingLabel = true;
                continue;
            }
            uint addr = Convert.ToUInt32(line.Substring(0, 8), 16);
            string wordsField = line.Substring(10, 25);
            string text = line.Substring(35).TrimEnd();
            var bytes = new List<byte>();
            foreach (string w in wordsField.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                ushort v = Convert.ToUInt16(w, 16);
                bytes.Add((byte)(v >> 8)); bytes.Add((byte)v);
            }
            if (bytes.Count == 0) continue;
            if (pendingLabel) { labels.Add(addr); pendingLabel = false; }
            entries.Add((addr, bytes.ToArray(), text));
            max = Math.Max(max, addr + (uint)bytes.Count);
        }

        var rom = new byte[max + 16];
        foreach (var (addr, bytes, _) in entries) Array.Copy(bytes, 0, rom, addr, bytes.Length);

        int bad = 0, shown = 0;
        foreach (var (addr, bytes, text) in entries)
        {
            DecodedInst n = StructDecoder.Decode(rom, addr);
            string? problem = null;
            if (!n.IsValid && !text.StartsWith("DC.W")) problem = "decoded as invalid";
            else if (n.Length != bytes.Length) problem = $"length {n.Length} vs {bytes.Length}";
            else if (n.Text != text) problem = $"text '{n.Text}' vs '{text}'";
            if (problem != null)
            {
                bad++;
                if (shown++ < 15) Console.WriteLine($"   {addr:X8}: {problem}");
            }
        }
        Console.WriteLine($"   {entries.Count:N0} listing instructions replayed, {bad} mismatches");
        romOut = rom;
        return bad;
    }

    private static int BlockChecks(byte[] rom, List<uint> labels)
    {
        Console.WriteLine($"3. Block graph from {labels.Count:N0} listing labels");
        int bad = 0;
        void Fail(string msg) { bad++; if (bad <= 15) Console.WriteLine("   FAIL: " + msg); }

        BlockGraph g = BlockGraph.Build(rom, labels);
        CodeMap map = CodeMap.Build(rom, labels);

        // Same reachable code as the existing CodeMap?
        if (g.Instructions.Count != map.Instructions.Count)
            Fail($"instruction count {g.Instructions.Count} vs CodeMap {map.Instructions.Count}");
        foreach (var (addr, ins) in map.Instructions)
            if (!g.Instructions.TryGetValue(addr, out var n) || n.Text != ins.Text || n.Length != ins.Length)
                Fail($"instruction at {addr:X6} differs from CodeMap");
        if (g.BytesCovered != map.BytesCovered) Fail($"bytes covered {g.BytesCovered} vs {map.BytesCovered}");

        // Structural invariants.
        int counted = 0;
        uint prevEnd = 0;
        foreach (var (start, b) in g.Blocks)
        {
            if (b.Insts.Count == 0) { Fail($"empty block {start:X6}"); continue; }
            if (b.Start != start || b.Insts[0].Address != start) Fail($"block {start:X6} start mismatch");
            if (start < prevEnd) Fail($"block {start:X6} overlaps the previous block");
            prevEnd = b.End;
            counted += b.Insts.Count;
            for (int i = 0; i < b.Insts.Count; i++)
            {
                if (i > 0 && b.Insts[i].Address != b.Insts[i - 1].NextAddress) Fail($"block {start:X6} not contiguous at {b.Insts[i].Address:X6}");
                if (i < b.Insts.Count - 1 && BlockGraph.EndsBlock(b.Insts[i].Flow)) Fail($"block {start:X6}: control transfer in the middle at {b.Insts[i].Address:X6}");
            }
        }
        if (counted != g.Instructions.Count) Fail($"{counted} instructions in blocks vs {g.Instructions.Count} decoded");

        // Every static branch/jump/call destination must be the start of a block.
        foreach (var b in g.Blocks.Values)
        {
            var l = b.Last;
            if (l.Target is uint t && l.Flow is FlowKind.Jump or FlowKind.Branch && !g.Blocks.ContainsKey(t))
                Fail($"branch at {l.Address:X6} targets {t:X6}, which is not a block start");
        }
        foreach (var ins in g.Instructions.Values)
            if (ins.Flow == FlowKind.Call && ins.Target is uint ct && !g.Blocks.ContainsKey(ct) && !g.ExternalTargets.Contains(ct))
                Fail($"call at {ins.Address:X6} targets {ct:X6}, which is not a block start");

        int totalEdges = g.Blocks.Values.Sum(b => g.Successors(b).Count());
        Console.WriteLine($"   {g.Instructions.Count:N0} instructions in {g.Blocks.Count:N0} blocks, " +
                          $"{g.FunctionEntries.Count:N0} function entries, {totalEdges:N0} edges, " +
                          $"{g.IndirectSites.Count} indirect sites, {g.BadAddresses.Count} undecodable, {bad} failures");
        return bad;
    }
}
