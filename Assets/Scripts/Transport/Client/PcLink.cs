using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using static XrssTransport.AuthProtocol;

namespace XrssTransport.Client
{
    public enum LinkMode { Dev, PairedLan }
    public enum LinkState { Dev, NotPaired, Searching, Connecting, Pairing, Connected, Reconnecting, Error }
    public enum PairStep { None, Connecting, ShowCode, WaitingForPc, Complete }
    public enum LinkError { None, PcNotFound, WrongPc, PairingClosed, CodeRejected, ConnectionLost, Removed, UnsupportedVersion, RateLimited, Busy, Timeout, Other }

    /// <summary>What the UI shows. Immutable; a new one replaces it on every change (<see cref="Version"/> increases).</summary>
    public sealed class LinkSnapshot
    {
        public int Version;
        public LinkMode Mode;
        public LinkState State;
        public PairStep Step;
        public LinkError Error;
        public string Text = "";                 // one plain sentence for customers, no crypto words
        public string Code;                      // the six digits while Step == ShowCode / WaitingForPc
        public string PcName;                    // the paired PC, if any
        public string ConnectedAddress;
        public bool Searching;
        public IReadOnlyList<DiscoveredPc> Found = new List<DiscoveredPc>();
        public string ManualAddress = "";
        public bool Paired;
    }

    public sealed class LinkTimings
    {
        public long DiscoveryMs, TcpMs, TlsMs, AuthMs, TotalMs;
        public string Via = "";
        public override string ToString() => $"via {Via}: discovery {DiscoveryMs} ms, tcp {TcpMs} ms, tls {TlsMs} ms, auth {AuthMs} ms, total {TotalMs} ms";
    }

    /// <summary>
    /// The connection supervisor between the app and the PC host. The application protocol (RemoteHost) only ever asks <see cref="TryConnect"/> for a Stream and reads/writes it; whether that
    /// stream is plain TCP over adb (<see cref="LinkMode.Dev"/>, exactly the old behaviour) or an authenticated TLS stream (<see cref="LinkMode.PairedLan"/>) is decided here and nowhere else.
    /// No panel, capture, input, audio, cursor or layout code knows about certificates, device secrets, fingerprints, codes or challenges.
    ///
    /// Paired reconnect order: last known address, then mDNS for the matching host id, then the typed address. Trust follows the saved certificate fingerprint and host id, never the address.
    /// Retry: immediately after an unexpected drop, then 0.5, 1, 2, 3, 5 s, then every 10 s. A PC that says it no longer knows this headset is NOT retried: the user is asked to pair again.
    /// </summary>
    public sealed class PcLink
    {
        public const int LanPort = 5610;
        /// <summary>Where the plain developer connection goes (adb reverse to the PC host). Settable for tests.</summary>
        public string DevAddress = "127.0.0.1";
        public int DevPort = 5600;
        const int ConnectTimeoutMs = 3000, StepTimeoutMs = 6000, DiscoveryMs = 4000, PairWaitForUserMs = 150000, PairWaitForPcMs = 130000;
        static readonly int[] Backoff = { 0, 500, 1000, 2000, 3000, 5000, 5000, 10000 };

        readonly IClientEnv m_Env;
        readonly IDiscovery m_Discovery;
        readonly object m_Lock = new object();
        readonly HeadsetIdentity m_Id;
        readonly PairedHostStore m_Store;
        LinkSnapshot m_Snap;
        int m_Version;
        LinkMode m_Mode;
        string m_Manual = "";
        int m_Attempt;
        bool m_WasConnected, m_EverConnected;
        DateTime m_BlockedUntil = DateTime.MinValue;
        IDisposable m_Live;                                       // the stream RemoteHost is using (so Forget/SetMode can drop it)
        (System.IO.Stream stream, IDisposable owner)? m_Ready;    // an already authenticated connection (the pairing one)
        volatile bool m_PairActive, m_CancelRequested;
        List<string> m_PairAddresses;
        SecureConnection m_PairConn;
        readonly ManualResetEventSlim m_UserAnswered = new ManualResetEventSlim(false);
        bool m_UserMatch;
        Thread m_SearchThread;

