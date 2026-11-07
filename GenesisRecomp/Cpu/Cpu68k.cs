namespace GenesisRecomp.Cpu;

/// <summary>Motorola 68000 interpreter (cycle counts are not modelled yet).</summary>
public sealed partial class Cpu68k
{
    public const ushort FC = 0x01, FV = 0x02, FZ = 0x04, FN = 0x08, FX = 0x10;

    public readonly uint[] D = new uint[8];
    public readonly uint[] A = new uint[8];       // A[7] is the active stack pointer (USP or SSP)
    public uint PC;
    public uint Usp, Ssp;                         // the inactive one is parked here
    public bool Stopped;
    public long InstructionCount;
    public bool StrictAlignment = true;            // real 68000: word/long access at an odd address traps (tests may relax this)
    public long Cycles;                            // rough 68000 cycle estimate: 4 per bus access plus 4 internal per instruction
    public Action<int>? InterruptAcknowledged;     // called when an interrupt of the given level is taken
    public int LastExceptionVector = -1;           // vector raised by the most recent Step(), or -1

    private ushort sr;
    private ushort op;
    private uint instrStart;
    private int irqLines;                          // bit n set = interrupt line of level n asserted (level-triggered)
    private readonly IBus bus;

    private sealed class IllegalOpException : Exception { }
    private sealed class PrivilegeException : Exception { }
    private static readonly IllegalOpException Illegal = new();
    private static readonly PrivilegeException Privilege = new();

    public Cpu68k(IBus bus) => this.bus = bus;

    public ushort SR { get => sr; set => SetSr(value); }
    public bool Supervisor => (sr & 0x2000) != 0;
    public uint CurrentInstructionAddress => instrStart;

    public bool X => (sr & FX) != 0;
    public bool N => (sr & FN) != 0;
    public bool Z => (sr & FZ) != 0;
    public bool V => (sr & FV) != 0;
    public bool C => (sr & FC) != 0;

    public void Reset()
    {
        sr = 0x2700;
        Usp = 0;
        A[7] = bus.Read32(0);
        Ssp = A[7];
        PC = bus.Read32(4);
        Stopped = false;
    }

    /// <summary>Assert or release the interrupt line of the given level (1-7). Devices keep it asserted until acknowledged.</summary>
    public void SetIrqLine(int level, bool asserted)
    {
        if (asserted) irqLines |= 1 << level; else irqLines &= ~(1 << level);
    }

    private void SetSr(ushort value)
    {
        value &= 0xA71F;
        bool wasSup = (sr & 0x2000) != 0, nowSup = (value & 0x2000) != 0;
        if (wasSup != nowSup)
        {
            if (wasSup) { Ssp = A[7]; A[7] = Usp; }
            else { Usp = A[7]; A[7] = Ssp; }
        }
        sr = value;
    }

    // ---------- stepping ----------

    public void Step()
    {
        if (TakeInterrupt()) return;
        if (Stopped) return;
        ExecuteOne();
    }

    /// <summary>If an interrupt is pending and not masked, enter its handler. Returns true if one was taken.</summary>
    private bool TakeInterrupt()
    {
        if (irqLines == 0) return false;
        int level = 31 - System.Numerics.BitOperations.LeadingZeroCount((uint)irqLines);
        if (level != 7 && level <= ((sr >> 8) & 7)) return false;
        Stopped = false;
        ushort old = sr;
        SetSr((ushort)((sr | 0x2000) & ~0x8000));
        sr = (ushort)((sr & ~0x0700) | (level << 8));
        Push32(PC);
        Push16(old);
        PC = bus.Read32((uint)(24 + level) * 4);
        Cycles += 44;
        InterruptAcknowledged?.Invoke(level);
        return true;
    }

    /// <summary>Fetch, decode and run the instruction at PC with the interpreter (no interrupt check).</summary>
    private void ExecuteOne()
    {
        instrStart = PC;
        LastExceptionVector = -1;
        InstructionCount++;
        Cycles += 4;
        try
        {
            op = Fetch16();
            Execute();
        }
        catch (IllegalOpException) { RaiseException(4, instrStart); }
        catch (PrivilegeException) { RaiseException(8, instrStart); }
    }

    private void RaiseException(int vector, uint pcToPush)
    {
        LastExceptionVector = vector;
        Cycles += 34;
        ushort old = sr;
        SetSr((ushort)((sr | 0x2000) & ~0x8000));
        Push32(pcToPush);
        Push16(old);
        PC = bus.Read32((uint)vector * 4);
    }

    private void RequireSupervisor() { if (!Supervisor) throw Privilege; }

    // ---------- memory ----------

