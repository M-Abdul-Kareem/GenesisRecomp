using GenesisRecomp;
using GenesisRecomp.Cpu;

if (args.Contains("--interp"))               // force the plain interpreter even if recompiled code is present
{
    GenesisRecomp.Cpu.Cpu68k.AllowRecompiled = false;
    args = args.Where(a => a != "--interp").ToArray();
}

if (args.Contains("--no-live"))               // don't recompile games in memory; ROMs not built into the program use the interpreter
{
    GenesisRecomp.Cpu.Cpu68k.AllowLive = false;
    args = args.Where(a => a != "--no-live").ToArray();
}

if (args.Contains("--verify-vdp"))            // draw every line with both renderers and report any difference
{
    GenesisRecomp.Machine.Vdp.VerifyRenderer = true;
    args = args.Where(a => a != "--verify-vdp").ToArray();
}

if (args.Length >= 1 && args[0] == "--verify-vdp-fuzz")
    return GenesisRecomp.Tools.VdpFuzz.Run(args);

if (args.Length >= 2 && args[0] == "--recompile")
    return GenesisRecomp.Recomp.RecompCommand.Run(args);
if (args.Length >= 2 && args[0] is "--verify-recomp" or "--verify-live")
    return GenesisRecomp.Tools.RecompVerify.Run(args);

if (args.Length >= 2 && args[0] == "--boot")
{
    long limit = args.Length >= 4 && args[2] == "--steps" ? long.Parse(args[3]) : 2_000_000;
    return GenesisRecomp.Tools.BootRunner.Run(File.ReadAllBytes(args[1]), limit);
}
if (args.Length >= 1 && args[0] == "--install") return GenesisRecomp.Setup.Installer.Install(args.Contains("--dry-run"));
if (args.Length >= 1 && args[0] == "--uninstall") return GenesisRecomp.Setup.Installer.Uninstall(args.Contains("--dry-run"));

#if WINDOWS
// Drag a ROM file onto the .exe (or give just its path) and it plays.
if (args.Length == 1 && !args[0].StartsWith("--") && File.Exists(args[0]))
    return PlayRom(args[0]);
if (args.Length >= 2 && args[0] == "--play")
    return PlayRom(args[1]);

static int PlayRom(string path)
{
    try { return GenesisRecomp.Ui.Launcher.Play(File.ReadAllBytes(path)); }
    catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
    {
        GenesisRecomp.Ui.StartupDialogs.Error("GenesisRecomp", $"{Path.GetFileName(path)} could not be played:\n{ex.Message}");
        return 1;
    }
}

if (args.Length == 0) return GenesisRecomp.Setup.AppStart.Run(PlayRom);   // double-clicked the .exe: offer to install, then pick a game

if (args.Length >= 1 && args[0] == "--analyze") args = args.Skip(1).ToArray();   // the code-discovery report (a bare path now means "play")
#endif
if (args.Length >= 2 && args[0] == "--run")
    return GenesisRecomp.Tools.FrameRunner.Run(args);
if (args.Length >= 2 && args[0] == "--run-rom")
    return GenesisRecomp.Tools.SoundRun.Run(args[1], args.Length >= 3 ? int.Parse(args[2]) : 300, args.Length >= 4 ? args[3] : null);
if (args.Length >= 2 && args[0] == "--audio-test")
    return GenesisRecomp.Tools.AudioTest.Run(args[1]);
if (args.Length >= 2 && args[0] == "--verify-z80")
    return GenesisRecomp.Tools.Z80Verify.Run(args[1]);
if (args.Length >= 1 && args[0] == "--verify-decoder")
    return GenesisRecomp.Tools.DecoderVerify.Run(args.Length >= 2 ? args[1] : null);
if (args.Length >= 2 && args[0] == "--verify")
    return GenesisRecomp.Tools.CpuVerify.Run(args[1]);

if (args.Length < 1)
{
    Console.WriteLine("Usage: GenesisRecomp <path-to-rom.bin> [--out listing.txt] [--show N]");
    return 1;
}

string path = args[0];
if (!File.Exists(path))
{
    Console.WriteLine($"File not found: {path}");
    return 1;
}

string outPath = "listing.txt";
int show = 30;
for (int i = 1; i < args.Length - 1; i++)
{
    if (args[i] == "--out") outPath = args[i + 1];
    if (args[i] == "--show") show = int.Parse(args[i + 1]);
}

byte[] rom = File.ReadAllBytes(path);

RomHeader header;
try { header = RomHeader.Parse(rom); }
catch (InvalidDataException ex)
{
    Console.WriteLine($"Error: {ex.Message}");
    return 1;
}

Console.WriteLine($"{header.OverseasName}  [{header.SerialNumber}]  regions {header.Regions}  " +
                  $"checksum {(header.ChecksumValid ? "OK" : "MISMATCH")}");
Console.WriteLine();

