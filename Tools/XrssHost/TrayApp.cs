using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;

namespace XrssHostApp
{
    /// <summary>
    /// Owns the notification-area icon and its menu, and the main window (created once, hidden instead of closed). The host itself runs regardless of whether the window is visible.
    /// </summary>
    sealed class TrayApp : ApplicationContext
    {
        readonly HostController m_C;
        readonly Autostart m_Auto;
        readonly string m_Exe;
        readonly MainForm m_Form;
        readonly NotifyIcon m_Tray;
        readonly ToolStripMenuItem m_StartStop, m_AutoItem;
        readonly Dictionary<Tone, Icon> m_Icons = new Dictionary<Tone, Icon>();
        Tone? m_ShownTone; string m_ShownText;
        bool m_FirstHide = true;

        public TrayApp(HostController c, Autostart auto, string exe, string version, bool showWindow)
        {
            m_C = c; m_Auto = auto; m_Exe = exe;
            m_Form = new MainForm(c, auto, exe, version);
            m_Form.StartWithWindowsChanged += SetAutostart;
            m_Form.FormClosing += (s, e) => { if (!m_Form.Exiting && m_FirstHide && e.CloseReason == CloseReason.UserClosing) { m_FirstHide = false; Toast("XR Spatial Screens Host is still running here. Right-click the icon for options."); } };

            var menu = new ContextMenuStrip { ShowImageMargin = false, Font = Theme.Ui(9.5f) };
            var open = new ToolStripMenuItem("Open XR Spatial Screens Host") { Font = Theme.Ui(9.5f, FontStyle.Bold) };
            open.Click += (s, e) => m_Form.ShowAndActivate();
            var pair = new ToolStripMenuItem("Pair Headset"); pair.Click += (s, e) => { m_Form.ShowAndActivate(); m_C.PairHeadset(); };
            var devices = new ToolStripMenuItem("Paired Devices"); devices.Click += (s, e) => m_Form.FocusDevices();
            m_StartStop = new ToolStripMenuItem("Stop Host"); m_StartStop.Click += (s, e) => { if (m_C.IsRunning) m_C.StopHost(); else m_C.StartHost(); };
            m_AutoItem = new ToolStripMenuItem("Start with Windows") { CheckOnClick = false }; m_AutoItem.Click += (s, e) => SetAutostart(!m_Auto.IsEnabled);
            var exit = new ToolStripMenuItem("Exit"); exit.Click += (s, e) => ExitApp();
            menu.Items.AddRange(new ToolStripItem[] { open, pair, devices, new ToolStripSeparator(), m_StartStop, m_AutoItem, new ToolStripSeparator(), exit });
            menu.Opening += (s, e) => { m_StartStop.Text = m_C.IsRunning ? "Stop Host" : "Start Host"; m_AutoItem.Checked = m_Auto.IsEnabled; };

            m_Tray = new NotifyIcon { ContextMenuStrip = menu, Visible = true, Text = "XR Spatial Screens Host" };
            m_Tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) m_Form.ShowAndActivate(); };
            m_Tray.DoubleClick += (s, e) => m_Form.ShowAndActivate();

            c.Changed += () => { try { if (m_Form.IsHandleCreated) m_Form.BeginInvoke(new Action(UpdateTray)); } catch (Exception) { } };
            c.AttentionNeeded += () => { try { if (m_Form.IsHandleCreated) m_Form.BeginInvoke(new Action(m_Form.ShowAndActivate)); } catch (Exception) { } };
            c.Toast += t => { try { if (m_Form.IsHandleCreated) m_Form.BeginInvoke(new Action(() => Toast(t))); } catch (Exception) { } };
            SystemEvents.PowerModeChanged += (s, e) => { if (e.Mode == PowerModes.Resume) m_C.OnResume(); };

            m_Form.CreateControl(); var _ = m_Form.Handle;               // a handle for BeginInvoke even while hidden
            UpdateTray();
            if (showWindow) m_Form.ShowAndActivate();
        }

        public void Show() => m_Form.ShowAndActivate();

        void UpdateTray()
        {
            var st = m_C.Snapshot();
            if (m_ShownTone != st.Tone)
            {
                if (!m_Icons.TryGetValue(st.Tone, out var ic)) { using (var b = IconFactory.Draw(32, Theme.ToneColor(st.Tone))) ic = IconFactory.ToIcon(b); m_Icons[st.Tone] = ic; }
                m_Tray.Icon = ic; m_ShownTone = st.Tone;
            }
            string text = "XR Spatial Screens Host - " + st.Headline;
            if (text != m_ShownText) { m_Tray.Text = text.Length > 63 ? text.Substring(0, 63) : text; m_ShownText = text; }
        }

        void Toast(string text) { try { m_Tray.ShowBalloonTip(4000, "XR Spatial Screens Host", text, ToolTipIcon.Info); } catch (Exception) { } }

        void SetAutostart(bool on)
        {
            try { if (on) m_Auto.Enable(m_Exe); else m_Auto.Disable(); m_C.Log.Info("start with Windows " + (on ? "enabled" : "disabled")); }
            catch (Exception e) { m_C.Log.Info("start with Windows could not be changed: " + e.Message); MessageBox.Show("Windows would not let this setting change: " + e.Message, "XR Spatial Screens Host"); }
        }

        void ExitApp()
        {
            m_Form.Exiting = true;
            m_Tray.Visible = false;
            m_C.StopHost();
            m_Tray.Dispose();
            m_Form.Close();
            ExitThread();
        }
    }
}
