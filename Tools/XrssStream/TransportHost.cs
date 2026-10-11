using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using XrssTransport;

/// <summary>
/// The only place where the host's application code meets the authentication layer: it creates the identity, device store, pairing window and secure listener, hands every
/// AUTHENTICATED connection to the unchanged <see cref="Session"/>, and offers admin commands. Capture, input, audio, cursor and the protocol code never reference XrssTransport.
/// Until a connection passes authentication, <see cref="Session"/> does not exist for it.
/// </summary>
sealed class TransportHost
{
    readonly int m_Port;
    readonly ITransportLog m_Log = new ConsoleTransportLog();
    HostIdentity m_Identity;
    DeviceStore m_Store;
    PairingWindow m_Window;
    SecureListener m_Listener;

    public static string Dir => Environment.GetEnvironmentVariable("XRSS_HOST_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XrSpatialScreens", "host");

    public TransportHost(int port) { m_Port = port; }

    public void Start()
    {
        var protector = new DpapiProtector();
        m_Identity = HostIdentity.LoadOrCreate(Dir, protector, m_Log);
        m_Store = new DeviceStore(Dir, protector);
        m_Window = new PairingWindow(m_Log);
        m_Listener = new SecureListener(m_Identity, m_Store, m_Window, m_Log);
        m_Window.CodeReady += s => ShowCode(s);
        m_Listener.OnAuthenticated = async c =>
        {
            if (!HostSessions.Enter(c.Client, true)) { m_Log.Info("connection from device " + LogIds.Short(c.DeviceIdHex) + " refused: another session is active"); return; }
            try { await Task.Run(() => new Session(c.Client, c.Stream).Run()).ConfigureAwait(false); }
            catch (Exception e) { Console.WriteLine("lan client ended: " + e.Message); }
            finally { HostSessions.Leave(); }
        };
        var addresses = LanInterfaces.PrivateIPv4();
        m_Listener.StartManaged(addresses, m_Port);
        Console.WriteLine($"paired LAN transport ready: TLS 1.2, port {m_Port}, interfaces [{string.Join(", ", addresses)}], {m_Store.Count} paired device(s); the port opens only while a device is paired or pairing is open");
        Console.WriteLine("host id " + m_Identity.HostIdHex.Substring(0, 8) + ", certificate fingerprint " + AuthCrypto.Hex(m_Identity.Fingerprint));
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
            case "pair": { int sec = p.Length > 1 ? int.Parse(p[1]) : 120; m_Window.Open(TimeSpan.FromSeconds(sec)); return "pairing window open for " + sec + " s"; }
            case "pair-close": m_Window.Close(); return "pairing closed";
            case "yes": return m_Window.ConfirmOnPc(true) ? "confirmed" : "no pairing code is waiting";
            case "no": return m_Window.ConfirmOnPc(false) ? "rejected" : "no pairing code is waiting";
            case "devices": return string.Join("\n", m_Store.List().Select(d => $"{d.DeviceId}  {d.Label}  paired {d.PairedUtc:u}  last {(d.LastConnectedUtc?.ToString("u") ?? "never")}").DefaultIfEmpty("no paired devices"));
            case "remove": return p.Length > 1 ? RemoveByPrefix(m_Store, p[1]) : "usage: remove <device id or its first characters>";
            case "remove-all": return "removed " + m_Store.RemoveAll() + " device(s)";
            case "status": return $"pairing {(m_Window.IsOpen ? "open until " + m_Window.ExpiresUtc : "closed")}, {m_Store.Count} paired, listening: {m_Listener.IsListening}, live: {m_Listener.LiveConnections}";
            default: return "commands: pair [seconds], pair-close, yes, no, devices, remove <id>, remove-all, status";
        }
    }

    static string RemoveByPrefix(DeviceStore store, string prefix)
    {
        var hits = store.List().Where(d => d.DeviceId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
        if (hits.Count != 1) return hits.Count == 0 ? "no such device" : "ambiguous: " + hits.Count + " devices match";
        store.Remove(hits[0].DeviceId);
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
            return true;
        }
    }

    public static void Leave() { lock (Gate) { s_Current = null; s_Killed = false; Monitor.PulseAll(Gate); } }
}
