namespace GenesisRecomp.Cpu;

public sealed partial class Cpu68k
{
    private void Execute()
    {
        switch (op >> 12)
        {
            case 0x0: Group0(); break;
            case 0x1: case 0x2: case 0x3: DoMove(); break;
            case 0x4: Group4(); break;
            case 0x5: Group5(); break;
            case 0x6: Group6(); break;
            case 0x7:
                if ((op & 0x0100) != 0) throw Illegal;
                D[(op >> 9) & 7] = (uint)(int)(sbyte)(op & 0xFF);
                LogicFlags(D[(op >> 9) & 7], Size.L);
                break;
            case 0x8: Group8(); break;
            case 0x9: AddSub(true); break;
            case 0xA: RaiseException(10, instrStart); break;
            case 0xB: GroupB(); break;
            case 0xC: GroupC(); break;
            case 0xD: AddSub(false); break;
            case 0xE: Shifts(); break;
            default: RaiseException(11, instrStart); break;
        }
    }

    // ================= group 0: immediate and bit operations =================

    private void Group0()
    {
        int mode = (op >> 3) & 7, reg = op & 7;

        if ((op & 0x0100) != 0)
        {
            if (mode == 1) { Movep(); return; }
            BitOp((op >> 6) & 3, (int)D[(op >> 9) & 7], mode, reg, isStatic: false);
            return;
        }

        int kind = (op >> 9) & 7;
        if (kind == 4)
        {
            ushort w = Fetch16();
            if ((w & 0xFF00) != 0) throw Illegal;
            BitOp((op >> 6) & 3, w, mode, reg, isStatic: true);
            return;
        }
        if (kind == 7) throw Illegal;

        // ORI/ANDI/EORI to CCR and SR
        if (op == 0x003C || op == 0x023C || op == 0x0A3C)
        {
            ushort w = Fetch16();
            if ((w & 0xFF00) != 0) throw Illegal;
            int ccr = sr & 0x1F;
            ccr = kind == 0 ? ccr | w : kind == 1 ? ccr & w : ccr ^ w;
            sr = (ushort)((sr & 0xFF00) | (ccr & 0x1F));
            return;
        }
        if (op == 0x007C || op == 0x027C || op == 0x0A7C)
        {
            RequireSupervisor();
            ushort w = Fetch16();
            SetSr(kind == 0 ? (ushort)(sr | w) : kind == 1 ? (ushort)(sr & w) : (ushort)(sr ^ w));
            return;
        }

        int sbits = (op >> 6) & 3;
        if (sbits == 3) throw Illegal;
        Size sz = SizeOf(sbits);
        uint imm = sz switch { Size.B => (uint)(Fetch16() & 0xFF), Size.W => Fetch16(), _ => Fetch32() };
        Ea ea = ResolveChecked(mode, reg, sz, DataAlt);
        uint dst = ReadEa(ea, sz);
        switch (kind)
        {
            case 0: { uint r = dst | imm; LogicFlags(r, sz); WriteEa(ea, sz, r); break; }
            case 1: { uint r = dst & imm; LogicFlags(r, sz); WriteEa(ea, sz, r); break; }
            case 2: { uint r = DoSub(imm, dst, sz, false, true); WriteEa(ea, sz, r); break; }
            case 3: { uint r = DoAdd(imm, dst, sz, false); WriteEa(ea, sz, r); break; }
            case 5: { uint r = dst ^ imm; LogicFlags(r, sz); WriteEa(ea, sz, r); break; }
            case 6: DoSub(imm, dst, sz, false, false); break;
        }
    }

    private void BitOp(int type, int bitNumber, int mode, int reg, bool isStatic)
    {
        Size sz = mode == 0 ? Size.L : Size.B;
        int allowed = type == 0 ? (isStatic ? DataNoImm : DataModes) : DataAlt;
        Ea ea = ResolveChecked(mode, reg, sz, allowed);
        int bit = mode == 0 ? bitNumber & 31 : bitNumber & 7;
        uint val = ReadEa(ea, sz);
        SetFlag(FZ, ((val >> bit) & 1) == 0);
        if (type == 0) return;
        uint m = 1u << bit;
        val = type == 1 ? val ^ m : type == 2 ? val & ~m : val | m;
        WriteEa(ea, sz, val);
    }

