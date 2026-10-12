using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading;
using XrssTransport;

namespace XrssHostApp
{
    enum PairStage { None, WaitingForHeadset, CodeShown, Confirmed, Succeeded, Failed, Expired }
    enum Tone { Idle, Good, Warn, Bad }

    sealed class DeviceRow
    {
        public string DeviceId, ShortId, Label;
        public DateTime PairedUtc;
        public DateTime? LastConnectedUtc;
        public bool Connected;
    }

    /// <summary>Everything the window and tray show, in customer language. Contains no secrets: device rows carry only label, short id and dates.</summary>
    sealed class HostStatus
    {
        public bool Running;
        public string Headline, Detail;
        public Tone Tone;
        public bool HeadsetConnected;
        public string HeadsetLabel;
        public string Connection;          // "Dongle", "Wi-Fi / LAN", "Developer (adb)", or null
        public string ConnectionDetail;    // "Connected via 10.0.0.224 (Ethernet)" for diagnostics
        public string LocalAddress;
        public int Port;
        public bool ListenerBound;
        public PairStage Stage;
        public bool PairingOpen;
        public TimeSpan PairingRemaining;
        public string Code;                // only while Stage == CodeShown
        public string PairMessage;         // success/failure wording for the pairing panel
        public string LaunchStatus;        // the result of the last "Launch on Headset" click (null until one happens)
        public bool Launching;             // a launch attempt is running
        public string Notice;              // something the user should know (no network, port in use, identity replaced, ...)
        public List<DeviceRow> Devices = new List<DeviceRow>();
    }

    /// <summary>
    /// The host app's view-model: the only thing the window and the tray talk to. It turns TransportHost's Host Core operations and events into one <see cref="HostStatus"/> and
    /// forwards user actions (pair, same code / doesn't match, remove, start / stop). It contains no SAS, TLS, HMAC, device-file or revocation logic of its own.
    /// </summary>
    sealed class HostController : IDisposable
    {
        readonly TransportHost m_Host;
        readonly HostLog m_Log;
        readonly Func<DateTime> m_Now;
        readonly Func<string, RouteInfo> m_Route;
        readonly object m_Lock = new object();

        PairStage m_Stage = PairStage.None;
        string m_Code, m_PairMessage, m_Notice;
        DateTime m_NoticeUntil;
        bool m_UserStopped;
        DateTime? m_LastDisconnect;
        bool m_WasConnected;
        Timer m_NetTimer;

        public event Action Changed;

        /// <summary>Starts the headset app over adb (see <see cref="HeadsetLauncher"/>); null in tests that do not need it.</summary>
        public HeadsetLauncher Launcher { get; set; }
        string m_LaunchStatus; bool m_Launching;

        /// <summary>The "Launch on Headset" button: runs in the background, reports through the status and a balloon.</summary>
        public void LaunchHeadset()
        {
            var l = Launcher; if (l == null || m_Launching) return;
            m_Launching = true; m_LaunchStatus = "Starting the app on the headset..."; Changed?.Invoke();
            l.LaunchAsync().ContinueWith(t =>
            {
                string msg = t.IsFaulted ? "Could not start the headset app: " + t.Exception.GetBaseException().Message : t.Result.Message;
                m_Log.Info("launch on headset: " + msg);
                m_LaunchStatus = msg; m_Launching = false;
                Toast?.Invoke(msg); Changed?.Invoke();
            });
        }
        /// <summary>The window should come to the front: a code is waiting for the user.</summary>
        public event Action AttentionNeeded;
        /// <summary>A short message for a balloon in the notification area (pairing finished, headset removed).</summary>
        public event Action<string> Toast;

        public HostController(TransportHost host, HostLog log, Func<DateTime> now = null, Func<string, RouteInfo> route = null)
        {
            m_Host = host; m_Log = log; m_Now = now ?? (() => DateTime.UtcNow); m_Route = route ?? (a => Route.Classify(a));
            host.Changed += OnHostChanged;
            host.CodeReady += OnCodeReady;
            host.Paired += OnPaired;
            host.PairingFailed += OnPairingFailed;
            host.DeviceRemoved += OnRemoved;
        }

        public TransportHost Host => m_Host;
        public HostLog Log => m_Log;
        public bool IsRunning => m_Host.IsRunning;

        // ------------------------------------------------------------------ actions

        public void StartHost()
        {
            m_UserStopped = false;
            try { m_Host.Start(); }
            catch (Exception e) { m_Log.Info("host could not start: " + e.GetType().Name + ": " + e.Message); SetNotice("The host could not start: " + e.Message, 60); }
            WatchNetwork();
            Changed?.Invoke();
        }

