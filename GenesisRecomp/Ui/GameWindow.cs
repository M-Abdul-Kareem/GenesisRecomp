#if WINDOWS
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using GenesisRecomp.Machine;
using GenesisRecomp.Tools;

namespace GenesisRecomp.Ui;

/// <summary>
/// A window that runs the machine in real time and shows the picture.
/// Default keys (change them with F1, mouse buttons can be assigned too):  arrows = D-pad, A/Z = A button, S/X = B button, D/C = C button, Enter = Start.
///   P = pause    Tab (hold) = fast forward    F1 = controls    F2 = screenshot    F3 = 4:3 / square pixels    Esc = quit
/// </summary>
public sealed class GameWindow : Form
{
    private const double FrameMs = 1000.0 / 59.92;                        // NTSC Mega Drive refresh rate

    private readonly GenesisMachine machine;
    private readonly string gameName;
    private readonly Bitmap bitmap = new(320, 240, PixelFormat.Format32bppRgb);
    private readonly int[] shared = new int[320 * 240];                  // latest finished frame (ARGB, 320 per row)
    private readonly object frameLock = new();
    private int srcWidth = 320, srcHeight = 224;
    private int pad;                                                      // pressed buttons, same bit layout as GenesisBus.Pad1
    private volatile bool running = true, paused, turbo;
    private bool tvAspect;
    private KeyMap keyMap = KeyMap.Load(KeyMap.DefaultPath);
    private double fps;
    private Thread? worker;
    private AudioOut? audio;

    public GameWindow(GenesisMachine machine, string gameName)
    {
        this.machine = machine;
        this.gameName = gameName;

        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Black;
        StartPosition = FormStartPosition.CenterScreen;

        // pick the largest whole-number zoom that fits the screen
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        int zoom = Math.Max(1, Math.Min(4, Math.Min((area.Width - 40) / 320, (area.Height - 80) / 224)));
        ClientSize = new System.Drawing.Size(320 * zoom, 224 * zoom);
        MinimumSize = new System.Drawing.Size(340, 280);
        UpdateTitle();
    }

    // ---------- keyboard ----------

    protected override void OnKeyDown(KeyEventArgs e)
    {
        int mask = keyMap.MaskFor(e.KeyCode);
        if (mask != 0) { Interlocked.Or(ref pad, mask); e.Handled = true; e.SuppressKeyPress = true; return; }
        switch (e.KeyCode)
        {
            case Keys.Escape: Close(); break;
            case Keys.P: paused = !paused; if (paused) audio?.Flush(); UpdateTitle(); break;
            case Keys.Tab: turbo = true; e.Handled = true; e.SuppressKeyPress = true; break;
            case Keys.F1: ShowControls(); break;
            case Keys.F2: SaveScreenshot(); break;
            case Keys.F3: tvAspect = !tvAspect; Invalidate(); break;
        }
        base.OnKeyDown(e);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        int mask = keyMap.MaskFor(e.KeyCode);
        if (mask != 0) { Interlocked.And(ref pad, ~mask); e.Handled = true; }
        else if (e.KeyCode == Keys.Tab) { turbo = false; e.Handled = true; }
        base.OnKeyUp(e);
    }

    // ---------- mouse buttons (can be assigned to pad buttons in the Controls window) ----------

    private static Keys MouseKey(MouseButtons b) => b switch
    {
        MouseButtons.Left => Keys.LButton,
        MouseButtons.Right => Keys.RButton,
        MouseButtons.Middle => Keys.MButton,
        MouseButtons.XButton1 => Keys.XButton1,
        MouseButtons.XButton2 => Keys.XButton2,
        _ => Keys.None
    };

    protected override void OnMouseDown(MouseEventArgs e)
    {
        int mask = keyMap.MaskFor(MouseKey(e.Button));
        if (mask != 0) Interlocked.Or(ref pad, mask);
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        int mask = keyMap.MaskFor(MouseKey(e.Button));
        if (mask != 0) Interlocked.And(ref pad, ~mask);
        base.OnMouseUp(e);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        Interlocked.Exchange(ref pad, 0);                                 // release all buttons when the window loses focus
        turbo = false;
        base.OnDeactivate(e);
    }

    private void ShowControls()
    {
        bool wasPaused = paused;
        paused = true;                                                    // stop the game while the Controls window is open
        Interlocked.Exchange(ref pad, 0);
        turbo = false;
        using (var dialog = new KeyMapDialog(keyMap.Clone()))
        {
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                keyMap = dialog.Result;
                try { keyMap.Save(KeyMap.DefaultPath); }
                catch (Exception ex) { MessageBox.Show("Could not save the key map:\n" + ex.Message, gameName, MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }
        }
        paused = wasPaused;
        UpdateTitle();
    }

    // ---------- emulation thread ----------

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        audio = AudioOut.TryCreate(48000);                                // null if there is no sound device: the game then runs silently
        machine.Bus.Sound.OutputRate = 48000;
        machine.Bus.Sound.Capture = audio != null;
        worker = new Thread(RunLoop) { IsBackground = true, Name = "Emulation" };
        worker.Start();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        running = false;
        worker?.Join(1500);
        audio?.Dispose();
        audio = null;
        base.OnFormClosing(e);
    }

