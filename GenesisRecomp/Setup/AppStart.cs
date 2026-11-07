#if WINDOWS
using GenesisRecomp.Ui;

namespace GenesisRecomp.Setup;

/// <summary>What happens when the program is started with no arguments (someone double-clicked GenesisRecomp.exe).</summary>
public static class AppStart
{
    public static int Run(Func<string, int> playRom)
    {
        if (!Installer.RunningFromInstallDir)
        {
            bool yes = StartupDialogs.Ask("GenesisRecomp",
                "Install GenesisRecomp for your Windows account?\n\n" +
                "It will be copied to:\n    " + Installer.InstallDir + "\n\n" +
                "and game files (.bin, .md, .gen, .smd) will open with it, so you can just double-click a game to play.\n" +
                "No administrator rights are needed, and you can remove it any time under Settings > Apps > Installed apps.");
            if (yes)
            {
                try
                {
                    Installer.Install(dryRun: false);
                    StartupDialogs.Info("GenesisRecomp",
                        "Installed.\n\nFrom now on, double-click a Mega Drive game file to play it.\n" +
                        "If Windows asks which app to use for the first time, choose GenesisRecomp and \"Always\".\n\n" +
                        "Next, pick a game to play now.");
                }
                catch (Exception ex)
                {
                    StartupDialogs.Error("GenesisRecomp", "Installing failed:\n" + ex.Message);
                }
            }
        }

        string? rom = StartupDialogs.PickRom();
        return rom == null ? 0 : playRom(rom);
    }
}
#endif
