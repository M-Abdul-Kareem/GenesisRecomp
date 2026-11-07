using Size = GenesisRecomp.Cpu.Size;
using GenesisRecomp.Cpu;

namespace GenesisRecomp.Recomp;

internal enum LK { Dn, An, Mem, Imm }

/// <summary>A resolved operand location: a register number, a C# address expression, or an immediate literal.</summary>
internal readonly record struct Loc(LK K, string E);

/// <summary>
/// Turns one decoded 68000 instruction into C# source. Everything the interpreter works out at run time
/// (opcode decoding, effective-address modes, register numbers, sizes, immediates, branch targets, condition tests)
/// is worked out here, once, so the generated code is straight-line statements with constants.
/// The statements call the same flag/arithmetic helpers and the same memory helpers (with the same cycle counts)
/// as the interpreter, in the same order, so both produce identical results.
/// Instructions that are not handled here return false and run through the interpreter instead.
/// </summary>
internal sealed class Lifter
{
    private readonly List<string> L = new();
    private readonly DecodedInst i;
    private int tmp;
    private bool setsPc;

    private Lifter(DecodedInst inst) => i = inst;

    /// <param name="setsPc">True if the generated lines set PC themselves (branches, jumps, calls, returns).</param>
    public static bool TryLift(DecodedInst inst, out List<string> lines, out bool setsPc)
    {
        var l = new Lifter(inst);
        bool ok = l.Run();
        lines = l.L;
        setsPc = l.setsPc;
        return ok;
    }

    // ---------------------------------------------------------------- small helpers

    private void Emit(string s) => L.Add(s);
    private string T(string p) => p + tmp++;
    private static string Lit(uint v) => $"0x{v:X}u";
    private static string SizeName(Size s) => s switch { Size.B => "Size.B", Size.W => "Size.W", _ => "Size.L" };
    private static string MaskLit(Size s) => s switch { Size.B => "0xFFu", Size.W => "0xFFFFu", _ => "0xFFFFFFFFu" };
    private static string KeepLit(Size s) => s switch { Size.B => "0xFFFFFF00u", Size.W => "0xFFFF0000u", _ => "0u" };
    private static int Bytes(Size s) => s switch { Size.B => 1, Size.W => 2, _ => 4 };
    private uint Next => i.NextAddress;

    private static string SignExt(string v, Size s) => s switch
    {
        Size.B => $"unchecked((uint)(int)(sbyte)({v}))",
        Size.W => $"unchecked((uint)(int)(short)({v}))",
        _ => v
    };

    private static string CondExpr(int cc) => cc switch
    {
        0 => "true",
        1 => "false",
        2 => "((sr & 0x05) == 0)",                                   // HI  !C && !Z
        3 => "((sr & 0x05) != 0)",                                   // LS  C || Z
        4 => "((sr & 0x01) == 0)",                                   // CC
        5 => "((sr & 0x01) != 0)",                                   // CS
        6 => "((sr & 0x04) == 0)",                                   // NE
        7 => "((sr & 0x04) != 0)",                                   // EQ
        8 => "((sr & 0x02) == 0)",                                   // VC
        9 => "((sr & 0x02) != 0)",                                   // VS
        10 => "((sr & 0x08) == 0)",                                  // PL
        11 => "((sr & 0x08) != 0)",                                  // MI
        12 => "((((sr >> 3) ^ (sr >> 1)) & 1) == 0)",                // GE  N == V
        13 => "((((sr >> 3) ^ (sr >> 1)) & 1) != 0)",                // LT
        14 => "((sr & 0x04) == 0 && (((sr >> 3) ^ (sr >> 1)) & 1) == 0)",   // GT
        _ => "((sr & 0x04) != 0 || (((sr >> 3) ^ (sr >> 1)) & 1) != 0)"     // LE
    };

    // ---------------------------------------------------------------- operands

