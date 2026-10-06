using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

// A tiny window that records every mouse press (client and screen coordinates, button) into a log file and shows the last one:
// the target for verifying that XR Spatial Screens forwards a laser click to the right pixel.
// usage: ClickTest.exe [x y w h] [logfile]
static class Program
{
    [STAThread]
    static void Main(string[] a)
    {
        int x = a.Length > 3 ? int.Parse(a[0]) : 2700, y = a.Length > 3 ? int.Parse(a[1]) : 300, w = a.Length > 3 ? int.Parse(a[2]) : 700, h = a.Length > 3 ? int.Parse(a[3]) : 500;
        string log = a.Length > 4 ? a[4] : Path.Combine(Path.GetTempPath(), "clicktest.txt");
        File.WriteAllText(log, "started\n");
        var f = new Form { Text = "XRSS ClickTest", StartPosition = FormStartPosition.Manual, Location = new Point(x, y), ClientSize = new Size(w, h), BackColor = Color.FromArgb(30, 60, 90) };
        var l = new Label { Dock = DockStyle.Fill, ForeColor = Color.White, Font = new Font("Consolas", 22), Text = "click me", TextAlign = ContentAlignment.MiddleCenter };
        f.Controls.Add(l);
        MouseEventHandler h1 = (s, e) =>
        {
            var p = Cursor.Position;
            var screen = (s as Control).PointToScreen(e.Location);
            string msg = $"client {e.X + (s == l ? 0 : 0)},{e.Y} screen {screen.X},{screen.Y} cursor {p.X},{p.Y} button {e.Button}";
            l.Text = msg.Replace(" cursor", "\ncursor");
            File.AppendAllText(log, msg + "\n");
        };
        f.MouseDown += h1; l.MouseDown += h1;
        Application.Run(f);
    }
}
