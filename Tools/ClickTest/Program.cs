using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

// A tiny window that records every mouse event it really receives (client and screen coordinates, button) into a log file and shows the last one:
// the target for verifying that XR Spatial Screens forwards a laser click to the right pixel.
// usage: ClickTest.exe [x y w h] [logfile] [title]
// log lines: "client X,Y screen X,Y ... button B" (press), "up ...", "double ...", "move-held ..." (moves while a button is held), "wheel D ..."
static class Program
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    sealed class Surface : Control
    {
        public string Caption = "click me";
        public Surface() { SetStyle(ControlStyles.StandardClick | ControlStyles.StandardDoubleClick | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true); BackColor = Color.FromArgb(30, 60, 90); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            using var f = new Font("Consolas", 18);
            e.Graphics.DrawString(Caption, f, Brushes.White, new RectangleF(0, 0, Width, Height), new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
        }
    }

    [STAThread]
    static void Main(string[] a)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);       // real pixels, so logged coordinates match what the player computes
        int x = a.Length > 3 ? int.Parse(a[0]) : 2700, y = a.Length > 3 ? int.Parse(a[1]) : 300, w = a.Length > 3 ? int.Parse(a[2]) : 700, h = a.Length > 3 ? int.Parse(a[3]) : 500;
        string log = a.Length > 4 ? a[4] : Path.Combine(Path.GetTempPath(), "clicktest.txt");
        string title = a.Length > 5 ? a[5] : "XRSS ClickTest";
        File.WriteAllText(log, "started\n");
        var f = new Form { Text = title, StartPosition = FormStartPosition.Manual, Location = new Point(x, y), ClientSize = new Size(w, h), BackColor = Color.FromArgb(30, 60, 90) };
        if (a.Length > 6 && a[6] == "topmost") { f.TopMost = true; f.Shown += (o, e) => { SetWindowPos(f.Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); f.Activate(); }; }          // a window that stays above everything, to test that a covered target is refused
        var s = new Surface { Dock = DockStyle.Fill };
        f.Controls.Add(s);

        void Write(string kind, MouseEventArgs e)
        {
            var screen = s.PointToScreen(e.Location);
            string msg = $"{kind} client {e.X},{e.Y} screen {screen.X},{screen.Y} button {e.Button}";
            s.Caption = msg.Replace(" screen", "\nscreen"); s.Invalidate();
            File.AppendAllText(log, msg + "\n");
        }
        s.MouseDown += (o, e) => Write("press", e);
        s.MouseUp += (o, e) => Write("up", e);
        s.MouseDoubleClick += (o, e) => Write("double", e);
        s.MouseMove += (o, e) => { if (e.Button != MouseButtons.None) Write("move-held", e); };
        s.MouseWheel += (o, e) => File.AppendAllText(log, $"wheel {e.Delta} at client {e.X},{e.Y}\n");
        f.KeyPreview = true;                                                      // keyboard events for the headset typing tests
        f.KeyDown += (o, e) => File.AppendAllText(log, $"keydown {e.KeyCode} ctrl={e.Control} shift={e.Shift} alt={e.Alt}\n");
        f.KeyUp += (o, e) => File.AppendAllText(log, $"keyup {e.KeyCode}\n");
        f.KeyPress += (o, e) => File.AppendAllText(log, $"char '{e.KeyChar}' U+{(int)e.KeyChar:X4}\n");
        Application.Run(f);
    }
}
