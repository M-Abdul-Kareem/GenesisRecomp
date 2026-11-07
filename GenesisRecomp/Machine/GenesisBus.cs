using GenesisRecomp.Cpu;

namespace GenesisRecomp.Machine;

/// <summary>
/// The Mega Drive memory map as seen by the 68000 (24-bit address bus).
///   000000-3FFFFF cartridge ROM      A00000-A0FFFF Z80 space      A10000-A1001F I/O ports
///   A11100 Z80 bus request           A11200 Z80 reset             C00000-C0001F VDP (PSG at C00011)
///   E00000-FFFFFF 64 KB work RAM (mirrored)
/// Z80 space: 0000-1FFF RAM (mirrored to 3FFF), 4000-4003 YM2612, 6000 bank register, 7F11 PSG, 8000-FFFF window onto 68000 memory.
/// </summary>
public sealed class GenesisBus : IBus
{
    private readonly byte[] rom;
    public readonly byte[] Ram = new byte[0x10000];
    public readonly byte[] Z80Ram = new byte[0x2000];
    private readonly byte[] io = new byte[0x20];
    private bool z80BusRequested;
    private bool z80Reset = true;                   // the Z80 is held in reset at power-on
    private int z80Bank;                            // 9-bit bank register: address bits 15-23 of the Z80's 32 KB window

    public readonly Vdp Vdp;
    public readonly SoundSystem Sound = new();
    public readonly Z80 Z80;
    public readonly List<string> IoLog = new();
    public int IoLogLimit = 400;

    /// <summary>Master-clock time of the 68000 access in progress (set by the machine before each instruction).</summary>
    public long Now68k;
    /// <summary>Master-clock time of the Z80 instruction in progress.</summary>
    public long Z80Time;

    /// <summary>The Z80 runs only when it is out of reset and the 68000 has not taken its bus.</summary>
    public bool Z80Runnable => !z80Reset && !z80BusRequested;

    /// <summary>Pressed buttons of pad 1 (bit set = pressed): 0 Up, 1 Down, 2 Left, 3 Right, 4 B, 5 C, 6 A, 7 Start.</summary>
    public byte Pad1;

    public GenesisBus(byte[] rom)
    {
        this.rom = rom;
        Vdp = new Vdp(a => Read16(a));
        Z80 = new Z80(new Z80Side(this));
        io[0x01] = 0xA0;                            // version register: overseas, NTSC, no Sega CD, hardware rev 0
    }

    private void Log(string s) { if (IoLog.Count < IoLogLimit) IoLog.Add(s); }

    // ---------- what the Z80 sees ----------

    private sealed class Z80Side : IZ80Bus
    {
        private readonly GenesisBus b;
        public Z80Side(GenesisBus bus) { b = bus; }
        public byte Read(ushort a) => b.Z80Read(a);
        public void Write(ushort a, byte v) => b.Z80Write(a, v);
        public byte In(ushort port) => 0xFF;
        public void Out(ushort port, byte value) { }
    }

    private byte Z80Read(ushort a)
    {
        if (a < 0x4000) return Z80Ram[a & 0x1FFF];
        if (a < 0x6000) { Sound.SyncTo(Z80Time); return Sound.Ym.ReadStatus(); }
        if (a < 0x8000) return 0xFF;                                    // bank register / VDP ports are not readable from here
        return Read8((uint)((z80Bank << 15) | (a & 0x7FFF)));
    }

    private void Z80Write(ushort a, byte v)
    {
        if (a < 0x4000) { Z80Ram[a & 0x1FFF] = v; return; }
        if (a < 0x6000) { Sound.SyncTo(Z80Time); Sound.Ym.Write(a & 3, v); return; }
        if (a < 0x6100) { ShiftBank(v); return; }
        if (a < 0x8000)
        {
            if ((a & 0xFF00) == 0x7F00 && (a & 0xFF) is 0x11 or 0x13 or 0x15 or 0x17)
            {
                Sound.SyncTo(Z80Time);
                Sound.Psg.Write(v);
            }
            return;
        }
        Write8((uint)((z80Bank << 15) | (a & 0x7FFF)), v);
    }

    private void ShiftBank(byte v) => z80Bank = (z80Bank >> 1) | ((v & 1) << 8);

    // ---------- 68000 access to the Z80 area ----------

    private byte Z80AreaRead(uint a)
    {
        uint off = a & 0xFFFF;
        if (off < 0x4000) return Z80Ram[off & 0x1FFF];
        if (off < 0x6000) { Sound.SyncTo(Now68k); return Sound.Ym.ReadStatus(); }
        return 0xFF;
    }

    private void Z80AreaWrite(uint a, byte v)
    {
        uint off = a & 0xFFFF;
        if (off < 0x4000) { Z80Ram[off & 0x1FFF] = v; return; }
        if (off < 0x6000) { Sound.SyncTo(Now68k); Sound.Ym.Write((int)(off & 3), v); return; }
        if (off < 0x6100) ShiftBank(v);
    }

