using System.Text;
using GenesisRecomp.Cpu;

namespace GenesisRecomp;

public static class Listing
{
    public static string Format(Instruction ins, byte[] rom)
    {
        var words = new StringBuilder();
        for (int i = 0; i < ins.Length && i < 10; i += 2)
            words.Append(RomHeader.ReadU16(rom, (int)ins.Address + i).ToString("X4")).Append(' ');
        return $"{ins.Address:X8}  {words,-25}{ins.Text}";
    }

    public static void Write(TextWriter w, CodeMap map, byte[] rom, Dictionary<uint, string> names)
    {
        uint expected = uint.MaxValue;
        foreach (var (addr, ins) in map.Instructions)
        {
            if (addr != expected)
                w.WriteLine();
            if (map.Labels.Contains(addr))
            {
                string label = names.TryGetValue(addr, out var n) ? $"{n}" : $"L_{addr:X6}";
                w.WriteLine($"{label}:");
            }
            w.WriteLine(Format(ins, rom));
            expected = ins.NextAddress;
        }
    }
}
