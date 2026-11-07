namespace GenesisRecomp.Cpu;

public enum Size { B, W, L }

/// <summary>Motorola 68000 instruction decoder (68000 only, no 68010/020 extensions).</summary>
public static class Decoder
{
    public static Instruction Decode(byte[] rom, uint address)
    {
        var d = new Disassembler(rom, address);
        try { return d.Run(); }
        catch (BadOpcodeException) { return d.Invalid(); }
    }
}

internal sealed class BadOpcodeException : Exception { }

internal sealed class Disassembler
{
    private static readonly string[] Cond =
        { "T", "F", "HI", "LS", "CC", "CS", "NE", "EQ", "VC", "VS", "PL", "MI", "GE", "LT", "GT", "LE" };

    // Addressing-mode sets, used to reject illegal operand combinations per instruction.
    private const int mDn = 1, mAn = 2, mInd = 4, mPost = 8, mPre = 16, mDisp = 32, mIdx = 64,
                      mAbsW = 128, mAbsL = 256, mPcD = 512, mPcX = 1024, mImm = 2048;
    private const int All = 4095;
    private const int Data = All & ~mAn;
    private const int DataAlt = mDn | mInd | mPost | mPre | mDisp | mIdx | mAbsW | mAbsL;
    private const int MemAlt = DataAlt & ~mDn;
    private const int Alterable = DataAlt | mAn;
    private const int CtrlModes = mInd | mDisp | mIdx | mAbsW | mAbsL | mPcD | mPcX;
    private const int DataNoImm = Data & ~mImm;
    private const int MovemStore = mInd | mPre | mDisp | mIdx | mAbsW | mAbsL;
    private const int MovemLoad = mInd | mPost | mDisp | mIdx | mAbsW | mAbsL | mPcD | mPcX;

    private readonly byte[] rom;
    private readonly uint start;
    private uint pos;
    private ushort op;
    private uint? eaAbs;     // absolute address seen by the last Ea() (for JMP/JSR targets)
    private uint? eaPcRel;   // PC-relative address seen by the last Ea()
    private uint? eaPcIdx;   // base address of a d8(PC,Xn) operand

    public Disassembler(byte[] rom, uint address)
    {
        this.rom = rom;
        start = address;
        pos = address;
    }

    public Instruction Run()
    {
        if ((start & 1) != 0) throw new BadOpcodeException();
        op = Next16();
        return (op >> 12) switch
        {
            0x0 => Group0(),
            0x1 or 0x2 or 0x3 => Move(),
            0x4 => Group4(),
            0x5 => Group5(),
            0x6 => Group6(),
            0x7 => Moveq(),
            0x8 => Group8(),
            0x9 => AddSub("SUB"),
            0xB => GroupB(),
            0xC => GroupC(),
            0xD => AddSub("ADD"),
            0xE => Shifts(),
            _ => throw new BadOpcodeException()   // 0xA = Line-A, 0xF = Line-F
        };
    }

    public Instruction Invalid()
    {
        string word = start + 1 < rom.Length ? $"${(rom[start] << 8) | rom[start + 1]:X4}" : "$????";
        return new Instruction
        {
            Address = start, Length = 2, Mnemonic = "DC.W", Operands = word,
            Flow = FlowKind.Halt, IsValid = false
        };
    }

    // ---------- reading helpers ----------

    private ushort Next16()
    {
        if (pos + 1 >= rom.Length) throw new BadOpcodeException();
        ushort v = (ushort)((rom[pos] << 8) | rom[pos + 1]);
        pos += 2;
        return v;
    }

    private uint Next32() => ((uint)Next16() << 16) | Next16();

    private Instruction Make(string mn, string ops = "", FlowKind flow = FlowKind.Normal, uint? target = null) =>
        new()
        {
            Address = start, Length = (int)(pos - start), Mnemonic = mn, Operands = ops,
            Flow = flow, Target = target, PcIndexedBase = eaPcIdx, PcRelAddress = eaPcRel
        };

    private static string Sfx(Size s) => s switch { Size.B => ".B", Size.W => ".W", _ => ".L" };

    private static string SDisp(int d) => d < 0 ? $"-${-d:X}" : $"${d:X}";

    private string Imm(Size s) => s switch
    {
        Size.B => $"#${Next16() & 0xFF:X2}",
        Size.W => $"#${Next16():X4}",
        _ => $"#${Next32():X8}"
    };

    private static bool TryDataSize(int bits, out Size s)
    {
        s = bits switch { 0 => Size.B, 1 => Size.W, _ => Size.L };
        return bits != 3;
    }