    private string IndexExpr(Operand o)
    {
        string r = (o.IndexIsAddr ? "A" : "D") + $"[{o.IndexReg}]";
        return o.IndexLong ? r : $"(uint)(int)(short){r}";
    }

    /// <summary>Emit the code that resolves an operand (including auto-increment/decrement side effects) and return where it lives.</summary>
    private Loc Resolve(Operand o, Size sz)
    {
        switch (o.Kind)
        {
            case OpKind.Dn: return new Loc(LK.Dn, o.Reg.ToString());
            case OpKind.An: return new Loc(LK.An, o.Reg.ToString());
            case OpKind.Ind:
            {
                string a = T("a");
                Emit($"uint {a} = A[{o.Reg}];");
                return new Loc(LK.Mem, a);
            }
            case OpKind.PostInc:
            {
                string a = T("a");
                int n = o.Reg == 7 && sz == Size.B ? 2 : Bytes(sz);
                Emit($"uint {a} = A[{o.Reg}]; A[{o.Reg}] += {n};");
                return new Loc(LK.Mem, a);
            }
            case OpKind.PreDec:
            {
                string a = T("a");
                int n = o.Reg == 7 && sz == Size.B ? 2 : Bytes(sz);
                Emit($"A[{o.Reg}] -= {n}; uint {a} = A[{o.Reg}];");
                return new Loc(LK.Mem, a);
            }
            case OpKind.Disp:
            {
                string a = T("a");
                Emit($"uint {a} = A[{o.Reg}] + {Lit(unchecked((uint)o.Disp))};");
                return new Loc(LK.Mem, a);
            }
            case OpKind.Index:
            {
                string a = T("a");
                Emit($"uint {a} = A[{o.Reg}] + {IndexExpr(o)} + {Lit(unchecked((uint)o.Disp))};");
                return new Loc(LK.Mem, a);
            }
            case OpKind.AbsW:
            case OpKind.AbsL:
            case OpKind.PcDisp:
                return new Loc(LK.Mem, Lit(o.Value));
            case OpKind.PcIndex:
            {
                string a = T("a");
                Emit($"uint {a} = {Lit(o.Value)} + {IndexExpr(o)};");
                return new Loc(LK.Mem, a);
            }
            case OpKind.Imm:
                return new Loc(LK.Imm, Lit(o.Value));
            default:
                throw new NotSupportedException();
        }
    }

    private static string Read(Loc l, Size s) => l.K switch
    {
        LK.Dn => s == Size.L ? $"D[{l.E}]" : $"(D[{l.E}] & {MaskLit(s)})",
        LK.An => s == Size.L ? $"A[{l.E}]" : $"(A[{l.E}] & {MaskLit(s)})",
        LK.Imm => l.E,
        _ => s switch { Size.B => $"R8({l.E})", Size.W => $"R16({l.E})", _ => $"R32({l.E})" }
    };

    private static string Write(Loc l, Size s, string val) => l.K switch
    {
        LK.Dn => s == Size.L ? $"D[{l.E}] = {val};" : $"D[{l.E}] = (D[{l.E}] & {KeepLit(s)}) | (({val}) & {MaskLit(s)});",
        LK.An => $"A[{l.E}] = {val};",
        LK.Mem => s switch
        {
            Size.B => $"W8({l.E}, (byte)({val}));",
            Size.W => $"W16({l.E}, (ushort)({val}));",
            _ => $"W32({l.E}, {val});"
        },
        _ => throw new NotSupportedException()
    };

    private static string Reg(Operand o) => (o.Kind == OpKind.An ? "A" : "D") + $"[{o.Reg}]";

    // ---------------------------------------------------------------- instructions

    private bool Run()
    {
        // Every instruction gets its own scope so temporaries never clash.
        Emit("{");
        bool ok;
        try { ok = Dispatch(); }
        catch (NotSupportedException) { ok = false; }
        Emit("}");
        return ok;
    }

