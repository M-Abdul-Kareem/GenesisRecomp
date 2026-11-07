using System.Linq.Expressions;
using System.Reflection;
using GenesisRecomp.Cpu;
using E = System.Linq.Expressions.Expression;
using Size = GenesisRecomp.Cpu.Size;

namespace GenesisRecomp.Recomp;

/// <summary>
/// The in-memory twin of <see cref="Lifter"/>: instead of writing C# text it builds .NET expression trees for a
/// segment of instructions, which the runtime turns into machine code on the spot (no compiler, no Visual Studio).
/// It follows the Lifter statement for statement, with the same helpers, the same order of side effects and the
/// same cycle counts, and covers exactly the same instructions; anything else is left to the interpreter.
/// --verify-live proves it matches the interpreter instruction by instruction.
/// </summary>
internal sealed class ExprLifter
{
    // ---------------------------------------------------------------- reflection handles (the helpers are private)

    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static MethodInfo M(string n) => typeof(Cpu68k).GetMethod(n, Any) ?? throw new MissingMethodException(nameof(Cpu68k), n);
    private static FieldInfo F(string n) => typeof(Cpu68k).GetField(n, Any) ?? throw new MissingFieldException(nameof(Cpu68k), n);

    private static readonly MethodInfo mR8 = M("R8"), mR16 = M("R16"), mR32 = M("R32"), mW8 = M("W8"), mW16 = M("W16"), mW32 = M("W32");
    private static readonly MethodInfo mLogic = M("LogicFlags"), mAdd = M("DoAdd"), mSub = M("DoSub"), mShift = M("DoShift"),
        mDivide = M("Divide"), mPush32 = M("Push32"), mPop32 = M("Pop32"), mPop16 = M("Pop16"), mSetFlag = M("SetFlag"),
        mExecuteOne = M("ExecuteOne");
    private static readonly FieldInfo fSr = F("sr"), fInstrStart = F("instrStart"), fPc = F("PC"), fCycles = F("Cycles"),
        fCount = F("InstructionCount"), fFallback = F("FallbackInsts"), fD = F("D"), fA = F("A");

    // ---------------------------------------------------------------- per-segment state

    private enum LK { Dn, An, Mem, Imm }
    private readonly record struct Loc(LK K, Expression X);

    private readonly ParameterExpression c = E.Parameter(typeof(Cpu68k), "c");
    private readonly ParameterExpression d = E.Variable(typeof(uint[]), "d");
    private readonly ParameterExpression a = E.Variable(typeof(uint[]), "a");
    private readonly List<ParameterExpression> locals = new();
    private readonly List<Expression> stmts = new();
    private readonly LabelTarget ret = E.Label(typeof(void), "ret");
    private int tmp;
    private bool setsPc;
    private DecodedInst i = null!;

    private ExprLifter() { locals.Add(d); locals.Add(a); }

    /// <summary>Build the delegate that runs one segment (a straight run of instructions) against a CPU.</summary>
    public static Action<Cpu68k> Build(IReadOnlyList<DecodedInst> insts, IReadOnlyList<bool> isLifted)
    {
        var x = new ExprLifter();
        x.stmts.Add(E.Assign(x.d, E.Field(x.c, fD)));
        x.stmts.Add(E.Assign(x.a, E.Field(x.c, fA)));

        // cycle / instruction counts for everything recompiled here, added once up front (a trailing fallback counts itself)
        int n = 0, cycles = 0;
        for (int k = 0; k < insts.Count; k++)
        {
            if (!isLifted[k]) continue;
            n++;
            cycles += 4 + 4 * (insts[k].Length / 2);
        }
        if (n > 0)
        {
            x.stmts.Add(E.AddAssign(E.Field(x.c, fCount), E.Constant((long)n)));
            x.stmts.Add(E.AddAssign(E.Field(x.c, fCycles), E.Constant((long)cycles)));
        }

        for (int k = 0; k < insts.Count; k++)
        {
            DecodedInst ins = insts[k];
            if (!isLifted[k])
            {
                x.stmts.Add(E.Assign(E.Field(x.c, fPc), x.Ku(ins.Address)));
                x.stmts.Add(E.PostIncrementAssign(E.Field(x.c, fFallback)));
                x.stmts.Add(E.Call(x.c, mExecuteOne));
                continue;                                    // always last in its segment
            }
            x.i = ins;
            x.setsPc = false;
            x.stmts.Add(E.Assign(E.Field(x.c, fInstrStart), x.Ku(ins.Address)));
            if (!x.Dispatch()) throw new NotSupportedException($"{ins.Op} is not supported by the in-memory recompiler");
            if (k == insts.Count - 1 && !x.setsPc)
                x.stmts.Add(E.Assign(E.Field(x.c, fPc), x.Ku(ins.NextAddress)));
        }
        x.stmts.Add(E.Label(x.ret));
        var body = E.Block(typeof(void), x.locals, x.stmts);
        return E.Lambda<Action<Cpu68k>>(body, x.c).Compile();
    }

