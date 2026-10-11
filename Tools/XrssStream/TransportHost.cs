using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using XrssTransport;

/// <summary>The authenticated connection currently being served (what the host app shows as "which headset, over which route").</summary>
sealed class ActiveLink
{
    public string DeviceId, Label, Remote, Local;
    public DateTime SinceUtc;
}

/// <summary>
/// The only place where the host's application code meets the authentication layer: it creates the identity, device store, pairing window and secure listener, hands every
/// AUTHENTICATED connection to the unchanged <see cref="Session"/>, and offers the Host Core operations (pairing, confirmation, device removal, start/stop) to BOTH front ends:
/// the console admin commands below and the Windows host app. Capture, input, audio, cursor and the protocol code never reference XrssTransport.
/// Until a connection passes authentication, <see cref="Session"/> does not exist for it. A front end never touches SAS, TLS, HMAC or the device files itself.
/// </summary>
sealed class TransportHost
{
    readonly int m_Port;
    readonly ITransportLog m_Log;
    readonly string m_Dir;
    readonly object m_Gate = new object();
    HostIdentity m_Identity;
    DeviceStore m_Store;
    PairingWindow m_Window;
    SecureListener m_Listener;
    MdnsAdvertiser m_Mdns;
    IReadOnlyList<IPAddress> m_Addresses = new IPAddress[0];
    volatile ActiveLink m_Active;

    public static string Dir => Environment.GetEnvironmentVariable("XRSS_HOST_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XrSpatialScreens", "host");

    /// <summary>Which private addresses the listener may use. Tests replace it (loopback); the product uses the up, private IPv4 interfaces.</summary>
    public Func<IReadOnlyList<IPAddress>> AddressProvider { get; set; } = () => LanInterfaces.PrivateIPv4();
    public bool EnableMdns { get; set; } = true;
    public bool PrintToConsole { get; set; } = true;

    public TransportHost(int port, ITransportLog log = null, string dir = null)
    {
        m_Port = port; m_Dir = dir ?? Dir;
        m_Log = new NoticeLog(this, log ?? new ConsoleTransportLog());
    }

    // ------------------------------------------------------------------ state for front ends

    public bool IsRunning { get; private set; }
    /// <summary>Anything a front end displays may have changed (state, pairing, devices, connection). Raised on arbitrary threads.</summary>
    public event Action Changed;
    public event Action<PairingSession> CodeReady;
    /// <summary>A headset finished pairing; the argument is its label.</summary>
    public event Action<string> Paired;
    public event Action<PairingFailure, string> PairingFailed;
    /// <summary>A paired device was removed (label). Its live connection has already been dropped.</summary>
    public event Action<string> DeviceRemoved;

    public DeviceStore Store => m_Store;
    public PairingWindow Window => m_Window;
    public SecureListener Listener => m_Listener;
    public ActiveLink Active => m_Active;
    public IReadOnlyList<IPAddress> Addresses => m_Addresses;
    public int Port => m_Port;
    public string HostIdShort => m_Identity?.HostIdHex.Substring(0, 8);
    public string FingerprintHex => m_Identity == null ? null : AuthCrypto.Hex(m_Identity.Fingerprint);
    /// <summary>Set when the stored identity could not be read and a new one was made: paired headsets must pair again.</summary>
    public bool IdentityRegenerated { get; private set; }
    public string BindError => m_Listener?.BindError;

    void Raise() { try { Changed?.Invoke(); } catch (Exception) { } }

    // ------------------------------------------------------------------ start / stop

    public void Start()
    {
        lock (m_Gate)
        {
            if (IsRunning) return;
            var protector = new DpapiProtector();
            if (m_Identity == null) m_Identity = HostIdentity.LoadOrCreate(m_Dir, protector, m_Log);
            if (m_Store == null)
            {
                m_Store = new DeviceStore(m_Dir, protector);
                m_Store.Changed += Raise;
                m_Store.Removed += id => { if (m_RemovedLabels.TryRemove(id, out var l)) DeviceRemoved?.Invoke(l); };
            }
            m_Window = new PairingWindow(m_Log);
            m_Window.CodeReady += s => { CodeReady?.Invoke(s); if (PrintToConsole) ShowCode(s); Raise(); };
            m_Window.Changed += Raise;
            m_Window.Failed += (k, why) => PairingFailed?.Invoke(k, why);
            m_Listener = new SecureListener(m_Identity, m_Store, m_Window, m_Log);
            m_Listener.OnAuthenticated = OnAuthenticated;
            m_Addresses = AddressProvider();
            var addresses = m_Addresses;
            // discovery: the PC is announced on the network only while the secure listener is bound (something paired or pairing open)
            m_Listener.ListeningChanged += on =>
            {
                try
                {
                    lock (m_Gate)
                    {
                        m_Mdns?.Stop(); m_Mdns = null;
                        if (on && IsRunning && EnableMdns) { m_Mdns = new MdnsAdvertiser(Environment.MachineName, m_Identity.HostIdHex, m_Listener.Port, addresses, m_Log); m_Mdns.Start(); }
                    }
                }
                catch (Exception e) { m_Log.Info("mdns: " + e.Message); }
                Raise();
            };
            IsRunning = true;
            m_Listener.StartManaged(addresses, m_Port);
            if (PrintToConsole)
            {
                Console.WriteLine($"paired LAN transport ready: TLS 1.2, port {m_Port}, interfaces [{string.Join(", ", addresses)}], {m_Store.Count} paired device(s); the port opens only while a device is paired or pairing is open");
                Console.WriteLine("host id " + m_Identity.HostIdHex.Substring(0, 8) + ", certificate fingerprint " + AuthCrypto.Hex(m_Identity.Fingerprint));
            }
        }
        Raise();
    }

