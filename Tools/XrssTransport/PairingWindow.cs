using System;
using System.Threading;
using System.Threading.Tasks;

namespace XrssTransport
{
    /// <summary>One pairing ceremony in progress on the PC. Created only by <see cref="PairingWindow.TryBegin"/>, so it can exist only while the window is open.</summary>
    public sealed class PairingSession
    {
        readonly PairingWindow m_Window;
        readonly TaskCompletionSource<bool> m_PcDecision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Done;

        internal PairingSession(PairingWindow window, string remote) { m_Window = window; Remote = remote; }

        public string Remote { get; }
        public string DeviceIdHex { get; internal set; }
        /// <summary>The six-digit code for this ceremony, once both nonces are known. Shown to the PC user, who confirms it matches the headset.</summary>
        public string Code { get; internal set; }
        public Task<bool> PcDecision => m_PcDecision.Task;
        internal void SetPcDecision(bool match) => m_PcDecision.TrySetResult(match);
        public void Fail(string why) => m_Window.EndFailed(this, why);
        public void Complete() => m_Window.EndCompleted(this);
        /// <summary>The connection ended without a result. Counts as a failure only if a code had already been shown.</summary>
        public void Abandon() => m_Window.EndAbandoned(this);
    }

    /// <summary>
    /// The explicit enrollment state on the PC. A person opens it (2 minutes by default); only while it is open can a ceremony start. One ceremony at a time. Three failed ceremonies, expiry,
    /// success or an explicit close shut it immediately. Being connected over TLS grants nothing: <see cref="TryBegin"/> is the only way in and returns null when closed.
    /// </summary>
    public sealed class PairingWindow
    {
        public const int DefaultMaxFailures = 3;
        public static readonly TimeSpan DefaultDuration = TimeSpan.FromMinutes(2);

        readonly object m_Lock = new object();
        readonly ITransportLog m_Log;
        readonly Func<DateTime> m_Now;
        readonly int m_MaxFailures;
        DateTime m_Expires;
        bool m_Open;
        int m_Failures;
        PairingSession m_Active;

        public event Action Changed;
        /// <summary>The code is ready for the PC user to compare with the headset.</summary>
        public event Action<PairingSession> CodeReady;

        public PairingWindow(ITransportLog log = null, Func<DateTime> now = null, int maxFailures = DefaultMaxFailures)
        {
            m_Log = log ?? new NullTransportLog(); m_Now = now ?? (() => DateTime.UtcNow); m_MaxFailures = maxFailures;
        }

        public bool IsOpen { get { lock (m_Lock) { Expire(); return m_Open; } } }
        public int Failures { get { lock (m_Lock) return m_Failures; } }
        public DateTime? ExpiresUtc { get { lock (m_Lock) { Expire(); return m_Open ? m_Expires : (DateTime?)null; } } }
        public PairingSession Active { get { lock (m_Lock) { Expire(); return m_Active; } } }

        public void Open(TimeSpan? duration = null)
        {
            lock (m_Lock) { m_Open = true; m_Failures = 0; m_Active = null; m_Expires = m_Now() + (duration ?? DefaultDuration); }
            m_Log.Info("pairing opened for " + (duration ?? DefaultDuration).TotalSeconds.ToString("F0") + " s");
            Changed?.Invoke();
        }

        public void Close(string reason = "closed on the PC") { if (CloseCore(reason)) Changed?.Invoke(); }

        /// <summary>Starts a ceremony for a remote address; null if the window is closed (or expired) or another ceremony is running.</summary>
        public PairingSession TryBegin(string remote)
        {
            lock (m_Lock)
            {
                Expire();
                if (!m_Open) return null;
                if (m_Active != null) return null;
                return m_Active = new PairingSession(this, remote);
            }
        }

        /// <summary>True while this ceremony is still the active one in an open window. Enrollment requires it at the moment of writing.</summary>
        public bool IsCurrent(PairingSession s) { lock (m_Lock) { Expire(); return m_Open && m_Active == s && !s.Done; } }

        /// <summary>Called by the transport when the code is known: tells the PC UI.</summary>
        public void PublishCode(PairingSession s, string deviceIdHex, string code)
        {
            lock (m_Lock) { s.DeviceIdHex = deviceIdHex; s.Code = code; }
            CodeReady?.Invoke(s);
        }

        /// <summary>The PC user's answer to "does the headset show this code?". False if no ceremony is waiting.</summary>
        public bool ConfirmOnPc(bool codesMatch)
        {
            PairingSession s;
            lock (m_Lock) { Expire(); s = m_Active; if (s == null || s.Code == null) return false; }
            s.SetPcDecision(codesMatch);
            return true;
        }

        internal void EndFailed(PairingSession s, string why)
        {
            bool closed = false;
            lock (m_Lock)
            {
                if (s.Done) return;
                s.Done = true; if (m_Active == s) m_Active = null;
                if (!m_Open) return;
                m_Failures++;
                m_Log.Info("pairing refused (" + why + "), failure " + m_Failures + " of " + m_MaxFailures);
                if (m_Failures >= m_MaxFailures) { m_Open = false; closed = true; }
            }
            if (closed) m_Log.Info("pairing closed: too many failures");
            Changed?.Invoke();
        }

        internal void EndCompleted(PairingSession s)
        {
            lock (m_Lock) { if (s.Done) return; s.Done = true; if (m_Active == s) m_Active = null; m_Open = false; }
            m_Log.Info("pairing completed");
            Changed?.Invoke();
        }

        internal void EndAbandoned(PairingSession s)
        {
            bool shown;
            lock (m_Lock) { shown = s.Code != null && !s.Done; if (!shown) { if (!s.Done && m_Active == s) m_Active = null; s.Done = true; } }
            if (shown) EndFailed(s, "connection ended after the code was shown");
        }

        bool CloseCore(string reason)
        {
            lock (m_Lock) { if (!m_Open) return false; m_Open = false; m_Active = null; }
            m_Log.Info("pairing closed: " + reason);
            return true;
        }

        void Expire()
        {
            if (m_Open && m_Now() >= m_Expires) { m_Open = false; m_Active = null; m_Log.Info("pairing expired"); ThreadPool.QueueUserWorkItem(_ => Changed?.Invoke()); }
        }
    }
}