    // ---------------------------------------------------------------- tiny expression vocabulary

    private Expression Ku(uint v) => E.Constant(v);
    private static Expression Ki(int v) => E.Constant(v);
    private static Expression Kb(bool v) => E.Constant(v);
    private static Expression KSize(Size s) => E.Constant(s);
    private Expression Dn(int n) => E.ArrayAccess(d, Ki(n));
    private Expression An(int n) => E.ArrayAccess(a, Ki(n));
    private Expression Pc => E.Field(c, fPc);
    private Expression Sr => E.Field(c, fSr);
    private Expression SrInt => E.Convert(Sr, typeof(int));
    private ParameterExpression Tmp(Type? t = null) { var v = E.Variable(t ?? typeof(uint), "t" + tmp++); locals.Add(v); return v; }
    private void Emit(Expression e) => stmts.Add(e);
    private Expression Call(MethodInfo m, params Expression[] args) => E.Call(c, m, args);
    private static Expression And(Expression x, uint k) => E.And(x, E.Constant(k));
    private static Expression Or(Expression x, Expression y) => E.Or(x, y);
    private static uint Mask(Size s) => s switch { Size.B => 0xFFu, Size.W => 0xFFFFu, _ => 0xFFFFFFFFu };
    private static uint Keep(Size s) => s switch { Size.B => 0xFFFFFF00u, Size.W => 0xFFFF0000u, _ => 0u };
    private static int Bytes(Size s) => s switch { Size.B => 1, Size.W => 2, _ => 4 };
    private static Expression ToUint(Expression x) => E.Convert(x, typeof(uint));
    private static Expression ToInt(Expression x) => E.Convert(x, typeof(int));
    private static Expression SignExt(Expression v, Size s) => s switch
    {
        Size.B => ToUint(ToInt(E.Convert(v, typeof(sbyte)))),
        Size.W => ToUint(ToInt(E.Convert(v, typeof(short)))),
        _ => v
    };

    private Expression Cond(int cc)
    {
        Expression Bit(int m) => E.NotEqual(E.And(SrInt, Ki(m)), Ki(0));
        Expression Clr(int m) => E.Equal(E.And(SrInt, Ki(m)), Ki(0));
        Expression NxorV() => E.And(E.ExclusiveOr(E.RightShift(SrInt, Ki(3)), E.RightShift(SrInt, Ki(1))), Ki(1));
        return cc switch
        {
            0 => Kb(true),
            1 => Kb(false),
            2 => Clr(0x05),
            3 => Bit(0x05),
            4 => Clr(0x01),
            5 => Bit(0x01),
            6 => Clr(0x04),
            7 => Bit(0x04),
            8 => Clr(0x02),
            9 => Bit(0x02),
            10 => Clr(0x08),
            11 => Bit(0x08),
            12 => E.Equal(NxorV(), Ki(0)),
            13 => E.NotEqual(NxorV(), Ki(0)),
            14 => E.AndAlso(Clr(0x04), E.Equal(NxorV(), Ki(0))),
            _ => E.OrElse(Bit(0x04), E.NotEqual(NxorV(), Ki(0)))
        };
    }

    // ---------------------------------------------------------------- operands

