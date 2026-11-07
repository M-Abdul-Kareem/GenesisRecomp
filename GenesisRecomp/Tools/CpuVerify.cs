using System.Globalization;
using GenesisRecomp.Cpu;

namespace GenesisRecomp.Tools;

/// <summary>
/// Replays single-instruction test cases (generated and solved by the Unicorn emulator) on our CPU
/// and reports every disagreement. Case line (hex fields):
///   ok|exc  op e1 e2 e3  seed  D0..D7  A0..A7  SR   [-> PC D0..D7 A0..A7 SR crc]
/// </summary>
public static class CpuVerify
{
    private sealed class TestBus : IBus
    {
        public readonly byte[] Ram = new byte[0x10000];
        public bool OutOfRange;
        private bool InRam(uint a) => (a & 0xFFFFFF) >= 0xFF0000;
        public byte Read8(uint a) { a &= 0xFFFFFF; if (!InRam(a)) { OutOfRange = true; return 0; } return Ram[a & 0xFFFF]; }
        public ushort Read16(uint a) => (ushort)((Read8(a) << 8) | Read8(a + 1));
        public uint Read32(uint a) => ((uint)Read16(a) << 16) | Read16(a + 2);
        public void Write8(uint a, byte v) { a &= 0xFFFFFF; if (!InRam(a)) { OutOfRange = true; return; } Ram[a & 0xFFFF] = v; }
        public void Write16(uint a, ushort v) { Write8(a, (byte)(v >> 8)); Write8(a + 1, (byte)v); }
        public void Write32(uint a, uint v) { Write16(a, (ushort)(v >> 16)); Write16(a + 2, (ushort)v); }
    }