// ---- 1. Exception vector table ----
uint[] vectors = Vectors.Read(rom);
var names = new Dictionary<uint, string>();
uint defaultHandler = vectors.Skip(2).GroupBy(v => v).OrderByDescending(g => g.Count()).First().Key;

Console.WriteLine("Vector table (entries that differ from the common default handler):");
for (int i = 1; i < 64; i++)
{
    uint v = vectors[i];
    if (i >= 2 && v == defaultHandler) continue;
    Console.WriteLine($"  {i,2}  {Vectors.Name(i),-30} 0x{v:X8}");
    string label = i switch { 1 => "Reset", 28 => "HBlank", 30 => "VBlank", _ => $"Vec{i}" };
    names.TryAdd(v, label);
}
Console.WriteLine($"  (all other vectors point to 0x{defaultHandler:X8})");
names.TryAdd(defaultHandler, "DefaultHandler");
Console.WriteLine();

// ---- 2. Find all reachable code, resolving jump tables until nothing new turns up ----
var baseRoots = new HashSet<uint> { vectors[1] };
foreach (uint v in vectors.Skip(2).Distinct())
    if (v >= 0x100 && v < rom.Length && (v & 1) == 0) baseRoots.Add(v);

static IEnumerable<uint> TableRoots(List<JumpTable> tables) =>
    tables.SelectMany(t => t.Kind == TableKind.BranchTable ? t.EntryAddresses : t.Targets);

var roots = new HashSet<uint>(baseRoots);
CodeMap map;
List<JumpTable> tables;
List<(uint Site, string Reason)> unresolved;
Console.WriteLine("Code discovery:");
for (int pass = 1; ; pass++)
{
    map = CodeMap.Build(rom, roots);
    JumpTables.Resolve(rom, map, out tables, out unresolved);

    int added = 0;
    foreach (uint r in TableRoots(tables)) if (roots.Add(r)) added++;

    Console.WriteLine($"  pass {pass}: {map.Instructions.Count,6:N0} instructions, " +
                      $"{100.0 * map.BytesCovered / rom.Length,5:F1}% of ROM, " +
                      $"{tables.Count} tables resolved, +{added} new entry points");
    if (added == 0 || pass >= 25) break;
}

Console.WriteLine();

foreach (JumpTable t in tables)
    if (t.Kind == TableKind.PointerTable)
        for (int i = 0; i < t.Targets.Count; i++) names.TryAdd(t.Targets[i], $"State{i}");

Console.WriteLine($"Code discovered : {map.Instructions.Count:N0} instructions, " +
                  $"{map.BytesCovered:N0} of {rom.Length:N0} bytes ({100.0 * map.BytesCovered / rom.Length:F1}%)");
Console.WriteLine($"Labels          : {map.Labels.Count:N0} (branch/call/jump targets)");
Console.WriteLine($"Jump tables     : {tables.Count} resolved, {unresolved.Count} still unresolved");
Console.WriteLine($"Undecodable     : {map.BadAddresses.Count:N0} reached addresses did not decode");
Console.WriteLine($"Outside ROM     : {map.ExternalTargets.Count:N0} targets in RAM/IO space or misaligned");
Console.WriteLine();

Console.WriteLine("Resolved tables:");
foreach (JumpTable t in tables.OrderBy(t => t.Site))
    Console.WriteLine($"  {t.Kind switch { TableKind.BranchTable => "branch ", TableKind.OffsetTable => "offset ", TableKind.AbsWordTable => "absword", _ => "pointer" }} table @0x{t.Base:X6}  " +
                      $"{t.EntryAddresses.Count,3} entries   used by 0x{t.Site:X6}");
if (unresolved.Count > 0)
{
    Console.WriteLine("Unresolved indirect jumps:");
    foreach (var (site, reason) in unresolved) Console.WriteLine($"  0x{site:X6}  {reason}");
}
Console.WriteLine();

// ---- 3. Show the start of the program ----
Console.WriteLine($"First {show} instructions from the reset vector:");
uint a = vectors[1];
for (int n = 0; n < show && map.Instructions.TryGetValue(a, out Instruction? ins); n++)
{
    Console.WriteLine("  " + Listing.Format(ins, rom));
    // follow straight-line flow only; stop at the first control transfer that leaves it
    if (ins.Flow is FlowKind.Jump or FlowKind.Return or FlowKind.Halt or FlowKind.IndirectJump) break;
    a = ins.NextAddress;
}

// ---- 4. Full listing to a file ----
using (var w = new StreamWriter(outPath))
    Listing.Write(w, map, rom, names);
Console.WriteLine();
Console.WriteLine($"Full listing written to: {Path.GetFullPath(outPath)}");

if (map.BadAddresses.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("Addresses that failed to decode (first 10):");
    foreach (uint bad in map.BadAddresses.Take(10))
        Console.WriteLine($"  0x{bad:X8}");
}

return 0;