        /// <summary>How long a TCP connect to an address takes (ms, -1 unreachable). Replaceable in tests.</summary>
        public AddressProbe.ConnectMs m_Probe = AddressProbe.TcpConnectMs;
        public int RetryDelayMs { get; private set; } = 1000;
        public LinkTimings Timings { get; private set; } = new LinkTimings();
        public HeadsetIdentity Identity => m_Id;
        public PairedHostStore Store => m_Store;

        public PcLink(IClientEnv env, IDiscovery discovery = null, LinkMode? defaultMode = null)
        {
            m_Env = env;
            m_Discovery = discovery ?? new MdnsDiscovery(env);
            m_Id = HeadsetIdentity.LoadOrCreate(env.SecretDir);
            m_Store = new PairedHostStore(env.SecretDir);
            LoadSettings(defaultMode ?? LinkMode.Dev);
            Publish(m_Mode == LinkMode.Dev ? LinkState.Dev : m_Store.Count > 0 ? LinkState.Connecting : LinkState.NotPaired, PairStep.None, LinkError.None, null);
        }

        public LinkMode Mode { get { lock (m_Lock) return m_Mode; } }
        public LinkSnapshot Snapshot { get { lock (m_Lock) return m_Snap; } }

        // ------------------------------------------------------------------ settings

        string SettingsPath => Path.Combine(m_Env.SettingsDir, "link.txt");

        void LoadSettings(LinkMode def)
        {
            m_Mode = def;
            try
            {
                Directory.CreateDirectory(m_Env.SettingsDir);
                if (File.Exists(SettingsPath))
                    foreach (var line in File.ReadAllLines(SettingsPath))
                    {
                        int eq = line.IndexOf('='); if (eq <= 0) continue;
                        string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim();
                        if (k == "mode") m_Mode = v == "paired" ? LinkMode.PairedLan : LinkMode.Dev;
                        else if (k == "manual") m_Manual = v;
                    }
            }
            catch (Exception) { }
        }

        void SaveSettings()
        {
            try { Directory.CreateDirectory(m_Env.SettingsDir); File.WriteAllText(SettingsPath, "mode=" + (m_Mode == LinkMode.PairedLan ? "paired" : "dev") + "\nmanual=" + m_Manual + "\n"); } catch (Exception) { }
        }

        public void SetMode(LinkMode mode)
        {
            lock (m_Lock) { if (m_Mode == mode) return; m_Mode = mode; SaveSettings(); m_Attempt = 0; m_BlockedUntil = DateTime.MinValue; }
            DropLive();
            Publish(mode == LinkMode.Dev ? LinkState.Dev : m_Store.Count > 0 ? LinkState.Connecting : LinkState.NotPaired, PairStep.None, LinkError.None, null);
        }

        public void SetManualAddress(string address)
        {
            lock (m_Lock) { m_Manual = (address ?? "").Trim(); SaveSettings(); m_Attempt = 0; m_BlockedUntil = DateTime.MinValue; }
            Publish(Snapshot.State, Snapshot.Step, Snapshot.Error, Snapshot.Text);
        }

        // ------------------------------------------------------------------ connecting (used by RemoteHost)