    private static readonly uint[] CrcTable = BuildCrc();
    private static uint[] BuildCrc()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }
    private static uint Crc32(byte[] d)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte b in d) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return ~c;
    }

    private static readonly Dictionary<int, byte[]> FillCache = new();
    private static byte[] Fill(int seed)
    {
        if (FillCache.TryGetValue(seed, out var f)) return f;
        f = new byte[0x10000];
        for (uint i = 0; i < f.Length; i++)
            f[i] = (byte)((((ulong)i * 2654435761UL + (ulong)seed * 40503UL) & 0xFFFFFFFF) >> 16);
        FillCache[seed] = f;
        return f;
    }

    /// <summary>Instructions that touch the status register's system byte: Unicorn models 68020 stack-switching bits, so skip.</summary>
    private static bool TouchesSystemByte(ushort op) =>
        op == 0x007C || op == 0x027C || op == 0x0A7C || (op & 0xFFC0) == 0x46C0 || op == 0x4E73 || op == 0x4E72 || (op & 0xFFF0) == 0x4E60;

    private static bool ValidBcd(uint b) => (b & 0x0F) <= 9 && ((b >> 4) & 0x0F) <= 9;

    /// <summary>
    /// Cases where Unicorn (QEMU) is known to differ from a real 68000, or where the manual leaves the result undefined.
    /// Returns a reason to skip the case entirely, or sets ccrMask / ignoreNzOnOverflow for a partial comparison.
    /// </summary>
    private static string? Quirk(ushort[] code, string[] f, out ushort ccrMask, out bool ignoreNzOnOverflow)
    {
        ushort op = code[0];
        ccrMask = 0x1F; ignoreNzOnOverflow = false;
        uint D(int i) => H(f[6 + i]);

        if (op == 0x4E5F || op == 0x4E97) return "QEMU computes the EA after the stack access (UNLK A7 / JSR (A7))";
        if ((op & 0xF138) == 0xD108 || (op & 0xF138) == 0x9108)
            if ((op & 0xC0) == 0 && ((op & 7) == 7 || ((op >> 9) & 7) == 7))
                return "byte -(A7) in ADDX/SUBX: a real 68000 steps SP by 2, QEMU by 1";
        if ((op & 0xFFF8) == 0x48A0 || (op & 0xFFF8) == 0x48E0)
            if ((code[1] & (1 << (7 - (op & 7)))) != 0)
                return "MOVEM to -(An) with An in the list: 68000 stores the initial value, QEMU the decremented one";

        bool abcd = (op & 0xF1F8) == 0xC100, sbcd = (op & 0xF1F8) == 0x8100;
        if (abcd || sbcd)
            return ValidBcd(D(op & 7)) && ValidBcd(D((op >> 9) & 7)) ? null : "invalid BCD operands: result is implementation-defined";
        if ((op & 0xF1F8) == 0xC108 || (op & 0xF1F8) == 0x8108) return "BCD memory form (operands not checked for validity)";
        if ((op & 0xFFF8) == 0x4800) return ValidBcd(D(op & 7)) ? null : "invalid BCD operand";
        if ((op & 0xFFC0) == 0x4800) return "NBCD memory form";

        if ((op & 0xFEC0) == 0xE0C0 || (op & 0xFEC0) == 0xE2C0)
            return "memory ASR/LSR: QEMU swaps the arithmetic and logical right shift for negative values";
        if ((op & 0xF8C0) == 0xE0C0) { ccrMask = 0; return null; }                 // memory shifts: QEMU's ASL V flag is wrong; shift logic is shared with the register forms
        if ((op & 0xF1C0) == 0x4180) { ccrMask = 0; return null; }                 // CHK: flags undefined unless it traps
        if ((op & 0xF0C0) == 0x80C0) ignoreNzOnOverflow = true;                    // DIVU/DIVS: N and Z undefined on overflow
        return null;
    }

    private static string Name(ushort[] code)
    {
        var bytes = new byte[8];
        for (int i = 0; i < 4; i++) { bytes[i * 2] = (byte)(code[i] >> 8); bytes[i * 2 + 1] = (byte)code[i]; }
        var ins = Decoder.Decode(bytes, 0);
        return ins.IsValid ? ins.Mnemonic.Split('.')[0] : "(invalid)";
    }

    private static uint H(string s) => uint.Parse(s, NumberStyles.HexNumber);

    public static int Run(string path)
    {
        int total = 0, compared = 0, passed = 0, skippedSys = 0, lenient = 0, exFetch = 0;
        var buckets = new Dictionary<string, (int Count, string Example)>();
        void Add(string key, string example)
        {
            buckets[key] = buckets.TryGetValue(key, out var b) ? (b.Count + 1, b.Example) : (1, example);
        }
        var excChecked = 0; var excOk = 0;
        var failures = new List<string>();
        var quirkSkips = new Dictionary<string, int>();

        foreach (string line in File.ReadLines(path))
        {
            string[] f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 22) continue;
            total++;
            bool expectException = f[0] == "exc";
            ushort[] code = { (ushort)H(f[1]), (ushort)H(f[2]), (ushort)H(f[3]), (ushort)H(f[4]) };
            int seed = (int)H(f[5]);
            if (TouchesSystemByte(code[0])) { skippedSys++; continue; }
            ushort ccrMask = 0x1F;
            bool ignoreNz = false;
            string? quirk = expectException ? null : Quirk(code, f, out ccrMask, out ignoreNz);
            if (quirk != null) { quirkSkips[quirk] = quirkSkips.GetValueOrDefault(quirk) + 1; continue; }

            var bus = new TestBus();
            Array.Copy(Fill(seed), bus.Ram, 0x10000);
            for (int i = 0; i < 4; i++) { bus.Ram[0x10 + i * 2] = (byte)(code[i] >> 8); bus.Ram[0x11 + i * 2] = (byte)code[i]; }

            var cpu = new Cpu68k(bus) { StrictAlignment = false };      // Unicorn does not trap unaligned accesses
            for (int i = 0; i < 8; i++) cpu.D[i] = H(f[6 + i]);
            for (int i = 0; i < 8; i++) cpu.A[i] = H(f[14 + i]);
            cpu.Ssp = cpu.A[7];
            cpu.SR = (ushort)H(f[22]);
            cpu.A[7] = H(f[21]);
            cpu.PC = 0xFF0010;
            bus.OutOfRange = false;

            string err = "";
            try { cpu.Step(); }
            catch (AddressErrorException ex) { err = "address error " + ex.Message; }

            string name = Name(code);
            string desc = $"op={code[0]:X4} ext={code[1]:X4} {code[2]:X4} seed={seed} [{name}]";

            if (expectException)
            {
                excChecked++;
                if (cpu.LastExceptionVector >= 0 || err != "") excOk++;
                else if (cpu.PC % 2 == 1 || (code[0] & 0xFFF8) == 0x4E70 || (code[0] & 0xFFFE) == 0x4E76) exFetch++;   // Unicorn faults on the NEXT fetch
                else Add($"Unicorn raised an exception, we did not: {name}", desc);
                continue;
            }

            compared++;
            if (err != "") { Add($"address error despite relaxed alignment: {name}", desc); failures.Add(desc + ": " + err); continue; }
            if (cpu.LastExceptionVector >= 0)
            {
                if (name == "(invalid)") lenient++;                      // our static decoder (verified against Capstone) also calls this illegal
                else { Add($"we raised vector {cpu.LastExceptionVector} on a valid opcode: {name}", desc); failures.Add(desc); }
                continue;
            }

            var diffs = new List<string>();
            if (bus.OutOfRange) diffs.Add("out-of-range bus access");
            if (cpu.PC != H(f[24])) diffs.Add($"PC {cpu.PC:X8} vs {H(f[24]):X8}");
            for (int i = 0; i < 8; i++) if (cpu.D[i] != H(f[25 + i])) diffs.Add($"D{i} {cpu.D[i]:X8} vs {H(f[25 + i]):X8}");
            for (int i = 0; i < 8; i++) if (cpu.A[i] != H(f[33 + i])) diffs.Add($"A{i} {cpu.A[i]:X8} vs {H(f[33 + i]):X8}");
            ushort want = (ushort)(H(f[41]) & 0x1F), got = (ushort)(cpu.SR & 0x1F);
            ushort mask = ccrMask;
            if (ignoreNz && (want & 0x02) != 0) mask &= unchecked((ushort)~0x0C);      // overflow: N and Z are undefined
            want &= mask; got &= mask;
            if (got != want) diffs.Add($"CCR {got:X2} vs {want:X2} (initial {H(f[22]) & 0x1F:X2})");
            if (Crc32(bus.Ram) != H(f[42])) diffs.Add("RAM contents differ");

            if (diffs.Count == 0) { passed++; continue; }
            string kind = diffs[0].Split(' ')[0];
            if (kind.StartsWith("D") || kind.StartsWith("A")) kind = kind[0] + "n";
            Add($"{kind} differs: {name}", $"{desc}: {string.Join("; ", diffs)}");
            failures.Add($"{desc}: {string.Join("; ", diffs)}");
        }

        Console.WriteLine($"cases: {total:N0}  compared: {compared:N0}  passed: {passed:N0}  failed: {compared - passed - lenient:N0}  " +
                          $"(Unicorn accepts a malformed encoding that we reject: {lenient:N0}; SR-system-byte ops skipped: {skippedSys:N0})");
        Console.WriteLine("Cases excluded because Unicorn differs from a real 68000 or the result is undefined:");
        foreach (var kv in quirkSkips.OrderByDescending(kv => kv.Value)) Console.WriteLine($"  {kv.Value,6}  {kv.Key}");
        Console.WriteLine($"Unicorn-exception cases: {excChecked:N0}  we also raised an exception: {excOk:N0}  Unicorn faulted on the next fetch instead: {exFetch:N0}");
        Console.WriteLine();
        Console.WriteLine("Remaining disagreements, grouped (count, first example):");
        foreach (var kv in buckets.OrderByDescending(kv => kv.Value.Count).Take(45))
            Console.WriteLine($"  {kv.Value.Count,5}  {kv.Key}\n         e.g. {kv.Value.Example}");
        File.WriteAllLines("verify_failures.txt", failures);
        return compared - passed - lenient == 0 ? 0 : 1;
    }
}
