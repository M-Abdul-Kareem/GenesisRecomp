using System.Text;

namespace GenesisRecomp.Cpu;

/// <summary>Turns a <see cref="DecodedInst"/> back into assembler text, byte-for-byte identical to the original disassembler's output.</summary>
public static class AsmFormat
{
    private static readonly string[] Cond =
        { "T", "F", "HI", "LS", "CC", "CS", "NE", "EQ", "VC", "VS", "PL", "MI", "GE", "LT", "GT", "LE" };

    public static string Sfx(Size s) => s switch { Size.B => ".B", Size.W => ".W", _ => ".L" };

    public static string SDisp(int d) => d < 0 ? $"-${-d:X}" : $"${d:X}";

    public static string Mnemonic(DecodedInst i)
    {
        string name = i.Op switch
        {
            Mn.Bcc => "B" + Cond[i.Cond],
            Mn.Dbcc => i.Cond == 1 ? "DBRA" : "DB" + Cond[i.Cond],
            Mn.Scc => "S" + Cond[i.Cond],
            Mn.Invalid => "DC.W",
            _ => i.Op.ToString()
        };
        return i.Sized ? name + Sfx(i.Size) : name;
    }

    public static string Operands(DecodedInst i)
    {
        if (i.Op == Mn.Invalid) return i.WordKnown ? $"${i.OpWord:X4}" : "$????";
        if (i.Src.IsNone) return Operand(i.Dst);
        if (i.Dst.IsNone) return Operand(i.Src);
        return Operand(i.Src) + "," + Operand(i.Dst);
    }

    public static string Text(DecodedInst i)
    {
        string ops = Operands(i);
        string mn = Mnemonic(i);
        return ops.Length == 0 ? mn : $"{mn,-9}{ops}";
    }

    public static string Operand(Operand o)
    {
        switch (o.Kind)
        {
            case OpKind.None: return "";
            case OpKind.Dn: return $"D{o.Reg}";
            case OpKind.An: return $"A{o.Reg}";
            case OpKind.Ind: return $"(A{o.Reg})";
            case OpKind.PostInc: return $"(A{o.Reg})+";
            case OpKind.PreDec: return $"-(A{o.Reg})";
            case OpKind.Disp: return $"{SDisp(o.Disp)}(A{o.Reg})";
            case OpKind.Index: return $"{SDisp(o.Disp)}(A{o.Reg},{IndexText(o)})";
            case OpKind.AbsW: return $"(${(ushort)o.Value:X4}).w";
            case OpKind.AbsL: return $"(${o.Value:X8}).l";
            case OpKind.PcDisp: return $"${o.Value:X8}(PC)";
            case OpKind.PcIndex: return $"${o.Value:X8}(PC,{IndexText(o)})";
            case OpKind.Imm:
                return o.Sz switch
                {
                    Size.B => $"#${o.Value & 0xFF:X2}",
                    Size.W => $"#${o.Value & 0xFFFF:X4}",
                    _ => $"#${o.Value:X8}"
                };
            case OpKind.ImmS: return $"#{SDisp(o.Disp)}";
            case OpKind.Quick: return $"#{o.Value}";
            case OpKind.Target: return $"${o.Value:X8}";
            case OpKind.RegList: return RegList((ushort)o.Value);
            case OpKind.Sr: return "SR";
            case OpKind.Ccr: return "CCR";
            case OpKind.Usp: return "USP";
            default: return "?";
        }
    }

    private static string IndexText(Operand o) =>
        $"{(o.IndexIsAddr ? 'A' : 'D')}{o.IndexReg}.{(o.IndexLong ? 'L' : 'W')}";

    /// <summary>"D0-D3/A0-A2" from a normalised mask (bit i = register i, D0..D7 = 0..7, A0..A7 = 8..15).</summary>
    public static string RegList(ushort mask)
    {
        var sb = new StringBuilder();
        for (int g = 0; g < 2; g++)
        {
            char c = g == 0 ? 'D' : 'A';
            int i = 0;
            while (i < 8)
            {
                if ((mask & (1 << (g * 8 + i))) == 0) { i++; continue; }
                int j = i;
                while (j + 1 < 8 && (mask & (1 << (g * 8 + j + 1))) != 0) j++;
                if (sb.Length > 0) sb.Append('/');
                sb.Append(j == i ? $"{c}{i}" : $"{c}{i}-{c}{j}");
                i = j + 1;
            }
        }
        return sb.ToString();
    }
}