    private Expression IndexValue(Operand o)
    {
        Expression r = o.IndexIsAddr ? An(o.IndexReg) : Dn(o.IndexReg);
        return o.IndexLong ? r : ToUint(ToInt(E.Convert(r, typeof(short))));
    }

    private Loc Resolve(Operand o, Size sz)
    {
        switch (o.Kind)
        {
            case OpKind.Dn: return new Loc(LK.Dn, Ki(o.Reg));
            case OpKind.An: return new Loc(LK.An, Ki(o.Reg));
            case OpKind.Ind:
            {
                var t = Tmp();
                Emit(E.Assign(t, An(o.Reg)));
                return new Loc(LK.Mem, t);
            }
            case OpKind.PostInc:
            {
                var t = Tmp();
                int n = o.Reg == 7 && sz == Size.B ? 2 : Bytes(sz);
                Emit(E.Assign(t, An(o.Reg)));
                Emit(E.AddAssign(An(o.Reg), Ku((uint)n)));
                return new Loc(LK.Mem, t);
            }
            case OpKind.PreDec:
            {
                var t = Tmp();
                int n = o.Reg == 7 && sz == Size.B ? 2 : Bytes(sz);
                Emit(E.SubtractAssign(An(o.Reg), Ku((uint)n)));
                Emit(E.Assign(t, An(o.Reg)));
                return new Loc(LK.Mem, t);
            }
            case OpKind.Disp:
            {
                var t = Tmp();
                Emit(E.Assign(t, E.Add(An(o.Reg), Ku(unchecked((uint)o.Disp)))));
                return new Loc(LK.Mem, t);
            }
            case OpKind.Index:
            {
                var t = Tmp();
                Emit(E.Assign(t, E.Add(E.Add(An(o.Reg), IndexValue(o)), Ku(unchecked((uint)o.Disp)))));
                return new Loc(LK.Mem, t);
            }
            case OpKind.AbsW:
            case OpKind.AbsL:
            case OpKind.PcDisp:
                return new Loc(LK.Mem, Ku(o.Value));
            case OpKind.PcIndex:
            {
                var t = Tmp();
                Emit(E.Assign(t, E.Add(Ku(o.Value), IndexValue(o))));
                return new Loc(LK.Mem, t);
            }
            case OpKind.Imm:
                return new Loc(LK.Imm, Ku(o.Value));
            default:
                throw new NotSupportedException();
        }
    }

    private int RegOf(Loc l) => (int)((ConstantExpression)l.X).Value!;

    private Expression Read(Loc l, Size s)
    {
        switch (l.K)
        {
            case LK.Dn: return s == Size.L ? Dn(RegOf(l)) : And(Dn(RegOf(l)), Mask(s));
            case LK.An: return s == Size.L ? An(RegOf(l)) : And(An(RegOf(l)), Mask(s));
            case LK.Imm: return l.X;
            default: return Call(s == Size.B ? mR8 : s == Size.W ? mR16 : mR32, l.X);
        }
    }

    private Expression Write(Loc l, Size s, Expression val)
    {
        switch (l.K)
        {
            case LK.Dn:
                if (s == Size.L) return E.Assign(Dn(RegOf(l)), val);
                return E.Assign(Dn(RegOf(l)), Or(And(Dn(RegOf(l)), Keep(s)), And(val, Mask(s))));
            case LK.An:
                return E.Assign(An(RegOf(l)), val);
            case LK.Mem:
                return s switch
                {
                    Size.B => Call(mW8, l.X, E.Convert(val, typeof(byte))),
                    Size.W => Call(mW16, l.X, E.Convert(val, typeof(ushort))),
                    _ => Call(mW32, l.X, val)
                };
            default: throw new NotSupportedException();
        }
    }

    private Expression Logic(Expression v, Size s) => Call(mLogic, v, KSize(s));
    private static Expression Reg(ExprLifter x, Operand o) => o.Kind == OpKind.An ? x.An(o.Reg) : x.Dn(o.Reg);

    // ---------------------------------------------------------------- instructions (same coverage as Lifter)