    // ---------- effective addresses ----------

    private string Ea(int mode, int reg, Size sz, int allowed = All)
    {
        int bit = mode < 7 ? 1 << mode : reg <= 4 ? 1 << (7 + reg) : 0;
        if ((allowed & bit) == 0) throw new BadOpcodeException();
        switch (mode)
        {
            case 0: return $"D{reg}";
            case 1: return $"A{reg}";
            case 2: return $"(A{reg})";
            case 3: return $"(A{reg})+";
            case 4: return $"-(A{reg})";
            case 5:
            {
                short d = (short)Next16();
                return $"{SDisp(d)}(A{reg})";
            }
            case 6: return Indexed($"A{reg}", false);
            case 7:
                switch (reg)
                {
                    case 0:
                    {
                        short a = (short)Next16();
                        eaAbs = (uint)(int)a;
                        return $"(${(ushort)a:X4}).w";
                    }
                    case 1:
                    {
                        uint a = Next32();
                        eaAbs = a;
                        return $"(${a:X8}).l";
                    }
                    case 2:
                    {
                        uint basePc = pos;
                        short d = (short)Next16();
                        uint t = (uint)(basePc + d);
                        eaPcRel = t;
                        return $"${t:X8}(PC)";
                    }
                    case 3: return Indexed("PC", true);
                    case 4: return Imm(sz);
                    default: throw new BadOpcodeException();
                }
            default: throw new BadOpcodeException();
        }
    }

    private string Indexed(string baseReg, bool isPc)
    {
        uint basePc = pos;                      // PC = address of the extension word
        ushort e = Next16();
        if ((e & 0x0100) != 0) throw new BadOpcodeException();   // 68020 full format
        int d8 = (sbyte)(e & 0xFF);
        string x = $"{((e & 0x8000) != 0 ? 'A' : 'D')}{(e >> 12) & 7}.{((e & 0x0800) != 0 ? 'L' : 'W')}";
        if (isPc)
        {
            uint t = (uint)(basePc + d8);
            eaPcRel = null;                     // indexed: target is not static
            eaPcIdx = t;
            return $"${t:X8}(PC,{x})";
        }
        return $"{SDisp(d8)}({baseReg},{x})";
    }

    // ---------- group 0: immediate and bit operations ----------

    private Instruction Group0()
    {
        int mode = (op >> 3) & 7, reg = op & 7;
        string[] bitOps = { "BTST", "BCHG", "BCLR", "BSET" };

        if ((op & 0x0100) != 0)
        {
            if (mode == 1) return Movep();
            Size s = mode == 0 ? Size.L : Size.B;
            string ea = Ea(mode, reg, s, ((op >> 6) & 3) == 0 ? Data : DataAlt);
            return Make(bitOps[(op >> 6) & 3] + Sfx(s), $"D{(op >> 9) & 7},{ea}");
        }

        int kind = (op >> 9) & 7;
        if (kind == 4)
        {
            Size s = mode == 0 ? Size.L : Size.B;
            ushort bitWord = Next16();
            if ((bitWord & 0xFF00) != 0) throw new BadOpcodeException();   // upper byte must be zero
            int bit = bitWord;
            string ea = Ea(mode, reg, s, ((op >> 6) & 3) == 0 ? DataNoImm : DataAlt);
            return Make(bitOps[(op >> 6) & 3] + Sfx(s), $"#{bit},{ea}");
        }

        string[] names = { "ORI", "ANDI", "SUBI", "ADDI", "", "EORI", "CMPI", "" };
        string name = names[kind];
        if (name == "") throw new BadOpcodeException();

        if (op == 0x003C || op == 0x023C || op == 0x0A3C)
            {
                ushort v = Next16();
                if ((v & 0xFF00) != 0) throw new BadOpcodeException();
                return Make(name, $"#${v:X2},CCR");
            }
        if (op == 0x007C || op == 0x027C || op == 0x0A7C)
            return Make(name, $"#${Next16():X4},SR");

        if (!TryDataSize((op >> 6) & 3, out Size sz)) throw new BadOpcodeException();
        string imm = Imm(sz);
        string dest = Ea(mode, reg, sz, DataAlt);
        return Make(name + Sfx(sz), $"{imm},{dest}");
    }

    private Instruction Movep()
    {
        int an = op & 7, dn = (op >> 9) & 7;
        int opmode = (op >> 6) & 7;
        if (opmode < 4) throw new BadOpcodeException();
        short d = (short)Next16();
        Size s = (opmode & 1) == 0 ? Size.W : Size.L;
        string mem = $"{SDisp(d)}(A{an})";
        return (opmode & 2) == 0
            ? Make("MOVEP" + Sfx(s), $"{mem},D{dn}")
            : Make("MOVEP" + Sfx(s), $"D{dn},{mem}");
    }

