namespace GenesisRecomp.Cpu;

/// <summary>What kind of thing an operand is. Everything a code generator needs is in <see cref="Operand"/> - no text parsing.</summary>
public enum OpKind : byte
{
    None,
    Dn,        // data register                      Reg
    An,        // address register                   Reg
    Ind,       // (An)                               Reg
    PostInc,   // (An)+                              Reg
    PreDec,    // -(An)                              Reg
    Disp,      // d16(An)                            Reg, Disp
    Index,     // d8(An,Xn.s)                        Reg, Disp, IndexReg/IndexIsAddr/IndexLong
    AbsW,      // (xxx).w   sign-extended address    Value
    AbsL,      // (xxx).l                            Value
    PcDisp,    // d16(PC)   resolved address         Value
    PcIndex,   // d8(PC,Xn) resolved base address    Value, IndexReg/IndexIsAddr/IndexLong
    Imm,       // immediate, printed in hex          Value, Sz (how many digits / how wide)
    ImmS,      // signed immediate, printed in hex   Disp   (MOVEQ, LINK)
    Quick,     // small immediate, printed decimal   Value  (ADDQ data, bit number, shift count, TRAP vector)
    Target,    // branch / DBcc destination          Value
    RegList,   // MOVEM register set                 Value: bit i set = register i (D0..D7 = 0..7, A0..A7 = 8..15), already un-reversed
    Sr, Ccr, Usp
}

public readonly struct Operand
{
    public OpKind Kind { get; init; }
    public int Reg { get; init; }
    public int Disp { get; init; }
    public uint Value { get; init; }
    public int IndexReg { get; init; }
    public bool IndexIsAddr { get; init; }
    public bool IndexLong { get; init; }
    public Size Sz { get; init; }

    public bool IsNone => Kind == OpKind.None;

    /// <summary>True when the operand names a location in memory (so reading or writing it goes through the bus).</summary>
    public bool IsMemory => Kind is OpKind.Ind or OpKind.PostInc or OpKind.PreDec or OpKind.Disp or OpKind.Index
                                 or OpKind.AbsW or OpKind.AbsL or OpKind.PcDisp or OpKind.PcIndex;

    public static Operand D(int r) => new() { Kind = OpKind.Dn, Reg = r };
    public static Operand A(int r) => new() { Kind = OpKind.An, Reg = r };
    public static Operand Of(OpKind k) => new() { Kind = k };
    public static Operand Imm(uint v, Size s) => new() { Kind = OpKind.Imm, Value = v, Sz = s };
    public static Operand ImmSigned(int v) => new() { Kind = OpKind.ImmS, Disp = v };
    public static Operand Quick(int v) => new() { Kind = OpKind.Quick, Value = (uint)v };
    public static Operand Target(uint a) => new() { Kind = OpKind.Target, Value = a };
}

/// <summary>Instruction families. The name is the assembler mnemonic (size suffix and condition are separate fields).</summary>
public enum Mn
{
    Invalid,
    ORI, ANDI, SUBI, ADDI, EORI, CMPI,
    BTST, BCHG, BCLR, BSET,
    MOVEP, MOVE, MOVEA, MOVEQ, MOVEM,
    LEA, PEA, CHK, TRAP, TRAPV, LINK, UNLK, SWAP, EXT, EXG,
    RESET, NOP, STOP, RTE, RTS, RTR, ILLEGAL, JSR, JMP,
    NBCD, TAS, NEGX, CLR, NEG, NOT, TST,
    ADDQ, SUBQ, Scc, Dbcc, Bcc, BRA, BSR,
    DIVU, DIVS, MULU, MULS, SBCD, ABCD, OR, AND, EOR,
    ADD, SUB, ADDA, SUBA, ADDX, SUBX,
    CMP, CMPA, CMPM,
    // Shifts and rotates. Order matters: ShiftMn() computes ASR + type*2 + (left ? 1 : 0).
    ASR, ASL, LSR, LSL, ROXR, ROXL, ROR, ROL
}

/// <summary>
/// One decoded 68000 instruction in structured form.
/// Operand order follows assembler syntax (Src first). An instruction with a single operand keeps it in <see cref="Dst"/>.
/// </summary>
public sealed class DecodedInst
{
    public uint Address { get; init; }
    public int Length { get; init; }
    public ushort OpWord { get; init; }
    public Mn Op { get; init; }
    /// <summary>Operation size. Only meaningful when <see cref="Sized"/> is true (the assembler would print .B/.W/.L).</summary>
    public Size Size { get; init; }
    public bool Sized { get; init; }
    /// <summary>Condition code 0-15 for Bcc / DBcc / Scc.</summary>
    public int Cond { get; init; }
    public Operand Src { get; init; }
    public Operand Dst { get; init; }

    public FlowKind Flow { get; init; }
    public uint? Target { get; init; }
    public uint? PcIndexedBase { get; init; }
    public uint? PcRelAddress { get; init; }
    public bool IsValid { get; init; } = true;
    /// <summary>For undecodable bytes: the raw word (if it could be read).</summary>
    public bool WordKnown { get; init; }

    public uint NextAddress => Address + (uint)Length;

    public string Mnemonic => AsmFormat.Mnemonic(this);
    public string Operands => AsmFormat.Operands(this);
    public string Text => AsmFormat.Text(this);
    public override string ToString() => $"{Address:X8}  {Text}";
}