    private bool Dispatch()
    {
        switch (i.Op)
        {
            case Mn.NOP: return true;
            case Mn.MOVEQ:
            {
                uint v = unchecked((uint)i.Src.Disp);
                Emit(E.Assign(Dn(i.Dst.Reg), Ku(v)));
                Emit(Logic(Ku(v), Size.L));
                return true;
            }
            case Mn.MOVE: return LiftMove();
            case Mn.MOVEA:
            {
                var src = Resolve(i.Src, i.Size);
                var v = Tmp();
                Emit(E.Assign(v, Read(src, i.Size)));
                Emit(E.Assign(An(i.Dst.Reg), SignExt(v, i.Size)));
                return true;
            }
            case Mn.LEA:
            {
                var src = Resolve(i.Src, Size.L);
                Emit(E.Assign(An(i.Dst.Reg), src.X));
                return true;
            }
            case Mn.PEA:
            {
                var dst = Resolve(i.Dst, Size.L);
                Emit(Call(mPush32, dst.X));
                return true;
            }
            case Mn.ADD: case Mn.SUB: case Mn.AND: case Mn.OR: case Mn.EOR: return LiftArith(i.Op);
            case Mn.ADDA: case Mn.SUBA:
            {
                var src = Resolve(i.Src, i.Size);
                var v = Tmp();
                Emit(E.Assign(v, SignExt(Read(src, i.Size), i.Size)));
                Emit(E.Assign(An(i.Dst.Reg), i.Op == Mn.SUBA ? E.Subtract(An(i.Dst.Reg), v) : E.Add(An(i.Dst.Reg), v)));
                return true;
            }
            case Mn.ADDQ: case Mn.SUBQ:
            {
                uint data = i.Src.Value;
                bool sub = i.Op == Mn.SUBQ;
                if (i.Dst.Kind == OpKind.An)
                {
                    Emit(E.Assign(An(i.Dst.Reg), sub ? E.Subtract(An(i.Dst.Reg), Ku(data)) : E.Add(An(i.Dst.Reg), Ku(data))));
                    return true;
                }
                var dst = Resolve(i.Dst, i.Size);
                var cur = Tmp(); var r = Tmp();
                Emit(E.Assign(cur, Read(dst, i.Size)));
                Emit(E.Assign(r, sub
                    ? Call(mSub, Ku(data), cur, KSize(i.Size), Kb(false), Kb(true))
                    : Call(mAdd, Ku(data), cur, KSize(i.Size), Kb(false))));
                Emit(Write(dst, i.Size, r));
                return true;
            }
            case Mn.ORI: case Mn.ANDI: case Mn.EORI: case Mn.ADDI: case Mn.SUBI: case Mn.CMPI: return LiftImmediate(i.Op);
            case Mn.CMP:
            {
                var src = Resolve(i.Src, i.Size);
                var v = Tmp();
                Emit(E.Assign(v, Read(src, i.Size)));
                Emit(Call(mSub, v, Dn(i.Dst.Reg), KSize(i.Size), Kb(false), Kb(false)));
                return true;
            }
            case Mn.CMPA:
            {
                var src = Resolve(i.Src, i.Size);
                var v = Tmp();
                Emit(E.Assign(v, SignExt(Read(src, i.Size), i.Size)));
                Emit(Call(mSub, v, An(i.Dst.Reg), KSize(Size.L), Kb(false), Kb(false)));
                return true;
            }
            case Mn.CMPM:
            {
                Size sz = i.Size;
                uint ys = (uint)(i.Src.Reg == 7 && sz == Size.B ? 2 : Bytes(sz)), xs = (uint)(i.Dst.Reg == 7 && sz == Size.B ? 2 : Bytes(sz));
                var sv = Tmp(); var dv = Tmp();
                Emit(E.Assign(sv, Read(new Loc(LK.Mem, An(i.Src.Reg)), sz)));
                Emit(E.AddAssign(An(i.Src.Reg), Ku(ys)));
                Emit(E.Assign(dv, Read(new Loc(LK.Mem, An(i.Dst.Reg)), sz)));
                Emit(E.AddAssign(An(i.Dst.Reg), Ku(xs)));
                Emit(Call(mSub, sv, dv, KSize(sz), Kb(false), Kb(false)));
                return true;
            }
            case Mn.CLR:
            {
                var dst = Resolve(i.Dst, i.Size);
                Emit(Logic(Ku(0), i.Size));
                Emit(Write(dst, i.Size, Ku(0)));
                return true;
            }
            case Mn.NEG: case Mn.NEGX:
            {
                var dst = Resolve(i.Dst, i.Size);
                var cur = Tmp();
                Emit(E.Assign(cur, Read(dst, i.Size)));
                Emit(Write(dst, i.Size, Call(mSub, cur, Ku(0), KSize(i.Size), Kb(i.Op == Mn.NEGX), Kb(true))));
                return true;
            }
            case Mn.NOT:
            {
                var dst = Resolve(i.Dst, i.Size);
                var r = Tmp();
                Emit(E.Assign(r, E.Not(Read(dst, i.Size))));
                Emit(Logic(r, i.Size));
                Emit(Write(dst, i.Size, r));
                return true;
            }
            case Mn.TST:
            {
                var dst = Resolve(i.Dst, i.Size);
                Emit(Logic(Read(dst, i.Size), i.Size));
                return true;
            }
            case Mn.Scc:
            {
                var dst = Resolve(i.Dst, Size.B);
                Emit(Write(dst, Size.B, E.Condition(Cond(i.Cond), Ku(0xFF), Ku(0))));
                return true;
            }
            case Mn.SWAP:
            {
                int r = i.Dst.Reg;
                var v = Tmp();
                Emit(E.Assign(v, E.Or(E.LeftShift(Dn(r), Ki(16)), E.RightShift(Dn(r), Ki(16)))));
                Emit(E.Assign(Dn(r), v));
                Emit(Logic(v, Size.L));
                return true;
            }
            case Mn.EXT:
            {
                int r = i.Dst.Reg;
                var v = Tmp();
                if (i.Size == Size.W)
                {
                    Emit(E.Assign(v, And(ToUint(ToInt(E.Convert(Dn(r), typeof(sbyte)))), 0xFFFF)));
                    Emit(E.Assign(Dn(r), Or(And(Dn(r), 0xFFFF0000u), v)));
                    Emit(Logic(v, Size.W));
                }
                else
                {
                    Emit(E.Assign(v, ToUint(ToInt(E.Convert(Dn(r), typeof(short))))));
                    Emit(E.Assign(Dn(r), v));
                    Emit(Logic(v, Size.L));
                }
                return true;
            }
            case Mn.EXG:
            {
                var t = Tmp();
                Emit(E.Assign(t, Reg(this, i.Src)));
                Emit(E.Assign(Reg(this, i.Src), Reg(this, i.Dst)));
                Emit(E.Assign(Reg(this, i.Dst), t));
                return true;
            }
            case Mn.LINK:
                Emit(Call(mPush32, An(i.Src.Reg)));
                Emit(E.Assign(An(i.Src.Reg), An(7)));
                Emit(E.AddAssign(An(7), Ku(unchecked((uint)i.Dst.Disp))));
                return true;
            case Mn.UNLK:
                Emit(E.Assign(An(7), An(i.Dst.Reg)));
                Emit(E.Assign(An(i.Dst.Reg), Call(mPop32)));
                return true;
            case Mn.BTST: case Mn.BCHG: case Mn.BCLR: case Mn.BSET: return LiftBit(i.Op);
            case Mn.ASR: case Mn.ASL: case Mn.LSR: case Mn.LSL:
            case Mn.ROXR: case Mn.ROXL: case Mn.ROR: case Mn.ROL: return LiftShift();
            case Mn.MULU: case Mn.MULS:
            {
                var src = Resolve(i.Src, Size.W);
                var s = Tmp(); var r = Tmp();
                int n = i.Dst.Reg;
                Emit(E.AddAssign(E.Field(c, fCycles), E.Constant(34L)));
                Emit(E.Assign(s, Read(src, Size.W)));
                Emit(E.Assign(r, i.Op == Mn.MULU
                    ? E.Multiply(And(Dn(n), 0xFFFF), And(s, 0xFFFF))
                    : ToUint(E.Multiply(ToInt(E.Convert(Dn(n), typeof(short))), ToInt(E.Convert(s, typeof(short)))))));
                Emit(E.Assign(Dn(n), r));
                Emit(Logic(r, Size.L));
                return true;
            }
            case Mn.DIVU: case Mn.DIVS:
            {
                var src = Resolve(i.Src, Size.W);
                Emit(E.Assign(Pc, Ku(i.NextAddress)));                 // a divide-by-zero exception pushes this
                Emit(Call(mDivide, Read(src, Size.W), Ki(i.Dst.Reg), Kb(i.Op == Mn.DIVS)));
                Emit(E.IfThen(E.NotEqual(Pc, Ku(i.NextAddress)), E.Return(ret)));   // exception raised: PC is the handler
                return true;
            }
            case Mn.MOVEM: return LiftMovem();

            // ---- control flow ----
            case Mn.BRA:
                setsPc = true;
                Emit(E.Assign(Pc, Ku(i.Target!.Value)));
                return true;
            case Mn.BSR:
                setsPc = true;
                Emit(Call(mPush32, Ku(i.NextAddress)));
                Emit(E.Assign(Pc, Ku(i.Target!.Value)));
                return true;
            case Mn.Bcc:
                setsPc = true;
                Emit(E.Assign(Pc, E.Condition(Cond(i.Cond), Ku(i.Target!.Value), Ku(i.NextAddress))));
                return true;
            case Mn.Dbcc:
            {
                setsPc = true;
                int r = i.Src.Reg;
                var cnt = Tmp(typeof(ushort));
                Emit(E.Assign(Pc, Ku(i.NextAddress)));
                Emit(E.IfThen(E.Not(Cond(i.Cond)), E.Block(
                    E.Assign(cnt, E.Convert(E.Subtract(Dn(r), Ku(1)), typeof(ushort))),
                    E.Assign(Dn(r), Or(And(Dn(r), 0xFFFF0000u), ToUint(cnt))),
                    E.IfThen(E.NotEqual(cnt, E.Constant((ushort)0xFFFF)), E.Assign(Pc, Ku(i.Target!.Value))))));
                return true;
            }
            case Mn.JSR: case Mn.JMP:
            {
                setsPc = true;
                var ea = Resolve(i.Dst, Size.L);
                if (i.Op == Mn.JSR) Emit(Call(mPush32, Ku(i.NextAddress)));
                Emit(E.Assign(Pc, ea.X));
                return true;
            }
            case Mn.RTS:
                setsPc = true;
                Emit(E.Assign(Pc, Call(mPop32)));
                return true;
            case Mn.RTR:
            {
                setsPc = true;
                var ccr = Tmp(typeof(ushort));
                Emit(E.Assign(ccr, Call(mPop16)));
                Emit(E.Assign(Sr, E.Convert(E.Or(E.And(SrInt, Ki(0xFF00)), E.And(ToInt(ccr), Ki(0x1F))), typeof(ushort))));
                Emit(E.Assign(Pc, Call(mPop32)));
                return true;
            }
            default:
                return false;
        }
    }

