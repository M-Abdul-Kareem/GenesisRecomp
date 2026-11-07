#if WINDOWS
using System.Text;

namespace GenesisRecomp.Ui;

public enum PadButton { Up, Down, Left, Right, B, C, A, Start }

/// <summary>
/// Which keyboard keys or mouse buttons press which Mega Drive pad buttons. Each button has two slots.
/// Saved as a small text file so it survives restarts:  %AppData%\GenesisRecomp\keymap.txt
/// </summary>
public sealed class KeyMap
{
    public const int Slots = 2;

    /// <summary>Order the buttons are shown in the Controls window.</summary>
    public static readonly PadButton[] DisplayOrder =
        { PadButton.Up, PadButton.Down, PadButton.Left, PadButton.Right, PadButton.A, PadButton.B, PadButton.C, PadButton.Start };

    /// <summary>Keys the window itself uses; they cannot be assigned to a pad button.</summary>
    private static readonly Keys[] Reserved =
        { Keys.Escape, Keys.P, Keys.Tab, Keys.F1, Keys.F2, Keys.F3, Keys.Menu, Keys.LWin, Keys.RWin };

    private readonly Keys[] keys = new Keys[8 * Slots];      // index = button * Slots + slot

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GenesisRecomp", "keymap.txt");

    /// <summary>Bit for the button in GenesisBus.Pad1.</summary>
    public static int MaskOf(PadButton b) => b switch
    {
        PadButton.Up => 1, PadButton.Down => 2, PadButton.Left => 4, PadButton.Right => 8,
        PadButton.B => 16, PadButton.C => 32, PadButton.A => 64, _ => 128
    };

    public static bool IsReserved(Keys k) => Array.IndexOf(Reserved, k) >= 0;

    public static KeyMap CreateDefault()
    {
        var m = new KeyMap();
        m.Put(PadButton.Up, Keys.Up, Keys.None);
        m.Put(PadButton.Down, Keys.Down, Keys.None);
        m.Put(PadButton.Left, Keys.Left, Keys.None);
        m.Put(PadButton.Right, Keys.Right, Keys.None);
        m.Put(PadButton.A, Keys.A, Keys.Z);
        m.Put(PadButton.B, Keys.S, Keys.X);
        m.Put(PadButton.C, Keys.D, Keys.C);
        m.Put(PadButton.Start, Keys.Enter, Keys.None);
        return m;
    }

    private void Put(PadButton b, Keys first, Keys second)
    {
        keys[(int)b * Slots] = first;
        keys[(int)b * Slots + 1] = second;
    }

    public KeyMap Clone()
    {
        var c = new KeyMap();
        Array.Copy(keys, c.keys, keys.Length);
        return c;
    }

    public Keys Get(PadButton b, int slot) => keys[(int)b * Slots + slot];

    /// <summary>The pad-button bits pressed by this key (0 if the key is unassigned).</summary>
    public int MaskFor(Keys key)
    {
        if (key == Keys.None) return 0;
        int mask = 0;
        for (int i = 0; i < keys.Length; i++)
            if (keys[i] == key) mask |= MaskOf((PadButton)(i / Slots));
        return mask;
    }

    /// <summary>
    /// Assigns a key to a slot. A key can only drive one slot, so it is taken away from wherever it was before.
    /// Returns a message to show the user (refusal or "moved from ..."), or null if there is nothing to say.
    /// </summary>
    public string? Set(PadButton b, int slot, Keys key)
    {
        int index = (int)b * Slots + slot;
        if (key == Keys.None) { keys[index] = Keys.None; return null; }
        if (IsReserved(key))
            return $"{KeyName(key)} is used by the window (pause, fast-forward, menus) and can't be assigned.";
        string? note = null;
        for (int i = 0; i < keys.Length; i++)
        {
            if (i == index || keys[i] != key) continue;
            keys[i] = Keys.None;
            note = $"{KeyName(key)} was moved here from {(PadButton)(i / Slots)}.";
        }
        keys[index] = key;
        return note;
    }

    public void Clear(PadButton b, int slot) => keys[(int)b * Slots + slot] = Keys.None;

    /// <summary>Drops reserved keys and duplicates (the first use of a key wins).</summary>
    private void Normalize()
    {
        var seen = new HashSet<Keys>();
        for (int i = 0; i < keys.Length; i++)
        {
            if (keys[i] == Keys.None) continue;
            if (IsReserved(keys[i]) || !seen.Add(keys[i])) keys[i] = Keys.None;
        }
    }

    // ---------- saving and loading ----------

    public static KeyMap Load(string path)
    {
        var map = CreateDefault();
        try
        {
            if (!File.Exists(path)) return map;
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string buttonName = line[..eq].Trim();
                int found = Array.FindIndex(DisplayOrder, b => b.ToString().Equals(buttonName, StringComparison.OrdinalIgnoreCase));
                if (found < 0) continue;                                  // unknown button name (numbers included): skip just this line
                PadButton button = DisplayOrder[found];
                string[] parts = line[(eq + 1)..].Split(',');
                for (int s = 0; s < Slots; s++)
                    map.keys[(int)button * Slots + s] = s < parts.Length ? ParseKey(parts[s].Trim()) : Keys.None;
            }
            map.Normalize();
            return map;
        }
        catch (Exception) { return CreateDefault(); }
    }

    private static Keys ParseKey(string text)
    {
        if (text.Length == 0 || text.Equals("None", StringComparison.OrdinalIgnoreCase)) return Keys.None;
        if (char.IsDigit(text[0])) return Keys.None;                      // key names never start with a digit ("D5" is the 5 key); numbers are junk
        if (!Enum.TryParse(text, true, out Keys k)) return Keys.None;
        return Enum.IsDefined(typeof(Keys), k) && (k & ~Keys.KeyCode) == 0 ? k : Keys.None;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var sb = new StringBuilder("# GenesisRecomp key map. Format: Button=Key1,Key2   (None = unassigned)\r\n");
        foreach (PadButton b in DisplayOrder)
            sb.Append(b).Append('=').Append(Get(b, 0)).Append(',').Append(Get(b, 1)).Append("\r\n");
        File.WriteAllText(path, sb.ToString());
    }

    /// <summary>Friendly name for showing a key to the user.</summary>
    public static string KeyName(Keys k)
    {
        if (k == Keys.None) return "(none)";
        switch (k)
        {
            case Keys.LButton: return "Left click";
            case Keys.RButton: return "Right click";
            case Keys.MButton: return "Middle click";
            case Keys.XButton1: return "Mouse button 4";
            case Keys.XButton2: return "Mouse button 5";
        }
        if (k == Keys.Enter) return "Enter";
        if (k == Keys.Back) return "Backspace";
        string s = k.ToString();
        if (s.Length == 2 && s[0] == 'D' && char.IsDigit(s[1])) return s[1].ToString();
        return s;
    }
}
#endif