        /// <summary>Returns a stream carrying the application protocol. Dev: plain TCP to 127.0.0.1:5600 (throws when the host is not there, as before). Paired: an authenticated TLS stream, or false with
        /// <see cref="RetryDelayMs"/> telling the caller how long to wait; the reason is in <see cref="Snapshot"/>.</summary>
        public bool TryConnect(out System.IO.Stream stream, out IDisposable owner)
        {
            stream = null; owner = null;
            if (Mode == LinkMode.Dev)
            {
                var c = new TcpClient { NoDelay = true };
                try { c.Connect(DevAddress, DevPort); } catch { c.Close(); throw; }
                stream = c.GetStream(); owner = c; RetryDelayMs = 1000;
                lock (m_Lock) m_Live = c;
                Publish(LinkState.Dev, PairStep.None, LinkError.None, "Dev connection (adb)");
                return true;
            }

            lock (m_Lock) { if (m_Ready.HasValue) { (stream, owner) = m_Ready.Value; m_Ready = null; } }
            if (stream != null) { MarkConnected(null, "pairing"); lock (m_Lock) m_Live = owner; return true; }

            if (m_PairActive) { RetryDelayMs = 250; return false; }
            var host = m_Store.Primary;
            if (host == null) { if (Snapshot.State != LinkState.Error) Publish(LinkState.NotPaired, PairStep.None, LinkError.None, "No PC paired. Open pairing on your PC, then choose Pair with PC."); RetryDelayMs = 500; return false; }
            if (host.RevokedSeenUtc.HasValue) { Publish(LinkState.Error, PairStep.None, LinkError.Removed, "This PC no longer trusts this headset. Pair again."); RetryDelayMs = 1000; return false; }
            if (DateTime.UtcNow < m_BlockedUntil) { RetryDelayMs = 1000; return false; }

            Publish(m_WasConnected || m_Attempt > 0 ? LinkState.Reconnecting : LinkState.Connecting, PairStep.None, LinkError.None, m_WasConnected ? "Connection lost. Reconnecting..." : "Connecting to " + Name(host) + "...");
            var total = System.Diagnostics.Stopwatch.StartNew();
            var r = AttemptPaired(host);
            if (r.conn != null)
            {
                r.timings.TotalMs = total.ElapsedMilliseconds;
                Timings = r.timings;
                m_Env.Log("[link] connected " + r.timings);
                m_Store.Update(host.HostIdHex, h => { h.LastAddress = r.conn.Address; h.LastPort = r.conn.Port; h.LastConnectedUtc = DateTime.UtcNow; foreach (var ad in r.seen) if (!h.Addresses.Contains(ad)) h.Addresses.Add(ad); if (!h.Addresses.Contains(r.conn.Address)) h.Addresses.Add(r.conn.Address); });
                stream = r.conn.Ssl; owner = r.conn;
                LearnAddresses(host.HostIdHex);
                MarkConnected(r.conn.Address, r.timings.Via);
                lock (m_Lock) m_Live = owner;
                return true;
            }

            if (r.revoked)
            {
                m_Store.Update(host.HostIdHex, h => h.RevokedSeenUtc = DateTime.UtcNow);
                Publish(LinkState.Error, PairStep.None, LinkError.Removed, "This PC no longer trusts this headset. Pair again.");
                RetryDelayMs = 1000; m_Env.Log("[link] the PC no longer knows this headset");
                return false;
            }
            if (r.unsupported) { m_BlockedUntil = DateTime.UtcNow.AddSeconds(30); Publish(LinkState.Error, PairStep.None, LinkError.UnsupportedVersion, "The PC software is a different version. Update XR Spatial Screens on both."); RetryDelayMs = 1000; return false; }
            if (r.rateLimited) { m_BlockedUntil = DateTime.UtcNow.AddSeconds(60); Publish(LinkState.Error, PairStep.None, LinkError.RateLimited, "The PC is asking us to wait a moment."); RetryDelayMs = 1000; return false; }
            if (r.wrongPc) Publish(LinkState.Error, PairStep.None, LinkError.WrongPc, "Wrong PC, or its identity changed. Pair again if you reinstalled the PC software.");
            else Publish(m_WasConnected ? LinkState.Reconnecting : LinkState.Error, PairStep.None, LinkError.PcNotFound, m_WasConnected ? "Connection lost. Looking for your PC..." : "PC not found. Is XR Spatial Screens running on it? If this headset was removed on the PC, pair again.");
            RetryDelayMs = Backoff[Math.Min(m_Attempt, Backoff.Length - 1)]; m_Attempt++;
            return false;
        }

        /// <summary>RemoteHost calls this when an established connection ended: retry at once, then back off.</summary>
        public void NotifyDisconnected()
        {
            lock (m_Lock) { m_Live = null; }
            if (Mode == LinkMode.Dev) { RetryDelayMs = 1000; return; }
            m_Attempt = 0; RetryDelayMs = 0;
            Publish(LinkState.Reconnecting, PairStep.None, LinkError.ConnectionLost, "Connection lost. Reconnecting...");
        }