    private void Movep()
    {
        int an = op & 7, dn = (op >> 9) & 7, opmode = (op >> 6) & 7;
        if (opmode < 4) throw Illegal;
        uint addr = A[an] + (uint)(int)(short)Fetch16();
        bool isLong = (opmode & 1) != 0;
        if ((opmode & 2) == 0)       // memory -> register
        {
            uint v = bus.Read8(addr);
            v = (v << 8) | bus.Read8(addr + 2);
            if (isLong) { v = (v << 8) | bus.Read8(addr + 4); v = (v << 8) | bus.Read8(addr + 6); D[dn] = v; }
            else D[dn] = (D[dn] & 0xFFFF0000) | (v & 0xFFFF);
        }
        else                          // register -> memory
        {
            uint v = D[dn];
            if (isLong)
            {
                bus.Write8(addr, (byte)(v >> 24)); bus.Write8(addr + 2, (byte)(v >> 16));
                bus.Write8(addr + 4, (byte)(v >> 8)); bus.Write8(addr + 6, (byte)v);
            }
            else { bus.Write8(addr, (byte)(v >> 8)); bus.Write8(addr + 2, (byte)v); }
        }
    }

    // ================= groups 1-3: MOVE / MOVEA =================

    private void DoMove()
    {
        Size sz = (op >> 12) switch { 1 => Size.B, 3 => Size.W, _ => Size.L };
        Ea src = ResolveChecked((op >> 3) & 7, op & 7, sz, sz == Size.B ? DataModes : AllModes);
        uint val = ReadEa(src, sz);
        int dMode = (op >> 6) & 7, dReg = (op >> 9) & 7;
        if (dMode == 1)
        {
            if (sz == Size.B) throw Illegal;
            A[dReg] = SignExtend(val, sz);
            return;
        }
        Ea dst = ResolveChecked(dMode, dReg, sz, DataAlt);
        LogicFlags(val, sz);
        WriteEa(dst, sz, val);
    }

    // ================= group 4: miscellaneous =================