    private bool Dispatch()
    {
        switch (i.Op)
        {
            case Mn.NOP: return true;
            case Mn.MOVEQ:
            {
                uint v = unchecked((uint)i.Src.Disp);
                Emit($"D[{i.Dst.Reg}] = {Lit(v)};");
                Emit($"LogicFlags({Lit(v)}, Size.L);");
                return true;
            }
            case Mn.MOVE: return LiftMove();
            case Mn.MOVEA:
            {
                var src = Resolve(i.Src, i.Size);
                string v = T("v");
                Emit($"uint {v} = {Read(src, i.Size)};");
                Emit($"A[{i.Dst.Reg}] = {SignExt(v, i.Size)};");
                return true;
            }
            case Mn.LEA:
            {
                var src = Resolve(i.Src, Size.L);
                Emit($"A[{i.Dst.Reg}] = {src.E};");
                return true;
            }
            case Mn.PEA:
            {
                var dst = Resolve(i.Dst, Size.L);
                Emit($"Push32({dst.E});");
                return true;
            }
            case Mn.ADD: case Mn.SUB: case Mn.AND: case Mn.OR: case Mn.EOR: return LiftArith(i.Op);
            case Mn.ADDA: case Mn.SUBA:
            {
                var src = Resolve(i.Src, i.Size);
                string v = T("v");
                Emit($"uint {v} = {SignExt(Read(src, i.Size), i.Size)};");
                Emit(i.Op == Mn.SUBA ? $"A[{i.Dst.Reg}] = A[{i.Dst.Reg}] - {v};" : $"A[{i.Dst.Reg}] = A[{i.Dst.Reg}] + {v};");
                return true;
            }
            case Mn.ADDQ: case Mn.SUBQ:
            {
                uint data = i.Src.Value;
                bool sub = i.Op == Mn.SUBQ;
                if (i.Dst.Kind == OpKind.An)
                {
                    Emit(sub ? $"A[{i.Dst.Reg}] = A[{i.Dst.Reg}] - {data}u;" : $"A[{i.Dst.Reg}] = A[{i.Dst.Reg}] + {data}u;");
                    return true;
                }
                var dst = Resolve(i.Dst, i.Size);
                string cur = T("c"), r = T("r");
                Emit($"uint {cur} = {Read(dst, i.Size)};");
                Emit(sub ? $"uint {r} = DoSub({data}u, {cur}, {SizeName(i.Size)}, false, true);" : $"uint {r} = DoAdd({data}u, {cur}, {SizeName(i.Size)}, false);");
                Emit(Write(dst, i.Size, r));
                return true;
            }
            case Mn.ORI: case Mn.ANDI: case Mn.EORI: case Mn.ADDI: case Mn.SUBI: case Mn.CMPI: return LiftImmediate(i.Op);
            case Mn.CMP:
            {
                var src = Resolve(i.Src, i.Size);
                string v = T("v");
                Emit($"uint {v} = {Read(src, i.Size)};");
                Emit($"DoSub({v}, D[{i.Dst.Reg}], {SizeName(i.Size)}, false, false);");
                return true;
            }
            case Mn.CMPA:
            {
                var src = Resolve(i.Src, i.Size);
                string v = T("v");
                Emit($"uint {v} = {SignExt(Read(src, i.Size), i.Size)};");
                Emit($"DoSub({v}, A[{i.Dst.Reg}], Size.L, false, false);");
                return true;
            }
            case Mn.CMPM:
            {
                Size sz = i.Size;
                int ys = i.Src.Reg == 7 && sz == Size.B ? 2 : Bytes(sz), xs = i.Dst.Reg == 7 && sz == Size.B ? 2 : Bytes(sz);
                string sv = T("s"), dv = T("d");
                Emit($"uint {sv} = {Read(new Loc(LK.Mem, $"A[{i.Src.Reg}]"), sz)}; A[{i.Src.Reg}] += {ys};");
                Emit($"uint {dv} = {Read(new Loc(LK.Mem, $"A[{i.Dst.Reg}]"), sz)}; A[{i.Dst.Reg}] += {xs};");
                Emit($"DoSub({sv}, {dv}, {SizeName(sz)}, false, false);");
                return true;
            }
            case Mn.CLR:
            {
                var dst = Resolve(i.Dst, i.Size);
                Emit($"LogicFlags(0, {SizeName(i.Size)});");
                Emit(Write(dst, i.Size, "0"));
                return true;
            }
            case Mn.NEG: case Mn.NEGX:
            {
                var dst = Resolve(i.Dst, i.Size);
                string c = T("c");
                Emit($"uint {c} = {Read(dst, i.Size)};");
                Emit(Write(dst, i.Size, $"DoSub({c}, 0, {SizeName(i.Size)}, {(i.Op == Mn.NEGX ? "true" : "false")}, true)"));
                return true;
            }
            case Mn.NOT:
            {
                var dst = Resolve(i.Dst, i.Size);
                string r = T("r");
                Emit($"uint {r} = ~{Read(dst, i.Size)};");
                Emit($"LogicFlags({r}, {SizeName(i.Size)});");
                Emit(Write(dst, i.Size, r));
                return true;
            }
            case Mn.TST:
            {
                var dst = Resolve(i.Dst, i.Size);
                Emit($"LogicFlags({Read(dst, i.Size)}, {SizeName(i.Size)});");
                return true;
            }
            case Mn.Scc:
            {
                var dst = Resolve(i.Dst, Size.B);
                Emit(Write(dst, Size.B, $"({CondExpr(i.Cond)} ? 0xFFu : 0u)"));
                return true;
            }
            case Mn.SWAP:
            {
                int r = i.Dst.Reg;
                string v = T("r");
                Emit($"uint {v} = (D[{r}] << 16) | (D[{r}] >> 16);");
                Emit($"D[{r}] = {v}; LogicFlags({v}, Size.L);");
                return true;
            }
            case Mn.EXT:
            {
                int r = i.Dst.Reg;
                string v = T("r");
                if (i.Size == Size.W)
                {
                    Emit($"uint {v} = (uint)(int)(sbyte)D[{r}] & 0xFFFF;");
                    Emit($"D[{r}] = (D[{r}] & 0xFFFF0000u) | {v}; LogicFlags({v}, Size.W);");
                }
                else
                {
                    Emit($"uint {v} = (uint)(int)(short)D[{r}];");
                    Emit($"D[{r}] = {v}; LogicFlags({v}, Size.L);");
                }
                return true;
            }
            case Mn.EXG:
            {
                string x = Reg(i.Src), y = Reg(i.Dst), t = T("t");
                Emit($"uint {t} = {x}; {x} = {y}; {y} = {t};");
                return true;
            }
            case Mn.LINK:
                Emit($"Push32(A[{i.Src.Reg}]);");
                Emit($"A[{i.Src.Reg}] = A[7];");
                Emit($"A[7] += {Lit(unchecked((uint)i.Dst.Disp))};");
                return true;
            case Mn.UNLK:
                Emit($"A[7] = A[{i.Dst.Reg}];");
                Emit($"A[{i.Dst.Reg}] = Pop32();");
                return true;
            case Mn.BTST: case Mn.BCHG: case Mn.BCLR: case Mn.BSET: return LiftBit(i.Op);
            case Mn.ASR: case Mn.ASL: case Mn.LSR: case Mn.LSL:
            case Mn.ROXR: case Mn.ROXL: case Mn.ROR: case Mn.ROL: return LiftShift();
            case Mn.MULU: case Mn.MULS:
            {
                var src = Resolve(i.Src, Size.W);
                string s = T("s"), r = T("r");
                int n = i.Dst.Reg;
                Emit("Cycles += 34;");
                Emit($"uint {s} = {Read(src, Size.W)};");
                Emit(i.Op == Mn.MULU
                    ? $"uint {r} = (D[{n}] & 0xFFFF) * ({s} & 0xFFFF);"
                    : $"uint {r} = (uint)((int)(short)D[{n}] * (int)(short){s});");
                Emit($"D[{n}] = {r}; LogicFlags({r}, Size.L);");
                return true;
            }
            case Mn.DIVU: case Mn.DIVS:
            {
                var src = Resolve(i.Src, Size.W);
                Emit($"PC = {Lit(Next)};");                   // a divide-by-zero exception pushes this
                Emit($"Divide({Read(src, Size.W)}, {i.Dst.Reg}, {(i.Op == Mn.DIVS ? "true" : "false")});");
                Emit($"if (PC != {Lit(Next)}) return;");      // exception raised: stop here, the handler address is in PC
                return true;
            }
            case Mn.MOVEM: return LiftMovem();

            // ---- control flow ----
            case Mn.BRA:
                setsPc = true;
                Emit($"PC = {Lit(i.Target!.Value)};");
                return true;
            case Mn.BSR:
                setsPc = true;
                Emit($"Push32({Lit(Next)});");
                Emit($"PC = {Lit(i.Target!.Value)};");
                return true;
            case Mn.Bcc:
                setsPc = true;
                Emit($"PC = {CondExpr(i.Cond)} ? {Lit(i.Target!.Value)} : {Lit(Next)};");
                return true;
            case Mn.Dbcc:
            {
                setsPc = true;
                int r = i.Src.Reg;
                string cnt = T("n");
                Emit($"PC = {Lit(Next)};");
                Emit($"if (!{CondExpr(i.Cond)})");
                Emit("{");
                Emit($"    ushort {cnt} = (ushort)(D[{r}] - 1);");
                Emit($"    D[{r}] = (D[{r}] & 0xFFFF0000u) | {cnt};");
                Emit($"    if ({cnt} != 0xFFFF) PC = {Lit(i.Target!.Value)};");
                Emit("}");
                return true;
            }
            case Mn.JSR: case Mn.JMP:
            {
                setsPc = true;
                var ea = Resolve(i.Dst, Size.L);
                if (i.Op == Mn.JSR) Emit($"Push32({Lit(Next)});");
                Emit($"PC = {ea.E};");
                return true;
            }
            case Mn.RTS:
                setsPc = true;
                Emit("PC = Pop32();");
                return true;
            case Mn.RTR:
                setsPc = true;
                Emit("ushort ccr = Pop16();");
                Emit("sr = (ushort)((sr & 0xFF00) | (ccr & 0x1F));");
                Emit("PC = Pop32();");
                return true;

            default:
                return false;   // MOVEP, CHK, TRAP, TRAPV, STOP, RESET, RTE, ILLEGAL, BCD, ADDX/SUBX, TAS ... -> interpreter
        }
    }

