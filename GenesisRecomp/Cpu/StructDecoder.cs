namespace GenesisRecomp.Cpu;

/// <summary>
/// Motorola 68000 decoder that returns structured <see cref="DecodedInst"/> values.
/// It follows the same decoding rules as <see cref="Decoder"/> (which stays as the reference);
/// Tools/DecoderVerify.cs proves the two agree.
/// </summary>
public static class StructDecoder
{
    public static DecodedInst Decode(byte[] rom, uint address)
    {
        var d = new SDec(rom, address);
        try { return d.Run(); }
        catch (BadOpcodeException) { return d.Invalid(); }
    }
}

internal sealed class SDec
{
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
    private uint? eaAbs, eaPcRel, eaPcIdx;

    public SDec(byte[] rom, uint address) { this.rom = rom; start = address; pos = address; }

    public DecodedInst Run()
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
            0x9 => AddSub(Mn.SUB),
            0xB => GroupB(),
            0xC => GroupC(),
            0xD => AddSub(Mn.ADD),
            0xE => Shifts(),
            _ => throw new BadOpcodeException()
        };
    }

    public DecodedInst Invalid()
    {
        bool known = start + 1 < rom.Length;
        return new DecodedInst
        {
            Address = start, Length = 2, Op = Mn.Invalid, Flow = FlowKind.Halt, IsValid = false,
            WordKnown = known, OpWord = known ? (ushort)((rom[start] << 8) | rom[start + 1]) : (ushort)0
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

    private DecodedInst Make(Mn mn, Operand src = default, Operand dst = default, Size size = Size.L, bool sized = false,
                             FlowKind flow = FlowKind.Normal, uint? target = null, int cond = 0) =>
        new()
        {
            Address = start, Length = (int)(pos - start), OpWord = op, Op = mn, Size = size, Sized = sized, Cond = cond,
            Src = src, Dst = dst, Flow = flow, Target = target, PcIndexedBase = eaPcIdx, PcRelAddress = eaPcRel
        };

    private static bool TryDataSize(int bits, out Size s)
    {
        s = bits switch { 0 => Size.B, 1 => Size.W, _ => Size.L };
        return bits != 3;
    }

    private Operand ImmOp(Size s) => s switch
    {
        Size.B => Operand.Imm((uint)(Next16() & 0xFF), Size.B),
        Size.W => Operand.Imm(Next16(), Size.W),
        _ => Operand.Imm(Next32(), Size.L)
    };

    // ---------- effective addresses ----------

    private Operand Ea(int mode, int reg, Size sz, int allowed = All)
    {
        int bit = mode < 7 ? 1 << mode : reg <= 4 ? 1 << (7 + reg) : 0;
        if ((allowed & bit) == 0) throw new BadOpcodeException();
        switch (mode)
        {
            case 0: return Operand.D(reg);
            case 1: return Operand.A(reg);
            case 2: return new Operand { Kind = OpKind.Ind, Reg = reg };
            case 3: return new Operand { Kind = OpKind.PostInc, Reg = reg };
            case 4: return new Operand { Kind = OpKind.PreDec, Reg = reg };
            case 5:
            {
                short d = (short)Next16();
                return new Operand { Kind = OpKind.Disp, Reg = reg, Disp = d };
            }
            case 6: return Indexed(reg, false);
            case 7:
                switch (reg)
                {
                    case 0:
                    {
                        short a = (short)Next16();
                        eaAbs = (uint)(int)a;
                        return new Operand { Kind = OpKind.AbsW, Value = (uint)(int)a };
                    }
                    case 1:
                    {
                        uint a = Next32();
                        eaAbs = a;
                        return new Operand { Kind = OpKind.AbsL, Value = a };
                    }
                    case 2:
                    {
                        uint basePc = pos;
                        short d = (short)Next16();
                        uint t = (uint)(basePc + d);
                        eaPcRel = t;
                        return new Operand { Kind = OpKind.PcDisp, Value = t };
                    }
                    case 3: return Indexed(0, true);
                    case 4: return ImmOp(sz);
                    default: throw new BadOpcodeException();
                }
            default: throw new BadOpcodeException();
        }
    }

    private Operand Indexed(int baseReg, bool isPc)
    {
        uint basePc = pos;
        ushort e = Next16();
        if ((e & 0x0100) != 0) throw new BadOpcodeException();   // 68020 full format
        int d8 = (sbyte)(e & 0xFF);
        int xReg = (e >> 12) & 7;
        bool xAddr = (e & 0x8000) != 0, xLong = (e & 0x0800) != 0;
        if (isPc)
        {
            uint t = (uint)(basePc + d8);
            eaPcRel = null;
            eaPcIdx = t;
            return new Operand { Kind = OpKind.PcIndex, Value = t, IndexReg = xReg, IndexIsAddr = xAddr, IndexLong = xLong };
        }
        return new Operand { Kind = OpKind.Index, Reg = baseReg, Disp = d8, IndexReg = xReg, IndexIsAddr = xAddr, IndexLong = xLong };
    }

    // ---------- group 0: immediate and bit operations ----------

    private static readonly Mn[] BitOps = { Mn.BTST, Mn.BCHG, Mn.BCLR, Mn.BSET };
    private static readonly Mn[] ImmOps = { Mn.ORI, Mn.ANDI, Mn.SUBI, Mn.ADDI, Mn.Invalid, Mn.EORI, Mn.CMPI, Mn.Invalid };

    private DecodedInst Group0()
    {
        int mode = (op >> 3) & 7, reg = op & 7;

        if ((op & 0x0100) != 0)
        {
            if (mode == 1) return Movep();
            Size s = mode == 0 ? Size.L : Size.B;
            Operand ea = Ea(mode, reg, s, ((op >> 6) & 3) == 0 ? Data : DataAlt);
            return Make(BitOps[(op >> 6) & 3], Operand.D((op >> 9) & 7), ea, s, sized: true);
        }

        int kind = (op >> 9) & 7;
        if (kind == 4)
        {
            Size s = mode == 0 ? Size.L : Size.B;
            ushort bitWord = Next16();
            if ((bitWord & 0xFF00) != 0) throw new BadOpcodeException();
            Operand ea = Ea(mode, reg, s, ((op >> 6) & 3) == 0 ? DataNoImm : DataAlt);
            return Make(BitOps[(op >> 6) & 3], Operand.Quick(bitWord), ea, s, sized: true);
        }

        Mn name = ImmOps[kind];
        if (name == Mn.Invalid) throw new BadOpcodeException();

        if (op == 0x003C || op == 0x023C || op == 0x0A3C)
        {
            ushort v = Next16();
            if ((v & 0xFF00) != 0) throw new BadOpcodeException();
            return Make(name, Operand.Imm(v, Size.B), Operand.Of(OpKind.Ccr));
        }
        if (op == 0x007C || op == 0x027C || op == 0x0A7C)
            return Make(name, Operand.Imm(Next16(), Size.W), Operand.Of(OpKind.Sr));

        if (!TryDataSize((op >> 6) & 3, out Size sz)) throw new BadOpcodeException();
        Operand imm = ImmOp(sz);
        Operand dest = Ea(mode, reg, sz, DataAlt);
        return Make(name, imm, dest, sz, sized: true);
    }

    private DecodedInst Movep()
    {
        int an = op & 7, dn = (op >> 9) & 7;
        int opmode = (op >> 6) & 7;
        if (opmode < 4) throw new BadOpcodeException();
        short d = (short)Next16();
        Size s = (opmode & 1) == 0 ? Size.W : Size.L;
        var mem = new Operand { Kind = OpKind.Disp, Reg = an, Disp = d };
        return (opmode & 2) == 0
            ? Make(Mn.MOVEP, mem, Operand.D(dn), s, sized: true)
            : Make(Mn.MOVEP, Operand.D(dn), mem, s, sized: true);
    }

    // ---------- groups 1-3: MOVE / MOVEA ----------

    private DecodedInst Move()
    {
        Size sz = (op >> 12) switch { 1 => Size.B, 3 => Size.W, _ => Size.L };
        Operand src = Ea((op >> 3) & 7, op & 7, sz, sz == Size.B ? Data : All);
        int dMode = (op >> 6) & 7, dReg = (op >> 9) & 7;
        if (dMode == 1)
        {
            if (sz == Size.B) throw new BadOpcodeException();
            return Make(Mn.MOVEA, src, Operand.A(dReg), sz, sized: true);
        }
        Operand dst = Ea(dMode, dReg, sz, DataAlt);
        return Make(Mn.MOVE, src, dst, sz, sized: true);
    }

    // ---------- group 4: miscellaneous ----------

    private DecodedInst Group4()
    {
        int mode = (op >> 3) & 7, reg = op & 7;

        if ((op & 0xF1C0) == 0x41C0)
        {
            Operand ea = Ea(mode, reg, Size.L, CtrlModes);
            return Make(Mn.LEA, ea, Operand.A((op >> 9) & 7));
        }
        if ((op & 0xF1C0) == 0x4180)
        {
            Operand ea = Ea(mode, reg, Size.W, Data);
            return Make(Mn.CHK, ea, Operand.D((op >> 9) & 7), Size.W, sized: true);
        }
        if ((op & 0xFFF0) == 0x4E40) return Make(Mn.TRAP, dst: Operand.Quick(op & 0xF));

        switch (op & 0xFFF8)
        {
            case 0x4E50: { short d = (short)Next16(); return Make(Mn.LINK, Operand.A(reg), Operand.ImmSigned(d)); }
            case 0x4E58: return Make(Mn.UNLK, dst: Operand.A(reg));
            case 0x4E60: return Make(Mn.MOVE, Operand.A(reg), Operand.Of(OpKind.Usp));
            case 0x4E68: return Make(Mn.MOVE, Operand.Of(OpKind.Usp), Operand.A(reg));
            case 0x4840: return Make(Mn.SWAP, dst: Operand.D(reg));
            case 0x4880: return Make(Mn.EXT, dst: Operand.D(reg), size: Size.W, sized: true);
            case 0x48C0: return Make(Mn.EXT, dst: Operand.D(reg), size: Size.L, sized: true);
        }

        switch (op)
        {
            case 0x4E70: return Make(Mn.RESET);
            case 0x4E71: return Make(Mn.NOP);
            case 0x4E72: return Make(Mn.STOP, dst: Operand.Imm(Next16(), Size.W));
            case 0x4E73: return Make(Mn.RTE, flow: FlowKind.Return);
            case 0x4E75: return Make(Mn.RTS, flow: FlowKind.Return);
            case 0x4E76: return Make(Mn.TRAPV);
            case 0x4E77: return Make(Mn.RTR, flow: FlowKind.Return);
            case 0x4AFC: return Make(Mn.ILLEGAL, flow: FlowKind.Halt);
        }

        switch (op & 0xFFC0)
        {
            case 0x4E80: return Control(Mn.JSR, mode, reg, isCall: true);
            case 0x4EC0: return Control(Mn.JMP, mode, reg, isCall: false);
            case 0x4840: { Operand ea = Ea(mode, reg, Size.L, CtrlModes); return Make(Mn.PEA, dst: ea); }
            case 0x4880: return Movem(false, Size.W);
            case 0x48C0: return Movem(false, Size.L);
            case 0x4C80: return Movem(true, Size.W);
            case 0x4CC0: return Movem(true, Size.L);
            case 0x40C0: { Operand ea = Ea(mode, reg, Size.W, DataAlt); return Make(Mn.MOVE, Operand.Of(OpKind.Sr), ea); }
            case 0x44C0: { Operand ea = Ea(mode, reg, Size.W, Data); return Make(Mn.MOVE, ea, Operand.Of(OpKind.Ccr)); }
            case 0x46C0: { Operand ea = Ea(mode, reg, Size.W, Data); return Make(Mn.MOVE, ea, Operand.Of(OpKind.Sr)); }
            case 0x4800: { Operand ea = Ea(mode, reg, Size.B, DataAlt); return Make(Mn.NBCD, dst: ea); }
            case 0x4AC0: { Operand ea = Ea(mode, reg, Size.B, DataAlt); return Make(Mn.TAS, dst: ea); }
        }

        Mn? name = (op & 0xFF00) switch
        {
            0x4000 => Mn.NEGX, 0x4200 => Mn.CLR, 0x4400 => Mn.NEG, 0x4600 => Mn.NOT, 0x4A00 => Mn.TST, _ => null
        };
        if (name == null || !TryDataSize((op >> 6) & 3, out Size sz)) throw new BadOpcodeException();
        Operand dest = Ea(mode, reg, sz, DataAlt);
        return Make(name.Value, dst: dest, size: sz, sized: true);
    }

    private DecodedInst Control(Mn mn, int mode, int reg, bool isCall)
    {
        eaAbs = null; eaPcRel = null;
        Operand ea = Ea(mode, reg, Size.L, CtrlModes);
        uint? target = eaAbs ?? eaPcRel;
        if (target.HasValue)
            return Make(mn, dst: ea, flow: isCall ? FlowKind.Call : FlowKind.Jump, target: target);
        return Make(mn, dst: ea, flow: isCall ? FlowKind.IndirectCall : FlowKind.IndirectJump);
    }

    private DecodedInst Movem(bool toRegs, Size sz)
    {
        int mode = (op >> 3) & 7, reg = op & 7;
        ushort mask = Next16();                      // the mask word comes BEFORE the EA extension words
        Operand ea = Ea(mode, reg, sz, toRegs ? MovemLoad : MovemStore);
        bool reversed = mode == 4 && !toRegs;        // pre-decrement masks are stored bit-reversed
        int norm = 0;
        for (int i = 0; i < 16; i++)
            if ((mask & (1 << i)) != 0) norm |= 1 << (reversed ? 15 - i : i);
        var list = new Operand { Kind = OpKind.RegList, Value = (uint)norm };
        return toRegs ? Make(Mn.MOVEM, ea, list, sz, sized: true) : Make(Mn.MOVEM, list, ea, sz, sized: true);
    }

    // ---------- group 5: ADDQ / SUBQ / Scc / DBcc ----------

    private DecodedInst Group5()
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
                return Make(Mn.Dbcc, Operand.D(reg), Operand.Target(t), flow: FlowKind.Branch, target: t, cond: cond);
            }
            Operand ea = Ea(mode, reg, Size.B, DataAlt);
            return Make(Mn.Scc, dst: ea, cond: cond);
        }

        TryDataSize((op >> 6) & 3, out Size sz);
        int data = (op >> 9) & 7;
        if (data == 0) data = 8;
        if (sz == Size.B && mode == 1) throw new BadOpcodeException();
        Operand dest = Ea(mode, reg, sz, Alterable);
        return Make((op & 0x0100) != 0 ? Mn.SUBQ : Mn.ADDQ, Operand.Quick(data), dest, sz, sized: true);
    }

    // ---------- group 6: Bcc / BRA / BSR ----------

    private DecodedInst Group6()
    {
        int cond = (op >> 8) & 0xF;
        int d8 = op & 0xFF;
        uint baseAddr = start + 2;
        int disp;
        Size sz;
        if (d8 == 0) { disp = (short)Next16(); sz = Size.W; }
        else if (d8 == 0xFF) throw new BadOpcodeException();
        else { disp = (sbyte)d8; sz = Size.B; }

        uint t = (uint)(baseAddr + disp);
        Operand tgt = Operand.Target(t);
        return cond switch
        {
            0 => Make(Mn.BRA, dst: tgt, size: sz, sized: true, flow: FlowKind.Jump, target: t),
            1 => Make(Mn.BSR, dst: tgt, size: sz, sized: true, flow: FlowKind.Call, target: t),
            _ => Make(Mn.Bcc, dst: tgt, size: sz, sized: true, flow: FlowKind.Branch, target: t, cond: cond)
        };
    }

    // ---------- group 7: MOVEQ ----------

    private DecodedInst Moveq()
    {
        if ((op & 0x0100) != 0) throw new BadOpcodeException();
        return Make(Mn.MOVEQ, Operand.ImmSigned((sbyte)(op & 0xFF)), Operand.D((op >> 9) & 7));
    }

    // ---------- groups 8 and C: OR/AND, DIV, MUL, BCD, EXG ----------

    private static Operand PreDec(int r) => new() { Kind = OpKind.PreDec, Reg = r };
    private static Operand PostInc(int r) => new() { Kind = OpKind.PostInc, Reg = r };

    private DecodedInst Group8()
    {
        int mode = (op >> 3) & 7, reg = op & 7, dn = (op >> 9) & 7;
        int opmode = (op >> 6) & 7;
        if (opmode == 3) return Make(Mn.DIVU, Ea(mode, reg, Size.W, Data), Operand.D(dn), Size.W, sized: true);
        if (opmode == 7) return Make(Mn.DIVS, Ea(mode, reg, Size.W, Data), Operand.D(dn), Size.W, sized: true);
        if (opmode == 4 && mode <= 1)
            return mode == 0 ? Make(Mn.SBCD, Operand.D(reg), Operand.D(dn)) : Make(Mn.SBCD, PreDec(reg), PreDec(dn));
        return LogicOp(Mn.OR, opmode, mode, reg, dn);
    }

    private DecodedInst GroupC()
    {
        int mode = (op >> 3) & 7, reg = op & 7, dn = (op >> 9) & 7;
        int opmode = (op >> 6) & 7;
        if (opmode == 3) return Make(Mn.MULU, Ea(mode, reg, Size.W, Data), Operand.D(dn), Size.W, sized: true);
        if (opmode == 7) return Make(Mn.MULS, Ea(mode, reg, Size.W, Data), Operand.D(dn), Size.W, sized: true);
        if (opmode == 4 && mode <= 1)
            return mode == 0 ? Make(Mn.ABCD, Operand.D(reg), Operand.D(dn)) : Make(Mn.ABCD, PreDec(reg), PreDec(dn));
        if (opmode == 5 && mode == 0) return Make(Mn.EXG, Operand.D(dn), Operand.D(reg));
        if (opmode == 5 && mode == 1) return Make(Mn.EXG, Operand.A(dn), Operand.A(reg));
        if (opmode == 6 && mode == 1) return Make(Mn.EXG, Operand.D(dn), Operand.A(reg));
        return LogicOp(Mn.AND, opmode, mode, reg, dn);
    }

    private DecodedInst LogicOp(Mn name, int opmode, int mode, int reg, int dn)
    {
        TryDataSize(opmode & 3, out Size sz);
        int allowed = opmode < 3 ? (name is Mn.OR or Mn.AND ? Data : sz == Size.B ? Data : All) : MemAlt;
        Operand ea = Ea(mode, reg, sz, allowed);
        return opmode < 3
            ? Make(name, ea, Operand.D(dn), sz, sized: true)
            : Make(name, Operand.D(dn), ea, sz, sized: true);
    }

    // ---------- groups 9 and D: SUB / ADD ----------

    private DecodedInst AddSub(Mn name)
    {
        int mode = (op >> 3) & 7, reg = op & 7, dn = (op >> 9) & 7;
        int opmode = (op >> 6) & 7;
        bool add = name == Mn.ADD;
        if (opmode == 3) return Make(add ? Mn.ADDA : Mn.SUBA, Ea(mode, reg, Size.W), Operand.A(dn), Size.W, sized: true);
        if (opmode == 7) return Make(add ? Mn.ADDA : Mn.SUBA, Ea(mode, reg, Size.L), Operand.A(dn), Size.L, sized: true);
        if (opmode >= 4 && mode <= 1)
        {
            TryDataSize(opmode & 3, out Size xs);
            Mn x = add ? Mn.ADDX : Mn.SUBX;
            return mode == 0
                ? Make(x, Operand.D(reg), Operand.D(dn), xs, sized: true)
                : Make(x, PreDec(reg), PreDec(dn), xs, sized: true);
        }
        return LogicOp(name, opmode, mode, reg, dn);
    }

    // ---------- group B: CMP / CMPA / CMPM / EOR ----------

    private DecodedInst GroupB()
    {
        int mode = (op >> 3) & 7, reg = op & 7, dn = (op >> 9) & 7;
        int opmode = (op >> 6) & 7;
        if (opmode == 3) return Make(Mn.CMPA, Ea(mode, reg, Size.W), Operand.A(dn), Size.W, sized: true);
        if (opmode == 7) return Make(Mn.CMPA, Ea(mode, reg, Size.L), Operand.A(dn), Size.L, sized: true);
        TryDataSize(opmode & 3, out Size sz);
        if (opmode < 3) return Make(Mn.CMP, Ea(mode, reg, sz, sz == Size.B ? Data : All), Operand.D(dn), sz, sized: true);
        if (mode == 1) return Make(Mn.CMPM, PostInc(reg), PostInc(dn), sz, sized: true);
        return Make(Mn.EOR, Operand.D(dn), Ea(mode, reg, sz, DataAlt), sz, sized: true);
    }

    // ---------- group E: shifts and rotates ----------

    private static Mn ShiftMn(int type, bool left) => (Mn)((int)Mn.ASR + type * 2 + (left ? 1 : 0));

    private DecodedInst Shifts()
    {
        bool left = (op & 0x0100) != 0;
        int mode = (op >> 3) & 7, reg = op & 7;

        if (((op >> 6) & 3) == 3)
        {
            if ((op & 0x0800) != 0) throw new BadOpcodeException();
            Operand ea = Ea(mode, reg, Size.W, MemAlt);
            return Make(ShiftMn((op >> 9) & 3, left), dst: ea, size: Size.W, sized: true);
        }

        TryDataSize((op >> 6) & 3, out Size sz);
        int cnt = (op >> 9) & 7;
        Operand count = (op & 0x20) != 0 ? Operand.D(cnt) : Operand.Quick(cnt == 0 ? 8 : cnt);
        return Make(ShiftMn((op >> 3) & 3, left), count, Operand.D(reg), sz, sized: true);
    }
}