    private void Group4()
    {
        int mode = (op >> 3) & 7, reg = op & 7;

        if ((op & 0xF1C0) == 0x41C0)
        {
            A[(op >> 9) & 7] = ResolveChecked(mode, reg, Size.L, CtrlModes).Val;
            return;
        }
        if ((op & 0xF1C0) == 0x4180)
        {
            Ea ea = ResolveChecked(mode, reg, Size.W, DataModes);
            int bound = (short)ReadEa(ea, Size.W);
            int dn = (short)D[(op >> 9) & 7];
            if (dn < 0) { SetFlag(FN, true); RaiseException(6, PC); }
            else if (dn > bound) { SetFlag(FN, false); RaiseException(6, PC); }
            return;
        }
        if ((op & 0xFFF0) == 0x4E40) { RaiseException(32 + (op & 0xF), PC); return; }

        switch (op & 0xFFF8)
        {
            case 0x4E50:
            {
                short d = (short)Fetch16();
                Push32(A[reg]);
                A[reg] = A[7];
                A[7] += (uint)(int)d;
                return;
            }
            case 0x4E58: A[7] = A[reg]; A[reg] = Pop32(); return;
            case 0x4E60: RequireSupervisor(); Usp = A[reg]; return;
            case 0x4E68: RequireSupervisor(); A[reg] = Usp; return;
            case 0x4840:
            {
                uint r = (D[reg] << 16) | (D[reg] >> 16);
                D[reg] = r; LogicFlags(r, Size.L);
                return;
            }
            case 0x4880:
            {
                uint r = (uint)(int)(sbyte)D[reg] & 0xFFFF;
                D[reg] = (D[reg] & 0xFFFF0000) | r; LogicFlags(r, Size.W);
                return;
            }
            case 0x48C0:
            {
                uint r = (uint)(int)(short)D[reg];
                D[reg] = r; LogicFlags(r, Size.L);
                return;
            }
        }

        switch (op)
        {
            case 0x4E70: RequireSupervisor(); return;                       // RESET: no external devices to reset yet
            case 0x4E71: return;                                             // NOP
            case 0x4E72: { RequireSupervisor(); ushort w = Fetch16(); SetSr(w); Stopped = true; return; }
            case 0x4E73:
            {
                RequireSupervisor();
                ushort newSr = Pop16();
                uint newPc = Pop32();
                SetSr(newSr);
                PC = newPc;
                return;
            }
            case 0x4E75: PC = Pop32(); return;
            case 0x4E76: if (V) RaiseException(7, PC); return;
            case 0x4E77:
            {
                ushort ccr = Pop16();
                sr = (ushort)((sr & 0xFF00) | (ccr & 0x1F));
                PC = Pop32();
                return;
            }
            case 0x4AFC: throw Illegal;
        }

        switch (op & 0xFFC0)
        {
            case 0x4E80: { Ea ea = ResolveChecked(mode, reg, Size.L, CtrlModes); Push32(PC); PC = ea.Val; return; }
            case 0x4EC0: { Ea ea = ResolveChecked(mode, reg, Size.L, CtrlModes); PC = ea.Val; return; }
            case 0x4840: { Ea ea = ResolveChecked(mode, reg, Size.L, CtrlModes); Push32(ea.Val); return; }
            case 0x4880: Movem(false, Size.W); return;
            case 0x48C0: Movem(false, Size.L); return;
            case 0x4C80: Movem(true, Size.W); return;
            case 0x4CC0: Movem(true, Size.L); return;
            case 0x40C0: { Ea ea = ResolveChecked(mode, reg, Size.W, DataAlt); WriteEa(ea, Size.W, sr); return; }
            case 0x44C0:
            {
                Ea ea = ResolveChecked(mode, reg, Size.W, DataModes);
                sr = (ushort)((sr & 0xFF00) | (ReadEa(ea, Size.W) & 0x1F));
                return;
            }
            case 0x46C0:
            {
                RequireSupervisor();
                Ea ea = ResolveChecked(mode, reg, Size.W, DataModes);
                SetSr((ushort)ReadEa(ea, Size.W));
                return;
            }
            case 0x4800: { Ea ea = ResolveChecked(mode, reg, Size.B, DataAlt); WriteEa(ea, Size.B, Nbcd(ReadEa(ea, Size.B))); return; }
            case 0x4AC0:
            {
                Ea ea = ResolveChecked(mode, reg, Size.B, DataAlt);
                uint v = ReadEa(ea, Size.B);
                LogicFlags(v, Size.B);
                WriteEa(ea, Size.B, v | 0x80);
                return;
            }
        }

        int sbits = (op >> 6) & 3;
        if (sbits == 3) throw Illegal;
        Size sz = SizeOf(sbits);
        switch (op & 0xFF00)
        {
            case 0x4000:
            {
                Ea ea = ResolveChecked(mode, reg, sz, DataAlt);
                WriteEa(ea, sz, DoSub(ReadEa(ea, sz), 0, sz, true, true));
                return;
            }
            case 0x4200:
            {
                Ea ea = ResolveChecked(mode, reg, sz, DataAlt);
                LogicFlags(0, sz);
                WriteEa(ea, sz, 0);
                return;
            }
            case 0x4400:
            {
                Ea ea = ResolveChecked(mode, reg, sz, DataAlt);
                WriteEa(ea, sz, DoSub(ReadEa(ea, sz), 0, sz, false, true));
                return;
            }
            case 0x4600:
            {
                Ea ea = ResolveChecked(mode, reg, sz, DataAlt);
                uint r = ~ReadEa(ea, sz);
                LogicFlags(r, sz);
                WriteEa(ea, sz, r);
                return;
            }
            case 0x4A00:
            {
                Ea ea = ResolveChecked(mode, reg, sz, DataAlt);
                LogicFlags(ReadEa(ea, sz), sz);
                return;
            }
        }
        throw Illegal;
    }