    private bool LiftMove()
    {
        Operand s = i.Src, d = i.Dst;
        if (d.Kind is OpKind.Usp or OpKind.Sr) return false;      // privileged: interpreter
        if (s.Kind == OpKind.Usp) return false;
        if (s.Kind == OpKind.Sr)                                   // MOVE SR,<ea>
        {
            var dl = Resolve(d, Size.W);
            Emit(Write(dl, Size.W, "(uint)sr"));
            return true;
        }
        if (d.Kind == OpKind.Ccr)                                  // MOVE <ea>,CCR
        {
            var sl = Resolve(s, Size.W);
            Emit($"sr = (ushort)((sr & 0xFF00) | (({Read(sl, Size.W)}) & 0x1F));");
            return true;
        }
        Size sz = i.Size;
        var src = Resolve(s, sz);
        string v = T("v");
        Emit($"uint {v} = {Read(src, sz)};");
        var dst = Resolve(d, sz);
        Emit($"LogicFlags({v}, {SizeName(sz)});");
        Emit(Write(dst, sz, v));
        return true;
    }

    private bool LiftArith(Mn m)
    {
        Size sz = i.Size;
        string S = SizeName(sz);
        if (m == Mn.EOR)
        {
            var dl = Resolve(i.Dst, sz);
            string r = T("r");
            Emit($"uint {r} = {Read(dl, sz)} ^ D[{i.Src.Reg}];");
            Emit($"LogicFlags({r}, {S});");
            Emit(Write(dl, sz, r));
            return true;
        }
        if (i.Dst.Kind == OpKind.Dn)           // <ea>,Dn
        {
            int n = i.Dst.Reg;
            var src = Resolve(i.Src, sz);
            string v = T("v"), r = T("r");
            Emit($"uint {v} = {Read(src, sz)};");
            switch (m)
            {
                case Mn.ADD: Emit($"uint {r} = DoAdd({v}, D[{n}], {S}, false);"); break;
                case Mn.SUB: Emit($"uint {r} = DoSub({v}, D[{n}], {S}, false, true);"); break;
                case Mn.AND: Emit($"uint {r} = D[{n}] & {v}; LogicFlags({r}, {S});"); break;
                default: Emit($"uint {r} = D[{n}] | {v}; LogicFlags({r}, {S});"); break;
            }
            Emit(Write(new Loc(LK.Dn, n.ToString()), sz, r));
        }
        else                                   // Dn,<ea>
        {
            int n = i.Src.Reg;
            var dst = Resolve(i.Dst, sz);
            string c = T("c"), r = T("r");
            Emit($"uint {c} = {Read(dst, sz)};");
            switch (m)
            {
                case Mn.ADD: Emit($"uint {r} = DoAdd(D[{n}], {c}, {S}, false);"); break;
                case Mn.SUB: Emit($"uint {r} = DoSub(D[{n}], {c}, {S}, false, true);"); break;
                case Mn.AND: Emit($"uint {r} = {c} & D[{n}]; LogicFlags({r}, {S});"); break;
                default: Emit($"uint {r} = {c} | D[{n}]; LogicFlags({r}, {S});"); break;
            }
            Emit(Write(dst, sz, r));
        }
        return true;
    }

