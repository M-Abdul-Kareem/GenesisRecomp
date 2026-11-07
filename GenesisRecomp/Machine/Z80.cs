namespace GenesisRecomp.Machine;

/// <summary>What the Z80 sees: a 64 KB memory space and a 64 KB I/O space.</summary>
public interface IZ80Bus
{
    byte Read(ushort address);
    void Write(ushort address, byte value);
    byte In(ushort port);
    void Out(ushort port, byte value);
}

/// <summary>
/// Zilog Z80 (the Mega Drive's sound CPU). All documented instructions plus the common undocumented ones
/// (IXH/IXL, SLL, flag bits 3 and 5, DDCB register copies). <see cref="Cycles"/> counts T-states.
/// Verified with the zexdoc / zexall instruction exercisers (see Tools/Z80Verify.cs).
/// </summary>
public sealed class Z80
{
    private const int FS = 0x80, FZ = 0x40, FY = 0x20, FH = 0x10, FX = 0x08, FPV = 0x04, FN = 0x02, FC = 0x01;

    private static readonly byte[] Parity = BuildParity();
    private static byte[] BuildParity()
    {
        var t = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            int b = i; b ^= b >> 4; b ^= b >> 2; b ^= b >> 1;
            t[i] = (byte)((b & 1) == 0 ? FPV : 0);
        }
        return t;
    }

    private readonly IZ80Bus bus;

    public byte A, F, B, C, D, E, H, L;
    public byte A2, F2, B2, C2, D2, E2, H2, L2;
    public ushort IX, IY, SP, PC, MemPtr;
    public byte I, R;
    public bool IFF1, IFF2, Halted;
    public int IM;
    /// <summary>
    /// Maskable interrupt request (the VDP vertical interrupt on the Mega Drive). It is a one-shot: the request is
    /// dropped as soon as the Z80 accepts it, so an interrupt routine that re-enables interrupts is not triggered again.
    /// </summary>
    public bool IntLine;
    /// <summary>Total T-states executed since reset.</summary>
    public long Cycles;

    private bool eiPending;
    private int idx;                // 0 = HL, 1 = IX, 2 = IY for the instruction being executed (set by DD/FD prefix)

    public Z80(IZ80Bus bus) { this.bus = bus; Reset(); }

    public void Reset()
    {
        A = F = B = C = D = E = H = L = 0xFF;
        A2 = F2 = B2 = C2 = D2 = E2 = H2 = L2 = 0xFF;
        IX = IY = 0xFFFF; SP = 0xFFFF; PC = 0; MemPtr = 0;
        I = R = 0; IFF1 = IFF2 = false; IM = 0; Halted = false; eiPending = false;
    }

    // ---------- register pairs ----------
    public ushort AF { get => (ushort)((A << 8) | F); set { A = (byte)(value >> 8); F = (byte)value; } }
    public ushort BC { get => (ushort)((B << 8) | C); set { B = (byte)(value >> 8); C = (byte)value; } }
    public ushort DE { get => (ushort)((D << 8) | E); set { D = (byte)(value >> 8); E = (byte)value; } }
    public ushort HL { get => (ushort)((H << 8) | L); set { H = (byte)(value >> 8); L = (byte)value; } }

    private ushort HLx
    {
        get => idx == 0 ? HL : idx == 1 ? IX : IY;
        set { if (idx == 0) HL = value; else if (idx == 1) IX = value; else IY = value; }
    }

    // ---------- bus access with timing ----------
    private byte Rd(ushort a) { Cycles += 3; return bus.Read(a); }
    private void Wr(ushort a, byte v) { Cycles += 3; bus.Write(a, v); }
    private byte Fetch() { Cycles += 4; R = (byte)((R & 0x80) | ((R + 1) & 0x7F)); return bus.Read(PC++); }
    private byte Imm8() { Cycles += 3; return bus.Read(PC++); }
    private ushort Imm16() { byte lo = Imm8(); byte hi = Imm8(); return (ushort)((hi << 8) | lo); }
    private ushort Rd16(ushort a) { byte lo = Rd(a); byte hi = Rd((ushort)(a + 1)); return (ushort)((hi << 8) | lo); }
    private void Wr16(ushort a, ushort v) { Wr(a, (byte)v); Wr((ushort)(a + 1), (byte)(v >> 8)); }
    private void Push(ushort v) { Wr(--SP, (byte)(v >> 8)); Wr(--SP, (byte)v); }
    private ushort Pop() { byte lo = Rd(SP++); byte hi = Rd(SP++); return (ushort)((hi << 8) | lo); }

    // ---------- register access by 3-bit code ----------
    private byte GetR(int r) => r switch
    {
        0 => B, 1 => C, 2 => D, 3 => E,
        4 => idx == 0 ? H : (byte)(HLx >> 8),
        5 => idx == 0 ? L : (byte)HLx,
        _ => A
    };

    private void SetR(int r, byte v)
    {
        switch (r)
        {
            case 0: B = v; break;
            case 1: C = v; break;
            case 2: D = v; break;
            case 3: E = v; break;
            case 4: if (idx == 0) H = v; else HLx = (ushort)((HLx & 0x00FF) | (v << 8)); break;
            case 5: if (idx == 0) L = v; else HLx = (ushort)((HLx & 0xFF00) | v); break;
            default: A = v; break;
        }
    }

    /// <summary>Same as GetR/SetR but always the real H and L (used when the other operand is (IX+d)).</summary>
    private byte GetPlain(int r) => r switch { 0 => B, 1 => C, 2 => D, 3 => E, 4 => H, 5 => L, _ => A };
    private void SetPlain(int r, byte v)
    {
        switch (r) { case 0: B = v; break; case 1: C = v; break; case 2: D = v; break; case 3: E = v; break; case 4: H = v; break; case 5: L = v; break; default: A = v; break; }
    }

    private ushort GetRp(int p) => p switch { 0 => BC, 1 => DE, 2 => HLx, _ => SP };
    private void SetRp(int p, ushort v) { switch (p) { case 0: BC = v; break; case 1: DE = v; break; case 2: HLx = v; break; default: SP = v; break; } }
    private ushort GetRp2(int p) => p switch { 0 => BC, 1 => DE, 2 => HLx, _ => AF };
    private void SetRp2(int p, ushort v) { switch (p) { case 0: BC = v; break; case 1: DE = v; break; case 2: HLx = v; break; default: AF = v; break; } }

    /// <summary>Address of the (HL) / (IX+d) / (IY+d) operand. With an index prefix this reads the displacement.</summary>
    private ushort MemAddr()
    {
        if (idx == 0) return HL;
        sbyte d = (sbyte)Imm8();
        Cycles += 5;
        ushort a = (ushort)(HLx + d);
        MemPtr = a;
        return a;
    }

    private bool Cond(int cc) => cc switch
    {
        0 => (F & FZ) == 0, 1 => (F & FZ) != 0,
        2 => (F & FC) == 0, 3 => (F & FC) != 0,
        4 => (F & FPV) == 0, 5 => (F & FPV) != 0,
        6 => (F & FS) == 0, _ => (F & FS) != 0
    };

    // ---------- 8-bit arithmetic and logic ----------
    private void Alu(int op, byte v)
    {
        int a = A, r;
        switch (op)
        {
            case 0: // ADD
            case 1: // ADC
            {
                int c = (op == 1 && (F & FC) != 0) ? 1 : 0;
                r = a + v + c;
                F = (byte)((r & (FS | FY | FX)) | ((r & 0xFF) == 0 ? FZ : 0) | ((a ^ v ^ r) & FH)
                           | ((~(a ^ v) & (a ^ r) & 0x80) != 0 ? FPV : 0) | (r > 0xFF ? FC : 0));
                A = (byte)r;
                break;
            }
            case 2: // SUB
            case 3: // SBC
            case 7: // CP
            {
                int c = (op == 3 && (F & FC) != 0) ? 1 : 0;
                r = a - v - c;
                int xy = op == 7 ? v : r;
                F = (byte)((r & FS) | (xy & (FY | FX)) | ((r & 0xFF) == 0 ? FZ : 0) | ((a ^ v ^ r) & FH)
                           | (((a ^ v) & (a ^ r) & 0x80) != 0 ? FPV : 0) | FN | (r < 0 ? FC : 0));
                if (op != 7) A = (byte)r;
                break;
            }
            case 4: A = (byte)(a & v); F = (byte)((A & (FS | FY | FX)) | (A == 0 ? FZ : 0) | FH | Parity[A]); break;
            case 5: A = (byte)(a ^ v); F = (byte)((A & (FS | FY | FX)) | (A == 0 ? FZ : 0) | Parity[A]); break;
            default: A = (byte)(a | v); F = (byte)((A & (FS | FY | FX)) | (A == 0 ? FZ : 0) | Parity[A]); break;
        }
    }

    private byte Inc8(byte v)
    {
        byte r = (byte)(v + 1);
        F = (byte)((F & FC) | (r & (FS | FY | FX)) | (r == 0 ? FZ : 0) | ((v & 0x0F) == 0x0F ? FH : 0) | (v == 0x7F ? FPV : 0));
        return r;
    }

    private byte Dec8(byte v)
    {
        byte r = (byte)(v - 1);
        F = (byte)((F & FC) | (r & (FS | FY | FX)) | (r == 0 ? FZ : 0) | ((v & 0x0F) == 0 ? FH : 0) | (v == 0x80 ? FPV : 0) | FN);
        return r;
    }

    private ushort Add16(ushort a, ushort b)
    {
        int r = a + b;
        F = (byte)((F & (FS | FZ | FPV)) | ((r >> 8) & (FY | FX)) | (((a ^ b ^ r) >> 8) & FH) | (r > 0xFFFF ? FC : 0));
        MemPtr = (ushort)(a + 1);
        return (ushort)r;
    }

    private void Adc16(ushort b)
    {
        int a = HL, c = (F & FC) != 0 ? 1 : 0, r = a + b + c;
        F = (byte)(((r >> 8) & (FS | FY | FX)) | ((r & 0xFFFF) == 0 ? FZ : 0) | (((a ^ b ^ r) >> 8) & FH)
                   | ((~(a ^ b) & (a ^ r) & 0x8000) != 0 ? FPV : 0) | (r > 0xFFFF ? FC : 0));
        MemPtr = (ushort)(a + 1);
        HL = (ushort)r;
    }

    private void Sbc16(ushort b)
    {
        int a = HL, c = (F & FC) != 0 ? 1 : 0, r = a - b - c;
        F = (byte)(((r >> 8) & (FS | FY | FX)) | ((r & 0xFFFF) == 0 ? FZ : 0) | (((a ^ b ^ r) >> 8) & FH)
                   | (((a ^ b) & (a ^ r) & 0x8000) != 0 ? FPV : 0) | FN | (r < 0 ? FC : 0));
        MemPtr = (ushort)(a + 1);
        HL = (ushort)r;
    }

    private void Daa()
    {
        int a = A, t = 0, carry = F & FC;
        if ((F & FH) != 0 || (a & 0x0F) > 9) t |= 0x06;
        if (carry != 0 || a > 0x99) { t |= 0x60; carry = FC; }
        int h;
        if ((F & FN) != 0) { h = ((F & FH) != 0 && (a & 0x0F) < 6) ? FH : 0; a -= t; }
        else { h = (a & 0x0F) > 9 ? FH : 0; a += t; }
        A = (byte)a;
        F = (byte)((A & (FS | FY | FX)) | (A == 0 ? FZ : 0) | h | Parity[A] | (F & FN) | carry);
    }

    // ---------- rotates and shifts ----------
    private byte Rot(int op, byte v)
    {
        int c, r;
        switch (op)
        {
            case 0: c = v >> 7; r = (v << 1) | c; break;                              // RLC
            case 1: c = v & 1; r = (v >> 1) | (c << 7); break;                        // RRC
            case 2: c = v >> 7; r = (v << 1) | (F & FC); break;                       // RL
            case 3: c = v & 1; r = (v >> 1) | ((F & FC) << 7); break;                 // RR
            case 4: c = v >> 7; r = v << 1; break;                                    // SLA
            case 5: c = v & 1; r = (v >> 1) | (v & 0x80); break;                      // SRA
            case 6: c = v >> 7; r = (v << 1) | 1; break;                              // SLL (undocumented)
            default: c = v & 1; r = v >> 1; break;                                    // SRL
        }
        byte b = (byte)r;
        F = (byte)((b & (FS | FY | FX)) | (b == 0 ? FZ : 0) | Parity[b] | c);
        return b;
    }

    // ---------- the instruction loop ----------

    /// <summary>Executes one instruction (or services an interrupt) and returns the T-states it took.</summary>
    public int Step()
    {
        long start = Cycles;

        if (IntLine && IFF1 && !eiPending)
        {
            Halted = false;
            IntLine = false;                                   // accepted: the request is used up
            IFF1 = IFF2 = false;
            R = (byte)((R & 0x80) | ((R + 1) & 0x7F));
            if (IM == 2)
            {
                Push(PC);
                ushort vec = (ushort)((I << 8) | 0xFF);
                PC = Rd16(vec);
                Cycles += 19 - 6 - 6;
            }
            else
            {
                Push(PC);
                PC = 0x38;
                Cycles += 13 - 6;
            }
            MemPtr = PC;
            return (int)(Cycles - start);
        }
        eiPending = false;

        if (Halted)
        {
            Cycles += 4;
            R = (byte)((R & 0x80) | ((R + 1) & 0x7F));
            return 4;
        }

        idx = 0;
        byte op = Fetch();
        while (op == 0xDD || op == 0xFD)
        {
            idx = op == 0xDD ? 1 : 2;
            op = Fetch();
            if (op == 0xED) { idx = 0; break; }
        }

        if (op == 0xCB) { if (idx == 0) ExecCB(); else ExecIndexedCB(); }
        else if (op == 0xED) ExecED();
        else ExecMain(op);

        return (int)(Cycles - start);
    }

    private void ExecMain(byte op)
    {
        int x = op >> 6, y = (op >> 3) & 7, z = op & 7, p = y >> 1, q = y & 1;

        switch (x)
        {
            case 0:
                switch (z)
                {
                    case 0:
                        switch (y)
                        {
                            case 0: break;                                                        // NOP
                            case 1: { byte t = A; A = A2; A2 = t; t = F; F = F2; F2 = t; break; } // EX AF,AF'
                            case 2: // DJNZ
                            {
                                Cycles += 1;
                                sbyte d = (sbyte)Imm8();
                                if (--B != 0) { PC = (ushort)(PC + d); MemPtr = PC; Cycles += 5; }
                                break;
                            }
                            case 3: { sbyte d = (sbyte)Imm8(); PC = (ushort)(PC + d); MemPtr = PC; Cycles += 5; break; }
                            default:
                            {
                                sbyte d = (sbyte)Imm8();
                                if (Cond(y - 4)) { PC = (ushort)(PC + d); MemPtr = PC; Cycles += 5; }
                                break;
                            }
                        }
                        break;
                    case 1:
                        if (q == 0) SetRp(p, Imm16());
                        else { Cycles += 7; HLx = Add16(HLx, GetRp(p)); }
                        break;
                    case 2:
                        switch (y)
                        {
                            case 0: Wr(BC, A); MemPtr = (ushort)(((A << 8) | ((BC + 1) & 0xFF))); break;
                            case 1: A = Rd(BC); MemPtr = (ushort)(BC + 1); break;
                            case 2: Wr(DE, A); MemPtr = (ushort)(((A << 8) | ((DE + 1) & 0xFF))); break;
                            case 3: A = Rd(DE); MemPtr = (ushort)(DE + 1); break;
                            case 4: { ushort a = Imm16(); Wr16(a, HLx); MemPtr = (ushort)(a + 1); break; }
                            case 5: { ushort a = Imm16(); HLx = Rd16(a); MemPtr = (ushort)(a + 1); break; }
                            case 6: { ushort a = Imm16(); Wr(a, A); MemPtr = (ushort)(((A << 8) | ((a + 1) & 0xFF))); break; }
                            default: { ushort a = Imm16(); A = Rd(a); MemPtr = (ushort)(a + 1); break; }
                        }
                        break;
                    case 3:
                        Cycles += 2;
                        SetRp(p, (ushort)(GetRp(p) + (q == 0 ? 1 : -1)));
                        break;
                    case 4:
                        if (y == 6) { ushort a = MemAddr(); byte v = Rd(a); Cycles += 1; Wr(a, Inc8(v)); }
                        else SetR(y, Inc8(GetR(y)));
                        break;
                    case 5:
                        if (y == 6) { ushort a = MemAddr(); byte v = Rd(a); Cycles += 1; Wr(a, Dec8(v)); }
                        else SetR(y, Dec8(GetR(y)));
                        break;
                    case 6:
                        if (y == 6) { ushort a = MemAddr(); byte n = Imm8(); Wr(a, n); }
                        else SetR(y, Imm8());
                        break;
                    default: // 7: RLCA RRCA RLA RRA DAA CPL SCF CCF
                        switch (y)
                        {
                            case 0: { int c = A >> 7; A = (byte)((A << 1) | c); F = (byte)((F & (FS | FZ | FPV)) | (A & (FY | FX)) | c); break; }
                            case 1: { int c = A & 1; A = (byte)((A >> 1) | (c << 7)); F = (byte)((F & (FS | FZ | FPV)) | (A & (FY | FX)) | c); break; }
                            case 2: { int c = A >> 7; A = (byte)((A << 1) | (F & FC)); F = (byte)((F & (FS | FZ | FPV)) | (A & (FY | FX)) | c); break; }
                            case 3: { int c = A & 1; A = (byte)((A >> 1) | ((F & FC) << 7)); F = (byte)((F & (FS | FZ | FPV)) | (A & (FY | FX)) | c); break; }
                            case 4: Daa(); break;
                            case 5: A = (byte)~A; F = (byte)((F & (FS | FZ | FPV | FC)) | (A & (FY | FX)) | FH | FN); break;
                            case 6: F = (byte)((F & (FS | FZ | FPV)) | (A & (FY | FX)) | FC); break;
                            default: F = (byte)((F & (FS | FZ | FPV)) | (A & (FY | FX)) | ((F & FC) != 0 ? FH : 0) | ((F & FC) ^ FC)); break;
                        }
                        break;
                }
                break;

            case 1:
                if (op == 0x76) Halted = true;                                                    // HALT: idle until an interrupt
                else if (y == 6) { ushort a = MemAddr(); Wr(a, GetPlain(z)); }
                else if (z == 6) { ushort a = MemAddr(); SetPlain(y, Rd(a)); }
                else SetR(y, GetR(z));
                break;

            case 2:
                if (z == 6) { ushort a = MemAddr(); Alu(y, Rd(a)); }
                else Alu(y, GetR(z));
                break;

            default: // x == 3
                switch (z)
                {
                    case 0:
                        Cycles += 1;
                        if (Cond(y)) { PC = Pop(); MemPtr = PC; }
                        break;
                    case 1:
                        if (q == 0) SetRp2(p, Pop());
                        else
                            switch (p)
                            {
                                case 0: PC = Pop(); MemPtr = PC; break;
                                case 1:
                                {
                                    byte t = B; B = B2; B2 = t; t = C; C = C2; C2 = t; t = D; D = D2; D2 = t;
                                    t = E; E = E2; E2 = t; t = H; H = H2; H2 = t; t = L; L = L2; L2 = t;
                                    break;
                                }
                                case 2: PC = HLx; break;
                                default: Cycles += 2; SP = HLx; break;
                            }
                        break;
                    case 2: { ushort a = Imm16(); MemPtr = a; if (Cond(y)) PC = a; break; }
                    case 3:
                        switch (y)
                        {
                            case 0: { ushort a = Imm16(); PC = a; MemPtr = a; break; }
                            case 2: { byte n = Imm8(); Cycles += 4; bus.Out((ushort)((A << 8) | n), A); MemPtr = (ushort)(((A << 8) | ((n + 1) & 0xFF))); break; }
                            case 3: { byte n = Imm8(); Cycles += 4; MemPtr = (ushort)(((A << 8) | n) + 1); A = bus.In((ushort)((A << 8) | n)); break; }
                            case 4:
                            {
                                Cycles += 0;
                                ushort lo = Rd16(SP);
                                Cycles += 1;
                                Wr((ushort)(SP + 1), (byte)(HLx >> 8));
                                Wr(SP, (byte)HLx);
                                Cycles += 2;
                                HLx = lo; MemPtr = lo;
                                break;
                            }
                            case 5: { ushort t = DE; DE = HL; HL = t; break; }                       // EX DE,HL (never indexed)
                            case 6: IFF1 = IFF2 = false; break;                                      // DI
                            default: IFF1 = IFF2 = true; eiPending = true; break;                    // EI
                        }
                        break;
                    case 4:
                    {
                        ushort a = Imm16(); MemPtr = a;
                        if (Cond(y)) { Cycles += 1; Push(PC); PC = a; }
                        break;
                    }
                    case 5:
                        if (q == 0) { Cycles += 1; Push(GetRp2(p)); }
                        else { ushort a = Imm16(); Cycles += 1; Push(PC); PC = a; MemPtr = a; }       // CALL nn (p == 0)
                        break;
                    case 6: Alu(y, Imm8()); break;
                    default: Cycles += 1; Push(PC); PC = (ushort)(y * 8); MemPtr = PC; break;          // RST
                }
                break;
        }
    }

    // ---------- CB prefix ----------
    private void ExecCB()
    {
        byte op = Fetch();
        int x = op >> 6, y = (op >> 3) & 7, z = op & 7;
        if (z == 6)
        {
            ushort a = HL;
            byte v = Rd(a);
            switch (x)
            {
                case 0: Cycles += 1; Wr(a, Rot(y, v)); break;
                case 1: Cycles += 1; Bit(y, v, (byte)(MemPtr >> 8)); break;
                case 2: Cycles += 1; Wr(a, (byte)(v & ~(1 << y))); break;
                default: Cycles += 1; Wr(a, (byte)(v | (1 << y))); break;
            }
        }
        else
        {
            byte v = GetPlain(z);
            switch (x)
            {
                case 0: SetPlain(z, Rot(y, v)); break;
                case 1: Bit(y, v, v); break;
                case 2: SetPlain(z, (byte)(v & ~(1 << y))); break;
                default: SetPlain(z, (byte)(v | (1 << y))); break;
            }
        }
    }

    private void Bit(int n, byte v, byte xySource)
    {
        int t = v & (1 << n);
        F = (byte)((F & FC) | FH | (xySource & (FY | FX)) | (t == 0 ? (FZ | FPV) : 0) | (n == 7 && t != 0 ? FS : 0));
    }

    private void ExecIndexedCB()
    {
        sbyte d = (sbyte)Imm8();
        byte op = Imm8();
        Cycles += 2;
        ushort a = (ushort)(HLx + d);
        MemPtr = a;
        int x = op >> 6, y = (op >> 3) & 7, z = op & 7;
        byte v = Rd(a);
        Cycles += 1;
        byte r;
        switch (x)
        {
            case 0: r = Rot(y, v); break;
            case 1: Bit(y, v, (byte)(a >> 8)); return;
            case 2: r = (byte)(v & ~(1 << y)); break;
            default: r = (byte)(v | (1 << y)); break;
        }
        Wr(a, r);
        if (z != 6) SetPlain(z, r);           // undocumented: the result is also copied to a register
    }

    // ---------- ED prefix ----------
    private void ExecED()
    {
        byte op = Fetch();
        int x = op >> 6, y = (op >> 3) & 7, z = op & 7, p = y >> 1, q = y & 1;
        idx = 0;
        if (x == 1)
        {
            switch (z)
            {
                case 0: // IN r,(C)
                {
                    Cycles += 4;
                    byte v = bus.In(BC);
                    MemPtr = (ushort)(BC + 1);
                    F = (byte)((F & FC) | (v & (FS | FY | FX)) | (v == 0 ? FZ : 0) | Parity[v]);
                    if (y != 6) SetPlain(y, v);
                    break;
                }
                case 1: // OUT (C),r
                    Cycles += 4;
                    bus.Out(BC, y == 6 ? (byte)0 : GetPlain(y));
                    MemPtr = (ushort)(BC + 1);
                    break;
                case 2:
                    Cycles += 7;
                    if (q == 0) Sbc16(GetRp(p)); else Adc16(GetRp(p));
                    break;
                case 3:
                {
                    ushort a = Imm16();
                    if (q == 0) Wr16(a, GetRp(p)); else SetRp(p, Rd16(a));
                    MemPtr = (ushort)(a + 1);
                    break;
                }
                case 4: // NEG
                {
                    byte v = A; A = 0; Alu(2, v);
                    break;
                }
                case 5: // RETN / RETI
                    IFF1 = IFF2;
                    PC = Pop(); MemPtr = PC;
                    break;
                case 6: IM = y switch { 0 or 1 or 4 or 5 => 0, 2 or 6 => 1, _ => 2 }; break;
                default:
                    switch (y)
                    {
                        case 0: Cycles += 1; I = A; break;
                        case 1: Cycles += 1; R = A; break;
                        case 2: Cycles += 1; A = I; F = (byte)((F & FC) | (A & (FS | FY | FX)) | (A == 0 ? FZ : 0) | (IFF2 ? FPV : 0)); break;
                        case 3: Cycles += 1; A = R; F = (byte)((F & FC) | (A & (FS | FY | FX)) | (A == 0 ? FZ : 0) | (IFF2 ? FPV : 0)); break;
                        case 4: // RRD
                        {
                            byte m = Rd(HL); Cycles += 4;
                            Wr(HL, (byte)((A << 4) | (m >> 4)));
                            A = (byte)((A & 0xF0) | (m & 0x0F));
                            F = (byte)((F & FC) | (A & (FS | FY | FX)) | (A == 0 ? FZ : 0) | Parity[A]);
                            MemPtr = (ushort)(HL + 1);
                            break;
                        }
                        case 5: // RLD
                        {
                            byte m = Rd(HL); Cycles += 4;
                            Wr(HL, (byte)((m << 4) | (A & 0x0F)));
                            A = (byte)((A & 0xF0) | (m >> 4));
                            F = (byte)((F & FC) | (A & (FS | FY | FX)) | (A == 0 ? FZ : 0) | Parity[A]);
                            MemPtr = (ushort)(HL + 1);
                            break;
                        }
                        default: break;                                                         // NOP
                    }
                    break;
            }
        }
        else if (x == 2 && z <= 3 && y >= 4) ExecBlock(y, z);
        // everything else in the ED page is a two-byte NOP
    }

    private void ExecBlock(int y, int z)
    {
        bool dec = (y & 1) != 0, repeat = y >= 6;
        switch (z)
        {
            case 0: // LDI LDD LDIR LDDR
            {
                byte v = Rd(HL);
                Wr(DE, v);
                Cycles += 2;
                int n = v + A;
                HL = (ushort)(HL + (dec ? -1 : 1));
                DE = (ushort)(DE + (dec ? -1 : 1));
                BC--;
                F = (byte)((F & (FS | FZ | FC)) | ((n & 0x02) != 0 ? FY : 0) | (n & FX) | (BC != 0 ? FPV : 0));
                if (repeat && BC != 0)
                {
                    Cycles += 5; PC -= 2; MemPtr = (ushort)(PC + 1);
                    F = (byte)((F & ~(FY | FX)) | ((PC >> 8) & (FY | FX)));
                }
                break;
            }
            case 1: // CPI CPD CPIR CPDR
            {
                byte v = Rd(HL);
                Cycles += 5;
                int r = A - v;
                int h = ((A ^ v ^ r) & FH) != 0 ? 1 : 0;
                int n = r - h;
                HL = (ushort)(HL + (dec ? -1 : 1));
                BC--;
                F = (byte)((F & FC) | (r & FS) | ((r & 0xFF) == 0 ? FZ : 0) | (h != 0 ? FH : 0) | ((n & 0x02) != 0 ? FY : 0) | (n & FX) | (BC != 0 ? FPV : 0) | FN);
                if (repeat && BC != 0 && (r & 0xFF) != 0)
                {
                    Cycles += 5; PC -= 2; MemPtr = (ushort)(PC + 1);
                    F = (byte)((F & ~(FY | FX)) | ((PC >> 8) & (FY | FX)));
                }
                else MemPtr = (ushort)(MemPtr + (dec ? -1 : 1));
                break;
            }
            case 2: // INI IND INIR INDR
            {
                Cycles += 1;
                byte v = bus.In(BC);
                Cycles += 4;
                MemPtr = (ushort)(BC + (dec ? -1 : 1));
                B--;
                Wr(HL, v);
                HL = (ushort)(HL + (dec ? -1 : 1));
                int k = v + ((C + (dec ? -1 : 1)) & 0xFF);
                IoBlockFlags(v, k);
                if (repeat && B != 0) { Cycles += 5; PC -= 2; }
                break;
            }
            default: // OUTI OUTD OTIR OTDR
            {
                Cycles += 1;
                byte v = Rd(HL);
                B--;
                Cycles += 4;
                bus.Out(BC, v);
                MemPtr = (ushort)(BC + (dec ? -1 : 1));
                HL = (ushort)(HL + (dec ? -1 : 1));
                int k = v + L;
                IoBlockFlags(v, k);
                if (repeat && B != 0) { Cycles += 5; PC -= 2; }
                break;
            }
        }
    }

    private void IoBlockFlags(byte v, int k)
    {
        F = (byte)((B & (FS | FY | FX)) | (B == 0 ? FZ : 0) | ((v & 0x80) != 0 ? FN : 0)
                   | (k > 0xFF ? (FH | FC) : 0) | Parity[(byte)((k & 7) ^ B)]);
    }
}
