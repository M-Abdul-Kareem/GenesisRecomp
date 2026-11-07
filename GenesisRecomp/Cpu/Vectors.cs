namespace GenesisRecomp.Cpu;

/// <summary>The 68000 exception vector table: 64 big-endian 32-bit addresses at the start of the ROM.</summary>
public static class Vectors
{
    public static string Name(int i) => i switch
    {
        0 => "Initial SP",
        1 => "Reset (entry point)",
        2 => "Bus error",
        3 => "Address error",
        4 => "Illegal instruction",
        5 => "Divide by zero",
        6 => "CHK",
        7 => "TRAPV",
        8 => "Privilege violation",
        9 => "Trace",
        10 => "Line-A",
        11 => "Line-F",
        25 => "Level 1 interrupt",
        26 => "Level 2 interrupt (external)",
        27 => "Level 3 interrupt",
        28 => "Level 4 interrupt (HBlank)",
        29 => "Level 5 interrupt",
        30 => "Level 6 interrupt (VBlank)",
        31 => "Level 7 interrupt",
        >= 32 and <= 47 => $"TRAP #{i - 32}",
        _ => $"Vector {i}"
    };

    public static uint[] Read(byte[] rom)
    {
        var v = new uint[64];
        for (int i = 0; i < 64; i++) v[i] = RomHeader.ReadU32(rom, i * 4);
        return v;
    }
}