    private bool LiftImmediate(Mn m)
    {
        if (i.Dst.Kind == OpKind.Sr) return false;                 // privileged
        uint imm = i.Src.Value;
        if (i.Dst.Kind == OpKind.Ccr)
        {
            string op = m switch { Mn.ORI => "|", Mn.ANDI => "&", _ => "^" };
            Emit("int ccr = sr & 0x1F;");
            Emit($"ccr = ccr {op} {(int)imm};");
            Emit("sr = (ushort)((sr & 0xFF00) | (ccr & 0x1F));");
            return true;
        }
        Size sz = i.Size;
        string S = SizeName(sz);
        var dst = Resolve(i.Dst, sz);
        string c = T("c"), r = T("r");
        Emit($"uint {c} = {Read(dst, sz)};");
        switch (m)
        {
            case Mn.ORI: Emit($"uint {r} = {c} | {Lit(imm)}; LogicFlags({r}, {S});"); break;
            case Mn.ANDI: Emit($"uint {r} = {c} & {Lit(imm)}; LogicFlags({r}, {S});"); break;
            case Mn.EORI: Emit($"uint {r} = {c} ^ {Lit(imm)}; LogicFlags({r}, {S});"); break;
            case Mn.SUBI: Emit($"uint {r} = DoSub({Lit(imm)}, {c}, {S}, false, true);"); break;
            case Mn.ADDI: Emit($"uint {r} = DoAdd({Lit(imm)}, {c}, {S}, false);"); break;
            default: Emit($"DoSub({Lit(imm)}, {c}, {S}, false, false);"); return true;   // CMPI: flags only
        }
        Emit(Write(dst, sz, r));
        return true;
    }

