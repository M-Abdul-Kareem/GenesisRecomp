using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace GenesisRecomp.Setup;

/// <summary>
/// Per-user installer built into the program itself (no administrator rights, nothing outside your account):
///   - copies the program to  %LocalAppData%\Programs\GenesisRecomp
///   - makes .bin / .md / .gen / .smd files open with it (double-click a game to play)
///   - adds a Start Menu shortcut and an entry under Settings > Installed apps (with Uninstall)
/// GenesisRecomp.exe --install [--dry-run]     GenesisRecomp.exe --uninstall [--dry-run]
/// </summary>
public static class Installer
{
    public const string ProgId = "GenesisRecomp.Rom";
    public static readonly string[] Extensions = { ".bin", ".md", ".gen", ".smd" };

    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\GenesisRecomp";
    private const string BackupKey = @"Software\GenesisRecomp\PreviousFileTypes";

    public static string InstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "GenesisRecomp");
    public static string InstalledExe => Path.Combine(InstallDir, "GenesisRecomp.exe");
    private static string ShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "GenesisRecomp.lnk");

    /// <summary>True when the running program is the installed copy.</summary>
    public static bool RunningFromInstallDir
    {
        get
        {
            string? exe = Environment.ProcessPath;
            if (exe == null) return false;
            string here = Path.GetFullPath(Path.GetDirectoryName(exe)!).TrimEnd('\\', '/');
            return string.Equals(here, Path.GetFullPath(InstallDir).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---------------------------------------------------------------- what gets written (also used by --dry-run)

    public sealed record RegEntry(string Key, string? Name, object Value, RegistryValueKind Kind = RegistryValueKind.String);

    public static List<RegEntry> BuildRegistryPlan(string exe)
    {
        var plan = new List<RegEntry>
        {
            new($@"Software\Classes\{ProgId}", null, "Mega Drive ROM"),
            new($@"Software\Classes\{ProgId}\DefaultIcon", null, $"\"{exe}\",0"),
            new($@"Software\Classes\{ProgId}\shell\open\command", null, $"\"{exe}\" \"%1\""),
        };
        foreach (string ext in Extensions)
            plan.Add(new($@"Software\Classes\{ext}\OpenWithProgids", ProgId, Array.Empty<byte>(), RegistryValueKind.None));
        plan.Add(new(UninstallKey, "DisplayName", "GenesisRecomp"));
        plan.Add(new(UninstallKey, "Publisher", "GenesisRecomp"));
        plan.Add(new(UninstallKey, "InstallLocation", Path.GetDirectoryName(exe)!));
        plan.Add(new(UninstallKey, "DisplayIcon", exe));
        plan.Add(new(UninstallKey, "UninstallString", $"\"{exe}\" --uninstall"));
        plan.Add(new(UninstallKey, "NoModify", 1, RegistryValueKind.DWord));
        plan.Add(new(UninstallKey, "NoRepair", 1, RegistryValueKind.DWord));
        return plan;
    }

    // ---------------------------------------------------------------- install

    public static int Install(bool dryRun)
    {
        string? self = Environment.ProcessPath;
        if (self == null) throw new InvalidOperationException("Cannot find the running program.");
        string exe = InstalledExe;

        Console.WriteLine($"Install folder : {InstallDir}");
        Console.WriteLine($"Copy           : {self}  ->  {exe}");
        foreach (var e in BuildRegistryPlan(exe))
            Console.WriteLine($"Registry (HKCU): {e.Key}  {(e.Name == null ? "(default)" : e.Name)} = {(e.Value is byte[] ? "(empty)" : e.Value)}");
        foreach (string ext in Extensions)
            Console.WriteLine($"File type      : {ext} opens with GenesisRecomp (previous default is remembered and restored on uninstall)");
        Console.WriteLine($"Shortcut       : {ShortcutPath}");
        if (dryRun) { Console.WriteLine("(dry run: nothing was changed)"); return 0; }

        if (!OperatingSystem.IsWindows()) { Console.WriteLine("Installing is only supported on Windows."); return 1; }

        // 1. copy the program (every file next to the exe, so it also works for a multi-file build)
        Directory.CreateDirectory(InstallDir);
        if (!RunningFromInstallDir)
        {
            string srcDir = Path.GetDirectoryName(self)!;
            foreach (string f in Directory.GetFiles(srcDir))
            {
                string name = Path.GetFileName(f);
                if (name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)) continue;
                string dest = Path.Combine(InstallDir, string.Equals(f, self, StringComparison.OrdinalIgnoreCase) ? "GenesisRecomp.exe" : name);
                File.Copy(f, dest, overwrite: true);
            }
        }

        // 2. registry (current user only)
        WriteRegistry(exe);

        // 3. Start Menu shortcut (best effort)
        TryCreateShortcut(ShortcutPath, exe);

        NotifyShell();
        return 0;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void WriteRegistry(string exe)
    {
        foreach (var e in BuildRegistryPlan(exe))
        {
            using RegistryKey k = Registry.CurrentUser.CreateSubKey(e.Key, writable: true)!;
            if (e.Name == null) k.SetValue(null, e.Value, e.Kind); else k.SetValue(e.Name, e.Value, e.Kind);
        }
        // Make these extensions open with us by default. If another program already owns one, remember it so uninstall can put it back.
        foreach (string ext in Extensions)
        {
            using RegistryKey k = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ext}", writable: true)!;
            string? current = k.GetValue(null) as string;
            if (!string.IsNullOrEmpty(current) && current != ProgId)
            {
                using RegistryKey b = Registry.CurrentUser.CreateSubKey(BackupKey, writable: true)!;
                b.SetValue(ext, current);
            }
            k.SetValue(null, ProgId);
        }
    }

    private static void TryCreateShortcut(string path, string target)
    {
        try
        {
            Type? t = Type.GetTypeFromProgID("WScript.Shell");
            if (t == null) return;
            dynamic shell = Activator.CreateInstance(t)!;
            dynamic link = shell.CreateShortcut(path);
            link.TargetPath = target;
            link.WorkingDirectory = Path.GetDirectoryName(target);
            link.Description = "GenesisRecomp - Mega Drive games";
            link.Save();
        }
        catch { /* the shortcut is a convenience only */ }
    }

    // ---------------------------------------------------------------- uninstall

    public static int Uninstall(bool dryRun)
    {
        Console.WriteLine($"Remove folder  : {InstallDir}");
        Console.WriteLine($"Remove registry: HKCU\\Software\\Classes\\{ProgId}, the 'open with' entries for {string.Join(" ", Extensions)}, the Installed-apps entry");
        Console.WriteLine($"Remove shortcut: {ShortcutPath}");
        if (dryRun) { Console.WriteLine("(dry run: nothing was changed)"); return 0; }
        if (!OperatingSystem.IsWindows()) { Console.WriteLine("Uninstalling is only supported on Windows."); return 1; }

        RemoveRegistry();
        try { File.Delete(ShortcutPath); } catch { }
        NotifyShell();

        // The running exe can't delete itself, so hand the folder to a tiny helper that waits a moment first.
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"{InstallDir}\"")
            { CreateNoWindow = true, UseShellExecute = false, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden };
            System.Diagnostics.Process.Start(psi);
        }
        catch { }
        return 0;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RemoveRegistry()
    {
        using (RegistryKey? b = Registry.CurrentUser.OpenSubKey(BackupKey))
        {
            foreach (string ext in Extensions)
            {
                using RegistryKey? k = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ext}", writable: true);
                if (k == null) continue;
                if (k.GetValue(null) as string == ProgId)
                {
                    string? previous = b?.GetValue(ext) as string;
                    if (previous != null) k.SetValue(null, previous); else k.DeleteValue("", throwOnMissingValue: false);
                }
                using RegistryKey? ow = k.OpenSubKey("OpenWithProgids", writable: true);
                ow?.DeleteValue(ProgId, throwOnMissingValue: false);
            }
        }
        Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ProgId}", throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\GenesisRecomp", throwOnMissingSubKey: false);
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    private static void NotifyShell()
    {
        try { SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero); } catch { }
    }
}
