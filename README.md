# GenesisRecomp

A Sega Mega Drive / Genesis game player for Windows, written in C#.

When you open a game, its 68000 machine code is translated into native code on your PC (in memory, in a fraction of a second,
no compiler and no extra download) instead of being interpreted instruction by instruction. The console's video chip, sound chips
and Z80 are still emulated, as in every recompilation of a console with its own custom hardware, so this is a hybrid, not a pure
port. Code that cannot be translated (for example code a game copies into RAM, or the few rare instructions) runs in an interpreter.
It was developed and checked with *Streets of Rage*; other games are untested.

## Download and play

1. Open **Releases** on this page and download **GenesisRecomp.exe**.
2. Double-click it. It offers to install itself for your Windows account (no administrator rights, nothing to download
   separately: the .NET runtime is inside the file).
3. Double-click a game file (`.bin`, `.md`, `.gen`, `.smd`) to play it, or drag a game onto the exe.
   If Windows asks which app to use the first time, choose GenesisRecomp and "Always".

Windows SmartScreen may say "Windows protected your PC" because the exe is not code-signed. Click **More info > Run anyway**.
To remove it: Settings > Apps > Installed apps > GenesisRecomp > Uninstall.

**Game files are not included.** Use your own legally obtained dumps.

## Controls

| Key | Action |
|---|---|
| Arrow keys | D-pad |
| A / Z, S / X, D / C | A, B, C buttons |
| Enter | Start |
| P | Pause |
| Tab (hold) | Fast-forward (sound muted) |
| F1 | Remap keys / mouse buttons |
| F2 | Screenshot (Desktop\GenesisRecomp_screenshots) |
| F3 | Square pixels / 4:3 TV shape |
| Esc | Quit |

Sound plays through the Windows default audio device, so the Windows volume controls it.

## For developers

Build: .NET 8 SDK, `dotnet build GenesisRecomp/GenesisRecomp.csproj`.

Self-checks (each prints 0 mismatches when healthy):

```
GenesisRecomp --verify-vdp-fuzz 400          fast video renderer vs. the reference renderer
GenesisRecomp --verify-decoder <listing>     instruction decoder
GenesisRecomp --verify-live "game.bin" 8     every instruction in a game: interpreter vs. the in-memory recompiler
GenesisRecomp --recompile "game.bin" --max-seg 1   then rebuild, then:
GenesisRecomp --verify-recomp "game.bin" 8   same check for code generated into the program (advanced, see below)
```

Optional, advanced: `GenesisRecomp/build_port.cmd "game.bin"` (needs the .NET SDK) can also generate C# for a game and build it into
the program (`Generated/Rom_<crc>.cs`). That code is derived from the game, so it is ignored by git on purpose: **do not commit or share it.**
The exe built by GitHub contains no game code; it recompiles each game on the player's own PC when the game is opened.
Add `--interp` to a command line to use the plain interpreter, or `--no-live` to turn off the in-memory recompiler (to compare).

`GenesisRecomp/make_exe.cmd` builds the same single-file exe locally.
