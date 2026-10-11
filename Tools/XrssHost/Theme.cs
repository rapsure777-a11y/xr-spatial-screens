using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace XrssHostApp
{
    /// <summary>The dark utility look: colours, fonts and small control factories shared by the window.</summary>
    static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(0x14, 0x16, 0x1B), Card = Color.FromArgb(0x1E, 0x21, 0x28), CardLine = Color.FromArgb(0x2B, 0x2F, 0x39);
        public static readonly Color Text = Color.FromArgb(0xEA, 0xEC, 0xF2), Dim = Color.FromArgb(0x93, 0x9B, 0xAB);
        public static readonly Color Accent = Color.FromArgb(0x4C, 0x8D, 0xFF), Good = Color.FromArgb(0x3D, 0xD6, 0x8C), Warn = Color.FromArgb(0xF5, 0xB6, 0x42), Bad = Color.FromArgb(0xF2, 0x6B, 0x6B), Idle = Color.FromArgb(0x6B, 0x73, 0x84);

        public static Color ToneColor(Tone t) => t == Tone.Good ? Good : t == Tone.Warn ? Warn : t == Tone.Bad ? Bad : Idle;

        public static Font Ui(float size, FontStyle style = FontStyle.Regular) => new Font("Segoe UI", size, style, GraphicsUnit.Point);

        public static Label Label(string text, float size = 10f, Color? color = null, FontStyle style = FontStyle.Regular)
            => new Label { Text = text, AutoSize = true, ForeColor = color ?? Text, BackColor = Color.Transparent, Font = Ui(size, style), Margin = new Padding(0) };

        public static Button Button(string text, bool primary = false, int width = 150)
        {
            var b = new Button
            {
                Text = text, Width = width, Height = primary ? 46 : 36, FlatStyle = FlatStyle.Flat, Font = Ui(primary ? 11.5f : 10f, primary ? FontStyle.Bold : FontStyle.Regular),
                BackColor = primary ? Accent : CardLine, ForeColor = primary ? Color.White : Text, Cursor = Cursors.Hand, UseVisualStyleBackColor = false, Margin = new Padding(0, 0, 8, 0)
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(0x6A, 0xA2, 0xFF) : Color.FromArgb(0x3A, 0x3F, 0x4C);
            b.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(0x3A, 0x78, 0xE6) : Color.FromArgb(0x24, 0x28, 0x31);
            return b;
        }

        public static Panel CardPanel() => new Panel { BackColor = Card, Padding = new Padding(16), Margin = new Padding(0, 0, 0, 12), AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top };
    }

    /// <summary>The big round status indicator.</summary>
    sealed class StatusDot : Control
    {
        Color m_Color = Theme.Idle;
        public StatusDot() { Size = new Size(44, 44); DoubleBuffered = true; SetStyle(ControlStyles.SupportsTransparentBackColor, true); BackColor = Color.Transparent; }
        public Color Tone { get => m_Color; set { if (m_Color != value) { m_Color = value; Invalidate(); } } }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var halo = new SolidBrush(Color.FromArgb(60, m_Color))) e.Graphics.FillEllipse(halo, 0, 0, Width - 1, Height - 1);
            using (var core = new SolidBrush(m_Color)) e.Graphics.FillEllipse(core, 9, 9, Width - 19, Height - 19);
        }
    }

    static class IconFactory
    {
        /// <summary>The app glyph (a screen with a corner mark) on a dark tile, with an optional status dot.</summary>
        public static Bitmap Draw(int size, Color? dot)
        {
            var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(Color.Transparent);
                float r = size * 0.22f;
                using (var path = Round(new RectangleF(0, 0, size - 1, size - 1), r))
                using (var bg = new LinearGradientBrush(new Rectangle(0, 0, size, size), Color.FromArgb(0x2A, 0x3A, 0x66), Color.FromArgb(0x12, 0x16, 0x26), 90f)) g.FillPath(bg, path);
                float pw = Math.Max(1.5f, size / 12f);
                using (var pen = new Pen(Color.FromArgb(0x8C, 0xB4, 0xFF), pw))
                {
                    var screen = new RectangleF(size * 0.17f, size * 0.24f, size * 0.66f, size * 0.46f);
                    using (var sp = Round(screen, size * 0.06f)) g.DrawPath(pen, sp);
                    g.DrawLine(pen, size * 0.5f, screen.Bottom, size * 0.5f, size * 0.8f);
                    g.DrawLine(pen, size * 0.32f, size * 0.8f, size * 0.68f, size * 0.8f);
                }
                if (dot.HasValue)
                {
                    float d = size * 0.38f;
                    using (var ring = new SolidBrush(Color.FromArgb(0x12, 0x16, 0x26))) g.FillEllipse(ring, size - d - 1, size - d - 1, d + 1, d + 1);
                    using (var b = new SolidBrush(dot.Value)) g.FillEllipse(b, size - d + size * 0.04f, size - d + size * 0.04f, d - size * 0.08f, d - size * 0.08f);
                }
            }
            return bmp;
        }

        static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath(); float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90); p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90); p.CloseFigure();
            return p;
        }

        public static Icon ToIcon(Bitmap bmp) { IntPtr h = bmp.GetHicon(); try { return (Icon)Icon.FromHandle(h).Clone(); } finally { NativeMethods.DestroyIcon(h); } }
    }

    static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);
    }
}