    private void Movem(bool toRegs, Size sz)
    {
        int mode = (op >> 3) & 7, reg = op & 7;
        ushort mask = Fetch16();                       // the mask comes before the EA extension words
        int step = Bytes(sz);
        int allowed = toRegs ? MovemLoad : MovemStore;
        int bitSel = mode < 7 ? 1 << mode : reg <= 4 ? 1 << (7 + reg) : 0;
        if ((allowed & bitSel) == 0) throw Illegal;

        if (!toRegs && mode == 4)                      // registers -> -(An): mask is reversed, A7 first
        {
            uint addr = A[reg];
            uint initial = A[reg];
            for (int i = 0; i < 16; i++)
            {
                if ((mask & (1 << i)) == 0) continue;
                int r = 15 - i;                        // bit 0 = A7 ... bit 15 = D0
                uint val = r < 8 ? D[r] : (r - 8 == reg ? initial : A[r - 8]);
                addr -= (uint)step;
                Write(addr, sz, val);
            }
            A[reg] = addr;
            return;
        }

        uint a;
        if (mode == 3) a = A[reg];
        else a = Resolve(mode, reg, sz).Val;

        for (int i = 0; i < 16; i++)
        {
            if ((mask & (1 << i)) == 0) continue;
            if (toRegs)
            {
                uint v = SignExtend(Read(a, sz), sz);
                if (i < 8) D[i] = v; else A[i - 8] = v;
            }
            else
            {
                Write(a, sz, i < 8 ? D[i] : A[i - 8]);
            }
            a += (uint)step;
        }
        if (mode == 3) A[reg] = a;
    }

    private uint Nbcd(uint dst)
    {
        int lo = -(int)(dst & 0x0F) - (X ? 1 : 0);
        int r = lo - (int)(dst & 0xF0);
        if (lo < 0) r -= 6;
        bool carry = r < 0;
        if (carry) r -= 0x60;
        uint result = (uint)r & 0xFF;
        if (result != 0) sr &= unchecked((ushort)~FZ);
        SetFlag(FC, carry); SetFlag(FX, carry);
        return result;
    }

    // ================= group 5: ADDQ / SUBQ / Scc / DBcc =================

    private void Group5()
    {
        int mode = (op >> 3) & 7, reg = op & 7, cc = (op >> 8) & 0xF;

        if (((op >> 6) & 3) == 3)
        {
            if (mode == 1)   // DBcc
            {
                uint basePc = PC;
                short d = (short)Fetch16();
                if (!Cond(cc))
                {
                    ushort cnt = (ushort)(D[reg] - 1);
                    D[reg] = (D[reg] & 0xFFFF0000) | cnt;
                    if (cnt != 0xFFFF) PC = basePc + (uint)(int)d;
                }
                return;
            }
            Ea ea = ResolveChecked(mode, reg, Size.B, DataAlt);
            WriteEa(ea, Size.B, Cond(cc) ? 0xFFu : 0u);
            return;
        }

        Size sz = SizeOf((op >> 6) & 3);
        uint data = (uint)((op >> 9) & 7);
        if (data == 0) data = 8;
        bool isSub = (op & 0x0100) != 0;
        if (mode == 1)   // to An: whole register, no flags
        {
            if (sz == Size.B) throw Illegal;
            A[reg] = isSub ? A[reg] - data : A[reg] + data;
            return;
        }
        Ea dst = ResolveChecked(mode, reg, sz, DataAlt);
        uint cur = ReadEa(dst, sz);
        WriteEa(dst, sz, isSub ? DoSub(data, cur, sz, false, true) : DoAdd(data, cur, sz, false));
    }

    // ================= group 6: Bcc / BRA / BSR =================

    private void Group6()
    {
        int cc = (op >> 8) & 0xF;
        uint baseAddr = PC;                           // PC after the opcode word
        int disp = (sbyte)(op & 0xFF);
        if ((op & 0xFF) == 0) disp = (short)Fetch16();
        else if ((op & 0xFF) == 0xFF) throw Illegal;  // 32-bit displacement: 68020+
        uint target = baseAddr + (uint)disp;
        if (cc == 1) { Push32(PC); PC = target; return; }
        if (cc == 0 || Cond(cc)) PC = target;
    }