    private void RunLoop()
    {
        var clock = Stopwatch.StartNew();
        double next = 0, fpsStart = 0;
        int frames = 0;
        while (running)
        {
            if (paused) { Thread.Sleep(20); next = clock.Elapsed.TotalMilliseconds; fpsStart = next; frames = 0; continue; }

            machine.Bus.Pad1 = (byte)Volatile.Read(ref pad);
            try { machine.RunFrame(); }
            catch (GenesisRecomp.Cpu.AddressErrorException ex)
            {
                running = false;
                string msg = $"The 68000 raised an address error at 0x{machine.Cpu.CurrentInstructionAddress:X6}.\n{ex.Message}";
                try { BeginInvoke(new MethodInvoker(() => { MessageBox.Show(msg, gameName, MessageBoxButtons.OK, MessageBoxIcon.Error); Close(); })); }
                catch (Exception) { }
                return;
            }

            bool audioPaced = false;
            var sound = machine.Bus.Sound;
            if (audio != null)
            {
                var (samples, count) = sound.Collect();
                if (!turbo && count > 0) audioPaced = audio.Submit(samples, count);     // blocks while the sound card is busy: that sets the game speed
            }
            else if (sound.Capture) sound.Collect();

            lock (frameLock)
            {
                Array.Copy(machine.Vdp.Frame, shared, shared.Length);
                srcWidth = machine.Vdp.Width;
                srcHeight = machine.Vdp.Height;
            }
            try { if (IsHandleCreated && !IsDisposed) BeginInvoke(new MethodInvoker(() => Invalidate())); }
            catch (Exception) { }

            double now = clock.Elapsed.TotalMilliseconds;
            frames++;
            if (now - fpsStart >= 1000)
            {
                fps = frames * 1000.0 / (now - fpsStart);
                frames = 0; fpsStart = now;
                try { if (IsHandleCreated && !IsDisposed) BeginInvoke(new MethodInvoker(() => UpdateTitle())); }
                catch (Exception) { }
            }

            if (turbo) { next = now; continue; }
            if (audioPaced) { next = clock.Elapsed.TotalMilliseconds; continue; }       // the sound card is keeping time for us
            next += FrameMs;
            double remaining = next - clock.Elapsed.TotalMilliseconds;
            if (remaining > 2) Thread.Sleep((int)(remaining - 1));
            while (clock.Elapsed.TotalMilliseconds < next) Thread.SpinWait(40);
            if (clock.Elapsed.TotalMilliseconds - next > 100) next = clock.Elapsed.TotalMilliseconds;   // fell far behind: resynchronise
        }
    }

    // ---------- drawing ----------

    protected override void OnPaint(PaintEventArgs e)
    {
        int w, h;
        lock (frameLock)
        {
            w = srcWidth; h = srcHeight;
            var data = bitmap.LockBits(new Rectangle(0, 0, 320, 240), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            Marshal.Copy(shared, 0, data.Scan0, shared.Length);
            bitmap.UnlockBits(data);
        }

        var g = e.Graphics;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.Clear(Color.Black);

        double aspect = tvAspect ? 4.0 / 3.0 : (double)w / h;
        int cw = ClientSize.Width, ch = ClientSize.Height;
        int dh = (int)Math.Min(ch, cw / aspect);
        int dw = (int)(dh * aspect);
        g.DrawImage(bitmap, new Rectangle((cw - dw) / 2, (ch - dh) / 2, dw, dh), 0, 0, w, h, GraphicsUnit.Pixel);
    }

    private void UpdateTitle() =>
        Text = $"{gameName}  -  {(paused ? "PAUSED" : fps.ToString("F1") + " fps")}   [F1 controls | P pause | Tab fast-forward | F2 screenshot | F3 aspect | Esc quit]";

    private void SaveScreenshot()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "GenesisRecomp_screenshots");
        Directory.CreateDirectory(dir);
        int[] copy = new int[shared.Length];
        int w, h;
        lock (frameLock) { Array.Copy(shared, copy, copy.Length); w = srcWidth; h = srcHeight; }
        string path = Path.Combine(dir, $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.bmp");
        Bmp.Save(path, copy, 320, w, h);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) bitmap.Dispose();
        base.Dispose(disposing);
    }
}
#endif