    /// <summary>Stops the secure listener and mDNS, closes the active consumer connection and any open pairing. Stored pairings stay.</summary>
    public void Stop()
    {
        SecureListener l; MdnsAdvertiser m;
        lock (m_Gate)
        {
            if (!IsRunning) return;
            IsRunning = false;
            l = m_Listener; m = m_Mdns; m_Mdns = null;
            m_Window?.Close("host stopped");
        }
        try { m?.Stop(); } catch (Exception) { }
        l?.Dispose();                                   // drops live connections
        m_Active = null;
        m_Log.Info("host stopped");
        Raise();
    }

    readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> m_RemovedLabels = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();

    async Task OnAuthenticated(AuthenticatedConnection c)
    {
        if (!HostSessions.Enter(c.Client, true)) { m_Log.Info("connection from device " + LogIds.Short(c.DeviceIdHex) + " refused: another session is active"); return; }
        string label = c.Label ?? m_Store.List().FirstOrDefault(d => string.Equals(d.DeviceId, c.DeviceIdHex, StringComparison.OrdinalIgnoreCase))?.Label ?? "Headset";
        string local = null;
        try { local = ((IPEndPoint)c.Client.Client.LocalEndPoint).Address.ToString(); } catch (Exception) { }
        var link = new ActiveLink { DeviceId = c.DeviceIdHex, Label = label, Remote = c.RemoteAddress, Local = local, SinceUtc = DateTime.UtcNow };
        m_Active = link;
        if (c.Label != null) Paired?.Invoke(label);                  // Label is set only by an enrollment
        Raise();
        try { await Task.Run(() => RunSession(c)).ConfigureAwait(false); }
        catch (Exception e) { m_Log.Info("lan client ended: " + e.Message); }
        finally { if (m_Active == link) m_Active = null; HostSessions.Leave(); Raise(); }
    }

    /// <summary>What serves an authenticated connection. The application protocol (<see cref="Session"/>) in the product; tests replace it.</summary>
    public Action<AuthenticatedConnection> SessionRunner { get; set; } = c => new Session(c.Client, c.Stream).Run();
    void RunSession(AuthenticatedConnection c) => SessionRunner(c);

    // ------------------------------------------------------------------ Host Core operations used by front ends

    /// <summary>Opens the explicit enrollment window (2 minutes). The host must be running.</summary>
    public bool OpenPairing(TimeSpan? duration = null)
    {
        if (!IsRunning) return false;
        m_Window.Open(duration); return true;
    }

    public void ClosePairing() { m_Window?.Close(); }

    /// <summary>The PC user's answer to "does the headset show the same code?". False if no code is waiting.</summary>
    public bool ConfirmCode(bool same) => m_Window != null && m_Window.ConfirmOnPc(same);

