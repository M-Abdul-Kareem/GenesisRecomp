#if WINDOWS
using System.Windows.Forms;

namespace GenesisRecomp.Ui;

/// <summary>The few message boxes and the file picker used before a game starts (the release build has no console window).</summary>
public static class StartupDialogs
{
    private static T OnStaThread<T>(Func<T> work)
    {
        T result = default!;
        Exception? error = null;
        var t = new Thread(() => { try { result = work(); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA);               // Windows Forms dialogs need a single-threaded apartment
        t.Start();
        t.Join();
        if (error != null) throw error;
        return result;
    }

    public static bool Ask(string title, string text) =>
        OnStaThread(() => MessageBox.Show(text, title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes);

    public static void Info(string title, string text) =>
        OnStaThread(() => { MessageBox.Show(text, title, MessageBoxButtons.OK, MessageBoxIcon.Information); return 0; });

    public static void Error(string title, string text) =>
        OnStaThread(() => { MessageBox.Show(text, title, MessageBoxButtons.OK, MessageBoxIcon.Error); return 0; });

    /// <summary>Returns the chosen ROM path, or null if cancelled.</summary>
    public static string? PickRom() => OnStaThread(() =>
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Choose a Mega Drive game",
            Filter = "Mega Drive games (*.bin;*.md;*.gen;*.smd)|*.bin;*.md;*.gen;*.smd|All files (*.*)|*.*",
        };
        return dlg.ShowDialog() == DialogResult.OK ? dlg.FileName : null;
    });
}
#endif