        public void StopHost()
        {
            m_UserStopped = true;
            lock (m_Lock) { m_Stage = PairStage.None; m_Code = null; }
            m_Host.Stop();
            Changed?.Invoke();
        }

        /// <summary>Pair Headset: opens the 2-minute enrollment window of Host Core. Starts the host first if it was stopped.</summary>
        public bool PairHeadset(TimeSpan? duration = null)
        {
            if (!m_Host.IsRunning) StartHost();
            if (!m_Host.OpenPairing(duration)) return false;
            lock (m_Lock) { m_Stage = PairStage.WaitingForHeadset; m_Code = null; m_PairMessage = null; }
            Changed?.Invoke();
            return true;
        }

        public void CancelPairing()
        {
            m_Host.ClosePairing();
            lock (m_Lock) { m_Stage = PairStage.None; m_Code = null; m_PairMessage = null; }
            Changed?.Invoke();
        }

        /// <summary>The user says the code on the PC is the same as the one on the headset. Host Core still waits for the headset side to confirm too.</summary>
        public bool SameCode()
        {
            bool ok = m_Host.ConfirmCode(true);
            if (ok) lock (m_Lock) { if (m_Stage == PairStage.CodeShown) m_Stage = PairStage.Confirmed; }
            Changed?.Invoke();
            return ok;
        }

        public bool DoesNotMatch()
        {
            bool ok = m_Host.ConfirmCode(false);
            Changed?.Invoke();
            return ok;
        }

        /// <summary>Hides a finished pairing result (success, failure, expiry).</summary>
        public void DismissPairResult()
        {
            lock (m_Lock) { if (m_Stage == PairStage.Succeeded || m_Stage == PairStage.Failed || m_Stage == PairStage.Expired) { m_Stage = PairStage.None; m_PairMessage = null; } }
            Changed?.Invoke();
        }

        public bool Remove(string deviceId) { bool r = m_Host.RemoveDevice(deviceId); Changed?.Invoke(); return r; }
        public int RemoveAll() { int n = m_Host.RemoveAllDevices(); Changed?.Invoke(); return n; }

        // ------------------------------------------------------------------ host events

        void OnHostChanged()
        {
            var a = m_Host.Active;
            if (a != null) Launcher?.Remember(a.Remote);          // the address the headset really uses, for "Launch on Headset" later
            lock (m_Lock)
            {
                if (a != null) { m_WasConnected = true; m_LastDisconnect = null; }
                else if (m_WasConnected) { m_WasConnected = false; m_LastDisconnect = m_Now(); }
            }
            Changed?.Invoke();
        }

        void OnCodeReady(PairingSession s)
        {
            lock (m_Lock) { m_Stage = PairStage.CodeShown; m_Code = s.Code; m_PairMessage = null; }
            AttentionNeeded?.Invoke();
        }

        void OnPaired(string label)
        {
            lock (m_Lock) { m_Stage = PairStage.Succeeded; m_Code = null; m_PairMessage = label + " is paired and connected."; }
            Toast?.Invoke(label + " is paired.");
            Changed?.Invoke();
        }

        void OnPairingFailed(PairingFailure kind, string why)
        {
            bool stillOpen = m_Host.Window != null && m_Host.Window.IsOpen;
            string msg;
            switch (kind)
            {
                case PairingFailure.HeadsetCodesDiffer: msg = "The headset says the codes didn't match. Nothing was paired."; break;
                case PairingFailure.PcCodesDiffer: msg = "You said the codes didn't match. Nothing was paired."; break;
                case PairingFailure.Timeout: msg = "Pairing took too long."; break;
                case PairingFailure.Disconnected: msg = "The headset disconnected before pairing finished."; break;
                case PairingFailure.WindowClosed: msg = "Pairing closed before it finished."; break;
                default: msg = "Pairing didn't complete."; break;
            }
            lock (m_Lock)
            {
                m_Code = null;
                if (stillOpen) { m_Stage = PairStage.WaitingForHeadset; m_PairMessage = msg + " You can try again on the headset."; }
                else { m_Stage = PairStage.Failed; m_PairMessage = msg + " Click Pair Headset to try again."; }
            }
            Changed?.Invoke();
        }

        void OnRemoved(string label)
        {
            SetNotice(label + " was removed. It has to be paired again before it can connect.", 30);
            Toast?.Invoke(label + " was removed.");
        }

        void SetNotice(string text, int seconds) { lock (m_Lock) { m_Notice = text; m_NoticeUntil = m_Now().AddSeconds(seconds); } Changed?.Invoke(); }