    public bool RemoveDevice(string deviceId)
    {
        var d = m_Store.List().FirstOrDefault(x => string.Equals(x.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
        if (d != null) m_RemovedLabels[d.DeviceId] = d.Label;
        return m_Store.Remove(deviceId);
    }

    public int RemoveAllDevices()
    {
        foreach (var d in m_Store.List()) m_RemovedLabels[d.DeviceId] = d.Label;
        return m_Store.RemoveAll();
    }

    sealed class NoticeLog : ITransportLog
    {
        readonly TransportHost m_Host; readonly ITransportLog m_Inner;
        public NoticeLog(TransportHost h, ITransportLog inner) { m_Host = h; m_Inner = inner; }
        public void Info(string message) { if (message.StartsWith("host identity unreadable")) m_Host.IdentityRegenerated = true; m_Inner.Info(message); }
    }

    void ShowCode(PairingSession s)
    {
        // The code is single use and short lived, but it is still kept out of log files: it is shown only on an interactive console (or when XRSS_SHOW_CODE=1 for tests).
        if (Console.IsOutputRedirected && Environment.GetEnvironmentVariable("XRSS_SHOW_CODE") != "1") { Console.WriteLine("pairing code ready (not shown: output is redirected; use an interactive console)"); return; }
        Console.WriteLine($"PAIRING CODE {s.Code}   (headset must show the same six digits; type 'yes' if it does, 'no' if not)");
    }

    // ------------------------------------------------------------------ admin commands (stdin)

    public void StartAdminConsoleThread() { new Thread(RunAdminConsole) { IsBackground = true, Name = "xrss-admin" }.Start(); }

    public void RunAdminConsole()
    {
        string line;
        try { while ((line = Console.In.ReadLine()) != null) { string r = Execute(line.Trim()); if (r != null) Console.WriteLine(r); } }
        catch (Exception) { }
        if (!Console.IsInputRedirected) return;
        Thread.Sleep(Timeout.Infinite);          // no console attached (supervised run): keep serving
    }

    public string Execute(string line)
    {
        var p = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (p.Length == 0) return null;
        switch (p[0].ToLowerInvariant())
        {
            case "pair": { int sec = p.Length > 1 ? int.Parse(p[1]) : 120; return OpenPairing(TimeSpan.FromSeconds(sec)) ? "pairing window open for " + sec + " s" : "the secure listener is not running"; }
            case "pair-close": ClosePairing(); return "pairing closed";
            case "yes": return ConfirmCode(true) ? "confirmed" : "no pairing code is waiting";
            case "no": return ConfirmCode(false) ? "rejected" : "no pairing code is waiting";
            case "devices": return string.Join("\n", m_Store.List().Select(d => $"{d.DeviceId}  {d.Label}  paired {d.PairedUtc:u}  last {(d.LastConnectedUtc?.ToString("u") ?? "never")}").DefaultIfEmpty("no paired devices"));
            case "remove": return p.Length > 1 ? RemoveByPrefix(m_Store, p[1], this) : "usage: remove <device id or its first characters>";
            case "remove-all": return "removed " + RemoveAllDevices() + " device(s)";
            case "status": return $"pairing {(m_Window.IsOpen ? "open until " + m_Window.ExpiresUtc : "closed")}, {m_Store.Count} paired, listening: {m_Listener.IsListening}, live: {m_Listener.LiveConnections}";
            default: return "commands: pair [seconds], pair-close, yes, no, devices, remove <id>, remove-all, status";
        }
    }

    static string RemoveByPrefix(DeviceStore store, string prefix, TransportHost host = null)
    {
        var hits = store.List().Where(d => d.DeviceId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
        if (hits.Count != 1) return hits.Count == 0 ? "no such device" : "ambiguous: " + hits.Count + " devices match";
        if (host != null) host.RemoveDevice(hits[0].DeviceId); else store.Remove(hits[0].DeviceId);
        return "removed " + hits[0].Label;
    }

    /// <summary>Commands that work on the files while the host is NOT running: --devices, --remove-device ID, --remove-all-devices, --regenerate-identity. Returns true if one ran.</summary>
    public static bool RunOfflineCommand(string[] argv)
    {
        if (argv.Contains("--devices") || argv.Contains("--remove-all-devices") || argv.Contains("--remove-device") || argv.Contains("--regenerate-identity"))
        {
            if (System.Diagnostics.Process.GetProcessesByName("XrssStream").Length > 1) Console.WriteLine("warning: a host is running; use its console commands instead, offline edits are not seen by it");
            var protector = new DpapiProtector();
            var store = new DeviceStore(Dir, protector);
            if (argv.Contains("--devices")) Console.WriteLine(string.Join("\n", store.List().Select(d => $"{d.DeviceId}  {d.Label}  paired {d.PairedUtc:u}").DefaultIfEmpty("no paired devices")));
            if (argv.Contains("--remove-all-devices")) Console.WriteLine("removed " + store.RemoveAll() + " device(s)");
            int i = Array.IndexOf(argv, "--remove-device"); if (i >= 0 && i + 1 < argv.Length) Console.WriteLine(RemoveByPrefix(store, argv[i + 1]));
            if (argv.Contains("--regenerate-identity")) { using (var id = HostIdentity.LoadOrCreate(Dir, protector)) { id.Regenerate(); Console.WriteLine("new identity " + id.HostIdHex.Substring(0, 8) + "; every paired device must pair again"); } store.RemoveAll(); }
            return true;
        }
        return false;
    }
}

/// <summary>One headset session at a time across both transports. A new paired-LAN connection replaces an older paired-LAN one (a headset that dropped off Wi-Fi reconnects at once); a plain dev connection is never kicked.</summary>
static class HostSessions
{
    static readonly object Gate = new object();
    static TcpClient s_Current; static bool s_CurrentIsLan, s_Killed;
    /// <summary>True while a plain developer (loopback/adb) connection is being served.</summary>
    public static bool DevActive { get { lock (Gate) return s_Current != null && !s_CurrentIsLan; } }
    public static event Action Changed;

    public static bool Enter(TcpClient client, bool lan)
    {
        lock (Gate)
        {
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (s_Current != null)
            {
                if (!(lan && s_CurrentIsLan)) return false;
                if (!s_Killed) { s_Killed = true; try { s_Current.Dispose(); } catch { } }
                if (!Monitor.Wait(Gate, 200) && DateTime.UtcNow > deadline) return false;
            }
            s_Current = client; s_CurrentIsLan = lan; s_Killed = false;
        }
        if (!lan) Changed?.Invoke();
        return true;
    }

    public static void Leave() { bool wasDev; lock (Gate) { wasDev = !s_CurrentIsLan; s_Current = null; s_Killed = false; Monitor.PulseAll(Gate); } if (wasDev) Changed?.Invoke(); }
}