        void MarkConnected(string address, string via)
        {
            m_Attempt = 0; m_WasConnected = true; m_EverConnected = true; RetryDelayMs = 0;
            var h = m_Store.Primary;
            Publish(LinkState.Connected, PairStep.None, LinkError.None, "Connected to " + (h != null ? Name(h) : "your PC"), address);
        }

        /// <summary>After a connection works, look around once (on a worker thread) for the PC's other addresses and remember them, so the next reconnect can pick the fastest path.</summary>
        void LearnAddresses(string hostIdHex)
        {
            new Thread(() =>
            {
                try
                {
                    var found = m_Discovery.Discover(3000, null);
                    foreach (var pc in found.Where(p => string.Equals(p.HostIdHex, hostIdHex, StringComparison.OrdinalIgnoreCase)))
                        m_Store.Update(hostIdHex, h => { foreach (var ad in pc.Addresses.Select(x => x.ToString())) if (!h.Addresses.Contains(ad)) h.Addresses.Add(ad); });
                }
                catch (Exception) { }
            }) { IsBackground = true, Name = "PcLinkLearn" }.Start();
        }

        static string Name(PairedHost h) => string.IsNullOrEmpty(h.DisplayName) ? "your PC" : h.DisplayName;

        sealed class Attempt { public List<string> seen = new List<string>(); public SecureConnection conn; public LinkTimings timings = new LinkTimings(); public bool revoked, unsupported, rateLimited, wrongPc; }

        Attempt AttemptPaired(PairedHost host)
        {
            var a = new Attempt();
            var tried = new HashSet<string>();
            byte[] pin = host.Fingerprint;

            // returns true to stop (connected or a definite answer from the verified PC)
            bool TryEndpoint(string address, int port, string via)
            {
                if (string.IsNullOrEmpty(address) || port <= 0 || !tried.Add(address + ":" + port)) return false;
                SecureConnection c;
                try { c = SecureChannel.Connect(address, port, pin, ConnectTimeoutMs); }
                catch (PinMismatchException) { a.wrongPc = true; m_Env.Log("[link] a different PC answered at " + address); return false; }
                catch (Exception e) { m_Env.Log("[link] " + via + " " + address + ": " + e.GetType().Name); return false; }
                var o = HostAuthClient.Authenticate(c, host, StepTimeoutMs);
                if (o.Status == AuthStatus.Ok) { a.conn = c; a.timings.TcpMs = c.TcpMs; a.timings.TlsMs = c.TlsMs; a.timings.AuthMs = o.Ms; a.timings.Via = via; return true; }
                c.Dispose();
                m_Env.Log("[link] authentication at " + address + ": " + o);
                if (o.Status == AuthStatus.HostMismatch) { a.wrongPc = true; return false; }
                if (o.Status == AuthStatus.Rejected)
                {
                    if (o.Reject == RejectCode.AuthFailed) { a.revoked = true; return true; }                  // identity verified by the pin and host id: the PC dropped us
                    if (o.Reject == RejectCode.UnsupportedVersion) { a.unsupported = true; return true; }
                    if (o.Reject == RejectCode.RateLimited) { a.rateLimited = true; return true; }
                }
                return false;
            }

            int port0 = host.LastPort > 0 ? host.LastPort : LanPort;
            var known = new List<string>(host.Addresses); if (!string.IsNullOrEmpty(host.LastAddress) && !known.Contains(host.LastAddress)) known.Insert(0, host.LastAddress);
            var ranked = AddressProbe.Rank(known, port0, 1500, 150, m_Probe);
            if (ranked.Count > 0) m_Env.Log("[link] saved addresses by speed: " + string.Join(", ", ranked.Select(r => r.address + " " + r.ms + " ms")));
            foreach (var r in ranked) if (TryEndpoint(r.address, port0, "saved address")) return a;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var found = m_Discovery.Discover(DiscoveryMs, list => list.Any(p => string.Equals(p.HostIdHex, host.HostIdHex, StringComparison.OrdinalIgnoreCase)));
            a.timings.DiscoveryMs = sw.ElapsedMilliseconds;
            foreach (var pc in found.Where(p => string.Equals(p.HostIdHex, host.HostIdHex, StringComparison.OrdinalIgnoreCase)))
            {
                var fast = AddressProbe.Rank(pc.Addresses.Select(x => x.ToString()), pc.Port, 1500, 150, m_Probe);
                m_Env.Log("[link] discovered addresses by speed: " + string.Join(", ", fast.Select(r => r.address + " " + r.ms + " ms")));
                foreach (var r in fast) a.seen.Add(r.address);
                foreach (var r in fast) if (TryEndpoint(r.address, pc.Port, "discovery")) return a;
            }

            string manual; lock (m_Lock) manual = m_Manual;
            if (manual.Length > 0) { ParseAddress(manual, out string ma, out int mp); if (TryEndpoint(ma, mp, "typed address")) return a; }
            return a;
        }