        // ------------------------------------------------------------------ status

        public HostStatus Snapshot()
        {
            var st = new HostStatus { Running = m_Host.IsRunning, Port = m_Host.Port, LaunchStatus = m_LaunchStatus, Launching = m_Launching };
            var window = m_Host.Window;
            var a = m_Host.Active;
            bool devActive = HostSessions.DevActive;

            st.PairingOpen = st.Running && window != null && window.IsOpen;
            if (st.PairingOpen && window.ExpiresUtc.HasValue) st.PairingRemaining = window.ExpiresUtc.Value - DateTime.UtcNow;
            st.ListenerBound = m_Host.Listener != null && m_Host.Listener.IsListening;

            lock (m_Lock)
            {
                // a window that closed under a waiting stage ran out (a cancel resets the stage first)
                if ((m_Stage == PairStage.WaitingForHeadset || m_Stage == PairStage.CodeShown || m_Stage == PairStage.Confirmed) && !st.PairingOpen)
                {
                    m_Stage = PairStage.Expired; m_Code = null;
                    m_PairMessage = "Pairing time ran out. Click Pair Headset to try again.";
                }
                st.Stage = m_Stage; st.PairMessage = m_PairMessage;
                st.Code = m_Stage == PairStage.CodeShown ? m_Code : null;
                if (m_Notice != null && m_Now() > m_NoticeUntil) m_Notice = null;
                st.Notice = m_Notice;
            }

            if (st.Running && m_Host.IdentityRegenerated && st.Notice == null)
                st.Notice = "This PC's identity had to be recreated, so any headset paired before has to be paired again.";
            if (st.Running && st.Notice == null && m_Host.Addresses.Count == 0)
                st.Notice = "No private network found. Connect this PC to your home network or to the Steam Frame dongle.";
            if (st.Running && st.Notice == null && m_Host.BindError != null && !st.ListenerBound)
                st.Notice = "The host could not open its connection: " + m_Host.BindError + ".";
            if (st.Notice == null && Program_CapturePathMissing())
                st.Notice = "A required component (XrssCapture) was not found next to the app, so windows can't be streamed.";

            foreach (var d in m_Host.Store?.List() ?? new List<PairedDevice>())
                st.Devices.Add(new DeviceRow
                {
                    DeviceId = d.DeviceId, ShortId = d.DeviceId.Substring(0, 6), Label = d.Label, PairedUtc = d.PairedUtc, LastConnectedUtc = d.LastConnectedUtc,
                    Connected = a != null && string.Equals(a.DeviceId, d.DeviceId, StringComparison.OrdinalIgnoreCase)
                });

            if (a != null)
            {
                var r = m_Route(a.Local);
                st.HeadsetConnected = true; st.HeadsetLabel = a.Label; st.LocalAddress = a.Local;
                st.Connection = r.Kind;
                st.ConnectionDetail = "Connected via " + a.Local + (r.Adapter != null ? " (" + r.Adapter + ")" : "") + ", headset at " + a.Remote;
            }
            else if (devActive) { st.HeadsetConnected = true; st.HeadsetLabel = "Developer connection"; st.Connection = "Developer (adb)"; st.ConnectionDetail = "Plain loopback connection through adb"; }

            // headline
            if (!st.Running) { st.Headline = "Host stopped"; st.Detail = "Click Start Host to let your headset connect."; st.Tone = Tone.Idle; }
            else if (st.HeadsetConnected) { st.Headline = "Connected"; st.Detail = st.HeadsetLabel + (st.Connection != null ? " via " + st.Connection : ""); st.Tone = Tone.Good; }
            else if (st.Stage == PairStage.CodeShown || st.Stage == PairStage.Confirmed) { st.Headline = "Pairing"; st.Detail = st.Stage == PairStage.Confirmed ? "Waiting for the headset to confirm." : "Compare the code with your headset."; st.Tone = Tone.Warn; }
            else if (st.PairingOpen) { st.Headline = "Pairing"; st.Detail = "Open XR Spatial Screens on your headset and choose Pair with PC."; st.Tone = Tone.Warn; }
            else if (m_LastDisconnect.HasValue && m_Now() - m_LastDisconnect.Value < TimeSpan.FromSeconds(90)) { st.Headline = "Reconnecting"; st.Detail = "The headset disconnected. It reconnects by itself when it can."; st.Tone = Tone.Warn; }
            else if (st.Devices.Count == 0) { st.Headline = "Waiting for headset"; st.Detail = "No headset is paired yet. Click Pair Headset."; st.Tone = Tone.Idle; }
            else { st.Headline = "Waiting for headset"; st.Detail = "Start XR Spatial Screens on your headset. It connects by itself."; st.Tone = Tone.Idle; }
            if (st.Running && st.Notice != null && !st.HeadsetConnected && st.Tone == Tone.Idle) st.Tone = Tone.Warn;
            return st;
        }