    // ================= groups 8 and C: OR/AND, DIV, MUL, BCD, EXG =================

    private void Group8()
    {
        int mode = (op >> 3) & 7, reg = op & 7, dn = (op >> 9) & 7, opmode = (op >> 6) & 7;
        if (opmode == 3 || opmode == 7)
        {
            Ea ea = ResolveChecked(mode, reg, Size.W, DataModes);
            Divide(ReadEa(ea, Size.W), dn, signed: opmode == 7);
            return;
        }
        if (opmode == 4 && mode <= 1) { BcdReg(isAdd: false, mode, reg, dn); return; }
        LogicOp(0, opmode, mode, reg, dn);
    }

    private void GroupC()
    {
        int mode = (op >> 3) & 7, reg = op & 7, dn = (op >> 9) & 7, opmode = (op >> 6) & 7;
        if (opmode == 3 || opmode == 7)
        {
            Ea ea = ResolveChecked(mode, reg, Size.W, DataModes);
            Cycles += 34;
            uint src = ReadEa(ea, Size.W);
            uint r = opmode == 3
                ? (D[dn] & 0xFFFF) * (src & 0xFFFF)
                : (uint)((int)(short)D[dn] * (int)(short)src);
            D[dn] = r;
            LogicFlags(r, Size.L);
            return;
        }
        if (opmode == 4 && mode <= 1) { BcdReg(isAdd: true, mode, reg, dn); return; }
        if (opmode == 5 && mode == 0) { (D[dn], D[reg]) = (D[reg], D[dn]); return; }
        if (opmode == 5 && mode == 1) { (A[dn], A[reg]) = (A[reg], A[dn]); return; }
        if (opmode == 6 && mode == 1) { (D[dn], A[reg]) = (A[reg], D[dn]); return; }
        LogicOp(1, opmode, mode, reg, dn);
    }

    /// <summary>kind 0 = OR, 1 = AND.</summary>
    private void LogicOp(int kind, int opmode, int mode, int reg, int dn)
    {
        if ((opmode & 3) == 3) throw Illegal;
        Size sz = SizeOf(opmode & 3);
        if (opmode < 3)
        {
            Ea ea = ResolveChecked(mode, reg, sz, DataModes);
            uint src = ReadEa(ea, sz);
            uint r = kind == 0 ? D[dn] | src : D[dn] & src;
            LogicFlags(r, sz);
            WriteEa(new Ea { Kind = EaKind.Dn, Val = (uint)dn }, sz, r);
        }
        else
        {
            Ea ea = ResolveChecked(mode, reg, sz, MemAlt);
            uint cur = ReadEa(ea, sz);
            uint r = kind == 0 ? cur | D[dn] : cur & D[dn];
            LogicFlags(r, sz);
            WriteEa(ea, sz, r);
        }
    }

    private void Divide(uint src, int dn, bool signed)
    {
        Cycles += 120;
        if ((src & 0xFFFF) == 0) { RaiseException(5, PC); return; }
        uint dst = D[dn];
        if (!signed)
        {
            uint divisor = src & 0xFFFF;
            uint q = dst / divisor, r = dst % divisor;
            if (q > 0xFFFF) { sr |= FV; sr &= unchecked((ushort)~FC); SetFlag(FN, true); SetFlag(FZ, false); return; }
            D[dn] = (r << 16) | q;
            SetFlag(FN, (q & 0x8000) != 0); SetFlag(FZ, q == 0);
            sr &= unchecked((ushort)~(FV | FC));
        }
        else
        {
            int divisor = (short)src;
            int dividend = (int)dst;
            long q = (long)dividend / divisor;
            long r = (long)dividend % divisor;
            if (q > short.MaxValue || q < short.MinValue) { sr |= FV; sr &= unchecked((ushort)~FC); SetFlag(FN, true); SetFlag(FZ, false); return; }
            D[dn] = ((uint)(ushort)(short)r << 16) | (ushort)(short)q;
            SetFlag(FN, (q & 0x8000) != 0); SetFlag(FZ, q == 0);
            sr &= unchecked((ushort)~(FV | FC));
        }
    }

