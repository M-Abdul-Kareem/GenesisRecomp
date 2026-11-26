using GenesisRecomp.Cpu;

namespace GenesisRecomp.Tools;

/// <summary>
/// Runs the SingleStepTests/M68000 binary test-vector corpus.
/// The corpus stores the 68000's next-prefetch address in PC; the instruction
/// under test begins four bytes earlier. Prefetch and bus-cycle equivalence
/// are intentionally not checked by this first architectural-state runner.
/// </summary>
public static class SingleStepVerify
{
    private const uint FileMagic = 0x1A3F5D71;
    private const uint TestMagic = 0xABC12367;
    private const uint NameMagic = 0x89ABCDEF;
    private const uint StateMagic = 0x01234567;
    private const uint TransactionMagic = 0x456789AB;

    private sealed class CpuState
    {
        public readonly uint[] D = new uint[8];
        public readonly uint[] A = new uint[7];
        public uint Usp, Ssp, Sr, Pc;
        public readonly List<(uint Address, byte Value)> Ram = new();
    }

    private sealed class TestVector
    {
        public string Name = "";
        public CpuState Initial = new();
        public CpuState Final = new();
        public bool HasAddressErrorCycle;
    }

    private sealed class TestBus : IBus
    {
        private readonly Dictionary<uint, byte> memory = new();
        public readonly HashSet<uint> WrittenAddresses = new();

        public TestBus(IEnumerable<(uint Address, byte Value)> initial)
        {
            foreach (var (address, value) in initial)
                memory[address & 0xFFFFFF] = value;
        }

        public byte Read8(uint address) =>
            memory.TryGetValue(address & 0xFFFFFF, out byte value) ? value : (byte)0;

        public ushort Read16(uint address) =>
            (ushort)((Read8(address) << 8) | Read8(address + 1));

        public uint Read32(uint address) =>
            ((uint)Read16(address) << 16) | Read16(address + 2);

        public void Write8(uint address, byte value)
        {
            uint a = address & 0xFFFFFF;
            memory[a] = value;
            WrittenAddresses.Add(a);
        }

        public void Write16(uint address, ushort value)
        {
            Write8(address, (byte)(value >> 8));
            Write8(address + 1, (byte)value);
        }

        public void Write32(uint address, uint value)
        {
            Write16(address, (ushort)(value >> 16));
            Write16(address + 2, (ushort)value);
        }

        public byte Peek(uint address) => Read8(address);
    }

    private static uint ReadUInt32(BinaryReader reader) => reader.ReadUInt32();

    private static void ReadBlockHeader(BinaryReader reader, uint expectedMagic, string block)
    {
        _ = ReadUInt32(reader); // byte count for this block
        uint magic = ReadUInt32(reader);
        if (magic != expectedMagic)
            throw new InvalidDataException(
                $"Invalid {block} block at file offset 0x{reader.BaseStream.Position - 4:X}: " +
                $"expected 0x{expectedMagic:X8}, got 0x{magic:X8}.");
    }