        bool Program_CapturePathMissing() => !string.IsNullOrEmpty(CapturePath) && !System.IO.File.Exists(CapturePath);
        /// <summary>Set by the app from the host's capture helper path; null in tests.</summary>
        public string CapturePath { get; set; }

        // ------------------------------------------------------------------ network and power

        IReadOnlyList<IPAddress> m_WatchedAddresses = new IPAddress[0];
        bool m_Watching;

        void WatchNetwork()
        {
            if (m_Watching) return;
            m_Watching = true;
            NetworkChange.NetworkAddressChanged += OnNetworkChanged;
            m_NetTimer = new Timer(_ => { }, null, Timeout.Infinite, Timeout.Infinite);
        }

        void OnNetworkChanged(object s, EventArgs e) => ScheduleNetworkCheck(2000, false);

        /// <summary>Windows woke from sleep: connections are dead, so the listener is rebuilt (stored pairings are untouched).</summary>
        public void OnResume() => ScheduleNetworkCheck(5000, true);

        void ScheduleNetworkCheck(int delayMs, bool force)
        {
            m_NetTimer?.Dispose();
            m_NetTimer = new Timer(_ => RefreshNetwork(force), null, delayMs, Timeout.Infinite);
        }

        /// <summary>Restarts the listener when the set of usable private addresses changed (or after wake-up). A host the user stopped is left alone.</summary>
        public void RefreshNetwork(bool force)
        {
            if (m_UserStopped || !m_Host.IsRunning) return;
            IReadOnlyList<IPAddress> now = m_Host.AddressProvider();
            bool same = now.Count == m_Host.Addresses.Count && now.All(x => m_Host.Addresses.Contains(x));
            if (same && !force) return;
            m_Log.Info(force ? "restarting the listener after wake-up" : "network changed, restarting the listener");
            bool wasPairing = m_Host.Window != null && m_Host.Window.IsOpen;
            m_Host.Stop(); m_Host.Start();
            if (wasPairing) m_Host.OpenPairing();
            Changed?.Invoke();
        }

        // ------------------------------------------------------------------ diagnostics

        public string DiagnosticsText(string version)
        {
            var st = Snapshot();
            var sb = new StringBuilder();
            sb.AppendLine("XR Spatial Screens Host " + version + "  (Gamebreak Labs)");
            sb.AppendLine("Host: " + (st.Running ? "running" : "stopped"));
            sb.AppendLine("Secure listener: " + (st.ListenerBound ? "open on port " + m_Host.Port : "closed (opens while a headset is paired or pairing is open)") + (m_Host.BindError != null ? "; last problem: " + m_Host.BindError : ""));
            sb.AppendLine("Private interfaces: " + (m_Host.Addresses.Count == 0 ? "none" : string.Join(", ", m_Host.Addresses)));
            sb.AppendLine("Pairing: " + (st.PairingOpen ? "open, " + (int)st.PairingRemaining.TotalSeconds + " s left" : "closed") + ", stage " + st.Stage);
            sb.AppendLine("Paired headsets: " + st.Devices.Count);
            sb.AppendLine("Connected headset: " + (st.HeadsetConnected ? st.HeadsetLabel : "none"));
            if (st.ConnectionDetail != null) sb.AppendLine("Connection: " + (st.Connection ?? "unknown route") + ". " + st.ConnectionDetail);
            sb.AppendLine("Host id: " + (m_Host.HostIdShort ?? "-") + "   Certificate: " + (m_Host.FingerprintHex == null ? "-" : m_Host.FingerprintHex.Substring(0, 16) + "..."));
            if (st.Notice != null) sb.AppendLine("Notice: " + st.Notice);
            sb.AppendLine();
            sb.AppendLine("Recent log:");
            foreach (var l in m_Log.Recent(40)) sb.AppendLine("  " + l);
            return sb.ToString();
        }

        public void Dispose()
        {
            NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
            m_NetTimer?.Dispose();
            m_Host.Changed -= OnHostChanged; m_Host.CodeReady -= OnCodeReady; m_Host.Paired -= OnPaired; m_Host.PairingFailed -= OnPairingFailed; m_Host.DeviceRemoved -= OnRemoved;
        }
    }
}