    private bool LiftMove()
    {
        Operand s = i.Src, dd = i.Dst;
        if (dd.Kind is OpKind.Usp or OpKind.Sr) return false;
        if (s.Kind == OpKind.Usp) return false;
        if (s.Kind == OpKind.Sr)                                   // MOVE SR,<ea>
        {
            var dl = Resolve(dd, Size.W);
            Emit(Write(dl, Size.W, ToUint(Sr)));
            return true;
        }
        if (dd.Kind == OpKind.Ccr)                                 // MOVE <ea>,CCR
        {
            var sl = Resolve(s, Size.W);
            Emit(E.Assign(Sr, E.Convert(E.Or(E.And(SrInt, Ki(0xFF00)), E.And(ToInt(Read(sl, Size.W)), Ki(0x1F))), typeof(ushort))));
            return true;
        }
        Size sz = i.Size;
        var src = Resolve(s, sz);
        var v = Tmp();
        Emit(E.Assign(v, Read(src, sz)));
        var dst = Resolve(dd, sz);
        Emit(Logic(v, sz));
        Emit(Write(dst, sz, v));
        return true;
    }

    private bool LiftArith(Mn m)
    {
        Size sz = i.Size;
        if (m == Mn.EOR)
        {
            var dl = Resolve(i.Dst, sz);
            var r = Tmp();
            Emit(E.Assign(r, E.ExclusiveOr(Read(dl, sz), Dn(i.Src.Reg))));
            Emit(Logic(r, sz));
            Emit(Write(dl, sz, r));
            return true;
        }
        if (i.Dst.Kind == OpKind.Dn)           // <ea>,Dn
        {
            int n = i.Dst.Reg;
            var src = Resolve(i.Src, sz);
            var v = Tmp(); var r = Tmp();
            Emit(E.Assign(v, Read(src, sz)));
            switch (m)
            {
                case Mn.ADD: Emit(E.Assign(r, Call(mAdd, v, Dn(n), KSize(sz), Kb(false)))); break;
                case Mn.SUB: Emit(E.Assign(r, Call(mSub, v, Dn(n), KSize(sz), Kb(false), Kb(true)))); break;
                case Mn.AND: Emit(E.Assign(r, E.And(Dn(n), v))); Emit(Logic(r, sz)); break;
                default: Emit(E.Assign(r, E.Or(Dn(n), v))); Emit(Logic(r, sz)); break;
            }
            Emit(Write(new Loc(LK.Dn, Ki(n)), sz, r));
        }
        else                                   // Dn,<ea>
        {
            int n = i.Src.Reg;
            var dst = Resolve(i.Dst, sz);
            var cur = Tmp(); var r = Tmp();
            Emit(E.Assign(cur, Read(dst, sz)));
            switch (m)
            {
                case Mn.ADD: Emit(E.Assign(r, Call(mAdd, Dn(n), cur, KSize(sz), Kb(false)))); break;
                case Mn.SUB: Emit(E.Assign(r, Call(mSub, Dn(n), cur, KSize(sz), Kb(false), Kb(true)))); break;
                case Mn.AND: Emit(E.Assign(r, E.And(cur, Dn(n)))); Emit(Logic(r, sz)); break;
                default: Emit(E.Assign(r, E.Or(cur, Dn(n)))); Emit(Logic(r, sz)); break;
            }
            Emit(Write(dst, sz, r));
        }
        return true;
    }