    private void BcdReg(bool isAdd, int mode, int ry, int rx)
    {
        uint s, d;
        Ea dstEa;
        if (mode == 0)
        {
            s = D[ry] & 0xFF; d = D[rx] & 0xFF;
            dstEa = new Ea { Kind = EaKind.Dn, Val = (uint)rx };
        }
        else
        {
            A[ry] -= (uint)(ry == 7 ? 2 : 1); s = bus.Read8(A[ry]);
            A[rx] -= (uint)(rx == 7 ? 2 : 1); d = bus.Read8(A[rx]);
            dstEa = Mem(A[rx]);
        }
        int xin = X ? 1 : 0;
        int res;
        bool carry;
        if (isAdd)
        {
            res = (int)(s & 0x0F) + (int)(d & 0x0F) + xin;
            if (res > 9) res += 6;
            res += (int)(s & 0xF0) + (int)(d & 0xF0);
            carry = res > 0x99;
            if (carry) res -= 0xA0;
        }
        else
        {
            res = (int)(d & 0x0F) - (int)(s & 0x0F) - xin;
            if (res < 0) res -= 6;
            res += (int)(d & 0xF0) - (int)(s & 0xF0);
            carry = res < 0;
            if (carry) res -= 0x60;
        }
        uint result = (uint)res & 0xFF;
        SetFlag(FC, carry); SetFlag(FX, carry);
        if (result != 0) sr &= unchecked((ushort)~FZ);
        WriteEa(dstEa, Size.B, result);
    }

    // ================= groups 9 and D: SUB / ADD =================

    private void AddSub(bool isSub)
    {
        int mode = (op >> 3) & 7, reg = op & 7, dn = (op >> 9) & 7, opmode = (op >> 6) & 7;

        if (opmode == 3 || opmode == 7)   // ADDA / SUBA
        {
            Size s = opmode == 3 ? Size.W : Size.L;
            Ea ea = ResolveChecked(mode, reg, s, AllModes);
            uint src = SignExtend(ReadEa(ea, s), s);
            A[dn] = isSub ? A[dn] - src : A[dn] + src;
            return;
        }

        Size sz = SizeOf(opmode & 3);
        if (opmode >= 4 && mode <= 1)     // ADDX / SUBX
        {
            uint sv, dv;
            Ea dstEa;
            if (mode == 0)
            {
                sv = D[reg] & Mask(sz); dv = D[dn] & Mask(sz);
                dstEa = new Ea { Kind = EaKind.Dn, Val = (uint)dn };
            }
            else
            {
                A[reg] -= (uint)(reg == 7 && sz == Size.B ? 2 : Bytes(sz)); sv = Read(A[reg], sz);
                A[dn] -= (uint)(dn == 7 && sz == Size.B ? 2 : Bytes(sz)); dv = Read(A[dn], sz);
                dstEa = Mem(A[dn]);
            }
            WriteEa(dstEa, sz, isSub ? DoSub(sv, dv, sz, true, true) : DoAdd(sv, dv, sz, true));
            return;
        }

        if (opmode < 3)   // <ea>,Dn
        {
            Ea ea = ResolveChecked(mode, reg, sz, sz == Size.B ? DataModes : AllModes);
            uint src = ReadEa(ea, sz);
            uint r = isSub ? DoSub(src, D[dn], sz, false, true) : DoAdd(src, D[dn], sz, false);
            WriteEa(new Ea { Kind = EaKind.Dn, Val = (uint)dn }, sz, r);
        }
        else              // Dn,<ea>
        {
            Ea ea = ResolveChecked(mode, reg, sz, MemAlt);
            uint cur = ReadEa(ea, sz);
            uint r = isSub ? DoSub(D[dn], cur, sz, false, true) : DoAdd(D[dn], cur, sz, false);
            WriteEa(ea, sz, r);
        }
    }

    // ================= group B: CMP / CMPA / CMPM / EOR =================