    private void SetZ80Reset(bool asserted)
    {
        if (asserted)
        {
            Z80.Reset();
            Sound.SyncTo(Now68k);
            Sound.Ym.Reset();                       // the reset line also resets the YM2612
        }
        z80Reset = asserted;
        Log($"Z80 reset line = {(z80Reset ? "asserted" : "released")}");
    }

    /// <summary>3-button pad: TH high returns Up/Down/Left/Right/B/C, TH low returns Up/Down/A/Start. Pressed = 0.</summary>
    private byte ReadPad(int port)
    {
        byte data = io[0x03 + port * 2], ctrl = io[0x09 + port * 2];
        bool th = (ctrl & 0x40) == 0 || (data & 0x40) != 0;
        int pressed = port == 0 ? Pad1 : 0;
        int v;
        if (th) v = 0x40 | (~pressed & 0x3F);
        else v = (~pressed & 0x03) | ((pressed & 0x40) != 0 ? 0 : 0x10) | ((pressed & 0x80) != 0 ? 0 : 0x20);
        return (byte)((v & ~ctrl) | (data & ctrl & 0x7F));
    }

    private static bool IsPsgPort(uint a) => (a & 0x1F) is 0x11 or 0x13 or 0x15 or 0x17;

    // ---------- reads ----------

    public byte Read8(uint address)
    {
        uint a = address & 0xFFFFFF;
        if (a < 0x400000) return a < rom.Length ? rom[a] : (byte)0;
        if (a >= 0xE00000) return Ram[a & 0xFFFF];
        if (a >= 0xC00000 && a < 0xE00000) return (byte)(Vdp.Read(a, 1) >> ((a & 1) != 0 ? 0 : 8));
        if (a >= 0xA00000 && a < 0xA10000) return Z80AreaRead(a);
        if (a >= 0xA10000 && a < 0xA10020)
        {
            uint r = a & 0x1F;
            if (r == 0x03) return ReadPad(0);
            if (r == 0x05) return ReadPad(1);
            return io[r];
        }
        if (a == 0xA11100) return (byte)(z80BusRequested ? 0x00 : 0x01);   // bit0 = 0 once the bus is granted
        return 0;
    }

    public ushort Read16(uint address)
    {
        uint a = address & 0xFFFFFF;
        if (a >= 0xC00000 && a < 0xE00000) return Vdp.Read(a, 2);
        if (a >= 0xA00000 && a < 0xA10000) { byte v = Z80AreaRead(a); return (ushort)((v << 8) | v); }   // the Z80 bus is 8 bits wide
        return (ushort)((Read8(a) << 8) | Read8(a + 1));
    }

    public uint Read32(uint address)
    {
        uint a = address & 0xFFFFFF;
        return ((uint)Read16(a) << 16) | Read16(a + 2);
    }

    // ---------- writes ----------

    public void Write8(uint address, byte value)
    {
        uint a = address & 0xFFFFFF;
        if (a >= 0xE00000) { Ram[a & 0xFFFF] = value; return; }
        if (a >= 0xC00000 && a < 0xE00000)
        {
            if (IsPsgPort(a)) { Sound.SyncTo(Now68k); Sound.Psg.Write(value); return; }
            Vdp.Write(a, value, 1);
            return;
        }
        if (a >= 0xA00000 && a < 0xA10000) { Z80AreaWrite(a, value); return; }
        if (a >= 0xA10000 && a < 0xA10020) { io[a & 0x1F] = value; Log($"I/O write8 {a:X6} = {value:X2}"); return; }
        if (a == 0xA11100) { z80BusRequested = (value & 1) != 0; Log($"Z80 busreq = {(z80BusRequested ? "request" : "release")}"); return; }
        if (a == 0xA11200) { SetZ80Reset((value & 1) == 0); return; }
        if (a >= 0xA14000 && a < 0xA14004) { Log($"TMSS write8 {a:X6} = {value:X2}"); return; }
    }

    public void Write16(uint address, ushort value)
    {
        uint a = address & 0xFFFFFF;
        if (a >= 0xC00000 && a < 0xE00000)
        {
            if ((a & 0x1F) is >= 0x10 and <= 0x17) { Sound.SyncTo(Now68k); Sound.Psg.Write((byte)value); return; }
            Vdp.Write(a, value, 2);
            return;
        }
        if (a >= 0xA00000 && a < 0xA10000) { Z80AreaWrite(a & ~1u, (byte)(value >> 8)); return; }   // only the high byte reaches the 8-bit Z80 bus
        if (a == 0xA11100) { z80BusRequested = (value & 0x0100) != 0; Log($"Z80 busreq = {(z80BusRequested ? "request" : "release")}"); return; }
        if (a == 0xA11200) { SetZ80Reset((value & 0x0100) == 0); return; }
        Write8(a, (byte)(value >> 8));
        Write8(a + 1, (byte)value);
    }

    public void Write32(uint address, uint value)
    {
        uint a = address & 0xFFFFFF;
        Write16(a, (ushort)(value >> 16));
        Write16(a + 2, (ushort)value);
    }
}