    private bool LiftBit(Mn m)
    {
        bool toDn = i.Dst.Kind == OpKind.Dn;
        Size sz = toDn ? Size.L : Size.B;
        int bitMask = toDn ? 31 : 7;
        string bit = T("b");
        if (i.Src.Kind == OpKind.Dn) Emit($"int {bit} = ((int)D[{i.Src.Reg}]) & {bitMask};");
        else Emit($"int {bit} = {(int)(i.Src.Value & (uint)bitMask)};");
        var dst = Resolve(i.Dst, sz);
        string val = T("v");
        Emit($"uint {val} = {Read(dst, sz)};");
        Emit($"SetFlag(FZ, (({val} >> {bit}) & 1) == 0);");
        if (m == Mn.BTST) return true;
        string upd = m switch { Mn.BCHG => $"{val} ^ (1u << {bit})", Mn.BCLR => $"{val} & ~(1u << {bit})", _ => $"{val} | (1u << {bit})" };
        Emit(Write(dst, sz, upd));
        return true;
    }

    private bool LiftShift()
    {
        int k = (int)i.Op - (int)Mn.ASR;
        int type = k / 2;
        string left = (k & 1) != 0 ? "true" : "false";
        if (i.Src.IsNone)                       // memory form: one bit, word size
        {
            var dst = Resolve(i.Dst, Size.W);
            string c = T("c");
            Emit($"uint {c} = {Read(dst, Size.W)};");
            Emit(Write(dst, Size.W, $"DoShift({type}, {left}, {c}, 1, Size.W)"));
            return true;
        }
        Size sz = i.Size;
        string count = i.Src.Kind == OpKind.Dn ? $"(int)(D[{i.Src.Reg}] & 63)" : ((int)i.Src.Value).ToString();
        string r = T("r");
        Emit($"uint {r} = DoShift({type}, {left}, D[{i.Dst.Reg}], {count}, {SizeName(sz)});");
        Emit(Write(new Loc(LK.Dn, i.Dst.Reg.ToString()), sz, r));
        return true;
    }