        static bool SameSubnet(string a, string b)
        {
            if (a == null || b == null) return false;
            int i = a.LastIndexOf('.'), j = b.LastIndexOf('.');
            return i > 0 && j > 0 && string.Compare(a, 0, b, 0, Math.Min(i, j) + 1, StringComparison.Ordinal) == 0 && i == j;
        }

        public static void ParseAddress(string typed, out string address, out int port)
        {
            typed = (typed ?? "").Trim(); port = LanPort; address = typed;
            int colon = typed.LastIndexOf(':');
            if (colon > 0 && typed.IndexOf(':') == colon && int.TryParse(typed.Substring(colon + 1), out int p) && p > 0 && p < 65536) { address = typed.Substring(0, colon); port = p; }
        }

        // ------------------------------------------------------------------ search (for the pairing screen)

        /// <summary>Looks for PCs for a few seconds on a worker thread; results appear in <see cref="Snapshot"/>.Found as they arrive.</summary>
        public void StartSearch(int timeoutMs = 6000)
        {
            lock (m_Lock) { if (m_SearchThread != null && m_SearchThread.IsAlive) return; }
            Mutate(s => s.Searching = true);
            var t = new Thread(() =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                List<DiscoveredPc> last = null;
                m_Discovery.Discover(timeoutMs, list => { last = list; Mutate(s => s.Found = list); return false; });
                m_Env.Log("[link] search found " + (last?.Count ?? 0) + " PC(s) in " + sw.ElapsedMilliseconds + " ms");
                Mutate(s => s.Searching = false);
            }) { IsBackground = true, Name = "PcLinkSearch" };
            lock (m_Lock) m_SearchThread = t;
            t.Start();
        }

        // ------------------------------------------------------------------ pairing

        public void BeginPairing(DiscoveredPc pc) => StartPairing(pc.Addresses.Select(a => (a.ToString(), pc.Port)).ToList(), pc.DisplayName);

        public void BeginPairingAt(string typedAddress)
        {
            ParseAddress(typedAddress, out string a, out int p);
            StartPairing(new List<(string, int)> { (a, p) }, null);
        }

        void StartPairing(List<(string address, int port)> candidates, string displayName)
        {
            if (m_PairActive || candidates.Count == 0) return;
            m_PairAddresses = candidates.Select(c => c.address).Distinct().ToList();
            m_PairActive = true; m_CancelRequested = false; m_UserAnswered.Reset(); m_UserMatch = false;
            DropLive();
            Publish(LinkState.Pairing, PairStep.Connecting, LinkError.None, "Connecting to the PC...");
            new Thread(() => RunPairing(candidates, displayName)) { IsBackground = true, Name = "PcLinkPairing" }.Start();
        }

