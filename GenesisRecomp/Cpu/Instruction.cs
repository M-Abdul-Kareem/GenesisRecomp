namespace GenesisRecomp.Cpu;

/// <summary>How execution continues after an instruction. Drives code discovery.</summary>
public enum FlowKind
{
    Normal,        // falls through to the next instruction
    Jump,          // unconditional jump to Target (BRA, JMP abs)
    Branch,        // conditional: Target or fall through (Bcc, DBcc)
    Call,          // subroutine call to Target, then returns (BSR, JSR abs)
    Return,        // RTS / RTE / RTR
    Halt,          // ILLEGAL or undecodable bytes: stop following this path
    IndirectJump,  // JMP through a register/table - target unknown statically
    IndirectCall   // JSR through a register/table - target unknown statically
}

public sealed class Instruction
{
    public uint Address { get; init; }
    public int Length { get; init; }
    public string Mnemonic { get; init; } = "";
    public string Operands { get; init; } = "";
    public FlowKind Flow { get; init; }
    public uint? Target { get; init; }
    /// <summary>Base address when the operand is d8(PC,Xn) - the usual shape of a table lookup.</summary>
    public uint? PcIndexedBase { get; init; }
    /// <summary>Absolute address when the operand is d16(PC) (e.g. LEA tbl(PC),An).</summary>
    public uint? PcRelAddress { get; init; }
    public bool IsValid { get; init; } = true;

    public uint NextAddress => Address + (uint)Length;

    public string Text => Operands.Length == 0 ? Mnemonic : $"{Mnemonic,-9}{Operands}";
}