    private bool LiftMovem()
    {
        Size sz = i.Size;
        int step = Bytes(sz);
        bool toRegs = i.Dst.Kind == OpKind.RegList;
        uint mask = (toRegs ? i.Dst : i.Src).Value;       // normalised: bit r = register r (D0..D7 = 0..7, A0..A7 = 8..15)
        Operand ea = toRegs ? i.Src : i.Dst;

        if (!toRegs && ea.Kind == OpKind.PreDec)
        {
            string a = T("a"), init = T("i");
            Emit($"uint {a} = A[{ea.Reg}]; uint {init} = {a};");
            for (int r = 15; r >= 0; r--)
            {
                if ((mask & (1u << r)) == 0) continue;
                string val = r < 8 ? $"D[{r}]" : (r - 8 == ea.Reg ? init : $"A[{r - 8}]");
                Emit($"{a} -= {step}; {Write(new Loc(LK.Mem, a), sz, val)}");
            }
            Emit($"A[{ea.Reg}] = {a};");
            return true;
        }

        string addr = T("a");
        if (ea.Kind == OpKind.PostInc) Emit($"uint {addr} = A[{ea.Reg}];");
        else
        {
            var l = Resolve(ea, sz);
            Emit($"uint {addr} = {l.E};");
        }
        for (int r = 0; r < 16; r++)
        {
            if ((mask & (1u << r)) == 0) continue;
            string reg = r < 8 ? $"D[{r}]" : $"A[{r - 8}]";
            if (toRegs) Emit($"{reg} = {SignExt(Read(new Loc(LK.Mem, addr), sz), sz)}; {addr} += {step};");
            else Emit($"{Write(new Loc(LK.Mem, addr), sz, reg)} {addr} += {step};");
        }
        if (ea.Kind == OpKind.PostInc) Emit($"A[{ea.Reg}] = {addr};");
        return true;
    }
}

/// <summary>Lets the test tool ask whether an instruction is recompiled without exposing the Lifter itself.</summary>
public static class RecompShim
{
    public static bool CanLift(DecodedInst ins) => Lifter.TryLift(ins, out _, out _);
}