    private ushort Fetch16()
    {
        if (StrictAlignment && (PC & 1) != 0) throw new AddressErrorException(PC);
        ushort v = bus.Read16(PC);
        PC += 2;
        Cycles += 4;
        return v;
    }

    private uint Fetch32() { uint hi = Fetch16(); return (hi << 16) | Fetch16(); }

    private uint Read(uint addr, Size s)
    {
        Cycles += s == Size.L ? 8 : 4;
        switch (s)
        {
            case Size.B: return bus.Read8(addr);
            case Size.W:
                if (StrictAlignment && (addr & 1) != 0) throw new AddressErrorException(addr);
                return bus.Read16(addr);
            default:
                if (StrictAlignment && (addr & 1) != 0) throw new AddressErrorException(addr);
                return bus.Read32(addr);
        }
    }

    private void Write(uint addr, Size s, uint v)
    {
        Cycles += s == Size.L ? 8 : 4;
        switch (s)
        {
            case Size.B: bus.Write8(addr, (byte)v); break;
            case Size.W:
                if (StrictAlignment && (addr & 1) != 0) throw new AddressErrorException(addr);
                bus.Write16(addr, (ushort)v); break;
            default:
                if (StrictAlignment && (addr & 1) != 0) throw new AddressErrorException(addr);
                bus.Write32(addr, v); break;
        }
    }

    private void Push16(ushort v) { Cycles += 4; A[7] -= 2; bus.Write16(A[7], v); }
    private void Push32(uint v) { Cycles += 8; A[7] -= 4; bus.Write32(A[7], v); }
    private ushort Pop16() { Cycles += 4; ushort v = bus.Read16(A[7]); A[7] += 2; return v; }
    private uint Pop32() { Cycles += 8; uint v = bus.Read32(A[7]); A[7] += 4; return v; }

    // ---------- size helpers ----------

    private static uint Mask(Size s) => s switch { Size.B => 0xFFu, Size.W => 0xFFFFu, _ => 0xFFFFFFFFu };
    private static uint Msb(Size s) => s switch { Size.B => 0x80u, Size.W => 0x8000u, _ => 0x80000000u };
    private static int Bytes(Size s) => s switch { Size.B => 1, Size.W => 2, _ => 4 };
    private static Size SizeOf(int bits) => bits switch { 0 => Size.B, 1 => Size.W, _ => Size.L };
    private static uint SignExtend(uint v, Size s) => s switch
    {
        Size.B => (uint)(int)(sbyte)v,
        Size.W => (uint)(int)(short)v,
        _ => v
    };

    // ---------- flags ----------

    private void SetFlag(ushort mask, bool on) { if (on) sr |= mask; else sr &= (ushort)~mask; }

    private void SetNZ(uint result, Size s)
    {
        result &= Mask(s);
        SetFlag(FN, (result & Msb(s)) != 0);
        SetFlag(FZ, result == 0);
    }

    private void LogicFlags(uint result, Size s)
    {
        SetNZ(result, s);
        sr &= unchecked((ushort)~(FV | FC));
    }

    private bool Cond(int cc) => cc switch
    {
        0 => true,
        1 => false,
        2 => !C && !Z,
        3 => C || Z,
        4 => !C,
        5 => C,
        6 => !Z,
        7 => Z,
        8 => !V,
        9 => V,
        10 => !N,
        11 => N,
        12 => N == V,
        13 => N != V,
        14 => !Z && N == V,
        _ => Z || N != V
    };

    // ---------- arithmetic with flags ----------

    private uint DoAdd(uint src, uint dst, Size s, bool useX)
    {
        uint m = Mask(s), msb = Msb(s);
        src &= m; dst &= m;
        uint xin = useX && X ? 1u : 0u;
        ulong r = (ulong)dst + src + xin;
        uint res = (uint)r & m;
        bool c = r > m;
        bool v = ((src ^ res) & (dst ^ res) & msb) != 0;
        SetFlag(FC, c); SetFlag(FX, c); SetFlag(FV, v);
        SetFlag(FN, (res & msb) != 0);
        if (useX) { if (res != 0) sr &= unchecked((ushort)~FZ); }   // ADDX: Z only cleared, never set
        else SetFlag(FZ, res == 0);
        return res;
    }

    /// <summary>dst - src (- X). setX=false for CMP.</summary>
    private uint DoSub(uint src, uint dst, Size s, bool useX, bool setX)
    {
        uint m = Mask(s), msb = Msb(s);
        src &= m; dst &= m;
        uint xin = useX && X ? 1u : 0u;
        uint res = (dst - src - xin) & m;
        bool c = (ulong)src + xin > dst;
        bool v = ((src ^ dst) & (dst ^ res) & msb) != 0;
        SetFlag(FC, c); if (setX) SetFlag(FX, c); SetFlag(FV, v);
        SetFlag(FN, (res & msb) != 0);
        if (useX) { if (res != 0) sr &= unchecked((ushort)~FZ); }
        else SetFlag(FZ, res == 0);
        return res;
    }