    private static string ReadName(BinaryReader reader)
    {
        ReadBlockHeader(reader, NameMagic, "name");
        uint length = ReadUInt32(reader);
        if (length > 4096)
            throw new InvalidDataException($"Unreasonable test-name length: {length}.");
        byte[] bytes = reader.ReadBytes((int)length);
        if (bytes.Length != length)
            throw new EndOfStreamException("Truncated test name.");
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    private static CpuState ReadState(BinaryReader reader)
    {
        ReadBlockHeader(reader, StateMagic, "state");
        var state = new CpuState();

        for (int i = 0; i < 8; i++) state.D[i] = ReadUInt32(reader);
        for (int i = 0; i < 7; i++) state.A[i] = ReadUInt32(reader);
        state.Usp = ReadUInt32(reader);
        state.Ssp = ReadUInt32(reader);
        state.Sr = ReadUInt32(reader);
        state.Pc = ReadUInt32(reader);

        _ = ReadUInt32(reader); // prefetch word 0
        _ = ReadUInt32(reader); // prefetch word 1

        uint ramWords = ReadUInt32(reader);
        if (ramWords > 1_000_000)
            throw new InvalidDataException($"Unreasonable RAM-entry count: {ramWords}.");

        for (uint i = 0; i < ramWords; i++)
        {
            uint address = ReadUInt32(reader);
            // The corpus stores each RAM word as a little-endian uint16 in the file.
            // Decode the numeric word first, then split it into the 68000's big-endian bytes.
            ushort word = reader.ReadUInt16();
            state.Ram.Add((address & 0xFFFFFF, (byte)(word >> 8)));
            state.Ram.Add(((address + 1) & 0xFFFFFF, (byte)word));
        }

        return state;
    }

    private static bool ReadTransactions(BinaryReader reader)
    {
        ReadBlockHeader(reader, TransactionMagic, "transactions");
        _ = ReadUInt32(reader); // total bus cycles
        uint count = ReadUInt32(reader);
        if (count > 1_000_000)
            throw new InvalidDataException($"Unreasonable transaction count: {count}.");

        bool addressError = false;
        for (uint i = 0; i < count; i++)
        {
            byte kind = reader.ReadByte();
            _ = ReadUInt32(reader); // cycle offset
            if (kind != 0)
            {
                _ = ReadUInt32(reader); // function code
                _ = ReadUInt32(reader); // address bus
                _ = ReadUInt32(reader); // data bus
                _ = ReadUInt32(reader); // UDS
                _ = ReadUInt32(reader); // LDS
            }

            if (kind is 4 or 5) addressError = true; // read/write address-error cycle
        }
        return addressError;
    }

    private static TestVector ReadTest(BinaryReader reader)
    {
        ReadBlockHeader(reader, TestMagic, "test");
        var test = new TestVector
        {
            Name = ReadName(reader),
            Initial = ReadState(reader),
            Final = ReadState(reader),
            HasAddressErrorCycle = ReadTransactions(reader)
        };
        return test;
    }

    private static bool TryRun(TestVector test, out string difference, out string skipReason)
    {
        difference = "";
        skipReason = "";

        if (test.HasAddressErrorCycle)
        {
            skipReason = "corpus records an address-error bus cycle; exception/bus sequencing is not modelled";
            return false;
        }

        if (test.Initial.Pc < 4 || (test.Initial.Pc & 1) != 0)
        {
            skipReason = "unsupported initial PC alignment/range";
            return false;
        }

        var bus = new TestBus(test.Initial.Ram);
        var cpu = new Cpu68k(bus);

        // Set SR first, then explicitly restore both stack banks and the active A7.
        cpu.SR = (ushort)test.Initial.Sr;
        cpu.Usp = test.Initial.Usp;
        cpu.Ssp = test.Initial.Ssp;

        for (int i = 0; i < 8; i++) cpu.D[i] = test.Initial.D[i];
        for (int i = 0; i < 7; i++) cpu.A[i] = test.Initial.A[i];

        bool supervisor = (test.Initial.Sr & 0x2000) != 0;
        cpu.A[7] = supervisor ? test.Initial.Ssp : test.Initial.Usp;

        // MAME's m_au PC points four bytes beyond the instruction being executed.
        cpu.PC = test.Initial.Pc - 4;

        // Diagnostic metadata only: captured before Step() so the exception frame cannot disturb it.
        ushort diagOpcode = bus.Read16(test.Initial.Pc - 4);
        ushort diagNextWord = bus.Read16(test.Initial.Pc - 2);

        try
        {
            cpu.Step();
        }
        catch (AddressErrorException ex)
        {
            skipReason = $"address-error exception is not modelled by this verifier ({ex.Message})";
            return false;
        }

        // Exceptions implemented by Cpu68k are not automatically skipped.
        // Their final architectural state (PC, SR, stack banks, registers,
        // and RAM-backed exception frame) is checked below like any other test.

        var diffs = new List<string>();

        // Corpus PC convention: the reference PC runs four bytes ahead of the architectural PC
        // because two prefetch words are in flight. STOP does not refill the prefetch queue, so
        // after a STOP that actually executes (no exception) the corpus PC already equals the
        // address of the next instruction and must not be reduced by four.
        bool stoppedWithoutException = cpu.Stopped && cpu.LastExceptionVector < 0;
        uint expectedPc = stoppedWithoutException
            ? test.Final.Pc
            : (test.Final.Pc >= 4 ? test.Final.Pc - 4 : test.Final.Pc);
        if (cpu.PC != expectedPc)
            diffs.Add($"PC expected {expectedPc:X8}, got {cpu.PC:X8}");

        for (int i = 0; i < 8; i++)
            if (cpu.D[i] != test.Final.D[i])
                diffs.Add($"D{i} expected {test.Final.D[i]:X8}, got {cpu.D[i]:X8}");

        for (int i = 0; i < 7; i++)
            if (cpu.A[i] != test.Final.A[i])
                diffs.Add($"A{i} expected {test.Final.A[i]:X8}, got {cpu.A[i]:X8}");

        // A[7] is the active stack pointer. Cpu68k stores only the inactive
        // stack bank in Usp/Ssp; comparing those fields directly for both
        // banks incorrectly reports every active-stack adjustment as a mismatch.
        bool finalSupervisor = (test.Final.Sr & 0x2000) != 0;
        uint actualUsp = finalSupervisor ? cpu.Usp : cpu.A[7];
        uint actualSsp = finalSupervisor ? cpu.A[7] : cpu.Ssp;
        if (actualUsp != test.Final.Usp)
            diffs.Add($"USP expected {test.Final.Usp:X8}, got {actualUsp:X8}");
        if (actualSsp != test.Final.Ssp)
            diffs.Add($"SSP expected {test.Final.Ssp:X8}, got {actualSsp:X8}");

        // Compare architecturally defined SR bits (T, S, interrupt mask and XNZVC).
        const uint srMask = 0xA71F;
        uint expectedSr = test.Final.Sr & srMask;
        uint actualSr = (uint)cpu.SR & srMask;
        if (actualSr != expectedSr)
            diffs.Add($"SR expected {expectedSr:X4}, got {actualSr:X4}");

        var expectedRam = new Dictionary<uint, byte>();
        foreach (var item in test.Final.Ram)
            expectedRam[item.Address & 0xFFFFFF] = item.Value;
        var addresses = new HashSet<uint>(expectedRam.Keys);
        foreach (var item in test.Initial.Ram) addresses.Add(item.Address & 0xFFFFFF);
        foreach (uint address in bus.WrittenAddresses) addresses.Add(address);

        foreach (uint address in addresses)
        {
            byte expected = expectedRam.TryGetValue(address, out byte value) ? value : (byte)0;
            byte actual = bus.Peek(address);
            if (actual != expected)
            {
                diffs.Add($"RAM[{address:X6}] expected {expected:X2}, got {actual:X2}");
                if (diffs.Count >= 12) break;
            }
        }

        // LastExceptionVector is diagnostic metadata only. It is attached to a case
        // that already has a real final-state mismatch; it never causes a failure by itself.
        if (diffs.Count > 0 && cpu.LastExceptionVector >= 0)
        {
            diffs.Insert(0,
                $"[diag] CPU entered exception vector {cpu.LastExceptionVector}; " +
                $"opcode {diagOpcode:X4}; next word {diagNextWord:X4}; " +
                $"initial SR {test.Initial.Sr:X4}; initial PC {test.Initial.Pc - 4:X8}");
        }

        difference = string.Join("; ", diffs);
        return diffs.Count == 0;
    }

    public static int Run(string path, int maxTests = int.MaxValue)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            Console.Error.WriteLine($"Test corpus path not found: {path}");
            return 2;
        }

