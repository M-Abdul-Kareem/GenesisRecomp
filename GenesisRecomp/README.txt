GenesisRecomp - install and play
================================
1. Copy EVERYTHING inside this 'GenesisRecomp' folder into your project folder:
       C:\Users\muthe\source\repos\GenesisRecomp\GenesisRecomp\
   and choose "Replace the files in the destination".
   NOTE: this zip includes GenesisRecomp.csproj. The only changes from the original are
         TargetFramework  net8.0  ->  net8.0-windows
         <UseWindowsForms>true</UseWindowsForms>   (new line)
   They are needed for the game window.
2. In Visual Studio (reload the project if it asks): Build > Rebuild Solution (Ctrl+Shift+B).
3. Choose the profile "GenesisRecompPlay" next to the green Start button and press F5.
   The little console window stays open behind the game; it only shows messages.

CONTROLS  (press F1 in the game to change them)
   Arrow keys ......... D-pad   (mouse buttons can be assigned too - see below)
   A or Z ............. A button   (Streets of Rage: special / call police)
   S or X ............. B button   (attack)
   D or C ............. C button   (jump)
   Enter .............. Start
   P .................. pause
   Tab (hold) ......... fast-forward
   F1 ................. Controls window (remap keys)
   F2 ................. screenshot -> Desktop\GenesisRecomp_screenshots
   F3 ................. switch between square pixels and 4:3 TV shape
   Esc ................ quit

CHANGING THE KEYS
   Press F1. The game pauses and the Controls window opens. Click a box, then press the key you want
   OR click a mouse button (left, right, middle, or the side buttons).
   Every button has two boxes, so you can have two keys/buttons per button.
   While a box says "Press a key...", the next mouse click anywhere in the Controls window is used for
   that box (so don't click Save yet - press Esc if you change your mind).
   Esc cancels the pick, Delete clears the box, "Defaults" restores the original keys.
   A key can drive only one button: if you pick a key that is already used, it moves.
   Esc, P, Tab, F1, F2, F3, Alt and the Windows keys belong to the window and can't be assigned.
   Mouse buttons are saved in the same file (LButton, RButton, MButton, XButton1, XButton2).
   Clicks only count while the pointer is inside the game picture; the window's title bar is not affected.
   "Save" stores your keys here and they are loaded every time:
       %AppData%\GenesisRecomp\keymap.txt     (plain text, you can edit it by hand; delete it to get the defaults back)

OTHER PROFILES
   GenesisRecompRun       runs 3600 frames without a window and saves screenshots to Desktop\frames
   GenesisRecompBoot      runs the CPU from reset for 3,000,000 instructions
   GenesisRecompAnalysis  code analysis of the ROM
   GenesisRecompAudioTest plays test tones through the sound chips and saves Desktop\audio_test.wav (no game needed)
   GenesisRecompVerifyDecoder  checks the instruction decoder; last line should say ALL DECODER CHECKS PASSED
   (The program's output folder is now bin\Debug\net8.0-windows\ instead of bin\Debug\net8.0\.)

SOUND
   The Mega Drive's sound hardware is now emulated: the Z80 sound CPU, the YM2612 FM chip (including the
   drum/voice sample channel) and the PSG. Sound plays through your default Windows playback device.
   The game is paced by the sound card, so speed stays steady. Tab (fast-forward) mutes the sound.
   If no sound device is found the game simply runs silently.
   Known gaps: the YM2612's SSG-EG envelope mode and CSM mode are not emulated (a few instruments in a few
   games will sound slightly different), and there is no volume control yet - use the Windows volume mixer.

COMMAND-LINE TOOLS (optional, for testing)
   GenesisRecomp --run-rom game.bin [frames] [out.wav]   run a ROM without a window and record its sound
   GenesisRecomp --audio-test out.wav                    sound self-test, no ROM needed
   GenesisRecomp --verify-z80 zexdoc.cim                 run the Z80 instruction exerciser
   GenesisRecomp --verify-decoder listing_reference.txt  check the instruction decoder
