using System.Runtime.CompilerServices;

namespace GenesisRecomp.Cpu;

/// <summary>One ROM whose 68000 code was recompiled ahead of time (see Generated/Rom_XXXXXXXX.cs).</summary>
public sealed record RecompiledGame(uint Crc, int Segments, int MaxSegment, Func<Cpu68k, uint, bool> Run);

/// <summary>
/// Runtime side of the static recompiler. Each generated file (Generated/Rom_&lt;crc&gt;.cs) registers its game here
/// when the program starts; a machine picks the entry that matches its ROM by CRC32.
/// A ROM that was never recompiled just runs through the interpreter, so any game can be played without rebuilding.
/// Anything inside a recompiled game that is not covered (code running from RAM, unusual instructions, unknown jump targets)
/// also runs through the interpreter, one instruction at a time.
/// </summary>
public sealed partial class Cpu68k
{
    /// <summary>Set to false (command line: --interp) to force the plain interpreter even when recompiled code exists.</summary>
    public static bool AllowRecompiled = true;

    /// <summary>Every game recompiled into this program, by ROM CRC32.</summary>
    public static readonly Dictionary<uint, RecompiledGame> RecompiledGames = new();

    /// <summary>Set to false (command line: --no-live) to stop ROMs without built-in recompiled code from being recompiled in memory.</summary>
    public static bool AllowLive = true;

    private Func<Cpu68k, uint, bool>? runBlock;

    /// <summary>What supplies the recompiled code: a <see cref="RecompiledGame"/> (built into the program) or a LiveRecompiler (made when the game was opened).</summary>
    public object? RecompSource;

    /// <summary>True when this CPU runs generated code instead of the interpreter.</summary>
    public bool UseRecompiled => runBlock != null;

    /// <summary>Instructions that had to go through the interpreter while recompiled mode was active.</summary>
    public long FallbackInsts;

    /// <summary>Use the recompiled code for the ROM with this CRC32 if this program contains it. Returns true if it does.</summary>
    public bool SelectRecompiled(uint romCrc)
    {
        RecompiledGame? game = null;
        runBlock = AllowRecompiled && RecompiledGames.TryGetValue(romCrc, out game) ? game.Run : null;
        RecompSource = runBlock != null ? game : null;
        return runBlock != null;
    }

    /// <summary>Use code recompiled in memory when the game was opened (see <see cref="Recomp.LiveRecompiler"/>).</summary>
    public void SelectLive(Recomp.LiveRecompiler live)
    {
        runBlock = live.Run;
        RecompSource = live;
    }

    /// <summary>Run one recompiled segment (or one interpreted instruction if PC is not at a segment start).</summary>
    public void StepBlock()
    {
        if (TakeInterrupt()) return;
        if (Stopped) return;
        LastExceptionVector = -1;
        if (runBlock == null || !runBlock(this, PC))
        {
            FallbackInsts++;
            ExecuteOne();
        }
    }

    /// <summary>Test hook: interpret exactly one instruction at PC, no interrupt check.</summary>
    public void InterpretOne() => ExecuteOne();

    /// <summary>Test hook: run the generated segment that starts at PC. False if there is none.</summary>
    public bool RunGenerated() => runBlock != null && runBlock(this, PC);

    // ---- memory helpers used by generated code (same cycle counts and alignment rules as the interpreter) ----

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint R8(uint a) { Cycles += 4; return bus.Read8(a); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint R16(uint a)
    {
        Cycles += 4;
        if (StrictAlignment && (a & 1) != 0) throw new AddressErrorException(a);
        return bus.Read16(a);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint R32(uint a)
    {
        Cycles += 8;
        if (StrictAlignment && (a & 1) != 0) throw new AddressErrorException(a);
        return bus.Read32(a);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void W8(uint a, byte v) { Cycles += 4; bus.Write8(a, v); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void W16(uint a, ushort v)
    {
        Cycles += 4;
        if (StrictAlignment && (a & 1) != 0) throw new AddressErrorException(a);
        bus.Write16(a, v);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void W32(uint a, uint v)
    {
        Cycles += 8;
        if (StrictAlignment && (a & 1) != 0) throw new AddressErrorException(a);
        bus.Write32(a, v);
    }
}