    // ---------- effective addresses ----------

    private const int mDn = 1, mAn = 2, mInd = 4, mPost = 8, mPre = 16, mDisp = 32, mIdx = 64,
                      mAbsW = 128, mAbsL = 256, mPcD = 512, mPcX = 1024, mImm = 2048;
    private const int AllModes = 4095;
    private const int DataModes = AllModes & ~mAn;
    private const int DataAlt = mDn | mInd | mPost | mPre | mDisp | mIdx | mAbsW | mAbsL;
    private const int MemAlt = DataAlt & ~mDn;
    private const int Alterable = DataAlt | mAn;
    private const int CtrlModes = mInd | mDisp | mIdx | mAbsW | mAbsL | mPcD | mPcX;
    private const int DataNoImm = DataModes & ~mImm;
    private const int MovemStore = mInd | mPre | mDisp | mIdx | mAbsW | mAbsL;
    private const int MovemLoad = mInd | mPost | mDisp | mIdx | mAbsW | mAbsL | mPcD | mPcX;

    private enum EaKind : byte { Dn, An, Mem, Imm }
    private struct Ea { public EaKind Kind; public uint Val; }   // Val = register index, address, or immediate

    private Ea ResolveChecked(int mode, int reg, Size s, int allowed)
    {
        int bit = mode < 7 ? 1 << mode : reg <= 4 ? 1 << (7 + reg) : 0;
        if ((allowed & bit) == 0) throw Illegal;
        return Resolve(mode, reg, s);
    }

    private Ea Resolve(int mode, int reg, Size s)
    {
        switch (mode)
        {
            case 0: return new Ea { Kind = EaKind.Dn, Val = (uint)reg };
            case 1: return new Ea { Kind = EaKind.An, Val = (uint)reg };
            case 2: return Mem(A[reg]);
            case 3:
            {
                uint addr = A[reg];
                A[reg] += (uint)(reg == 7 && s == Size.B ? 2 : Bytes(s));
                return Mem(addr);
            }
            case 4:
                A[reg] -= (uint)(reg == 7 && s == Size.B ? 2 : Bytes(s));
                return Mem(A[reg]);
            case 5:
            {
                short d = (short)Fetch16();
                return Mem(A[reg] + (uint)(int)d);
            }
            case 6: return Mem(IndexedAddress(A[reg]));
            default:
                switch (reg)
                {
                    case 0: return Mem((uint)(int)(short)Fetch16());
                    case 1: return Mem(Fetch32());
                    case 2:
                    {
                        uint basePc = PC;
                        short d = (short)Fetch16();
                        return Mem(basePc + (uint)(int)d);
                    }
                    case 3: return Mem(IndexedAddress(PC));
                    case 4:
                    {
                        uint v = s switch { Size.B => (uint)(Fetch16() & 0xFF), Size.W => Fetch16(), _ => Fetch32() };
                        return new Ea { Kind = EaKind.Imm, Val = v };
                    }
                    default: throw Illegal;
                }
        }
    }

    private static Ea Mem(uint addr) => new Ea { Kind = EaKind.Mem, Val = addr };

    /// <summary>Brief-format index extension word: base + d8 + Xn.</summary>
    private uint IndexedAddress(uint baseValue)
    {
        ushort e = Fetch16();
        if ((e & 0x0100) != 0) throw Illegal;                  // 68020 full format
        int idxReg = (e >> 12) & 7;
        uint idx = (e & 0x8000) != 0 ? A[idxReg] : D[idxReg];
        if ((e & 0x0800) == 0) idx = (uint)(int)(short)idx;
        return baseValue + idx + (uint)(int)(sbyte)(e & 0xFF);
    }

    private uint ReadEa(Ea ea, Size s)
    {
        switch (ea.Kind)
        {
            case EaKind.Dn: return D[ea.Val] & Mask(s);
            case EaKind.An: return A[ea.Val] & Mask(s);
            case EaKind.Imm: return ea.Val;
            default: return Read(ea.Val, s);
        }
    }

    private void WriteEa(Ea ea, Size s, uint v)
    {
        switch (ea.Kind)
        {
            case EaKind.Dn:
            {
                uint m = Mask(s);
                D[ea.Val] = (D[ea.Val] & ~m) | (v & m);
                break;
            }
            case EaKind.An: A[ea.Val] = v; break;
            case EaKind.Mem: Write(ea.Val, s, v); break;
            default: throw Illegal;
        }
    }
}