    // ---------- groups 1-3: MOVE / MOVEA ----------

    private Instruction Move()
    {
        Size sz = (op >> 12) switch { 1 => Size.B, 3 => Size.W, _ => Size.L };
        string src = Ea((op >> 3) & 7, op & 7, sz, sz == Size.B ? Data : All);
        int dMode = (op >> 6) & 7, dReg = (op >> 9) & 7;
        if (dMode == 1)
        {
            if (sz == Size.B) throw new BadOpcodeException();
            return Make("MOVEA" + Sfx(sz), $"{src},A{dReg}");
        }
        string dst = Ea(dMode, dReg, sz, DataAlt);
        return Make("MOVE" + Sfx(sz), $"{src},{dst}");
    }

    // ---------- group 4: miscellaneous ----------

    private Instruction Group4()
    {
        int mode = (op >> 3) & 7, reg = op & 7;

        if ((op & 0xF1C0) == 0x41C0)
        {
            string ea = Ea(mode, reg, Size.L, CtrlModes);
            return Make("LEA", $"{ea},A{(op >> 9) & 7}");
        }
        if ((op & 0xF1C0) == 0x4180)
        {
            string ea = Ea(mode, reg, Size.W, Data);
            return Make("CHK.W", $"{ea},D{(op >> 9) & 7}");
        }
        if ((op & 0xFFF0) == 0x4E40) return Make("TRAP", $"#{op & 0xF}");

        switch (op & 0xFFF8)
        {
            case 0x4E50: { short d = (short)Next16(); return Make("LINK", $"A{reg},#{SDisp(d)}"); }
            case 0x4E58: return Make("UNLK", $"A{reg}");
            case 0x4E60: return Make("MOVE", $"A{reg},USP");
            case 0x4E68: return Make("MOVE", $"USP,A{reg}");
            case 0x4840: return Make("SWAP", $"D{reg}");
            case 0x4880: return Make("EXT.W", $"D{reg}");
            case 0x48C0: return Make("EXT.L", $"D{reg}");
        }

        switch (op)
        {
            case 0x4E70: return Make("RESET");
            case 0x4E71: return Make("NOP");
            case 0x4E72: return Make("STOP", $"#${Next16():X4}");
            case 0x4E73: return Make("RTE", "", FlowKind.Return);
            case 0x4E75: return Make("RTS", "", FlowKind.Return);
            case 0x4E76: return Make("TRAPV");
            case 0x4E77: return Make("RTR", "", FlowKind.Return);
            case 0x4AFC: return Make("ILLEGAL", "", FlowKind.Halt);
        }

        switch (op & 0xFFC0)
        {
            case 0x4E80: return Control("JSR", mode, reg, isCall: true);
            case 0x4EC0: return Control("JMP", mode, reg, isCall: false);
            case 0x4840: { string ea = Ea(mode, reg, Size.L, CtrlModes); return Make("PEA", ea); }
            case 0x4880: return Movem(false, Size.W);
            case 0x48C0: return Movem(false, Size.L);
            case 0x4C80: return Movem(true, Size.W);
            case 0x4CC0: return Movem(true, Size.L);
            case 0x40C0: { string ea = Ea(mode, reg, Size.W, DataAlt); return Make("MOVE", $"SR,{ea}"); }
            case 0x44C0: { string ea = Ea(mode, reg, Size.W, Data); return Make("MOVE", $"{ea},CCR"); }
            case 0x46C0: { string ea = Ea(mode, reg, Size.W, Data); return Make("MOVE", $"{ea},SR"); }
            case 0x4800: { string ea = Ea(mode, reg, Size.B, DataAlt); return Make("NBCD", ea); }
            case 0x4AC0: { string ea = Ea(mode, reg, Size.B, DataAlt); return Make("TAS", ea); }
        }

        string? name = (op & 0xFF00) switch
        {
            0x4000 => "NEGX", 0x4200 => "CLR", 0x4400 => "NEG", 0x4600 => "NOT", 0x4A00 => "TST", _ => null
        };
        if (name == null || !TryDataSize((op >> 6) & 3, out Size sz)) throw new BadOpcodeException();
        string dest = Ea(mode, reg, sz, DataAlt);
        return Make(name + Sfx(sz), dest);
    }