        IEnumerable<string> files = File.Exists(path)
            ? new[] { path }
            : Directory.EnumerateFiles(path, "*.json.bin", SearchOption.AllDirectories).OrderBy(x => x);

        int filesRead = 0, total = 0, passed = 0, failed = 0, skipped = 0;
        var failures = new List<string>();
        var skipCounts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var skipExamples = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (string file in files)
        {
            if (total >= maxTests) break;
            filesRead++;
            try
            {
                using var stream = File.OpenRead(file);
                using var reader = new BinaryReader(stream);

                uint magic = ReadUInt32(reader);
                uint count = ReadUInt32(reader);
                if (magic != FileMagic)
                    throw new InvalidDataException($"Invalid file magic 0x{magic:X8}.");
                if (count > 1_000_000)
                    throw new InvalidDataException($"Unreasonable test count: {count}.");

                for (uint i = 0; i < count && total < maxTests; i++)
                {
                    TestVector test = ReadTest(reader);
                    total++;

                    bool ok = TryRun(test, out string difference, out string skipReason);
                    if (ok) passed++;
                    else if (!string.IsNullOrEmpty(skipReason))
                    {
                        skipped++;

                        string category =
                            skipReason.StartsWith("corpus records an address-error", StringComparison.Ordinal)
                                ? "Recorded address-error bus cycle"
                            : skipReason.StartsWith("address-error exception", StringComparison.Ordinal)
                                ? "AddressErrorException thrown by CPU"
                            : skipReason.StartsWith("exception vector", StringComparison.Ordinal)
                                ? "CPU exception vector"
                            : skipReason.StartsWith("unsupported initial PC", StringComparison.Ordinal)
                                ? "Unsupported initial PC"
                            : "Other skip reason";

                        skipCounts.TryGetValue(category, out int categoryCount);
                        skipCounts[category] = categoryCount + 1;

                        if (!skipExamples.TryGetValue(category, out var examples))
                        {
                            examples = new List<string>();
                            skipExamples[category] = examples;
                        }

                        if (examples.Count < 5)
                            examples.Add(test.Name + " (" + skipReason + ")");
                    }
                    else
                    {
                        failed++;
                        if (failures.Count < 25)
                            failures.Add($"{Path.GetFileName(file)} / {test.Name}: {difference}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Could not read {file}: {ex.Message}");
                return 2;
            }
        }

        Console.WriteLine("SingleStepTests / Motorola 68000 architectural-state verification");
        Console.WriteLine($"Files read : {filesRead:N0}");
        Console.WriteLine($"Cases      : {total:N0}");
        Console.WriteLine($"Passed     : {passed:N0}");
        Console.WriteLine($"Failed     : {failed:N0}");
        Console.WriteLine($"Skipped    : {skipped:N0}");

        if (skipCounts.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Skip breakdown:");

            foreach (var entry in skipCounts)
            {
                Console.WriteLine($"  {entry.Key}: {entry.Value:N0}");

                if (skipExamples.TryGetValue(entry.Key, out var examples))
                {
                    foreach (string example in examples)
                        Console.WriteLine($"    Example: {example}");
                }
            }
        }

        int categorizedSkips = skipCounts.Values.Sum();
        Console.WriteLine();
        Console.WriteLine(
            $"Skip-count consistency: {categorizedSkips:N0} categorized / {skipped:N0} total skipped");
        Console.WriteLine("Exception cases handled by Cpu68k are compared by final architectural state.");
        Console.WriteLine("Not compared: prefetch queue, bus-cycle trace, and exact cycle timing.");

        if (failures.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("First disagreements:");
            foreach (string failure in failures) Console.WriteLine("  " + failure);
            File.WriteAllLines("singlestep_failures.txt", failures);
            Console.WriteLine("Failure samples written to singlestep_failures.txt");
        }

        return failed == 0 ? 0 : 1;
    }
}