    private void GroupB()
    {
        int mode = (op >> 3) & 7, reg = op & 7, dn = (op >> 9) & 7, opmode = (op >> 6) & 7;

        if (opmode == 3 || opmode == 7)
        {
            Size s = opmode == 3 ? Size.W : Size.L;
            Ea ea = ResolveChecked(mode, reg, s, AllModes);
            DoSub(SignExtend(ReadEa(ea, s), s), A[dn], Size.L, false, false);
            return;
        }

        Size sz = SizeOf(opmode & 3);
        if (opmode < 3)
        {
            Ea ea = ResolveChecked(mode, reg, sz, sz == Size.B ? DataModes : AllModes);
            DoSub(ReadEa(ea, sz), D[dn], sz, false, false);
            return;
        }
        if (mode == 1)   // CMPM (Ay)+,(Ax)+
        {
            uint sv = Read(A[reg], sz); A[reg] += (uint)(reg == 7 && sz == Size.B ? 2 : Bytes(sz));
            uint dv = Read(A[dn], sz); A[dn] += (uint)(dn == 7 && sz == Size.B ? 2 : Bytes(sz));
            DoSub(sv, dv, sz, false, false);
            return;
        }
        Ea dst = ResolveChecked(mode, reg, sz, DataAlt);
        uint r = ReadEa(dst, sz) ^ D[dn];
        LogicFlags(r, sz);
        WriteEa(dst, sz, r);
    }

    // ================= group E: shifts and rotates =================

    private void Shifts()
    {
        int mode = (op >> 3) & 7, reg = op & 7;
        bool left = (op & 0x0100) != 0;

        if (((op >> 6) & 3) == 3)   // memory form: one bit, word size
        {
            if ((op & 0x0800) != 0) throw Illegal;
            Ea ea = ResolveChecked(mode, reg, Size.W, MemAlt);
            WriteEa(ea, Size.W, DoShift((op >> 9) & 3, left, ReadEa(ea, Size.W), 1, Size.W));
            return;
        }

        Size sz = SizeOf((op >> 6) & 3);
        int cnt = (op >> 9) & 7;
        int count = (op & 0x20) != 0 ? (int)(D[cnt] & 63) : (cnt == 0 ? 8 : cnt);
        uint r = DoShift((op >> 3) & 3, left, D[reg], count, sz);
        WriteEa(new Ea { Kind = EaKind.Dn, Val = (uint)reg }, sz, r);
    }

    /// <summary>type: 0 AS, 1 LS, 2 ROX, 3 RO. Shifts one bit at a time so flag rules fall out naturally.</summary>
    private uint DoShift(int type, bool left, uint val, int count, Size s)
    {
        Cycles += 2 * count;
        uint m = Mask(s), msb = Msb(s);
        val &= m;
        bool c = false, v = false, x = X;
        if (count == 0)
        {
            SetNZ(val, s);
            SetFlag(FV, false);
            SetFlag(FC, type == 2 && X);
            return val;
        }
        for (int i = 0; i < count; i++)
        {
            switch (type)
            {
                case 0:
                    if (left)
                    {
                        c = (val & msb) != 0;
                        uint nv = (val << 1) & m;
                        if (((nv ^ val) & msb) != 0) v = true;
                        val = nv;
                    }
                    else
                    {
                        c = (val & 1) != 0;
                        val = (val >> 1) | (val & msb);
                    }
                    x = c;
                    break;
                case 1:
                    if (left) { c = (val & msb) != 0; val = (val << 1) & m; }
                    else { c = (val & 1) != 0; val >>= 1; }
                    x = c;
                    break;
                case 2:
                    if (left) { c = (val & msb) != 0; val = ((val << 1) & m) | (x ? 1u : 0u); }
                    else { c = (val & 1) != 0; val = (val >> 1) | (x ? msb : 0u); }
                    x = c;
                    break;
                default:
                    if (left) { c = (val & msb) != 0; val = ((val << 1) & m) | (c ? 1u : 0u); }
                    else { c = (val & 1) != 0; val = (val >> 1) | (c ? msb : 0u); }
                    break;
            }
        }
        SetNZ(val, s);
        SetFlag(FC, c);
        SetFlag(FV, type == 0 && left && v);
        if (type != 3) SetFlag(FX, x);
        return val;
    }
}
