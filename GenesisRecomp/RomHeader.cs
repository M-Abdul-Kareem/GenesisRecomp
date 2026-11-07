using System.Text;

namespace GenesisRecomp;

/// <summary>
/// Parses the Sega Genesis / Mega Drive ROM header.
/// Layout: 68000 vector table at 0x000, "SEGA" header block at 0x100-0x1FF.
/// All multi-byte values in a Genesis ROM are big-endian.
/// </summary>
public sealed class RomHeader
{
    public uint InitialStackPointer { get; private init; }
    public uint InitialProgramCounter { get; private init; }

    public string SystemType { get; private init; } = "";
    public string Copyright { get; private init; } = "";
    public string DomesticName { get; private init; } = "";
    public string OverseasName { get; private init; } = "";
    public string SerialNumber { get; private init; } = "";
    public ushort StoredChecksum { get; private init; }
    public ushort CalculatedChecksum { get; private init; }
    public string DeviceSupport { get; private init; } = "";
    public uint RomStart { get; private init; }
    public uint RomEnd { get; private init; }
    public uint RamStart { get; private init; }
    public uint RamEnd { get; private init; }
    public string Regions { get; private init; } = "";

    public bool ChecksumValid => StoredChecksum == CalculatedChecksum;

    public static RomHeader Parse(byte[] rom)
    {
        if (rom.Length < 0x200)
            throw new InvalidDataException("File is too small to be a Genesis ROM.");

        string system = Ascii(rom, 0x100, 16);
        if (!system.StartsWith("SEGA"))
            throw new InvalidDataException(
                "No 'SEGA' marker at 0x100. Not a raw .bin Genesis ROM (or it is interleaved/.smd).");

        return new RomHeader
        {
            InitialStackPointer = ReadU32(rom, 0x00),
            InitialProgramCounter = ReadU32(rom, 0x04),
            SystemType = system,
            Copyright = Ascii(rom, 0x110, 16),
            DomesticName = Ascii(rom, 0x120, 48),
            OverseasName = Ascii(rom, 0x150, 48),
            SerialNumber = Ascii(rom, 0x180, 14),
            StoredChecksum = ReadU16(rom, 0x18E),
            CalculatedChecksum = ComputeChecksum(rom),
            DeviceSupport = Ascii(rom, 0x190, 16),
            RomStart = ReadU32(rom, 0x1A0),
            RomEnd = ReadU32(rom, 0x1A4),
            RamStart = ReadU32(rom, 0x1A8),
            RamEnd = ReadU32(rom, 0x1AC),
            Regions = Ascii(rom, 0x1F0, 16),
        };
    }

    /// <summary>Checksum = sum of all 16-bit words from 0x200 to the end, truncated to 16 bits.</summary>
    private static ushort ComputeChecksum(byte[] rom)
    {
        uint sum = 0;
        for (int i = 0x200; i + 1 < rom.Length; i += 2)
            sum += ReadU16(rom, i);
        return (ushort)(sum & 0xFFFF);
    }

    public static ushort ReadU16(byte[] data, int offset) =>
        (ushort)((data[offset] << 8) | data[offset + 1]);

    public static uint ReadU32(byte[] data, int offset) =>
        ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) |
        ((uint)data[offset + 2] << 8) | data[offset + 3];

    private static string Ascii(byte[] data, int offset, int length) =>
        Encoding.ASCII.GetString(data, offset, length).Trim();
}
