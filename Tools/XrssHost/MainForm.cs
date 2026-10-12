using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace XrssHostApp
{
    /// <summary>
    /// The main window: status first, one Pair Headset button, the pairing panel (shown only while pairing), the paired-device list, and a collapsed details section.
    /// It only reads <see cref="HostStatus"/> and calls <see cref="HostController"/>; it holds no transport logic.
    /// </summary>
    sealed class MainForm : Form
    {
        readonly HostController m_C;
        readonly Autostart m_Auto;
        readonly string m_Exe;
        readonly string m_Version;
        readonly TableLayoutPanel m_Flow = new TableLayoutPanel { ColumnCount = 1, GrowStyle = TableLayoutPanelGrowStyle.AddRows, AutoScroll = true, Dock = DockStyle.Fill, Padding = new Padding(20, 16, 20, 16), BackColor = Theme.Bg };
        readonly List<Control> m_Wide = new List<Control>();
        readonly List<Label> m_Wrap = new List<Label>();

        StatusDot m_Dot; Label m_Headline, m_Detail, m_Notice, m_HostVal, m_HeadsetVal, m_ConnVal;
        Button m_Pair, m_StartStop, m_Launch, m_Install; Label m_LaunchMsg;
        Panel m_PairCard; Label m_PairTitle, m_PairBody, m_Countdown, m_Code, m_PairResult; Button m_Same, m_Differ, m_Cancel, m_Dismiss, m_PairAgain;
        ListView m_List; Button m_Remove, m_RemoveAll; Label m_Empty;
        CheckBox m_AutoBox; bool m_SettingAuto;
        LinkLabel m_DiagToggle; Panel m_DiagPanel; TextBox m_DiagText;
        readonly Timer m_Tick = new Timer { Interval = 500 };
        string m_ListSig = "";
        public bool Exiting;
        public event Action<bool> StartWithWindowsChanged;

        public MainForm(HostController c, Autostart auto, string exePath, string version)
        {
            m_C = c; m_Auto = auto; m_Exe = exePath; m_Version = version;
            Text = "XR Spatial Screens Host";
            AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96f, 96f);
            BackColor = Theme.Bg; ForeColor = Theme.Text; Font = Theme.Ui(10f);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(620, 800); MinimumSize = new Size(560, 520);
            try { using (var b = IconFactory.Draw(32, null)) Icon = IconFactory.ToIcon(b); } catch (Exception) { }

            m_Flow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Build();
            Controls.Add(m_Flow);
            Resize += (s, e) => FitWidths();
            Shown += (s, e) => FitWidths();
            m_Tick.Tick += (s, e) => RefreshView();
            m_Tick.Start();
            c.Changed += () => { try { if (IsHandleCreated && !IsDisposed) BeginInvoke(new Action(RefreshView)); } catch (Exception) { } };
            RefreshView();
        }

        // ------------------------------------------------------------------ construction

        void Add(Control ctl, bool wide = true) { if (wide) { ctl.Dock = DockStyle.Top; m_Wide.Add(ctl); } else ctl.Anchor = AnchorStyles.Top | AnchorStyles.Left; m_Flow.Controls.Add(ctl); }

        void Build()
        {
            // header
            var header = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, 14) };
            header.Controls.Add(Theme.Label("XR Spatial Screens Host", 18f, Theme.Text, FontStyle.Bold));
            header.Controls.Add(Theme.Label("Gamebreak Labs", 9.5f, Theme.Dim));
            Add(header, false);

            // status card
            var card = Theme.CardPanel();
            var t = new TableLayoutPanel { ColumnCount = 2, RowCount = 3, AutoSize = true, Dock = DockStyle.Top, BackColor = Color.Transparent };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64)); t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            m_Dot = new StatusDot { Margin = new Padding(0, 4, 0, 0) };
            m_Headline = Theme.Label("", 20f, Theme.Text, FontStyle.Bold);
            m_Detail = Theme.Label("", 10.5f, Theme.Dim);
            m_Notice = Theme.Label("", 10f, Theme.Warn); m_Notice.Margin = new Padding(0, 6, 0, 0);
            t.Controls.Add(m_Dot, 0, 0); t.SetRowSpan(m_Dot, 2);
            t.Controls.Add(m_Headline, 1, 0); t.Controls.Add(m_Detail, 1, 1);
            t.Controls.Add(m_Notice, 1, 2);
            m_Wrap.Add(m_Detail); m_Wrap.Add(m_Notice);
            card.Controls.Add(t);
            Add(card);

            // info grid
            var info = Theme.CardPanel();
            var g = new TableLayoutPanel { ColumnCount = 2, RowCount = 3, AutoSize = true, Dock = DockStyle.Top, BackColor = Color.Transparent };
            g.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110)); g.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            m_HostVal = Theme.Label("", 10.5f); m_HeadsetVal = Theme.Label("", 10.5f); m_ConnVal = Theme.Label("", 10.5f);
            void Row(int r, string name, Label v) { var l = Theme.Label(name, 10.5f, Theme.Dim); l.Margin = new Padding(0, 3, 0, 3); v.Margin = new Padding(0, 3, 0, 3); g.Controls.Add(l, 0, r); g.Controls.Add(v, 1, r); }
            Row(0, "Host", m_HostVal); Row(1, "Headset", m_HeadsetVal); Row(2, "Connection", m_ConnVal);
            m_Wrap.Add(m_HostVal); m_Wrap.Add(m_HeadsetVal); m_Wrap.Add(m_ConnVal);
            info.Controls.Add(g);
            Add(info);

            // buttons
            var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, 14) };
            m_Pair = Theme.Button("Pair Headset", true, 220); m_Pair.Click += (s, e) => m_C.PairHeadset();
            m_StartStop = Theme.Button("Stop Host", false, 130); m_StartStop.Height = 46; m_StartStop.Click += (s, e) => { if (m_C.IsRunning) m_C.StopHost(); else m_C.StartHost(); };
            m_Launch = Theme.Button("Launch on Headset", false, 190); m_Launch.Height = 46; m_Launch.Click += (s, e) => m_C.LaunchHeadset();
            m_Install = Theme.Button("Install / Update on Headset", false, 250); m_Install.Height = 46; m_Install.Click += (s, e) => m_C.InstallOnHeadset();
            row.Controls.Add(m_Pair); row.Controls.Add(m_StartStop); row.Controls.Add(m_Launch); row.Controls.Add(m_Install);
            Add(row, false);
            m_LaunchMsg = Theme.Label("", 11.5f, Theme.Dim, FontStyle.Bold); m_LaunchMsg.Margin = new Padding(0, 0, 0, 10); m_LaunchMsg.Visible = false; m_Wrap.Add(m_LaunchMsg);
            Add(m_LaunchMsg, false);

            BuildPairCard();
            BuildDevices();

            // options
            m_AutoBox = new CheckBox { Text = "Start XR Spatial Screens Host with Windows", AutoSize = true, ForeColor = Theme.Text, BackColor = Color.Transparent, Font = Theme.Ui(10f), Margin = new Padding(0, 4, 0, 8) };
            m_AutoBox.CheckedChanged += (s, e) => { if (!m_SettingAuto) StartWithWindowsChanged?.Invoke(m_AutoBox.Checked); };
            Add(m_AutoBox, false);

            // details
            m_DiagToggle = new LinkLabel { Text = "Show details", AutoSize = true, LinkColor = Theme.Accent, ActiveLinkColor = Theme.Text, VisitedLinkColor = Theme.Accent, BackColor = Color.Transparent, Font = Theme.Ui(10f), Margin = new Padding(0, 0, 0, 6) };
            m_DiagToggle.LinkClicked += (s, e) => { m_DiagPanel.Visible = !m_DiagPanel.Visible; m_DiagToggle.Text = m_DiagPanel.Visible ? "Hide details" : "Show details"; RefreshDiag(); FitWidths(); };
            Add(m_DiagToggle, false);
            m_DiagPanel = Theme.CardPanel(); m_DiagPanel.Visible = false;
            var dflow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Dock = DockStyle.Top, BackColor = Color.Transparent };
            m_DiagText = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Height = 230, Width = 520, BackColor = Theme.Bg, ForeColor = Theme.Text, BorderStyle = BorderStyle.None, Font = new Font("Consolas", 9f), Margin = new Padding(0, 0, 0, 8) };
            var drow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, BackColor = Color.Transparent };
            var open = Theme.Button("Open Log Folder", false, 150); open.Click += (s, e) => OpenLogs();
            var copy = Theme.Button("Copy Diagnostics", false, 150); copy.Click += (s, e) => { try { Clipboard.SetText(m_C.DiagnosticsText(m_Version)); copy.Text = "Copied"; } catch (Exception) { copy.Text = "Copy failed"; } };
            copy.Leave += (s, e) => copy.Text = "Copy Diagnostics";
            drow.Controls.Add(open); drow.Controls.Add(copy);
            dflow.Controls.Add(m_DiagText); dflow.Controls.Add(drow);
            m_DiagPanel.Controls.Add(dflow);
            Add(m_DiagPanel);
            Add(Theme.Label("Version " + m_Version + "  -  Gamebreak Labs", 8.5f, Theme.Dim), false);
        }

        void BuildPairCard()
        {
            m_PairCard = Theme.CardPanel(); m_PairCard.Visible = false; m_PairCard.BackColor = Color.FromArgb(0x1B, 0x26, 0x3D);
            var f = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Dock = DockStyle.Top, BackColor = Color.Transparent };
            m_PairTitle = Theme.Label("", 13f, Theme.Text, FontStyle.Bold);
            m_PairBody = Theme.Label("", 10.5f, Theme.Text); m_PairBody.Margin = new Padding(0, 4, 0, 4);
            m_Code = Theme.Label("", 44f, Theme.Text, FontStyle.Bold); m_Code.Font = new Font("Consolas", 44f, FontStyle.Bold); m_Code.Margin = new Padding(0, 4, 0, 4);
            m_Countdown = Theme.Label("", 10.5f, Theme.Warn); m_Countdown.Margin = new Padding(0, 0, 0, 8);
            m_PairResult = Theme.Label("", 10.5f, Theme.Text); m_PairResult.Margin = new Padding(0, 0, 0, 8);
            m_Wrap.Add(m_PairBody); m_Wrap.Add(m_PairResult);
            var b = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Color.Transparent };
            m_Same = Theme.Button("Same code", true, 170); m_Same.BackColor = Color.FromArgb(0x2E, 0xA8, 0x6B); m_Same.Click += (s, e) => m_C.SameCode();
            m_Differ = Theme.Button("Doesn't match", false, 150); m_Differ.Height = 46; m_Differ.Click += (s, e) => m_C.DoesNotMatch();
            m_Cancel = Theme.Button("Cancel", false, 110); m_Cancel.Click += (s, e) => m_C.CancelPairing();
            m_Dismiss = Theme.Button("Done", false, 110); m_Dismiss.Click += (s, e) => m_C.DismissPairResult();
            m_PairAgain = Theme.Button("Pair Again", true, 150); m_PairAgain.Click += (s, e) => m_C.PairHeadset();
            b.Controls.AddRange(new Control[] { m_Same, m_Differ, m_PairAgain, m_Cancel, m_Dismiss });
            f.Controls.AddRange(new Control[] { m_PairTitle, m_PairBody, m_Code, m_Countdown, m_PairResult, b });
            m_PairCard.Controls.Add(f);
            Add(m_PairCard);
        }

        void BuildDevices()
        {
            Add(Theme.Label("Paired devices", 12f, Theme.Text, FontStyle.Bold), false);
            var card = Theme.CardPanel(); card.Padding = new Padding(10);
            var f = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Dock = DockStyle.Top, BackColor = Color.Transparent };
            m_List = new ListView { View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false, OwnerDraw = true, BackColor = Theme.Card, ForeColor = Theme.Text, BorderStyle = BorderStyle.None, Height = 150, Width = 540, HeaderStyle = ColumnHeaderStyle.Nonclickable, Font = Theme.Ui(10f), Margin = new Padding(0, 0, 0, 8) };
            m_List.Columns.Add("Headset", 170); m_List.Columns.Add("Paired", 110); m_List.Columns.Add("Last connected", 130); m_List.Columns.Add("Status", 90);
            m_List.DrawColumnHeader += (s, e) => { using (var br = new SolidBrush(Theme.Card)) e.Graphics.FillRectangle(br, e.Bounds); TextRenderer.DrawText(e.Graphics, e.Header.Text, Theme.Ui(9f), e.Bounds, Theme.Dim, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis); };
            m_List.DrawItem += (s, e) => { };
            m_List.DrawSubItem += (s, e) =>
            {
                bool sel = e.Item.Selected;
                using (var br = new SolidBrush(sel ? Color.FromArgb(0x2A, 0x3A, 0x5E) : Theme.Card)) e.Graphics.FillRectangle(br, e.Bounds);
                Color c = e.ColumnIndex == 3 ? (e.SubItem.Text == "Connected" ? Theme.Good : Theme.Dim) : Theme.Text;
                TextRenderer.DrawText(e.Graphics, e.SubItem.Text, m_List.Font, new Rectangle(e.Bounds.X + 4, e.Bounds.Y, e.Bounds.Width - 6, e.Bounds.Height), c, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            };
            m_List.SelectedIndexChanged += (s, e) => UpdateRemoveButtons();
            m_Empty = Theme.Label("No headsets paired yet.", 10f, Theme.Dim); m_Empty.Margin = new Padding(4, 0, 0, 8);
            var b = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, BackColor = Color.Transparent };
            m_Remove = Theme.Button("Remove", false, 110); m_Remove.Click += (s, e) => RemoveSelected();
            m_RemoveAll = Theme.Button("Remove All", false, 110); m_RemoveAll.Click += (s, e) => RemoveEverything();
            b.Controls.Add(m_Remove); b.Controls.Add(m_RemoveAll);
            f.Controls.Add(m_List); f.Controls.Add(m_Empty); f.Controls.Add(b);
            card.Controls.Add(f);
            Add(card);
        }

        // ------------------------------------------------------------------ layout

        void FitWidths()
        {
            int w = m_Flow.ClientSize.Width - m_Flow.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth;
            if (w < 200) return;
            foreach (var l in m_Wrap) l.MaximumSize = new Size(Math.Max(120, w - 100), 0);
            m_PairBody.MaximumSize = new Size(w - 40, 0); m_PairResult.MaximumSize = new Size(w - 40, 0);
            m_List.Width = w - 22; m_DiagText.Width = w - 34;
            int cols = m_List.ClientSize.Width;
            if (cols > 200) { m_List.Columns[3].Width = 90; m_List.Columns[2].Width = 130; m_List.Columns[1].Width = 110; m_List.Columns[0].Width = Math.Max(110, cols - 330); }
        }

        // ------------------------------------------------------------------ view

        void RefreshView()
        {
            if (IsDisposed) return;
            var st = m_C.Snapshot();
            m_Dot.Tone = Theme.ToneColor(st.Tone);
            m_Headline.Text = st.Headline; m_Detail.Text = st.Detail;
            m_Notice.Text = st.Notice ?? ""; m_Notice.Visible = st.Notice != null;
            m_LaunchMsg.ForeColor = st.Launching ? Theme.Dim : st.LaunchOk ? Theme.Good : Theme.Warn; m_LaunchMsg.Text = st.LaunchStatus ?? ""; m_LaunchMsg.Visible = st.LaunchStatus != null; m_Launch.Enabled = !st.Launching; m_Install.Enabled = !st.Launching;
            m_HostVal.Text = st.Running ? "Running" : "Stopped";
            m_HeadsetVal.Text = st.HeadsetConnected ? "Connected  -  " + st.HeadsetLabel : "Not connected";
            m_ConnVal.Text = !st.HeadsetConnected ? "-" : (st.Connection ?? "Local network") + (st.LocalAddress != null && st.Connection != "Developer (adb)" ? "  -  " + st.LocalAddress + ":" + st.Port : "");
            m_StartStop.Text = st.Running ? "Stop Host" : "Start Host";
            m_Pair.Enabled = true;

            // pairing panel
            bool show = st.Stage != PairStage.None;
            m_PairCard.Visible = show;
            if (show)
            {
                bool waiting = st.Stage == PairStage.WaitingForHeadset, code = st.Stage == PairStage.CodeShown, conf = st.Stage == PairStage.Confirmed;
                bool ok = st.Stage == PairStage.Succeeded, bad = st.Stage == PairStage.Failed || st.Stage == PairStage.Expired;
                m_PairTitle.Text = ok ? "Headset paired" : bad ? "Pairing didn't finish" : code ? "Compare the code" : conf ? "Waiting for the headset" : "Pair a headset";
                m_PairBody.Text = waiting ? "Open XR Spatial Screens on your Steam Frame and choose Pair with PC."
                                : code ? "Your headset should show the same six digits. Only confirm if they are identical."
                                : conf ? "You confirmed the code. Confirm it on the headset too." : "";
                m_PairBody.Visible = m_PairBody.Text.Length > 0;
                m_Code.Visible = code; m_Code.Text = st.Code != null && st.Code.Length == 6 ? st.Code.Substring(0, 3) + " " + st.Code.Substring(3) : "";
                bool counting = waiting || code || conf;
                m_Countdown.Visible = counting;
                var rem = st.PairingRemaining < TimeSpan.Zero ? TimeSpan.Zero : st.PairingRemaining;
                m_Countdown.Text = "Pairing available for " + (int)rem.TotalMinutes + ":" + rem.Seconds.ToString("00");
                m_PairResult.Visible = !string.IsNullOrEmpty(st.PairMessage);
                m_PairResult.Text = st.PairMessage ?? "";
                m_PairResult.ForeColor = ok ? Theme.Good : bad ? Theme.Warn : Theme.Text;
                m_Same.Visible = code; m_Differ.Visible = code;
                m_Cancel.Visible = counting; m_Dismiss.Visible = ok; m_PairAgain.Visible = bad;
            }

            RefreshDevices(st);
            m_SettingAuto = true; try { m_AutoBox.Checked = m_Auto.IsEnabled; } finally { m_SettingAuto = false; }
            if (m_DiagPanel.Visible) RefreshDiag();
        }

        void RefreshDevices(HostStatus st)
        {
            string sig = string.Join("|", st.Devices.Select(d => d.DeviceId + d.Label + d.Connected + d.LastConnectedUtc?.Ticks));
            if (sig != m_ListSig)
            {
                m_ListSig = sig;
                string selected = m_List.SelectedItems.Count > 0 ? (string)m_List.SelectedItems[0].Tag : null;
                m_List.BeginUpdate();
                m_List.Items.Clear();
                foreach (var d in st.Devices)
                {
                    var it = new ListViewItem(new[] { d.Label, d.PairedUtc.ToLocalTime().ToString("MMM d, yyyy"), d.LastConnectedUtc.HasValue ? Ago(d.LastConnectedUtc.Value) : "Never", d.Connected ? "Connected" : "Offline" }) { Tag = d.DeviceId };
                    it.Selected = d.DeviceId == selected;
                    m_List.Items.Add(it);
                }
                m_List.EndUpdate();
            }
            m_List.Visible = st.Devices.Count > 0; m_Empty.Visible = st.Devices.Count == 0;
            UpdateRemoveButtons();
        }

        void UpdateRemoveButtons() { m_Remove.Enabled = m_List.SelectedItems.Count > 0; m_RemoveAll.Enabled = m_List.Items.Count > 0; }

        static string Ago(DateTime utc)
        {
            var l = utc.ToLocalTime();
            if (l.Date == DateTime.Today) return "Today " + l.ToString("HH:mm");
            if (l.Date == DateTime.Today.AddDays(-1)) return "Yesterday";
            return l.ToString("MMM d");
        }

        string m_LastDiag;
        void RefreshDiag()
        {
            string t = m_C.DiagnosticsText(m_Version).Replace("\n", "\r\n").Replace("\r\r\n", "\r\n");
            if (t != m_LastDiag) { m_LastDiag = t; m_DiagText.Text = t; }
        }

        // ------------------------------------------------------------------ actions

        void RemoveSelected()
        {
            if (m_List.SelectedItems.Count == 0) return;
            var it = m_List.SelectedItems[0];
            if (MessageBox.Show(this, "Remove " + it.Text + "?\r\n\r\nIt disconnects now and has to be paired again before it can connect.", "Remove headset", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                m_C.Remove((string)it.Tag);
        }

        void RemoveEverything()
        {
            if (m_List.Items.Count == 0) return;
            if (MessageBox.Show(this, "Remove all paired headsets?\r\n\r\nEvery headset disconnects now and has to be paired again.", "Remove All", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
                m_C.RemoveAll();
        }

        void OpenLogs() { try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + m_C.Log.LogDirectory + "\"") { UseShellExecute = true }); } catch (Exception) { } }

        public void FocusDevices() { ShowAndActivate(); m_List.Focus(); }

        public void ShowAndActivate()
        {
            if (!Visible) Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate(); BringToFront();
            RefreshView(); FitWidths();
        }

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { int dark = 1; DwmSetWindowAttribute(Handle, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref dark, 4); } catch (Exception) { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!Exiting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }       // the host keeps running in the tray
            base.OnFormClosing(e);
        }
    }
}