    private bool LiftImmediate(Mn m)
    {
        if (i.Dst.Kind == OpKind.Sr) return false;
        uint imm = i.Src.Value;
        if (i.Dst.Kind == OpKind.Ccr)
        {
            var ccr = Tmp(typeof(int));
            Emit(E.Assign(ccr, E.And(SrInt, Ki(0x1F))));
            Expression k = Ki((int)imm);
            Emit(E.Assign(ccr, m switch { Mn.ORI => E.Or(ccr, k), Mn.ANDI => E.And(ccr, k), _ => E.ExclusiveOr(ccr, k) }));
            Emit(E.Assign(Sr, E.Convert(E.Or(E.And(SrInt, Ki(0xFF00)), E.And(ccr, Ki(0x1F))), typeof(ushort))));
            return true;
        }
        Size sz = i.Size;
        var dst = Resolve(i.Dst, sz);
        var cur = Tmp(); var r = Tmp();
        Emit(E.Assign(cur, Read(dst, sz)));
        switch (m)
        {
            case Mn.ORI: Emit(E.Assign(r, E.Or(cur, Ku(imm)))); Emit(Logic(r, sz)); break;
            case Mn.ANDI: Emit(E.Assign(r, E.And(cur, Ku(imm)))); Emit(Logic(r, sz)); break;
            case Mn.EORI: Emit(E.Assign(r, E.ExclusiveOr(cur, Ku(imm)))); Emit(Logic(r, sz)); break;
            case Mn.SUBI: Emit(E.Assign(r, Call(mSub, Ku(imm), cur, KSize(sz), Kb(false), Kb(true)))); break;
            case Mn.ADDI: Emit(E.Assign(r, Call(mAdd, Ku(imm), cur, KSize(sz), Kb(false)))); break;
            default: Emit(Call(mSub, Ku(imm), cur, KSize(sz), Kb(false), Kb(false))); return true;   // CMPI
        }
        Emit(Write(dst, sz, r));
        return true;
    }

