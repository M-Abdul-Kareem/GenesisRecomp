#if WINDOWS
namespace GenesisRecomp.Ui;

/// <summary>The Controls window: click a box, then press a key or click a mouse button. Esc cancels the pick, Delete/Backspace clears the box.</summary>
public sealed class KeyMapDialog : Form
{
    private readonly KeyMap map;
    private readonly Button[] slotButtons = new Button[8 * KeyMap.Slots];
    private readonly Label status = new();
    private int listening = -1;                                         // index of the box waiting for a key, or -1
    private readonly MouseCatcher catcher;
    private bool filterInstalled;
    private long listenStartedAt;                                       // Environment.TickCount64 when the current pick began

    /// <summary>The edited copy of the key map (valid when the dialog returns OK).</summary>
    public KeyMap Result => map;

    public KeyMapDialog(KeyMap map)
    {
        this.map = map;
        catcher = new MouseCatcher(this);
        Text = "Controls";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;

        const int rowHeight = 34, top = 44;
        ClientSize = new System.Drawing.Size(420, top + 8 * rowHeight + 100);

        AddLabel("Button", 16, 14, 100);
        AddLabel("Key 1", 150, 14, 100);
        AddLabel("Key 2", 282, 14, 100);

        int row = 0;
        foreach (PadButton b in KeyMap.DisplayOrder)
        {
            int y = top + row * rowHeight;
            AddLabel(b == PadButton.Start ? "Start" : b.ToString() + (b is PadButton.A or PadButton.B or PadButton.C ? " button" : ""), 16, y + 6, 120);
            for (int s = 0; s < KeyMap.Slots; s++)
            {
                int index = (int)b * KeyMap.Slots + s;
                var button = new Button { Left = 150 + s * 132, Top = y, Width = 120, Height = 28 };
                button.Click += (sender, e) => StartListening(index);
                slotButtons[index] = button;
                Controls.Add(button);
            }
            row++;
        }

        int below = top + 8 * rowHeight + 8;
        status.Left = 16; status.Top = below; status.Width = 390; status.Height = 36; status.AutoSize = false;
        status.Text = "Click a box, then press a key or click a mouse button.\nEsc cancels, Delete clears the box.";
        Controls.Add(status);

        AddButton("Defaults", 16, below + 46, 90, () => { Copy(KeyMap.CreateDefault()); Say("Back to the default keys (not saved until you press Save)."); });
        AddButton("Save", 236, below + 46, 80, () => { DialogResult = DialogResult.OK; });
        AddButton("Cancel", 326, below + 46, 80, () => { DialogResult = DialogResult.Cancel; });

        Refresh2();
    }

    private void AddLabel(string text, int x, int y, int width)
    {
        Controls.Add(new Label { Text = text, Left = x, Top = y, Width = width, AutoSize = false, Height = 20 });
    }

    private void AddButton(string text, int x, int y, int width, Action onClick)
    {
        var b = new Button { Text = text, Left = x, Top = y, Width = width, Height = 28 };
        b.Click += (sender, e) => onClick();
        Controls.Add(b);
    }

    private void Copy(KeyMap source)
    {
        foreach (PadButton b in KeyMap.DisplayOrder)
            for (int s = 0; s < KeyMap.Slots; s++)
                map.Set(b, s, Keys.None);
        foreach (PadButton b in KeyMap.DisplayOrder)
            for (int s = 0; s < KeyMap.Slots; s++)
                if (source.Get(b, s) != Keys.None) map.Set(b, s, source.Get(b, s));
        StopListening();
        Refresh2();
    }

    private void Say(string text) => status.Text = text;

    private void StartListening(int index)
    {
        listening = index;
        listenStartedAt = Environment.TickCount64;
        if (!filterInstalled) { Application.AddMessageFilter(catcher); filterInstalled = true; }
        Refresh2();
        Say("Press a key or click a mouse button for this box...\n(Esc = cancel, Delete = clear)");
    }

    private void StopListening()
    {
        listening = -1;
        if (filterInstalled) { Application.RemoveMessageFilter(catcher); filterInstalled = false; }
    }

    /// <summary>Called for every mouse-button press while a box is waiting. Returns true if the click was used up.</summary>
    private bool OnMouseButton(Keys key, IntPtr windowHandle)
    {
        if (listening < 0) return false;
        Control? c = Control.FromHandle(windowHandle);
        if (c == null || c.FindForm() != this) return false;           // a click somewhere else: leave it alone
        if (Environment.TickCount64 - listenStartedAt < 400) return true;   // the tail of the double-click that opened the pick: ignore it
        AssignKey(key);
        return true;
    }

    private void AssignKey(Keys key)
    {
        var button = (PadButton)(listening / KeyMap.Slots);
        int slot = listening % KeyMap.Slots;
        string text = map.Set(button, slot, key) ?? $"{KeyMap.KeyName(key)} assigned.";
        StopListening();
        Refresh2();
        Say(text);
    }

    /// <summary>Watches for mouse-button presses (anywhere in the window) while a box is waiting, so they can be assigned like keys.</summary>
    private sealed class MouseCatcher : IMessageFilter
    {
        private const int WmLButtonDown = 0x0201, WmLButtonDblClk = 0x0203, WmRButtonDown = 0x0204, WmRButtonDblClk = 0x0206,
                          WmMButtonDown = 0x0207, WmMButtonDblClk = 0x0209, WmXButtonDown = 0x020B, WmXButtonDblClk = 0x020D;
        private readonly KeyMapDialog owner;
        public MouseCatcher(KeyMapDialog owner) { this.owner = owner; }

        public bool PreFilterMessage(ref Message m)
        {
            Keys key;
            switch (m.Msg)
            {
                case WmLButtonDown: case WmLButtonDblClk: key = Keys.LButton; break;
                case WmRButtonDown: case WmRButtonDblClk: key = Keys.RButton; break;
                case WmMButtonDown: case WmMButtonDblClk: key = Keys.MButton; break;
                case WmXButtonDown: case WmXButtonDblClk:
                    key = (((long)m.WParam >> 16) & 0xFFFF) == 2 ? Keys.XButton2 : Keys.XButton1;
                    break;
                default: return false;
            }
            return owner.OnMouseButton(key, m.HWnd);
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        StopListening();
        base.OnFormClosed(e);
    }

    private void Refresh2()
    {
        foreach (PadButton b in KeyMap.DisplayOrder)
            for (int s = 0; s < KeyMap.Slots; s++)
            {
                int index = (int)b * KeyMap.Slots + s;
                slotButtons[index].Text = index == listening ? "Press a key..." : KeyMap.KeyName(map.Get(b, s));
            }
    }

    /// <summary>While a box is waiting, take every key (including arrows and Enter, which normally move the focus).</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (listening < 0) return base.ProcessCmdKey(ref msg, keyData);

        Keys key = keyData & Keys.KeyCode;
        var button = (PadButton)(listening / KeyMap.Slots);
        int slot = listening % KeyMap.Slots;
        if (key == Keys.Escape || key == Keys.Delete || key == Keys.Back)
        {
            string text = "Cancelled.";
            if (key != Keys.Escape) { map.Clear(button, slot); text = "Cleared."; }
            StopListening();
            Refresh2();
            Say(text);
            return true;
        }
        AssignKey(key);
        return true;
    }
}
#endif
