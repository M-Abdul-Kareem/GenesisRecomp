namespace GenesisRecomp.Cpu;

/// <summary>The 68000's view of the machine: everything it reads or writes goes through here.</summary>
public interface IBus
{
    byte Read8(uint address);
    ushort Read16(uint address);
    uint Read32(uint address);
    void Write8(uint address, byte value);
    void Write16(uint address, ushort value);
    void Write32(uint address, uint value);
}

public sealed class AddressErrorException : Exception
{
    public uint Address { get; }
    public AddressErrorException(uint address) : base($"Address error at 0x{address:X8}") => Address = address;
}