    private bool LiftBit(Mn m)
    {
        bool toDn = i.Dst.Kind == OpKind.Dn;
        Size sz = toDn ? Size.L : Size.B;
        int bitMask = toDn ? 31 : 7;
        var bit = Tmp(typeof(int));
        if (i.Src.Kind == OpKind.Dn) Emit(E.Assign(bit, E.And(ToInt(Dn(i.Src.Reg)), Ki(bitMask))));
        else Emit(E.Assign(bit, Ki((int)(i.Src.Value & (uint)bitMask))));
        var dst = Resolve(i.Dst, sz);
        var val = Tmp();
        Emit(E.Assign(val, Read(dst, sz)));
        Emit(Call(mSetFlag, E.Constant(Cpu68k.FZ), E.Equal(E.And(E.RightShift(val, bit), Ku(1)), Ku(0))));
        if (m == Mn.BTST) return true;
        Expression one = E.LeftShift(Ku(1), bit);
        Expression upd = m switch { Mn.BCHG => E.ExclusiveOr(val, one), Mn.BCLR => E.And(val, E.Not(one)), _ => E.Or(val, one) };
        Emit(Write(dst, sz, upd));
        return true;
    }

    private bool LiftShift()
    {
        int k = (int)i.Op - (int)Mn.ASR;
        int type = k / 2;
        bool left = (k & 1) != 0;
        if (i.Src.IsNone)                       // memory form: one bit, word size
        {
            var dst = Resolve(i.Dst, Size.W);
            var cur = Tmp();
            Emit(E.Assign(cur, Read(dst, Size.W)));
            Emit(Write(dst, Size.W, Call(mShift, Ki(type), Kb(left), cur, Ki(1), KSize(Size.W))));
            return true;
        }
        Size sz = i.Size;
        Expression count = i.Src.Kind == OpKind.Dn ? ToInt(And(Dn(i.Src.Reg), 63)) : Ki((int)i.Src.Value);
        var r = Tmp();
        Emit(E.Assign(r, Call(mShift, Ki(type), Kb(left), Dn(i.Dst.Reg), count, KSize(sz))));
        Emit(Write(new Loc(LK.Dn, Ki(i.Dst.Reg)), sz, r));
        return true;
    }