        void RunPairing(List<(string address, int port)> candidates, string displayName)
        {
            SecureConnection conn = null;
            bool success = false;
            try
            {
                {
                    var rank = AddressProbe.Rank(candidates.Select(c => c.address), candidates[0].port, 1500, 150, m_Probe);
                    if (rank.Count > 0) m_Env.Log("[link] PC addresses by speed: " + string.Join(", ", rank.Select(r => r.address + " " + r.ms + " ms")));
                    var order = rank.Select(r => candidates.First(c => c.address == r.address)).Concat(candidates.Where(c => rank.All(r => r.address != c.address))).ToList();
                    candidates = order;
                }
                foreach (var (address, port) in candidates)
                {
                    try { conn = SecureChannel.Connect(address, port, null, 4000); break; }
                    catch (Exception e) { m_Env.Log("[link] pairing connect " + address + ": " + e.GetType().Name); }
                }
                if (conn == null) { Publish(LinkState.Error, PairStep.None, LinkError.PcNotFound, "PC not found. Is XR Spatial Screens running on it?"); return; }
                lock (m_Lock) m_PairConn = conn;

                var ceremony = new PairingCeremony(conn, m_Id);
                var st = ceremony.Begin(StepTimeoutMs);
                if (st != PairStatus.CodeReady) { PairingError(st, ceremony.Reject); return; }

                Publish(LinkState.Pairing, PairStep.ShowCode, LinkError.None, "Compare this code with the one on your PC.", null, ceremony.Code);
                if (!m_UserAnswered.Wait(PairWaitForUserMs)) { ceremony.Finish(false, null, 0); Publish(LinkState.Error, PairStep.None, LinkError.Timeout, "Pairing timed out."); return; }
                if (!m_UserMatch) { ceremony.Finish(false, null, 0); Publish(m_Store.Count > 0 ? LinkState.Reconnecting : LinkState.NotPaired, PairStep.None, LinkError.CodeRejected, "Pairing cancelled. Nothing was saved."); return; }

                Publish(LinkState.Pairing, PairStep.WaitingForPc, LinkError.None, "Now confirm the same code on your PC.", null, ceremony.Code);
                if (!ceremony.Finish(true, m_Id.Label, PairWaitForPcMs)) { PairingError(PairStatus.Rejected, ceremony.Reject == RejectCode.PairingFailed ? RejectCode.PairingFailed : ceremony.Reject); return; }

                // the PC enrolled us: only now is anything saved
                string name = displayName;
                var host = new PairedHost
                {
                    HostIdHex = AuthCrypto.Hex(ceremony.HostId), FingerprintHex = AuthCrypto.Hex(conn.PresentedFingerprint), DeviceIdHex = m_Id.DeviceIdHex, Secret = ceremony.Secret,
                    LastAddress = conn.Address, LastPort = conn.Port, Addresses = new List<string>(m_PairAddresses ?? new List<string>()), DisplayName = string.IsNullOrEmpty(name) ? "your PC" : name, PairedUtc = DateTime.UtcNow, LastConnectedUtc = DateTime.UtcNow,
                };
                m_Store.Save(host);
                Array.Clear(ceremony.Secret, 0, ceremony.Secret.Length);
                success = true;
                lock (m_Lock) { m_Ready = (conn.Ssl, conn); m_PairConn = null; }
                m_Env.Log("[link] paired with " + host.DisplayName + " (host " + host.HostIdHex.Substring(0, 8) + ")");
                Publish(LinkState.Pairing, PairStep.Complete, LinkError.None, "Paired with " + host.DisplayName + ".", conn.Address);
            }
            catch (Exception e) { m_Env.Log("[link] pairing failed: " + e.GetType().Name); Publish(LinkState.Error, PairStep.None, LinkError.Other, "Pairing failed. Try again."); }
            finally
            {
                if (!success) { try { conn?.Dispose(); } catch { } lock (m_Lock) m_PairConn = null; }
                if (m_CancelRequested && !success) Publish(m_Store.Count > 0 ? LinkState.Reconnecting : LinkState.NotPaired, PairStep.None, LinkError.None, "Pairing cancelled. Nothing was saved.");
                m_PairActive = false;
            }
        }

