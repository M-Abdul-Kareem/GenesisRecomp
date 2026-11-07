#if WINDOWS
using System.Runtime.InteropServices;
using GenesisRecomp.Machine;

namespace GenesisRecomp.Ui;

public static class Launcher
{
    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint milliseconds);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint milliseconds);

    /// <summary>Opens the game window and blocks until it is closed.</summary>
    public static int Play(byte[] rom)
    {
        string name = RomHeader.Parse(rom).OverseasName;
        timeBeginPeriod(1);                                               // 1 ms timer resolution for steady frame pacing
        try
        {
            var ui = new Thread(() =>
            {
                Application.SetHighDpiMode(HighDpiMode.SystemAware);
                Application.EnableVisualStyles();
                Application.Run(new GameWindow(new GenesisMachine(rom), name));
            });
            ui.SetApartmentState(ApartmentState.STA);                    // Windows Forms needs a single-threaded apartment
            ui.Start();
            ui.Join();
        }
        finally { timeEndPeriod(1); }
        return 0;
    }
}
#endif