    private Instruction Control(string name, int mode, int reg, bool isCall)
    {
        eaAbs = null; eaPcRel = null;
        string ea = Ea(mode, reg, Size.L, CtrlModes);
        uint? target = eaAbs ?? eaPcRel;
        if (target.HasValue)
            return Make(name, ea, isCall ? FlowKind.Call : FlowKind.Jump, target);
        return Make(name, ea, isCall ? FlowKind.IndirectCall : FlowKind.IndirectJump);
    }

    private Instruction Movem(bool toRegs, Size sz)
    {
        int mode = (op >> 3) & 7, reg = op & 7;
        ushort mask = Next16();                      // the mask word comes BEFORE the EA extension words
        string ea = Ea(mode, reg, sz, toRegs ? MovemLoad : MovemStore);
        string regs = RegList(mask, reversed: mode == 4 && !toRegs);
        return Make("MOVEM" + Sfx(sz), toRegs ? $"{ea},{regs}" : $"{regs},{ea}");
    }

    /// <summary>Formats a MOVEM register mask as "D0-D3/A0-A2". Pre-decrement masks are bit-reversed.</summary>
    private static string RegList(ushort mask, bool reversed)
    {
        var has = new bool[16];
        for (int i = 0; i < 16; i++)
            if ((mask & (1 << i)) != 0) has[reversed ? 15 - i : i] = true;

        var parts = new List<string>();
        for (int g = 0; g < 2; g++)
        {
            char c = g == 0 ? 'D' : 'A';
            int i = 0;
            while (i < 8)
            {
                if (!has[g * 8 + i]) { i++; continue; }
                int j = i;
                while (j + 1 < 8 && has[g * 8 + j + 1]) j++;
                parts.Add(j == i ? $"{c}{i}" : $"{c}{i}-{c}{j}");
                i = j + 1;
            }
        }
        return string.Join("/", parts);
    }

    // ---------- group 5: ADDQ / SUBQ / Scc / DBcc ----------

    private Instruction Group5()
    {
        int mode = (op >> 3) & 7, reg = op & 7;
        int cond = (op >> 8) & 0xF;

        if (((op >> 6) & 3) == 3)
        {
            if (mode == 1)
            {
                uint dispAddr = pos;
                short d = (short)Next16();
                uint t = (uint)(dispAddr + d);
                string name = cond == 1 ? "DBRA" : "DB" + Cond[cond];
                return Make(name, $"D{reg},${t:X8}", FlowKind.Branch, t);
            }
            string ea = Ea(mode, reg, Size.B, DataAlt);
            return Make("S" + Cond[cond], ea);
        }

        TryDataSize((op >> 6) & 3, out Size sz);
        int data = (op >> 9) & 7;
        if (data == 0) data = 8;
        if (sz == Size.B && mode == 1) throw new BadOpcodeException();
        string dest = Ea(mode, reg, sz, Alterable);
        return Make(((op & 0x0100) != 0 ? "SUBQ" : "ADDQ") + Sfx(sz), $"#{data},{dest}");
    }

    // ---------- group 6: Bcc / BRA / BSR ----------

    private Instruction Group6()
    {
        int cond = (op >> 8) & 0xF;
        int d8 = op & 0xFF;
        uint baseAddr = start + 2;
        int disp;
        string sfx;
        if (d8 == 0) { disp = (short)Next16(); sfx = ".W"; }
        else if (d8 == 0xFF) throw new BadOpcodeException();     // 32-bit displacement is 68020+
        else { disp = (sbyte)d8; sfx = ".B"; }

        uint t = (uint)(baseAddr + disp);
        return cond switch
        {
            0 => Make("BRA" + sfx, $"${t:X8}", FlowKind.Jump, t),
            1 => Make("BSR" + sfx, $"${t:X8}", FlowKind.Call, t),
            _ => Make("B" + Cond[cond] + sfx, $"${t:X8}", FlowKind.Branch, t)
        };
    }

    // ---------- group 7: MOVEQ ----------

    private Instruction Moveq()
    {
        if ((op & 0x0100) != 0) throw new BadOpcodeException();
        return Make("MOVEQ", $"#{SDisp((sbyte)(op & 0xFF))},D{(op >> 9) & 7}");
    }

    // ---------- groups 8 and C: OR/AND, DIV, MUL, BCD, EXG ----------