        void PairingError(PairStatus st, RejectCode reject)
        {
            if (st == PairStatus.Timeout) { Publish(LinkState.Error, PairStep.None, LinkError.Timeout, "The PC did not answer."); return; }
            if (st == PairStatus.Rejected)
                switch (reject)
                {
                    case RejectCode.PairingClosed: Publish(LinkState.Error, PairStep.None, LinkError.PairingClosed, "The PC is not accepting new headsets. Open pairing on the PC, then try again."); return;
                    case RejectCode.Busy: Publish(LinkState.Error, PairStep.None, LinkError.Busy, "The PC is pairing another device."); return;
                    case RejectCode.RateLimited: Publish(LinkState.Error, PairStep.None, LinkError.RateLimited, "Too many tries. Wait a minute."); return;
                    case RejectCode.UnsupportedVersion: Publish(LinkState.Error, PairStep.None, LinkError.UnsupportedVersion, "The PC software is a different version."); return;
                    case RejectCode.PairingFailed: Publish(LinkState.Error, PairStep.None, LinkError.CodeRejected, "The code was not confirmed on the PC. Nothing was saved."); return;
                    case RejectCode.Timeout: Publish(LinkState.Error, PairStep.None, LinkError.Timeout, "The PC did not confirm in time."); return;
                }
            Publish(LinkState.Error, PairStep.None, LinkError.Other, "Pairing failed. Try again.");
        }

        /// <summary>The user compared the codes: true = they match ("Same code"), false = they do not. Never called automatically.</summary>
        public void AnswerCode(bool match)
        {
            if (Snapshot.Step != PairStep.ShowCode) return;
            m_UserMatch = match; m_UserAnswered.Set();
        }

        /// <summary>Abandons pairing at any step. Nothing is saved.</summary>
        public void CancelPairing()
        {
            if (!m_PairActive) return;
            if (Snapshot.Step == PairStep.ShowCode) { AnswerCode(false); return; }
            SecureConnection c; lock (m_Lock) c = m_PairConn;
            m_CancelRequested = true;
            try { c?.Dispose(); } catch { }                                                    // unblocks the worker; it ends with an error and saves nothing
            Publish(m_Store.Count > 0 ? LinkState.Reconnecting : LinkState.NotPaired, PairStep.None, LinkError.None, "Pairing cancelled. Nothing was saved.");
        }

        /// <summary>Forget the paired PC: its record (including the secret) is deleted and the live connection dropped. This is the explicit remove-trust action.</summary>
        public void Forget()
        {
            var h = m_Store.Primary;
            if (h != null) m_Store.Remove(h.HostIdHex);
            lock (m_Lock) { m_Attempt = 0; m_BlockedUntil = DateTime.MinValue; m_Ready = null; m_WasConnected = false; }
            DropLive();
            Publish(Mode == LinkMode.Dev ? LinkState.Dev : LinkState.NotPaired, PairStep.None, LinkError.None, "Forgot the PC. Pair again to use it.");
        }

        /// <summary>Try again now (clears waiting periods; does not clear a "PC removed this headset" state, which needs pairing again).</summary>
        public void RetryNow() { lock (m_Lock) { m_Attempt = 0; m_BlockedUntil = DateTime.MinValue; } RetryDelayMs = 0; }

        void DropLive() { IDisposable l; lock (m_Lock) { l = m_Live; m_Live = null; } try { l?.Dispose(); } catch { } }

        // ------------------------------------------------------------------ snapshot plumbing

        void Publish(LinkState state, PairStep step, LinkError error, string text, string address = null, string code = null)
        {
            Mutate(s =>
            {
                s.Mode = m_Mode; s.State = state; s.Step = step; s.Error = error; if (text != null) s.Text = text; s.Code = code;
                if (address != null) s.ConnectedAddress = address; if (state != LinkState.Connected && state != LinkState.Dev) s.ConnectedAddress = null;
            });
        }

        void Mutate(Action<LinkSnapshot> change)
        {
            lock (m_Lock)
            {
                var o = m_Snap;
                var n = new LinkSnapshot();
                if (o != null) { n.Mode = o.Mode; n.State = o.State; n.Step = o.Step; n.Error = o.Error; n.Text = o.Text; n.Code = o.Code; n.ConnectedAddress = o.ConnectedAddress; n.Searching = o.Searching; n.Found = o.Found; }
                change(n);
                var h = m_Store?.Primary;
                n.PcName = h?.DisplayName; n.Paired = h != null; n.ManualAddress = m_Manual; n.Mode = m_Mode; n.Version = ++m_Version;
                m_Snap = n;
            }
        }
    }
}