    private bool LiftMovem()
    {
        Size sz = i.Size;
        uint step = (uint)Bytes(sz);
        bool toRegs = i.Dst.Kind == OpKind.RegList;
        uint mask = (toRegs ? i.Dst : i.Src).Value;       // bit r = register r (D0..D7 = 0..7, A0..A7 = 8..15)
        Operand ea = toRegs ? i.Src : i.Dst;

        if (!toRegs && ea.Kind == OpKind.PreDec)
        {
            var addr = Tmp(); var init = Tmp();
            Emit(E.Assign(addr, An(ea.Reg)));
            Emit(E.Assign(init, addr));
            for (int r = 15; r >= 0; r--)
            {
                if ((mask & (1u << r)) == 0) continue;
                Expression val = r < 8 ? Dn(r) : (r - 8 == ea.Reg ? init : An(r - 8));
                Emit(E.SubtractAssign(addr, Ku(step)));
                Emit(Write(new Loc(LK.Mem, addr), sz, val));
            }
            Emit(E.Assign(An(ea.Reg), addr));
            return true;
        }

        var a0 = Tmp();
        if (ea.Kind == OpKind.PostInc) Emit(E.Assign(a0, An(ea.Reg)));
        else
        {
            var l = Resolve(ea, sz);
            Emit(E.Assign(a0, l.X));
        }
        for (int r = 0; r < 16; r++)
        {
            if ((mask & (1u << r)) == 0) continue;
            Expression reg = r < 8 ? Dn(r) : An(r - 8);
            if (toRegs) Emit(E.Assign(reg, SignExt(Read(new Loc(LK.Mem, a0), sz), sz)));
            else Emit(Write(new Loc(LK.Mem, a0), sz, reg));
            Emit(E.AddAssign(a0, Ku(step)));
        }
        if (ea.Kind == OpKind.PostInc) Emit(E.Assign(An(ea.Reg), a0));
        return true;
    }
}