    private Instruction Group8()
    {
        int mode = (op >> 3) & 7, reg = op & 7, dn = (op >> 9) & 7;
        int opmode = (op >> 6) & 7;
        if (opmode == 3) return Make("DIVU.W", $"{Ea(mode, reg, Size.W, Data)},D{dn}");
        if (opmode == 7) return Make("DIVS.W", $"{Ea(mode, reg, Size.W, Data)},D{dn}");
        if (opmode == 4 && mode <= 1) return Make("SBCD", mode == 0 ? $"D{reg},D{dn}" : $"-(A{reg}),-(A{dn})");
        return LogicOp("OR", opmode, mode, reg, dn);
    }

    private Instruction GroupC()
    {
        int mode = (op >> 3) & 7, reg = op & 7, dn = (op >> 9) & 7;
        int opmode = (op >> 6) & 7;
        if (opmode == 3) return Make("MULU.W", $"{Ea(mode, reg, Size.W, Data)},D{dn}");
        if (opmode == 7) return Make("MULS.W", $"{Ea(mode, reg, Size.W, Data)},D{dn}");
        if (opmode == 4 && mode <= 1) return Make("ABCD", mode == 0 ? $"D{reg},D{dn}" : $"-(A{reg}),-(A{dn})");
        if (opmode == 5 && mode == 0) return Make("EXG", $"D{dn},D{reg}");
        if (opmode == 5 && mode == 1) return Make("EXG", $"A{dn},A{reg}");
        if (opmode == 6 && mode == 1) return Make("EXG", $"D{dn},A{reg}");
        return LogicOp("AND", opmode, mode, reg, dn);
    }

    private Instruction LogicOp(string name, int opmode, int mode, int reg, int dn)
    {
        TryDataSize(opmode & 3, out Size sz);
        int allowed = opmode < 3 ? (name is "OR" or "AND" ? Data : sz == Size.B ? Data : All) : MemAlt;
        string ea = Ea(mode, reg, sz, allowed);
        return opmode < 3
            ? Make(name + Sfx(sz), $"{ea},D{dn}")
            : Make(name + Sfx(sz), $"D{dn},{ea}");
    }

    // ---------- groups 9 and D: SUB / ADD ----------

    private Instruction AddSub(string name)
    {
        int mode = (op >> 3) & 7, reg = op & 7, dn = (op >> 9) & 7;
        int opmode = (op >> 6) & 7;
        if (opmode == 3) return Make(name + "A.W", $"{Ea(mode, reg, Size.W)},A{dn}");
        if (opmode == 7) return Make(name + "A.L", $"{Ea(mode, reg, Size.L)},A{dn}");
        if (opmode >= 4 && mode <= 1)
        {
            TryDataSize(opmode & 3, out Size xs);
            return Make(name + "X" + Sfx(xs), mode == 0 ? $"D{reg},D{dn}" : $"-(A{reg}),-(A{dn})");
        }
        return LogicOp(name, opmode, mode, reg, dn);
    }

    // ---------- group B: CMP / CMPA / CMPM / EOR ----------

    private Instruction GroupB()
    {
        int mode = (op >> 3) & 7, reg = op & 7, dn = (op >> 9) & 7;
        int opmode = (op >> 6) & 7;
        if (opmode == 3) return Make("CMPA.W", $"{Ea(mode, reg, Size.W)},A{dn}");
        if (opmode == 7) return Make("CMPA.L", $"{Ea(mode, reg, Size.L)},A{dn}");
        TryDataSize(opmode & 3, out Size sz);
        if (opmode < 3) return Make("CMP" + Sfx(sz), $"{Ea(mode, reg, sz, sz == Size.B ? Data : All)},D{dn}");
        if (mode == 1) return Make("CMPM" + Sfx(sz), $"(A{reg})+,(A{dn})+");
        return Make("EOR" + Sfx(sz), $"D{dn},{Ea(mode, reg, sz, DataAlt)}");
    }

    // ---------- group E: shifts and rotates ----------

    private Instruction Shifts()
    {
        string[] types = { "AS", "LS", "ROX", "RO" };
        string dir = (op & 0x0100) != 0 ? "L" : "R";
        int mode = (op >> 3) & 7, reg = op & 7;

        if (((op >> 6) & 3) == 3)
        {
            if ((op & 0x0800) != 0) throw new BadOpcodeException();   // bit-field ops are 68020+
            string ea = Ea(mode, reg, Size.W, MemAlt);
            return Make(types[(op >> 9) & 3] + dir + ".W", ea);
        }

        TryDataSize((op >> 6) & 3, out Size sz);
        int cnt = (op >> 9) & 7;
        string count = (op & 0x20) != 0 ? $"D{cnt}" : $"#{(cnt == 0 ? 8 : cnt)}";
        return Make(types[(op >> 3) & 3] + dir + Sfx(sz), $"{count},D{reg}");
    }
}
